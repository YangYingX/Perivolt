# Third-party notices

PowerHub's own code is licensed under the repository [MIT License](LICENSE).
Third-party components retain their own copyrights and licenses. The project's
MIT license does not replace or relicense any dependency, plugin, protocol
documentation, runtime, or installer engine.

## BatteryMonitor

The following narrowly matched protocol implementations or device tables are
adapted from [BatteryMonitor](https://github.com/JKWTCN/BatteryMonitor):

| PowerHub source | Upstream source |
| --- | --- |
| `Services/WeishengBatteryReader.cs` | [VgnHidProvider.cpp](https://github.com/JKWTCN/BatteryMonitor/blob/master/src/providers/hid/VgnHidProvider.cpp) |
| `Services/LogitechBatteryReader.cs` receiver table | [LogitechHidProvider.cpp](https://github.com/JKWTCN/BatteryMonitor/blob/master/src/providers/hid/LogitechHidProvider.cpp) |
| `Services/RazerBatteryReader.cs` | [RazerHidProvider.cpp](https://github.com/JKWTCN/BatteryMonitor/blob/master/src/providers/hid/RazerHidProvider.cpp) |
| `Services/CompxBatteryReader.cs` battery frame reference | [VgnHidProvider.cpp / MouseEnc](https://github.com/JKWTCN/BatteryMonitor/blob/master/src/providers/hid/VgnHidProvider.cpp) |
| `Services/AulaBatteryReader.cs` | [AulaHidProvider.cpp](https://github.com/JKWTCN/BatteryMonitor/blob/master/src/providers/hid/AulaHidProvider.cpp) |

MIT License

Copyright (c) 2026 JKWTCN

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## python-pulsar-mouse-tool

`Services/CompxBatteryReader.cs` also adapts the battery/online frame reference in
[python-pulsar-mouse-tool](https://github.com/andrewrabert/python-pulsar-mouse-tool),
including its [pulsar.py implementation](https://github.com/andrewrabert/python-pulsar-mouse-tool/blob/main/pulsar.py).
The [upstream license](https://github.com/andrewrabert/python-pulsar-mouse-tool/blob/main/LICENSE)
is retained below.

MIT License

Copyright (c) 2026 Andrew Rabert

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Protocol documentation consulted

These references describe protocol facts used by independently written readers.
No GPL-licensed implementation code from the Linux kernel or HaloBattery is
incorporated into PowerHub, and those projects are not relicensed by this notice.

- Logitech's [HID++ 2.0 documentation](https://github.com/Logitech/cpg-docs/tree/master/hidpp20): feature discovery and battery report semantics.
- Linux [hid-playstation.c](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-playstation.c): DualSense USB/Bluetooth input-report field layout and CRC framing.
- Linux [hid-steam.c](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-steam.c): original Steam Controller status-report field layout.
- Valve's [SDL Steam Controller driver](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/steam/controller_structs.h) and [HIDAPI Steam driver](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_steam.c): interface/status packet documentation.
- [HaloBattery protocol documentation](https://github.com/HeyOkay/HaloBattery/blob/main/docs/protocols.md): additional exact Compx transport identifiers. Only hardware/protocol facts were consulted.
- Microsoft Windows API documentation and USB-IF HID Usage Tables: Windows device enumeration, battery properties, GATT, HID and XInput contracts.
- [MCHOSE's official driver](https://www.mchose.com.cn/): headset family identifiers, battery-query framing, charge and sleep fields. Only interoperability facts were consulted; proprietary driver source and assets are not included or relicensed.
- [LANGTU's official site](https://www.langtucn.com/) and its linked [Shinetek keyboard driver](https://langtu.shinetek-ic.net/): shared keyboard device table, status-query fields and connection modes. Only interoperability facts were consulted; proprietary driver source and assets are not included or relicensed. Source snapshot hashes and validation scope are recorded in `docs/protocol-families.md`.
- [HeadsetControl](https://github.com/Sapd/HeadsetControl): publicly documented hardware identifiers and Corsair headset status fields. No GPL implementation code or binaries are copied, linked or bundled.
- Linux SteelSeries Arctis driver protocol documentation: Nova 7 connection, battery and charging report fields, including discrete versus percentage encoding. No kernel code or drivers are included.
- [HyperHeadset](https://github.com/LennardKittner/HyperHeadset): Cloud-series hardware identifiers and read-only `66` protocol facts. No implementation code or binaries are copied, linked or bundled. Pinned research revisions and the unverified hardware boundaries are documented in `docs/protocol-families.md`.

## MCHOSE V9 Pro protocol reference

[mchose-v9-pro-battery-tray](https://github.com/rafagfran/mchose-v9-pro-battery-tray)
provided the publicly documented V9 Pro HID descriptor and hardware packet capture
used to cross-check the independently implemented family reader. Its MIT notice
is retained here for attribution.

MIT License

Copyright (c) 2026 Rafael

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Microsoft .NET runtime

Self-contained releases include the Microsoft .NET 10 runtime and Windows desktop
runtime. These are third-party binaries with their own licensing and bundled
third-party components. The release package must retain their original `LICENSE`
and `ThirdPartyNotices` files alongside this project notice. See also the official
[.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)
and [.NET third-party notices](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT).

## Inno Setup

The Windows installer is generated with [Inno Setup](https://jrsoftware.org/).
The installation engine retains its original copyright and license notices:

Copyright (C) 1997-2026 Jordan Russell. All rights reserved.
Portions Copyright (C) 2000-2026 Martijn Laan. All rights reserved.

The Simplified Chinese installer message overrides in
`installer/ChineseSimplified.isl` were written for PowerHub; they are not an
official Inno Setup translation. Untranslated messages use Inno Setup's original
defaults. The compiler itself is a development tool and is not included in the
application's portable package.

## Names and artwork

PowerHub's application icon was created for this project. Device/brand names are
used only to identify compatibility; their trademarks belong to their respective
owners. No manufacturer endorsement is implied.
