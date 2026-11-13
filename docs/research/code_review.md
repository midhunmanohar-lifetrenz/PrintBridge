# PrintBridge code review (read-only)

Scope: README.md, docs/ARCHITECTURE.md, docs/API.md and all of `src/LtzPrintBridge` (Core, Server, Installer.cs, App.axaml.cs, Program.cs, ViewModels, Views). Nothing was built or run; every finding below was checked against the source text. Items that depend on runtime or OS behaviour I could not execute are marked **possible**.

Paths are relative to `src/LtzPrintBridge/`.

---

## Summary table

| # | Sev | Area | Finding | Location |
|---|---|---|---|---|
| 1 | High | Certs | Yearly renewal mints a new, untrusted CA and never re-installs trust | Core/CertManager.cs:27-30, ViewModels/MainViewModel.cs:154 |
| 2 | High | Printing | Preview flow silently truncates label quantity to 100 (`^PQ500` becomes `^PQ100`) | Core/Registry.cs:123,128-129; Views/PrintPreviewWindow.axaml:15 |
| 3 | High | Printing | Saved default printer missing -> jobs silently rerouted to OS default / first printer | Core/Registry.cs:38-41,56,62 |
| 4 | Medium | Security | Requests with no Origin and no Sec-Fetch-Site are trusted as "local app": no gate, no preview | Server/BridgeServer.cs:112-119; Core/Registry.cs:104,110 |
| 5 | Medium | Security | Gate keys on host only: any port on localhost and plain-http origins are equivalent to the approved site | Server/BridgeServer.cs:110; Core/Models.cs:42 |
| 6 | Medium | Security | Prompt flooding; prompt window outlives its 90 s timeout and its buttons then do nothing | Core/HostGate.cs:28-47; App.axaml.cs:118-128 |
| 7 | Medium | Printing | Windows status bit table is wrong (jam shown as "Printing", toner-low blocks jobs) | Core/WinSpool.cs:70-82 |
| 8 | Medium | Printing | Client disconnect is not propagated: job still prints after the caller gave up -> duplicates | Core/Registry.cs:138,145; Server/BridgeServer.cs:186,211 |
| 9 | Medium | Printing | TCP send: one 4 s budget for connect+write, no half-close/drain | Core/Registry.cs:174-181 |
| 10 | Medium | Printing | `/read` opens a fresh connection, so it can never return the reply to a prior `/write` | Core/Registry.cs:184-192; Server/BridgeServer.cs:190-198 |
| 11 | Medium | Printing | `data` is always UTF-8 encoded; `/write` has no binary path | Core/Languages.cs:46; Server/BridgeServer.cs:186,209 |
| 12 | Medium | Robustness | settings.json written non-atomically; corrupt file is replaced by defaults with no backup | Core/SettingsStore.cs:48-65 |
| 13 | Medium | Robustness | Settings lists mutated without the lock that readers use | Core/HostGate.cs:50-78; Server/BridgeServer.cs:119 |
| 14 | Medium | Robustness | One endpoint failing to bind takes down both; no-ports case falls to Kestrel default port; no retry | Server/BridgeServer.cs:34-53 |
| 15 | Medium | Installer | Interactive uninstall leaves the EXE behind (helper fires before the message box is dismissed) | Installer.cs:136-138 |
| 16 | Medium | API | Malformed input returns 500 with exception text; 403 has no CORS headers; error shapes differ | Server/BridgeServer.cs:131,153-157,166-213 |
| 17 | Low | Printing | TSPL `PRINT m` / copies > 100 / partial repeat handling | Core/LinePreview.cs:65-72; Core/Registry.cs:145-151 |
| 18 | Low | Printing | WritePrinter failure still closes the document normally (partial label printed) | Core/WinSpool.cs:126-135 |
| 19 | Low | Certs | Corrupt server.pfx is never regenerated; hard-coded PFX password | Core/CertManager.cs:16,24-32 |
| 20 | Low | Robustness | Autostart points at whatever copy is running (Downloads, dev build, DMG) | ViewModels/MainViewModel.cs:286-322 |
| 21 | Low | Robustness | Second launch exits silently; fire-and-forget init; async-void timer | Program.cs:13-14; App.axaml.cs:55; MainViewModel.cs:152 |
| 22 | Low | Misc | Test page innerHTML, `AllowHost` keeps port/path, jobs.jsonl append race, NU1903 suppressed | see section |

