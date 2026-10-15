using Novell.Directory.Ldap;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Resultado de um bind LDAP bem-sucedido.</summary>
/// <param name="Email">E-mail do usuário (atributo configurado).</param>
/// <param name="Name">Nome de exibição (atributo configurado).</param>
public sealed record LdapIdentity(string? Email, string? Name);

/// <summary>
/// Autenticação via bind LDAP simples. Configuração por variáveis de ambiente:
/// <c>LDAP_SERVER</c> (obrigatória), <c>LDAP_PORT</c> (389), <c>LDAP_USER_DN_TEMPLATE</c>
/// (ex.: <c>uid={username},ou=users,dc=example,dc=com</c>),
/// <c>LDAP_MAIL_ATTRIBUTE</c> (mail), <c>LDAP_NAME_ATTRIBUTE</c> (cn),
/// <c>LDAP_SEARCH_BASE</c> (opcional; vazio = usa o próprio DN do usuário como base).
/// </summary>
public static class LdapService
{
    /// <summary>LDAP está configurado (servidor + template de DN definidos).</summary>
    public static bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LDAP_SERVER"))
        && !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("LDAP_USER_DN_TEMPLATE"));

    /// <summary>
    /// Tenta autenticar via bind LDAP. Retorna a identidade em caso de sucesso,
    /// <c>null</c> quando LDAP está desabilitado ou o bind falha (sem vazar detalhes).
    /// </summary>
    public static async Task<LdapIdentity?> TryBindAsync(
        string username, string password, CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var server = Environment.GetEnvironmentVariable("LDAP_SERVER")!;
        var port = int.TryParse(Environment.GetEnvironmentVariable("LDAP_PORT"), out var p)
            ? p
            : LdapConnection.DefaultPort;
        var template = Environment.GetEnvironmentVariable("LDAP_USER_DN_TEMPLATE")!;
        var mailAttribute = Environment.GetEnvironmentVariable("LDAP_MAIL_ATTRIBUTE") ?? "mail";
        var nameAttribute = Environment.GetEnvironmentVariable("LDAP_NAME_ATTRIBUTE") ?? "cn";
        var searchBase = Environment.GetEnvironmentVariable("LDAP_SEARCH_BASE");

        var dn = template.Replace("{username}", username, StringComparison.Ordinal);
        if (dn == template)
        {
            return null; // template sem placeholder — config inválida
        }

        try
        {
            using var connection = new LdapConnection();
            await connection.ConnectAsync(server, port, ct);
            await connection.BindAsync(dn, password, ct);
            if (!connection.Bound)
            {
                return null;
            }

            var baseDn = string.IsNullOrWhiteSpace(searchBase) ? dn : searchBase;
            var results = await connection.SearchAsync(
                baseDn, LdapConnection.ScopeSub, "(objectClass=*)",
                [mailAttribute, nameAttribute], false, ct);
            await using var enumerator = results.WithCancellation(ct).GetAsyncEnumerator();
            if (await enumerator.MoveNextAsync())
            {
                var attributeSet = enumerator.Current.GetAttributeSet();
                return new LdapIdentity(
                    attributeSet.GetAttribute(mailAttribute)?.StringValue,
                    attributeSet.GetAttribute(nameAttribute)?.StringValue);
            }

            return new LdapIdentity(null, null);
        }
        catch (LdapException)
        {
            return null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
