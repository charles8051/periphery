// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0
//
// The WebUSB half of Periphery.Usb.Browser (ADR-0092 D10). .NET calls these through [JSImport]
// in WebUsbInterop.cs.
//
// No async function here rejects. A failed WebUSB call returns REJECTED, and its DOMException's
// name and message wait under the call's operation id until .NET takes them with takeError.
// A transfer's bytes wait the same way for takeData: [JSImport] cannot marshal an array inside a
// promise, so a read is two calls, await the length and then copy (exploration, "The interop
// shape a transport must use").

const OK = 0;
const REJECTED = -1;
const STALL = -2;
const BABBLE = -3;
const UNKNOWN_STATUS = -4;

const devices = new Map();     // handle -> USBDevice
const handles = new WeakMap(); // USBDevice -> handle
let nextHandle = 1;

const outcomes = new Map();    // operation id -> { data?: Uint8Array, error?: { name, message }, staged?: Uint8Array }

function handleOf(device) {
  let handle = handles.get(device);
  if (handle === undefined) {
    handle = nextHandle++;
    handles.set(device, handle);
  }
  devices.set(handle, device);
  return handle;
}

function deviceOf(handle) {
  const device = devices.get(handle);
  if (!device) {
    throw new DOMException("The device was disconnected.", "NotFoundError");
  }
  return device;
}

function fail(op, e) {
  outcomes.set(op, { error: { name: e?.name ?? "Error", message: e?.message ?? String(e) } });
  return REJECTED;
}

function inResult(op, result) {
  switch (result.status) {
    case "ok": {
      const length = result.data?.byteLength ?? 0;
      if (length > 0) {
        outcomes.set(op, { data: new Uint8Array(result.data.buffer, result.data.byteOffset, length) });
      }
      return length;
    }
    case "stall": return STALL;
    case "babble": return BABBLE;
    default: return UNKNOWN_STATUS;
  }
}

function outResult(result) {
  switch (result.status) {
    case "ok": return result.bytesWritten;
    case "stall": return STALL;
    case "babble": return BABBLE;
    default: return UNKNOWN_STATUS;
  }
}

function takeStaged(op) {
  const staged = outcomes.get(op)?.staged ?? new Uint8Array(0);
  outcomes.delete(op);
  return staged;
}

export function isSupported() {
  return typeof navigator !== "undefined" && !!navigator.usb;
}

// The granted, attached devices as JSON, or null when getDevices() rejects. Handles of devices no
// longer listed are dropped: a replugged device is a new USBDevice with a new handle.
export async function getDevices(op) {
  try {
    const list = await navigator.usb.getDevices();
    const live = new Set(list);
    for (const [handle, device] of devices) {
      if (!live.has(device)) devices.delete(handle);
    }
    return JSON.stringify(list.map(d => ({
      handle: handleOf(d),
      vendorId: d.vendorId,
      productId: d.productId,
      serialNumber: d.serialNumber ?? null,
      productName: d.productName ?? null,
      manufacturerName: d.manufacturerName ?? null,
      deviceClass: d.deviceClass,
      deviceSubclass: d.deviceSubclass,
      deviceProtocol: d.deviceProtocol,
    })));
  } catch (e) {
    fail(op, e);
    return null;
  }
}

// Opens the chooser. Must run under transient activation, from a click handler.
export async function requestDevice(op, filtersJson) {
  try {
    const device = await navigator.usb.requestDevice({ filters: JSON.parse(filtersJson) });
    return handleOf(device);
  } catch (e) {
    return fail(op, e);
  }
}

export async function open(op, handle) {
  try { await deviceOf(handle).open(); return OK; } catch (e) { return fail(op, e); }
}

export async function selectConfiguration(op, handle, value) {
  try { await deviceOf(handle).selectConfiguration(value); return OK; } catch (e) { return fail(op, e); }
}

export async function claimInterface(op, handle, interfaceNumber) {
  try { await deviceOf(handle).claimInterface(interfaceNumber); return OK; } catch (e) { return fail(op, e); }
}

export async function close(op, handle) {
  try { await deviceOf(handle).close(); return OK; } catch (e) { return fail(op, e); }
}

export function activeConfiguration(handle) {
  return devices.get(handle)?.configuration?.configurationValue ?? -1;
}

export function configurationValues(handle) {
  return (devices.get(handle)?.configurations ?? []).map(c => c.configurationValue);
}

export async function controlTransferIn(op, handle, requestType, recipient, request, value, index, length) {
  try {
    const setup = { requestType, recipient, request, value, index };
    return inResult(op, await deviceOf(handle).controlTransferIn(setup, length));
  } catch (e) {
    return fail(op, e);
  }
}

// The data was staged under op by stage() just before this call.
export async function controlTransferOut(op, handle, requestType, recipient, request, value, index) {
  const data = takeStaged(op);
  try {
    const setup = { requestType, recipient, request, value, index };
    return outResult(await deviceOf(handle).controlTransferOut(setup, data));
  } catch (e) {
    return fail(op, e);
  }
}

export async function transferIn(op, handle, endpointNumber, length) {
  try {
    return inResult(op, await deviceOf(handle).transferIn(endpointNumber, length));
  } catch (e) {
    return fail(op, e);
  }
}

// The data was staged under op by stage() just before this call.
export async function transferOut(op, handle, endpointNumber) {
  const data = takeStaged(op);
  try {
    return outResult(await deviceOf(handle).transferOut(endpointNumber, data));
  } catch (e) {
    return fail(op, e);
  }
}

export async function clearHalt(op, handle, direction, endpointNumber) {
  try { await deviceOf(handle).clearHalt(direction, endpointNumber); return OK; } catch (e) { return fail(op, e); }
}

// Copies the bytes to send out of .NET memory, which the view covers only during this call.
export function stage(op, view) {
  outcomes.set(op, { staged: view.slice() });
}

export function takeData(op, destination) {
  const data = outcomes.get(op)?.data;
  outcomes.delete(op);
  if (data) destination.set(data);
}

export function takeError(op) {
  const error = outcomes.get(op)?.error ?? { name: "Error", message: "No error was recorded for this call." };
  outcomes.delete(op);
  return JSON.stringify(error);
}
