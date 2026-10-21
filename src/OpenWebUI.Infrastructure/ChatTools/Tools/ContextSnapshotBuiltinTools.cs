using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:context_save</c> — grava uma nova versão do snapshot de
/// contexto do chat (SPEC-20261020-context-snapshots RF-005): escopo é o
/// chat da run (sem parâmetro repo — o <c>RepoKey</c> vem do binding
/// vigente), payload validado pelo <see cref="ContextSnapshotService"/>
/// (kinds, tamanhos, control chars, segredos — nunca persistidos).
/// Opcionalmente pré-compacta os itens antes de salvar.
/// </summary>
public sealed class ContextSaveBuiltinTool(
    ContextSnapshotService snapshots,
    ContextBoundaryService boundaries,
    IContextSummarizer summarizer,
    ModelRoleResolver roles) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "context_save";

    /// <inheritdoc />
    public string Description =>
        "Save a versioned snapshot of working context for this chat "
            + "(decisions, notes, passages, tool outputs). Survives reloads "
            + "and compaction — use context_recovery to read it back.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "label": {
              "type": "string",
              "description": "Optional snapshot label."
            },
            "items": {
              "type": "array",
              "description": "Context items to persist.",
              "items": {
                "type": "object",
                "properties": {
                  "kind": {
                    "type": "string",
                    "enum": ["decision", "note", "passage", "tool_output", "summary"]
                  },
                  "title": { "type": "string" },
                  "content": { "type": "string" },
                  "score": { "type": "number" }
                },
                "required": ["kind", "content"]
              }
            },
            "compact": {
              "type": "string",
              "enum": ["none", "elision", "elision+summary"],
              "description": "Pre-compact items before saving (default 'none')."
            }
          },
          "required": ["items"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Write;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        if (!await boundaries.EnabledAsync(ct))
        {
            return ContextSnapshotTools.Disabled();
        }
        if (string.IsNullOrEmpty(context.ChatId))
        {
            return ContextSnapshotTools.NoChat(Name);
        }

        var limits = await snapshots.LimitsAsync(ct);
        var strategy = ContextSnapshotTools.StrategyArg(
            args, "compact", out var strategyError);
        if (strategyError is not null)
        {
            return ContextSnapshotTools.Error(Name, strategyError);
        }

        if (!args.TryGetProperty("items", out var itemsEl))
        {
            return ContextSnapshotTools.Error(Name,
                new ContextSnapshotService.SnapshotError("invalid_payload", "'items' é obrigatório."));
        }
        var (items, parseError) = ContextSnapshotService.ParseItems(itemsEl, limits.MaxItems);
        if (parseError is not null)
        {
            return ContextSnapshotTools.Error(Name, parseError);
        }

        var label = args.TryGetProperty("label", out var labelEl)
            && labelEl.ValueKind == JsonValueKind.String
                ? labelEl.GetString() : null;

        var compacted = false;
        var pruned = 0;
        if (!string.Equals(strategy, "none", StringComparison.Ordinal))
        {
            var model = await ContextSnapshotTools.SummaryModelAsync(roles, context, ct);
            var (compactedItems, stats) = await ContextSnapshotCompactor.ApplyBudgetAsync(
                items!, strategy, limits.ThresholdTokens, summarizer, model,
                context.UserId, context.ChatId,
                TimeSpan.FromSeconds(limits.SummaryTimeoutSeconds), ct);
            items = compactedItems;
            compacted = true;
            pruned = stats.DroppedCount + stats.ElidedCount;
        }

        var (snap, error) = await snapshots.SaveAsync(
            context.ChatId, context.UserId, label, items!, strategy, compacted, ct);
        if (error is not null)
        {
            return ContextSnapshotTools.Error(Name, error);
        }

        var payload = new
        {
            id = snap!.Id,
            version = snap.Version,
            tokenEstimate = snap.TokenEstimate,
            compacted,
            strategy,
            pruned,
        };
        return new BuiltinToolResult(JsonSerializer.Serialize(payload), payload);
    }
}

