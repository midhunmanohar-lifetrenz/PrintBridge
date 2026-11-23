# PrintBridge 1.3.0 installers

| File | For |
|---|---|
| `PrintBridge-1.3.0-win-x64.zip` | Windows 10 and 11 (64-bit). Extract and run `PrintBridge.exe`. |
| `PrintBridge-1.3.0-mac-arm64.dmg` | Mac with Apple silicon (M1 and later) |
| `PrintBridge-1.3.0-mac-x64.dmg` | Mac with Intel processor |

New in 1.3.0: **Start scanner** (tray menu or Ctrl+Shift+S) reads barcodes and QR codes with the camera and types them
into the web page field that has the cursor. macOS asks for Camera and Accessibility permission; Windows needs
*Settings > Privacy & security > Camera > Let desktop apps access your camera*.

**These builds are not signed.** Windows Smart App Control blocks the EXE on most Windows 11 PCs, and macOS needs
*System Settings > Privacy & Security > Open Anyway* on first start. Production builds must be signed and notarized
by following `docs/PrintBridge_Release_Guide.docx`. Check a download against `SHA256SUMS.txt`.

Installation steps: `docs/PrintBridge_User_Guide.docx`. Changes: `CHANGELOG.md`.
