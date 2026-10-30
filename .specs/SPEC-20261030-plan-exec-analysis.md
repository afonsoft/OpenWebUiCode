# SPEC-20261030-plan-exec-analysis

## 0. Metadata

| Field | Value |
|-------|-------|
| Feature | Modo plan com execução analítica e escrita restrita a SPECs |
| Type | Feature |
| Stack | .NET 10 / ASP.NET Core / Blazor WASM |
| Repository | afonsoft/OpenWebUiCode |
| Branch | fix/devin-20261030-plan-mode-exec-and-stream-resilience |
| Status | In implementation |

## 1. User Story

As a usuário do agente, I want o modo plan capaz de rodar builds, testes e comandos git (clone/status/diff/log/commit) e de gravar SPECs em markdown, So that a análise para o plano seja detalhada e o plano/SPEC fique persistido — sem abrir edição de código-fonte antes da aprovação.

## 2. Scope

**In scope**

- `PermissionRuleset`: plan anuncia `shell_exec`/`code_interpreter` (decisão Ask → gate do preset), escritores restritos por path (`file_write`, `file_edit`, `apply_patch` → só `*.md` ou `.openwebui/`, `.specs/`, `docs/specs/`, `plans/`) e leitura estrutural livre (`worktree_diff`, `specs`, LSP).
- Prompt do modo plan (`AgentModeContributor`, `PlanSpecTemplate`) descrevendo o novo escopo.
- Aprovação do `plan_exit` promovendo plan → build (já existia — comportamento mantido).
- Correções adjacentes descobertas nos logs de produção: hardening do parse de providers (`Prop`/`Str` nos indexers crus remanescentes) e fire-and-forget não observados no `ChatView`.

**Out of scope**

- Execução livre sem preset em plan (o gate do preset continua valendo por call).
- Escrita de código-fonte ou paths fora do escopo de planejamento em plan.
- Tools externas (HTTP/MCP/python) em plan — seguem negadas.

## 3. Technical Context

- `src/OpenWebUI.Infrastructure/ChatTools/PermissionRuleset.cs` — `PlanAllowed`, `PlanExecGated`, `PlanSpecWriters`, `PlanWritableDirs`, `PlanWriteDecision`, `PlanWritePaths`, `IsPlanWritablePath`.
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` — `Evaluate(mode, tool, call.ArgumentsJson)` recebe os args reais por call; `Evaluate(mode, tool)` com args nulos alimenta `ShouldAnnounce` (args ilegíveis → Ask → anunciada e decidida na call).
- `src/OpenWebUI.Api/Completions/Contributors/SystemPromptContributors.cs` — bloco do modo plan.
- `src/OpenWebUI.Infrastructure/ChatTools/PlanSpecTemplate.cs` — instrução de análise.
- Adjacentes: `ProviderService.cs` (indexers crus → `Prop`/`Str`/`is JsonObject`), `ChatView.razor` (`PatchChatAsync` awaitado, catches transient, `GuardedAsync`, `GetRunAsync` tolerante no reattach).

## 4. Requirements

- **RF-001** — Em plan, `shell_exec` e `code_interpreter` são anunciados e devolvem `Ask` no ruleset: o gate normal do preset aprova/nega por call (build, testes, `git clone/commit`).
- **RF-002** — `file_write`, `file_edit` e `apply_patch` em plan: todos os paths tocados devem ser `*.md` ou estar sob `.openwebui/`, `.specs/`, `docs/specs/` ou `plans/` → `Allow`; path fora → `Deny` estruturado; args sem path legível → `Ask`.
- **RF-003** — `worktree_diff`, `specs` e as tools LSP rodam livres em plan (`Allow`); `job_kill`, `worktree_merge`, `delegate_task`, `n8n_trigger`, `generate_*` e tools não-builtin seguem `Deny`.
- **RF-004** — Paths absolutos, com `..` ou drive (`C:`) nunca são considerados writable no plan.
- **RF-005** — Aprovação do `plan_exit` continua promovendo o chat a build (sem repo vinculado → chat), com evento `mode` publicado.
- **RF-006** — `PatchChatAsync` de preset/modo é awaitado antes da run começar; falhas transitórias dos fire-and-forget (`GenerateTitle`, `LoadFollowUps`, `Stop/Pause/ResumeRun`, `Storage.RemoveAsync`) são observadas localmente, não no handler global.
- **RF-007** — Parse de respostas de provider não derruba a run em JSON fora do shape esperado (linha escalar, campo String onde se espera Object) nos caminhos Ollama NDJSON bridge, `FilterOpenAiDeltaLine`, completions buffered, `ParseAnthropicResponse` e model-list fetchers.

## 5. Acceptance Criteria

- Given um chat em modo plan, When o modelo chama `shell_exec` com `dotnet test`, Then a call chega ao gate do preset (pergunta ou auto-aprova conforme o preset).
- Given plan, When `file_write` em `.specs/SPEC-x.md`, Then `Allow`; When em `src/App.cs`, Then erro "bloqueada no modo plan".
- Given plan, When a SPEC é aprovada via `plan_exit`, Then `chat.Mode` vira `build` na mesma run.
- Given queda de rede durante o patch de preset/modo, Then a falha é logada e não vira exceção não observada.
- Given chunk SSE escalar (`data: "keepalive"`) no bridge Ollama, Then a linha é ignorada e a run segue.

## 6. Task Plan

- T1: `PermissionRuleset` — novos conjuntos + decisão por path.
- T2: prompts do modo plan + mensagem de deny do gate.
- T3: testes do ruleset (matriz + paths) e ajuste do E2E.
- T4: hardening do `ProviderService` remanescente.
- T5: `ChatView` — fire-and-forget observados + race do patch.
- T6: build + testes + esta SPEC em `In implementation`.

## 7. Definition of Done

- `dotnet build` verde; `dotnet test` com os novos casos passando.
- Sem regressão nos fluxos plan→build e nas denials estruturadas.
- SPEC arquivada em `docs/specs/` ao completar.

## Open Questions

- `specs transition` em plan faz `git mv` para `docs/specs/` — hoje segue o preset (mutável). Se ficar ruidoso, separar ops read das de transição numa próxima iteração.
