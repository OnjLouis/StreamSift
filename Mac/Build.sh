#!/bin/bash
set -euo pipefail
source_dir="$(cd "$(dirname "$0")/.." && pwd -P)"
scratch="${1:?Specify a fresh build directory outside the source tree}"
mkdir -p "$scratch"
scratch="$(cd "$scratch" && pwd -P)"
case "$scratch/" in "$source_dir/"*) echo "Build outside the source tree." >&2; exit 1;; esac
app="$scratch/StreamSift.app"
[[ ! -e "$app" ]]
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$scratch/cache"
scanners=()
for arch in arm64 x64; do
  scanner="$scratch/scanner-$arch"
  if [[ -n "${SCANNER_BUILD_ROOT:-}" ]]; then
    scanner="$SCANNER_BUILD_ROOT/scanner-$arch"
  else
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false \
      "${DOTNET:-dotnet}" publish "$source_dir/Scanner/Scanner.csproj" -c Release -r "osx-$arch" \
      -p:PublishAot=true -p:StripSymbols=true --artifacts-path "$scratch/artifacts" \
      --disable-build-servers -warnaserror -o "$scanner"
  fi
  scanners+=("$scanner/Scanner")
  swift_arch="$arch"
  [[ "$swift_arch" != x64 ]] || swift_arch=x86_64
  xcrun swiftc -O -warnings-as-errors -swift-version 5 -target "$swift_arch-apple-macosx13.0" \
    -module-cache-path "$scratch/cache" "$source_dir"/Mac/Sources/*.swift -o "$scratch/StreamSift-$arch"
done
xcrun lipo -create "$scratch/StreamSift-arm64" "$scratch/StreamSift-x64" -output "$app/Contents/MacOS/StreamSift"
xcrun lipo -create "${scanners[@]}" -output "$app/Contents/MacOS/Scanner"
cp "$source_dir/Mac/Info.plist" "$app/Contents/Info.plist"
# Embed the third sound without adding an archive entry that older updaters reject.
cue="$(/usr/bin/base64 -i "$source_dir/Windows/Assets/error.wav" | /usr/bin/tr -d '\r\n')"
/usr/bin/plutil -insert StreamSiftFailureSound -data "$cue" "$app/Contents/Info.plist"
cp "$source_dir/Mac/Manual.html" "$source_dir/AngleSharp-LICENSE.txt" "$source_dir"/Windows/Assets/{search,done}.wav "$app/Contents/Resources/"
dotnet_executable="$(command -v "${DOTNET:-dotnet}")"
dotnet_root="$(dirname "$(realpath "$dotnet_executable")")"
cp "$dotnet_root/LICENSE.txt" "$app/Contents/Resources/DotNet-LICENSE.txt"
cp "$dotnet_root/ThirdPartyNotices.txt" "$app/Contents/Resources/DotNet-ThirdPartyNotices.txt"
chmod -R u=rwX,go=rX "$app"
if [[ -n "${SIGN_IDENTITY:-}" ]]; then
  for code in "$app/Contents/MacOS/Scanner" "$app"; do
    codesign --force --options runtime --timestamp --sign "$SIGN_IDENTITY" "$code"
  done
else
  codesign --force --sign - "$app/Contents/MacOS/Scanner"
  codesign --force --sign - "$app"
fi
codesign --verify --deep --strict "$app"
"$app/Contents/MacOS/StreamSift" --self-test
