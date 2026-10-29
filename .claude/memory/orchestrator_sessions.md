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
