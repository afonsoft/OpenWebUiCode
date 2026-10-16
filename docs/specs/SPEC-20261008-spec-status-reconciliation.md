# SPEC-20261008-spec-status-reconciliation

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `spec-status-reconciliation` |
| Type | `Chore` (docs/verificação) |
| Stack | `Markdown / NUnit (verificação)` |
| Repository | `afonsoft/open-webui` |
| Branch | `chore/spec-reconciliation` |
| Ticket | `GAP-housekeeping-spec-status` |
| Status | `Completed` |
| Priority | `low` — hygiene; mantém `docs/specs/` como fonte de verdade |

## 1. User Story

Como maintainer, quero que o `Status` dos SPECs em `docs/specs/` reflita a realidade do código e das Issues, para que futuras rodadas de gap-analysis/orchestrator não re-avaliem trabalho já entregue como pendente (ou vice-versa).

## 2. Scope

**In scope:**

- Reconciliar RF-a-RF os 13 SPECs com status inconsistente:
  - 5 × `SPEC-20261003-*` com `Status: Approved` mas `## Delivered` preenchido e Issues #83/#86–#89 **CLOSED** → `Completed`.
  - 8 × `SPEC-20261007-*` (`Draft`/`In Progress`) com implementação evidenciada no código → verificar e marcar `Completed`, ou `Approved` com lista explícita de RFs residuais.
- Resíduo encontrado (RF parcial/ausente) vira nota no SPEC e, se relevante, candidato a novo gap.

**Out of scope:**

- Implementar RFs residuais (viraria SPEC próprio se confirmado).
- Reescrever requisitos; apenas reconciliar status/veredito.

## 3. Technical Context

**AS-IS (evidência coletada em 2026-10-08):**

| SPEC | Status atual | Evidência de entrega |
| --- | --- | --- |
| `SPEC-20261003-audio-engines` | `Approved` | `## Delivered` + Issue #83 CLOSED |
| `SPEC-20261003-i18n-locales` | `Approved` | `## Delivered` + Issue #88 CLOSED; 8 locales com paridade 707 chaves |
| `SPEC-20261003-model-filters` | `Approved` | `## Delivered` + Issue #87 CLOSED |
| `SPEC-20261003-permissions-granular` | `Approved` | `## Delivered` + Issue #86 CLOSED; `PermissionsGranularTests.cs` |
| `SPEC-20261003-utils-community` | `Approved` | `## Delivered` + Issue #89 CLOSED; `shareCommunity` em `app.js` |
| `SPEC-20261007-chat-agent-parity` | `Draft` | RF-014 pause/resume: `ChatRunPauses.cs`, botões `chat.pause/resume` em `ChatView`; RF-018 git bar: `ChatWorkspacePanel.razor:76-90` (comentário cita RF-018); RF-010/011/016/017: `TodoWrite`, `File`, `DelegateTask`, `BrowserScreenshot` builtin tools |
| `SPEC-20261007-chat-agent-tools` | `Draft` | `src/OpenWebUI.Infrastructure/ChatTools/Tools/`: 13 tools incl. `GenerateImage`, `CodeInterpreter`, `ShellExec`, `JobBuiltinTools`, `FetchUrl`, `WebSearch` |
| `SPEC-20261007-chat-agent-ux` | `In Progress` | `ToolArgsPreview.cs`, cards de tool, aprovações — verificar RF-a-RF |
| `SPEC-20261007-chat-detached-runs` | `Draft` | `ChatRunEndpoints.cs`, `ChatRunDispatcher.cs`, `ChatRunApprovals.cs`, `ChatRunPauses.cs` |
| `SPEC-20261007-chat-notifications` | `Draft` | `SignalRChatRunNotifier` (comentário cita RF-002), `WebPushChatRunNotifier`, `ChatHub`, `openwebui.notify` |
| `SPEC-20261007-chat-tool-streaming` | `Draft` | SSE `ChatToolCallEvent`/`ChatToolResultEvent`, `ToolCallCard.razor`, resume loop em `ChatView.razor:1580` (cita o SPEC) |
| `SPEC-20261007-mcp-ux-chat-polish` | `Draft` | `admin.mcp_test` + cards MCP em `Admin.razor` — verificar RF-a-RF |
| `SPEC-20261007-theme-layout-refresh` | `Draft` | default light em `index.html:19`, `data-theme`, `ThemeService`, toggle no `SettingsModal`/`Sidebar` — verificar se RF-T2 (toggle no header) foi atendido no espírito |

**TO-BE:** `Status` correto por SPEC + veredito por RF registrado; residuais (se houver) enumerados.

## 4. Requirements

### RF-001: Veredito RF-a-RF

Para cada um dos 13 SPECs, produzir tabela RF × veredito (`delivered`/`partial`/`missing`) com evidência (arquivo:linha ou teste). Registrar a tabela na seção `## Delivered` do próprio SPEC (ou seção `## Reconciliation` nos que já têm Delivered).

### RF-002: Corrigir `Status`

`Completed` quando todos os RFs entregues; `Approved` mantido apenas quando houver RF residual (com a lista explícita). Nunca rebaixar `Completed` sem evidência.

### RF-003: Residuais → candidatos

RFs `partial`/`missing` viram lista no relatório de entrega desta slice; cada um é candidato a SPEC novo (ex.: se o toggle de tema no header não existir, decidir se o SPEC aceita a alternativa implementada ou se abre gap).

### RF-004: Consistência com Issues

Confirmar que as Issues referenciadas nos `Ticket` estão fechadas quando o SPEC ficar `Completed`; se uma Issue aberta corresponder a RF residual, linkar no SPEC.

## 5. API Contract

N/A.

## 6. Critérios de Aceite

- [ ] Tabela de veredito RF-a-RF presente nos 13 SPECs.
- [ ] `Status` consistente: zero SPECs `Approved`/`Draft`/`In Progress` com entrega completa evidenciada.
- [ ] Lista de residuais (ou declaração "nenhum") no corpo do PR.
- [ ] `dotnet test` segue verde (docs-only change não pode quebrar testes; validar por precaução se qualquer `.razor`/`.cs` for citado incorretamente).

## 7. Notas

- Mudança essencialmente documental — baixo risco; serve de trilha para o próximo gap-analysis não re-avaliar estes SPECs.
- A evidência já coletada (tabela da seção 3) cobre a maior parte — o trabalho é confirmar os pontos marcados "verificar RF-a-RF" e redigir as tabelas.

## Delivered

- **PR:** #205 · **Issue:** #203 — mergeado 2026-10-08.
- 13 SPECs reconciliados RF-a-RF com `## Reconciliation` (veredito + evidência arquivo:linha) → `Completed`.
