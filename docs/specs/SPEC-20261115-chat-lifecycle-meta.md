# SPEC-20261115-chat-lifecycle-meta

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-lifecycle-meta` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/Devin-20261115-chat-lifecycle-meta` |
| Status | `Completed` |

## 1. User Story

**As a** administrador e usuário do Open WebUI
**I want** que arquivar/excluir chats libere disco e banco (mantendo métricas e um resumo) e que o analytics mostre título correto e link apenas para chats navegáveis
**So that** a base fique enxuta, o "Top chats por custo" pare de exibir "Sem Título" com link morto, e desarquivar retome o trabalho com o resumo como contexto.

**Problem context:**
- `GET /api/v1/admin/usage` enriquece top chats via join em `Chats` — chats deletados voltam `Title=null` ("Sem Título") e o link `/c/{id}` dá 404.
- Delete já libera worktree/worktrees de run/checkpoints/PTYs (`ReleaseChatDiskAsync`), mas não registra nenhum rastro identificável do chat para o analytics.
- Archive só remove o worktree (`keepBinding:true`) e mantém mensagens + `ChatRuns`/`ChatRunEvents` completos — sem ganho de espaço no banco.
- Unarchive recria o worktree sob demanda (`EnsureChatWorktreeAsync`) mas o histórico volta íntegro (quando existe) — o usuário quer o resumo como contexto.

**Decisão aprovada:** resumo gerado por LLM (reusando `LlmContextSummarizer`/`IContextSummarizer` do compaction, SPEC-20261010) com fallback determinístico (trechos truncados das mensagens) quando o modelo falhar.

## 2. Scope

**In scope:**
- Nova entidade `ChatArchiveMeta` (chatId, userId, title, summary, status `archived|deleted`, timestamps) + migration EF.
- Serviço `ChatLifecycleService` (Infrastructure): gera resumo (LLM → fallback), upserta meta.
- Archive: gera meta (`archived`), apaga `ChatMessages`, `ChatRunEvents`, `ChatRunSteers`, `ChatRuns`, mata PTYs do chat, libera worktree (já existe). Mantém `RunUsageRecords` (métricas) e o `Chat` (arquivado).
- Delete: gera meta (`deleted`, reusando summary do archive quando houver), liberação de disco existente inalterada, `Chat` removido por cascade. `RunUsageRecords` permanecem.
- Unarchive: insere `ChatMessage` `Role="compaction"` com o resumo (mesmo marcador visual do compaction — renderiza na tela e chega ao LLM colapsado como `user` no provider).
- `GET /api/v1/admin/usage` top chats: `Title` resolvido de meta quando o chat não existir mais, `Status` (`active|archived|deleted`) e `Summary` presente.
- `GET /api/v1/admin/chats/{chatId}/lifecycle`: `{status, title, summary, metrics{runs,tokens,costUsd}}` para o popup.
- Client `Admin.razor` top chats: link `/c/{id}` só quando `active|archived`; `deleted` (ou chat inexistente) vira botão que abre popup com resumo + métricas. i18n nos 8 locales.
- Testes NUnit.

**Out of scope:**
- Re-sumario periódico, export/relatórios, retorno dos dados deletados (irreversível por decisão), alteração de `RunUsageRecord`, purge de bases antigas (meta só é criada a partir desta versão — chats já deletados continuam "Sem Título" + sem link).

## 3. Technical Context

