using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Endpoints de retrieval avançado (/api/v1/retrieval): process de
/// file/text/url/youtube, web search com engines configuráveis,
/// config admin (mascarada) e reset de índice/uploads.
/// </summary>
public static class RetrievalEndpoints
{
    private const string NoIndexTextMessage = "Não foi possível indexar — sem texto ou provider de embedding.";
    private const string PlainTextMediaType = "text/plain";
    private const string MaskedSecret = "********";
    /// <summary>Mapeia o grupo /api/v1/retrieval.</summary>
    public static void MapRetrievalEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/retrieval").RequireAuthorization();
        group.MapPost("/process/file", ProcessFileAsync);
        group.MapPost("/process/text", ProcessTextAsync);
        group.MapPost("/process/url", ProcessUrlAsync);
        group.MapPost("/process/youtube", ProcessYoutubeAsync);
        group.MapPost("/process/web/search", WebSearchAsync);
        group.MapGet("/config", GetConfigAsync);
        group.MapPost("/config/update", UpdateConfigAsync);
        group.MapPost("/reset/{target}", ResetAsync);
    }

    private static async Task<IResult> ProcessFileAsync(
        [FromBody] ProcessFileRequest request,
        HttpContext http, AppDbContext db, RagService rag, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var file = await db.Files.FirstOrDefaultAsync(
            f => f.Id == request.FileId && f.UserId == user.Id, ct);
        if (file is null)
        {
            return Results.NotFound(new { detail = "Arquivo não encontrado." });
        }

        var ok = await rag.IndexFileAsync(file, ct);
        if (!ok)
        {
            return Results.BadRequest(
                new { detail = NoIndexTextMessage });
        }
        await LinkCollectionAsync(request.CollectionId, file.Id, user.Id, db, ct);
        var chunks = await db.EmbeddingChunks.CountAsync(c => c.FileId == file.Id, ct);
        return Results.Ok(new ProcessResponse(file.Id, file.Filename, chunks));
    }

    private static async Task<IResult> ProcessTextAsync(
        [FromBody] ProcessTextRequest request,
        HttpContext http, AppDbContext db, RagService rag, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return Results.BadRequest(new { detail = "Conteúdo é obrigatório." });
        }

        var file = new FileEntry
        {
            UserId = user.Id,
            Filename = string.IsNullOrWhiteSpace(request.Name)
                ? $"texto-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.txt"
                : request.Name.Trim(),
            ContentType = PlainTextMediaType,
            ExtractedText = request.Content,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        db.Files.Add(file);
        await db.SaveChangesAsync(ct);

        var ok = await rag.IndexFileAsync(file, ct);
        if (!ok)
        {
            return Results.BadRequest(
                new { detail = NoIndexTextMessage });
        }
        await LinkCollectionAsync(request.CollectionId, file.Id, user.Id, db, ct);
        var chunks = await db.EmbeddingChunks.CountAsync(c => c.FileId == file.Id, ct);
        return Results.Ok(new ProcessResponse(file.Id, file.Filename, chunks));
    }

    private static async Task<IResult> ProcessUrlAsync(
        [FromBody] ProcessUrlRequest request,
        HttpContext http, AppDbContext db,
        WebLoaderService loader, RagService rag, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        string text;
        try
        {
            text = await loader.LoadTextAsync(request.Url, ct);
        }
        catch (WebLoaderException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return Results.BadRequest(new { detail = "Nenhum texto extraído da URL." });
        }

        var file = new FileEntry
        {
            UserId = user.Id,
            Filename = new Uri(request.Url).Host + ".txt",
            ContentType = PlainTextMediaType,
            ExtractedText = text,
            SourceUrl = request.Url,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        db.Files.Add(file);
        await db.SaveChangesAsync(ct);

        var ok = await rag.IndexFileAsync(file, ct);
        if (!ok)
        {
            return Results.BadRequest(
                new { detail = NoIndexTextMessage });
        }
        await LinkCollectionAsync(request.CollectionId, file.Id, user.Id, db, ct);
        var chunks = await db.EmbeddingChunks.CountAsync(c => c.FileId == file.Id, ct);
        return Results.Ok(new ProcessResponse(file.Id, file.Filename, chunks));
    }

    private static async Task<IResult> ProcessYoutubeAsync(
        [FromBody] ProcessYoutubeRequest request,
        HttpContext http, AppDbContext db,
        WebLoaderService loader, RagService rag, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        string text;
        try
        {
            text = await loader.LoadYoutubeTranscriptAsync(request.Url, ct);
        }
        catch (WebLoaderException ex)
        {
            return Results.BadRequest(new { detail = ex.Message });
        }

        var file = new FileEntry
        {
            UserId = user.Id,
            Filename = $"youtube-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.txt",
            ContentType = PlainTextMediaType,
            ExtractedText = text,
            SourceUrl = request.Url,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        db.Files.Add(file);
        await db.SaveChangesAsync(ct);

        var ok = await rag.IndexFileAsync(file, ct);
        if (!ok)
        {
            return Results.BadRequest(
                new { detail = NoIndexTextMessage });
        }
        await LinkCollectionAsync(request.CollectionId, file.Id, user.Id, db, ct);
        var chunks = await db.EmbeddingChunks.CountAsync(c => c.FileId == file.Id, ct);
        return Results.Ok(new ProcessResponse(file.Id, file.Filename, chunks));
    }

    private static async Task<IResult> WebSearchAsync(
        [FromBody] WebSearchRequest request,
        HttpContext http, AppDbContext db,
        WebSearchService search, RagService rag, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return Results.BadRequest(new { detail = "Query é obrigatória." });
        }

        List<WebSearchResult>? results;
        try
        {
            results = await search.SearchAsync(request.Query, request.Count ?? 5, ct);
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                "Engine de busca indisponível.", statusCode: StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout interno do HttpClient (engine não respondeu) → 502, não 500.
            return Results.Problem(
                "Engine de busca não respondeu a tempo.", statusCode: StatusCodes.Status502BadGateway);
        }
        if (results is null)
        {
            return Results.Problem(
                "Web search desabilitada — configure uma engine em retrieval.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var dedup = results
            .Where(r => !string.IsNullOrWhiteSpace(r.Url))
            .GroupBy(r => r.Url)
            .Select(g => g.First())
            .ToList();

        ProcessResponse? processed = null;
        if (request.ProcessResults && dedup.Count > 0)
        {
            var content = string.Join("\n\n",
                dedup.Select(r => $"# {r.Title}\n{r.Url}\n{r.Snippet}"));
            var file = new FileEntry
            {
                UserId = user.Id,
                Filename = $"web-search-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.txt",
                ContentType = PlainTextMediaType,
                ExtractedText = content,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            db.Files.Add(file);
            await db.SaveChangesAsync(ct);
            await rag.IndexFileAsync(file, ct);
            var chunks = await db.EmbeddingChunks.CountAsync(c => c.FileId == file.Id, ct);
            processed = new ProcessResponse(file.Id, file.Filename, chunks);
        }

        return Results.Ok(new { results = dedup, processed });
    }

    private static async Task<IResult> GetConfigAsync(
        HttpContext http, AppDbContext db, ConfigService config, CancellationToken ct)
    {
        if (!await IsAdminAsync(http, db, ct))
        {
            return Results.Forbid();
        }

        var cfg = await config.GetAsync("retrieval.config", RetrievalConfig.Default, ct);
        return Results.Ok(cfg.Masked());
    }

    private static async Task<IResult> UpdateConfigAsync(
        [FromBody] RetrievalConfig request,
        HttpContext http, AppDbContext db, ConfigService config, CancellationToken ct)
    {
        if (!await IsAdminAsync(http, db, ct))
        {
            return Results.Forbid();
        }

        var valid = request.Engine is "none" or "auto" or "searxng" or "duckduckgo" or "tavily"
            or "brave" or "google_pse" or "jina" or "exa" or "kagi" or "perplexity"
            && request.TopK is > 0 and <= 100
            && request.ChunkSize is >= 100 and <= 10000
            && request.ChunkOverlap >= 0
            && request.ChunkOverlap < request.ChunkSize
            && request.HybridWeight is >= 0 and <= 1
            && request.RerankEngine is "local" or "external";
        if (!valid)
        {
            return Results.BadRequest(new { detail = "Configuração de retrieval inválida." });
        }

        // Chave mascarada (MaskedSecret) ou nula mantém o valor já persistido.
        var current = await config.GetAsync("retrieval.config", RetrievalConfig.Default, ct);
        var merged = request with
        {
            BraveApiKey = request.BraveApiKey is null or MaskedSecret ? current.BraveApiKey : request.BraveApiKey,
            TavilyApiKey = request.TavilyApiKey is null or MaskedSecret ? current.TavilyApiKey : request.TavilyApiKey,
            GooglePseApiKey = request.GooglePseApiKey is null or MaskedSecret ? current.GooglePseApiKey : request.GooglePseApiKey,
            JinaApiKey = request.JinaApiKey is null or MaskedSecret ? current.JinaApiKey : request.JinaApiKey,
            ExaApiKey = request.ExaApiKey is null or MaskedSecret ? current.ExaApiKey : request.ExaApiKey,
            KagiApiKey = request.KagiApiKey is null or MaskedSecret ? current.KagiApiKey : request.KagiApiKey,
            PerplexityApiKey = request.PerplexityApiKey is null or MaskedSecret ? current.PerplexityApiKey : request.PerplexityApiKey,
            RerankExternalApiKey = request.RerankExternalApiKey is null or MaskedSecret ? current.RerankExternalApiKey : request.RerankExternalApiKey,
        };
        await config.SetAsync("retrieval.config", merged, ct);
        return Results.Ok(merged.Masked());
    }

    private static async Task<IResult> ResetAsync(
        string target, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!await IsAdminAsync(http, db, ct))
        {
            return Results.Forbid();
        }

        switch (target)
        {
            case "db":
                await db.EmbeddingChunks.ExecuteDeleteAsync(ct);
                return Results.Ok(new { status = true });
            case "uploads":
                await db.EmbeddingChunks.ExecuteDeleteAsync(ct);
                await db.KnowledgeFiles.ExecuteDeleteAsync(ct);
                await db.Files.ExecuteDeleteAsync(ct);
                return Results.Ok(new { status = true });
            default:
                return Results.NotFound(new { detail = "Alvo de reset inválido (db|uploads)." });
        }
    }

    private static async Task LinkCollectionAsync(
        string? collectionId, string fileId, string userId, AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return;
        }

        var collection = await db.KnowledgeCollections.FirstOrDefaultAsync(
            k => k.Id == collectionId && k.UserId == userId, ct);
        if (collection is null)
        {
            return;
        }

        var exists = await db.KnowledgeFiles.AnyAsync(
            f => f.CollectionId == collectionId && f.FileId == fileId, ct);
        if (!exists)
        {
            db.KnowledgeFiles.Add(new KnowledgeFile
            {
                CollectionId = collectionId,
                FileId = fileId,
                AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task<bool> IsAdminAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        return user?.Role == UserRoles.Admin;
    }
}
