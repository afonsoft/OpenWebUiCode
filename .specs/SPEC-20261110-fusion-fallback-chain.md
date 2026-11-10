# SPEC-20261110-fusion-fallback-chain

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `fusion-fallback-chain` |
| Type | `Feature/Resilience` |
| Stack | `.NET` |
| Repository | `OpenWebUiCode` |
| Status | `Approved` |
| Depende de | SPEC-20261106 (adaptive-fusion), SPEC-20261016 (model-combos) |

## 1. User Story

**As a** usuário do modo Adaptive/Fusion
**I want** que o par `fusion:` aceite 2 opções de Leader e 2 de Sidekick com fallback ordenado entre si
**So that** quando o provedor do Leader morre (quota/crédito/conexão — ex.: `502 bai/claude-opus-5: Access restricted. Deposit required`), a run continua no próximo modelo da cadeia em vez de falhar de cara.

**Problem context (incidente real, 2026-11-10):** chat `3b692797…` com `fusion:auto/claude-opus>auto/claude-sonnet` — o `auto/claude-opus` do OmniRoute esgotou todos os upstreams (bai 403 deposit, antigravity 429, requesty 412, openrouter/kilocode 402, cursor 401) → run falhou com `server-error` após 6 tentativas. Existia sidekick (`auto/claude-sonnet`) com pool de providers saudável que nunca foi tentado — o par fusion hoje só troca de modelo se o usuário reenviar a mensagem.

## 2. Sintaxe

Extensão retrocompatível de `fusion:{leader}>{sidekick}`:

```
fusion:{L1,L2}>{S1,S2}
fusion:auto/claude-opus,auto/claude-sonnet>auto/claude-haiku,auto/glm
```

- `,` separa opções ordenadas **dentro** de cada papel (vírgula não aparece em ids de modelo — verificado no catálogo: ids usam `/`, `.`, `:`, `@`, `~`).
- `>` continua separando a cadeia de leaders da cadeia de sidekicks.
- `fusion:L>S` (um de cada) = comportamento atual, cadeia de 1. Nada muda.
- Slots vazios (`fusion:L,>S`) → erro de parse, rejeitado.
- Dedup: o mesmo id repetido na cadeia é mantido uma única vez (primeira posição).

Novo ref interno `chain:{m1,m2,...}` — fallback ordenado **sem** semântica de orquestração (sem prompt fusion, sem kv de par). Gerado pelo executor para os filhos (ver §4) e aceito direto no campo `Model` como utilidade.

## 3. Semântica de fallback (server)

Cadeia efetiva da run = `[L1, L2, S1, S2]` — ordem absoluta: leaders primeiro, depois sidekicks ("todos pode ser fallback de todos, porém sempre segue a ordem").

1. `ApplyFusionPairingAsync` resolve a cadeia uma vez; a tentativa `i` roda com `request.Model = chain[i]`.
2. "x tentativas" por posição = o retry budget **existente** do `ProviderRetryPolicy` (`Providers:MaxAttempts`) — o fallback avança quando a tentativa termina com falha classificada de provedor:
   - `HttpRequestException` (inclui `ProviderHttpException`),
   - `TaskCanceledException`/`TimeoutException`,
   - `InvalidOperationException` com prefixo tipado do retry loop: `server-error:`, `rate-limit:`, `provider-timeout:`.
3. **Gate de segurança** (mesmo princípio do combo, porém menos estrito): avança apenas enquanto `state.ToolMessages` está vazio — nenhuma tool executada = nenhum efeito colateral a duplicar. Conteúdo parcial já emitido NÃO bloqueia: o prefixo permanece e o novo modelo continua a resposta (melhor que falhar a run inteira). Com tool results presentes → falha normal.
4. Ao avançar: `PublishPhaseAsync("fusion", "Leader X falhou → tentando Y")` + `RunEventWriter` note, para o usuário ver a troca na UI.
5. kv `r:{run}:fusion.active` registra o modelo que efetivamente serviu (analytics/debug).
6. Cancelamento (`OperationCanceledException`) nunca avança a cadeia — sai como `Stopped` como hoje.

## 4. Sub-agentes (sidekick)

