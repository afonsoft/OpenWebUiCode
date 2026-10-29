# gap-analysis — 2026-10-01

## Fontes (inventário)

- `.specs/` PRESENT (13 SPECs gerados neste run) · `docs/` PRESENT · `docs/architecture/` ABSENT
- `.claude/` PRESENT (CONTEXT/MEMORY/memory/rules/agents) · `CLAUDE.md` PRESENT · `AGENTS.md` ABSENT (por decisão do harness)
- `gh auth` OK; remote `afonsoft/OpenWebUiCode` (nota: `gh` resolve `open-webui/open-webui` por default neste clone — usar `--repo`)
- Build manifests: 5 csproj + testes NUnit + Dockerfile + docker-compose + dotnet.yml

## Candidatos e veredictos

| Gap key | Veredicto | Evidência |
|---|---|---|
| GAP-implementation-rag-knowledge | CONFIRMADO | nenhum vector store/embedding em `src/`; doc `MIGRACAO-DOTNET.md:50,60-61` |
| GAP-implementation-realtime-channels | CONFIRMADO | sem SignalR/hub nem Channel; doc `:63,69` |
| GAP-security-auth-sso-rbac | CONFIRMADO | papéis apenas admin/user/pending; sem OAuth/LDAP; doc `:64-65` |
| GAP-implementation-voice | CONFIRMADO | sem STT/TTS; doc `:66` |
| GAP-implementation-image-generation | CONFIRMADO | sem image gen; doc `:67` |
| GAP-implementation-code-execution | CONFIRMADO | sem Pyodide/exec; doc `:68` |
| GAP-implementation-pwa-offline | CONFIRMADO | sem manifest/SW; doc `:70` |
| GAP-implementation-i18n | CONFIRMADO | strings pt-BR hardcoded; doc `:71` |
| GAP-observability-analytics | CONFIRMADO | sem agregações; doc `:72` |
| GAP-implementation-automations | CONFIRMADO | sem scheduler/calendário; doc `:73,89` |
| GAP-implementation-tools-functions | CONFIRMADO | sem tools/functions; doc `:62` |
| GAP-architecture-ef-migrations | CONFIRMADO | SchemaBootstrap ad-hoc; doc `:74` |
| GAP-requirements-missing-pages | CONFIRMADO | /playground e abas admin ausentes; doc `:85-89` |
| GAP-requirements-upload-rag-parcial | DUPLICADO | absorvido dentro do SPEC rag-knowledge |
| GAP-operation-deploy-dedicado | REJEITADO | Dockerfile + compose existem; k8s/helm fora de escopo |

## SPECs (Approved) → Issues

| SPEC | Issue |
|---|---|
| rag-knowledge | #24 |
| realtime-channels | #25 |
| auth-sso-rbac | #16 |
| voice | #27 |
| image-generation | #21 |
| code-execution | #18 |
| pwa-offline | #23 |
| i18n | #20 |
| analytics | #15 |
| automations-calendar | #17 |
| tools-functions | #26 |
| ef-migrations | #19 |
| missing-pages | #22 |

Epic: #14 (`gap-analysis-20261001`, label `epic`+`todo`). PR dos SPECs: #13 (mergeado).

## Pendências

- Nenhuma fila de execução iniciada — slices aguardam `/execute-specs` em ordem de dependência (ef-migrations e auth-sso-rbac primeiro é sugestão: schema e auth tocam tudo).
- `docs/architecture/` ainda ausente — `/architecture` deve gerar diagramas ao fim do primeiro Epic.
