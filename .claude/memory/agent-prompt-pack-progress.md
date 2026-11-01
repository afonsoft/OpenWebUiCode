# agent-prompt-pack (issue #579) — branch feature/devin-20261031-agent-prompt-pack

Implementado: RF-001 footer tool-result (ChatPipeline.PublishCallResultAsync +
ToolLoopCallbacks.ToolResultFooter, default inline, config AgentLoop:ToolResultFooter);
RF-002 unseen_messages builtin (ChatRunSteer pending→promoted); RF-003 instant_context
(RestrictedExecRunner: rg/readfile/tree → <ANSWER> XML; desvio: single-turn collector,
não sub-agente); RF-004 extend_context (ExtendContextBudget, teto 2, pula 1 compactação);
RF-005 stall_watch (StallWatchdog SHA256 tool+args+output, threshold 4, nudge via
_contextNotes); RF-006 cache_keepalive (WaitApprovalAsync wrapper, ping CompleteAsync,
AgentLoop:CacheKeepaliveSeconds); RF-007 AgentDisciplineContributor (Order 86,
delegation-discipline + verify-once pt-BR). Testes: AgentLoopMechanismsTests 6/6.

# sidekick-orchestration (issue #575) — branch feature/devin-20261031-sidekick-orchestration
persistent+sidekick_id via kv+SidekickGate; explore/general .agent.md builtin;
max_nesting alias de maxDepth; handoff/retention/report-contract no
DelegationPolicyContributor; fg/bg endpoints → Chat.ApprovalPreset
(fg=approve-mutations, bg=allow-readonly). PR #594 CI fix: Sonar new-code
(S3776/S2325/S6608/S8949) corrigido no próprio pack.
