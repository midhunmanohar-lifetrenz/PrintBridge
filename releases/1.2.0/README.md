# PrintBridge 1.2.0 installers

| File | For |
|---|---|
| `PrintBridge-1.2.0-win-x64.zip` | Windows 10 and 11 (64-bit). Extract and run `PrintBridge.exe`. |
| `PrintBridge-1.2.0-mac-arm64.dmg` | Mac with Apple silicon (M1 and later) |
| `PrintBridge-1.2.0-mac-x64.dmg` | Mac with Intel processor |

**These builds are not signed.** Windows Smart App Control blocks the EXE on most Windows 11 PCs, and macOS needs
*System Settings > Privacy & Security > Open Anyway* on first start. Production builds must be signed and notarized
by following `docs/PrintBridge_Release_Guide.docx`. Check a download against `SHA256SUMS.txt`.

Installation steps: `docs/PrintBridge_User_Guide.docx`. Changes: `CHANGELOG.md`.
