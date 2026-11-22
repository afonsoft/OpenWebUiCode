# SPEC-20261124-compaction-diet — dieta progressiva do transcript do summarizer

**Status**: Proposta

## Motivação

Portado do `anomalyco/opencode` (dev, `compaction.ts`): o transcript
enviado ao summarizer passa por uma escada de redução até caber no
budget — o summarizer nunca recebe um transcript que ele mesmo não
consegue ler. Hoje `BuildTranscriptAsync`
(`src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs:1112`)
faz só a poda mecânica do `ContextPruner` e manda o head inteiro —
runs longas com muita saída de tool podem estourar o próprio
summarizer ou devolver resumo degradado.

## Design

Escada em `BuildTranscriptAsync`, budget estimado em chars
(≈ tokens×4, mesmo estimador da compactação), aplicada enquanto
`transcript.Length > budget` — degradações em ordem, sempre mais
antigos primeiro e **a troca mais recente nunca é omitida**:

1. **Clip tool outputs** — `role:tool`/`ToolCallsJson` para head+tail
   de ≤2.000 chars por ponta (`…[elided N chars]…` no meio).
2. **Clip tool inputs** — args das tool calls assistant idem.
3. **Clip user text** — mensagens `user` para head+tail 2.000.
4. **Omitir trocas antigas** — pares user/assistant inteiros do início
   do head, mantendo sempre os marcadores `[pos]` corretos (posição
   absoluta — os breadcrumbs do resumo seguem válidos).
5. Último recurso: transcript mínimo = resumo anterior + última troca.

Mídia (quando entrar no transcript) vira marcador textual
`[image/png omitted]` / `[application/pdf omitted]` com estimativa
fixa (1.500/2.000 tok) — nunca payload binário para o summarizer.
Padrão já alinhado ao `[pos]`/`history://` existente.

Sem mudança de contrato: o summarizer continua recebendo
`prompt + transcript`; o JSON estruturado de saída é inalterado.

## Testes

- `BuildTranscript_Cabe_Identico` — abaixo do budget não corta nada.
- `Diet_ClipToolOutputs_PreservaHeadTail` — marcador elided + pontas.
- `Diet_EscadaProgressiva` — transcript gigante passa pelos 4 degraus
  em ordem (assert por tipo de marcador presente).
- `Diet_NuncaOmiteUltimaTroca` — head de 1 troca gigante → clipada,
  não removida.
- `Diet_PosicoesCorretas` — breadcrumbs `[pos]` permanecem absolutos
  após omissões.
- E2E: chat com head enorme → `CompactAsync` não lança e resumo sai.
