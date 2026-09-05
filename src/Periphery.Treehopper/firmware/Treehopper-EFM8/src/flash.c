/******************************************************************************
 * Copyright (c) 2015 by Silicon Laboratories Inc. All rights reserved.
 *
 * http://developer.silabs.com/legal/version/v11/Silicon_Labs_Software_License_Agreement.txt
 *****************************************************************************/

#include "efm8_usb.h"
#include "flash.h"

// ----------------------------------------------------------------------------
// Enables the supply (VDD) monitor and selects it as a reset source.
//
// The reference manual requires the supply monitor enabled AND selected in RSTSRC
// before any flash write or erase (RM sec. 12.4, "Flash Write and Erase Guidelines").
// Neither was ever done here: VDM0CN was never written, and RSTSRC appeared only at the
// three deliberate-reset sites. Without the monitor a supply dip during a write leaves
// cells partially programmed instead of resetting the part, and a weakly-erased cell
// reads back differently from one enumeration to the next. See issue #170, where the
// same serial number read back with its letter case drifting toward the erased state
// (every flip set bit 5) on a station whose hub loses power on any mains dip.
//
// Called from writeUsbString rather than from the two primitives below, so the cost is
// paid once per config-page update and not once per byte.
// ----------------------------------------------------------------------------
void flash_armVddMonitor(void) {
	uint8_t SFRPAGE_save = SFRPAGE;
	uint8_t i;
	SFRPAGE = 0x00;
	VDM0CN = VDM0CN_VDMEN__ENABLED;
	// The monitor needs time to stabilise before it may be selected as a reset source,
	// otherwise selecting it can itself trigger a spurious reset. This loop is far longer
	// than the datasheet's requirement at any clock the part runs.
	for (i = 0; i < 200; i++);
	// RSTSRC is write-to-enable per bit and reads back FLAGS, not the enables, so a plain
	// write is correct here and a read-modify-write would not be. PORSF is the supply
	// monitor's enable bit.
	RSTSRC = RSTSRC_PORSF__SET;
	SFRPAGE = SFRPAGE_save;
}

// ----------------------------------------------------------------------------
// Writes one byte to flash memory.
// ----------------------------------------------------------------------------
void flash_writeByte(uint16_t addr, uint8_t byte) {
	uint8_t SI_SEG_XDATA * pwrite = (uint8_t SI_SEG_XDATA *) addr;

	// Unlock flash by writing the key sequence
	FLKEY = 0xA5;
	FLKEY = 0xF1;

	// Enable flash writes, then do the write
	PSCTL |= PSCTL_PSWE__WRITE_ENABLED;
	*pwrite = byte;
	PSCTL &= ~(PSCTL_PSEE__ERASE_ENABLED | PSCTL_PSWE__WRITE_ENABLED);
}

// ----------------------------------------------------------------------------
// Erases one page of flash memory.
// ----------------------------------------------------------------------------
void flash_erasePage(uint16_t addr) {
	// Enable flash erasing, then start a write cycle on the selected page
	PSCTL |= PSCTL_PSEE__ERASE_ENABLED;
	flash_writeByte(addr, 0);
}

//// ----------------------------------------------------------------------------
//// Writes one byte to flash memory.
//// ----------------------------------------------------------------------------
//void flash_writeByte(uint16_t addr, uint8_t byte)
//{
//  // Don't bother writing the erased value to flash
//  if (byte != 0xFF)
//  {
//    writeByte(addr, byte);
//  }
//}
