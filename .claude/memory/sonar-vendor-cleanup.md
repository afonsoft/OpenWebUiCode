# SonarCloud — cleanup de issues vendor stale (ação humana)

Data da análise: 2026-10-17 · projeto `afonsoft_open-webui` (SonarCloud).

## Contexto

O board tem ~2900 issues OPEN. **~2260 (78%) são de `docs/` e `skills/`** —
código vendor/documentação criado entre 08–10/out, **antes** das exclusões
do PR #397 (`sonar.exclusions` cobrindo `docs/**` e `skills/**`). As
exclusões funcionam — o scan de 17/out só criou issues em `src/` — mas o
SonarCloud **não fecha automaticamente** issues de arquivos que saíram do
escopo da análise; elas ficam OPEN até resolução manual.

Precisa de `SONAR_TOKEN` (secret de CI — não disponível em ambiente local).

## Opção 1 — UI do SonarCloud (recomendada, sem scripting)

1. https://sonarcloud.io/project/issues?id=afonsoft_open-webui&resolved=false
2. Filtro **Directories**: selecionar `docs` → bulk change: selecionar tudo
   (checkbox do header) → "Resolve as Won't Fix". Repetir para `skills`.
3. (Opcional) filtro `tools/` — avaliar se é código mantido ou vendor antes
   de resolver.

## Opção 2 — API bulk_change (scriptável)

```bash
export SONAR_TOKEN="<token>"
PROJECT=afonsoft_open-webui
for DIR in docs skills; do
  # pagina em blocos de 500 issues por chamada
  while true; do
    KEYS=$(curl -s -u "$SONAR_TOKEN:" \
      "https://sonarcloud.io/api/issues/search?componentKeys=$PROJECT&directories=$DIR&resolved=false&ps=500&p=1" \
      | python3 -c "import sys,json;print(','.join(i['key'] for i in json.load(sys.stdin)['issues']))")
    [ -z "$KEYS" ] && break
    curl -s -u "$SONAR_TOKEN:" -X POST \
      "https://sonarcloud.io/api/issues/bulk_change" \
      -d "issues=$KEYS&do_transition=wontfix" >/dev/null
  done
done
```

## Resultado esperado

Board passa de ~2900 para ~640 issues — todas de produto (`src/` 603,
`tools/` 33, `tests/` 2, Dockerfile 1, workflow 1). Métricas de
reliability/security/maintainability voltam a refletir o código real.

## Triagem das issues de produto (pós-cleanup)

- **Já resolvido no repo**: S8949 ×2 (RequestAborted), S2699 ×2 (asserts em
  testes), css:S8776 — PR #427 merged.
- **Revisão real pendente**: S5693 (ChatView.razor:1818 — form sem CSRF?),
  S7039 (Program.cs:333 — CSP incompleta).
- **FP/won't-fix provável**: S6506 no workflow (o `--proto-redir =https` já
  existe — FP do scanner), S7044/S2068/S5332/S6471/S2245 em contextos
  controlados.