/// <summary>
/// <c>builtin:context_recovery</c> — devolve um snapshot do chat (último
/// ou versão pedida) aplicando poda/elision sobre uma CÓPIA dentro do
/// budget — o snapshot persistido nunca muda
/// (SPEC-20261020-context-snapshots RF-005).
/// </summary>
public sealed class ContextRecoveryBuiltinTool(
    ContextSnapshotService snapshots,
    ContextBoundaryService boundaries) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "context_recovery";

    /// <inheritdoc />
    public string Description =>
        "Recover a saved context snapshot for this chat — latest by "
            + "default, or a specific 'version'. Elision is applied to a "
            + "copy to fit 'tokenBudget'; the stored snapshot never changes.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "version": {
              "type": "integer",
              "description": "Snapshot version (default: latest)."
            },
            "tokenBudget": {
              "type": "integer",
              "description": "Token budget for the returned copy (default 16000)."
            }
          }
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        if (!await boundaries.EnabledAsync(ct))
        {
            return ContextSnapshotTools.Disabled();
        }
        if (string.IsNullOrEmpty(context.ChatId))
        {
            return ContextSnapshotTools.NoChat(Name);
        }

        var limits = await snapshots.LimitsAsync(ct);
        var budget = ContextSnapshotTools.BudgetArg(args, limits);

        var snap = args.TryGetProperty("version", out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var version)
                ? await snapshots.GetAsync(context.ChatId, version, ct)
                : await snapshots.LatestAsync(context.ChatId, ct);
        if (snap is null)
        {
            return ContextSnapshotTools.Error(Name,
                new ContextSnapshotService.SnapshotError("context_not_found",
                    "nenhum snapshot neste chat para a versão pedida."));
        }

        var items = ContextSnapshotService.DeserializeItems(snap.PayloadJson);
        var (fitted, stats) = await ContextSnapshotCompactor.ApplyBudgetAsync(
            items, "elision", budget, summarizer: null, model: null,
            userId: null, context.ChatId,
            TimeSpan.FromSeconds(limits.SummaryTimeoutSeconds), ct);

        var payload = new
        {
            version = snap.Version,
            items = fitted.Select(ContextSnapshotTools.ToJson).ToArray(),
            stats = ContextSnapshotTools.StatsJson(stats),
            withinBudget = stats.WithinBudget,
        };
        return new BuiltinToolResult(JsonSerializer.Serialize(payload), payload);
    }
}

/// <summary>
/// <c>builtin:context_compact</c> — compacta itens fornecidos ou um
/// snapshot salvo (exatamente uma origem) sob budget, com estratégia
/// none|elision|elision+summary, e opcionalmente grava o resultado como
/// nova versão (SPEC-20261020-context-snapshots RF-005). O tier é
/// <see cref="ApprovalTier.Write"/> mesmo com <c>save=false</c> — o
/// contrato é estático e a tool pode escrever.
/// </summary>
public sealed class ContextCompactBuiltinTool(
    ContextSnapshotService snapshots,
    ContextBoundaryService boundaries,
    IContextSummarizer summarizer,
    ModelRoleResolver roles) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "context_compact";

    /// <inheritdoc />
    public string Description =>
        "Compact context items (or a saved snapshot via 'fromVersion') "
            + "under 'tokenBudget' using none|elision|elision+summary. "
            + "Set 'save' to persist the result as a new version.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "items": {
              "type": "array",
              "description": "Items to compact (mutually exclusive with 'fromVersion').",
              "items": {
                "type": "object",
                "properties": {
                  "kind": {
                    "type": "string",
                    "enum": ["decision", "note", "passage", "tool_output", "summary"]
                  },
                  "title": { "type": "string" },
                  "content": { "type": "string" },
                  "score": { "type": "number" }
                },
                "required": ["kind", "content"]
              }
            },
            "fromVersion": {
              "type": "integer",
              "description": "Compact a saved snapshot version (mutually exclusive with 'items')."
            },
            "strategy": {
              "type": "string",
              "enum": ["none", "elision", "elision+summary"],
              "description": "Compaction strategy (default 'elision+summary')."
            },
            "tokenBudget": {
              "type": "integer",
              "description": "Token budget (default 16000)."
            },
            "save": {
              "type": "boolean",
              "description": "Persist the compacted items as a new version (default false)."
            }
          }
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Write;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        if (!await boundaries.EnabledAsync(ct))
        {
            return ContextSnapshotTools.Disabled();
        }
        if (string.IsNullOrEmpty(context.ChatId))
        {
            return ContextSnapshotTools.NoChat(Name);
        }

        var limits = await snapshots.LimitsAsync(ct);
        var strategy = ContextSnapshotTools.StrategyArg(
            args, "strategy", out var strategyError, "elision+summary");
        if (strategyError is not null)
        {
            return ContextSnapshotTools.Error(Name, strategyError);
        }
        var budget = ContextSnapshotTools.BudgetArg(args, limits);

        // Exatamente uma origem (RF-005): items XOR fromVersion.
        var hasItems = args.TryGetProperty("items", out var itemsEl)
            && itemsEl.ValueKind != JsonValueKind.Null;
        var fromVersion = 0;
        var hasVersion = args.TryGetProperty("fromVersion", out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out fromVersion);
        if (hasItems == hasVersion)
        {
            return ContextSnapshotTools.Error(Name,
                new ContextSnapshotService.SnapshotError("invalid_payload",
                    "informe exatamente uma origem: 'items' ou 'fromVersion'."));
        }

        List<ContextSnapshotService.SnapshotItem> items;
        if (hasVersion)
        {
            var snap = await snapshots.GetAsync(context.ChatId, fromVersion, ct);
            if (snap is null)
            {
                return ContextSnapshotTools.Error(Name,
                    new ContextSnapshotService.SnapshotError("context_not_found",
                        $"versão {fromVersion} não existe neste chat."));
            }
            items = ContextSnapshotService.DeserializeItems(snap.PayloadJson);
        }
        else
        {
            var (parsed, parseError) = ContextSnapshotService.ParseItems(itemsEl, limits.MaxItems);
            if (parseError is not null)
            {
                return ContextSnapshotTools.Error(Name, parseError);
            }
            items = parsed!;
        }

        var model = await ContextSnapshotTools.SummaryModelAsync(roles, context, ct);
        var (compacted, stats) = await ContextSnapshotCompactor.ApplyBudgetAsync(
            items, strategy, budget, summarizer, model, context.UserId,
            context.ChatId, TimeSpan.FromSeconds(limits.SummaryTimeoutSeconds), ct);

        int? savedVersion = null;
        if (args.TryGetProperty("save", out var saveEl) && saveEl.ValueKind == JsonValueKind.True)
        {
            var (snap, error) = await snapshots.SaveAsync(
                context.ChatId, context.UserId, label: null, compacted,
                strategy, compacted: true, ct);
            if (error is not null)
            {
                return ContextSnapshotTools.Error(Name, error);
            }
            savedVersion = snap!.Version;
        }

        var payload = new
        {
            compacted = compacted.Select(ContextSnapshotTools.ToJson).ToArray(),
            stats = ContextSnapshotTools.StatsJson(stats),
            savedVersion,
        };
        return new BuiltinToolResult(JsonSerializer.Serialize(payload), payload);
    }
}

