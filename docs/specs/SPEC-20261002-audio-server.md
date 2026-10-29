# SPEC-20261002-audio-server

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `audio-server` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261002-audio-server` |
| Ticket | Issue #54 |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** STT/TTS por providers server-side (Whisper/OpenAI/etc.) quando o navegador não suporta Web Speech
**So that** voz funciona em qualquer cliente, igual ao upstream.

**Problem context:**
Voz atual é 100% client-side (Web Speech API — `audio.js`), cobrindo só Chromium/Safari modernos. Upstream `audio.py` tem `/speech` (TTS), `/transcriptions` (STT Whisper/Deepgram/OpenAI), `/voices`, `/models`, `/config`.

## 2. Scope

**In scope:**
- `GET/POST /api/v1/audio/config` — providers STT/TTS, chaves mascaradas, modelos, engine (local/openai/deepgram).
- `POST /api/v1/audio/speech` — TTS: `{model?, voice?, input}` → audio bytes (Content-Type do provider).
- `POST /api/v1/audio/transcriptions` — STT multipart file → `{text}`.
- `GET /api/v1/audio/voices` e `/models` — lista do provider configurado.
- Client: fallback automático — se Web Speech indisponível, usa endpoints (gravação MediaRecorder → transcriptions; playback do TTS).
- Preferência por usuário: engine `web-speech|server` em Settings → Geral.

**Out of scope:**
- Whisper/Kokoro **locais** (binários nativos pesados) — só providers HTTP.
- VAD (voice activity detection) avançado — manter push-to-talk/loop simples.
- Streaming TTS em tempo real (upstream tbm é buffered na maior parte).

## 3. Technical Context

**Where:** `Application` (IAudioService, contratos), `Infrastructure` (AudioService com OpenAI-compatible `/audio/*` + Deepgram), `Api` (`AudioEndpoints`), `Client` (`audio.js` fallback + `VoiceButton`/`CallOverlay`).

**Files to read:**
- `src/OpenWebUI.Client/wwwroot/js/audio.js` (Web Speech impl)
- `src/OpenWebUI.Client/Components/{VoiceButton,CallOverlay}.razor`
- `src/OpenWebUI.Infrastructure/Services/ImageGenerationService.cs` (padrão provider config)

**Files to create/modify:**
```text
src/OpenWebUI.Application/Contracts/AudioContracts.cs
src/OpenWebUI.Application/Interfaces/IAudioService.cs
src/OpenWebUI.Infrastructure/Services/AudioService.cs
src/OpenWebUI.Api/Endpoints/AudioEndpoints.cs
src/OpenWebUI.Client/wwwroot/js/audio.js (modo server)
src/OpenWebUI.Client/Components/{VoiceButton,CallOverlay}.razor
tests/OpenWebUI.Api.Tests/AudioEndpointsTests.cs
```

## 4. Requirements

### RF-001: Config
- **Description:** Engine STT/TTS + chaves configuráveis em Admin → Audio; chaves mascaradas.
- **Input → Output:** POST config → persistido; GET → `key_configured` flags.

### RF-002: TTS server-side
- **Description:** `/speech` encaminha ao provider (OpenAI `/audio/speech` compatível) e devolve bytes de áudio.
- **Rules:** provider desabilitado → `501`; propagar erro do provider com `detail`.
- **Input → Output:** `{input, voice?, model?}` → `audio/mpeg`

### RF-003: STT server-side
- **Description:** `/transcriptions` aceita multipart `file` e devolve `{text}` (Whisper/OpenAI/Deepgram).
- **Rules:** limite de tamanho; formatos comuns (wav/mp3/webm/m4a).
- **Input → Output:** `file` → `{text}`

### RF-004: Fallback do cliente
- **Description:** `VoiceButton`/`CallOverlay` preferem Web Speech; se ausente ou `engine=server`, gravam via MediaRecorder e chamam `/transcriptions`; TTS via `/speech` quando `speechSynthesis` ausente.
- **Rules:** sem engine alguma → botão escondido (comportamento atual).

**Invariants:** chaves de provider no servidor; áudio do usuário não é persistido além do processamento.

## 5. API Contract

`GET|POST /api/v1/audio/config` (admin) · `POST /api/v1/audio/speech` · `POST /api/v1/audio/transcriptions` (multipart) · `GET /api/v1/audio/{voices,models}`
**Auth:** Bearer · config: admin

## 6. Critérios de Aceite

- [ ] `/transcriptions` com mock de provider retorna `{text}`.
- [ ] `/speech` devolve bytes com content-type correto.
- [ ] Provider não configurado → `501` consistente com image-generation.
- [ ] Cliente faz fallback sem quebrar o fluxo Web Speech existente.
- [ ] Testes NUnit com HttpListener mock.

## 7. Notas

Seguir o padrão de `ImageGenerationService` (config admin + `501`). Avaliar Whisper self-hosted (whisper.cpp) como fase 2 — dependência nativa no Docker aumenta imagem.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/69 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/54
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/48
