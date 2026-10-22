# macOS dependencies

This file supplements the repository's `THIRD-PARTY-NOTICES.md`, `NOTICE.md`, `AUTHORS.md` and `LICENSE`. The Perivolt personal non-commercial license does not replace the licenses of third-party components.

| Component | Version | License and source |
| --- | --- | --- |
| Avalonia desktop, native backend and Fluent theme | 11.3.21 | MIT; [upstream](https://github.com/AvaloniaUI/Avalonia/tree/11.3.21). Full license and upstream notices are in `LICENSES/Avalonia-LICENSE.txt` and `LICENSES/Avalonia-NOTICE.md`. |
| MicroCom.Runtime | 0.11.0 | MIT; [upstream](https://github.com/kekekeks/MicroCom). Full license is in `LICENSES/MicroCom-LICENSE.txt`. |
| SkiaSharp and macOS native assets | 2.88.9 | MIT wrapper with native component notices; [upstream](https://github.com/mono/SkiaSharp/tree/v2.88.9). See `LICENSES/SkiaSharp-LICENSE.txt` and `LICENSES/SkiaSharp-HarfBuzz-NOTICES.txt`. |
| HarfBuzzSharp and macOS native assets | 8.3.1.1 | MIT wrapper; [upstream](https://github.com/mono/SkiaSharp). See `LICENSES/HarfBuzzSharp-LICENSE.txt` and `LICENSES/SkiaSharp-HarfBuzz-NOTICES.txt`. The native notice files from the two packages are byte-identical and are retained once. |
| .NET runtime | The SDK-selected .NET 10 patch | MIT and third-party notices; self-contained bundles copy the runtime's license and third-party notices into `Contents/Resources/runtime-notices/`. |

Development on Windows can load Avalonia's Windows backend for sample-data screenshots. The macOS bundle is published with an explicit `osx-arm64` or `osx-x64` runtime identifier and uses the native macOS backend. Build tooling is not shipped as an application plugin.
