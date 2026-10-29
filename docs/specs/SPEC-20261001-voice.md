# SPEC-20261001-voice

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `voice` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261001-voice` |
| Ticket | `GAP-implementation-voice — Issue a criar` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** ditar mensagens por voz (STT), ouvir respostas (TTS) e usar modo Call
**So that** a interação por áudio funcione como no upstream.

**Problem context:**
Sem STT/TTS/Call (`docs/MIGRACAO-DOTNET.md:66` — ⬜). Input de chat é texto-only.

## 2. Scope

**In scope:**
- STT no navegador: botão microfone no input usando Web Speech API (gratuito, local) — `[A DEFINIR]` se Whisper remoto entra também.
- TTS: botão "ouvir" por mensagem; engine navegador (speechSynthesis) + provider configurável (OpenAI TTS) — `[A DEFINIR]` escopo da primeira entrega.
- Modo Call (`/call` ou overlay): conversa por voz turn-based com STT→LLM→TTS.
- Configurações de áudio em Settings (engine, voz, auto-send após ditado).

**Out of scope:**
- STT/TTS self-hosted (Whisper, Kokoro, Edge-TTS) — fase 2.
- VAD avançado/interrupção de fala do modelo.
- Chamada com vídeo.

## 3. Technical Context

**Where the change happens:**
`Client` (JS interop de áudio em `wwwroot/js/`, componentes do input/Call), `Api` (`/api/v1/audio/speech` proxy TTS se provider remoto), `Application` (contratos de config de áudio).

**Files to read before implementing:**
- `src/OpenWebUI.Client/Components/{ChatView,MessageBubble}.razor`, `wwwroot/js/app.js`
- `src/OpenWebUI.Api/Endpoints/ApiEndpoints.cs` (config de features)

**Files to create or modify:**
```text
src/OpenWebUI.Client/wwwroot/js/audio.js
src/OpenWebUI.Client/Components/{VoiceButton,TtsButton,CallOverlay}.razor
src/OpenWebUI.Api/Endpoints/AudioEndpoints.cs (se TTS remoto)
tests — cobertura de config/feature flags
```

## 4. Requirements

### RF-001: Ditar (STT)
- **Description:** Botão microfone no input inicia/para ditado e preenche o campo.
- **Rules:** permission prompt do navegador tratado; fallback "não suportado" sem quebrar UI.
- **Input → Output:** fala → texto no input

### RF-002: Ouvir (TTS)
- **Description:** Botão por mensagem lê o conteúdo em voz alta.
- **Rules:** para ao clicar de novo ou navegar; respeita engine/voz configurada.
- **Input → Output:** texto → áudio

### RF-003: Modo Call
- **Description:** Interface full-screen de conversa por voz: ouve → envia → reproduz resposta, em loop.
- **Rules:** botão sair; funciona com o modelo selecionado; erro de provider encerra o turno com aviso.
- **Input → Output:** turnos de voz → transcrição + resposta falada

**Business rules / invariants:**
- Features de voz são opt-in e degradam silenciosamente sem suporte do navegador.
- Nenhum áudio persistido por padrão.

## 5. API Contract

**Endpoint:** `POST /api/v1/audio/speech` (opcional, provider TTS remoto)
**Auth:** `Bearer`

**Request:** `{"text":"...","voice":"...","model":"..."}`
**Response:** `200 audio/mpeg` · `501` sem provider configurado

## 6. Critérios de Aceite

- [ ] Ditado preenche o input no Chrome.
- [ ] Mensagem reproduz áudio ao clicar "ouvir".
- [ ] Modo Call executa um turno completo (voz→resposta falada) com mock de provider.
- [ ] Sem permissão de microfone → UI mostra estado claro, sem crash.

## 7. Notas

`[A DEFINIR]` engines da primeira entrega — recomendado: Web Speech (STT+TTS) no cliente já; provider remoto TTS (OpenAI) como config adicional.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/41 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/27
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/14
