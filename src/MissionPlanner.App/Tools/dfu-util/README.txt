dfu-util in Mission Planner 3
=============================

Mission Planner runs dfu-util to flash boards that are in STM32 DFU mode: SETUP > Install Firmware >
"Install via DFU (hex)", or a .hex / .bin picked with "Load custom firmware...". Only the build for the
platform the app was made for is installed, in Tools/dfu-util/ next to the app (with this file and the
licences). Mission Planner uses this copy first and a dfu-util on the PATH when it is missing or does not
start; on Apple Silicon Macs a native dfu-util (Homebrew: brew install dfu-util) is preferred.

dfu-util 0.11            https://dfu-util.sourceforge.net/
  Licence: GNU General Public License, version 2 or later (COPYING)
  Source:  https://dfu-util.sourceforge.net/releases/dfu-util-0.11.tar.gz
           sha256 b4b53ba21a82ef7e3d4c47df2952adf5fa494f499b6b0b57c58c5d04ae8ff19e

libusb                   https://libusb.info/   https://github.com/libusb/libusb
  Licence: GNU Lesser General Public License, version 2.1 or later (COPYING.libusb)


Where each build comes from
---------------------------

linux-x64, win-x64, win-x86, osx-x64: the official dfu-util 0.11 binary release, unchanged,
  https://dfu-util.sourceforge.net/releases/dfu-util-0.11-binaries.tar.xz
  sha256 6450de30a7dcd8d8c1273f43f0b153f054fd24d85f7f38296b1ad8edbd2ddb25

    linux-x64/dfu-util            <- linux-amd64/dfu-util-static   libusb linked in; needs libc and libudev.so.1
    win-x64/dfu-util.exe          <- win64/dfu-util-static.exe     libusb linked in (also used on Windows on Arm)
    win-x86/dfu-util.exe          <- win32/dfu-util-static.exe     libusb linked in
    osx-x64/dfu-util              <- darwin-x86_64/dfu-util        Intel; Apple Silicon runs it under Rosetta 2
    osx-x64/libusb-1.0.0.dylib    <- darwin-x86_64/libusb-1.0.0.dylib
                                     (found through DYLD_FALLBACK_LIBRARY_PATH; the binary asks for
                                     /usr/local/opt/libusb/lib/libusb-1.0.0.dylib)

  The release's README-bin.txt: the libusb in these builds is libusb git v1.0.24-66-g1a90627
  (2021-09-05, before 1.0.25); the Windows builds were made with MinGW on Ubuntu 20.04, the macOS
  ones on macOS 10.13.6, from the dfu-util 0.11 release tarball.

linux-arm64: Debian 12 (bookworm) arm64 packages from deb.debian.org, checked against the signed
  bookworm Release file (Debian Stable Release Key 12 and Archive Signing Key 13) and its Packages index:

    linux-arm64/dfu-util          <- usr/bin/dfu-util from dfu-util_0.11-1_arm64.deb
                                     sha256 8839800b822d7dcfc2bd3f9d1cd3c7ea01de4143d3942fbb70ae439b3fa51057
    linux-arm64/libusb-1.0.so.0   <- usr/lib/aarch64-linux-gnu/libusb-1.0.so.0.3.0 from
                                     libusb-1.0-0_1.0.26-1_arm64.deb (libusb 1.0.26)
                                     sha256 e0648086b231c9204514d31480d517cb1b51e301ac39e69335a67d01ec785608
                                     (found through LD_LIBRARY_PATH; needs libc and libudev.so.1)
    linux-arm64/copyright-*.txt   <- the packages' Debian copyright files

  Sources: https://deb.debian.org/debian/pool/main/d/dfu-util/ (dfu-util_0.11-1.dsc) and
           https://deb.debian.org/debian/pool/main/libu/libusb-1.0/ (libusb-1.0_1.0.26-1.dsc),
           also https://sources.debian.org/src/dfu-util/0.11-1/ and .../libusb-1.0/2:1.0.26-1/


SHA-256 of the files as shipped
-------------------------------

4bb88a54938d6000b3aeb4c9edeefdcfed4e3746fdb2e26c07c6df231e2034b9  linux-x64/dfu-util
ba3419a76de7369ce9a86c73ce2ad1f0055432dd5b02815479475a084fe7ee33  linux-arm64/dfu-util
68e5f56b93ee03b203fa59eb9785075b11703b38b27a273a27bc73da7ffc5847  linux-arm64/libusb-1.0.so.0
662cc5c1c1992d0779c2f4201ae4770d60c6f56d8974ca71abdcf35927b9f87d  win-x64/dfu-util.exe
8e9fae3ad6018ade313112dee1e474acbc032634e7d0d28704d6191cb13f5740  win-x86/dfu-util.exe
0c15fd254911a032a4621efa3f8301bc57b42637267e9ab5de64e2fbfba0fdfc  osx-x64/dfu-util
bbc85b497816f2b3e7d1def8594064eaab07526755206918468ee8215efd6ede  osx-x64/libusb-1.0.0.dylib
(the macOS package signs dfu-util and the dylib ad hoc, which changes these two files there)


Drivers
-------

Windows: a board in DFU mode ("STM32 BOOTLOADER") needs the WinUSB driver for dfu-util. Install it
  once with Zadig (https://zadig.akeo.ie): Options > List All Devices, choose STM32 BOOTLOADER,
  WinUSB, Install Driver. No driver is shipped with Mission Planner.
Linux: HELP > Allow USB access installs a udev rule that lets the desktop user open 0483:df11.
macOS: no driver needed.
