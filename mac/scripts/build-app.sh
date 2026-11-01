#!/usr/bin/env bash
# Build MyOwnEQ.app (universal: Apple Silicon + Intel).
#   mac/scripts/build-app.sh            release, universal
#   mac/scripts/build-app.sh --native   release, this Mac's arch only (faster), dev build
#   mac/scripts/build-app.sh --run      like --native, then quit any running MyOwnEQ
#                                       and open the fresh build
# Dev builds (--native/--run) never self-update, so they can't be replaced by
# an update the installed app downloaded.
# Env: SIGN_IDENTITY (default: first "Apple Development" identity, else ad-hoc "-")
#      VERSION (default: latest git tag without the v, else 0.1.0)
set -Eeuo pipefail
if [[ -n "${GITHUB_ACTIONS:-}" ]]; then
  trap 'echo "::error::build-app.sh line $LINENO failed: $BASH_COMMAND"' ERR
fi

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
MAC="$ROOT/mac"
OUT="$MAC/build"
APP="$OUT/MyOwnEQ.app"
if ! command -v cargo >/dev/null && [[ -f "$HOME/.cargo/env" ]]; then
  set +u; source "$HOME/.cargo/env"; set -u
fi

DEV=0
RUN=0
case "${1:-}" in
  --native) DEV=1 ;;
  --run) DEV=1; RUN=1 ;;
esac
if [[ $DEV == 1 ]]; then
  ARCHS=("$(uname -m | sed 's/arm64/aarch64/')")
else
  ARCHS=(aarch64 x86_64)
fi

echo "==> Rust core (${ARCHS[*]})"
LIBS=()
for a in "${ARCHS[@]}"; do
  MACOSX_DEPLOYMENT_TARGET=14.2 cargo build --release -p myowneq-ffi --target "$a-apple-darwin" --manifest-path "$ROOT/Cargo.toml"
  LIBS+=("$ROOT/target/$a-apple-darwin/release/libmyowneq_ffi.a")
done
mkdir -p "$OUT/rust"
lipo -create "${LIBS[@]}" -output "$OUT/rust/libmyowneq_ffi.a"

echo "==> Swift app"
SWIFT_ARCHS=()
for a in "${ARCHS[@]}"; do SWIFT_ARCHS+=(--arch "$(echo "$a" | sed 's/aarch64/arm64/')"); done
BIN="$(swift build -c release --package-path "$MAC" "${SWIFT_ARCHS[@]}" --show-bin-path)/MyOwnEQ"
# SwiftPM doesn't track the Rust .a as an input: force a relink when it changed.
if [[ -f "$BIN" && "$OUT/rust/libmyowneq_ffi.a" -nt "$BIN" ]]; then rm -f "$BIN"; fi
swift build -c release --package-path "$MAC" "${SWIFT_ARCHS[@]}"

echo "==> Bundle"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/MyOwnEQ"
cp "$MAC/Resources/Info.plist" "$APP/Contents/Info.plist"
VERSION="${VERSION:-$( (git -C "$ROOT" describe --tags --abbrev=0 2>/dev/null || true) | sed 's/^v//')}"
VERSION="${VERSION:-0.1.0}"
plutil -replace CFBundleShortVersionString -string "$VERSION" "$APP/Contents/Info.plist"
plutil -replace CFBundleVersion -string "$VERSION" "$APP/Contents/Info.plist"
if [[ $DEV == 1 ]]; then
  STAMP="dev $(date '+%d/%m %H:%M:%S')"
  plutil -replace MyOwnEQBuild -string "$STAMP" "$APP/Contents/Info.plist"
  echo "    version $VERSION ($STAMP)"
else
  echo "    version $VERSION"
fi

# Themes + languages (shared with Windows later) + app icon rendered by the app itself.
mkdir -p "$APP/Contents/Resources/Themes"
cp "$ROOT"/themes/*.json "$APP/Contents/Resources/Themes/"
mkdir -p "$APP/Contents/Resources/Lang"
cp "$ROOT"/lang/*.json "$APP/Contents/Resources/Lang/"
ICONSET="$OUT/AppIcon.iconset"
rm -rf "$ICONSET" && mkdir -p "$ICONSET"
if "$BIN" --render-icon wave 1024 "$OUT/icon-1024.png"; then
  for sz in 16 32 128 256 512; do
    sips -z $sz $sz "$OUT/icon-1024.png" --out "$ICONSET/icon_${sz}x${sz}.png" >/dev/null
    sips -z $((sz * 2)) $((sz * 2)) "$OUT/icon-1024.png" --out "$ICONSET/icon_${sz}x${sz}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
  plutil -replace CFBundleIconFile -string "AppIcon" "$APP/Contents/Info.plist"
  echo "    icon ok"
else
  echo "    warning: icon render failed, using generic icon"
fi

IDENTITY="${SIGN_IDENTITY:-$(security find-identity -v -p codesigning | awk -F'"' '/Apple Development/{print $2; exit}')}"
IDENTITY="${IDENTITY:--}"
echo "==> Sign ($IDENTITY)"
codesign --force --options runtime --timestamp=none \
  --entitlements "$MAC/Resources/MyOwnEQ.entitlements" \
  --sign "$IDENTITY" "$APP"

echo "==> Done: $APP"

if [[ $RUN == 1 ]]; then
  echo "==> Restart"
  osascript -e 'quit app id "com.prnleg.myowneq"' >/dev/null 2>&1 || true
  for _ in $(seq 1 50); do pgrep -x MyOwnEQ >/dev/null || break; sleep 0.1; done
  pkill -x MyOwnEQ 2>/dev/null || true
  sleep 0.3
  open -n "$APP"
  echo "    running $STAMP (check Preferences or Sync & Updates → Version)"
fi
