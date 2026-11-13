PrintBridge 1.1.0 for Mac
Label printing for web applications. macOS 12 or newer.

INSTALL
1. Quit Zebra Browser Print or QZ Tray if installed (they use the same ports, 9100 and 9101).
2. Drag PrintBridge into Applications and open it.
3. The first time, macOS asks for your Mac password to trust the "PrintBridge Local CA" certificate. This removes the
   "Your connection is not private" warning for https://localhost:9101 in Safari and Chrome.
   (Firefox keeps its own certificate list; see the Firefox note below.)
4. On the Printers page choose your default printer. With "Send every print request here" on, every website prints to it.

PRINTERS
- Add the label printer in System Settings > Printers & Scanners first. Pick the manufacturer's driver, or the generic
  "Zebra ZPL Label Printer" driver if none exists. PrintBridge sends the label commands to the printer unchanged.
- USB printers show as USB, network printers as Network. Printers that are paused or disabled are reported clearly.
- Set each printer's Language (ZPL, TSPL, EPL, ...) to match what your website sends.

PREVIEW BEFORE PRINTING (optional, off by default)
- On the Printers page set "Preview before printing" to All websites, or to Only chosen websites and tick Preview next to each website on the Websites page.
  When one of those websites prints, a window shows the label and asks how many copies. Print sends it, Cancel stops it (the website is told it was cancelled).
  No change is needed in the website.
- The picture is drawn for ZPL, TSPL and EPL labels (EPL-style BPLE too, unconfirmed); other languages show their printer commands. It is an approximation of the printer's output, assuming 203 dpi.
- The copies are written into the label itself (ZPL ^PQ, TSPL PRINT m,n, EPL P), so one job prints exactly that many. "Do not ask again for this website for 2 minutes" helps when a page sends many labels in a row.

NOTES
- The app lives in the menu bar. Closing the window keeps it running. Quit from the menu bar icon.
- It starts when you log in. Turn that off in Settings.
- Settings, logs and the certificate are stored in ~/Library/Application Support/PrintBridge.
- Status page: http://localhost:9100/api/v1/status
- Firefox: Settings > Privacy & Security > Certificates > View Certificates > Authorities > Import the file
  ~/Library/Application Support/PrintBridge/certs/ltz-printbridge-ca.cer
- Uninstall: quit the app, delete it from Applications, delete ~/Library/Application Support/PrintBridge and
  ~/Library/LaunchAgents/com.lifetrenz.printbridge.plist, and remove "PrintBridge Local CA" from Keychain Access.
