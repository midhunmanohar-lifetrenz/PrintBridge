#!/usr/bin/env bash
# Builds PrintBridge.app and a .dmg for macOS.
#   ./mac/build-mac.sh [arm64|x64]
# Optional environment:
#   SIGN_IDENTITY   "Developer ID Application: Company (TEAMID)"  (default: ad-hoc signing, for local testing only)
#   NOTARY_PROFILE  name saved with: xcrun notarytool store-credentials <name> ...   (enables notarization)
set -euo pipefail
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
ARCH="${1:-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/src/LtzPrintBridge/LtzPrintBridge.csproj" | head -1)"; OUT="$ROOT/dist/mac-$ARCH"; APP="$OUT/PrintBridge.app"
IDENTITY="${SIGN_IDENTITY:--}"
rm -rf "$OUT"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

dotnet publish "$ROOT/src/LtzPrintBridge" -c Release -r "osx-$ARCH" --self-contained true -p:DebugType=none -p:UseAppHost=true -o "$OUT/publish" >/dev/null
cp -R "$OUT/publish/." "$APP/Contents/MacOS/"

# icon
ICONSET="$OUT/icon.iconset"; mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s "$ROOT/src/LtzPrintBridge/Assets/icon.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$ROOT/src/LtzPrintBridge/Assets/icon.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/PrintBridge.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>PrintBridge</string>
  <key>CFBundleDisplayName</key><string>PrintBridge</string>
  <key>CFBundleIdentifier</key><string>com.lifetrenz.printbridge</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>PrintBridge</string>
  <key>CFBundleIconFile</key><string>PrintBridge</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSLocalNetworkUsageDescription</key><string>PrintBridge sends labels to network printers on your local network.</string>
  <key>NSHumanReadableCopyright</key><string>Lifetrenz</string>
</dict></plist>
PLIST

# sign every nested binary first, then the bundle (hardened runtime is required for notarization)
SIGN=(codesign --force --options runtime --timestamp --entitlements "$ROOT/mac/entitlements.plist" --sign "$IDENTITY")
[ "$IDENTITY" = "-" ] && SIGN=(codesign --force --sign -)
find "$APP/Contents/MacOS" -type f ! -name PrintBridge -print0 | xargs -0 -n1 -P4 "${SIGN[@]}" 2>/dev/null
"${SIGN[@]}" "$APP/Contents/MacOS/PrintBridge"
"${SIGN[@]}" "$APP"
codesign --verify --deep --strict "$APP"

# Notarize the app itself first and staple the ticket to it, so it also opens on a Mac that is offline.
if [ -n "${NOTARY_PROFILE:-}" ] && [ "$IDENTITY" != "-" ]; then
  APPZIP="$OUT/PrintBridge-app.zip"
  ditto -c -k --keepParent "$APP" "$APPZIP"
  xcrun notarytool submit "$APPZIP" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP"
  rm -f "$APPZIP"
fi

# dmg with an Applications shortcut
DMG="$ROOT/dist/PrintBridge-$VERSION-mac-$ARCH.dmg"; STAGE="$OUT/dmg"; rm -rf "$STAGE" "$DMG"; mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"; ln -s /Applications "$STAGE/Applications"
cp "$ROOT/mac/README-mac.txt" "$STAGE/README.txt"
hdiutil create -volname "PrintBridge" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
[ "$IDENTITY" != "-" ] && codesign --force --timestamp --sign "$IDENTITY" "$DMG"

if [ -n "${NOTARY_PROFILE:-}" ] && [ "$IDENTITY" != "-" ]; then
  xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$DMG"
  echo "--- Checks ---"
  spctl -a -vvv -t install "$DMG" || true
  xcrun stapler validate "$DMG" || true
fi
echo "Built: $DMG   (version $VERSION)"