- kv `r:{run}:fusion.sidekick` passa a gravar `chain:{S1,S2,L1,L2}` (cadeia a partir do primeiro sidekick, completada com os leaders — "todos fallback de todos" na ordem sidekick→leader).
- `DelegateTaskBuiltinTool` lê a kv como hoje — o valor já sai como `chain:` e o run filho executa com fallback próprio (`chain:` resolve posição a posição com a mesma regra do §3, sem prompt fusion).
- `model:`/`agent:` explícito no `delegate_task` continua vencendo.
- kv `fusion.leader` mantém o primeiro leader (retrocompat com leitores existentes).

## 5. UI (ModelSelector)

- O bloco fusion ganha 4 selects: `Leader` (obrigatório), `Leader 2` (opcional, "—"), `Sidekick` (obrigatório), `Sidekick 2` (opcional, "—").
- Emite `fusion:{L1[,L2]}>{S1[,S2]}` ignorando slots vazios/`—`.
- Restaura do `Value` fazendo split por `,` nos dois lados.
- Persiste `webui.fusion.leader.2`/`webui.fusion.sidekick.2` além das chaves atuais.
- i18n: `models.fusion_leader2`, `models.fusion_sidekick2`, `models.fusion_fallback_hint` nos 8 locales; chip/label pode exibir a cadeia (ex.: `Fusion: opus → sonnet`).

## 6. Plano de implementação

| # | Arquivo | Mudança |
| --- | --- | --- |
| 1 | `src/OpenWebUI.Application/Contracts/FusionModelRef.cs` | `TryParseChain(model, out leaders, out sidekicks)`; `TryParse` atual delega ao chain; `ChainPrefix` + `Format`/`TryParse` para `chain:` (pode viver em `ChainModelRef.cs` próprio) |
| 2 | `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` | Estado `fusionChain`/`chainPos` no `ExecuteAsync`; `IsProviderFailure(ex)`; avanço da cadeia no caminho de `HandleRunFailureAsync` (gate `ToolMessages` vazio); `ApplyFusionPairingAsync` aceita posição da cadeia; kv `fusion.sidekick`=ref `chain:`, `fusion.active`, `fusion.leader`; eventos de fase por avanço; `chain:` resolvido no mesmo laço (sem pairing) |
| 3 | `src/OpenWebUI.Infrastructure/ChatTools/Tools/DelegateTaskBuiltinTool.cs` | Sem mudança funcional (a kv já entrega `chain:`) — ajuste de comentário/doc |
| 4 | `src/OpenWebUI.Api/Endpoints/ApiEndpoints.cs` | `TryParse` → primeiro leader da cadeia (`LeaderOrSelf` já resolve) |
| 5 | `src/OpenWebUI.Client/Components/ModelSelector.razor` | 4 selects + emissão/restore da ref + chaves extras |
| 6 | `src/OpenWebUI.Client/wwwroot/i18n/*.json` | Novas chaves nos 8 locales |
| 7 | Testes | `FusionModelRef` (parse cadeia, dedup, retrocompat); executor (provider-failure → próximo modelo; gate tool results; cancel não avança); `chain:` em run filha; emissão da ref no selector |

## 7. Critérios de aceite

- `fusion:L1,L2>S1,S2` com L1 falhando por provedor → run continua em L2, depois S1, S2; evento `fusion` visível no stream a cada troca.
- Cadeia esgotada → run falha com o último erro (comportamento atual).
- `fusion:L>S` funciona exatamente como hoje (regressão).
- Sub-agente herdando sidekick cadeia executa com fallback próprio na ordem S→L.
- Erro não-provider (ex.: budget negado, model fora do catálogo, exception de código) não avança a cadeia — falha direto.
- Cancelamento/Stop permanece `stopped`, sem avanço.
- Conteúdo parcial preservado ao trocar de modelo; tool side-effects nunca são reexecutados.

## 8. Fora de escopo

- Trocar de modelo no meio do tool loop após tool calls (precisaria de journal de side-effects — o gate `ToolMessages` vazio já cobre o caso principal: provedor morto desde o início).
- Estratégia inteligente de ordenação (score/latência) — a ordem é a declarada pelo usuário.
- `arena:`/`combo:`/`pipeline:` aninhados dentro de `fusion:` — seguem proibidos como hoje.
