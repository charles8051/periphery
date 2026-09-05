/******************************************************************************
 * Copyright (c) 2015 by Silicon Laboratories Inc. All rights reserved.
 *
 * http://developer.silabs.com/legal/version/v11/Silicon_Labs_Software_License_Agreement.txt
 *****************************************************************************/

#ifndef __FLASH_H__
#define __FLASH_H__

/**************************************************************************//**
 * Enable the supply monitor and select it as a reset source. Must be called before
 * any flash write or erase; see the definition for why.
 *****************************************************************************/
extern void flash_armVddMonitor(void);

/**************************************************************************//**
 * Erase the flash page the contains the specified address.
 *
 * @param addr Flash address that lies within the page to erase.
 *****************************************************************************/
extern void flash_erasePage(uint16_t addr);

/**************************************************************************//**
 * Write one byte to flash.
 *
 * @param addr Flash address that will be written.
 * @param byte Data value that will be written.
 *****************************************************************************/
extern void flash_writeByte(uint16_t addr, uint8_t byte);

#endif // __FLASH_H__
