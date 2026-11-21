# SPEC-20261124 — Watchdog global de runs (sweep periódico)

| Campo | Valor |
|-------|-------|
| Status | Em execução |
| Autor | Devin (agente) |
| Data | 2026-11-24 |
| Relacionadas | SPEC-20261016-run-recovery (RF-003/005), SPEC-20261121-dev-pipeline |

## Contexto

`ChatRunDispatcher.SweepOrphansAsync` classifica e repara runs órfãs
**apenas no boot** do processo (`ExecuteAsync`, antes dos workers). Com o
servidor no ar, uma run que fica `Running` com heartbeat morto nunca é
reavaliada até o próximo restart:

- heartbeat task da run morreu (falta não tratada) enquanto o executor
  segue pendurado;
- co-instância morreu em cenário multi-instância (o sweep do boot não a
  vê, pois a morte aconteceu depois);
- run reivindicada num sweep teve o `TryWrite` para a fila descartado
  (canal cheio) — `Running` + dono = nós, mas nunca executa.

O `DevPipelineService` tem detecção de stall própria para as fases da
pipeline; esta SPEC é a rede de segurança **global** para qualquer run —
equivalente ao `schedule{IsDaemon}` monitor do agy.

## Requisitos funcionais

### RF-001 Loop periódico

`WatchdogLoopAsync` roda junto aos workers do dispatcher: a cada
`ChatRuns:WatchdogSeconds` (default **60**; `<= 0` desliga) chama
`SweepOrphansAsync` no modo periódico.

### RF-002 Varredura periódica não re-enfileira `queued`

No boot, `queued → requeue` faz sentido (a fila em memória morre com o
processo). Em varredura periódica, uma run `Queued` no banco está
necessariamente espelhada no canal (enqueue dá `TryWrite` antes de
persistir o estado; o worker vira `Running` ao consumir). Re-enfileirar a
cada tick duplicaria entradas no canal bounded (`DropWrite`), podendo
derrubar enqueues legítimos. Modo periódico **pula `Queued`**.

### RF-003 Run stale ativa na instância local → `TryStop`, nunca requeue

Uma run `Running` stale cujo `OwnerInstance == InstanceId` **e** que ainda
consta no registro de execução local (`_runCancels`) está viva neste
processo — o heartbeat dela morreu. Reivindicar + re-enfileirar faria um
segundo worker executá-la em dobro. Ação: `TryStop` (cancelamento
graceful — o executor finaliza o status pelo caminho normal).

### RF-004 Escalada: interrupt forçado após N ticks

Se a run segue `Running` + stale após `ChatRuns:WatchdogInterruptAfter`
(default **3**) observações consecutivas do watchdog (≈3min sem reagir
ao stop), ela é marcada `interrupted` no banco — observadores deixam de
vê-la como viva. Se o executor eventualmente concluir, o status final dele
prevalece (verdade do resultado).

### RF-005 Demais classificações inalteradas

- `paused` → skip (pausa é estado do usuário);
- heartbeat fresco de qualquer dono → skip (viva);
- stale fora do registro local (dono morto ou fantasma) → com snapshot:
  reivindica (`OwnerInstance`, `HeartbeatAt`) + requeue; sem snapshot:
  `interrupted` — igual ao sweep do boot.

### RF-006 Telemetria

Log `Warning` por tick com contagens quando houver ação:
`stopped-local`, `interrupted` (inclui forçadas), `resumed`,
`requeued` (só no boot).

## Não-objetivos

- Não substitui a detecção de stall da pipeline (que *substitui* a fase
  semanticamente); o watchdog lida com o nível infraestrutura.
- Não introduz novo hosted service — o loop vive no `ChatRunDispatcher`,
  que já é `BackgroundService`.

## Critérios de aceite

- [ ] Tick periódico não altera runs `Queued` no banco nem as re-enfileira.
- [ ] Run `Running` stale + dono local + ativa → `TryStop` chamado
      (cts cancelado), sem requeue nem mudança direta de status.
- [ ] Após `WatchdogInterruptAfter` ticks com a run ainda stale →
      status `interrupted` no banco.
- [ ] Run `Running` stale de dono morto continua resumível/interrompida
      exatamente como no boot (regressão dos testes `RunRecoveryTests`).
- [ ] `ChatRuns:WatchdogSeconds <= 0` desliga o loop sem afetar o boot.

## Testes

`tests/OpenWebUI.Api.Tests/RunWatchdogTests.cs` (fixture nova, reusa os
helpers do `RunRecoveryTests`):

1. `Watchdog_Periodico_NaoReenfileiraQueued`
2. `Watchdog_RunLocalAtiva_StopEnaoReenfileira`
3. `Watchdog_RunLocalZumbi_InterruptAposNTicks`
4. `Watchdog_StaleDonoMorto_ResumivelReivindicada` (regressão do caminho
   de retomada no modo periódico)

Hooks internos para teste: `ChatRunDispatcher.CurrentInstanceId`
(`internal`) e `RegisterRunCancelForTest` (`internal`).
