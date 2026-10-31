# gap-analysis-20261031 — bug `<|channel>` em produção + sonarqube-autofix

## Fontes

| Fonte | Estado |
| --- | --- |
| `.specs/` (6 em voo), `docs/specs/` (161 arquivadas) — `check-spec-status.py` OK | present |
| `CLAUDE.md`, `.claude/rules/`, `.claude/memory/` | present |
| Banco de produção `openwebui` container `/data/openwebui.db` (788MB, copiado p/ `/tmp`) | present |
| SonarCloud `afonsoft_open-webui` (API pública sem token) | present — 3063 issues OPEN baixadas |
| gh auth `afonsoft/OpenWebUiCode` | ok |
| Working tree | clean (`main` @ c971286) |
| Sibling skills write-specs/create-issues/orchestrator | present |
| Prior run | `gap-analysis-20261024.md` (gateway — entregue) |

## Incidente analisado (input do usuário)

Chat `30b15271-12c9-42b6-8fa6-ca2a8968ab5a` ("Análise Comparativa do OpenCode", user `4270e160`, criado 2026-10-31 16:43 UTC, ~150 msgs).

- `ChatMessages` `7ee15ec2` (pos 148, assistant/Gemini): `Content` termina em `\n\n<|channel>thought\n<channel|>`.
- `ChatMessages` `fbd17835` (pos 150): mesmo prefixo + `<|channel>thought\n<channel|>` **inline** seguido do restante da resposta.
- `ChatRunEvents` do run `cfeece72-09b5-4379-9116-f0cebe6901e1` (completed 17:49): evento `content_delta` contém o token **cru no `delta.content`** — veio do provider (OmniRoute → modelo "Gemini"), não é artefato de UI.
- Persistência verbatim em `ChatRunExecutor.cs:2464` (`Content = content`).

## Veredictos (categoria → prioridade → chave)

| Gap key | Prioridade | Veredicto | Evidência |
| --- | --- | --- | --- |
| GAP-implementation-reasoning-tag-leak | **Alta** | CONFIRMADO | AS-IS: `ProviderService` filtra só campos estruturados (`ExtractOpenAiReasoning` :532-548) + DSML (`DsmlToolCallFilter` :1101-1205) — zero handling de `<|…|>`/tags de raciocínio inline (grep vazio em src/tests). TO-BE: upstream `middleware.py:280-290` `DEFAULT_REASONING_TAGS` + `tag_output_handler` (:4198+, :5439) — detecta pares `<think>/<thought>/<|begin_of_thought|>/◁think▷` inline, move ao canal reasoning e re-emite output limpo. Produção: msgs 148/150 + `content_delta` com token cru. SPEC: `SPEC-20261031-reasoning-tag-sanitization.md`. |
| GAP-security-sonar-vulnerabilities | **Alta** | CONFIRMADO | 13 VULNERABILITY acionáveis (1 BLOCKER githubactions:S8482 exec-de-artefato; S7044 API traversal AuthService; S7039 CSP; S5693 upload cap; S6506×2 TLS; S8541; S4036; S6471; S5332; S2068×3 FP). SPECs: `sonar-vuln-ci-workflows`, `sonar-vuln-code`, `sonar-vuln-fp-triage`. |
| GAP-implementation-sonar-code-smells | **Alta** | CONFIRMADO | 757 CODE_SMELL acionáveis em 440 grupos rule×file — top: S3776 ×106 (65 arquivos), S3358 ×104 (50), S1192 ×97 (44), S107 ×53 (31), misc 254 (113), js 98 (11), scripts/tools 43 (8). Effort estimado Sonar: ~88,6h. SPECs: `sonar-smell-s3776|s3358|s1192|s107|csharp-misc|js|scripts-tools`. |
| GAP-automation-sonar-vendor-stale | Média | DUPLICADO | 2293 issues OPEN em `docs/`, `skills/`, `.agents/`, `.claude/`, `.devin/`, `.ci/` — já excluídos do scan (`sonar.exclusions`) mas issues não fecham sozinhas. Ação manual documentada em `.claude/memory/sonar-vendor-cleanup.md` (requer SONAR_TOKEN). Nada novo a criar. |
| GAP-implementation-bugs-docs | Baixa | DUPLICADO | 2 BUGs (`S2777` em `docs/architecture/runtime-architecture.html`) — mesmo bucket vendor-stale acima. |
| GAP-tests-special-token-filter | Média | CONFIRMADO→coberto | Sem teste para markup não-DSML; a SPEC do bugfix inclui RF-006 (regressão c/ payload real) — coberto pela mesma SPEC, não vira SPEC própria. |
| GAP-observability-content-sanitize-log | Baixa | REJEITADO | Sanitização silenciosa já é o padrão DSML (logs só contam calls) — sem gap. |
| Data fix retroativo `ChatMessages` | — | INCONCLUSIVO | Reescrever `Content` das mensagens afetadas em produção? Escopo fora do bugfix → Open Question na SPEC; decidir com o usuário. |

## SPECs Draft gerados (`.specs/`)

| SPEC | Tipo | Cobertura |
| --- | --- | --- |
| `SPEC-20261031-reasoning-tag-sanitization` | Bugfix | vazamento `<|channel>`/tags de raciocínio inline |
| `SPEC-20261031-sonar-vuln-ci-workflows` | Security | 4 vulns + 1 smell em workflows (hard rule — revisão humana) |
| `SPEC-20261031-sonar-vuln-code` | Security | 6 vulns + S7031 (Dockerfile merge RUN) |
| `SPEC-20261031-sonar-vuln-fp-triage` | Security | 3× json:S2068 pt-BR.json (FP provável) |
| `SPEC-20261031-sonar-smell-s3776` | Refactor | 106× complexidade cognitiva |
| `SPEC-20261031-sonar-smell-s3358` | Refactor | 104× ternário aninhado C# |
| `SPEC-20261031-sonar-smell-s1192` | Refactor | 97× literal duplicada |
| `SPEC-20261031-sonar-smell-s107` | Refactor | 53× >7 parâmetros |
| `SPEC-20261031-sonar-smell-csharp-misc` | Refactor | 254× demais regras C# |
| `SPEC-20261031-sonar-smell-js` | Refactor | 98× wwwroot/js + service-worker |
| `SPEC-20261031-sonar-smell-scripts-tools` | Refactor | 43× scripts/tools/ide-bundle |

Board completo (440 grupos, issue IDs + linhas): `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` · dados brutos: `.sonar_devin_auto_fix/sonarqube_issues.json` (3063 issues).

## Sumário

- Candidatos: 8 | Confirmados: 4 | Rejeitados: 1 | Duplicados: 2 | Inconclusivos: 1
- Issues Sonar acionáveis cobertas: **770/770** (757 smells + 13 vulns) em 10 SPECs; stale vendor via cleanup documentado.
- `.sonar_devin_auto_fix/` já consta em `.gitignore:36` — Phase 4 step 1 satisfeito.

## Pendências / gates

- [ ] Aprovação do usuário: SPECs → `Approved` + criação das Issues GitHub (epic + slices) via create-issues.
- [ ] Hard rule: SPEC vuln-ci-workflows exige revisão humana do diff em `.github/workflows/`.
- [ ] Soft rule: SPEC vuln-code toca `Dockerfile` — confirmação do usuário antes.
- [ ] `SONAR_TOKEN` indisponível localmente — won't-fix/bulk actions no SonarCloud requerem ação humana (UI).
- [ ] Decisão: data fix retroativo das mensagens corrompidas (INCONCLUSIVO acima).
