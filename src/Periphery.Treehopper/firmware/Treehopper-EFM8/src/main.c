/////////////////////////////////////////////////////////////////////////////
// main.c
/////////////////////////////////////////////////////////////////////////////

/////////////////////////////////////////////////////////////////////////////
// Includes
/////////////////////////////////////////////////////////////////////////////
#include <SI_EFM8UB1_Register_Enums.h>
#include "InitDevice.h"
#include "efm8_usb.h"
#include "descriptors.h"
#include "usbconfig.h"
#include "treehopper.h"
#include "adc.h"
#include "spi.h"
#include "uart.h"
#include "i2c.h"
#include "i2c_0.h"
#include "serialNumber.h"
#include "led.h"
#include "pwm.h"
#include "softPwm.h"
#include "gpio.h"
//-----------------------------------------------------------------------------
// Variables
//-----------------------------------------------------------------------------

//TIMERS
//Timer0: SMBus clock rate
//Timer1: UART clock rate
//Timer2: Unused
//Timer3: SMBus SCL low timeout detection
//Timer4: SoftPWM

// Set from the USB ISR on every SOF (callback.c).
extern volatile bit Usb_SofSeen;

// Superloop passes a CONFIGURED device may go without seeing a SOF before the feed is
// withheld. Counted in loop passes rather than milliseconds because the superloop has no
// timebase - every timer on this part is already spoken for, and standing one up costs
// flash we do not have (BUILD.md: 51 bytes free before this change, 0 after).
//
// Counting passes is safe in the direction that matters. A healthy CONFIGURED board sees a
// SOF every 1 ms, so tripping this would need 65535 passes inside one 1 ms frame - not
// physically possible on a 48 MHz 8051. The budget therefore cannot produce a false positive
// no matter how fast the loop runs; loop speed only sets how *quickly* a real SOF outage is
// noticed, and a slower loop makes it later, never twitchier.
#define SOF_IDLE_BUDGET 0xFFFFU

// IDATA, not DATA: the 128-byte directly-addressable DATA space is full - two more bytes
// there overflows it (LX51 L107 on ?C?LIB_DATA). IDATA is indirectly addressed via R0/R1,
// which costs a few code bytes but is the only RAM left cheaper than XDATA.
static SI_SEGMENT_VARIABLE(sofBudget, uint16_t, SI_SEG_IDATA) = SOF_IDLE_BUDGET;

int16_t main(void) {
	enter_DefaultMode_from_RESET();
	Treehopper_Init();

#ifdef ENABLE_TIMING_DEBUGGING
	GPIO_MakeOutput(10, PushPullOutput);
#endif

#ifdef ENABLE_UART_DEBGUGGING
	UART_StartDebugging115200();
#endif

	while (1) {
		// Feed the watchdog. This is the ONLY feed in the firmware, and that is the point:
		// the fault it exists to catch is the superloop stopping while the ISRs keep running
		// (a wedged board still enumerates and serves descriptors, but EP_PeripheralConfig is
		// re-armed only from Treehopper_Task, so it stops being drained and every bulk write
		// times out). A feed from a timer ISR would keep feeding straight through exactly that
		// hang and the watchdog would never fire. Feeding here, and only here, means "the
		// superloop completed a pass".
		//
		// WDTCN is SFR page ALL (RM sec. 23.4.1), so no SFRPAGE juggling is needed. Writing
		// 0xA5 both enables and restarts the timer (RM sec. 23.3).
		//
		// Interval is left at the power-on default. The reference manual (sec. 23.3) puts that at
		// WDTCN[2:0] = 111b => T_LFOSC * 4^10 = ~13.1 s at a nominal 80 kHz LFOSC.
		//
		// MEASURED RECOVERY IS ~8 s, NOT ~13.1 s - reproducible 3/3 on hardware, and unexplained.
		// Do not treat the 13.1 s figure as the real deadline. Candidates: the tool's elapsed
		// measurement may start after the hang actually began; LFOSC trim; or the factory
		// bootloader (which runs between reset and this code) leaving WDTCN at a shorter interval,
		// since the 0xA5 feed below enables and restarts the timer but does NOT set the interval.
		// Reading WDTCN back early in Treehopper_Init would settle it.
		//
		// Either way the margin holds: ~8 s is still ~30x the longest legitimate single pass we
		// know of (a 255-byte UART transaction at 9600 baud, ~266 ms), so a healthy board cannot
		// trip it, while a stopped superloop recovers on its own instead of needing a replug.
		// A shorter interval is deliberately NOT set yet, and this is the reason rather than an
		// oversight: nominal says 13.1 s and hardware says ~8 s, so the true scale between the
		// WDTCN setting and real elapsed time is unknown. WDTCN = 0x04 is nominally ~205 ms, but
		// at the same ~0.6x the measurement implies it could be ~125 ms - only about 2x the ~60 ms
		// worst-case ISR block (the enumeration LED blink in callback.c runs six 60000-iteration
		// spins inside the USB ISR, and nothing can preempt it at default priority). That is not
		// enough margin to pick blind. Resolve the discrepancy first, then tighten.
		//
		// SOF GATE. The feed above catches a stopped foreground; the EP0 rescue request catches a
		// wedged endpoint with a live ISR. Neither catches a DEAD USB ISR while the foreground
		// still runs: the superloop keeps feeding, and the EP0 rescue needs the very ISR that
		// died. So the feed is additionally gated on the USB ISR proving itself alive.
		//
		// USBD_SofCb runs from the USB ISR on every SOF, so it stops exactly when that ISR stops.
		// It is used here as EVIDENCE, never as a carrier: it does not feed, it only permits the
		// foreground's feed. An unconditional feed from the ISR would be actively harmful - it
		// would keep feeding through the stopped-foreground fault this watchdog was built for.
		//
		// Absence of SOF is only suspicious while CONFIGURED. Every legitimate reason for SOF to
		// stop takes the device out of that state first, which is why one test covers all of them:
		//   - never plugged in / un-enumerated  -> DEFAULT or ADDRESSED
		//   - host reboot or port reset         -> handleUsbResetInt -> DEFAULT
		//   - HOST BUS-SUSPEND (laptop sleep)   -> handleUsbSuspendInt -> SUSPENDED
		// The suspend case is the one that would otherwise reset a healthy board: #226 set
		// SLAB_USB_PWRSAVE_MODE = OFF, so the foreground keeps running through a host sleep. It is
		// covered because the stack sets USBD_STATE_SUSPENDED from the suspend interrupt
		// unconditionally (efm8_usbdint.c handleUsbSuspendInt) - the PWRSAVE flag only controls
		// whether USBD_Suspend() parks the core, not whether the state is tracked. Preferring this
		// over a merely generous deadline is deliberate: it is exact, it costs nothing extra, and a
		// host may legitimately stay suspended for hours, which no deadline can outlast.
		//
		// Conversely a dead ISR cannot fake an exemption: with the ISR gone the reset and suspend
		// interrupts cannot fire either, so the state stays pinned at CONFIGURED and the gate
		// closes. That is the whole mechanism.
		if (Usb_SofSeen) {
			Usb_SofSeen = 0;
			sofBudget = SOF_IDLE_BUDGET;
		} else if (sofBudget != 0U) {
			sofBudget--;
		}

		// Short-circuit order matters: the budget test is a couple of instructions and is true on
		// essentially every pass, so USBD_GetUsbState() is only reached once the budget is spent.
		if (sofBudget != 0U || USBD_GetUsbState() != USBD_STATE_CONFIGURED) {
			WDTCN = 0xA5;
		}

		Treehopper_Task();
	}

}
