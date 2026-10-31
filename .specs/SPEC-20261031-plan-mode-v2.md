# SPEC-20261031-plan-mode-v2

## 0. Metadata

| Field | Value |
|-------|-------|
| Feature | Plan mode v2 — `write_plan` + diretório de planos + workflow de 5 fases |
| Type | Feature |
| Stack | .NET 10 / ASP.NET Core |
| Repository | afonsoft/OpenWebUiCode |
| Branch | feature/devin-20261031-plan-mode-v2 |
| Ticket | [A DEFINIR] |
| Status | Approved |

## 1. User Story

**As a** usuário do agente
**I want** um modo plan que produza um arquivo de plano persistido num diretório dedicado, seguindo um workflow estruturado de descoberta→design→verificação
**So that** planos fiquem rastreáveis, revisáveis e promovíveis a build com um único gate — sem edição de código antes da aprovação.

**Problem context:**
O Devin CLI tem (`docs/DEVIN-CLI-REA-ANALYSIS.md` §3, `toolbox/write_plan.rs` + `exit_plan_mode.rs`) um plan mode com **`write_plan`** — tool que grava o plano num arquivo dedicado em `~/.devin/plans/` — e um **workflow de 5 fases** no system prompt: Understand → Design → Verification Planning → Present Plan (template `## Summary / Implementation Steps / Files to Modify / Verification / Risks`) → `exit_plan_mode`. O OpenWebUiCode já tem plan mode (`PermissionRuleset.PlanAllowed`, `PlanExitBuiltinTool`, `PlanSpecTemplate`, gate de execução analítica de SPEC-20261030) — mas o plano vive só na conversa/mensagem: não há tool `write_plan` que persista num diretório de planos, nem as fases explícitas do workflow.

## 2. Scope

**In scope:**

- Nova builtin tool `write_plan` — grava o plano atual num arquivo `{plansDir}/YYYY-MM-DD-<slug>.md` (ou sobrescreve o arquivo da sessão); retorna o path.
- Diretório de planos por workspace: `<repo>/plans/` (e fallback `~/.openwebui/plans/` sem repo); arquivos só `.md`.
- Workflow de 5 fases no prompt do modo plan: **1) Understand** (perguntas, leitura, gather facts), **2) Design** (abordagem, alternativas, tradeoffs), **3) Verification Planning** (como testar/verificar cada passo), **4) Present Plan** (template fixo), **5) Exit** (`plan_exit` após aprovação implícita/explícita).
- Template do plano: `## Summary`, `## Implementation Steps`, `## Files to Modify`, `## Verification`, `## Risks` (+ `## Open Questions` opcional).
- `write_plan` é a única tool de escrita permitida em plan (além do já existente `*.md`/`.specs/` etc.); `plan_exit` continua promovendo plan→build e referencia o arquivo do plano.
- Endpoint `GET /api/v1/workspace/repo/plans` lista planos do diretório (para o painel do usuário).
- O arquivo do plano é mencionado no resultado do `plan_exit` e linkado na transição para build.

**Out of scope:**

- Versionamento/histórico do plano (overwrite simples; git cuida).
- Renderização especial do plano no frontend (markdown padrão basta).
- Migração de planos antigos (não existem).

## 3. Technical Context

**Where the change happens:**

`src/OpenWebUI.Infrastructure/ChatTools/Tools/` (novo `WritePlanBuiltinTool`), `src/OpenWebUI.Infrastructure/ChatTools/PermissionRuleset.cs` (adicionar `write_plan` a `PlanAllowed`), `src/OpenWebUI.Api/Completions/Contributors/SystemPromptContributors.cs` (workflow de 5 fases), `src/OpenWebUI.Infrastructure/ChatTools/PlanSpecTemplate.cs` (template do plano), `src/OpenWebUI.Api/Endpoints/` (listagem de planos), `PlanExitBuiltinTool` (referência ao arquivo).

**Files to read before implementing:**

- `src/OpenWebUI.Infrastructure/ChatTools/PermissionRuleset.cs` (`PlanAllowed`, `PlanWritePaths`, `IsPlanWritablePath`)
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/PlanExitBuiltinTool.cs`
- `src/OpenWebUI.Infrastructure/ChatTools/PlanSpecTemplate.cs`
- `src/OpenWebUI.Api/Completions/Contributors/SystemPromptContributors.cs` (bloco plan)
- `docs/DEVIN-CLI-REA-ANALYSIS.md` §3
- `.specs/SPEC-20261030-plan-exec-analysis.md`

**Files to create or modify:**

```text
src/OpenWebUI.Infrastructure/ChatTools/Tools/WritePlanBuiltinTool.cs    (novo)
src/OpenWebUI.Infrastructure/ChatTools/PermissionRuleset.cs             (write_plan em PlanAllowed)
src/OpenWebUI.Infrastructure/ChatTools/PlanSpecTemplate.cs              (template do plano)
src/OpenWebUI.Api/Completions/Contributors/SystemPromptContributors.cs  (5 fases)
src/OpenWebUI.Api/Endpoints/PlanEndpoints.cs                            (novo — listar)
src/OpenWebUI.Infrastructure/ChatTools/Tools/PlanExitBuiltinTool.cs     (link do plano)
tests/OpenWebUI.Api.Tests/WritePlanTests.cs                             (novo)
```

## 4. Requirements

### RF-001: `write_plan` grava plano em arquivo
- **Description:** `write_plan` recebe o conteúdo do plano (markdown) e grava em `{plansDir}/YYYY-MM-DD-<slug>.md`; chamadas subsequentes na mesma sessão atualizam o mesmo arquivo (slug derivado da primeira linha/título).
- **Rules:** path só dentro de `{plansDir}` (`<repo>/plans/` ou `~/.openwebui/plans/`); somente `.md`; cria o diretório se não existir; retorna `{path, bytes}`.

### RF-002: Workflow de 5 fases no prompt
- **Description:** O bloco do modo plan instrui as fases **Understand → Design → Verification Planning → Present Plan → Exit**, cada uma com saída esperada.
- **Rules:** Understand pode fazer perguntas ao usuário (`ask_user_question` equivalente) antes de escrever; Present Plan usa o template fixo; `plan_exit` só depois do plano apresentado.

### RF-003: Template fixo do plano
- **Description:** `PlanSpecTemplate` ganha a forma do plano: `## Summary`, `## Implementation Steps` (ordenados, pequenos), `## Files to Modify`, `## Verification` (como testar cada passo), `## Risks`, `## Open Questions`.
- **Rules:** `write_plan` valida presença das seções obrigatórias e avisa se faltar (não bloqueia); o template vai no prompt.

