PrintBridge 1.1.0
Label printing for web applications. Windows 10 and 11, 64-bit. Works with Zebra (ZPL), TSC (TSPL), TVS / SNBC (BPLE, BPLZ, BPLB)
and other label printers that accept raw commands.

INSTALL
1. Quit Zebra Browser Print or QZ Tray if installed (they use the same ports, 9100 and 9101).
2. Run PrintBridge.exe and choose Yes when it asks to install. No administrator rights are needed.
   It installs to %LOCALAPPDATA%\Programs\PrintBridge, adds a Start menu shortcut and an entry in Settings > Apps, then opens.
   (Choose No to run it from the current folder without installing.)
3. Windows asks once to trust the "PrintBridge Local CA" certificate. Click Yes.
   This removes the "Your connection is not private" screen for https://localhost:9101.
4. The app runs in the system tray and starts when you sign in. Close the window to keep it running; use Quit in Settings or the tray menu to stop it.

FIRST USE
0. USB printers: install the printer's Windows driver, plug it in and switch it on. It appears on the Printers page as "USB".
   Pick it as the Default printer at the top of the Printers page. With "Send every print request here" on (default),
   every print from any website goes to that printer, whatever printer the website asks for. Turn it off to let websites choose.
   If the printer is off, unplugged or out of paper, the print fails with a clear message instead of waiting silently in the Windows queue.
1. Printers: choose the printer, then check its Language (ZPL II, TSPL, BPLE...). Press "Print test label".
2. Network printers: "Add network printer", enter its IP address and port 9100.
3. Open your web application. The first time a website asks to use the printers a prompt appears: Always allow, Allow once or Block.

PREVIEW BEFORE PRINTING (optional, off by default)
- On the Printers page set "Preview before printing" to All websites, or to Only chosen websites and tick Preview next to each website on the Websites page.
  When one of those websites prints, a window shows the label and asks how many copies. Print sends it, Cancel stops it (the website is told it was cancelled).
  No change is needed in the website.
- The picture is drawn for ZPL, TSPL and EPL labels (EPL-style BPLE too, unconfirmed); other languages show their printer commands. It is an approximation of the printer's output, assuming 203 dpi.
- The copies are written into the label itself (ZPL ^PQ, TSPL PRINT m,n, EPL P), so one job prints exactly that many. "Do not ask again for this website for 2 minutes" helps when a page sends many labels in a row.

FOR IT
- Preload approved websites and the default printer with policy.json (see policy.sample.json).
- Settings, logs and the certificate are stored in %LOCALAPPDATA%\LtzPrintBridge (settings.json, bridge.log, jobs.jsonl, certs).
- Silent start at sign-in is on by default; turn it off in Settings.
- Firefox keeps its own certificate list: import https://localhost:9101/ca.cer under Settings > Privacy and Security > View Certificates > Authorities.
- Diagnostics (in the app) checks the service, secure connection, printers and ports.
- Silent install for IT (no prompts, no window):  PrintBridge.exe --install --silent
  Update: run the same command with the newer PrintBridge.exe. It replaces the installed copy and keeps all settings.
- Uninstall: Settings > Apps > PrintBridge > Uninstall. It asks whether to delete settings, history and the trusted certificate.
  Silent uninstall (keeps settings and certificate):  "%LOCALAPPDATA%\Programs\PrintBridge\PrintBridge.exe" --uninstall --silent

API FOR DEVELOPERS
  GET  http://127.0.0.1:9100/api/v1/printers
  POST http://127.0.0.1:9100/api/v1/print   {"printerId":"...","data":"<label commands>","copies":1}
Sites written for Zebra Browser Print (BrowserPrint.js) work unchanged. No setting is needed.
