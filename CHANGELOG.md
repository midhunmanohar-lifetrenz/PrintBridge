# Changelog

## 1.1.0 (13 Nov 2026)

Fixes from the November 2026 review (`docs/Review_and_Test_Report.md`). Not yet run on Windows or on physical printers.

### Fixed
- **Secure connection after one year:** a renewed certificate is trusted again automatically; an unreadable certificate file is replaced.
- **Quantities:** jobs left unchanged in the preview are sent exactly as received (no more cut to 100). The preview accepts up to 9999 per label. TSPL `PRINT m,n` and EPL `Pp1,p2` are read as sets x copies; ZPL uses the last `^PQ`. `copies` outside 1-100 is refused with a clear error instead of being changed.
- **Wrong printer:** if the default printer you chose is unplugged, jobs are refused (`503 DEFAULT_PRINTER_UNAVAILABLE`) instead of going to another printer.
- **Windows printer status:** paper jam, ribbon/toner low, printing and not-available flags are read correctly.
- **Permission window:** closing it no longer blocks the website for good; at most three windows open at once; a window nobody answers closes itself.
- **Preview:** if the website stops waiting, the preview closes and nothing prints (no duplicate on retry).
- **Network printers:** separate time limits for connecting and sending; the error says how many copies went out.
- **Zebra `/read`:** returns the printer's answer to a status command written just before.
- **Settings file:** saved safely with a backup copy; a damaged file no longer wipes the allow-list.
- **Start-up:** one busy port no longer stops the other; busy ports are retried every 20 seconds.
- **Uninstall:** the program file is removed after the confirmation message.
- **API errors:** broken JSON, bad base64, unknown encoding give `400 BAD_REQUEST` instead of 500.
- **Websites list:** a pasted address (`https://site:8443/path`) is stored as the website name.
- A failed Windows print no longer releases half a label to the printer.

### Added
- **Printer recognition** for Godex (EZPL), Argox (PPLA/PPLB/PPLZ), Honeywell and Intermec (Fingerprint, IPL, ESim), Citizen, SATO, Toshiba, Bixolon, Zebra EPL and CPCL models, TVS LP 46 Lite/Plus, Epson ColorWorks, and the TSPL brands (Xprinter, HPRT, iDPRT, Gainscha, Gprinter, Deli, Rongta, Munbyn, Rollo and others). Raster-only printers (DYMO, Brother QL, Star TSP100) are shown as "Driver only". Checked against 513 real names.
- New language entries and test labels: EZPL, PPLA, PPLB, Fingerprint, IPL, SBPL, TPCL (test labels follow the manuals; not tried on printers).
- `encoding` field on `/api/v1/print`, `printerEncodings` in `policy.json`, and `base64` on the Zebra `/write` call.
- Setting **Allow programs on this computer** (`allowLocalPrograms` in `policy.json`) for shared servers. Such requests now go through the preview.
- Only `http://` and `https://` website origins are accepted.
- macOS: Local Network permission text for network printers.

### Not changed (known)
- The allow-list is by website name; `http://` and `https://` of the same name, and all ports, count as one website.
- Blocked websites get a plain 403 without CORS headers (deliberate: nothing is revealed to them).
- Old PrintBridge certificates stay in the trust store after renewal (harmless: their key was destroyed).
- Per-printer encoding has no screen yet; set it in `policy.json` or `settings.json`.

## 1.0.0 (23 Oct 2026)
First packaged version.
