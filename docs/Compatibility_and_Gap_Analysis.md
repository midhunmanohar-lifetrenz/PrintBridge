# PrintBridge: compatibility, gaps and standards (22 Oct 2026)

Status labels: **Verified** = tested by us. **Sourced** = from a web source listed at the end, not tested by us. **Unknown** = not confirmed.

## 1. Will it work with all printers?

PrintBridge sends the label commands from the website to the printer unchanged (raw printing). So it works with any printer that understands the commands the website sends, through a Windows driver queue, a macOS CUPS queue or a network address (port 9100).

| Printer family | Language | Status |
|---|---|---|
| Zebra (ZD, ZT, GK, GX, ZQ) | ZPL, EPL, CPCL | Verified end to end on Mac (CUPS) and Windows USB (your test). Zebra BrowserPrint.js not tested. |
| TSC | TSPL / TSPL2 | Raw path is the same. Not tested on hardware. |
| TVS / SNBC LP46 Dlite | BPLE (BPLA/B/Z) | Language unconfirmed. Printer must be set to whatever the website sends. |
| SATO | SBPL | Raw works. No test label or auto-detect. |
| Honeywell / Datamax | DPL, Fingerprint, IPL | DPL has a test label. Fingerprint/IPL raw only. |
| Receipt printers (Epson, Star, Bixolon) | ESC/POS, StarPRNT | ESC/POS test label. Star not tested. |
| Brother QL/PT, DYMO LabelWriter | Raster / vendor SDK | **Not covered.** These usually need the driver to render the label, not raw commands. |
| Inkjet / colour label (Epson ColorWorks) | Driver rendered | **Not covered.** |

Gaps as built:
- Print preview draws ZPL, TSPL and EPL labels only (approximate, 203 dpi assumed). Other languages show commands.
- No PDF, image or HTML printing. The website must send printer commands.
- No reading back printer status or errors from the printer (only the Windows or CUPS queue state).
- Needs a Windows or CUPS queue for USB. No direct USB (WinUSB/libusb) for printers without a driver.
- macOS no longer allows raw queues; a PPD driver (for example the stock Zebra ZPL one) must be added.
- Windows Protected Print Mode removes third-party drivers and only supports Mopria-certified printers (Sourced). Label printers behind a vendor driver may disappear on PCs where IT turns it on. Raw TCP (manual network printer) is the fallback. Not tested.

## 2. Will it work with all browsers?

| Browser | Status |
|---|---|
| Chrome, Edge (Windows, Mac) | Works. Since Chrome 142 each website must be granted Local Network Access (prompt or policy). Verified on your UAT site. |
| Firefox | Mozilla says a similar local-device prompt rolls out gradually from Firefox 151 (Sourced). Firefox has its own certificate store, so the HTTPS certificate must be imported. Not tested. |
| Safari | WebKit blocks HTTPS pages from calling http://localhost as mixed content (Sourced). Only HTTPS 9101 with a trusted certificate works. Not tested. |
| Brave, Opera, other Chromium | Likely follow Chrome. Brave Shields may block local requests. Unknown. |
| Chromebook, Android, iOS | **Not supported.** A local bridge cannot run there. |
| Linux desktop | No build yet. |
| Websites inside iframes | Need explicit permission delegation; policies do not override this (Sourced). |

## 3. Market standards to adopt

1. **Local Network Access (W3C / Chrome / Edge / Firefox).** Document the per-site grant and the Chrome policy `LocalNetworkAccessAllowedForUrls` (names Sourced from vendor articles; confirm in Chrome Enterprise docs). Already sends `Access-Control-Allow-Private-Network`.
2. **Zebra Browser Print API** (done, best effort) and **QZ Tray API** (WebSocket 8181/8182, signed requests). QZ is the common alternative in ERPs; a compatible endpoint would widen adoption. Not built.
3. **Code signing.** Windows needs Authenticode (Smart App Control blocks unsigned apps). macOS needs Developer ID plus notarization.
4. **Enterprise packaging.** MSI or MSIX for Intune/Group Policy, and auto-update. Today: self-install and a silent switch only.
5. **IPP / IPP Everywhere / Mopria** for driverless printing and Windows Protected Print Mode.
6. **Cloud fallback** (agent polls a server) for Chromebooks, mobile and locked-down PCs. Not built.
7. **WebUSB / WebSerial** as a no-install option in Chrome and Edge only (Sourced: libraries exist; Chromium-only).

## 4. Suggested order

| Priority | Item |
|---|---|
| P0 | Sign Windows and Mac builds. Publish the Chrome/Edge/Firefox permission steps and enterprise policy for clinics. |
| P0 | Test TSC, TVS LP46 and one Brother/DYMO printer on real hardware. |
| P1 | PDF/image to raster (ZPL ^GF, TSPL BITMAP) so websites can send PDFs. |
| P1 | Printer status read-back (Zebra ~HS, TSPL status) and clearer job results. |
| P1 | MSI and auto-update. Firefox and Safari test pass. |
| P2 | QZ Tray API compatibility. Direct USB. Linux build. |
| P3 | Cloud print agent for mobile and Chromebook. |

## Sources

- Chrome Local Network Access: https://developer.chrome.com/blog/local-network-access
- Chrome LNA enterprise policy discussion: https://groups.google.com/a/chromium.org/g/blink-dev/c/cwu_RUmBpzY
- Edge LNA: https://learn.microsoft.com/en-us/deployedge/ms-edge-local-network-access
- Firefox LNA prompt: https://support.mozilla.org/en-US/kb/control-personal-device-local-network-permissions-firefox
- MDN Local network access: https://developer.mozilla.org/en-US/docs/Web/Security/Defenses/Local_network_access
- Safari mixed content on localhost (WebKit bug): https://bugs.webkit.org/show_bug.cgi?id=171934
- Windows Protected Print Mode: https://learn.microsoft.com/en-us/windows/modern-print/windows-protected-print-mode/windows-protected-print-mode
- QZ Tray signing: https://qz.io/wiki/signing
- QZ Tray raw printing: https://qz.io/docs/What-is-Raw-Printing
- Zebra Browser Print forum: https://developer.zebra.com/forum/25566
- WebZLP (WebUSB): https://app.unpkg.com/webzlp@2.0.3/files/README.md
- Brother developer information: https://support.brother.com/g/s/es/dev/en/index.html
- DYMO SDK: https://www.dymo.com/online-support-sdk.html
