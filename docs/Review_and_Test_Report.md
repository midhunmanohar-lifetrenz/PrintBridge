# PrintBridge 1.0.0: review and test report (12 Nov 2026)

> **Status, 13 Nov 2026:** the defects H1 to H5 and M1 to M13 and the printer-recognition gaps below were fixed in version 1.1.0 (see `CHANGELOG.md`). After the fix the 513-name test gives 0 wrong and 9 not recognised, and the automated suite has 199 passing tests. This report describes version 1.0.0 as reviewed. Nothing here has been run on physical printers.

Scope: web research on printers, browsers and operating systems; an independent code review; and simulated
tests with real printer names and real label programs. **No physical printer was used.** Supporting files are
in `docs/research/`.

Labels: **Tested** = run here against the app's code. **Reviewed** = read in the code and confirmed.
**Sourced** = from the web, not tested here. **Unconfirmed** = could not be verified.

## 1. What was tested

| Test | Input | Result |
|---|---|---|
| Printer recognition | 513 real printer and driver names, 33 brands (`research/printer_catalog.json`) | 188 correct, 110 correctly left "Not set" (language the app has no entry for), 115 not recognised, **100 guessed wrongly** |
| Label languages | One sample program for each of 15 languages (`research/language_samples.md`) | All 15 pass through unchanged as text. Preview picture for ZPL, EPL, TSPL only (plus PPLB and ESC/Label, which look the same). Nothing else was mis-detected. |
| Preview and copies | 28 tricky label programs (multi-label, graphics, comments, CRLF, lower case, stored formats) | No crash, graphics and line endings preserved. **4 wrong-quantity cases** (section 3). |
| Existing automated suite | 159 tests | 154 passed, 5 skipped (Windows-only), 0 failed |

A wrong language guess does **not** stop printing: the website's commands are always sent unchanged. It affects
the language shown, the built-in test label and nothing else. The user can correct it per printer.

## 2. Printer recognition: what is wrong (Tested)

| Brand | Names tested | Problem | Change needed |
|---|---|---|---|
| Godex | 31 | Guessed ZPL. Native language is EZPL; ZPL only on "GZPL" queues, EPL on "GEPL". | Add EZPL; read the GZPL / GEPL suffix |
| Argox | 33 | Guessed ZPL. The driver name states PPLA (Datamax-like), PPLB (EPL-like) or PPLZ (ZPL-like). | Read the PPLA / PPLB / PPLZ suffix |
| Honeywell | 19 | "- DP" guessed as DPL. It means Direct Protocol (Fingerprint). "- ESim" is EPL. | Read the "- DP", "- ESim", "- IPL", "- ZSim" suffix |
| Citizen | 25 | Not recognised. Plain names are Datamax emulation, "Z" suffix is ZPL. | Add Citizen (default emulation is Unconfirmed) |
| Xprinter, HPRT, iDPRT, Gainscha, Gprinter, Deli, Rongta, Munbyn, Rollo, Wincode and similar | 74 | Not recognised. Almost all are TSPL. | Add these brands as TSPL |
| Zebra | 62 | 7 wrong: LP/TLP 2844 is EPL; ZQ310/320 are CPCL. | Read the "EPL" / "CPCL" text in the name |
| TVS-E | 9 | 4 wrong: LP 46 Lite and Plus are TSPL, not BPLE. LP 46 Dlite, Neo and LP 45 are BPLE (correct). | Split by model |
| Brother | 19 | TD-4xxxTN is TSPL-compatible, guessed as driver-only. | Add the TN series |
| Epson | 16 | ColorWorks (CW-C…) guessed as ESC/POS. They use ESC/Label (ZPL-like). | Add ColorWorks |
| SATO, Intermec, Bixolon | 77 | SATO WS4 "SEPL/SDPL/SZPL", Intermec "- ESim" not read. | Read the suffixes |
| DYMO, Brother QL, Star TSP100 | about 30 | Raster-only. Raw label commands cannot work on these. | Show "needs driver printing, not supported" instead of "Not set" |

The TVS LP 46 Dlite factory language is still **Unconfirmed**: the drivers say BPLE (EPL-style), but no TVS
document found states it. Confirm on the real printer.

## 3. Defects found

### High

| # | Defect | Effect | Status |
|---|---|---|---|
| H1 | Secure-connection certificate is replaced after about a year, but the app never asks to trust the new one. | HTTPS (port 9101) silently stops working around month 12. Safari users are affected first. | Reviewed |
| H2 | With preview on, quantities above 100 are cut to 100 with no warning, even if the operator changed nothing. | A job for 500 labels prints 100 and reports success. | Reviewed |
| H3 | If the saved default printer is briefly missing (USB unplugged, spooler slow), jobs go to the Windows default or the first printer in the list. | Labels can come out on the wrong printer. | Reviewed |
| H4 | TSPL `PRINT 4` (4 sets) is shown as quantity 1; choosing 3 sends `PRINT 4,3` = 12 labels. | Wrong label count from the preview window. | Tested |
| H5 | EPL `P2,3` (2 sets of 3) is shown as 3, and choosing 3 leaves it unchanged = 6 labels. | Wrong label count from the preview window. | Tested |

### Medium

