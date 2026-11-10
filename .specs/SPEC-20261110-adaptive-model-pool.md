# SPEC-20261110-adaptive-model-pool

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `adaptive-model-pool` |
| Type | `Feature/Resilience` |
| Stack | `.NET` |
| Repository | `OpenWebUiCode` |
| Status | `Approved` |
| Depende de | SPEC-20261110 (fusion-fallback-chain — classificação de falhas, avanço de cadeia, gate de side-effect, eventos de fase), SPEC-20261106 (adaptive-fusion — mesma família no seletor) |

## 1. User Story

**As a** usuário que roda chats sobre providers instáveis (quota, crédito, conexão corporativa)
**I want** um modo `adaptive:` com até 4 modelos ordenados por preferência, que usa sempre o primeiro disponível, cai para o próximo em falha, volta ao primeiro na tarefa seguinte — e, quando o primeiro acumula falhas consecutivas em várias tarefas, fica preso ("sticky") no último que funcionou
**So that** a escolha do modelo se adapta sozinha à saúde dos providers ao longo do tempo, sem eu trocar manualmente.

**Problem context (mesmo incidente do fusion-chain, 2026-11-10):** `auto/claude-opus` no OmniRoute tinha pool esgotado (bai 403, antigravity 429, requesty 412, openrouter/kilocode 402, cursor 401) — toda run nova batia no mesmo muro. O `fusion:` cobre fallback **dentro** da run; o `adaptive:` cobre **entre** runs: lembra o que falhou nas últimas tarefas e para de insistir no modelo morto — mas continua tentando-o periodicamente porque quota volta.

**Relação com o Fusion existente:** a família "Adaptive" do seletor hoje tem um membro — `fusion:` (orquestração leader/sidekick, SPEC-20261106). O `adaptive:` entra como **segundo membro** da mesma família: routing adaptativo por pool ordenado com memória, sem roles. São ortogonais e podem coexistir (`fusion:` dentro do `adaptive:` fica fora de escopo — ver §9).

## 2. Sintaxe

```
adaptive:{M1,M2,M3,M4}
adaptive:auto/claude-opus,auto/claude-sonnet,auto/claude-haiku,auto/glm
```

- `,` separa candidatos em **ordem de preferência** (M1 = preferido, M4 = último recurso).
- 2 a 4 candidatos; 1 candidato = inválido (não há o que adaptar).
- Cada Mi é um model-id do catálogo (mesma validação do `chain:`/fusion — `openai:gpt-4o`, `auto/...`, etc.).
- Sem roles, sem `>` — lista plana.
- Dedup case-insensitive preservando a primeira ocorrência (reuso de `FusionModelRef`).

## 3. Semântica — o algoritmo adaptativo

Estado por chat (o ref `adaptive:` é o modelo do chat; estado persistido em KV com escopo do chat — §4):

| Campo | Tipo | Significado |
|---|---|---|
| `sticky` | int (default 0) | índice do último modelo que completou uma run com sucesso |
| `fails[i]` | int[4] | falhas consecutivas do candidato i **como modelo de início de run** |
| `probeAt` | timestamp | próximo momento em que `fails` zera (sonda de recuperação) |

**Regras:**

