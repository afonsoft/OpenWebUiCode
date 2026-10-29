# SPEC-20261001-tools-functions

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `tools-functions` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261001-tools-functions` |
| Ticket | `GAP-implementation-tools-functions — Issue a criar` |
| Status | `Completed` |

## 1. User Story

**As a** usuário avançado do Open WebUI
**I want** registrar tools/functions que os modelos podem invocar no chat
**So that** o modelo execute ações externas (function calling) como no sistema de plugins do upstream.

**Problem context:**
Sem Tools/Functions/Pipes/Filters (`docs/MIGRACAO-DOTNET.md:62` — ⬜). Upstream implementa plugins Python; no .NET o equivalente natural é function calling com tools declarativas/HTTP.

## 2. Scope

**In scope:**
- Entidade `Tool`: nome, descrição, schema JSON de parâmetros, endpoint HTTP de execução (URL + auth opcional).
- Function calling no pipeline de completions: tools ativas do chat viram `tools` no payload para providers que suportam (OpenAI-compatível); resposta `tool_calls` dispara execução HTTP e o resultado volta ao modelo (loop de turno).
- Aba Tools em `/workspace` (CRUD, toggle por chat via menu `+`).
- `[A DEFINIR]` tools nativas compiladas (C# built-ins como web search/calculator) — recomendado: sim, via registry simples.

**Out of scope:**
- Sandbox de execução de código Python do usuário (upstream executa Python — no .NET isso é outra arquitetura; fase 2 via Open Terminal/SPEC code-execution).
- Pipes/filters custom completos (fase 2).
- Valves/secrets por tool (fase 2 — usar env vars inicialmente).

## 3. Technical Context

**Where the change happens:**
`Domain` (Tool), `Infrastructure` (ToolExecutor HTTP + registry nativo), `Api` (`/api/v1/tools` + tool loop no completions), `Client` (aba Tools, seletor por chat).

**Files to read before implementing:**
- `src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs` (pipeline de completions/streaming)
- `src/OpenWebUI.Api/Endpoints/WorkspaceEndpoints.cs`, `src/OpenWebUI.Client/Pages/Workspace.razor`

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities/Tool.cs
src/OpenWebUI.Application/Contracts/ToolContracts.cs
src/OpenWebUI.Infrastructure/Services/ToolExecutor.cs
src/OpenWebUI.Api/Endpoints/ToolEndpoints.cs (+ completions tool loop)
src/OpenWebUI.Client/Pages/Workspace.razor, Components/ChatView.razor
tests/OpenWebUI.Api.Tests/ToolEndpointsTests.cs
```

## 4. Requirements

### RF-001: CRUD de tools
- **Description:** Registrar tools com schema JSON e endpoint de execução.
- **Rules:** schema validado; dono/admin-only.
- **Input → Output:** spec da tool → tool registrada

### RF-002: Tool calling no chat
- **Description:** Tools selecionadas são enviadas no completions; `tool_calls` executa e injeta resultado, repetindo até resposta final.
- **Rules:** máx N iterações (default 5); erro de execução vira resultado de erro ao modelo; streaming da resposta final preservado.
- **Input → Output:** mensagem + tools → resposta final pós-execuções

### RF-003: Seleção por chat
- **Description:** Menu no input permite ligar/desligar tools da conversa.
- **Rules:** seleção persistida por chat.

**Business rules / invariants:**
- Execução de tool é sempre servidor-side; URL de execução nunca exposta ao cliente.
- Tool desabilitada não é enviada ao modelo.

## 5. API Contract

**Endpoint:** `GET|POST|PUT|DELETE /api/v1/tools`
**Auth:** `Bearer`

**Response:** `200` tool · `400` schema inválido · `403`

## 6. Critérios de Aceite

- [ ] Tool mock (endpoint local) é invocada quando o provider retorna `tool_calls`.
- [ ] Resultado da tool aparece na resposta final do modelo.
- [ ] Aba Tools lista/cria/edita; toggle por chat persiste.
- [ ] Testes cobrem o loop de tool calling com provider mock.

## 7. Notas

`[A DEFINIR]` se tools HTTP usam chamada direta ou queue; recomendado: direta com timeout 30s.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/33 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/19
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/14
