#!/usr/bin/env python3
"""Independence gate — PORT_PLAN.md §11.

`ported/` lives inside the parent repo only for the duration of the port and must be
extractable into its own repository at any moment. This checks that no project,
props or targets file references a path that resolves outside this directory.

Relative paths are *resolved*, not pattern-matched: `tools/MathFingerprint` legitimately
uses `../../AsteroidsSim`, which lands back inside the root. Only paths that actually
escape are failures.

Exit 0 if clean, 1 otherwise.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Attributes in MSBuild files that carry a path we care about.
PATH_ATTRS = re.compile(
    r'<(?:ProjectReference|Import|Reference|Compile|None|Content|AdditionalFiles|EmbeddedResource)\b[^>]*'
    r'(?:Include|Project|HintPath)\s*=\s*"([^"]+)"',
    re.IGNORECASE,
)

# The one sanctioned exception: the Phase 1 differential-test harness references the
# legacy engine. It is deleted at the Phase 5 gate. Guarded by LEGACY_DIFF.
ALLOWED_ESCAPES = (
    "AsteroidsSim.Tests/Legacy/",          # Phase 1 differential test harness
    "tools/SpikePhysicsBaseline/",         # Phase 0 Spike B: measures the existing solver
)


def main() -> int:
    failures: list[str] = []
    scanned = 0

    patterns = ("*.csproj", "*.props", "*.targets", "*.sln")
    for pattern in patterns:
        for f in ROOT.rglob(pattern):
            if any(part in {"bin", "obj", ".git"} for part in f.parts):
                continue
            scanned += 1
            rel_f = f.relative_to(ROOT)
            try:
                text = f.read_text(encoding="utf-8")
            except UnicodeDecodeError:
                continue

            for raw in PATH_ATTRS.findall(text):
                if "$(" in raw or "*" in raw:
                    continue  # MSBuild variable or glob — can't resolve statically
                candidate = (f.parent / raw.replace("\\", "/")).resolve()
                try:
                    candidate.relative_to(ROOT)
                except ValueError:
                    if any(str(rel_f).startswith(a) for a in ALLOWED_ESCAPES):
                        print(f"  (allowed) {rel_f} -> {raw}")
                        continue
                    failures.append(f"{rel_f} references {raw} -> {candidate}")

    if failures:
        print("::error::Reference(s) escape ported/ — see PORT_PLAN.md §11")
        for x in failures:
            print(f"  {x}")
        return 1

    print(f"OK: no escaping path references ({scanned} project files scanned).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
