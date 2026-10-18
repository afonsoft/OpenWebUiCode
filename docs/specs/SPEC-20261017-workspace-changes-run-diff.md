# SPEC-20261017-workspace-changes-run-diff

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `workspace-changes-run-diff` |
| Type | `Bugfix` |
| Stack | ASP.NET Core Minimal APIs + git CLI; Blazor WASM (panel) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `fix/agent-20261017-workspace-changes-run-diff` |
| Status | `Completed` |
| Ticket | [#444](https://github.com/afonsoft/open-webui/issues/444) |
| Origem | bug reportado 2026-10-17 — aba Changes do workspace mostra só o arquivo novo (spec) após a run; alterações commitadas pelo agente não aparecem |

## 1. User Story

**As a** usuário acompanhando uma run do agente
**I want** que a aba Changes mostre TODAS as mudanças da run — commitadas e não commitadas — com status correto (M/A/D)
**So that** o painel reflita o trabalho real feito no worktree, não apenas sujeira não-commitada.

**Problem context:** `WorkspaceGitService.GetInfoAsync` calcula a lista e o patch com `git diff HEAD` (`WorkspaceGitService.cs:62-64,85`) e marca untracked via `??` do porcelain. Quando o agente commita dentro do worktree (fluxo normal — ex.: sessão analisada, commit `05217540` com 3 arquivos), `diff HEAD` zera: **o trabalho commitado desaparece do painel**, restando só untracked posteriores — exatamente o sintoma reportado ("só mostra o arquivo novo do spec"). Além disso o status é sempre `M`/`A` — deleções e renames saem rotulados errado.

## 2. Scope

**In scope:**
- Base de diff por run: merge-base com a branch de origem (`origin/{binding.Branch}`) — cobre commits do agente + working tree + untracked.
- Status real por arquivo (`M/A/D/R`) via `--name-status`; badges na UI para todos (hoje só `new` para `A`).
- Untracked incluídos no patch expandido (hoje aparecem na lista mas sem diff — `chat.panel_no_diff`).
- Header da aba: mostrar base do diff (`{branch} ← {baseRef curto}`) além de +a/−d agregados.
- Contrato `WorkspaceGitResponse` ganha `BaseRef`/`BaseSha` curtos (retrocompatível — campos novos opcionais).

**Out of scope:**
- Diff de merges octopus/multi-base; repos sem remote seguem fallback.
- Paginação do diff (cap atual 96KB permanece).
- Regravação de histórico do checkpoint — checkpoint continua existindo para revert; esta SPEC só adiciona base de *exibição*.

## 3. Technical Context

- `src/OpenWebUI.Infrastructure/Services/WorkspaceGitService.cs` — `GetInfoAsync` (42-74), `CollectChangedFilesAsync` (80-118): `diff HEAD --numstat` + `status --porcelain` `??`.
- `src/OpenWebUI.Api/Endpoints/ChatRunEndpoints.cs` — `GetRunDiffAsync` (732-767): resolve workdir (worktree isolado > binding por chat/global) e devolve `WorkspaceGitResponse`.
- `src/OpenWebUI.Application/Contracts/ChatRunContracts.cs` — `WorkspaceGitFileResponse(Path,Added,Removed,Status)`, `WorkspaceGitResponse` (280-299).
- `src/OpenWebUI.Client/Components/ChatWorkspacePanel.razor` — aba `changes` (79-227): branch chip, `git.Files`, badge `new` só para `A`, diff por arquivo via `GitFileDiff`.
- Padrão já existente: `WorktreeService.DiffAsync` (287-296) usa `git add --intent-to-add --all` + `diff HEAD` + `reset` para o patch cobrir untracked — reutilizável com a base nova.
- `WorkspaceRepoService.ResolveBindingAsync` — binding por chat/global fornece `binding.Branch` para computar `origin/{branch}`.

## 4. Requirements

- **RF-001 — Base de exibição:** `GetInfoAsync` aceita `baseRef` opcional. `GetRunDiffAsync` resolve: `merge-base HEAD origin/{binding.Branch}` quando há binding+ref remota; senão `origin/{branch-atual}`; senão `HEAD` (comportamento atual). Commits do agente passam a aparecer no numstat/patch.
- **RF-002 — Status real:** `CollectChangedFilesAsync` usa `git diff {base} --name-status --no-renames` (ou `-M` para detectar `R`) + porcelain completo (não só `??` — `A `, ` D`, ` M` do index/worktree quando cobertos pela base) → `Status` ∈ `M|A|D|R`. Deleções exibem `removed` correto e badge.
- **RF-003 — Untracked no patch:** aplicar padrão intent-to-add (add → diff base → reset) para o unified diff cobrir arquivos novos; guarda: nunca deixar o index sujo se a tool falhar no meio (try/finally `reset`).
- **RF-004 — Contrato:** `WorkspaceGitResponse` acrescenta `string? BaseRef`, `string? BaseSha` (opcionais, default null). `WorkspaceGitFileResponse.Status` passa a aceitar `D`/`R` além de `M`/`A`.
- **RF-005 — UI:** badges por status — `M` (azul), `A`/`new` (verde), `D` (vermelho), `R` (roxo com `old → new` no title). Header exibe `@{baseRef}` curto. Chaves i18n novas nas 8 locales + `check-i18n-parity.py`.
- **RF-006 — Não-repo/sem remote:** comportamento atual preservado (lista `Changes` agregada das tools `file_*`).

## 5. API Contract

`GET /api/v1/chats/{id}/runs/{runId}/diff` (existente) — resposta ampliada:

```json
{
  "git": true, "branch": "chat/01J…", "worktree": true,
  "baseRef": "origin/main", "baseSha": "a1b2c3d",
  "added": 123, "removed": 45,
  "files": [{ "path": "src/X.cs", "added": 10, "removed": 2, "status": "M" }],
  "diff": "…", "diffTruncated": false
}
```

Erros inalterados (401/404 existentes).

## 6. Acceptance Criteria

- **Cenário 1 (o bug):** dado worktree onde o agente commitou 3 arquivos e criou 1 untracked, quando abro Changes, então vejo os **4** arquivos (3 `M`/`A` commitados + 1 `A` untracked) e o diff de cada um.
- **Cenário 2:** dado arquivo deletado pelo agente, então aparece com badge `D` e `removed>0`.
- **Cenário 3:** sem binding/remote (repo local), cai no diff vs `HEAD` atual — sem regressão.
- **Cenário 4:** falha no meio do intent-to-add → `finally reset` deixa index como estava (testado).

| Edge case | Entrada | Esperado |
| --- | --- | --- |
| Repo sem `origin` | binding local | base = `HEAD`, comportamento atual |
| `merge-base` inexistente | históricos disjuntos | base = `HEAD` |
| Index pré-existente sujo | staged antes da chamada | intent-to-add preserva/ restaura index (reset) |
| Binário modificado | `added/removed` = `-` | numstat 0/0, badge `M`, diff "Binary files differ" |

## 7. Task Plan

- [x] T1 — teste NUnit reproduzindo o bug: worktree com commit → `GetInfoAsync` deve listar os arquivos commitados (RED).
- [x] T2 — `GetInfoAsync(workdir, baseRef)` + resolução de base no endpoint (merge-base → origin → HEAD).
- [x] T3 — `--name-status` para status M/A/D/R + intent-to-add no patch com try/finally.
- [x] T4 — contrato (`BaseRef`/`BaseSha`), panel com badges/status + i18n 8 locales.
- [x] T5 — `dotnet test`, `dotnet build -c Release`, drift guards, PR.

## 8. Organization Guardrails

- Zero escrita no repo do usuário além de index intent-to-add restaurado (padrão já usado por `WorktreeService`).
- Não alterar formato de checkpoint/`CheckpointService` — a base é só de exibição.
- Contrato retrocompatível: campos novos opcionais; cliente antigo ignora.

## 9. Definition of Done

- [x] Changes mostra mudanças commitadas + não commitadas + untracked com status correto.
- [x] Teste de regressão do bug verde; suíte completa verde.
- [x] i18n/CSS guards verdes; PR aberto.

## 10. Open Questions

- Guardar `BaseSha` da run em `ChatRun.MetaJson` no start (base imutável mesmo se `origin` avançar) ou computar merge-base a cada chamada (base "viva")? ➡️ recomendado: persistir no start se já houver hook de início de run; senão merge-base dinâmico — mais simples e suficiente.
- Diff baseado no primeiro checkpoint (turn 0) como alternativa quando não houver remote? ➡️ recomendado: não nesta SPEC — `HEAD` basta como fallback.

## Delivered

| Item | Link |
| --- | --- |
| Issue | https://github.com/afonsoft/open-webui/issues/444 |
| PR/commit | #447 · 72e1bcabe |
