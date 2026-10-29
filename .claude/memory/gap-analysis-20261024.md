# gap-analysis-20261024 — auditoria gateway + bookkeeping

## Fontes

| Fonte | Estado |
| --- | --- |
| `.specs/` (12 em voo), `docs/specs/` (152 arquivadas) | present |
| `CLAUDE.md`, `AGENTS.md`, `.claude/rules/`, memory | present |
| gh auth `afonsoft/OpenWebUiCode` | ok |
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

## Proposta (aprovada pelo usuário)

Itens 1–8 corrigidos na própria branch `feature/Devin-20261023-messaging-gateway` antes do merge. Item 9 = bookkeeping direto. Item 10 = fila do orchestrator → criar issues.

## Resultado pós-aprovação

| Item | Resolução | Evidência |
| --- | --- | --- |
| 1 session-reuse | `Unpair` remove `GatewaySession` via `ExecuteDeleteAsync` | `GatewayEndpoints.cs` + teste `Unpair_Remove_Sessao_E_Requer_Novo_Codigo` |
| 2 pair-bruteforce | `RateLimitService` dedicado `PairAttemptLimit(10/600s,5→300s)`; `RegisterFailedAttemptAsync` morto removido | `GatewayEndpoints.cs`, `GatewayPairingTests.cs` |
| 3 ratelimit-off | `WebhookRateLimit(30/60s,5→300s)` dedicado sempre ativo, independente de `ratelimit.enabled` | `GatewayEndpoints.cs` + `GatewayRateLimitTests.cs` |
| 4 restricted-memory | `memory_save`/`memory_learn`/`ask_user` removidos da allowlist | `Entities.cs` |
| 5 payload-cap | `Content-Length`>256KB→413 imediato; `ReadBoundedAsync` bound em chunked | `GatewayEndpoints.cs` + teste bounded |
| 6 instincts-extraction | gateway chats excluídos salvo `instincts.includeGateway=true` | `InstinctExtractor.cs` + teste opt-in |
| 7 reply-infoleak | `run.Error` não vaza (msg genérica); skip `Blocked`/`!Enabled` | `GatewayReplyService.cs` + testes |
| 8 delete-orphans | delete cascata deliveries→sessions→peers→channel + `secrets.DeleteUserAsync` | `GatewayEndpoints.cs` + teste `SecretRef=""` |
| 9 dup SPEC | `.specs/SPEC-…-sdd-orchestrator-prompts.md` removido; `check-spec-status.py` detecta dup cross-dir | commit `98c26ff` |
| 10 SPECs órfãs | issues #506 (epic) + #507–#513 (slices) criadas | gh issues |

Commits: `98c26ff` (fixes), `2a05a7f`/`db53581` (cobertura). CI do #505 em `db53581`.

## Follow-up 2026-10-26 (épica #506, builder)

Verificação pós-merge em `main` (`144096f`):

- **Gateway security (1–8):** fixes presentes em `main` — squash-merge via #497 (`6e92264`); `98c26ff`/`db53581` não aparecem como ancestors porque o squash reescreve. Evidência em `GatewayEndpoints.cs`: `WebhookRateLimit`/`PairAttemptLimit` sempre ativos, cascata `ExecuteDeleteAsync` no delete, `ReadBoundedAsync` + cap por `Content-Length`, allowlist restricted sem `memory_save`/`memory_learn`/`ask_user`.
- **Slices:** #507–#511 merged + SPECs arquivadas; #512 merged (`90188d4`, PR #525); #513 `in-progress` (agent-devin) — segue aberta.
- **Bookkeeping corrigido nesta rodada** (SPEC-20261026-spec-bookkeeping):
  - `docs/specs/SPEC-20261018-model-variants.md`: estava arquivada com `Status: Approved` → `Completed` + `Delivered` (drift guard falhava).
  - `SPEC-20261018-references.md`: `.specs/` → `docs/specs/` + `Completed` + `Delivered`.
- **Pendências reportadas ao orchestrator (fora do escopo do builder):**
  - Issue #512 remota segue `open`/`todo` apesar do merge — fechar/etiquetar.
  - Labels stale em issues fechadas: #507 (`todo`,`in_progress`), #511 (`todo`).
  - `check-spec-status.py` é guard local/pre-PR — não roda em CI (diferente dos 3 client guards em `ci-build-test.yml`).