### RF-004: `plan_exit` referencia o arquivo
- **Description:** Ao promover plan→build, `plan_exit` inclui o path do plano (se existir) na confirmação e o anexa como contexto da run de build.
- **Rules:** sem arquivo → `plan_exit` funciona como hoje (plano só na conversa); com arquivo → o conteúdo é injetado como referência.

### RF-005: Listagem de planos
- **Description:** `GET /api/v1/workspace/repo/plans` lista `{name, path, modifiedAt, title}` dos `.md` do diretório de planos.
- **Rules:** ordenado por `modifiedAt` desc; só workspace com repo vinculado; auth do usuário.

### RF-006: Restrições de escrita mantidas
- **Description:** Em plan, `write_plan` é a tool preferida de escrita; `file_write`/`file_edit`/`apply_patch` continuam restritos a `.md`/`.specs/`/`docs/specs/`/`plans/` (SPEC-20261030).
- **Rules:** `write_plan` é a forma "oficial" (não exige aprovação — escreve só no plansDir); o resto segue o gate existente.

## 5. API Contract

**Endpoint:** `GET /api/v1/workspace/repo/plans`
**Auth:** JWT do usuário com workspace vinculado.
**Response:** `[{ "name": "2026-10-31-x.md", "path": "plans/2026-10-31-x.md", "title": "...", "modifiedAt": "..." }]`

## 6. Acceptance Criteria

- [ ] **Given** modo plan, **when** o modelo chama `write_plan` com o plano, **then** um arquivo `.md` é criado em `plans/` e o path é retornado.
- [ ] **Given** segunda chamada de `write_plan` na mesma sessão, **when** executada, **then** o mesmo arquivo é atualizado.
- [ ] **Given** `write_plan` fora de plan, **when** chamada, **then** funciona também (planos podem ser escritos em code mode como artefato).
- [ ] **Given** `plan_exit` com plano em arquivo, **when** aprovado, **then** o path do plano aparece na confirmação e segue como contexto do build.
- [ ] **Given** `GET /api/v1/workspace/repo/plans`, **when** chamado, **then** lista os `.md` do diretório ordenados.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Sem repo vinculado | plan sem workspace | `~/.openwebui/plans/` fallback |
| Plano sem seções do template | markdown incompleto | grava + warning das seções faltantes |
| `write_plan` com path absoluto no conteúdo | texto qualquer | slug sanitizado; path sempre no plansDir |
| Concorrência de sessões | 2 plans mesma data | slugs distintos por sufixo |

## 7. Task Plan

- [ ] **T1 — Discovery:** ler `PermissionRuleset`/`PlanExit`/`PlanSpecTemplate`/contributors.
- [ ] **T2 — `write_plan`:** `WritePlanBuiltinTool` (slug, plansDir, persistência por sessão).
- [ ] **T3 — Template+prompt:** `PlanSpecTemplate` com 5 fases + seções; contributor do modo plan.
- [ ] **T4 — `plan_exit`:** referência ao arquivo + contexto pro build.
- [ ] **T5 — Endpoint:** `PlanEndpoints` GET list.
- [ ] **T6 — Testes:** unitários (slug, template validation, fallback dir) + integração (plan→exit→build com arquivo).
- [ ] **T7 — Done + PR.**

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **.NET** | Unit tests (write_plan, template, slug, fallback); integration tests (fluxo plan completo); 80% coverage. |

## 8. Organization Guardrails

- **Branches:** `feature/devin-20261031-plan-mode-v2`.
- **Security:** `write_plan` só escreve no plansDir (allowlist); path traversal sanitizado.
- **Scope:** sem versionamento de plano no MVP.
- **Architecture:** tool builtin; persistência filesystem.

## 9. Definition of Done

- [ ] RFs implementados; aceite coberto por testes.
- [ ] Edge cases tratados; `dotnet test` verde; `check-spec-status.py` ok.
- [ ] Guardrails respeitados; logs sem PII.

## Open Questions / Pending Ambiguity

- `write_plan` também disponível em code mode? (recomendo sim — planos como artefato).
- O plano deve aparecer como attachment/mensagem especial no frontend? (recomendo fase 2).