---

## High

### 1. Certificate renewal produces an untrusted CA and nobody is told
`Core/CertManager.cs:27-30`, `Core/CertManager.cs:35-61`, `ViewModels/MainViewModel.cs:154-161`

- The server certificate is valid 397 days (`:55`). `Ensure()` regenerates when fewer than 30 days remain (`:27`).
- `Generate()` always creates a **new CA** (`:38-43`) because the old CA key was discarded by design. It overwrites `ltz-printbridge-ca.cer`.
- The automatic trust install only runs when `!Certs.IsTrusted() && !Store.Value.CertificateTrustAsked` (`MainViewModel.cs:154`). `CertificateTrustAsked` was set to `true` on first run and is never cleared.

Scenario: install on day 0, trust accepted. First app start after about day 367 -> new CA and server cert written, Kestrel serves the new cert, the trust store still holds only the old CA. HTTPS on 9101 now fails certificate validation for every site (Safari is HTTPS-only per docs/API.md). No prompt, no toast; only the Diagnostics page shows a failure. The old CA also stays in the trust store forever (harmless, its key is gone, but it accumulates).

Fix: in `Generate()` (or after `Ensure()` when a regeneration happened) reset `CertificateTrustAsked = false` so `InitAsync` re-installs trust, and remove the previous CA from the store by thumbprint before overwriting the file. Consider also checking expiry on a daily timer since the tray app can run for weeks.

### 2. Preview flow silently caps label quantity at 100
`Core/Registry.cs:123`, `:128-129`; `Views/PrintPreviewWindow.axaml:15`; `Views/PrintPreviewWindow.axaml.cs:23`

`initial = Math.Min(100, q[0] * times)`, the NumericUpDown has `Maximum="100"`, and after confirmation `copies = Math.Max(1, Math.Min(d.Copies, 100))` is written back with `LabelPreview.WithQuantity`.

Scenario: preview is on; the site sends `^XA...^PQ500^XZ` (or TSPL `PRINT 1,250`, EPL `P300`). The window shows "100", the operator presses Print without touching the field, the job is rewritten to `^PQ100` and the API returns `200 ok`. 400 labels are lost with no error. With preview off the same job prints 500, so behaviour depends on a UI setting.

Fix: do not rewrite when the operator did not change the value; raise the UI maximum to the language limit (ZPL 99,999,999; `WithQuantity` already allows 9999) or refuse with a clear error when the requested quantity exceeds the cap.

Related (Low): `copies` in `/api/v1/print` is silently clamped to 1..100 at `Registry.cs:145`; a caller asking for 500 gets 100 and `ok: true`.

### 3. Missing default printer silently reroutes every job
`Core/Registry.cs:38-41`, `:56`, `:59`, `:62`; `Core/Models.cs:50` (`RouteAllToDefault = true` by default)

When `DefaultPrinterId` is set but that printer is not in the current list, the `if` at `:40` is false and the code falls to `:41`. The OS default already has `IsDefault = true` from `:27`, otherwise `list[0]` is chosen. `Resolve()` then returns that printer for every request because routing is on by default.

Scenario: clinic selects "Zebra ZD230" as default. The queue disappears for one refresh (network/WSD queue offline, queue renamed, `EnumPrinters` second call failing at `WinSpool.cs:49`, or `lpstat` timing out so `CupsSpool.List()` returns an empty list at `Spool.cs:100`). The next label job is sent RAW to the OS default (for example an office laser printer, which prints pages of ZPL text) or to another label printer with a different language and stock. The API answers `200 submitted`.

The same applies on first run before any default is chosen: the OS default printer receives all label jobs.

