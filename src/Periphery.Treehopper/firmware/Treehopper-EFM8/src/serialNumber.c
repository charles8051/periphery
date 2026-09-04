/*
 * serialNumber.c
 *
 *  Created on: Dec 1, 2015
 *      Author: jay
 */

#include "efm8_usb.h"
#include "flash.h"
#include "serialNumber.h"
#include "adc.h"
#include "gpio.h"

SI_LOCATED_VARIABLE_NO_INIT( serialNumber_serial[64], USB_StringDescriptor_TypeDef,
		SI_SEG_CODE, SER_ADDR);
SI_LOCATED_VARIABLE_NO_INIT( serialNumber_name[64], USB_StringDescriptor_TypeDef,
		SI_SEG_CODE, NAME_ADDR);

void writeUsbString(uint8_t* string, uint8_t len, uint16_t addr);

// Largest packed payload that fits the 64-byte config page after the 3-byte header.
#define USB_STRING_MAX_PACKED_LEN 61

uint8_t serialString[8];

uint8_t getRandomPrintableCharacter()
{
	uint8_t i;
	uint16_t ch = 0;
	for(i=0;i<8;i++)
	{
		ch ^= ADC_GetVal(i);
	}

	// scale random character so it is between 48 to 57, 65 to 90, and 97 to 122 (62 total)

	// first, compress it so it is between 0 and 63;
	ch = ch & 0x3F;
	// clip the top so it is between 0 and 61 (62 total values)
	if(ch > 61)
		ch = 61;
	if(ch < 10)
		return(ch + 48); // 0-9
	if(ch < 36)
		return(ch + 55); // A-Z
	return(ch + 61);
}

void generateRandomString()
{
	uint8_t i = 0;
	uint8_t randomChar = 0;

	// use the ADC as a random seed
	for(i=0;i<8;i++)
	{
		ADC_Enable(i, VREF_3V3);
	}

	for(i=0;i<8;i++)
	{
		serialString[i] = getRandomPrintableCharacter();
	}

	for(i=0;i<8;i++)
	{
		GPIO_MakeInput(i, true);
	}
}



void SerialNumber_Init() {
	if (serialNumber_serial[0] == 0xFF) // blank, program with default
	{
		generateRandomString();
		SerialNumber_update(serialString, 8);
	}

	if (serialNumber_name[0] == 0xFF) {
		SerialNumber_updateName("Treehopper", 10);
	}
}

void SerialNumber_update(uint8_t* string, uint8_t len) {
	writeUsbString(string, len, SER_ADDR);
}

void SerialNumber_updateName(uint8_t* string, uint8_t len) {
	writeUsbString(string, len, NAME_ADDR);
}

void writeUsbString(uint8_t* string, uint8_t len, uint16_t addr) {
	int i;

	// `len` arrives straight off the wire as Treehopper_PeripheralConfig[1] and used to be
	// trusted. The record is [0] marker, [1] descriptor length, [2] descriptor type, [3..]
	// payload, inside ONE 64-byte flash page, so the payload cannot exceed 61 bytes. An
	// unbounded len ran a name write past 0xF87F into the unerased reserved region that holds
	// bootloader data and the lock byte - a zero written there can permanently lock the part -
	// and ran a serial write into the name page without erasing it, AND-corrupting it.
	//
	// Reject rather than truncate: a silently shortened name or serial is still corruption,
	// and the host bounds this too (TreehopperBoard.UpdateNameAsync). See issue #170, where a
	// desynchronised EP_PeripheralConfig stream regularly put an APA102 header byte (0xFF) in
	// [1], asking for a 255-byte write.
	if (len > USB_STRING_MAX_PACKED_LEN)
		return;

	flash_armVddMonitor();
	IE_EA = 0; // disable all interrupts
	flash_erasePage(addr);
	flash_writeByte(addr + 1, (len + 1) * 2);
	flash_writeByte(addr + 2, USB_STRING_DESCRIPTOR);
	for (i = 0; i < len; i++) {
		flash_writeByte(addr + 3 + i, string[i]);
	}
	// The encoding marker is written LAST, and this ordering is the whole point.
	//
	// Byte [0] is the only thing SerialNumber_Init tests, so it is the record's validity
	// flag. Writing it first meant any interruption after byte 0 and before the payload left
	// a record that looked valid forever: self-repair was dead and the damage survived every
	// reboot. Written last, an interrupted write leaves [0] == 0xFF (erased) and the next
	// boot regenerates the string. See issue #170.
	flash_writeByte(addr, USB_STRING_DESCRIPTOR_UTF16LE_PACKED);
	IE_EA = 1;
}