/// <summary>Helpers compartilhados das tools de snapshot (args, erros, payload).</summary>
internal static class ContextSnapshotTools
{
    /// <summary>Resultado padrão de gate desligado (contexttools.enabled=false).</summary>
    internal static BuiltinToolResult Disabled() =>
        new("Tools de contexto estão desabilitadas pelo administrador.");

    /// <summary>Resultado padrão quando não há chat persistido (playground).</summary>
    internal static BuiltinToolResult NoChat(string name) =>
        new($"{name} requer chat persistido — indisponível no playground.");

    /// <summary>Erro estável (RF-005): código na mensagem + structured {error}.</summary>
    internal static BuiltinToolResult Error(
        string tool, ContextSnapshotService.SnapshotError error) =>
        new($"{tool} falhou: {error.Code} — {error.Detail}",
            new { error = error.Code });

    /// <summary>Estratégia do arg (com default); inválida → invalid_payload.</summary>
    internal static string StrategyArg(
        JsonElement args, string property,
        out ContextSnapshotService.SnapshotError? error, string fallback = "none")
    {
        error = null;
        if (!args.TryGetProperty(property, out var s) || s.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }
        var value = s.GetString() ?? fallback;
        if (!ContextSnapshotCompactor.Strategies.Contains(value))
        {
            error = new ContextSnapshotService.SnapshotError(
                "invalid_payload", $"'{property}' deve ser none|elision|elision+summary.");
        }
        return value;
    }

    /// <summary>tokenBudget com clamp 256–threshold_tokens; default 16 000 (RF-006).</summary>
    internal static int BudgetArg(JsonElement args, ContextSnapshotService.Limits limits)
    {
        var budget = args.TryGetProperty("tokenBudget", out var b)
            && b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var v)
                ? v : 16_000;
        return Math.Clamp(budget, 256, limits.ThresholdTokens);
    }

    /// <summary>
    /// Modelo para sumarização (RF-008): papel "memory" resolve sobre o
    /// modelo da run como fallback; vazio quando nada resolve.
    /// </summary>
    internal static async Task<string> SummaryModelAsync(
        ModelRoleResolver roles, BuiltinToolContext context, CancellationToken ct) =>
        await roles.ResolveAsync("memory", context.UserId, context.Model ?? "", ct);

    /// <summary>Item → objeto JSON do payload de saída.</summary>
    internal static object ToJson(ContextSnapshotService.SnapshotItem i) => new
    {
        kind = i.Kind,
        title = i.Title,
        content = i.Content,
        score = i.Score,
        elided = i.Elided,
    };

    /// <summary>Stats → objeto JSON do payload de saída (RF-005).</summary>
    internal static object StatsJson(ContextSnapshotCompactor.CompactionStats s) => new
    {
        tokensBefore = s.TokensBefore,
        tokensAfter = s.TokensAfter,
        droppedCount = s.DroppedCount,
        elidedCount = s.ElidedCount,
        summaryUsed = s.SummaryUsed,
        summaryFallback = s.SummaryFallback,
    };
}