Fix: when `DefaultPrinterId` (or the policy default) is set and not found, make `DefaultPrinter()`/`Resolve()` return null and answer `404/409 DEFAULT_PRINTER_UNAVAILABLE`. Keep the previous list when enumeration fails or returns nothing instead of replacing it.

---

## Medium

### 4. No-Origin requests skip the website gate and the preview
`Server/BridgeServer.cs:112-119`; `Core/Registry.cs:104`, `:110`

Line 115 rejects only when `Origin` is `"null"` or `Sec-Fetch-Site` is present and not `same-origin|none`. If both headers are absent the request becomes `site = "local app"`, which bypasses `HostGate` (`:119`) and, because `"local app"` is in `OwnSites`, also bypasses the operator preview.

Scenario: any local process (`curl -d '{"data":"^XA...^XZ"}' http://127.0.0.1:9100/api/v1/print`), a script, or another user's session on a shared host prints without a prompt, even when preview mode is "All". docs/API.md line 110 says no-Origin is allowed only for `Sec-Fetch-Site: same-origin|none`; the code is wider than the documentation.

Browser exposure is limited: modern browsers send `Sec-Fetch-Site` to loopback and send `Origin` on cross-origin POST, so this is mainly a local-process and legacy-browser path (Safari before 16.4 sends no Sec-Fetch headers, but only header-less GETs get through there, and they are read-only with no CORS headers).

Related deployment risk (**possible**, depends on environment): on Windows multi-session hosts (RDS/Citrix) loopback is shared between sessions. The mutex is `Local\` (per session, `Program.cs:13`), so the first user's instance owns 9100/9101 and other users' browsers talk to it and print on that user's printers.

Fix: require `Sec-Fetch-Site: same-origin|none` for the no-Origin path as documented, or issue a per-run token embedded in the test page; do not exempt the no-Origin path from preview.

### 5. Gate ignores scheme and port
`Server/BridgeServer.cs:110` (`site = ou.Host`); defaults at `Core/Models.cs:42`

- `localhost` and `127.0.0.1` are accepted by default, so `http://localhost:3000`, `http://127.0.0.1:8080` and every other local web server on any port are allowed without a prompt.
- Approving `app.example.com` also approves `http://app.example.com` (plain HTTP, injectable on a hostile network) and every port on that host.
- The prompt (`Views/HostPromptWindow.axaml`) shows only the host, so the operator cannot tell which scheme or port they are approving.
- Non-web schemes are accepted: `chrome-extension://<id>` yields a prompt showing only the opaque id.

Fix: store and match full origins (`scheme://host[:port]`), keep host-only entries as a migration path meaning "https only, default port", show the full origin in the prompt, and restrict the default localhost entries to the bridge's own ports.

Wildcard matching itself (`HostGate.cs:16-18`) is correct at the label boundary: `*.example.com` does not match `evilexample.com`, and does not match the apex. An entry of `*.` or `*.com` typed by the user would be very broad; there is no validation.

### 6. Prompt flooding and stale prompt windows
`Core/HostGate.cs:28-29`, `:39`, `:47`; `App.axaml.cs:118-128`

- De-duplication is per host only. A page that cycles through `a1.evil.example`, `a2.evil.example`, ... opens one Topmost, focus-taking window per name with no global cap or queue. Each request also holds a connection for 90 s.
- After 90 s `Ask` returns false, but `AskHost` never closes the window. The window stays; if the operator then clicks "Always allow", `tcs` completes but nothing is listening, so the click does nothing. A retry from the page meanwhile opens a second window for the same host.
- Closing the window with the title-bar X records a permanent Block (`HostPromptWindow.axaml.cs:9`, `HostGate.cs:44`).

Fix: one prompt at a time through a queue, a cap on pending unknown hosts (answer 403 immediately beyond it), close the window on timeout, and treat window-close as "not now" rather than a permanent block.

### 7. Wrong Windows printer status constants
`Core/WinSpool.cs:70-82`

