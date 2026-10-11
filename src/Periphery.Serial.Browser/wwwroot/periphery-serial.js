// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0
//
// The Web Serial half of Periphery.Serial.Browser (ADR-0092 D10). .NET calls these through
// [JSImport] in WebSerialInterop.cs.
//
// No async function here rejects. A failed call returns REJECTED, or null for a string, and its
// DOMException's name and message wait under the call's operation id until .NET takes them with
// takeError. A read's bytes wait the same way for takeData: [JSImport] cannot marshal an array
// inside a promise, so a read is two calls.

const OK = 0;
const REJECTED = -1;

const ports = new Map();       // handle -> SerialPort
const handles = new WeakMap(); // SerialPort -> handle
let nextHandle = 1;

// handle -> { reader, chunk, offset }: the open reader, and bytes from its last chunk not yet read.
const readers = new Map();

// handle -> the writer of a write in progress, so close() can abort it.
const writers = new Map();

const outcomes = new Map();    // operation id -> { data?: Uint8Array, error?: { name, message }, staged?: Uint8Array }

// Line errors end the current reader but not the stream: port.readable is replaced and reading
// goes on. Their bytes are lost.
const LINE_ERRORS = new Set(["BufferOverrunError", "BreakError", "FramingError", "ParityError"]);

function handleOf(port) {
  let handle = handles.get(port);
  if (handle === undefined) {
    handle = nextHandle++;
    handles.set(port, handle);
  }
  ports.set(handle, port);
  return handle;
}

function portOf(handle) {
  const port = ports.get(handle);
  if (!port) {
    throw new DOMException("The port is no longer known to this page.", "NotFoundError");
  }
  return port;
}

function fail(op, e) {
  outcomes.set(op, { error: { name: e?.name ?? "Error", message: e?.message ?? String(e) } });
  return REJECTED;
}

export function isSupported() {
  return typeof navigator !== "undefined" && !!navigator.serial;
}

// Opens the chooser. Must run under transient activation, from a click handler. Resolves with the
// port's handle and getInfo() as JSON, or null when it rejects.
export async function requestPort(op, filtersJson) {
  try {
    const port = await navigator.serial.requestPort({ filters: JSON.parse(filtersJson) });
    const info = port.getInfo();
    return JSON.stringify({
      handle: handleOf(port),
      usbVendorId: info.usbVendorId ?? null,
      usbProductId: info.usbProductId ?? null,
    });
  } catch (e) {
    fail(op, e);
    return null;
  }
}

export async function open(op, handle, optionsJson) {
  try { await portOf(handle).open(JSON.parse(optionsJson)); return OK; } catch (e) { return fail(op, e); }
}

// Reads up to max bytes. Resolves with the count, 0 once the stream has ended, or REJECTED.
export async function read(op, handle, max) {
  try {
    const port = portOf(handle);
    let state = readers.get(handle);
    while (!state?.chunk) {
      if (!state) {
        if (!port.readable) return 0;
        state = { reader: port.readable.getReader(), chunk: null, offset: 0 };
        readers.set(handle, state);
      }

      let result;
      try {
        result = await state.reader.read();
      } catch (e) {
        if (!LINE_ERRORS.has(e?.name)) throw e;
        state.reader.releaseLock();
        readers.delete(handle);
        state = null;
        continue;
      }

      if (result.done) {
        state.reader.releaseLock();
        readers.delete(handle);
        return 0;
      }
      if (result.value?.byteLength) {
        state.chunk = result.value;
        state.offset = 0;
      }
    }

    const count = Math.min(max, state.chunk.byteLength - state.offset);
    outcomes.set(op, { data: state.chunk.subarray(state.offset, state.offset + count) });
    state.offset += count;
    if (state.offset >= state.chunk.byteLength) state.chunk = null;
    return count;
  } catch (e) {
    return fail(op, e);
  }
}

// The data was staged under op by stage() just before this call.
export async function write(op, handle) {
  const data = outcomes.get(op)?.staged ?? new Uint8Array(0);
  outcomes.delete(op);
  try {
    const writer = portOf(handle).writable.getWriter();
    writers.set(handle, writer);
    try {
      await writer.write(data);
    } finally {
      writers.delete(handle);
      writer.releaseLock();
    }
    return OK;
  } catch (e) {
    return fail(op, e);
  }
}

export async function setSignals(op, handle, signalsJson) {
  try { await portOf(handle).setSignals(JSON.parse(signalsJson)); return OK; } catch (e) { return fail(op, e); }
}

// Aborts a write in progress, which then rejects, cancels a pending read, which then resolves as
// ended, and closes the port. Under hardware flow control a write may never finish on its own, so
// close() does not wait for one.
export async function close(op, handle) {
  try {
    const writer = writers.get(handle);
    if (writer) {
      writers.delete(handle);
      try { await writer.abort(); } catch { /* the stream may already have errored */ }
      try { writer.releaseLock(); } catch { /* released by the write it aborted */ }
    }
    const state = readers.get(handle);
    if (state) {
      readers.delete(handle);
      try { await state.reader.cancel(); } catch { /* the stream may already have errored */ }
      try { state.reader.releaseLock(); } catch { /* released by the read it cancelled */ }
    }
    await portOf(handle).close();
    return OK;
  } catch (e) {
    return fail(op, e);
  }
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
