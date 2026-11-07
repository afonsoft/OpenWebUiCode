# orchestrator_sessions

## Session — 2026-10-01 21:20

**Scope**: Bootstrap do harness + instalação do catálogo afonsoft/skills no repo afonsoft/OpenWebUiCode (.NET 10 / Blazor WASM).
**Decisions**: catálogo instalado versionado em `.devin/skills/` (repo-level, não user-level); harness em `.claude/` compartilhado Claude Code + Devin CLI via `read_config_from`; sem `AGENTS.md` (Devin lê `CLAUDE.md` nativamente).
**Delivered**: PR #10 (catálogo 24 skills), PR #11 (CLAUDE.md, .claude/ completo, .devin/config.json, docs/, .specs/, orchestrator_stats.md) — ambos mergeados.
**Remaining**: nenhuma fila — 0 issues abertas em afonsoft/OpenWebUiCode, nenhum SPEC; pendências de paridade documentadas em docs/MIGRACAO-DOTNET.md (RAG, OAuth/LDAP, voice, realtime, i18n...) aguardam decisão do usuário para virar SPECs/Issues.
**Lessons**: `gh` dentro do clone resolve para `open-webui/open-webui` (remote `upstream` vence) — usar sempre `--repo afonsoft/OpenWebUiCode`.

## Session — 2026-10-15 12:35

**Scope**: Orquestração pós-entrega — reconciliação de issues, sonar-remediation (#377), warnings Windows, plan-spec-sdd em curso.
**Decisions**: 9 issues implementadas fechadas com label `done` (#349, #350, #352, #354, #363–#366, #377); 12 SPECs promovidas a `Done` (batch 20261013/20261014 + sonar); dependabot bumps #373–#375 mergeados exigiram resolução de conflito nos csproj do #378 (ficou a versão 10.0.13 de ambos os lados); testes POSIX-only passaram a `Assert.Ignore` em Windows (CA1416 → 0 warnings).
**Delivered**: PR #378 merged (sonar: segurança + confiabilidade), PR #379 aberto (warnings/Windows), branch `feature/Devin-20261015-plan-spec-sdd` criada para o plano SPEC SDD (stash@{0} guarda o trabalho parcial).
**Remaining**: mergear #379; concluir plan-spec-sdd (reaplicar stash, suite, PR); redeploy quando o usuário pedir.
**Lessons**: `File.SetUnixFileMode` quebra em host Windows — guardar com `OperatingSystem.IsWindows()` + return após `Assert.Ignore` silencia o CA1416; dependabot pode conflitar com bumps manuais em csproj — resolver tomando o lado com o superset de versões.

## Session — 2026-11-07 17:45

**Scope**: Diagnóstico produção (chat stalls) + confiabilidade de runs + OmniRoute masked-200.
**Decisions**: erros de run passam a ser exibidos na UI (evento SSE + polling fallback) e persistidos como mensagem `⚠️` do assistente; prosa com comandos shell não conta como tool call (false-success guard); OmniRoute falso-200 corrigido via quality-gate estrito gated em completion_tokens==0.
**Delivered**: commits `a67a796` (stream resilience/DSML/model selector), `1baf888` (false-success guard), `8abccdc` (erro visível) — todos em `main`, push ok, container redeployado healthy. OmniRoute: issue cheaper-inference/OmniRoute#19278 + PR #19281 aberto no fork afonsoft/OmniRoute.
**Remaining**: Issue #679 (SPEC-20261103-gemini-interactions-api Approved, não implementada — única SPEC viva); PR #19281 aguarda review upstream; OmniRoute produção 3.8.53 → upgrade pendente; pool OmniRoute esgotado (só auto/gemini saudável); 22 mensagens poluídas com texto "enough credits" no DB; stash omniroute `wip: delecoes bin/`; branches merged locais podem ser apagadas.
**Lessons**: mudanças em working tree NÃO vão pro deploy — commitar antes de rebuildar; run failed sem erro visível = "Pensando" eterno pro usuário; call_logs do OmniRoute provam roteamento real — diagnosticar provider pool antes de culpar a app.

**DB sanitization (2026-11-07)**: 21 mensagens poluídas com resposta falsa do Pollinations saneadas — 17 puras (15 assistant + 2 compaction) substituídas por marcador `> ⚠️ Falha do provedor upstream...`; 4 embutidas truncadas no ponto do texto falso + marcador `[trecho removido]`. Backup em `/home/ubuntu/backups/openwebui-db-20261107/` (db+wal+shm, 255MB). 0 ocorrências restantes de "enough credits".
