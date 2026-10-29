# SPEC-20261002-plugin-ecosystem

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `plugin-ecosystem` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261002-plugin-ecosystem` |
| Ticket | Issue #62 |
| Status | `Completed` |

## 1. User Story

**As a** admin do Open WebUI
**I want** Functions (pipes/filters/valves), Skills e Pipelines para estender o chat sem rebuild
**So that** o ecossistema de plugins do upstream exista no .NET.

**Problem context:**
Upstream tem `functions.py` (17 eps — filter/pipe/action plugins em Python), `skills.py` (9 eps), `pipelines.py` (8 eps — servidor de pipelines). Nosso port tem só Tools HTTP (SPEC tools-functions). Em .NET, "código de usuário" executado server-side é sandbox pesado — decisão de escopo necessária.

## 2. Scope

**In scope (fase 1 — pragmatic):**
- **Skills** (entidade upstream nova): `Skill` = nome + conteúdo (markdown/instrução) anexável a modelos/chats — CRUD `/api/v1/skills`, páginas `/workspace/skills/*`, injeção no system prompt.
- **Functions como metadados**: entidade `Function` (id, name, type `filter|pipe|action`, manifest, `active`) com CRUD `/api/v1/functions` + `/admin/functions` e `/workspace/functions/*` — mas **execução delegada a Pipelines** (URLs externas), não execução de código C# arbitrário.
- **Pipelines**: `POST /api/v1/pipelines` registra servidor externo (URL + key); `inlet/outlet` chamam o servidor em volta do chat completion; `GET /api/v1/pipelines/list` descobre pipes do servidor e os expõe como modelos `pipeline:*`.
- Valves: manifest com `valves` schema → forms de config por usuário/admin (armazenar valores em config, sem executar).

**Out of scope (documentado):**
- Execução de Python do upstream — incompatível; o equivalente .NET seguro é delegar ao servidor de pipelines externo.
- Hot-reload de assemblies; Roslyn scripting (risco de segurança — decisão explícita de não executar código arbitrário).
- Integração openwebui.com community (SPEC separada se desejada).

## 3. Technical Context

**Where:** `Domain` (Skill, Function, Pipeline server), `Infrastructure` (PipelineClient HTTP), `Api` (`SkillEndpoints`, `FunctionEndpoints`, `PipelineEndpoints` + hook no completions), `Client` (páginas workspace/admin + selector de skill).

**Files to read:**
- `src/OpenWebUI.Api/Endpoints/{ToolEndpoints,ChatEndpoints}.cs` (tool loop, completions)
- `src/OpenWebUI.Domain/Entities/Tool.cs`

**Files to create/modify:**
```text
src/OpenWebUI.Domain/Entities/{Skill,Function,PipelineServer}.cs
src/OpenWebUI.Application/Contracts/{SkillContracts,FunctionContracts,PipelineContracts}.cs
src/OpenWebUI.Infrastructure/Services/PipelineClientService.cs
src/OpenWebUI.Api/Endpoints/{SkillEndpoints,FunctionEndpoints,PipelineEndpoints}.cs
src/OpenWebUI.Client/Pages/Admin.razor + Pages/Workspace.razor (abas)
tests/OpenWebUI.Api.Tests/PluginEcosystemTests.cs
```

## 4. Requirements

### RF-001: Skills
- **Description:** CRUD de skills (nome + conteúdo); anexável a modelo custom; conteúdo injetado no system prompt.
- **Input → Output:** `{name, content}` → skill

### RF-002: Functions registry
- **Description:** CRUD de functions com `type`, `active`, manifest; apenas registro/gestão (sem execução local).
- **Input → Output:** `{id,name,type,content,active}` → function

### RF-003: Pipelines
- **Description:** Registrar servidor pipeline (url+key mascarada); `/list` descobre pipes e expõe como modelos selecionáveis; completions roteia `pipeline:*` ao servidor com inlet/outlet.
- **Rules:** servidor fora → `502`; modelo inexistente → `404`.
- **Input → Output:** prompt → resposta do pipeline

### RF-004: Valves
- **Description:** Manifest `valves` (JSON schema) → valores por usuário salvos em config; enviados ao pipeline server nas chamadas.

**Invariants:** nenhum código arbitrário executa no servidor .NET; URLs/keys mascaradas.

## 5. API Contract

`GET|POST|DELETE /api/v1/skills[/{id}]` · idem `/api/v1/functions` · `POST /api/v1/functions/{id}/toggle` · `GET|POST|DELETE /api/v1/pipelines[/{id}]` · `GET /api/v1/pipelines/list`
**Auth:** Bearer; functions/pipelines admin

## 6. Critérios de Aceite

- [ ] Skill anexada a modelo altera o system prompt.
- [ ] Pipeline server mock: pipe aparece como modelo e completion roteia.
- [ ] Functions CRUD admin sem execução (documentado).
- [ ] Valves persistem por usuário.
- [ ] Testes NUnit com HttpListener.

## 7. Notas

Decisão registrada: **não executar código de usuário no processo** (diferente do upstream Python) — extensibilidade via pipelines externos e tools HTTP já existentes. Revisitar se houver demanda por sandbox (ex.: subprocess isolado).

## 8. Delivered

- **PR**: #77 · **Issue**: #62
- **Skills** (`/api/v1/skills/`): CRUD owner-scoped (admin vê todas); conteúdo injetado no system prompt quando anexada ao modelo custom via `MetaJson.skill_ids`; aba Skills no workspace (edição inline — desvio documentado das rotas dedicadas do SPEC, mesmo resultado).
- **Functions** (`/api/v1/functions/`): registry admin-only de `filter|pipe|action` com `ManifestJson` + `ValvesJson`; toggle `/{id}/toggle`; `/{id}/valves` valida JSON. Execução delegada a pipelines (decisão da SPEC: zero código arbitrário no servidor .NET).
- **Pipelines** (`/api/v1/pipelines/`): servidores externos (name/url/key mascarada, `********` preserva); `GET /list` agrega pipes de todos (tolerante a servidor fora — ignora na listagem).
- **Roteamento**: `pipeline:{id}` em `/api/chat/completions` → `FindServerForPipeAsync` resolve o servidor → POST `{url}/chat/completions` com `{model,messages,stream,valves}` → passthrough do corpo (SSE). Pipe inexistente → 404; servidor fora → 502. Pipes aparecem em `/api/models` como `pipeline:{id}`.
- Valves: `{fn_id: {...}}` de todas as functions ativas é mesclado no corpo enviado ao pipeline.
- Entidades como migration `PluginEcosystem` em `Entities.cs` (convenção do repo sobre o file-layout do SPEC).
- Fora de escopo (mantido): execução de Python/Roslyn, hot-reload, inlet/outlet filters como middleware, community openwebui.com.
- Testes: `PluginEcosystemTests` (9 — skills CRUD/isolamento, injeção no system prompt via mock Ollama, functions admin 403/CRUD/toggle/valves/tipo inválido, pipelines registro/descoberta/roteamento/404/502).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/77 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/62
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/48
