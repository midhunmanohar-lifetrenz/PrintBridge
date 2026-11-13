# PrintBridge

A small desktop app (Windows and macOS) that lets a **web application print raw label commands**
(ZPL, TSPL, EPL, BPLE, CPCL, DPL, ESC/POS) to a local USB, spooler, CUPS or network printer, with no
browser plug-in. It runs in the system tray and listens on loopback only.

- **Stack:** C# / .NET 10, Avalonia UI 11.3 (desktop), Kestrel (local server), SkiaSharp + ZXing.Net (label preview)
- **Ports:** HTTP `127.0.0.1:9100`, HTTPS `127.0.0.1:9101` (both configurable)
- **Compatibility:** also answers the Zebra Browser Print API (`/available`, `/default`, `/write`, `/read`)
- **Version:** single source, `<Version>` in `src/LtzPrintBridge/LtzPrintBridge.csproj`

## Quick start (developer)

Prerequisite: [.NET 10 SDK](https://dotnet.microsoft.com/download). Nothing else.

```bash
dotnet build PrintBridge.sln          # build everything
dotnet run --project src/LtzPrintBridge   # run the app (tray icon + window)
./run-tests.sh                        # Windows: run-tests.cmd   (all tests, headless, no printer needed)
```

Then open <http://127.0.0.1:9100/> for the built-in test page.

Data and logs: macOS `~/Library/Application Support/PrintBridge`, Windows `%LOCALAPPDATA%\LtzPrintBridge`
(`settings.json`, `jobs.jsonl`, `bridge.log`, `certs/`). Set `LTZ_DATA_DIR` to use another folder
(handy for experiments and tests).

## Repository map

| Path | What is in it |
|---|---|
| `src/LtzPrintBridge/Core` | Printing, settings, security and preview logic. No UI code. |
| `src/LtzPrintBridge/Server` | Kestrel API (`BridgeServer.cs`) and the built-in HTML pages (`Pages.cs`) |
| `src/LtzPrintBridge/Views`, `ViewModels` | Avalonia windows and the main view-model |
| `src/LtzPrintBridge/Installer.cs` | Built-in Windows installer (`--install`, `--uninstall`, `--silent`) |
| `tests/LtzPrintBridge.Tests` | xUnit tests, including headless UI tests |
| `mac/` | `build-mac.sh` (app bundle, DMG, sign, notarize), entitlements |
| `release/windows/` | `build-windows.ps1` (test, build, sign, zip), sample files |
| `docs/` | Architecture, API, user guide, release guide, compatibility analysis |
| `.github/workflows/ci.yml` | Windows CI: test, publish, installer round trip |

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): how the pieces fit, request flow, security model
- [docs/API.md](docs/API.md): HTTP API for web applications
- [CHANGELOG.md](CHANGELOG.md): what changed in each version
- [docs/Review_and_Test_Report.md](docs/Review_and_Test_Report.md): November 2026 review, research and simulated printer tests
- [CONTRIBUTING.md](CONTRIBUTING.md): coding conventions, tests, how to add a printer language, release steps
- [docs/PrintBridge_Release_Guide.docx](docs/PrintBridge_Release_Guide.docx): signing and publishing (non-technical)
- [docs/PrintBridge_User_Guide.docx](docs/PrintBridge_User_Guide.docx): for end users
- [docs/Compatibility_and_Gap_Analysis.md](docs/Compatibility_and_Gap_Analysis.md): known gaps and roadmap

## Known status

Not yet verified on real hardware or accounts: the Windows build script, the Windows installer prompts,
Apple notarization with a real Developer ID, TSC and TVS LP46 printers, Firefox and Safari. Windows builds
are unsigned until a certificate is obtained (Smart App Control blocks unsigned EXEs).
