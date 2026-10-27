using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// SPEC-20261018-themes-custom: temas customizados por usuário (upload JSON
/// validado), tema de instância via <c>WEBUI_THEME_JSON</c> e persistência da
/// seleção ativa (<c>themeId</c> + <c>mode</c>) em <c>User.SettingsJson</c>.
/// </summary>
public static class ThemeEndpoints
{
    private static readonly string[] ValidModes = ["system", "light", "dark"];

    /// <summary>Mapeia as rotas de temas.</summary>
    public static RouteGroupBuilder MapThemeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/themes").RequireAuthorization();

        group.MapGet("/", ListThemesAsync);
        group.MapPost("/", CreateThemeAsync);
        group.MapPut("/active", SetActiveThemeAsync);
        group.MapDelete("/{id}", DeleteThemeAsync);

        return group;
    }

    private static async Task<IResult> ListThemesAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var rows = await db.UserThemes.AsNoTracking()
            .Where(t => t.UserId == user.Id)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        var customs = rows
            .Select(ToResponse)
            .Where(t => t is not null)
            .ToList();

        var (activeId, activeMode) = ReadSelection(user);

        return Results.Ok(new
        {
            instance = ReadInstanceTheme(),
            customs,
            active = new { themeId = activeId, mode = activeMode },
        });
    }

    private static async Task<IResult> CreateThemeAsync(
        JsonElement request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (request.GetRawText().Length > ThemeValidator.MaxJsonBytes)
        {
            return Results.BadRequest(new { error = $"tema excede {ThemeValidator.MaxJsonBytes / 1024}KB" });
        }

        if (!ThemeValidator.TryParse(request, out var theme, out var error) || theme is null)
        {
            return Results.BadRequest(new { error });
        }

        var exists = await db.UserThemes
            .AnyAsync(t => t.UserId == user.Id && t.ThemeId == theme.Id, ct);
        if (exists)
        {
            return Results.Conflict(new { error = $"já existe um tema '{theme.Id}'" });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var row = new UserTheme
        {
            UserId = user.Id,
            ThemeId = theme.Id,
            Name = theme.Name,
            Json = request.GetRawText(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.UserThemes.Add(row);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new UserThemeResponse(row.Id, row.ThemeId, row.Name, row.Active, theme));
    }

    private static async Task<IResult> SetActiveThemeAsync(
        SetActiveThemeRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var mode = request.Mode?.Trim().ToLowerInvariant();
        if (!ValidModes.Contains(mode))
        {
            return Results.BadRequest(new { error = "mode deve ser system|light|dark" });
        }

        var themeId = request.ThemeId?.Trim();
        if (string.IsNullOrWhiteSpace(themeId) ||
            themeId.Length > 64 ||
            (!string.Equals(themeId, "default", StringComparison.Ordinal) &&
             !ThemeValidator.IsValidThemeId(themeId)))
        {
            return Results.BadRequest(new { error = "themeId inválido" });
        }

        var customs = await db.UserThemes
            .Where(t => t.UserId == user.Id)
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var row in customs)
        {
            var active = row.ThemeId == themeId;
            if (row.Active != active)
            {
                row.Active = active;
                row.UpdatedAt = now;
            }
        }

        WriteSelection(user, themeId, mode!);
        user.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { themeId, mode });
    }

    private static async Task<IResult> DeleteThemeAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var row = await db.UserThemes
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == user.Id, ct);
        if (row is null)
        {
            return Results.NotFound();
        }

        var wasActive = row.Active;
        db.UserThemes.Remove(row);

        if (wasActive)
        {
            WriteSelection(user, "default", ReadSelection(user).Mode);
            user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { deleted = true });
    }

    /// <summary>
    /// Tema da instância (<c>WEBUI_THEME_JSON</c>): lido por request para
    /// refletir mudanças de ambiente sem rebuild; inválido vira null.
    /// </summary>
    private static CustomThemeDefinition? ReadInstanceTheme()
    {
        var raw = Environment.GetEnvironmentVariable("WEBUI_THEME_JSON");
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > ThemeValidator.MaxJsonBytes)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return ThemeValidator.TryParse(doc.RootElement, out var theme, out _) ? theme : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string ThemeId, string Mode) ReadSelection(User user)
    {
        try
        {
            using var doc = JsonDocument.Parse(user.SettingsJson);
            if (doc.RootElement.TryGetProperty("theme", out var theme) &&
                theme.ValueKind == JsonValueKind.Object)
            {
                var id = theme.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var mode = theme.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return (id, mode is "system" or "dark" ? mode : "light");
                }
            }
        }
        catch (JsonException)
        {
            // SettingsJson malformado — cai no default.
        }

        return ("default", "system");
    }

    private static void WriteSelection(User user, string themeId, string mode)
    {
        Dictionary<string, JsonElement> settings;
        try
        {
            settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(user.SettingsJson)
                       ?? [];
        }
        catch (JsonException)
        {
            settings = [];
        }

        settings["theme"] = JsonSerializer.SerializeToElement(new { id = themeId, mode });
        user.SettingsJson = JsonSerializer.Serialize(settings);
    }

    private static UserThemeResponse? ToResponse(UserTheme row)
    {
        try
        {
            using var doc = JsonDocument.Parse(row.Json);
            if (!ThemeValidator.TryParse(doc.RootElement, out var theme, out _) || theme is null)
            {
                return null;
            }

            return new UserThemeResponse(row.Id, row.ThemeId, row.Name, row.Active, theme);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
