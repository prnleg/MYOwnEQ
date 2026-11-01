#!/usr/bin/env bash
# Regenerate windows/MyOwnEQ/Assets/MyOwnEQ.ico from the shared logo (same
# drawing code as the Mac app). Run on a Mac after changing the logo:
#   windows/scripts/make-icon.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BIN="$(swift build -c release --package-path "$ROOT/mac" --show-bin-path)/MyOwnEQ"
[[ -x "$BIN" ]] || swift build -c release --package-path "$ROOT/mac"
TMP="$(mktemp -d)"
for s in 16 20 24 32 40 48 64 128 256; do
  # Small sizes fill the whole canvas so the shape stays readable.
  fill=$([[ $s -le 32 ]] && echo 1.0 || echo 0.92)
  "$BIN" --render-icon wave "$s" "$TMP/$s.png" "$fill"
done
PY3=$(command -v /usr/bin/python3 || command -v python3)
"$PY3" - "$TMP" "$ROOT/windows/MyOwnEQ/Assets/MyOwnEQ.ico" <<'PY'
import struct, sys, pathlib
src, out = pathlib.Path(sys.argv[1]), sys.argv[2]
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
pngs = [(s, (src / f"{s}.png").read_bytes()) for s in sizes]
header = struct.pack("<HHH", 0, 1, len(pngs))
offset = 6 + 16 * len(pngs)
entries, data = b"", b""
for s, png in pngs:
    dim = 0 if s >= 256 else s
    entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset + len(data))
    data += png
pathlib.Path(out).write_bytes(header + entries + data)
print(f"wrote {out} ({len(pngs)} sizes)")
PY
rm -rf "$TMP"