Compared with `winspool.h`: `0x8` is PAPER_JAM (code says "Printing", level ok), `0x40` is PAPER_PROBLEM (code says "Paper jam"), `0x20000` is TONER_LOW (code says "Not available", level bad), PRINTING is `0x400`, NOT_AVAILABLE is `0x1000`.

Scenario: a driver reporting TONER_LOW/ribbon-low makes `JobManager` refuse every job at `Registry.cs:143-144` with "The printer is not available"; a real jam is shown as "Printing" and jobs are accepted.

Fix: correct the table (`0x8` jam, `0x400` printing, `0x1000` not available, `0x20000` toner low as warn).

### 8. Aborted requests still print
`Core/Registry.cs:138`, `:145`; `Server/BridgeServer.cs:186`, `:211`

`PrintAsync` takes no cancellation token. The per-printer semaphore is held while the preview window is open (up to 2 minutes), and queued jobs wait on `sem.WaitAsync()` with no timeout.

Scenario: preview is on, the web app uses a 30 s fetch timeout. The operator confirms after 45 s: the label prints, the web app has already reported failure and retries, producing a duplicate specimen label. Jobs queued behind an open preview behave the same way.

Fix: pass `ctx.RequestAborted` into `PrintAsync`, use it in `WaitAsync` and to close the preview; accept an optional idempotency key.

### 9. TCP send timeout and close
`Core/Registry.cs:174-181`

- One `CancellationTokenSource(4000)` covers connect, write and flush. A large graphic job to a printer that drains its buffer slowly is cancelled mid-stream: partial label, `failed`, then a client retry.
- The socket is disposed immediately after the write with no `Shutdown(Send)` and without reading. **Possible**: if the printer has sent anything back (status reply), closing with unread inbound data produces a reset and the printer can discard unprocessed data.
- With `copies > 1` a failure on repeat N returns `failed` although N-1 copies already printed (`:147-151`).

Fix: separate connect timeout (about 4 s) from a write timeout scaled to payload size, then `Shutdown(SocketShutdown.Send)` and drain until EOF or a short timeout. Report how many repeats succeeded.

### 10. `/read` cannot return a printer reply
`Core/Registry.cs:184-192`; `Server/BridgeServer.cs:190-198`

`SendTcp` and `ReadTcp` each open and close their own connection. A Zebra reply to `~HS`/`~HQES` is sent on the connection that carried the command, which is already closed. `/read` therefore waits 1.5 s and returns an empty string, so Zebra Browser Print's send-then-read status checks never work. A connect timeout or refused connection throws outside the inner `try` and surfaces as a 500. `/read` also does not take the per-printer lock.

The endpoint is not an SSRF vector: the address comes only from `ManualPrinters` in settings, never from the request.

Fix: keep one connection per write+read exchange (for example cache the socket per printer for a few seconds, or add a combined endpoint), take the printer lock, and map connection errors to 502.

### 11. Encoding is always UTF-8
`Core/Languages.cs:46`; `Server/BridgeServer.cs:186`, `:209`

`Encode` ignores the language argument.
- ZPL without `^CI28`, EPL, TSPL without a UTF-8 code page, and ESC/POS expect a legacy code page: non-ASCII patient names print as mojibake.
- A caller that packs binary into a JS string (chars 0x80-0xFF, common for ESC/POS raster, TSPL `BITMAP`, ZPL `~DG` binary) gets each such byte expanded to two bytes: corrupted graphics.
- `/write` has no `base64` alternative, so binary cannot be sent through the Zebra-compatible path at all.

Fix: add an `encoding` field (`utf-8`, `latin1`, code pages) to both endpoints, accept `base64` on `/write`, and document that `data` is UTF-8.

`Guess()` (`Languages.cs:18-33`) affects only the displayed language and the test label, not the bytes sent. Its substring rules are loose ("tsc", "zt2", "gk4", "epson"), Zebra EPL-only models are guessed as ZPL, and Godex/Argox are assumed to be in ZPL emulation.

### 12. settings.json durability
`Core/SettingsStore.cs:48-65`

