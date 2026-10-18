# SPEC-20261018-permissions-v2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `permissions-v2` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261018-permissions-v2` |
| Status | `Approved` |
| Parent | `docs/analise-opencode-v2.md` (P1.2); depends on SPEC-20261018-policies-admin |

## 1. User Story

**As a** usuário rodando agentes em repos locais
**I want** regras ordenadas allow/ask/deny por tool+arg, aprovações "sempre" que sobrevivem a restart e um boundary de diretório externo
**So that** eu controle finamente o que o agente pode fazer sem re-aprovar a cada reinício — paridade com opencode v2 `permissions` (actions: read/edit/glob/grep/shell/subagent/skill/question/webfetch/websearch/external_directory/MCP tools; saved approvals once/always/reject).

## 2. Scope

**In scope:**
- Regras de usuário ordenadas `{action, resource(glob), effect: allow|ask|deny}`, last-match-wins, avaliadas depois de admin policies (deny admin vence sempre) e antes do risk gate.
- Aprovação persistente: "Sempre" salva `(userId, workdir, tool, argPattern)` em tabela; "Uma vez" só na sessão atual; "Rejeitar" pode ser once/always (always = deny rule temporária, revogável).
- Boundary `external_directory`: default `ask` — acesso a paths fora do workspace corrente pede confirmação; configurável deny/allow por usuário e por repo-binding.
- UI: Settings → Permissions com lista de regras + lista de aprovações salvas (revogar individual).

**Out of scope:**
- Regras por worktree/session-id (v2 avalia por workdir normalizado do run). Compartilhamento de regras entre usuários (admin policies cobrem o global).

## 3. Technical Context

**Where the change happens:**
`ChatRunApprovals` (hoje `_remembered` in-memory) → persistir; `PermissionService` → avaliador ordenado único que também decide worktree/CLI-safety hoje; chat file tools checam `external_directory` antes de tocar FS fora do workdir.

**Files to read before implementing:**
- `src/OpenWebUI.Api/Runs/ChatRunApprovals.cs` — in-memory atual
- `src/OpenWebUI.Infrastructure/Services/PermissionService.cs` — camadas atuais
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/*.cs` — file/shell tools (workdir do contexto)
- `src/OpenWebUI.Domain/Entities.cs`

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/PermissionRuleEvaluator.cs # NOVO: ordered rules
src/OpenWebUI.Domain/Entities.cs                               # UserPermissionRule + SavedApproval
src/OpenWebUI.Api/Runs/ChatRunApprovals.cs                     # always → SavedApproval (DB)
src/OpenWebUI.Api/Endpoints/PermissionEndpoints.cs             # CRUD regras + approvals
src/OpenWebUI.Client/Pages/Settings/SettingsPermissions.razor  # NOVO
src/OpenWebUI.Infrastructure/ChatTools/Tools/FileBuiltinTools.cs # external_directory check
src/OpenWebUI.Infrastructure/ChatTools/Tools/ShellBuiltinTool.cs # idem
tests/OpenWebUI.Api.Tests/PermissionRuleEvaluatorTests.cs      # NOVO
tests/OpenWebUI.Api.Tests/ExternalDirectoryTests.cs            # NOVO
```

## 4. Requirements

### RF-001: Avaliador ordenado
- **Description:** `PermissionRuleEvaluator.Evaluate(action, toolName, argString, workdir)` → `Allow|Ask|Deny`. Ordem: (1) admin policy deny → Deny; (2) user rules last-match; (3) saved approval match → Allow; (4) persona/tool allowlist legacy; (5) risk gate → Ask; default Ask.
- **Rules:** `action` normaliza tool→action (`file_read`→read, `shell`→shell, `webfetch`→webfetch, `subagent`/`delegate_task`→subagent, MCP→`mcp:{server}`). Resource match: `tool` exato, `tool:argprefix*`, `*` global.

### RF-002: Aprovações duráveis
- **Description:** `SavedApproval { UserId, WorkdirHash, Tool, ArgPattern, Decision(Allow|Deny), CreatedAt }`. "Sempre" grava; "rejeitar sempre" grava Deny. Settings lista e revoga.
- **Rules:** ArgPattern salvo = o que a UI já mostra (`chat-cmd:*`); match por glob igual às rules. Sobrevive restart e vale em todos os chats do mesmo workdir.

### RF-003: external_directory
- **Description:** todo tool que toca path (file_read/write/edit, shell com `cd`, patch) resolve o path real; fora do workdir/bindings do run → aplica regra `external_directory` do usuário (default ask, prompt mostra o path).
- **Rules:** symlinks resolvidos via `Path.GetFullPath` + `realpath` quando possível; paths dentro de referências (SPEC references) contam como dentro.

## 5. Acceptance Criteria

- "Sempre" num shell call → sobrevive `dotnet` restart e auto-aprova no próximo chat do mesmo workdir.
- Regra `deny shell:git push*` impede o call mesmo com aprovação salva (deny vence).
- `file_read /etc/passwd` fora do workdir dispara ask; `allow external_directory` libera.
- Rules order testado: `allow shell:*` seguido de `deny shell:rm *` → `rm` bloqueado, resto livre.
- UI lista/revoga approvals; testes NUnit cobrem ordem, glob, persistência.
