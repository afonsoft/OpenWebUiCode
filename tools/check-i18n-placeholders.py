#!/usr/bin/env python3
"""Guard de placeholders i18n (SPEC-20261017-calendar-mobile-ui AC-1).

Falha quando:
  - um valor de locale usa placeholder de chave simples {nome} — L.T só
    interpola {{nome}}; {nome} renderizaria literal na tela;
  - uma chave com {{nome}} é consumida via L["..."] (indexer, sem
    interpolação) — renderizaria {{nome}} cru;
  - um call site L.T("key", ...) não passa todos os placeholders da chave.

Uso: python3 tools/check-i18n-placeholders.py
"""

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
I18N = ROOT / "src/OpenWebUI.Client/wwwroot/i18n"
CLIENT = ROOT / "src/OpenWebUI.Client"

SINGLE_BRACE = re.compile(r"(?<!\{)\{(\w+)\}(?!\})")
DOUBLE_BRACE = re.compile(r"\{\{(\w+)\}\}")


def load_interpolated_keys():
    """Chaves cujo valor (en-US) contém placeholders {{nome}}."""
    en = json.loads((I18N / "en-US.json").read_text(encoding="utf-8"))
    return {k: set(DOUBLE_BRACE.findall(str(v))) for k, v in en.items()
            if DOUBLE_BRACE.search(str(v))}


def check_single_brace(errors):
    """Nenhum valor de locale pode usar {nome} — sempre {{nome}}."""
    for path in sorted(I18N.glob("*.json")):
        if path.name == "locales.json":
            continue
        data = json.loads(path.read_text(encoding="utf-8"))
        for key, value in data.items():
            bad = SINGLE_BRACE.findall(str(value))
            if bad:
                errors.append(
                    f"{path.name}: '{key}' usa placeholder de chave simples "
                    f"{bad} — use {{{{...}}}} (L.T não interpola {{x}}).")


def lt_args(source, lt_pos):
    """Extrai os nomes de arg de um call L.T(...) começando em `lt_pos` (o 'L')."""
    i = source.index("(", lt_pos)  # '(' que abre a chamada T(
    depth = 0
    for j in range(i, len(source)):
        if source[j] == "(":
            depth += 1
        elif source[j] == ")":
            depth -= 1
            if depth == 0:
                return set(re.findall(r'\(\s*"(\w+)"\s*,', source[i:j + 1]))
    return set()


def check_call_sites(interp_keys, errors):
    """Valida usos de L[...] e L.T(...) em .razor/.cs contra as chaves."""
    files = list(CLIENT.rglob("*.razor")) + list(CLIENT.rglob("*.cs"))
    for path in files:
        source = path.read_text(encoding="utf-8")
        rel = path.relative_to(ROOT)
        for m in re.finditer(r'L\["([^"]+)"\]', source):
            if m.group(1) in interp_keys:
                line = source[: m.start()].count("\n") + 1
                errors.append(
                    f"{rel}:{line}: L[\"{m.group(1)}\"] tem placeholders "
                    "{{x}} mas é lida sem interpolação — use L.T(...).")
        for m in re.finditer(r'L\.T\("([^"]+)"\s*,', source):
            key = m.group(1)
            if key not in interp_keys:
                continue
            args = lt_args(source, m.start())
            missing = interp_keys[key] - args
            if missing:
                line = source[: m.start()].count("\n") + 1
                errors.append(
                    f"{rel}:{line}: L.T(\"{key}\") sem arg(s) "
                    f"{sorted(missing)} — placeholder renderizaria cru.")


def main():
    errors = []
    check_single_brace(errors)
    check_call_sites(load_interpolated_keys(), errors)
    if errors:
        print("i18n placeholder guard FALHOU:")
        for e in errors:
            print(" -", e)
        sys.exit(1)
    print("OK: placeholders i18n consistentes ({{nome}} + args completos)")


if __name__ == "__main__":
    main()
