# SPEC-20261001-image-generation

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `image-generation` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261001-image-generation` |
| Ticket | `[image-generation] Issue #21` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** gerar imagens a partir do chat e editá-las
**So that** eu tenha a feature de image generation do upstream.

**Problem context:**
Sem image generation (`docs/MIGRACAO-DOTNET.md:67` — ⬜). Upstream suporta AUTOMATIC1111, ComfyUI, OpenAI DALL·E, Gemini.

## 2. Scope

**In scope:**
- Botão "Gerar imagem" por mensagem do assistente + comando no input.
- Provider OpenAI Images (`gpt-image-1`/DALL·E) como primeira engine, configurável por admin (base URL + key).
- Galeria: imagem gerada aparece inline na conversa; salva em `files`.
- Config em Settings → Admin (engine, modelo, tamanho, steps `[A DEFINIR]` subconjunto).

**Out of scope:**
- ComfyUI/AUTOMATIC1111/workflows (fase 2).
- Edição/inpainting.
- Image-to-image.

## 3. Technical Context

**Where the change happens:**
`Api` (`/api/v1/images/generations` + config admin), `Infrastructure` (cliente HTTP do provider, storage em files), `Client` (botão no MessageBubble, render inline, settings).

**Files to read before implementing:**
- `src/OpenWebUI.Api/Endpoints/{FileEndpoints,ApiEndpoints}.cs`
- `src/OpenWebUI.Client/Components/{MessageBubble,SettingsModal}.razor`
- `src/OpenWebUI.Infrastructure/Services/` (provider http pattern)

**Files to create or modify:**
```text
src/OpenWebUI.Api/Endpoints/ImageEndpoints.cs
src/OpenWebUI.Infrastructure/Services/ImageGenerationService.cs
src/OpenWebUI.Application/Contracts/ImageContracts.cs
src/OpenWebUI.Client/Components/MessageBubble.razor, SettingsModal.razor
tests/OpenWebUI.Api.Tests/ImageEndpointsTests.cs
```

## 4. Requirements

### RF-001: Geração por prompt
- **Description:** `POST /api/v1/images/generations` recebe prompt e retorna imagem persistida.
- **Rules:** provider desabilitado → `501`; timeout configurável; imagem salva em files com owner.
- **Input → Output:** `{prompt, size?}` → `{url}`

### RF-002: Integração no chat
- **Description:** Botão na ação da mensagem gera imagem a partir do texto e anexa à conversa.
- **Rules:** só com feature habilitada; falha vira mensagem de erro inline.
- **Input → Output:** clique → imagem inline na mensagem

### RF-003: Config admin
- **Description:** Engine, modelo, credenciais e parâmetros em Settings → Admin.
- **Rules:** secrets mascarados; persistência como demais conexões.
- **Input → Output:** form → config ativa

## 5. API Contract

**Endpoint:** `POST /api/v1/images/generations`
**Auth:** `Bearer`

**Request:** `{"prompt":"...","n":1,"size":"1024x1024"}`
**Response:** `200 [{url}]` · `401` · `501` feature off

## 6. Critérios de Aceite

- [ ] Com provider mock, gerar imagem salva arquivo e retorna URL renderizável.
- [ ] Botão na mensagem anexa a imagem à conversa.
- [ ] Feature off → endpoint `501` e botão oculto.
- [ ] Testes cobrem geração e autorização.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/37 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/23
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/14
