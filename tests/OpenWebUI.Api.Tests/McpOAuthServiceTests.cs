using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261018-mcp-oauth: authorize URL (PKCE + discovery + DCR),
/// callback code→token persistido, refresh automático com mutex,
/// marker required e disconnect.
/// </summary>
[TestFixture, IsolateEnvironment]
public class McpOAuthServiceTests
{
    private string _root = null!;
    private AppDbContext _db = null!;
    private ConfigService _config = null!;
    private UserSecretsService _secrets = null!;
    private HybridCache _cache = null!;
    private AsHandler _as = null!;
    private McpOAuthService _oauth = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-oauth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Join(_root, "t.db")}").Options);
        DatabaseMigrator.MigrateAsync(_db).GetAwaiter().GetResult();
        _cache = TestCache.Create();
        _config = new ConfigService(_db, _cache);
        _secrets = new UserSecretsService(_config);
        _as = new AsHandler();
        _oauth = new McpOAuthService(_secrets, _cache,
            new StubFactory(_as), NullLogger<McpOAuthService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _as.Dispose();
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private static McpServer Server() => new()
    {
        Id = "s1", Name = "gh", Transport = "http",
        Url = "http://as.local/mcp", UserId = "u1",
    };

    [Test]
    public async Task Authorize_MontaUrlComPkceEDcr()
    {
        var url = await _oauth.BeginAuthorizeAsync(
            "u1", Server(), "http://app/cb", default);
        Assert.Multiple(() =>
        {
            Assert.That(url, Does.StartWith("http://as.local/authorize?"));
            Assert.That(url, Does.Contain("code_challenge="));
            Assert.That(url, Does.Contain("code_challenge_method=S256"));
            Assert.That(url, Does.Contain("client_id=dcr-123"));
            Assert.That(_as.RegisterCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Complete_TrocaCodeEPersisteToken()
    {
        var url = await _oauth.BeginAuthorizeAsync(
            "u1", Server(), "http://app/cb", default);
        var state = url!.Split("state=")[1].Split('&')[0];
        var done = await _oauth.CompleteAsync(state!, "code-1", default);
        Assert.Multiple(() =>
        {
            Assert.That(done, Is.EqualTo(("u1", "s1")));
            Assert.That(_as.LastVerifier, Has.Length.GreaterThan(40));
        });
        Assert.That(await _oauth.GetAuthStateAsync("u1", "s1", default),
            Is.EqualTo("authorized"));
        Assert.That(await _oauth.GetAccessTokenAsync("u1", "s1", default),
            Is.EqualTo("access-1"));
    }

    [Test]
    public async Task Complete_StateInvalido_RetornaNull() =>
        Assert.That(await _oauth.CompleteAsync("bad", "x", default), Is.Null);

    [Test]
    public async Task GetAccessToken_Expirado_FazRefresh()
    {
        // Semeia token expirado com refresh.
        await _secrets.SetUserAsync("u1", "MCP_OAUTH_S1", JsonSerializer.Serialize(
            new
            {
                accessToken = "old", refreshToken = "r1",
                tokenEndpoint = "http://as.local/token",
                clientId = "c1", clientSecret = (string?)null,
                expiresAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
            }), default);
        _as.NextAccessToken = "access-2";

        var token = await _oauth.GetAccessTokenAsync("u1", "s1", default);
        Assert.Multiple(() =>
        {
            Assert.That(token, Is.EqualTo("access-2"));
            Assert.That(_as.RefreshCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task GetAccessToken_RefreshFalha_MarcaRequired()
    {
        await _secrets.SetUserAsync("u1", "MCP_OAUTH_S1", JsonSerializer.Serialize(
            new
            {
                accessToken = "old", refreshToken = "r1",
                tokenEndpoint = "http://as.local/token-fail",
                clientId = "c1", clientSecret = (string?)null,
                expiresAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
            }), default);

        Assert.That(await _oauth.GetAccessTokenAsync("u1", "s1", default), Is.Null);
        Assert.That(await _oauth.GetAuthStateAsync("u1", "s1", default),
            Is.EqualTo("required"));
    }

    [Test]
    public async Task Disconnect_LimpaTudo()
    {
        var url = await _oauth.BeginAuthorizeAsync(
            "u1", Server(), "http://app/cb", default);
        var state = url!.Split("state=")[1].Split('&')[0];
        await _oauth.CompleteAsync(state!, "code-1", default);
        await _oauth.DisconnectAsync("u1", "s1", default);
        Assert.That(await _oauth.GetAuthStateAsync("u1", "s1", default),
            Is.EqualTo("none"));
        Assert.That(await _oauth.GetAccessTokenAsync("u1", "s1", default), Is.Null);
    }

    [Test]
    public async Task Authorize_SemMetadata_RetornaNull()
    {
        var server = Server();
        server.Url = "http://noauth.local/mcp";
        Assert.That(await _oauth.BeginAuthorizeAsync(
            "u1", server, "http://app/cb", default), Is.Null);
    }

    /// <summary>Authorization server fake: metadata + DCR + token.</summary>
    private sealed class AsHandler : HttpMessageHandler
    {
        public int RegisterCalls, RefreshCalls;
        public string? LastVerifier;
        public string NextAccessToken = "access-1";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.Host != "as.local")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            var path = req.RequestUri!.AbsolutePath;
            object? body = path switch
            {
                "/.well-known/oauth-protected-resource" => new
                {
                    resource = "http://as.local/mcp",
                    authorization_servers = new[] { "http://as.local" },
                },
                "/.well-known/oauth-authorization-server" => new
                {
                    issuer = "http://as.local",
                    authorization_endpoint = "http://as.local/authorize",
                    token_endpoint = "http://as.local/token",
                    registration_endpoint = "http://as.local/register",
                },
                "/register" when req.Method == HttpMethod.Post => Reg(),
                "/token" => await Token(req),
                _ => (object?)null,
            };
            if (body is null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };

            object Reg()
            {
                RegisterCalls++;
                return new { client_id = "dcr-123" };
            }

            async Task<object> Token(HttpRequestMessage r)
            {
                var form = await r.Content!.ReadAsStringAsync(ct);
                var fields = form.Split('&').Select(p => p.Split('='))
                    .ToDictionary(p => Uri.UnescapeDataString(p[0]),
                        p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));
                if (fields["grant_type"] == "refresh_token")
                {
                    RefreshCalls++;
                }
                else
                {
                    LastVerifier = fields["code_verifier"];
                }
                return new
                {
                    access_token = NextAccessToken,
                    refresh_token = "r1", expires_in = 3600,
                    token_type = "Bearer",
                };
            }
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
