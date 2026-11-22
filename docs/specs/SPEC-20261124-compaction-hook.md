# SPEC-20261124-compaction-hook — hook SessionCompacting no summarizer

**Status**: Completed

## Motivação

Portado do `anomalyco/opencode` (dev): o hook
`experimental.session.compacting` roda antes de gerar o resumo de
continuação — um plugin pode **acrescentar contexto** ao prompt do
summarizer (`output.context`) ou **substituir o prompt** inteiro
(`output.prompt`). Permite a um time injetar instruções de domínio
("preserve endpoints da API", "resuma diffs como lista de arquivos")
sem recompilar o summarizer. Hoje `KnownEvents`
(`HookConfigLoader.cs:18-21`) cobre
PreToolUse/PostToolUse/UserPromptSubmit/Stop/SessionStart/SessionEnd —
sem evento de compactação.

## Design

- Novo evento `SessionCompacting` em `KnownEvents` — disparado em
  `ContextCompactionService` imediatamente antes da chamada ao
  `LlmContextSummarizer` (auto, overflow e manual `/compact`;
  compaction nativa do provider não dispara — lá não há prompt nosso).
- Payload do hook: `chatId`, `model`, `trigger` (auto|overflow|manual),
  `transcriptChars`, `compactionN` (ordinal).
- Semântica opencode:
  - hook devolve `context` → texto anexado ao prompt vigente
    (`Prompt`/`PromptV3`) antes do transcript;
  - hook devolve `prompt` → substitui o prompt inteiro (context
    ignorado). Substituição só vale quando o hook é `type:"prompt"` —
    command hooks podem contribuir `context` mas nunca substituir
    (substituir exige prompt local, não shell).
- Falha/timeout do hook → compacta com o prompt padrão (a run nunca
  morre por causa de hook) — mesmo padrão `HookOutcome.ApproveNone`.
- Auditoria: evento `compaction_hook` gravado no run (outcome,
  elapsed, source) via `RecordHookAuditAsync` existente.

## Testes

- `Loader_SessionCompacting_Reconhecido` — entra no KnownEvents sem
  warning.
- `Compact_ComContextHook_AnexaPrompt` — `context` aparece no request
  do summarizer (mock captura body).
- `Compact_ComPromptHook_Substitui` — request usa o prompt do hook.
- `Compact_HookFalha_UsaPadrao` — hook que explode não impede o resumo.
- `Compact_Nativo_NaoDispara` — rota `compaction` nativa do provider
  não emite o evento.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/772
- Branch: feature/agent-20261124-opencode-gaps
