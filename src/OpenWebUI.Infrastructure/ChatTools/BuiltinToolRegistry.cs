using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Registro das tools built-in do chat (SPEC-20261007-chat-agent-tools):
/// resolve <c>builtin:{name}</c> nos ToolIds do request em
/// <see cref="Tool"/> sintéticos (spec OpenAI + URL <c>builtin://{name}</c>)
/// e executa via <see cref="IBuiltinChatTool"/>. Tools listadas em
/// <c>BuiltinTools:Disabled</c> (csv) são excluídas.
/// </summary>
public sealed class BuiltinToolRegistry(IEnumerable<IBuiltinChatTool> tools, IConfiguration configuration)
{
    /// <summary>Prefixo dos ToolIds que resolvem tools built-in.</summary>
    public const string IdPrefix = "builtin:";

    /// <summary>Prefixo das URLs sintéticas usadas no dispatch do ToolExecutor.</summary>
    public const string UrlPrefix = "builtin://";

    /// <summary>Prefixo do nome de função anunciado ao provider (válido no spec OpenAI).</summary>
    public const string SpecPrefix = "builtin_";

    private readonly Dictionary<string, IBuiltinChatTool> _byName = BuildMap(tools, configuration);

    private static Dictionary<string, IBuiltinChatTool> BuildMap(
        IEnumerable<IBuiltinChatTool> tools, IConfiguration configuration)
    {
        var disabled = (configuration["BuiltinTools:Disabled"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<string, IBuiltinChatTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (disabled.Contains(tool.Name) || disabled.Contains($"{IdPrefix}{tool.Name}"))
            {
                continue;
            }

            map[tool.Name] = tool;
        }

        return map;
    }

    /// <summary>Todas as tools ativas (para o picker de tools).</summary>
    public IReadOnlyCollection<IBuiltinChatTool> All => _byName.Values;

    /// <summary>
    /// Traduz os ToolIds do request: ids <c>builtin:{name}</c> viram
    /// <see cref="Tool"/> sintéticos; demais ids retornam para lookup no DB.
    /// </summary>
    /// <param name="toolIds">Ids pedidos no request.</param>
    /// <param name="dbIds">Saida: ids que são de tools do DB.</param>
    /// <returns>Tools sintéticas das built-ins pedidas e habilitadas.</returns>
    public List<Tool> ResolveIds(IReadOnlyList<string> toolIds, List<string> dbIds)
    {
        var synthetic = new List<Tool>();
        foreach (var id in toolIds)
        {
            if (id.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase)
                && _byName.TryGetValue(id[IdPrefix.Length..], out var tool))
            {
                synthetic.Add(ToSyntheticTool(tool));
            }
            else
            {
                dbIds.Add(id);
            }
        }

        return synthetic;
    }

    /// <summary>Converte uma built-in num <see cref="Tool"/> de catálogo (para o picker).</summary>
    /// <remarks>
    /// O nome da função anunciado ao provider usa <c>builtin_{name}</c>:
    /// o spec OpenAI exige <c>^[a-zA-Z0-9_-]+$</c> e <c>builtin:{name}</c>
    /// fazia providers/modelos emitirem o nome sem prefixo, quebrando o
    /// match no <see cref="Services.ToolExecutor"/>.
    /// </remarks>
    public Tool ToSyntheticTool(IBuiltinChatTool tool) => new()
    {
        Id = $"{IdPrefix}{tool.Name}",
        Name = $"builtin/{tool.Name}",
        Url = $"{UrlPrefix}{tool.Name}",
        SpecJson = JsonSerializer.Serialize(new
        {
            type = "function",
            function = new
            {
                name = $"{SpecPrefix}{tool.Name}",
                description = tool.Description,
                parameters = JsonDocument.Parse(tool.ParametersJson).RootElement,
            },
        }),
        RequiresApproval = tool.RequiresApproval,
        Enabled = true,
    };

    /// <summary>
    /// Executa a built-in apontada pela URL <c>builtin://{name}</c> de um
    /// <see cref="Tool"/> carregado. Null se a URL não for de built-in ou a
    /// tool não existir/estiver desabilitada.
    /// </summary>
    public async Task<BuiltinToolResult?> ExecuteAsync(
        Tool tool, string argumentsJson, BuiltinToolContext context, CancellationToken ct)
    {
        if (tool.Url?.StartsWith(UrlPrefix, StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        var name = tool.Url[UrlPrefix.Length..];
        if (!_byName.TryGetValue(name, out var builtin))
        {
            return new BuiltinToolResult($"Tool built-in '{name}' não existe ou está desabilitada.");
        }

        JsonElement args;
        try
        {
            args = NormalizeArgs(argumentsJson);
        }
        catch (JsonException)
        {
            return new BuiltinToolResult($"Argumentos inválidos para '{name}' — JSON malformado.");
        }

        if (args.ValueKind != JsonValueKind.Object)
        {
            // Args não-objeto (ex.: modelo mandou string/array em vez de
            // objeto) viram erro da tool, nunca exceção — um TryGetProperty
            // no elemento errado encerrava a run inteira como failed.
            return new BuiltinToolResult(
                $"Argumentos inválidos para '{name}' — esperado objeto JSON.");
        }

        return await builtin.ExecuteAsync(args, context, ct);
    }

    /// <summary>
    /// Normaliza o JSON de argumentos da tool call: alguns modelos emitem os
    /// args "double-encoded" — uma string JSON contendo o objeto (ex. Gemini).
    /// Desembrulha até 2 níveis de string; o resto vai cru e o chamador
    /// rejeita se o root final não for objeto.
    /// </summary>
    internal static JsonElement NormalizeArgs(string? argumentsJson)
    {
        var current = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
        JsonElement last = default;
        for (var i = 0; i < 3; i++)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(current);
            }
            catch (JsonException) when (i > 0)
            {
                // Nível interno não é JSON válido — devolve o último que
                // parseou; o chamador rejeita se não for objeto.
                return last;
            }
            using (doc)
            {
                var root = doc.RootElement.Clone();
                last = root;
                if (root.ValueKind != JsonValueKind.String
                    || root.GetString() is not { } inner
                    || string.IsNullOrWhiteSpace(inner))
                {
                    return root;
                }
                current = inner;
            }
        }
        return last;
    }
}