**Files to read:** `ChatEndpoints.cs` (ToggleArchive/Delete/ReleaseChatDiskAsync), `UsageEndpoints.cs`, `RunUsageLedgerService.cs`, `ContextCompactionService.cs` (`IContextSummarizer`, `LlmContextSummarizer`, `ContextSummary`), `WorkspaceRepoService.ReleaseChatWorkspaceAsync`, `TerminalSessionManager.KillForChatAsync`, `AppDbContext.cs`, `Admin.razor` (~l. 422-450), `ApiService.cs` (`GetAdminUsageAsync`).

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities.cs                       (ChatArchiveMeta)
src/OpenWebUI.Infrastructure/Data/AppDbContext.cs      (DbSet + config)
src/OpenWebUI.Infrastructure/Migrations/*              (migration nova)
src/OpenWebUI.Infrastructure/Services/ChatLifecycleService.cs (novo)
src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs           (archive/delete/unarchive)
src/OpenWebUI.Api/Endpoints/UsageEndpoints.cs          (top chats + lifecycle endpoint)
src/OpenWebUI.Application/Contracts/UsageContracts.cs  (campos novos)
src/OpenWebUI.Client/Pages/Admin.razor                 (link condicional + popup)
src/OpenWebUI.Client/Services/ApiService.cs            (GetChatLifecycleAsync)
src/OpenWebUI.Client/wwwroot/i18n/*.json               (8 locales)
tests/OpenWebUI.Api.Tests/ChatLifecycleTests.cs        (novo)
tests/OpenWebUI.Api.Tests/AdminUsageTests.cs           (status/title)
```

## 4. Requirements

### RF-001: `ChatArchiveMeta` + `ChatLifecycleService`
- **Description:** Entidade `{Id, ChatId(unique), UserId, Title, Summary, Status("archived"|"deleted"), ArchivedAt, DeletedAt?, CreatedAt}` (epoch seconds). Serviço `SummarizeAsync(chat, ct)`: transcript "role: content" do branch ativo (cap ~32k chars), `IContextSummarizer.SummarizeAsync(model=chat.ModelsJson[0] ou tasks-role resolvido pelo caller)` → texto; falha/null → fallback determinístico: `1ª user + últimas 2 assistant/user` truncadas a 500 chars cada. `RecordMetaAsync(chat, status, summary)` faz upsert.
- **Rules:** summarizer nunca lança — qualquer falha degrada ao fallback; meta preserva o `Title` vigente no momento.

### RF-002: Archive com resumo e limpeza
- **Description:** `POST /chats/{id}/archive` (direção arquivar): kill PTYs do chat → resumo + meta `archived` → `ExecuteDelete` em `ChatRunEvents`/`ChatRunSteers`/`ChatRuns`/`ChatMessages` do chat → `ReleaseChatWorkspaceAsync(keepBinding:true)` (existente) → `Archived=true`.
- **Rules:** `RunUsageRecords` intocados; ordem: summary antes de apagar mensagens; ArchiveAll aplica o mesmo fluxo por chat.

### RF-003: Unarchive com contexto
- **Description:** Direção desarquivar: `Archived=false`; se existir meta com `Summary`, insere `ChatMessage{Role="compaction", Content=Summary, Position=0}` para exibir na tela e alimentar o contexto (providers colapsam `compaction`→`user`). Worktree já é recriado sob demanda por `EnsureChatWorktreeAsync` (sem mudança).

### RF-004: Delete com meta
- **Description:** `DELETE /chats/{id}`: se não houver meta, gera resumo + meta `deleted` (reusa `Summary`/`ArchivedAt` de meta archived existente, atualizando `Status`/`DeletedAt`); liberação de disco inalterada; `Chat` removido (cascade apaga resto).

### RF-005: Analytics top chats + popup
- **Description:** `AdminUsageTopChat` ganha `Status` e resolve `Title`: `Chats.Title` quando existe (com `Status` archived/active), senão `meta.Title`. Novo `GET /api/v1/admin/chats/{chatId}/lifecycle` retorna `{status, title, summary, runs, tokens, costUsd}` (agregados de `RunUsageRecords`; chat vivo → summary null). Client: `Status` deleted ou chatId sem match → linha sem link + botão "resumo" abrindo modal com título/status/resumo/métricas; `active|archived` → `<a href="/c/{id}">` como hoje.
- **Rules:** admin-only em ambos; chats deletados antes desta versão (sem meta) → `Status="deleted"`, `Title=null` → "Sem Título" sem link.

## 5. Acceptance Criteria

- [ ] Top chats mostra título real de chats arquivados e deletados (com meta).
- [ ] Chat deletado não tem link; clicar abre popup com resumo + runs/tokens/custo.
- [ ] Arquivar remove worktree, apaga mensagens/runs/events, mantém métricas — chat mostra o resumo.
- [ ] Desarquivar recria worktree ao usar e o resumo aparece no histórico como marcador compaction.
- [ ] Excluir apaga histórico e worktree; `GET /admin/usage` segue agregando custo do chat.
- [ ] `dotnet test` verde; `check-i18n-parity`, `check-css-classes`, `check-form-a11y`, `check-spec-status` passam.
