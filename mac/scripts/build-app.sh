#!/usr/bin/env bash
# Build MyOwnEQ.app (universal: Apple Silicon + Intel).
#   mac/scripts/build-app.sh            release, universal
#   mac/scripts/build-app.sh --native   release, this Mac's arch only (faster)
# Env: SIGN_IDENTITY (default: first "Apple Development" identity, else ad-hoc "-")
#      VERSION (default: latest git tag without the v, else 0.1.0)
set -euo pipefail
if [[ -n "${GITHUB_ACTIONS:-}" ]]; then
  trap 'echo "::error::build-app.sh line $LINENO failed: $BASH_COMMAND"' ERR
fi

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
MAC="$ROOT/mac"
OUT="$MAC/build"
APP="$OUT/MyOwnEQ.app"
source "$HOME/.cargo/env" 2>/dev/null || true

if [[ "${1:-}" == "--native" ]]; then
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
swift build -c release --package-path "$MAC" "${SWIFT_ARCHS[@]}"
BIN="$(swift build -c release --package-path "$MAC" "${SWIFT_ARCHS[@]}" --show-bin-path)/MyOwnEQ"

echo "==> Bundle"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/MyOwnEQ"
cp "$MAC/Resources/Info.plist" "$APP/Contents/Info.plist"
VERSION="${VERSION:-$( (git -C "$ROOT" describe --tags --abbrev=0 2>/dev/null || true) | sed 's/^v//')}"
VERSION="${VERSION:-0.1.0}"
plutil -replace CFBundleShortVersionString -string "$VERSION" "$APP/Contents/Info.plist"
plutil -replace CFBundleVersion -string "$VERSION" "$APP/Contents/Info.plist"
echo "    version $VERSION"

IDENTITY="${SIGN_IDENTITY:-$(security find-identity -v -p codesigning | awk -F'"' '/Apple Development/{print $2; exit}')}"
IDENTITY="${IDENTITY:--}"
echo "==> Sign ($IDENTITY)"
codesign --force --options runtime --timestamp=none \
  --entitlements "$MAC/Resources/MyOwnEQ.entitlements" \
  --sign "$IDENTITY" "$APP"

echo "==> Done: $APP"
