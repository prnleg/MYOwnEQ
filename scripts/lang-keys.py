#!/usr/bin/env python3
"""Collect every L("...") key from the Mac (Swift) and Windows (C#) sources.

  scripts/lang-keys.py          regenerate lang/en-US.json, report gaps
  scripts/lang-keys.py --check  only report; exit 1 if a pack misses keys

Both apps share lang/*.json, so en-US.json is the union of both platforms.
"""
import json, pathlib, re, sys

root = pathlib.Path(__file__).resolve().parents[1]
sources = [(root / "mac" / "Sources", "*.swift"), (root / "windows" / "MyOwnEQ", "*.cs")]
lang = root / "lang"
pat = re.compile(r'\bL\(\s*"((?:[^"\\]|\\.)*)"')
bad_cs = re.compile(r'\bL\(\s*\$"')

keys = set()
for base, glob in sources:
    for f in base.rglob(glob):
        if "/bin/" in str(f) or "/obj/" in str(f):
            continue
        text = f.read_text(encoding="utf-8")
        if bad_cs.search(text):
            sys.exit(f"{f.relative_to(root)}: L($\"...\") — keys must be literals. Use %@ placeholders.")
        for m in pat.finditer(text):
            k = m.group(1)
            if "\\(" in k:
                sys.exit(f"{f.relative_to(root)}: interpolation inside L(): {k!r}. Use %@ placeholders.")
            keys.add(k.encode().decode("unicode_escape").encode("latin1").decode("utf8"))

# Keys used through a variable (filter kinds, macro names) — keep them in the template.
keys |= {"Peak", "Low Shelf", "High Shelf", "Low Pass", "High Pass", "Notch", "Band Pass", "Bass", "Mids", "Clarity", "Treble"}

check = "--check" in sys.argv
if not check:
    en = {"code": "en-US", "name": "English (US)", "author": "MyOwnEQ",
          "strings": {k: k for k in sorted(keys, key=str.lower)}}
    (lang / "en-US.json").write_text(json.dumps(en, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"en-US.json: {len(keys)} keys (Mac + Windows)")

bad = False
for f in sorted(lang.glob("*.json")):
    if f.name == "en-US.json":
        continue
    pack = json.loads(f.read_text(encoding="utf-8"))
    have = {k for k, v in pack.get("strings", {}).items() if v}
    missing, unused = sorted(keys - have), sorted(set(pack.get("strings", {})) - keys)
    print(f"{f.name}: {len(keys) - len(missing)}/{len(keys)} translated")
    for k in missing:
        print(f"  missing: {k}")
    for k in unused:
        print(f"  unused:  {k}")
    bad |= bool(missing)
sys.exit(1 if (check and bad) else 0)