- **RF-001 — Início de run:** `start = 0` (sempre o preferido), EXCETO quando `fails[0] >= K` (default **K=3** falhas consecutivas como primeiro candidato) → `start = sticky`. Se `sticky` também estourou `fails[sticky] >= K`, `start` desce para o próximo índice com `fails < K` (menor índice possível), senão `start = 0`.
- **RF-002 — Avanço dentro da run:** em falha de provedor (mesma classificação do fusion-chain: `HttpRequestException`, `TaskCanceledException`, `TimeoutException`, `ProviderHttpException`, `InvalidOperationException` `server-error:`/`rate-limit:`/`provider-timeout:`) e **sem tool side-effects** (`ToolMessages` vazio), a run avança `start → start+1 → … → M4`. Cancelamento do usuário não avança.
- **RF-003 — Conteúdo parcial preservado** no avanço (mesma injeção assistant do fusion-chain).
- **RF-004 — Sucesso:** run completa no candidato `j` → `sticky = j`, `fails[j] = 0`. Se a run **começou** em `s > 0` porque `fails[0] >= K` e completou em `j`, `sticky = j` (memória atualizada). `fails[i≠j]` não zeram (cada um carrega sua própria reputação).
- **RF-005 — Falha como start:** quando a tentativa do candidato `s` (o start da run) falha e a run avança para `s+1`, `fails[s] += 1`. Falhas de candidatos não-start (`s+1…M4`) também incrementam o próprio `fails` — um modelo que falha como fallback não deve virar start depois.
- **RF-006 — Reset ao preferido ("terminou, volta pro primeiro"):** se a run completou no preferido (`j = 0`), `fails[0] = 0` e a próxima run volta a `start = 0`. Enquanto `fails[0] < K`, toda run nova tenta M1 primeiro — mesmo que a anterior tenha terminado num fallback.
- **RF-007 — Stickiness entre tasks:** quando `fails[0] >= K`, o start deixa de ser M1 e passa a ser `sticky` — o último que funcionou. O usuário não paga o custo de uma falha garantida por run.
- **RF-008 — Sonda de recuperação (probe):** quando o relógio passa de `probeAt` (default **30 min** após a última atualização de `fails[0]`), `fails` zera — a próxima run volta a tentar M1. Se M1 recuperou (quota renovada, provider voltou), o sistema retorna ao preferido sozinho. `probeAt` é renovado a cada incremento de `fails[0]`.
- **RF-009 — Pool esgotado:** todos os candidatos falharam na run → run falha com o último erro (igual fusion-chain esgotado).
- **RF-010 — Escopo do estado:** por **chat** — o ref `adaptive:` é um modelo do chat; cada chat tem sua reputação. (Decisão: estado global por usuário foi descartado — providers degradam por-workspace/carga, e dois chats com usos distintos merecem reputações independentes.)

### Exemplo de sequência

```
t1: M1 403 → M2 ok          → sticky=2, fails[1]=1
t2: M1 ok                  → sticky=1, fails zerado de M1      (volta pro 1º)
t3: M1 403 → M2 ok         → fails[1]=1, sticky=2
t4: M1 429 → M2 ok         → fails[1]=2, sticky=2
t5: M1 403 → M3 ok         → fails[1]=3 ≥K, sticky=3
t6: start=sticky(3) — M1 nem é tentado → M3 ok → sticky=3
…30min depois (probeAt)…
t7: fails zerado → start=M1 → M1 ok → sticky=1, de volta ao preferido
```

## 4. Estado persistente

`ConfigService` (mesmo mecanismo dos kvs `r:{run}:`), escopo chat:

```
c:{chatId}:adaptive.sticky   → int
c:{chatId}:adaptive.fails    → int[] (4)
c:{chatId}:adaptive.probe    → unix ts
```

- Sem tabela nova — o KV genérico já persiste JSON por chave.
- Hash do ref **não** entra na chave: o chat só tem 1 modelo; se o usuário troca o ref `adaptive:` por outro, o estado anterior não se aplica (os índices não significariam nada). Trocar de modelo (qualquer ref) reseta a reputação — aceitável e simples.
- `r:{runId}:adaptive.active` espelha o modelo efetivo da run (mesmo papel do `fusion.active`) — auditoria + `PartialContent`/replay.

## 5. Integração — ChatRunExecutor

Reusa a máquina do fusion-chain (SPEC-20261110) como caso especial de cadeia:

```
ApplyAdaptivePoolAsync(run, request, user, chat, ct):
    pool = AdaptiveModelRef.Parse(request.Model)         // ref novo
    state = KvAdaptive.Load(chat.Id)                     // sticky/fails/probe
    start = state.StartIndex()                           // RF-001 + probe (RF-008)
    valida pool[i] no catálogo + budget (por candidato)
    kv r:{run}:adaptive.active = pool[start]
    request.Model = pool[start]
    chainPos = start                                     // reusa state do executor

HandleRunFailureAsync (ramo adaptive):
    IsProviderFailure(ex) && ToolMessages vazio && chainPos+1 < pool.Count
        → kv adaptive.fails[chainPos]++ , probeAt = now+30min
        → chainPos++, request.Model = pool[chainPos], retry
    cadeia esgotada → run falha

RunCompleted (sucesso):
    sticky = chainPos; fails[chainPos] = 0; persistir
```

`AdaptiveModelRef` (contrato novo, `src/OpenWebUI.Application/Contracts/`):
`Prefix="adaptive:"`, `TryParse → IReadOnlyList<string> pool` (2–4), valida por-candidato igual `ChainModelRef`.

**Fase events** (visíveis no SSE + polling-fallback):
- `adaptive` — `{"phase":"fallback","from":i,"to":j,"model":"…"}` em cada avanço;
- `adaptive` — `{"phase":"sticky","model":"…","reason":"fails[0]=3"}` quando o start deixa de ser o preferido;
- `adaptive` — `{"phase":"probe","model":"…"}` quando a sonda re-tenta o preferido.

