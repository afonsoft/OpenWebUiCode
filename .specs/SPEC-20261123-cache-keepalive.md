# SPEC-20261123-cache-keepalive — keep-alive de prompt cache entre turnos

**Status**: Proposta

## Motivação

Portado do Devin CLI (`affogato/src/cache_keepalive{,_bucket,_head,_inputs,_policy}.rs`
— **4 arquivos dedicados** a manter o prompt cache quente entre turnos). O
cache ephemeral do Anthropic tem TTL ~5min: turnos espaçados perdem o
`cache_read` e pagam input cheio de novo.

Nós já emitimos `cache_control ephemeral` por modelo
(`ProviderService.cs:2298` + knob `cacheFormat`), mas não há keep-alive:
chat com pausas >5min re-cria o cache do zero — custo de input e latência
maiores no turno seguinte.

## Design

- `CacheKeepalivePolicy` por provider/modelo: quando o modelo da run usa
  `cache_control` com TTL conhecido (Anthropic ephemeral = 300s), e a run
  está **idle aguardando usuário** (não ocupada com run ativa), um timer
  agenda um mini-turno de keep-alive ~20s antes do TTL expirar.
- O mini-turno é um ping de cache: request mínima com o **mesmo prefixo
  cacheado** (system + tools, sem nova mensagem de usuário — max_tokens=1,
  conteúdo sintético mínimo que não entra no histórico) só para renovar o
  TTL. Não gera mensagem visível, não roda tools, não conta como step.
- Limites de custo: no máximo N=6 keep-alives consecutivos por idle
  (~30min de cobertura); depois para — renovar indefinidamente custaria
  mais que re-criar. Reseta o contador a cada turno real.
- Config: `cache_keepalive:enabled` (default true para modelos com
  `cacheFormat` ativo), `cache_keepalive:ttl_sec` (default 280),
  `cache_keepalive:max_idle_pings` (default 6). Desliga quando provider não
  reporta `cache_read` (keep-alive sem benefício = custo puro).
- Telemetria: `cache_keepalive_fired` + métrica de `cache_read_tokens` por
  turno — permite medir a economia real (gate do feature flag depois).

## Testes

- `Keepalive_IdleDisparaAntesDoTTL` — run idle 280s → 1 ping emitido com o
  prefixo cacheado, sem mensagem no histórico.
- `Keepalive_TurnoRealReseta` — usuário fala → contador de pings zera.
- `Keepalive_MaxPings_Para` — 6 pings sem turno real → para e loga.
- `Keepalive_SemCacheRead_NoOp` — provider sem `cache_read` no usage →
  política inerte (não dispara).
- `Keepalive_NaoViraMensagem` — transcript do chat não contém o ping.
