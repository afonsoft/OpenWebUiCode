using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de arquivos, espelhando <c>/api/v1/files</c> do Open WebUI.</summary>
public static class FileEndpoints
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".html", ".htm", ".log",
        ".yaml", ".yml", ".toml", ".ini", ".cs", ".py", ".js", ".ts", ".tsx", ".jsx",
        ".css", ".scss", ".sql", ".sh", ".ps1", ".java", ".go", ".rs", ".rb", ".php",
    };

    /// <summary>Mapeia as rotas de arquivos.</summary>
    public static RouteGroupBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/files").RequireAuthorization()
            .RequirePermission(PermissionService.WorkspaceFiles);

        group.MapPost("/", UploadFileAsync).DisableAntiforgery();
        group.MapGet("/", ListFilesAsync);
        group.MapGet("/{id}", GetFileMetaAsync);
        group.MapGet("/{id}/content", GetFileContentAsync);
        group.MapDelete("/{id}", DeleteFileAsync);

        return group;
    }

    /// <summary>Cap de upload do endpoint (S5693): 100 MB de arquivo +
    /// folga para o envelope multipart — aplicado como limite de request
    /// body antes do form ser lido.</summary>
    private const long MaxUploadBytes = 100L * 1024 * 1024;
    private const long MaxUploadRequestBytes = MaxUploadBytes + (1L << 20);

    private static async Task<IResult> UploadFileAsync(
        HttpContext http, AppDbContext db, IWebHostEnvironment env, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        // Cap por-request antes de bufar o body — sem isso o default do
        // Kestrel (~30MB) já cortava com 413 genérico e bodies grandes
        // eram lidos até o limite do form antes de qualquer check.
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()
            ?.MaxRequestBodySize = MaxUploadRequestBytes;

        var form = await http.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new { detail = "Nenhum arquivo enviado." });
        }

        if (file.Length > MaxUploadBytes)
        {
            return Results.BadRequest(new { detail = "Arquivo excede 100 MB." });
        }

        var id = Guid.NewGuid().ToString();
        var uploadDir = Path.Join(DataPaths.Root(env.ContentRootPath), "uploads", user.Id);
        Directory.CreateDirectory(uploadDir);

        var safeName = Path.GetFileName(file.FileName);
        var storagePath = Path.Join(uploadDir, $"{id}_{safeName}");

        await using (var fs = File.Create(storagePath))
        {
            await file.CopyToAsync(fs, ct);
        }

        string? extracted = null;
        var ext = Path.GetExtension(safeName);
        if (TextExtensions.Contains(ext) && file.Length < 5 * 1024 * 1024)
        {
            extracted = await File.ReadAllTextAsync(storagePath, ct);
            if (extracted.Length > 50_000)
            {
                extracted = extracted[..50_000];
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entry = new FileEntry
        {
            Id = id,
            UserId = user.Id,
            Filename = safeName,
            ContentType = file.ContentType,
            StoragePath = storagePath,
            Size = file.Length,
            ExtractedText = extracted,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Files.Add(entry);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(entry));
    }

    private static async Task<IResult> ListFilesAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var files = await db.Files.AsNoTracking()
            .Where(f => f.UserId == user.Id)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(ct);

        return Results.Ok(files.Select(ToResponse).ToList());
    }

    private static async Task<IResult> GetFileMetaAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var file = await db.Files.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id && f.UserId == user!.Id, ct);
        return file is null ? Results.NotFound() : Results.Ok(ToResponse(file));
    }

    private static async Task<IResult> GetFileContentAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var file = await db.Files.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id && f.UserId == user!.Id, ct);
        if (file is null)
        {
            return Results.NotFound();
        }

        if (file.ExtractedText is not null)
        {
            return Results.Ok(new FileContentResponse(file.ExtractedText));
        }

        if (File.Exists(file.StoragePath))
        {
            return Results.File(
                File.OpenRead(file.StoragePath),
                file.ContentType ?? "application/octet-stream",
                file.Filename);
        }

        return Results.NotFound();
    }

    private static async Task<IResult> DeleteFileAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == id && f.UserId == user!.Id, ct);
        if (file is null)
        {
            return Results.NotFound();
        }

        if (File.Exists(file.StoragePath))
        {
            File.Delete(file.StoragePath);
        }

        // Deleção do arquivo remove seus chunks vetoriais (regra RAG).
        await db.EmbeddingChunks.Where(c => c.FileId == file.Id)
            .ExecuteDeleteAsync(ct);
        db.Files.Remove(file);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    /// <summary>Monta contexto textual dos arquivos informados para injetar na completion.</summary>
    internal static async Task<string> BuildFileContextAsync(
        IReadOnlyList<string> fileIds, string userId, AppDbContext db, CancellationToken ct)
    {
        if (fileIds.Count == 0)
        {
            return string.Empty;
        }

        var files = await db.Files.AsNoTracking()
            .Where(f => fileIds.Contains(f.Id) && f.UserId == userId && f.ExtractedText != null)
            .ToListAsync(ct);

        if (files.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Contexto de arquivos enviados pelo usuário:");
        foreach (var file in files)
        {
            sb.AppendLine($"\n--- {file.Filename} ---");
            sb.AppendLine(file.ExtractedText);
        }

        return sb.ToString();
    }

    private static FileResponse ToResponse(FileEntry f) =>
        new(f.Id, f.Filename, f.ContentType, f.Size, f.ExtractedText is not null, f.CreatedAt);
}