## 6. UI — ModelSelector

Segundo membro fixo da família **Adaptive** no seletor (hoje só `fusion:`):

```
[ Adaptativo ]
  (•) Adaptive Pool   — até 4 modelos, 1º preferido, fallback ordenado
  ( ) Fusion          — leader/sidekick
```

- Seleção "Adaptive Pool" → 4 selects empilhados ordenados (`model 1` … `model 4`), slots 3 e 4 opcionais (`models.fusion_none` reaproveitado ou nova chave `models.adaptive_none`).
- Serializa `adaptive:{m1,m2,m3,m4}` omitindo vazios; re-parse idempotente na volta.
- Browser storage: `webui.model` (ref) + `webui.adaptive.1`…`webui.adaptive.4` (restauração).
- i18n: chaves novas nos 8 locales (`models.adaptive_pool`, `models.adaptive_pool_hint`, `models.adaptive_slot` + `{0}`, hint de ordem/sticky).

## 7. Validações e gates (mesmos invariantes do fusion-chain)

- Todo candidato validado contra o catálogo efetivo do usuário (inclui gate de visibilidade SPEC-20261104 — `adaptive:` com modelo desabilitado → erro claro, não fallback silencioso para o próximo).
- Budget/tier checado por candidato antes do 1º uso na run.
- Gate de side-effect: avanço só antes de qualquer tool executar.
- `IsModelGateBlockedAsync` por candidato no stream direto.

## 8. Caminhos degradados (fora de run)

| Caminho | Resolução |
|---|---|
| `/api/chat/completions` (playground) | `adaptive:` → primeiro membro do pool |
| Tasks auxiliares (title/tags/etc.) | `adaptive:` → primeiro membro |
| `delegate_task` sem `model:` explícito | herda o ref `adaptive:` **inteiro** — sub-agente tem pool próprio, estado por chat-filho (kv `r:{parentRun}:adaptive.child` = o ref) |
| `LeaderOrSelf`/metadata | primeiro membro |

## 9. Fora de escopo

- `fusion:` dentro de `adaptive:` (e vice-versa) — refs virtuais aninhados não são suportados; candidatos são ids concretos/`auto:`/`combo:`.
- Estado adaptativo global (cross-chat) — decidido por-chat (RF-010).
- Reordenação automática do pool por latência/custo — só reputação por falhas.
- Expor `adaptive.sticky/fails` na UI (read-only) — observabilidade via fases basta no v1.

## 10. Critérios de aceite

1. `adaptive:{a,b,c,d}` com `a` forçado a 429 (mock) → run completa em `b`; evento de fase `adaptive fallback` emitido.
2. Duas runs seguidas com `a` falho: run1 `a→b` (start=0), run2 `a→b` (start=0 — `fails[0]=1<3`, volta ao 1º).
3. `fails[0]` atinge K=3 → run seguinte começa direto no sticky (`b`), sem tentar `a`; evento `sticky`.
4. `probeAt` expirado → run tenta `a` de novo; se `a` ok → `sticky=a`, `fails` zerado.
5. Tool executada antes da falha → **não** avança (side-effect gate).
6. `adaptive:{x}` (1 candidato) e `adaptive:{}` rejeitados na validação.
7. `adaptive:{a,a,b}` deduplica para `{a,b}`.
8. Playground com `adaptive:{a,b}` usa `a`; `TaskEndpoints` idem.
9. Sub-agente herda `adaptive:` completo e falha também avança nele.
10. Estado persiste entre restarts (KV) — run pós-restart honra `sticky`.
11. Parsing/edge-cases com cobertura de contrato; e2e com mock forçando falhas por token de modelo (fixture existente `FailModelToken`).

## 11. Questões abertas (decidir na aprovação)

- **K** (falhas consecutivas antes de sticky): proposto **3**. Alternativa: 1 (sticky imediato) — mais agressivo, menos tentativas ao preferido.
- **Janela da sonda**: proposto **30 min**. Alternativas: fixo 15/60min, backoff exponencial (30→60→120… cap 4h).
- **Escopo do estado**: proposto por-chat. Alternativa global por usuário (uma reputação para todos os chats do ref) — mais simples de raciocinar, menos adaptado a contextos distintos.
- **Nome de exibição**: "Adaptive Pool" no seletor — colide minimamente com a família "Adaptativo" existente (que hoje só abriga o Fusion); alternativa "Adaptive (4 modelos)". Ref `adaptive:` mantido conforme pedido.
