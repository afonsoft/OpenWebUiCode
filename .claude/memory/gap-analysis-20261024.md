# gap-analysis-20261024 — auditoria gateway + bookkeeping

## Fontes

| Fonte | Estado |
| --- | --- |
| `.specs/` (12 em voo), `docs/specs/` (152 arquivadas) | present |
| `CLAUDE.md`, `AGENTS.md`, `.claude/rules/`, memory | present |
| gh auth `afonsoft/open-webui` | ok |
| Sibling skills write-specs/create-issues/orchestrator | present |
| Working tree | clean (`feature/Devin-20261023-messaging-gateway` @ ebf7407) |
| Submodules | none |

Prior run: `gap-analysis-20261017.md` — itens entregues desde então (#400-#404 → specs arquivadas).

## Veredictos (ordem: categoria → prioridade → chave)

| Gap key | Prioridade | Veredicto | Evidência |
| --- | --- | --- | --- |
| GAP-security-gateway-session-reuse | Alta | CONFIRMADO | `GatewayEndpoints.cs:262-266` (Unpair limpa só PairedUserId/PairedAt); `GatewayInboundService.cs:136-143` reusa `GatewaySession`→chat antigo; nenhum código remove sessions. Re-pair p/ outro usuário → mensagens/run do novo dono caem no chat do dono anterior (contexto + sidebar vazam). |
| GAP-security-gateway-pair-bruteforce | Média | CONFIRMADO | `GatewayPairingService.cs:107-123` `RegisterFailedAttemptAsync` é código morto (nunca chamado); `PairingAttempts` nunca incrementa; `/pair` sem rate limit → 1M codes brute-forceável por usuário autenticado na janela de 15min. |
| GAP-security-gateway-ratelimit-default-off | Média | CONFIRMADO | `GatewayEndpoints.cs:315-328` depende de `rateConfig.Enabled`; `RateLimitConfig.Default` = `new(false,…)` (`ConfigContracts.cs:299`) → webhook anônimo nasce sem limite; cada assinatura inválida ainda grava `GatewayDeliveries` (write amplification). SPEC RF-001 exige rate limit. |
| GAP-security-gateway-restricted-memory-write | Média | CONFIRMADO | `Entities.cs:2225-2232` allowlist restricted inclui `memory_save`/`memory_learn` (escrita na memória do dono via injeção) e `ask_user` (peer externo não aprova — approvals web-only). SPEC: "não entra em memória sem opt-in explícito". |
| GAP-security-gateway-payload-cap-postread | Baixa | CONFIRMADO | `GatewayEndpoints.cs:330-339`: `ReadToEndAsync` lê o corpo inteiro antes do check 256KB → POST anônimo multi-MB aloca antes do 413. Mesmo padrão fraco em `AutomationHookEndpoints.cs:234-236` (pré-existente). |
| GAP-security-gateway-instincts-extraction | Baixa | CONFIRMADO | `InstinctExtractor.cs:58-60` filtra só `IsEphemeral`; chats gateway não são efêmeros → conversa de terceiros alimenta extração (mitigação parcial: candidatos ficam `pending` p/ aprovação humana; SPEC pede opt-in explícito). |
| GAP-implementation-gateway-reply-infoleak | Baixa | CONFIRMADO | `GatewayReplyService.cs:47-51` envia `run.Error` verbatim ao peer externo (detalhes internos); não re-checa `peer.Blocked`/`channel.Enabled` na entrega. |
| GAP-implementation-gateway-delete-orphans | Baixa | CONFIRMADO | `DeleteChannelAsync` `ExecuteDeleteAsync` (`GatewayEndpoints.cs:176-180`); migration com 0 FKs → peers/sessions/deliveries órfãos (invisíveis via filtro de dono) + `SecretRef` residual no vault. |
| GAP-bookkeeping-sdd-orchestrator-prompts-dup | Baixa | CONFIRMADO | `.specs/` diz `In implementation`; `docs/specs/` diz `Completed`; issue #491 closed+done. `check-spec-status.py` conta como em voo e não detecta dup cross-dir. |
| GAP-automation-approved-specs-no-issues | — | INCONCLUSIVO | 7 `Approved` sem Ticket/issue: agent-polish, attachments-v2, formatters-catalog, mcp-oauth, model-variants, references, themes-custom (+ policies-admin `In implementation`, pr-review-sweep `Draft`). Fila intencional do orchestrator ou backlog órfão? |

## Rejeitados (verificados, sem gap)

- Assinatura verificada antes de parse/efeitos (`GatewayInboundService.cs:71-77`) ✅
- `FixedTimeEquals` + `SsrfGuard` + chunk≤4000 + fallback plaintext (`TelegramAdapter.cs`) ✅
- Token via `SecretRef`/`UserSecretsService`, nunca em resposta/log ✅
- Wrap `<untrusted_input>` com escape de fechamento (`GatewayInboundService.cs:180-183`) ✅
- `shell_exec` fora do restricted; budget gate cobre runs gateway via executor ✅
- Instinct extractor usa provider real (`CompleteAsync`) ✅
- Gateway desabilitado → 404 ✅

## Proposta (aguardando gate)

Itens 1–8 estão em PR **aberto** (#505) — são achados de revisão pré-merge: recomendado corrigir na própria branch `feature/Devin-20261023-messaging-gateway` antes do merge (fixes pequenos e focados), em vez de spec+issue pós-merge. Item 9 = ação de bookkeeping direta (mover dup p/ fora de `.specs/`, opcional: ensinar o drift guard a detectar dup cross-dir). Item 10 = pergunta ao usuário.
