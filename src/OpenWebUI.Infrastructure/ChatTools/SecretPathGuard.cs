namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Guarda de paths sensíveis (SPEC-20261123-secret-file-guard — portado do
/// Devin CLI <c>secret_file_guard</c>): as tools de arquivo recusam ler,
/// escrever ou patchar arquivos que tipicamente carregam credenciais —
/// <c>.env*</c>, chaves privadas, keystores, <c>~/.ssh</c>, credenciais de
/// nuvem e o próprio <c>webui.db</c>. O bloqueio é por nome/segmento do
/// caminho relativo (case-insensitive), não por conteúdo — conteúdo com
/// segredo já é mascarado pelo <see cref="SecretScrubber"/> na saída.
/// </summary>
public static class SecretPathGuard
{
    /// <summary>Nomes exatos de arquivo/dir considerados secretos.</summary>
    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".envrc", "credentials", "config.json.credentials",
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
        ".netrc", ".npmrc", ".pypirc", ".pgpass", ".my.cnf",
        "secrets.json", "service-account.json", "serviceaccount.json",
        "webui.db", "webui.db-shm", "webui.db-wal",
    };

    /// <summary>Sufixos de arquivo considerados secretos (chaves/keystores).</summary>
    private static readonly string[] SecretSuffixes =
    [
        ".pem", ".key", ".p12", ".pfx", ".keystore", ".jks", ".kdbx",
        "_rsa", "_dsa", "_ecdsa", "_ed25519", // id_rsa.pub coberto pelo prefixo id_*.
    ];

    /// <summary>Prefixos <c>.env.*</c> (.env.local, .env.production, …).</summary>
    private const string EnvPrefix = ".env.";

    /// <summary>Segmentos de diretório que só guardam credenciais.</summary>
    private static readonly HashSet<string> SecretDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker",
    };

    /// <summary>
    /// True quando <paramref name="path"/> (relativo ao workspace ou
    /// absoluto — só basename e segmentos são avaliados) aponta para
    /// arquivo que tipicamente guarda credenciais.
    /// </summary>
    public static bool IsSecretPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = segments[^1];
        if (SecretNames.Contains(name)
            || name.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase)
            || SecretSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        // Diretório credencial em qualquer nível do caminho
        // (.ssh/id_rsa, sub/.aws/credentials, …).
        return segments.Take(segments.Length - 1).Any(SecretDirs.Contains);
    }

    /// <summary>
    /// Mensagem padrão de bloqueio devolvida à tool (sem eco do path real —
    /// o nome pode já revelar a estrutura do diretório do usuário).
    /// </summary>
    public static string BlockMessage(string displayPath) =>
        $"Erro: '{displayPath}' é um arquivo de credenciais/segredos e não "
        + "pode ser lido ou modificado pela tool. Se precisar dele no "
        + "ambiente, peça ao usuário para configurar o valor via Settings "
        + "ou variável de ambiente.";
}
