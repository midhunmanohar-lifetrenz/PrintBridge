# Architecture

```
Web app (browser)  --HTTP/HTTPS, loopback-->  BridgeServer (Kestrel)
                                                |  HostGate (Origin allow-list, prompt)
                                                |  PrinterRegistry (resolve printer)
                                                v
                                            JobManager --(optional) preview window --> operator
                                                |
                          +---------------------+----------------------+
                          v                     v                      v
                      WinSpool (Windows)    CupsSpool (macOS)     TCP :9100 (network)
```

## Components (src/LtzPrintBridge)

| File | Responsibility |
|---|---|
| `Program.cs` | Entry. Runs `Installer.Handle(args)` first, then a single-instance mutex, then Avalonia (tray app, `OnExplicitShutdown`). |
| `App.axaml(.cs)` | Theme dictionaries (Light/Dark), tray icon, wiring of `AskHost` and `AskReview` prompts. |
| `Server/BridgeServer.cs` | Request pipeline: Host check (421) -> public pages -> Origin/Sec-Fetch checks -> `HostGate` -> CORS -> routes. Wrapper `Handle` logs each request (`Requested` event). |
| `Server/Pages.cs` | Built-in HTML (test page, certificate help). |
| `Core/HostGate.cs` | Accept/Block/wildcard lists and the "allow this website?" prompt. |
| `Core/Registry.cs` | `PrinterRegistry` (discovery, default, routing) and `JobManager` (per-printer lock, queue pre-check, review flow, history). |
| `Core/Spool.cs`, `WinSpool.cs` | Raw output: `winspool.drv` (Windows), `lp -o raw` + lpstat (macOS/CUPS), TCP 9100. |
| `Core/Languages.cs`, `Models.cs` | `PrintLanguage`, guess from name/driver, encoding, models, `AppSettings`. |
| `Core/ZplPreview.cs`, `LinePreview.cs`, `LabelPreview.cs` | Draw ZPL, TSPL and EPL labels (SkiaSharp, ZXing.Net, 203 dpi assumed). Other languages show the commands. |
| `Core/CertManager.cs`, `MacTrust.cs` | Per-install local CA and server certificate. Private key of the CA is discarded. Trust: CurrentUser Root (Windows), login keychain (macOS). |
| `Core/SettingsStore.cs` | `AppPaths`, `Log` (rolling 2 MB), JSON settings. |
| `Core/Policy.cs` | Optional `policy.json` (IT preload of sites, default printer, ports). |
| `Installer.cs` | Per-user Windows install, no admin rights: copy, Start Menu shortcut, uninstall registry key, HKCU Run autostart. |
| `ViewModels/MainViewModel.cs`, `Views/*` | UI (Settings-style sidebar). |

## Key behaviours

- **Preview flow:** `PreviewMode` Off/All/Selected (+`PreviewSites`). Copies are written into the label (ZPL `^PQ`, TSPL `PRINT m,n`, EPL `Pn`) when quantities are uniform, otherwise the job is repeated. Non-UTF-8 data is never rewritten. Cancel returns HTTP 409.
- **Security model:** loopback only, Host header check, Origin allow-list (http/https origins only), Sec-Fetch-Site rejection, at most three permission windows at once, optional block for non-browser programs (`AllowLocalPrograms`), 2 MB cap, metadata-only job log (print content is never stored), per-install CA that is re-trusted automatically when it is renewed.
- **Quantities:** a job the operator does not change in the preview is sent byte for byte. Quantity is read as the printer reads it: last `^PQ` (ZPL), sets x copies (TSPL `PRINT m,n`, EPL `Pp1,p2`). Whole-job repeats are limited to 100; quantities written into the label to 9999.
- **Default printer:** a default the person chose is never replaced silently. While it is missing, jobs get `503 DEFAULT_PRINTER_UNAVAILABLE`.
- **Printer recognition:** `Languages.Guess` is checked against 513 real printer and driver names (`tests/.../data/printer_catalog.json`). Names only; no hardware.
- **Status and confirmation:** `PrinterStatus` reads a printer's state (ZPL `~HS`, TSPL `ESC ! ?`, or the system queue). After each job `JobManager.Confirm` polls until the printer is idle or the system queue has released the job, and keeps the result per job id (`JobManager.Status`). Status questions take the same per-printer lock as jobs, so they never interleave with label data.
- **Start-up:** each port is checked on its own; a port taken by another program is retried every 20 seconds.
- **Settings:** written to a temporary file and swapped in; the previous file is kept as `settings.json.bak` and used if the main file is damaged.
- **macOS printing:** CUPS no longer supports raw queues; the queue needs a PPD (for example `drv:///sample.drv/zebra.ppd`).
- **Chrome Local Network Access:** the browser blocks the call silently until the site is allowed. If a site never appears in *Websites > Recent requests*, check the Chrome console first.

## Data on disk

`settings.json`, `jobs.jsonl`, `bridge.log`, `certs/` under the data folder (see README). `LTZ_DATA_DIR` overrides it. `LTZ_INSTALL_DIR` overrides the Windows install folder (used by CI).

## Tests (tests/LtzPrintBridge.Tests)

Unit and bridge tests (in-process server), security tests, preview tests, headless UI tests (Avalonia.Headless + Skia), Windows spooler tests (skipped off Windows), macOS CUPS tests (skipped off macOS). `GuideScreenshots.cs` regenerates documentation screenshots; it is not a normal test.