`File.WriteAllText` truncates then writes. A crash, power loss, or the installer's `Kill(true)` (`Installer.cs:69`) during a save leaves a truncated file. `Load()` then falls back to defaults, and the next `Save()` (immediately, via `Policy.Apply` or `InitAsync`) overwrites the damaged file. Allowed sites, manual printers, the default printer and `CertificateTrustAsked` are lost with no backup.

Also: a hand-edited `"AcceptedHosts": null` deserialises to null and every request then throws in `HostGate.Match`; out-of-range ports or `MaxPayloadKb <= 0` are not validated.

Fix: write to a temp file and `File.Replace`/`Move` over the original, keep `settings.bak`, rename an unreadable file to `settings.corrupt-<date>.json`, and normalise nulls and ranges after load.

### 13. Unsynchronised settings collections
`Core/HostGate.cs:24-30` (reads under `_lock`) versus `:50-78` and `Core/Policy.cs:39-40` (writes without it); `Core/SettingsStore.cs:62`

`Accept`, `Block`, `Remove`, `ResetAll` and the view-model mutate `List<string>` instances that Kestrel threads enumerate in `Match`. A request arriving while the operator clicks Allow can throw "Collection was modified" at `BridgeServer.cs:119`, which is outside the `try` at `:133`, giving an unhandled 500. `Save()` serialises the same live object graph and can fail the same way; the failure is logged and the change is not persisted until a later save.

Fix: take `_lock` in every mutator, or treat the lists as immutable and swap a new list in.

### 14. Listener start-up
`Server/BridgeServer.cs:24-53`

- `o.Listen(...)` only registers an endpoint; binding happens in `StartAsync`. The `try/catch` around the IPv6 `Listen` calls (`:44-45`) therefore catches nothing. If `[::1]:9100` or `[::1]:9101` is taken while the IPv4 port is free, `StartAsync` throws and neither HTTP nor HTTPS comes up.
- If both ports are busy, no endpoint is registered and Kestrel falls back to its default `http://localhost:5000` (**possible**, standard Kestrel behaviour; not executed here), while the UI reports "Not running".
- There is no retry. If the old instance is still exiting during an upgrade, or Zebra Browser Print holds the port at login, the bridge stays down until the app is restarted.
- An over-limit body makes Kestrel throw inside `Body()`; the generic handler returns 500 rather than 413.

Fix: probe IPv6 the same way as IPv4 before registering, skip `StartAsync` when nothing is available, and retry binding every few seconds.

The 2 MB cap is enforced twice (Kestrel `MaxRequestBodySize` at `:41`, and decoded size at `Registry.cs:142`), which is good; the second check runs only after the printer lock is acquired and reports 502.

### 15. Uninstall leaves the executable
`Installer.cs:136-138`

The self-delete helper (`ping -n 3` then `del`) is started before `Tell("PrintBridge was removed.")`. The message box keeps the process alive, so unless the user clicks OK within about two seconds the `del` runs while the EXE is still locked and fails; `rmdir` then fails too.

Fix: show the message first, or have the helper loop until the file is gone (`:retry` / `if exist ... goto retry`), or wait on the PID.

Other installer notes (Low):
- `KillOthers` (`:65-72`) kills by process name, mid-job, with no graceful shutdown.
- The copy retry (`:82-83`) catches only `IOException`; an `UnauthorizedAccessException` from a just-killed image is not retried (**possible**).
- No version comparison: running an older EXE offers to "update" and downgrades.
- `rmdir` has no `/s`, so a `policy.json` next to the EXE keeps the folder.
- Silent uninstall keeps settings and the trusted CA (documented). There is no macOS uninstall: the LaunchAgent plist, keychain trust and data folder remain after the app is dragged to Trash.
- `IsInstalled()` (`:36`) does not dispose the registry key.
- Quoting of `UninstallString`, the Run value and the `cmd /c` line is correct for paths with spaces.

### 16. API behaviour versus docs/API.md
`Server/BridgeServer.cs`

