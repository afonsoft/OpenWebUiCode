# SPEC-20261003-audio-engines

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `audio-engines` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261003-audio-engines` |
| Ticket | Issue #83 |
| Status | `Completed` |

## 1. User Story

**As a** usuário
**I want** engines extras de STT/TTS (Whisper local, ElevenLabs, Azure)
**So that** o router `audio` upstream (6 eps + engines) cobre as engines restantes.

**Problem context:**
Slice audio-server entregou STT openai/deepgram + TTS OpenAI-compatible + fallback Web Speech. Falta: Whisper local (faster-whisper via container opcional), ElevenLabs e Azure Speech.

## 2. Scope

**In scope:**
- STT: engine `whisper` — chamada HTTP a servidor faster-whisper externo configurável (`WHISPER_URL`); sem engine local embutida no processo.
- TTS: engines `elevenlabs` (`{base_url, api_key, voice_id}`), `azure` (`{region, key, voice}`) e `transformers` [A DEFINIR — spike].
- Config admin estende `AudioConfig` com campos por engine + vozes por engine em `/audio/voices`.
- Cliente: seletor de engine já existente ganha as novas opções.

**Out of scope:**
- Whisper embutido no processo .NET (peso de runtime ML); Kokoro.

## 3. Technical Context

**Files to read:**
- `src/OpenWebUI.Api/Endpoints/AudioEndpoints.cs`
- `src/OpenWebUI.Infrastructure/Services/AudioService.cs`
- `src/OpenWebUI.Client/Components/SettingsModal.razor` (seção Áudio)

## 4. Requirements

### RF-001: Whisper externo
- **Description:** multipart → `WHISPER_URL` (faster-whisper-server); timeout 60s; falha → 502.

### RF-002: ElevenLabs/Azure TTS
- **Description:** `POST /audio/speech` roteia por engine; vozes listadas por engine.

### RF-003: Config/UI
- **Rules:** chaves mascaradas (`********` preserva); engines sem config → não aparecem em `/capabilities`.

## 5. API Contract

Mesmos endpoints; `engine` aceita `openai|deepgram|whisper|elevenlabs|azure`.

## 6. Critérios de Aceite

- [ ] STT via whisper mockado (HttpListener) retorna texto.
- [ ] TTS elevenlabs/azure retornam audio/* com provider mock.
- [ ] Capabilities refletem engines configuradas.

## Delivered

- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/83
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/79
- PR: https://github.com/afonsoft/OpenWebUiCode/pull/104

## Reconciliation

_Reconciliado em 2026-10-08 (SPEC-20261008-spec-status-reconciliation, Issue #203)._

| RF | Veredito | Evidência |
| --- | --- | --- |
| RF-001 Whisper externo | delivered | `Infrastructure/Services/AudioService.cs:229-235` — engine `whisper`, multipart para `{sttBaseUrl}/audio/transcriptions`, timeout 60s (vs 120s demais), falha → `AudioProviderException` → 502; testado em `AudioServiceTests` |
| RF-002 ElevenLabs/Azure TTS | delivered | `AudioService.SpeechAsync` roteia por engine (`:158-210`): elevenlabs `xi-api-key` + `/v1/text-to-speech/{voice}`, azure SSML + `Ocp-Apim-Subscription-Key` + `X-Microsoft-OutputFormat`; `GetVoicesAsync` com URL por engine; testado em `AudioServiceTests.Voices_UrlsPorEngine` |
| RF-003 Config/UI | delivered | `AudioConfig.Masked()` (`Application/Contracts/AudioContracts.cs:30`) retorna `********` preservando chave salva; `GET /audio/capabilities` (`Api/Endpoints/AudioEndpoints.cs:24`) só lista engines com config |

