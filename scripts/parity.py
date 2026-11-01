#!/usr/bin/env python3
"""Keep the Mac and Windows apps feature-equal (see parity/features.json, CONTRIBUTING.md).

  scripts/parity.py           check; exit 1 on any problem
  scripts/parity.py --write   also regenerate PARITY.md from the registry

Checks:
  1. every feature marked "yes" for a platform has a `// parity: <id>` marker in
     that platform's sources;
  2. every marker names a feature in the registry, and the feature says "yes" there;
  3. a feature marked "no" (missing on a platform) explains why in "note";
  4. every L("…") text used on only one platform is declared in platform_strings
     (or variable_keys) — otherwise the other app is missing that screen/message;
  5. declared platform strings still exist (no stale entries);
  6. PARITY.md matches the registry.
"""
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCES = {
    "mac": (ROOT / "mac" / "Sources", "*.swift"),
    "windows": (ROOT / "windows" / "MyOwnEQ", "*.cs"),
}
MARKER = re.compile(r"//\s*parity:\s*([a-z0-9-]+)")
KEY = re.compile(r'\bL\(\s*"((?:[^"\\]|\\.)*)"')


def files(platform):
    base, glob = SOURCES[platform]
    for f in base.rglob(glob):
        if "/bin/" in str(f) or "/obj/" in str(f):
            continue
        yield f


def scan(platform):
    markers, keys = {}, set()
    for f in files(platform):
        text = f.read_text(encoding="utf-8")
        for m in MARKER.finditer(text):
            markers.setdefault(m.group(1), []).append(f.relative_to(ROOT))
        for m in KEY.finditer(text):
            keys.add(m.group(1).encode().decode("unicode_escape").encode("latin1").decode("utf8"))
    return markers, keys


LABEL = {"yes": "✅", "no": "⛔", "n/a": "—"}


def render(reg):
    plats = reg["platforms"]
    out = [
        "# Paridade Mac × Windows",
        "",
        "<!-- Gerado por scripts/parity.py --write a partir de parity/features.json. Não edite à mão. -->",
        "",
        "Os dois apps devem ter os mesmos recursos. Cada linha abaixo vem de",
        "`parity/features.json`; o CI confere se o código das duas plataformas tem o",
        "marcador `// parity: <id>` de cada recurso e se nenhum texto da interface existe",
        "em só um dos apps sem justificativa. Veja `CONTRIBUTING.md`.",
        "",
        "✅ tem · ⛔ falta (com motivo) · — não se aplica",
        "",
        "| Recurso | id | " + " | ".join(p.capitalize() for p in plats) + " | Obs. |",
        "|---|---|" + ":-:|" * len(plats) + "---|",
    ]
    for f in reg["features"]:
        cells = " | ".join(LABEL[f.get(p, "n/a")] for p in plats)
        out.append(f"| {f['title']} | `{f['id']}` | {cells} | {f.get('note', '')} |")
    out.append("")
    return "\n".join(out)


def main():
    reg = json.loads((ROOT / "parity" / "features.json").read_text(encoding="utf-8"))
    plats = reg["platforms"]
    ids = {f["id"]: f for f in reg["features"]}
    errors = []

    scanned = {p: scan(p) for p in plats}
    for f in reg["features"]:
        for p in plats:
            state = f.get(p, "n/a")
            if state not in LABEL:
                errors.append(f"{f['id']}: {p} must be yes / no / n/a, not {state!r}")
            if state == "yes" and f["id"] not in scanned[p][0]:
                errors.append(f"{f['id']}: marked yes for {p} but no `// parity: {f['id']}` in {p} sources")
            if state == "no" and not f.get("note"):
                errors.append(f"{f['id']}: missing on {p} — add a note explaining why")
    for p in plats:
        for mid, where in scanned[p][0].items():
            if mid not in ids:
                errors.append(f"unknown parity id {mid!r} in {', '.join(map(str, where))}")
            elif ids[mid].get(p) != "yes":
                errors.append(f"{mid}: marker in {p} ({where[0]}) but the registry says {ids[mid].get(p)!r} — update features.json")

    shared = set(reg.get("variable_keys", []))
    declared = {p: set() for p in plats}
    for g in reg.get("platform_strings", []):
        for p in plats:
            declared[p] |= set(g.get(p, []))
    for p in plats:
        others = set().union(*(scanned[o][1] for o in plats if o != p))
        only_here = scanned[p][1] - others - shared
        for k in sorted(only_here - declared[p]):
            errors.append(f"text only in {p}: {k!r} — add it to the other app, or declare it in platform_strings with a reason")
        for k in sorted(declared[p] - scanned[p][1]):
            errors.append(f"stale platform_strings entry for {p}: {k!r} (no longer used)")

    md = render(reg)
    parity_md = ROOT / "PARITY.md"
    if "--write" in sys.argv:
        parity_md.write_text(md, encoding="utf-8")
        print("PARITY.md written")
    elif not parity_md.exists() or parity_md.read_text(encoding="utf-8") != md:
        errors.append("PARITY.md is out of date — run scripts/parity.py --write")

    if errors:
        print(f"parity: {len(errors)} problem(s)")
        for e in errors:
            print("  -", e)
        sys.exit(1)
    print(f"parity: OK ({len(ids)} features, {', '.join(plats)})")


if __name__ == "__main__":
    main()
