using Microsoft.AspNetCore.Hosting;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Ambiente efetivo dos processos do agente (SPEC-20261017-agent-secrets-env
/// RF-005/RF-006): secrets globais ∪ do usuário (user vence) mais as vars
/// gerenciadas da integração GitHub, e os valores a mascarar na saída.
/// </summary>
/// <param name="Vars">Variáveis a injetar no <c>ProcessStartInfo.Environment</c>.</param>
/// <param name="SecretsToScrub">Valores literais (≥4 chars) a virar <c>[REDACTED_SECRET]</c> no output.</param>
public sealed record AgentEnv(
    IReadOnlyDictionary<string, string> Vars,
    IReadOnlyList<string> SecretsToScrub)
{
    /// <summary>Env vazia — nada resolvido, zero mudança de comportamento.</summary>
    public static readonly AgentEnv Empty = new(
        new Dictionary<string, string>(), []);
}

/// <summary>
/// Resolve a env dos processos do agente (shell_exec, jobs, terminal local):
/// merge global→user dos secrets mais <c>GITHUB_TOKEN</c>/<c>GH_TOKEN</c>/
/// <c>GIT_TERMINAL_PROMPT=0</c>/<c>GIT_ASKPASS</c> quando o PAT resolve
/// (user → global, via <see cref="GitHubService.GetTokenAsync"/>). As vars
/// gerenciadas sobrepõem qualquer secret de mesmo nome — o cadastro delas já
/// é bloqueado em <see cref="UserSecretsService.ValidateName"/>.
/// </summary>
public sealed class AgentSecretsEnvService(
    UserSecretsService secrets, GitHubService github, IWebHostEnvironment env)
{
    /// <summary>Tamanho mínimo do valor para entrar no scrub literal (RF-007).</summary>
    private const int MinScrubLength = 4;

    /// <summary>
    /// Env efetiva do usuário: secrets ∪ managed GitHub. O askpass é um
    /// script <c>0700</c> em <c>data/credentials/{userId}/</c> que só ecoa
    /// <c>$GITHUB_TOKEN</c> — o segredo nunca vai a disco nem a .git/config.
    /// Sem nada resolvido devolve <see cref="AgentEnv.Empty"/>.
    /// </summary>
    public async Task<AgentEnv> BuildAsync(string userId, CancellationToken ct)
    {
        var vars = new Dictionary<string, string>(
            await secrets.ResolveValuesAsync(userId, ct), StringComparer.Ordinal);
        var scrub = vars.Values
            .Where(v => v.Length >= MinScrubLength)
            .OrderByDescending(v => v.Length) // sobreposição: o maior primeiro
            .ToList();

        var pat = await github.GetTokenAsync(userId, ct);
        if (pat is { Length: > 0 })
        {
            vars["GITHUB_TOKEN"] = pat;
            vars["GH_TOKEN"] = pat;
            vars["GIT_TERMINAL_PROMPT"] = "0";
            vars["GIT_ASKPASS"] = EnsureAskPass(userId);
            scrub.Insert(0, pat);
        }
        else
        {
            RemoveAskPass(userId); // PAT removido — artefato não fica órfão.
        }

        return vars.Count == 0 ? AgentEnv.Empty : new AgentEnv(vars, scrub);
    }

    /// <summary>
    /// Env da camada <b>global</b> apenas — para processos compartilhados
    /// (terminal local spawnado pelo admin): secrets de usuário não podem
    /// vazar num processo que serve vários usuários. Inclui as vars
    /// gerenciadas quando o PAT <i>global</i> existe.
    /// </summary>
    public async Task<AgentEnv> BuildGlobalAsync(CancellationToken ct)
    {
        var vars = new Dictionary<string, string>(
            await secrets.ResolveGlobalValuesAsync(ct), StringComparer.Ordinal);
        var scrub = vars.Values
            .Where(v => v.Length >= MinScrubLength)
            .OrderByDescending(v => v.Length)
            .ToList();

        if (await github.GetGlobalTokenAsync(ct) is { Length: > 0 } pat)
        {
            vars["GITHUB_TOKEN"] = pat;
            vars["GH_TOKEN"] = pat;
            vars["GIT_TERMINAL_PROMPT"] = "0";
            vars["GIT_ASKPASS"] = EnsureAskPass("__global__");
            scrub.Insert(0, pat);
        }

        return vars.Count == 0 ? AgentEnv.Empty : new AgentEnv(vars, scrub);
    }

    /// <summary>Dir de credenciais do usuário (fora do workspace/jail das tools).</summary>
    private string CredentialsDir(string userId) =>
        Path.Join(OpenWebUI.Domain.DataPaths.Root(env.ContentRootPath),
            "credentials", SanitizeDirName(userId));

    /// <summary>
    /// Garante <c>git-askpass.sh</c> (0700) que responde username com
    /// <c>x-access-token</c> e password com <c>$GITHUB_TOKEN</c> da env.
    /// Idempotente — reescreve só se o conteúdo divergir.
    /// </summary>
    private string EnsureAskPass(string userId)
    {
        var dir = CredentialsDir(userId);
        var path = Path.Join(dir, "git-askpass.sh");
        if (File.Exists(path))
        {
            return path; // conteúdo é fixo — só env resolve o segredo
        }

        Directory.CreateDirectory(dir);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead
                    | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (IOException) { /* FS sem suporte a modo unix — segue */ }
        catch (UnauthorizedAccessException) { /* idem */ }

        File.WriteAllText(path, AskPassScript);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead
                    | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (IOException) { /* idem */ }
        catch (UnauthorizedAccessException) { /* idem */ }
        return path;
    }

    /// <summary>Remove o askpass quando o PAT some (best-effort).</summary>
    private void RemoveAskPass(string userId)
    {
        try
        {
            var path = Path.Join(CredentialsDir(userId), "git-askpass.sh");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    /// <summary>userId é GUID — sanitize defensivo para path mesmo assim.</summary>
    private static string SanitizeDirName(string userId) =>
        string.Concat(userId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

    /// <summary>
    /// Script askpass: o git pergunta "Username for ..."/"Password for ..." —
    /// respondemos sem segredo em disco (só referência à env injetada).
    /// </summary>
    private const string AskPassScript = """
        #!/bin/sh
        # Gerenciado pelo OpenWebUI (SPEC-20261017-agent-secrets-env).
        # Nunca conter segredo literal — só lê a env do processo chamador.
        case "$1" in
          *sername*|*user*|*login*) printf '%s' "${GIT_USERNAME:-x-access-token}" ;;
          *) printf '%s' "${GITHUB_TOKEN:-}" ;;
        esac
        """;
}
