#!/usr/bin/env bash
# check-env-drift.sh — verifica se toda variável de ambiente lida pelo código
# (Environment.GetEnvironmentVariable("X") em src/) está documentada em
# .env.exemplo. Vars de framework/sistema (ASPNETCORE_*, DOTNET_*, PATH, HOME,
# ConnectionStrings__*) são allowlisted — o gate cobre só vars do app.
set -euo pipefail
cd "$(dirname "$0")/.."

# src/OpenWebUI.Api/data/ é runtime local não rastreado — excluído do grep.
used=$(grep -rhoE 'Environment\.GetEnvironmentVariable\("[A-Z_]+"\)' \
    src --include='*.cs' --exclude-dir=data \
    | sed -E 's/.*\("([A-Z_]+)"\)/\1/' | sort -u)

missing=0
echo "== Variáveis lidas via GetEnvironmentVariable =="
for v in $used; do
    case "$v" in
        PATH|HOME|ASPNETCORE_*|DOTNET_*|ConnectionStrings__*|WEBUI_PORT)
            echo "  SKIP $v (framework/sistema)"
            continue
            ;;
        *) ;;
    esac
    if grep -q "$v" .env.exemplo; then
        echo "  OK   $v"
    else
        echo "  MISS $v"
        missing=$((missing + 1))
    fi
done

if [[ "$missing" -gt 0 ]]; then
    echo "-> $missing variável(is) sem documentação em .env.exemplo"
    exit 1
fi
echo "-> .env.exemplo cobre todas as variáveis do app"