- Invalid JSON, a non-object body, a non-string `printerId`/`data`, a string `copies`, or invalid base64 all throw and return `500` with the raw exception message as plain text (`:153-157`). These should be `400` with the documented JSON shape.
- Empty `data` returns `502 PRINT_FAILED`; too-large returns 502 or 500, never 413.
- A blocked or pending site gets `403` with no CORS headers (`:120`, `:131`), so page script sees a generic network error and cannot tell "blocked" from "not installed". Consider sending `Access-Control-Allow-Origin` on the 403.
- Native errors are JSON with 502; Zebra-path errors are plain text with 500 (`:187`); `/read` 404 has no body.
- `/write` success returns an empty body and no job id; there is no `copies`.
- With `RouteAllToDefault` (default on) `printerId` is ignored and `/printers` lists one printer; API.md does not say so, and 404 is then only returned when no printer exists.
- Missing: job status after "submitted", printer status codes (`PRINTER_OFFLINE` is folded into `PRINT_FAILED`), distinction between preview cancel and preview timeout (both 409), version/capability negotiation beyond the `/api/v1` prefix, idempotency.
- `/api/v1/printers` returns `status` and `isDefault`, which API.md does not list; `statusLevel` and `connection` are not exposed.

---

## Low

### 17. Quantity rewriting edge cases
`Core/ZplPreview.cs:21-47`, `Core/LinePreview.cs:25-26`, `:65-84`

Checked and found safe: text outside `^XA...^XZ` (for example `~DG` downloads) is preserved; CRLF is preserved; ZPL matching is case-insensitive; non-UTF-8 payloads are never rewritten (`Registry.cs:116`) and valid UTF-8 round-trips byte-for-byte; `^PQ5,0,1,Y` keeps its trailing parameters; a caret cannot appear literally inside `^FD` or `^FX`, so the "`^PQ` inside field data" case cannot occur with the default prefix.

Remaining issues:
- TSPL `PRINT 2` (two sets): `TsplQuantity` reports 1, the window shows 1, and the rewrite produces `PRINT 2,<n>`, so the operator's number is multiplied by the set count.
- `WithQuantity` also inserts `^PQn` into non-printing blocks (`^XA^DF...^XZ` templates, configuration-only blocks). **Possible** extra blank feeds on some firmware.
- A job that changes the command prefix (`^CC`/`~CC`) is not recognised; the lazy `\^XA.*?\^XZ` could then span unrelated text. **Possible**, rare.
- Detection is by content, not by the printer's language, so plain text for a non-label printer that happens to contain a line `N` and a line starting `P<digits>` would be treated as EPL and rewritten.
- `copies` given as `2.0` is silently treated as 1; as a string it returns 500.

### 18. Windows spooler write path
`Core/WinSpool.cs:112-138`

Handles are closed on every path and partial writes are looped correctly. On a `WritePrinter` failure the document is still ended normally, so a truncated label is released to the printer; `EndDocPrinter`'s result is ignored. Fix: on failure cancel the job (`SetJob` with `JOB_CONTROL_DELETE`, or `AbortPrinter`).

`QueueStatus` (`:84-89`) re-enumerates all printers for every job.

### 19. Certificate files
`Core/CertManager.cs`

- A damaged `server.pfx` throws at `:26`, is caught at `:32`, and `Generate()` is never reached: HTTPS stays down until the file is deleted by hand.
- PFX password is the constant `ltz-local` (`:16`); protection is file permissions only. Acceptable for a loopback-only key, but worth stating.
- `X509KeyStorageFlags.Exportable` is not needed. **Possible** on Windows: each load materialises a key container that is only removed on dispose.
- `NotAfter` (local time) is compared with `UtcNow` (`:27`); harmless at a 30-day margin.

Verified good: the CA key lives only in a `using` RSA and is never written; CA has `pathLen=0`; leaf has serverAuth EKU and SANs `localhost`, `127.0.0.1`, `::1`; 397-day validity meets Apple's limit; trust is per-user on both platforms; `security` and `certutil` are invoked without a shell.

### 20. Autostart
`ViewModels/MainViewModel.cs:286-322`

