# SonarCloud — FPs conhecidos `json:S2068` em i18n (SPEC-20261031-sonar-vuln-fp-triage / GH#564)

Data da triagem: 2026-10-31 · projeto `afonsoft_open-webui` (SonarCloud).

## Falsos positivos confirmados

| Issue Sonar | Arquivo:linha | Chave | Valor (label pt-BR) |
|---|---|---|---|
| `AaEl44TaMT913z7YBsg0` | `src/OpenWebUI.Client/wwwroot/i18n/pt-BR.json:132` | `settings.current_password` | `"Senha atual"` |
| `AaEl44TaMT913z7YBsg1` | `src/OpenWebUI.Client/wwwroot/i18n/pt-BR.json:161` | `settings.new_password` | `"Nova senha"` |
| `AaEl44TaMT913z7YBsg2` | `src/OpenWebUI.Client/wwwroot/i18n/pt-BR.json:176` | `settings.allow_password_login` | `"Permitir login com senha"` |

**Por que é FP:** os valores são labels de UI; a heurística dispara na
palavra `password` no **nome da chave**. Nenhum segredo presente.

**Por que não renomear:** `tools/check-i18n-parity.py` (CI) exige o mesmo
conjunto de chaves nos 8 locales e as chaves são referenciadas em
`src/OpenWebUI.Client/Components/SettingsModal.razor` e
`src/OpenWebUI.Client/Pages/Admin.razor`. Custo/benefício pende para
resolução formal, não para rename.

## Regra de recorrência

Findings `S2068` (hard-coded credential) em `src/OpenWebUI.Client/wwwroot/i18n/*.json`
são **FP conhecidos** quando o valor é label de UI — o arquivo é 100%
tradução. Antes de abrir trabalho, conferir esta nota.

## Resolução pendente — ação humana (requer `SONAR_TOKEN`)

### Opção 1 — UI do SonarCloud

1. https://sonarcloud.io/project/issues?id=afonsoft_open-webui&resolved=false&rules=json%3AS2068
2. Selecionar as 3 issues acima → "Resolve as **False Positive**" (ou Won't
   Fix) com comentário:
   > i18n label key, not a credential — key name triggers heuristic;
   > rename blocked by i18n parity guard (tools/check-i18n-parity.py).

### Opção 2 — API (scriptável)

```bash
export SONAR_TOKEN="<token>"
curl -s -u "$SONAR_TOKEN:" -X POST \
  "https://sonarcloud.io/api/issues/bulk_change" \
  -d "issues=AaEl44TaMT913z7YBsg0,AaEl44TaMT913z7YBsg1,AaEl44TaMT913z7YBsg2" \
  -d "do_transition=falsepositive" \
  -d "comment=i18n label key, not a credential — key name triggers heuristic; rename blocked by i18n parity guard"
```

## Verificação

- `python3 tools/check-i18n-parity.py` → OK (1266 keys, 8 locales) — nada renomeado.
- Nenhum diff de código no caminho won't-fix.
