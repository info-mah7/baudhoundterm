---
name: BIOS and UEFI setup
description: Enter and navigate a machine's firmware setup over a KVM, before any OS runs
protocols: kvm
group: Firmware
default: off
steps: 150
---
Only a KVM can do this: the keyboard is a real USB device the firmware reads at power-on, and the
picture is the HDMI output, so you see and drive the setup screens before any OS. Over VNC or RDP
this is impossible, because those need a running OS.

Firmware is unforgiving. A wrong boot order, SATA mode, or Secure Boot change can leave the machine
unable to boot. So, before changing anything: read the current value and say what it is, say what
you will change it to, and for anything beyond reading, get the user's go-ahead. Note the old value
so it can be put back.

Entering setup:
- Setup is entered during POST, the few seconds after power-on or reboot, by holding or tapping a
  key. The window is short, so send the key repeatedly while the first logo or POST text is on
  screen, not once.
- The key depends on the vendor, shown on the POST screen if at all. Try, in order: `key delete`
  (most desktops, AMI/Gigabyte/ASUS/MSI), `key f2` (Dell, many laptops, Intel NUC), `key f10` (HP),
  `key esc` then the menu, `key f1` (some Lenovo/IBM). Read the POST screen for "Press <X> to enter
  Setup" and use that key.
- You cannot press a physical power button through the KVM. To reach POST, the machine must reboot:
  if an OS is up, the user (or another tab) restarts it; if it is already at POST or in a boot menu,
  work from there. Say so rather than waiting for a screen that will not come.
- A boot menu (one-time boot device) is usually a different key: `key f12` (Dell, Lenovo, many),
  `key f9` (HP), `key f8`, `key esc`. Use it to boot a USB or network target once without changing
  the saved order.

Navigating (keyboard only; the mouse usually does nothing in text setup, and may not in a UEFI GUI
either):
- Read the on-screen legend first. It is usually along the bottom or right and names the real keys
  for this firmware. Use those over the defaults below.
- Move with the arrow keys. `key enter` opens a menu or a field's value list. `key esc` backs out of
  a screen, and from the top level offers to exit.
- Change a value with `key +` and `key -`, or `key enter` then arrows to pick from the list then
  `key enter`. `key f5`/`key f6` or `key f7` often step a value; the legend says which.
- Move between top tabs (Main, Advanced, Boot, Security, Power, Exit) with `key right`/`key left`,
  or `key pageup`/`key pagedown` on some firmware.
- `key f9` or `key f3` usually loads optimized defaults (it asks first; read the prompt).
- `key f10` is Save and Exit on most firmware; it asks to confirm, so read the prompt and answer
  (often `key enter` for Yes, or type Y). `key esc` at the top then Discard leaves nothing changed.

Common tasks:
- Boot order: the Boot tab. Select the device list, move an entry with `key +`/`key -` or the keys
  the legend names. To boot once from USB without changing the order, use the boot menu instead.
- Virtualization: Advanced > CPU, look for "Intel VT-x"/"Virtualization Technology" or "SVM Mode"
  (AMD), and VT-d/IOMMU for passthrough. Set Enabled.
- Secure Boot: the Security or Boot tab. Turning it on without the right keys enrolled can stop an
  OS booting; turning it off is needed for some installers. Confirm with the user which they want.
- Boot mode UEFI vs Legacy/CSM, and SATA mode (AHCI/RAID/IDE): changing these on an installed system
  usually makes it unbootable. Do not change them unless the user asked and understands that.
- A setup or admin password (Security tab) locks out future changes. Only set one if asked, and make
  sure the user records it: there is no recovery from here.

Reading the screen:
- BIOS text is small. Zoom, or use ocr_read where offered, and quote values exactly. A setting read
  wrong here is a setting changed wrong.
- After each key, check the new screenshot: did the highlight move, the menu open, the value change?
  Firmware can drop a keypress sent too fast, so if nothing changed, send it again before moving on.
- After Save and Exit the machine reboots. Watch for POST again to confirm it came back, and that it
  boots as expected.
