# SPEC-20261002-image-engines

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `image-engines` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261002-image-engines` |
| Ticket | Issue #57 |
| Status | `Completed` |

## 1. User Story

**As a** admin do Open WebUI
**I want** escolher o engine de geração de imagens (OpenAI, ComfyUI, A1111, Gemini)
**So that** posso usar backends locais ou alternativos, como no upstream.

**Problem context:**
Slice image-generation só cobre OpenAI Images. Upstream `images.py` (6 eps) suporta ComfyUI (workflow JSON), AUTOMATIC1111, Gemini e OpenAI, além de edição de imagem.

## 2. Scope

**In scope:**
- Abstração `IImageEngine` + engines: `OpenAI` (existente), `ComfyUI` (prompt → workflow JSON parametrizado), `Automatic1111` (`/sdapi/v1/txt2img`), `Gemini` (`generateImage`).
- Engine selecionável em Admin → Imagens; config por engine (URL, key mascarada, modelo, steps/size).
- `POST /api/v1/images/generations` despacha para engine ativo (mesma assinatura pública).
- Edição de imagem (`image-to-image`) onde o engine suporta (A1111 `img2img`, OpenAI `edits`).

**Out of scope:**
- ComfyUI websocket progress; fila/prioridades; inpainting avançado.
- Storage externo (S3) — imagens continuam em `files`.

## 3. Technical Context

**Where:** `Application` (`IImageEngine`, contratos), `Infrastructure` (`ImageGenerationService` → factory + engines), `Api` (mesmos endpoints + config), `Client` (dropdown de engine na config admin).

**Files to read:**
- `src/OpenWebUI.Infrastructure/Services/ImageGenerationService.cs`
- `src/OpenWebUI.Api/Endpoints/ImageEndpoints.cs`
- `src/OpenWebUI.Client/Components/SettingsModal.razor` (aba imagens)

**Files to create/modify:**
```text
src/OpenWebUI.Application/Interfaces/IImageEngine.cs
src/OpenWebUI.Infrastructure/Services/Image/{OpenAiImageEngine,ComfyUiEngine,A1111Engine,GeminiImageEngine}.cs
src/OpenWebUI.Infrastructure/Services/ImageGenerationService.cs (factory)
src/OpenWebUI.Api/Endpoints/ImageEndpoints.cs
tests/OpenWebUI.Api.Tests/ImageEnginesTests.cs
```

## 4. Requirements

### RF-001: Engine factory
- **Description:** `ImageGenerationService` resolve engine por `images.engine` do config; desconhecido → erro claro.
- **Input → Output:** engine name → implementação

### RF-002: Engines
- **Description:** Cada engine implementa `GenerateAsync(prompt, options)`; ComfyUI aceita workflow template com placeholders `{prompt},{seed},{steps}`; A1111 chama txt2img; Gemini chama generateImage; OpenAI mantém comportamento.
- **Rules:** timeout/retries da config; falha → `502 {detail}`.
- **Input → Output:** prompt → bytes da imagem → salvo em `files` (fluxo existente)

### RF-003: Edição
- **Description:** `POST /api/v1/images/edit` com `image_id` + prompt, quando engine suporta.
- **Rules:** engine sem suporte → `501`.

### RF-004: Config admin
- **Description:** Seleção de engine + parâmetros por engine persistidos; chaves mascaradas; teste de conectividade opcional (`POST .../config/test`).

**Invariants:** config de engine inativo pode existir sem quebrar; chaves nunca retornam.

## 5. API Contract

`POST /api/v1/images/generations` (existente) · `POST /api/v1/images/edit` (novo) · `GET|POST /api/v1/images/config` (admin)
**Auth:** Bearer · config: admin

## 6. Critérios de Aceite

- [ ] Trocar engine na config muda o backend sem restart.
- [ ] ComfyUI/A1111/Gemini testados com HttpListener mock.
- [ ] `edit` retorna `501` em engine sem suporte.
- [ ] Testes NUnit por engine + factory.

## 7. Notas

ComfyUI exige workflow JSON por instalação — config guarda template, não workflow fixo upstream-específico. Prioridade de engines: A1111 (API simples) → Gemini → ComfyUI (mais complexo).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/72 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/57
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/48
