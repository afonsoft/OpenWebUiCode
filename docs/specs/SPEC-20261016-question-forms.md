# SPEC-20261016-question-forms

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `question-forms` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261016-question-forms` |
| Ticket | [#414](https://github.com/afonsoft/open-webui/issues/414) · Epic [#408](https://github.com/afonsoft/open-webui/issues/408) |
| Status | `Completed` |
| Fonte | análise fork alltomatos/opencode — `opencode/src/question/index.ts` (ask multi-pergunta com options/tool attribution/reject) |

## 1. User Story

**As a** usuário executando uma run de agente
**I want** que `ask_user` apresente até N perguntas de uma vez com opções estruturadas por pergunta
**So that** runs que precisam de múltiplas decisões não serializam perguntas uma a uma.

**Problem context:** `ask_user` hoje faz UMA pergunta com opções por chamada. O Question do fork aceita uma lista de perguntas (cada uma com opções próprias + livre), devolve array de respostas e suporta rejeição explícita.

## 2. Scope

**In scope:**
- RF-001: `ask_user` aceita `questions: [{question, header?, options:[{label,description?}], multiple?, required?}]` (até 5); forma singular atual vira açúcar para uma pergunta (retrocompatível).
- RF-002: Gate emite `question_asked` com array; UI renderiza form agrupado (uma tela) — resposta = array alinhado por índice `{answers: [..]}` ou `null` (dismiss).
- RF-003: `multiple:true` permite multi-seleção; `required:false` permite pular (resposta vazia).
- RF-004: Reject/dismiss devolve `Rejected` ao modelo como `output:"usuário recusou responder"` — run segue sem retry.
- RF-005: `tool` attribution: campo opcional no evento indica a tool que originou (telemetria/debug).

**Out of scope:**
- Validação de tipos além de string/multi.
- UI de review posterior das perguntas — só a tela inline existente.

## 3. Technical Context

- `src/OpenWebUI.Infrastructure/ChatTools/Tools/AskUserBuiltinTool.cs` — tool atual (gate-based).
- `ChatRunApprovals`/gate `ToolGateDecision.Output` — canal de resposta.
- `ChatView.razor` — render do prompt de pergunta.

## 4. Requirements

### RF-001: Schema
- **Rules:** `questions[]` com `question` obrigatório por item; `options` 0..4; cap total 5 perguntas; exceder → erro de tool `max 5 questions`.

### RF-002/003: Respostas
- **Rules:** `answers[i]` = string | string[] | null; ausência de answers → reject.

## 5. API Contract

Evento `question_asked` payload: `{questions:[{question,header,options,multiple,required}], tool?}`; resposta `{answers:[...]}`; dismiss `{rejected:true}`.

## 6. Acceptance Criteria

- [x] **Given** tool chamada com 3 perguntas **when** gate emite **then** form único com 3 blocos; resposta array alinhado.
- [x] **Given** `multiple:true` **when** usuário marca 2 opções **then** `answers[i]` = array com 2.
- [x] **Given** dismiss **when** resposta **then** modelo recebe texto de recusa, run continua.
- [x] **Given** forma singular antiga **when** chamada **then** comportamento idêntico ao atual (retrocompat).

## 7. Task Plan

- [x] **T1 —** Schema/parsing multi-pergunta na tool + validações.
- [x] **T2 —** Evento/gate payload + UI form agrupado.
- [x] **T3 —** Reject + retrocompat + testes.

**7.1 Validation strategy:** unit tests de schema/serialização + teste do fluxo gate→resposta.

## 8. Organization Guardrails

- Perguntas nunca carregam segredos; cap de texto por pergunta/opção (500 chars).
- Respostas sanitizadas antes de voltar ao modelo.

## 9. Definition of Done

- [x] RF-001..RF-005 verdes; build+test limpos; spec-status ok; retrocompat coberta por teste.

## Delivered

| Item | Link |
| --- | --- |
| PR/commit | #421 · 7a2bb4265 |
| Issue | https://github.com/afonsoft/open-webui/issues/414 |