`ApplyAutostart` runs on every start with `StartWithWindows` defaulting to true and writes `Environment.ProcessPath`. Running a copy from Downloads with "No = run without installing", `--portable`, a `dotnet run` dev build, or the app straight from the mounted DMG registers that path for login. On macOS the path may be a translocated or `/Volumes` path that does not exist later (**possible**).

Fix: only write autostart when running from the install location (`Installer.InInstallDir()` / inside `/Applications`).

### 21. Start-up and lifetime
- `Program.cs:13-14`: a second launch returns silently; the existing window is not brought forward, so a user whose window is hidden in the tray sees nothing happen.
- `App.axaml.cs:55`: `_ = _vm.InitAsync()` discards exceptions; a throw before `Server.StartAsync` leaves the UI at "Starting" with no error.
- `MainViewModel.cs:152`: async-void timer handler; an exception from `RefreshAsync` is unhandled on the UI thread. Refreshes can overlap when one takes longer than 6 s. On macOS each refresh starts three `lpstat` processes.
- `App.axaml.cs:108-114`: if constructing `PrintPreviewWindow` throws, the `TaskCompletionSource` never completes and that printer's lock is held until restart (**possible**).

### 22. Miscellaneous
- `Server/Pages.cs:22`, `:24`: printer names and error messages are inserted with `innerHTML` on a page whose origin is allowed by default. A printer name containing markup would execute there. Use `textContent`.
- `ViewModels/MainViewModel.cs:253`: `AllowHost` strips the scheme but keeps port and path, so `localhost:4200` or `app.example.com/login` is stored and never matches.
- `Core/Registry.cs:169`: concurrent jobs on different printers append to `jobs.jsonl` without a lock; a sharing violation is swallowed and the record is lost. The file is trimmed only at start-up (`MainViewModel.cs:137`), and one bad line stops history loading (`:138-141`).
- `Server/BridgeServer.cs:156`: exception messages (which can include paths and queue names) are returned to the caller.
- `LtzPrintBridge.csproj`: `<NoWarn>NU1903</NoWarn>` hides NuGet's high-severity vulnerability warning; identify the package and remove the suppression.
- `mac/entitlements.plist`: `disable-library-validation` and `allow-unsigned-executable-memory` are broader than a .NET app normally needs (`allow-jit` is usually enough). **Possible** tightening.
- `Core/Spool.cs:46`: if `lp` exits early the error reported is a broken-pipe message rather than `lp`'s own text.

---

## What looked solid

- **Host header check** (`BridgeServer.cs:86-87`) runs first on every request including public pages, with an exact loopback list. This stops DNS rebinding.
- **Loopback-only binding** (`IPAddress.Loopback` / `IPv6Loopback`), server header removed.
- **Origin gate does not depend on preflight**: a `text/plain` simple POST to `/write` or `/api/v1/print` still carries `Origin` and is gated, so the classic CSRF path is closed. `Origin: null` is rejected. Look-alike hosts (`localhost.evil.com`) do not match.
- **CORS** headers are emitted only for allowed origins, echo the exact origin with `Vary: Origin`, and never allow credentials.
- **No path-based file access**; the only file served is the fixed CA path.
- **No command injection**: `lp`, `lpstat`, `lpoptions` and `security` use `ArgumentList` with `UseShellExecute = false`; `LC_ALL=C` makes parsing stable; 20 s timeout with kill.
- **WinSpool**: correct RAW datatype, handle cleanup in nested `finally`, partial-write loop, last-error reporting.
- **Per-printer serialisation** of jobs, prompt de-duplication per host, metadata-only job log (no label content on disk).
- **Binary safety of the rewrite**: strict UTF-8 decode gates all rewriting.
- **Preview renderers** catch their own exceptions, so a label the renderer cannot draw is still printable.
- **Log** rotation is bounded (2 MB plus one generation) and locked.
- **CA design**: per-install, key discarded, path length 0, per-user trust.
- **Installer** quoting, per-user registry writes, and uninstall removal of the Run value, uninstall key and shortcut.
