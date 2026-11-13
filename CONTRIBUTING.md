# Contributing

## Setup
Install the .NET 10 SDK, then `dotnet build PrintBridge.sln` and `./run-tests.sh`. No printer is needed; tests use fakes, a local TCP listener and (where available) real spoolers.

## Conventions
- Keep logic in `Core/`; windows and view-models only present and call it.
- Match the surrounding style (4 spaces, file-scoped namespaces, terse comments for non-obvious reasons only). `.editorconfig` is included.
- SkiaSharp is pinned to 2.88.9: use `SKPaint` text APIs (`SKFont.MeasureText(string)` does not exist there).
- UI colours come from theme brushes (`DynamicResource`) so Light and Dark both work.
- Never store label content in logs or history; metadata only.
- Add a test for every behaviour change; run the full suite before pushing.

## Add a printer language
1. Add the value to `PrintLanguage` (`Core/Models.cs`).
2. Teach `Languages.cs` to guess it from the printer name/driver and to encode text.
3. Optional: a test label and a preview (`LabelPreview.cs`).
4. Add tests in `UnitTests.cs` and update the user guide table.

## Change the version
Edit `<Version>` in `src/LtzPrintBridge/LtzPrintBridge.csproj` only. The API status, the Mac bundle and file names follow it.

## Build and release
- Windows: `release/windows/build-windows.cmd` (see `docs/PrintBridge_Release_Guide.docx`).
- macOS: `./mac/build-mac.sh arm64|x64` (set `SIGN_IDENTITY` and `NOTARY_PROFILE` for a signed, notarized build).
- Never commit certificates, `.p12`/`.pfx` files or passwords.
