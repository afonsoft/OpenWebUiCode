# SPEC-20261001-code-execution

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `code-execution` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261001-code-execution` |
| Ticket | `GAP-implementation-code-execution — Issue a criar` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** executar código gerado pelo modelo diretamente na interface
**So that** eu valide snippets sem sair do chat, como no upstream (Pyodide/Open Terminal).

**Problem context:**
Sem code execution (`docs/MIGRACAO-DOTNET.md:68` — ⬜). Blocos de código no chat são somente visualização.

## 2. Scope

**In scope:**
- Botão "Executar" em blocos `python`/`javascript` do markdown.
- Engine JS: execução sandboxed no navegador (Web Worker, timeout, capture de stdout/stderr).
- Engine Python via Pyodide (WASM, carregamento sob demanda) — `[A DEFINIR]` CDN vs asset local (recomendado: CDN com fallback de aviso).
- Painel de resultado inline sob o bloco (stdout, stderr, duração).

**Out of scope:**
- Jupyter server remoto / "Open Terminal" backend (fase 2).
- Instalação de pacotes Python arbitrários (apenas stdlib Pyodide).
- Execução de outras linguagens.

## 3. Technical Context

**Where the change happens:**
`Client` apenas na primeira entrega — JS interop (`wwwroot/js/codeexec.js`), renderer de blocos em `MessageBubble`, feature flag em `/api/config`.

**Files to read before implementing:**
- `src/OpenWebUI.Client/Components/MessageBubble.razor`, `wwwroot/js/app.js`
- `src/OpenWebUI.Api/Endpoints/ApiEndpoints.cs` (feature flags)

**Files to create or modify:**
```text
src/OpenWebUI.Client/wwwroot/js/{codeexec.js,py-worker.js}
src/OpenWebUI.Client/Components/{CodeBlock,MessageBubble}.razor
```

## 4. Requirements

### RF-001: Executar JS
- **Description:** Bloco `javascript`/`js` ganha botão Executar; roda em Worker com timeout e captura `console.log`.
- **Rules:** timeout default 10s; sem acesso ao DOM do app.
- **Input → Output:** código → stdout/stderr inline

### RF-002: Executar Python
- **Description:** Bloco `python` ganha botão Executar via Pyodide; estado de loading visível.
- **Rules:** download lazy; erro de carregamento → botão desabilitado com tooltip.
- **Input → Output:** código → saída inline

### RF-003: Feature flag
- **Description:** `code_execution` flag em `/api/config` liga/desliga os botões.
- **Rules:** default off até admin habilitar? `[A DEFINIR]` — recomendado: on por padrão (client-side, sem risco ao servidor).

## 5. API Contract

N/A (client-side). Flag exposta em `GET /api/config`.

## 6. Critérios de Aceite

- [ ] `print("oi")` em bloco python executa e mostra `oi` inline.
- [ ] `console.log("oi")` em bloco js mostra `oi`.
- [ ] Código com loop infinito é abortado pelo timeout com mensagem clara.
- [ ] Flag off → nenhum botão renderizado.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/35 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/21
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/14
