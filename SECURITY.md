# Security Policy

## Reporting a vulnerability

**Do not open a public issue.**

Report privately through GitHub's
[private vulnerability reporting](https://github.com/charles8051/periphery/security/advisories/new)
— the **Security** tab, then **Report a vulnerability**. That channel is private
between you and the maintainer, and it needs no email address from either side.

This is a one-person project. Expect an acknowledgement within a week, and be
patient after that; there is no security team and no on-call rotation. If you have
had no response in two weeks, open a public issue saying only that you are waiting
on a private report — no details.

## Supported versions

Only the latest release. Periphery has no long-term support line, and fixes land on
`main` rather than being backported to an older line.

## Scope

Periphery enumerates hardware and talks to devices. The interesting reports are
things like:

- Memory-safety faults in the native interop layers — SetupAPI and cfgmgr32 on
  Windows, libudev and V4L2 on Linux, IOKit on macOS. These parse OS-supplied
  data, and a malformed device descriptor reaching a fixed buffer is a real bug.
- Anything that lets an attacker-controlled USB or HID descriptor cause more than
  a failed enumeration.
- Firmware-flashing paths that could be induced to write to a device other than
  the one selected, or to bypass the bootloader safety gates.
- Privilege or handle-lifetime problems in the device-handle and reset code.

**Out of scope**, because they are how the library is meant to work:

- Needing elevation for operations that genuinely require it.
- Reading device metadata that any local process can already read.
- Vulnerabilities in the underlying OS APIs or in third-party dependencies —
  report those upstream, though telling us is still welcome so we can pin or
  work around.
- Anything that requires the attacker to already have physical access *and*
  administrative rights, which is the threat model for most firmware flashing.

## What to include

The platform and OS version, the .NET version, which package and version, and the
smallest reproduction you can manage. If it involves a device, the VID/PID and the
raw descriptor are usually the load-bearing detail.

## Disclosure

Tell us first and give a reasonable window to fix it. Credit is offered in the
advisory unless you would rather not be named.
