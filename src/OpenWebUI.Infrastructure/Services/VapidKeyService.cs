using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Identidade VAPID usada para assinar os envios Web Push
/// (SPEC-20261007-chat-notifications RF-004). Ordem de resolução:
/// 1) env <c>VAPID__PUBLIC_KEY</c>/<c>VAPID__PRIVATE_KEY</c>/<c>VAPID__SUBJECT</c>;
/// 2) ConfigEntry <c>vapid.public_key</c>/<c>vapid.private_key</c>/<c>vapid.subject</c>;
/// 3) par novo gerado uma única vez (ECDSA P-256, público no formato
///    0x04‖X‖Y base64url) e persistido — rotacionar a chave privada
///    orfanaria todas as subscriptions salvas.
/// Sem chaves em nenhuma fonte e persistência indisponível, o push fica
/// desligado silenciosamente.
/// </summary>
public sealed class VapidKeyService(
    ConfigService config,
    ILogger<VapidKeyService> logger)
{
    /// <summary>Chaves de configuração (env e ConfigEntry).</summary>
    public const string PublicKeyKey = "vapid.public_key";

    /// <summary>Chave da chave privada VAPID.</summary>
    public const string PrivateKeyKey = "vapid.private_key";

    /// <summary>Chave do subject (mailto:) VAPID.</summary>
    public const string SubjectKey = "vapid.subject";

    private const string DefaultSubject = "mailto:admin@localhost";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Identidade resolvida — nunca contém campos vazios.</summary>
    public sealed record Keys(string PublicKey, string PrivateKey, string Subject);

    /// <summary>Chave pública somente para entrega ao navegador, ou null se push desligado.</summary>
    public async Task<string?> GetPublicKeyAsync(CancellationToken ct = default) =>
        (await GetOrCreateAsync(ct))?.PublicKey;

    /// <summary>
    /// Resolve (ou gera e persiste) a identidade VAPID. Retorna null quando
    /// não há chaves configuradas e a persistência falha.
    /// </summary>
    public async Task<Keys?> GetOrCreateAsync(CancellationToken ct = default)
    {
        var subject = ReadEnv("VAPID__SUBJECT")
            ?? await config.GetAsync<string?>(SubjectKey, null, ct)
            ?? DefaultSubject;
        var publicKey = ReadEnv("VAPID__PUBLIC_KEY")
            ?? await config.GetAsync<string?>(PublicKeyKey, null, ct);
        var privateKey = ReadEnv("VAPID__PRIVATE_KEY")
            ?? await config.GetAsync<string?>(PrivateKeyKey, null, ct);
        if (!string.IsNullOrWhiteSpace(publicKey) && !string.IsNullOrWhiteSpace(privateKey))
        {
            return new Keys(publicKey, privateKey, subject);
        }

        await _gate.WaitAsync(ct);
        try
        {
            // Releitura dentro do gate — outro escopo pode ter vencido a corrida.
            publicKey = await config.GetAsync<string?>(PublicKeyKey, null, ct);
            privateKey = await config.GetAsync<string?>(PrivateKeyKey, null, ct);
            if (!string.IsNullOrWhiteSpace(publicKey) && !string.IsNullOrWhiteSpace(privateKey))
            {
                return new Keys(publicKey, privateKey, subject);
            }

            var (generatedPublic, generatedPrivate) = Generate();
            await config.SetAsync(PublicKeyKey, generatedPublic, ct);
            await config.SetAsync(PrivateKeyKey, generatedPrivate, ct);
            await config.SetAsync(SubjectKey, subject, ct);
            logger.LogInformation("Par VAPID gerado e persistido — identidade Web Push estável.");
            return new Keys(generatedPublic, generatedPrivate, subject);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível gerar/persistir o par VAPID — Web Push desligado.");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? ReadEnv(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static (string PublicKey, string PrivateKey) Generate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
        var publicBytes = new byte[65];
        publicBytes[0] = 0x04;
        parameters.Q.X!.CopyTo(publicBytes, 1);
        parameters.Q.Y!.CopyTo(publicBytes, 33);
        return (
            Base64Url.EncodeToString(publicBytes),
            Base64Url.EncodeToString(parameters.D!));
    }
}
