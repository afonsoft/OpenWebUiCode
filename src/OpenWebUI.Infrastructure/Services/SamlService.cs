using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using System.IO.Compression;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Login SAML 2.0 SP-initiated (HTTP-POST binding): monta o AuthnRequest,
/// expõe o metadata do SP e valida assertions do IdP (assinatura via
/// <see cref="SignedXml"/>, issuer, janela de validade e audience).
/// Não usa biblioteca externa — o BCL cobre XML-DSig; o XML é parseado
/// com resolução de entidades proibida (anti-XXE).
/// </summary>
public class SamlService(ConfigService config)
{
    private const string Saml2Ns = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string Saml2PNs = "urn:oasis:names:tc:SAML:2.0:protocol";

    /// <summary>Configuração SAML efetiva (valores de config `saml.*`).</summary>
    public async Task<SamlSettings> GetSettingsAsync(CancellationToken ct = default) =>
        new(
            await config.GetAsync("saml.enabled", false, ct),
            await config.GetAsync("saml.idp_sso_url", string.Empty, ct),
            await config.GetAsync("saml.idp_entity_id", string.Empty, ct),
            await config.GetAsync("saml.idp_cert", string.Empty, ct),
            await config.GetAsync("saml.sp_entity_id", string.Empty, ct),
            await config.GetAsync("saml.role_attribute", string.Empty, ct));

