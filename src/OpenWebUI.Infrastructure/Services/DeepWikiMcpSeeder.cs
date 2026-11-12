using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Provisiona o <b>DeepWiki MCP</b> como servidor global default
/// (<c>UserId=null</c>, habilitado) — contexto de repositório para todos
/// os usuários desde o primeiro boot, sem configuração manual.
///
/// <list type="bullet">
///   <item>Primeira execução: cria <see cref="McpServer"/> com id estável
///   <c>deepwiki</c> (<c>http</c>, <c>https://mcp.deepwiki.com/mcp</c>) e
///   marca <c>seed.deepwiki-mcp</c> — se o admin remover o server depois,
///   o marker impede recriação (respeita a decisão).</item>
///   <item>A cada boot com o server habilitado e <b>sem tools
///   materializadas</b>: tenta <c>RefreshToolsAsync</c> — self-healing
///   para boot offline (LastError gravado pelo refresh; próximo boot
///   tenta de novo).</item>
///   <item>Overlay por usuário (<c>u:{id}:mcp-disabled</c>) continua
///   permitindo esconder o server individualmente.</item>
/// </list>
/// </summary>
public sealed class DeepWikiMcpSeeder(
    IServiceScopeFactory scopeFactory,
    ILogger<DeepWikiMcpSeeder>? logger = null) : BackgroundService
{
    /// <summary>Id estável do servidor — tools virtuais ficam <c>mcp://deepwiki/{tool}</c>.</summary>
    public const string ServerId = "deepwiki";

    /// <summary>Endpoint público do DeepWiki MCP (streamable HTTP, sem auth).</summary>
    public const string ServerUrl = "https://mcp.deepwiki.com/mcp";

    /// <summary>Marker config — gravado após o primeiro seed; impede recriação se o admin remover.</summary>
    public const string SeedMarkerKey = "seed.deepwiki-mcp";

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(4);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var mcp = scope.ServiceProvider.GetRequiredService<McpClientService>();

            var server = await db.McpServers
                .FirstOrDefaultAsync(s => s.Id == ServerId, stoppingToken);
            if (server is null)
            {
                if (await db.ConfigEntries.FindAsync(
                        [SeedMarkerKey], stoppingToken) is not null)
                {
                    return;   // admin removeu após o seed — não recria.
                }
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                server = new McpServer
                {
                    Id = ServerId,
                    Name = "deepwiki",
                    Transport = "http",
                    Url = ServerUrl,
                    ArgsJson = "[]",
                    EnvJson = "[]",
                    HeadersJson = "{}",
                    Enabled = true,
                    UserId = null,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.McpServers.Add(server);
                db.ConfigEntries.Add(new ConfigEntry
                {
                    Key = SeedMarkerKey,
                    ValueJson = "true",
                    UpdatedAt = now,
                });
                await db.SaveChangesAsync(stoppingToken);
                logger?.LogInformation(
                    "DeepWiki MCP provisionado como servidor global default.");
            }

            // Refresh só quando não há tools materializadas — cobre boot
            // offline e atualização de catálogo sem re-descobrir a cada boot.
            var hasTools = await db.Tools.AsNoTracking()
                .AnyAsync(t => t.Url.StartsWith(
                    $"{McpClientService.VirtualUrlPrefix}{ServerId}/"), stoppingToken);
            if (server.Enabled && !hasTools)
            {
                try
                {
                    var count = await mcp.RefreshToolsAsync(server, stoppingToken);
                    logger?.LogInformation(
                        "DeepWiki MCP: {Count} tools materializadas.", count);
                }
                catch (InvalidOperationException ex)
                {
                    logger?.LogWarning(ex,
                        "DeepWiki MCP: refresh falhou — nova tentativa no próximo boot.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown — normal.
        }
        catch (Exception ex)
        {
            // Seeder é best-effort: nunca derruba a aplicação.
            logger?.LogWarning(ex, "DeepWikiMcpSeeder falhou — ignorado.");
        }
    }
}