| # | Defect | Status |
|---|---|---|
| M1 | Windows status flags are misread: "paper jam" is shown as "Printing"; "toner low" blocks printing as "Not available". | Reviewed |
| M2 | ZPL: when `^PQ` appears twice in a label (for example after a `^FX` comment), the first is used; the printer uses the last. | Tested |
| M3 | A request with no website name (from a program on the same PC, not a browser) skips the allow-list and the preview. On shared servers (Citrix, Remote Desktop) another user's session may be able to print. | Reviewed |
| M4 | The allow-list checks the website name only, not `https` versus `http` or the port. | Reviewed |
| M5 | Unknown websites can open many permission windows at once; a window left open after 90 seconds does nothing; closing it with X blocks the website permanently. | Reviewed |
| M6 | If the website gives up while the preview is open, the label still prints when the operator confirms; a retry then prints it twice. | Reviewed |
| M7 | Network printers: one 4-second limit for connect and send; a failure on copy N is reported as failed although N-1 copies printed. | Reviewed |
| M8 | Zebra `/read` opens a new connection, so it can never return the printer's answer to a status command. | Reviewed |
| M9 | Text is always sent as UTF-8. Printers and languages that expect a code page (EPL, DPL, classic ESC/POS) print accented and non-Latin characters wrongly unless the website sends `base64`. The Zebra `/write` call has no `base64` option. | Reviewed |
| M10 | `settings.json` is not saved safely; a damaged file is replaced by defaults with no backup (allow-list lost). | Reviewed |
| M11 | If one of the two ports is busy at start, both can fail, and there is no retry. | Reviewed |
| M12 | Uninstall from the dialog can leave `PrintBridge.exe` behind. | Reviewed |
| M13 | Bad input (broken JSON, bad base64) returns error 500 with internal text instead of a clear 400. Blocked websites get no readable error in the browser. Error formats differ between the native and Zebra calls. | Reviewed |

Low items (11) are listed in `research/code_review.md`.

### What is solid (Reviewed)

Loopback-only listening, Host check before anything else (stops DNS rebinding), allow-list enforced on the server
for every call (so a "simple" form post cannot print), `Origin: null` rejected, correct wildcard matching, no
file access by path, no shell use for printer commands, label content never rewritten when it is not valid text,
the certificate authority key is destroyed after use.

## 4. Platform changes since the last review (Sourced)

| Area | Finding | Action |
|---|---|---|
| Chrome / Edge | The permission is now named `loopback-network` (since 145); the policy for IT is `LoopbackNetworkAllowedForUrls` (146+), alongside `LocalNetworkAccessAllowedForUrls`. The temporary opt-out policy is being removed (milestone Unconfirmed). | Update the user guide and the admin section; add a "connection blocked by the browser" help page |
| Firefox | Prompts for local access by default since 153 (July 2026). Trusts certificates installed in Windows or macOS by default. Policy: `LocalNetworkAccess.SkipDomains`. | Add Firefox steps to the guide; test |
| Safari | Still blocks `http://127.0.0.1` from secure pages. Must use HTTPS 9101 with the certificate trusted. | Makes H1 important; test |
| Websites in iframes | The parent page must grant `allow="local-network; loopback-network"` at every level. | Document for web teams |
| macOS | Sending to a network printer needs the "Local Network" permission; unsigned builds handle it unreliably. Developer ID certificates are now valid one year. macOS 27 is Apple-silicon only. | Add `NSLocalNetworkUsageDescription`; sign with Developer ID |
| Windows signing | **Azure Artifact Signing is not available to organisations in India.** A commercial OV or EV certificate is needed (now limited to 460 days). | Correct the Release Guide: use Option B |
| Windows printing | Microsoft prefers its own driver since 1 July 2026 and ends third-party driver updates on 1 July 2027. Protected Print Mode removes vendor drivers when switched on. | Direct network (TCP 9100) already works; consider direct USB printing later |

Unconfirmed: Zebra Browser Print's current version and its own handling of the browser prompt; whether raw
printing through CUPS changes in macOS 27; Safari 26 behaviour with `https://127.0.0.1:9101`.

## 5. Recommended changes, in order

1. **Fix H1 to H5 and M1, M2** (certificate renewal, quantity handling, default-printer fallback, status flags). Small, contained code changes with tests.
2. **Improve printer recognition** using the 513-name list as a permanent test (section 2). Add EZPL, PPLA/PPLB, Fingerprint, SBPL, TPCL as named languages, and mark raster-only printers as unsupported.
3. **Correct the documents**: Windows signing option for India, the new Chrome and Firefox permission and policy names, iframe note, Firefox and Safari steps.
4. **Harden M3 to M6, M10, M11, M13** (no-website requests, permission-window limits, safe settings save, clear errors).
5. **Text encoding (M9)**: per-printer code page option and `base64` for the Zebra call.
6. **Later**: printer status read-back (fixes M8), signed builds, direct USB printing, PDF or image labels, MSI package and auto-update.

## 6. Still to be tested on real equipment

TVS LP 46 Dlite and a TSC printer (language and test label), one Zebra over USB on Windows 11 with the signed
build, Firefox and Safari end to end, a network printer on macOS with the Local Network permission, the Windows
installer and build script, Apple notarization.
