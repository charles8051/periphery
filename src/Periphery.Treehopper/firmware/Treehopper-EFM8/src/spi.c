/*
 * spi.c
 *
 *  Created on: Jul 23, 2015
 *      Author: jay
 */

#include "spi.h"
#include <SI_EFM8UB1_Register_Enums.h>
#include "spi_0.h"
#include "gpio.h"

#define SCK_BIT		1
#define MISO_BIT	2
#define MOSI_BIT	4

uint8_t csPin;
ChipSelectMode_t csMode;
SPI0_ClockMode_t clockMode = SPI0_CLKMODE_0;


void SPI_Init() {

}

void SPI_Transaction(SpiConfigData_t* config, uint8_t count, uint8_t* dataToSend, uint8_t* dataToReceive) {

	// SPI0CKR lives on SFR page 0x20, not 0x00. Writing it on page 0x00 (as this
	// did originally) hit an unrelated SFR, so the host's per-transaction clock
	// rate was silently ignored and SPI ran at the boot default (~6.25 MHz).
	// Write it on the correct page, then restore page 0x00 so the rest of this
	// function -- and the next command in the main loop -- see the page they
	// expect (UART_Transaction etc. assume SFRPAGE == 0x00).
	SFRPAGE = 0x20;
	SPI0CKR = config->CkrVal;
	SFRPAGE = 0x00;
	// if we want to change the SPI Mode, we have to reinit the peripheral, so only do it if necessary.
	if(clockMode != config->SpiMode)
	{
		SPI0_init(config->SpiMode, true, false);
		clockMode = config->SpiMode;
	}

	switch(config->CsMode)
	{
	case CsMode_SpiActiveHigh:
		GPIO_WriteValue(config->CsPin, true);
		break;
	case CsMode_SpiActiveLow:
		GPIO_WriteValue(config->CsPin, false);
		break;
	case CsMode_PulseHighAtBeginning:
		GPIO_WriteValue(config->CsPin, true); // approx 1 us pulse
		GPIO_WriteValue(config->CsPin, false);
		break;
	case CsMode_PulseLowAtBeginning:
		GPIO_WriteValue(config->CsPin, false);
		GPIO_WriteValue(config->CsPin, true);
		break;
	}

	SPI0_pollTransfer(dataToSend, dataToReceive, SPI0_TRANSFER_RXTX, count);

	switch(config->CsMode)
	{
	case CsMode_SpiActiveHigh:
		GPIO_WriteValue(config->CsPin, false);
		break;
	case CsMode_SpiActiveLow:
		GPIO_WriteValue(config->CsPin, true);
		break;
	case CsMode_PulseHighAtEnd:
		GPIO_WriteValue(config->CsPin, true);
		GPIO_WriteValue(config->CsPin, false);
		break;
	case CsMode_PulseLowAtEnd:
		GPIO_WriteValue(config->CsPin, true);
		GPIO_WriteValue(config->CsPin, false);
		break;
	}
}

void SPI_SetConfig(uint8_t enabled) {
	if (enabled) {
		SPI0_init(clockMode, true, false);
		SPI_Enable();
	} else
		SPI_Disable();

}

void SPI_Enable() {
	P0MDOUT |= SCK_BIT | MISO_BIT | MOSI_BIT;
	P0SKIP &= ~(SCK_BIT | MISO_BIT | MOSI_BIT);

	// which pins should be open-drain vs push-pull?
	XBR0 |= XBR0_SPI0E__ENABLED;
}

void SPI_Disable() {
	P0SKIP |= SCK_BIT | MISO_BIT | MOSI_BIT;
	P0MDOUT &= ~(SCK_BIT | MISO_BIT | MOSI_BIT);
	XBR0 &= ~XBR0_SPI0E__ENABLED;
}
