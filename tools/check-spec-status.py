#!/usr/bin/env python3
"""Drift guard: SPEC bookkeeping consistency (.specs/ vs docs/specs/).

Convenção (.specs/README.md): `.specs/` só abriga trabalho em voo. SPECs
entregues devem ter `Status: Completed` e morar em `docs/specs/` com seção
`Delivered`. Este guard falha quando:

- um SPEC em `.specs/` não tem header `Status` reconhecível
  (`| Status |`, `Status:` ou `**Status**:`);
- um SPEC em `.specs/` tem status terminal (Completed/Done/Implemented/
  Merged) — deveria estar arquivado em `docs/specs/`;
- um SPEC em `docs/specs/` tem status não-terminal — foi arquivado cedo
  demais ou falta marcar Completed.

Uso: python3 tools/check-spec-status.py  (exit 0 = OK, 1 = drift)
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ACTIVE = ROOT / ".specs"
ARCHIVE = ROOT / "docs" / "specs"

STATUS_RE = [
    re.compile(r"\|\s*Status\s*\|\s*`?([^|`]+?)`?\s*\|"),
    re.compile(r"^\s*Status\s*:\s*(.+)$", re.M),
    re.compile(r"\*\*Status\*\*\s*:\s*(.+)"),
]
TERMINAL = ("completed", "done", "implemented", "merged", "delivered")


def read_status(path: Path) -> str | None:
    text = path.read_text(encoding="utf-8")
    for rx in STATUS_RE:
        m = rx.search(text)
        if m:
            return m.group(1).strip().strip("`").strip("*").strip()
    return None


def is_terminal(status: str) -> bool:
    return status.lower().split("(")[0].strip() in TERMINAL or any(
        status.lower().startswith(t) for t in TERMINAL
    )


def main() -> int:
    problems: list[str] = []

    for f in sorted(ACTIVE.glob("SPEC-*.md")):
        status = read_status(f)
        if status is None:
            problems.append(f"{f.relative_to(ROOT)}: sem header `Status`")
        elif is_terminal(status):
            problems.append(
                f"{f.relative_to(ROOT)}: status terminal `{status}` — "
                "entregue; mover para docs/specs/ com seção `Delivered`"
            )

    for f in sorted(ARCHIVE.glob("SPEC-*.md")):
        status = read_status(f)
        if status is None:
            problems.append(f"{f.relative_to(ROOT)}: sem header `Status`")
        elif not is_terminal(status):
            problems.append(
                f"{f.relative_to(ROOT)}: arquivado com status não-terminal "
                f"`{status}` — ainda em voo? devolver a .specs/ ou completar"
            )

    if problems:
        print("SPEC status drift:")
        for p in problems:
            print(f"  FAIL {p}")
        return 1

    n_active = len(list(ACTIVE.glob("SPEC-*.md")))
    n_arch = len(list(ARCHIVE.glob("SPEC-*.md")))
    print(f"OK: {n_active} SPECs em voo, {n_arch} arquivados, todos com Status")
    return 0


if __name__ == "__main__":
    sys.exit(main())
