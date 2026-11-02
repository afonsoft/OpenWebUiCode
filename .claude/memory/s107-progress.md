# S107 progress (>7 params) — branch fix/sonar-s107-20261102

Remaining (SonarCloud, 2026-11-02) — apply private sealed record + destructure:
- ChatRunExecutor.cs: 231 (10p), 282 (11p), 763 (11p), 858 (8p), 1203 (9p), 1243 (8p), 2548 (8p), 3454 (9p)
- ChatPipeline.cs: 1163 (8p), 1234 (8p), 1277 (9p), 1319 (11p)
- ApiEndpoints.cs: 289 (13p), 462 (12p)
- DelegateTaskBuiltinTool.cs: 607 (12p — PersistentSidekickDispatchAsync)
- ProviderService.cs: 935 (9p), 1105 (8p), 1489 (10p), 2245 (12p), 2305 (9p), 2931 (9p)
DONE (all 24): UnifiedDiff.EmitHunk (HunkCtx), TeamBuiltinTools.MutateTaskAsync (TaskMutation), GatewayInboundService.EnqueueRunAsync (InboundRun), ChatRunExecutor (AttemptCtx/ToolLoopRunCtx/CallbackDeps/RunCompletion/RunFailure/ToolResultEvent + OnToolResult), ChatPipeline (ToolCallLoopCtx x4), ApiEndpoints (IServiceProvider handler + OpenApiLoopDeps), ProviderService (ProviderStreamCtx/OpenAiStreamCtx/AnthropicStreamCtx x6), DelegateTaskBuiltinTool (SidekickDispatch). PR #615 open — verify SonarCloud S107 -> 0 post-merge.
