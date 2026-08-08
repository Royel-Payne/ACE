"""Extract every Shadowgain-added config dial from PropertyManager.cs.

Parses the property registration table rather than reading it by hand, so the
reference doc cannot drift from the source through transcription error.
"""
import re
import sys

PM = r"C:\Git Projects\Shadowgain\ACE\Source\ACE.Server\Managers\PropertyManager.cs"

src = open(PM, encoding="utf-8").read()

# ("key", new Property<T>(default, "description")),
pat = re.compile(
    r'\("([a-z0-9_]+)"\s*,\s*new\s+Property<(\w+)>\(\s*(.*?)\s*,\s*"((?:[^"\\]|\\.)*)"\s*\)\s*\)',
    re.S,
)

rows = []
for m in pat.finditer(src):
    key, typ, default, desc = m.groups()
    desc = desc.replace('\\"', '"').replace("\\n", " ")
    desc = re.sub(r"\s+", " ", desc).strip()
    if "Shadowgain" in desc:
        rows.append((key, typ, default, desc))

rows.sort()

sys.stdout.reconfigure(encoding="utf-8")
print("TOTAL", len(rows))
for r in rows:
    print("\x1f".join(r))
