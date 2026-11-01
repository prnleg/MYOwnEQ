#!/usr/bin/env python3
"""Collect every L("...") key from the Mac sources.

  mac/scripts/lang-keys.py          regenerate lang/en-US.json, report gaps
  mac/scripts/lang-keys.py --check  only report; exit 1 if a pack misses keys
"""
import json, pathlib, re, sys

root = pathlib.Path(__file__).resolve().parents[2]
src = root / "mac" / "Sources"
lang = root / "lang"
pat = re.compile(r'\bL\(\s*"((?:[^"\\]|\\.)*)"')

keys = set()
for f in src.rglob("*.swift"):
    for m in pat.finditer(f.read_text()):
        k = m.group(1)
        if "\\(" in k:
            sys.exit(f"{f.name}: interpolation inside L(): {k!r}. Use %@ placeholders.")
        keys.add(k.encode().decode("unicode_escape").encode("latin1").decode("utf8"))

check = "--check" in sys.argv
if not check:
    en = {"code": "en-US", "name": "English (US)", "author": "MyOwnEQ",
          "strings": {k: k for k in sorted(keys, key=str.lower)}}
    (lang / "en-US.json").write_text(json.dumps(en, ensure_ascii=False, indent=2) + "\n")
    print(f"en-US.json: {len(keys)} keys")

bad = False
for f in sorted(lang.glob("*.json")):
    if f.name == "en-US.json":
        continue
    pack = json.loads(f.read_text())
    have = {k for k, v in pack.get("strings", {}).items() if v}
    missing, unused = sorted(keys - have), sorted(set(pack.get("strings", {})) - keys)
    print(f"{f.name}: {len(keys) - len(missing)}/{len(keys)} translated")
    for k in missing:
        print(f"  missing: {k}")
    for k in unused:
        print(f"  unused:  {k}")
    bad |= bool(missing)
sys.exit(1 if (check and bad) else 0)
