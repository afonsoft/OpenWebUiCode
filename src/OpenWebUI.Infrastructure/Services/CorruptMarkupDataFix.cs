using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Data fix retroativo (bug original dos special tokens
/// <c>&lt;|channel|&gt;</c>/<c>&lt;think&gt;</c> — SPEC
/// reasoning-tag-sanitization): mensagens persistidas antes do
/// <see cref="ReasoningMarkupFilter"/> chegaram ao banco com markup de
/// raciocínio e tokens de controle órfãos. Roda uma vez no startup,
/// depois das migrations, e marca a execução em
/// <c>ConfigEntries["datafix:reasoning-markup-v1"]</c> — idempotente
/// (re-run sem o marcador só reprocessa o que ainda estiver sujo).
/// </summary>
public static class CorruptMarkupDataFix
{
    /// <summary>Chave-marcador da execução em ConfigEntries.</summary>
    public const string MarkerKey = "datafix:reasoning-markup-v1";

    /// <summary>
    /// Aplica a limpeza. Devolve o número de mensagens alteradas
    /// (0 quando já marcada ou nada sujo).
    /// </summary>
    public static async Task<int> RunAsync(
        AppDbContext db, CancellationToken ct = default)
    {
        if (await db.ConfigEntries.FindAsync([MarkerKey], ct) is not null)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var count = await FixMessagesAsync(db, now, ct);

        db.ConfigEntries.Add(new ConfigEntry
        {
            Key = MarkerKey,
            ValueJson = JsonSerializer.Serialize(
                new { ranAt = now, fixedCount = count }),
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return count;
    }

    private static async Task<int> FixMessagesAsync(
        AppDbContext db, long now, CancellationToken ct)
    {
        var count = 0;
        // Candidatos: content/versions com marcador ou token órfão —
        // LIKE no banco; a decisão final é do Strip idempotente.
        var candidates = db.ChatMessages
            .Where(m => EF.Functions.Like(m.Content, "%<|%")
                || EF.Functions.Like(m.Content, "%|>%")
                || EF.Functions.Like(m.Content, "%<think>%")
                || EF.Functions.Like(m.VersionsJson, "%<|%")
                || EF.Functions.Like(m.VersionsJson, "%|>%")
                || EF.Functions.Like(m.VersionsJson, "%<think>%"));

        await foreach (var msg in candidates.AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            var changed = false;
            var clean = ReasoningMarkupFilter.Strip(msg.Content);
            if (!string.Equals(clean, msg.Content, StringComparison.Ordinal))
            {
                msg.Content = clean;
                changed = true;
            }

            var versions = StripVersions(msg.VersionsJson);
            if (versions is not null)
            {
                msg.VersionsJson = versions;
                changed = true;
            }

            if (changed)
            {
                count++;
            }
        }

        await db.SaveChangesAsync(ct);
        return count;
    }

    /// <summary>
    /// Reescreve o <c>content</c> de cada versão
    /// (<c>[{content, model, timestamp}]</c>) com o Strip; devolve o JSON
    /// reescrito ou <c>null</c> quando nada mudou/JSON inválido.
    /// </summary>
    private static string? StripVersions(string versionsJson)
    {
        if (string.IsNullOrWhiteSpace(versionsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(versionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var changed = false;
            var items = new List<Dictionary<string, JsonElement>>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var map = el.EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.Clone());
                if (map.TryGetValue("content", out var c)
                    && c.ValueKind == JsonValueKind.String)
                {
                    var clean = ReasoningMarkupFilter.Strip(c.GetString()!);
                    if (clean != c.GetString())
                    {
                        map["content"] = JsonSerializer.SerializeToElement(clean);
                        changed = true;
                    }
                }
                items.Add(map);
            }

            return changed ? JsonSerializer.Serialize(items) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