    /// <summary>EntityDescriptor do SP (sem chave — AuthnRequests não assinados).</summary>
    public static string BuildMetadata(SamlSettings settings, string entityId, string acsUrl) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata" entityID="{Escape(entityId)}">
          <md:SPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol"
              AuthnRequestsSigned="false" WantAssertionsSigned="true">
            <md:NameIDFormat>urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress</md:NameIDFormat>
            <md:AssertionConsumerService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST"
                Location="{Escape(acsUrl)}" index="0" isDefault="true"/>
          </md:SPSSODescriptor>
        </md:EntityDescriptor>
        """;

    /// <summary>Monta o redirect SP-initiated (AuthnRequest DEFLATE + Base64 + URL-encoded).</summary>
    public static string BuildLoginRedirect(SamlSettings settings, string entityId, string acsUrl)
    {
        var authnRequest = $"""
            <samlp:AuthnRequest xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol"
                xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion"
                ID="_{Guid.NewGuid():N}" Version="2.0"
                IssueInstant="{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}"
                AssertionConsumerServiceURL="{Escape(acsUrl)}"
                ProtocolBinding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST">
              <saml:Issuer>{Escape(entityId)}</saml:Issuer>
            </samlp:AuthnRequest>
            """;
        var bytes = Encoding.UTF8.GetBytes(authnRequest);
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionMode.Compress, leaveOpen: true))
        {
            deflate.Write(bytes);
        }

        var encoded = Uri.EscapeDataString(Convert.ToBase64String(buffer.ToArray()));
        var separator = settings.IdpSsoUrl.Contains('?') ? '&' : '?';
        return $"{settings.IdpSsoUrl}{separator}SAMLRequest={encoded}";
    }

    /// <summary>
    /// Valida uma SAMLResponse (Base64 do POST /acs): assinatura, issuer,
    /// janela NotBefore/NotOnOrAfter e audience. Retorna o usuário extraído
    /// ou null quando qualquer validação falha.
    /// </summary>
    /// <param name="samlResponseBase64">Valor do campo SAMLResponse.</param>
    /// <param name="settings">Configuração SAML.</param>
    /// <param name="expectedAudience">URI esperado em AudienceRestriction (ACS URL ou entity id).</param>
    public static SamlAssertion? ValidateResponse(
        string samlResponseBase64, SamlSettings settings, string expectedAudience)
    {
        XmlDocument doc;
        try
        {
            var xml = Encoding.UTF8.GetString(Convert.FromBase64String(samlResponseBase64));
            doc = LoadSecureXml(xml);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (XmlException)
        {
            return null;
        }

        var assertion = doc.GetElementsByTagName("Assertion", Saml2Ns)
            .OfType<XmlElement>().FirstOrDefault();
        if (assertion is null)
        {
            return null;
        }

        // 1. Assinatura (da Response ou da Assertion) contra o cert do IdP.
        if (!ValidateSignature(doc, settings.IdpCert))
        {
            return null;
        }

        // 2. Issuer
        var issuer = assertion.GetElementsByTagName("Issuer", Saml2Ns)
            .OfType<XmlElement>().FirstOrDefault()?.InnerText;
        if (!string.IsNullOrEmpty(settings.IdpEntityId)
            && !string.Equals(issuer, settings.IdpEntityId, StringComparison.Ordinal))
        {
            return null;
        }

        // 3. Janela de validade + audience
        var conditions = assertion.GetElementsByTagName("Conditions", Saml2Ns)
            .OfType<XmlElement>().FirstOrDefault();
        if (conditions is not null)
        {
            var now = DateTime.UtcNow;
            var notBefore = ParseInstant(conditions.GetAttribute("NotBefore"));
            var notOnOrAfter = ParseInstant(conditions.GetAttribute("NotOnOrAfter"));
            if ((notBefore is not null && now < notBefore.Value.AddMinutes(-1))
                || (notOnOrAfter is not null && now >= notOnOrAfter.Value.AddMinutes(1)))
            {
                return null;
            }

            var audiences = conditions.GetElementsByTagName("Audience", Saml2Ns)
                .OfType<XmlElement>().Select(e => e.InnerText).ToList();
            if (audiences.Count > 0
                && !audiences.Any(a => string.Equals(
                    a, expectedAudience, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a, settings.SpEntityId, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
        }

        var nameId = assertion.GetElementsByTagName("NameID", Saml2Ns)
            .OfType<XmlElement>().FirstOrDefault()?.InnerText;
        if (string.IsNullOrWhiteSpace(nameId))
        {
            return null;
        }

        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attr in assertion.GetElementsByTagName("Attribute", Saml2Ns).OfType<XmlElement>())
        {
            var name = attr.GetAttribute("Name");
            var value = attr.GetElementsByTagName("AttributeValue", Saml2Ns)
                .OfType<XmlElement>().FirstOrDefault()?.InnerText;
            if (!string.IsNullOrEmpty(name) && value is not null)
            {
                attributes[name] = value;
            }
        }

        var email = FirstValue(attributes, "email", "mail", "emailaddress",
            "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress") ?? nameId;
        var displayName = FirstValue(attributes, "name", "displayname", "displayName",
            "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name");
        var role = string.IsNullOrEmpty(settings.RoleAttribute)
            ? null : FirstValue(attributes, settings.RoleAttribute);

        return new SamlAssertion(nameId, email, displayName, role);
    }

    private static bool ValidateSignature(XmlDocument doc, string certPem)
    {
        if (string.IsNullOrWhiteSpace(certPem))
        {
            return false;
        }

        X509Certificate2 cert;
        try
        {
            cert = certPem.Contains("BEGIN CERTIFICATE")
                ? X509Certificate2.CreateFromPem(certPem)
                : X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certPem));
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
        catch (FormatException) { return false; }

        var signature = doc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)
            .OfType<XmlElement>().FirstOrDefault();
        if (signature is null)
        {
            return false;
        }

        try
        {
            var signedXml = new SignedXml(doc);
            signedXml.LoadXml(signature);
            return signedXml.CheckSignature(cert, verifySignatureOnly: true);
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
        catch (System.Xml.XmlException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static XmlDocument LoadSecureXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        doc.Load(reader);
        return doc;
    }

    private static DateTime? ParseInstant(string value) =>
        DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime() : null;

    private static string? FirstValue(Dictionary<string, string> attrs, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (attrs.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;");
}

/// <summary>Configuração SAML resolvida do config.</summary>
public sealed record SamlSettings(
    bool Enabled, string IdpSsoUrl, string IdpEntityId, string IdpCert,
    string SpEntityId, string RoleAttribute);

/// <summary>Dados extraídos de uma assertion SAML válida.</summary>
public sealed record SamlAssertion(
    string NameId, string Email, string? DisplayName, string? Role);
