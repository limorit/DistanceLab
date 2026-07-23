#!/usr/bin/env bash
set -euo pipefail

rid="${1:-osx-arm64}"
case "$rid" in
  osx-arm64) architecture="arm64" ;;
  osx-x64) architecture="x86_64" ;;
  *) echo "Usage: $0 [osx-arm64|osx-x64]" >&2; exit 2 ;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cmake_command="${CMAKE_COMMAND:-cmake}"
dotnet_command="${DOTNET_COMMAND:-dotnet}"
if ! command -v "$cmake_command" >/dev/null; then echo "CMake 3.24 or newer is required." >&2; exit 2; fi
if ! command -v "$dotnet_command" >/dev/null; then echo ".NET 10 SDK or newer is required." >&2; exit 2; fi
if [[ -z "${OpenCV_DIR:-}" ]] && command -v brew >/dev/null; then
  OpenCV_DIR="$(brew --prefix opencv)/lib/cmake/opencv4"
  export OpenCV_DIR
fi
if [[ -z "${OpenCV_DIR:-}" ]] || [[ ! -f "$OpenCV_DIR/OpenCVConfig.cmake" ]]; then
  echo "Set OpenCV_DIR to an OpenCV 4 CMake package built for $architecture." >&2
  exit 2
fi

native_build="$root/build/native/$rid"
native_output="$root/artifacts/native/$rid"
publish_output="$root/artifacts/publish/$rid"
app="$root/artifacts/products/$rid/DistanceLab.app"

"$cmake_command" -S "$root" -B "$native_build" -G "Unix Makefiles" \
  -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES="$architecture" \
  -DOpenCV_DIR="$OpenCV_DIR" -DCMAKE_INSTALL_PREFIX="$native_output"
"$cmake_command" --build "$native_build" --parallel --target distance_tracker_native distance_tracker_native_test
"$native_build/native/distance_tracker_native_test"
"$cmake_command" --install "$native_build"

rm -rf "$publish_output" "$app"
"$dotnet_command" publish "$root/src/OpenCVDistanceLab.App/OpenCVDistanceLab.App.csproj" \
  -c Release -r "$rid" --self-contained true -p:PublishReadyToRun=true \
  -p:UseSharedCompilation=false -m:1 -nodeReuse:false -o "$publish_output"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Frameworks" "$app/Contents/Resources"
cp -R "$publish_output/." "$app/Contents/MacOS/"
cp "$root/packaging/macos/Info.plist" "$app/Contents/Info.plist"

icon_source="$root/src/OpenCVDistanceLab.App/Assets/app-icon.png"
iconset="$root/build/macos/AppIcon.iconset"
rm -rf "$iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$icon_source" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$icon_source" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
icon_output="$app/Contents/Resources/app-icon.icns"
if ! iconutil -c icns "$iconset" -o "$icon_output" 2>/dev/null; then
  # iconutil in some Command Line Tools releases can extract but cannot create
  # an otherwise valid iconset. ICNS supports PNG payloads directly, so keep a
  # deterministic fallback that needs only the python3 shipped with the tools.
  command -v python3 >/dev/null || { echo "python3 is required for the ICNS fallback." >&2; exit 2; }
  python3 - "$iconset" "$icon_output" <<'PY'
import pathlib
import struct
import sys

source = pathlib.Path(sys.argv[1])
destination = pathlib.Path(sys.argv[2])
entries = [
    (b"icp4", "icon_16x16.png"),
    (b"ic11", "icon_16x16@2x.png"),
    (b"icp5", "icon_32x32.png"),
    (b"ic12", "icon_32x32@2x.png"),
    (b"ic07", "icon_128x128.png"),
    (b"ic13", "icon_128x128@2x.png"),
    (b"ic08", "icon_256x256.png"),
    (b"ic14", "icon_256x256@2x.png"),
    (b"ic09", "icon_512x512.png"),
    (b"ic10", "icon_512x512@2x.png"),
]
chunks = []
for kind, name in entries:
    payload = (source / name).read_bytes()
    chunks.append(kind + struct.pack(">I", len(payload) + 8) + payload)
body = b"".join(chunks)
destination.write_bytes(b"icns" + struct.pack(">I", len(body) + 8) + body)
PY
fi

native_name="libdistance_tracker_native.dylib"
mv "$app/Contents/MacOS/$native_name" "$app/Contents/Frameworks/$native_name"
ln -s "../Frameworks/$native_name" "$app/Contents/MacOS/$native_name"

opencv_lib_dir="$(cd "$OpenCV_DIR/../.." && pwd)"

# Bundle all non-system dylib dependencies (including a shared OpenCV build) and
# rewrite their install names so the app does not depend on Homebrew at runtime.
for pass in 1 2 3 4 5 6; do
  changed=0
  while IFS= read -r binary; do
    while IFS= read -r dependency; do
      [[ -z "$dependency" ]] && continue
      case "$dependency" in
        /System/*|/usr/lib/*|@loader_path/*|@executable_path/*) continue ;;
        @rpath/*)
          name="$(basename "$dependency")"
          destination="$app/Contents/Frameworks/$name"
          if [[ ! -f "$destination" ]] && [[ -e "$opencv_lib_dir/$name" ]]; then
            cp -L "$opencv_lib_dir/$name" "$destination"
            chmod u+w "$destination"
            changed=1
          fi
          continue
          ;;
      esac
      name="$(basename "$dependency")"
      destination="$app/Contents/Frameworks/$name"
      if [[ ! -f "$destination" ]]; then
        cp "$dependency" "$destination"
        chmod u+w "$destination"
        changed=1
      fi
      install_name_tool -change "$dependency" "@loader_path/$name" "$binary"
    done < <(otool -L "$binary" | tail -n +2 | awk '{print $1}')
  done < <(find "$app/Contents/Frameworks" -type f -name '*.dylib')
  [[ "$changed" -eq 0 ]] && break
done
while IFS= read -r dylib; do
  install_name_tool -id "@rpath/$(basename "$dylib")" "$dylib" || true
done < <(find "$app/Contents/Frameworks" -type f -name '*.dylib')

identity="${APPLE_SIGN_IDENTITY:--}"
main_executable="$app/Contents/MacOS/DistanceLab"
while IFS= read -r binary; do
  [[ "$binary" == "$main_executable" ]] && continue
  if [[ "$identity" == "-" ]]; then
    codesign --force --sign "$identity" "$binary"
  else
    codesign --force --options runtime --sign "$identity" "$binary"
  fi
done < <(find "$app/Contents/Frameworks" "$app/Contents/MacOS" -type f)
if [[ "$identity" == "-" ]]; then
  codesign --force --entitlements "$root/packaging/macos/entitlements.plist" \
    --sign "$identity" "$main_executable"
  codesign --force --entitlements "$root/packaging/macos/entitlements.plist" \
    --sign "$identity" "$app"
else
  codesign --force --options runtime --entitlements "$root/packaging/macos/entitlements.plist" \
    --sign "$identity" "$main_executable"
  codesign --force --options runtime --entitlements "$root/packaging/macos/entitlements.plist" \
    --sign "$identity" "$app"
fi

if [[ -n "${APPLE_NOTARY_PROFILE:-}" ]] && [[ "$identity" != "-" ]]; then
  archive="$root/artifacts/products/$rid/DistanceLab.zip"
  ditto -c -k --sequesterRsrc --keepParent "$app" "$archive"
  xcrun notarytool submit "$archive" --keychain-profile "$APPLE_NOTARY_PROFILE" --wait
  xcrun stapler staple "$app"
fi

echo "macOS product: $app"
