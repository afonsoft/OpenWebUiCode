using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Hosting;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Vínculo repositório+branch ↔ workspace do usuário
/// (SPEC-20261008-github-repo-workspace): o checkout mora em
/// <c>data/workspaces/{userId}/repos/{owner}__{repo}</c> — dentro do jail
/// das tools <c>file_*</c>/<c>shell_exec</c>. O binding vai no kv por
/// usuário; o token nunca é persistido no remote do git (clone/fetch
/// autenticam via <c>http.extraheader</c>).
/// </summary>
public sealed class WorkspaceRepoService(ConfigService config, IHostEnvironment env)
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(3);

    private static string BindingKey(string userId) => $"u:{userId}:workspace.repo";

    /// <summary>
    /// Binding por chat (SPEC-20261010-chat-repo-binding): precede o binding
    /// global do usuário — cada chat pode trabalhar num repo diferente
    /// (estilo opencode-web, uma session por projeto).
    /// </summary>
    private static string ChatBindingKey(string chatId) => $"chat:{chatId}:workspace.repo";

    /// <summary>Raiz do workspace do usuário (jail das tools). Respeita
    /// <see cref="DataPaths.Root"/> (env <c>DATA_ROOT</c> — no Docker, /data).</summary>
    public string WorkspaceRoot(string userId) =>
        Path.Join(DataPaths.Root(env.ContentRootPath), "workspaces", userId);

    /// <summary>Binding atual (null quando não há repo vinculado).</summary>
    public Task<WorkspaceRepoBinding?> GetBindingAsync(string userId, CancellationToken ct) =>
        config.GetAsync<WorkspaceRepoBinding?>(BindingKey(userId), null, ct);

    /// <summary>Binding por chat (null quando o chat não tem repo próprio).</summary>
    public Task<WorkspaceRepoBinding?> GetChatBindingAsync(string chatId, CancellationToken ct) =>
        config.GetAsync<WorkspaceRepoBinding?>(ChatBindingKey(chatId), null, ct);

    /// <summary>Define ou limpa (null) o binding por chat.</summary>
    public Task SetChatBindingAsync(
        string chatId, WorkspaceRepoBinding? binding, CancellationToken ct) =>
        config.SetAsync(ChatBindingKey(chatId), binding, ct);

    /// <summary>
    /// Binding efetivo com a origem. Com <paramref name="chatId"/> a
    /// resolução é estrita — só o binding explícito do chat conta
    /// (<c>chat</c> | <c>none</c>): SPEC-15, conversa sem repo selecionado
    /// não herda o repo global. Sem chatId devolve o binding global do
    /// usuário (<c>user</c> | <c>none</c>).
    /// </summary>
    public async Task<(WorkspaceRepoBinding? Binding, string Source)> ResolveBindingAsync(
        string userId, string? chatId, CancellationToken ct)
    {
        if (chatId is not null)
        {
            var chatBinding = await GetChatBindingAsync(chatId, ct);
            return (chatBinding, chatBinding is not null ? "chat" : "none");
        }
        var userBinding = await GetBindingAsync(userId, ct);
        return (userBinding, userBinding is not null ? "user" : "none");
    }

    /// <summary>
    /// Diretório de trabalho efetivo: o checkout/worktree do repo vinculado
    /// quando existe, senão a raiz do workspace.
    /// </summary>
    public Task<string> ResolveWorkdirAsync(string userId, CancellationToken ct) =>
        ResolveWorkdirAsync(userId, null, ct);

    /// <summary>
    /// Igual a <see cref="ResolveWorkdirAsync(string, CancellationToken)"/>
    /// resolvendo o binding por chat (SPEC-20261010-chat-repo-binding).
    /// Quando o binding aponta um worktree por conversa que ainda não
    /// existe em disco (ex.: chat desarquivado, binding herdado), ele é
    /// recriado sob demanda a partir do checkout base.
    /// </summary>
    public async Task<string> ResolveWorkdirAsync(
        string userId, string? chatId, CancellationToken ct)
    {
        var (binding, _) = await ResolveBindingAsync(userId, chatId, ct);
        if (binding is null)
        {
            return WorkspaceRoot(userId);
        }

        var dir = Path.Join(WorkspaceRoot(userId), binding.Dir);
        if (IsGitDir(dir))
        {
            return dir;
        }

        // Worktree por conversa (SPEC-15): binding sobrevive a archive —
        // recria o worktree na primeira utilização após unarchive.
        if (binding.Dir.StartsWith("worktrees/", StringComparison.Ordinal)
            && await EnsureChatWorktreeAsync(userId, chatId!, binding, ct) is { } recreated)
        {
            return recreated;
        }

        return WorkspaceRoot(userId);
    }

    /// <summary>
    /// Abre (clone ou troca de branch) o repositório no workspace.
    /// <paramref name="cloneUrl"/> permite testes com remote <c>file://</c>;
    /// produção passa <c>https://github.com/{slug}.git</c>. Devolve o
    /// binding persistido ou erro amigável.
    /// </summary>
    public async Task<(WorkspaceRepoBinding? Binding, string? Error)> OpenAsync(
        string userId, string slug, string branch, string cloneUrl, string? token, CancellationToken ct)
    {
        if (!IsValidSlug(slug))
        {
            return (null, "Repositório inválido — use o formato owner/repo.");
        }
        if (string.IsNullOrWhiteSpace(branch) || branch.Any(char.IsWhiteSpace)
            || branch.StartsWith('-'))
        {
            return (null, "Branch inválida.");
        }

        var (dir, absDir, error) = await EnsureCheckoutAsync(
            userId, slug, branch, cloneUrl, token, ct);
        if (error is not null)
        {
            return (null, error);
        }

        // TestCommand/FormatCommand customizados sobrevivem a re-open/branch switch (RF-003).
        var previous = await GetBindingAsync(userId, ct);
        var binding = new WorkspaceRepoBinding(
            slug, branch, dir, previous?.TestCommand, previous?.FormatCommand);
        await config.SetAsync(BindingKey(userId), binding, ct);
        // Bind/branch novo → cache de skills/commands do workdir expira na hora.
        SkillDiscoveryService.Invalidate(absDir);
        return (binding, null);
    }

    /// <summary>
    /// Vincula o chat a <paramref name="slug"/>/<paramref name="branch"/>
    /// criando um <b>worktree exclusivo da conversa</b> (SPEC-15):
    /// <c>workspaces/{user}/worktrees/{owner}__{repo}/{chatId}</c> na branch
    /// <c>chat/{chatId}</c> a partir de <c>origin/{branch}</c>. O checkout
    /// base em <c>repos/{owner}__{repo}</c> é compartilhado por slug; o
    /// worktree é removido no archive/delete do chat e o base só sai de
    /// disco quando nenhuma conversa (nem o global) referencia o repo.
    /// </summary>
    public async Task<(WorkspaceRepoBinding? Binding, string? Error)> OpenChatAsync(
        string userId, string chatId, string slug, string branch,
        string cloneUrl, string? token, CancellationToken ct)
    {
        if (!IsValidSlug(slug))
        {
            return (null, "Repositório inválido — use o formato owner/repo.");
        }
        if (string.IsNullOrWhiteSpace(branch) || branch.Any(char.IsWhiteSpace)
            || branch.StartsWith('-'))
        {
            return (null, "Branch inválida.");
        }

        var (_, absDir, error) = await EnsureCheckoutAsync(
            userId, slug, branch, cloneUrl, token, ct);
        if (error is not null)
        {
            return (null, error);
        }

        var (dir, absWt, wtError) = await CreateChatWorktreeAsync(
            userId, chatId, slug, branch, absDir, ct);
        if (wtError is not null)
        {
            return (null, wtError);
        }

        // Comandos customizados: herda do binding de chat anterior, caindo
        // no global do usuário quando o chat nunca teve repo próprio.
        var previous = await GetChatBindingAsync(chatId, ct)
            ?? await GetBindingAsync(userId, ct);
        var binding = new WorkspaceRepoBinding(
            slug, branch, dir, previous?.TestCommand, previous?.FormatCommand);
        await config.SetAsync(ChatBindingKey(chatId), binding, ct);
        SkillDiscoveryService.Invalidate(absWt);
        return (binding, null);
    }

    /// <summary>
    /// Binding por chat para uma sub-conversa (SPEC-15): mesmo repo/branch
    /// do pai, mas worktree próprio em <c>worktrees/…/{childChatId}</c> —
    /// materializado sob demanda na primeira resolução de workdir.
    /// </summary>
    public WorkspaceRepoBinding InheritChatBinding(string childChatId, WorkspaceRepoBinding parent)
    {
        var repoDir = RepoDirName(parent.Repo);
        return parent with { Dir = $"worktrees/{repoDir}/{childChatId}" };
    }

    /// <summary>
    /// Libera o workspace do chat (SPEC-15): remove o worktree exclusivo;
    /// <paramref name="keepBinding"/> preserva o binding kv (archive — o
    /// worktree é recriado sob demanda no unarchive). Se nenhuma conversa
    /// (nem o binding global) referencia mais o repo, o checkout base em
    /// <c>repos/</c> também sai de disco para liberar espaço.
    /// </summary>
    public async Task ReleaseChatWorkspaceAsync(
        string userId, string chatId, bool keepBinding, CancellationToken ct)
    {
        var binding = await GetChatBindingAsync(chatId, ct);
        if (!keepBinding)
        {
            await SetChatBindingAsync(chatId, null, ct);
        }
        if (binding is null)
        {
            return;
        }

        var root = WorkspaceRoot(userId);
        var repoDir = RepoDirName(binding.Repo);
        var baseAbs = Path.Join(root, "repos", repoDir);

        if (binding.Dir.StartsWith("worktrees/", StringComparison.Ordinal))
        {
            var wtAbs = Path.Join(root, binding.Dir);
            var error = await RunGitLoggedAsync(
                baseAbs, ct, null, "worktree", "remove", "--force", wtAbs);
            if (error is not null && Directory.Exists(wtAbs))
            {
                TryDeleteDir(wtAbs);
            }
            _ = await RunGitAsync(baseAbs, ct, "worktree", "prune");
            // Pasta worktrees/{owner}__{repo} vazia → sai junto.
            var wtParent = Path.Join(root, "worktrees", repoDir);
            if (Directory.Exists(wtParent) && !Directory.EnumerateFileSystemEntries(wtParent).Any())
            {
                TryDeleteDir(wtParent);
            }
        }

        // O repo base sai de disco só quando nenhuma conversa (incl.
        // arquivadas — o binding sobrevive) nem o binding global o usam.
        if (!keepBinding && !await IsRepoReferencedAsync(userId, binding.Repo, ct)
            && Directory.Exists(baseAbs))
        {
            TryDeleteDir(baseAbs);
            TryDeleteDir(Path.Join(root, "worktrees", repoDir));
            SkillDiscoveryService.Invalidate(baseAbs);
        }
    }

    /// <summary>True quando algum binding (chat:* ou global) usa o slug.</summary>
    private async Task<bool> IsRepoReferencedAsync(
        string userId, string slug, CancellationToken ct)
    {
        if (await GetBindingAsync(userId, ct) is { } global
            && string.Equals(global.Repo, slug, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var chatBindings = await config.ListByPrefixAsync<WorkspaceRepoBinding>(
            "chat:", ct);
        return chatBindings.Any(kv =>
            kv.Key.EndsWith(":workspace.repo", StringComparison.Ordinal)
            && kv.Value is { } b
            && string.Equals(b.Repo, slug, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Cria (ou recria) o worktree da conversa em
    /// <c>worktrees/{owner}__{repo}/{chatId}</c> com a branch exclusiva
    /// <c>chat/{chatId}</c> a partir de <c>origin/{branch}</c>. Devolve o
    /// dir relativo ao workspace root e o caminho absoluto.
    /// </summary>
    private async Task<(string Dir, string AbsDir, string? Error)> CreateChatWorktreeAsync(
        string userId, string chatId, string slug, string branch,
        string baseAbs, CancellationToken ct)
    {
        var repoDir = RepoDirName(slug);
        var dir = $"worktrees/{repoDir}/{Sanitize(chatId)}";
        var absWt = Path.Join(WorkspaceRoot(userId), dir);
        var chatBranch = $"chat/{chatId}";

        if (Directory.Exists(absWt))
        {
            // Rebind/recriação: remove o worktree anterior antes de re-criar.
            _ = await RunGitLoggedAsync(
                baseAbs, ct, null, "worktree", "remove", "--force", absWt);
            if (Directory.Exists(absWt))
            {
                TryDeleteDir(absWt);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(absWt)!);

        // Start point: ref remota atualizada no fetch; fallback na local.
        var startRef = await RunGitAsync(
            baseAbs, ct, "rev-parse", "--verify", $"refs/remotes/origin/{branch}") is not null
            ? $"origin/{branch}"
            : branch;
        var error = await RunGitLoggedAsync(
            baseAbs, ct, null, "worktree", "add", "-b", chatBranch, absWt, startRef);
        if (error is not null)
        {
            // Branch chat/{id} ficou de uma tentativa anterior — recria.
            _ = await RunGitAsync(baseAbs, ct, "branch", "-D", chatBranch);
            error = await RunGitLoggedAsync(
                baseAbs, ct, null, "worktree", "add", "-b", chatBranch, absWt, startRef);
        }
        return error is not null
            ? (dir, absWt, $"git worktree add: {error}")
            : (dir, absWt, null);
    }

    /// <summary>Recria sob demanda o worktree de um binding por chat.</summary>
    private async Task<string?> EnsureChatWorktreeAsync(
        string userId, string chatId, WorkspaceRepoBinding binding, CancellationToken ct)
    {
        var baseAbs = Path.Join(WorkspaceRoot(userId), "repos", RepoDirName(binding.Repo));
        if (!Directory.Exists(Path.Join(baseAbs, ".git")))
        {
            return null;
        }
        var (_, absWt, error) = await CreateChatWorktreeAsync(
            userId, chatId, binding.Repo, binding.Branch, baseAbs, ct);
        return error is null ? absWt : null;
    }

    /// <summary>
    /// Garante o checkout do repo no workspace (clone shallow ou
    /// fetch+switch de branch no existente). Devolve o dir relativo e o
    /// caminho absoluto; <paramref name="error"/> preenchido em falha.
    /// </summary>
    private async Task<(string Dir, string AbsDir, string? Error)> EnsureCheckoutAsync(
        string userId, string slug, string branch,
        string cloneUrl, string? token, CancellationToken ct)
    {
        var dir = $"repos/{RepoDirName(slug)}";
        var root = WorkspaceRoot(userId);
        var absDir = Path.Join(root, dir);

        Directory.CreateDirectory(root);
        string? error;
        if (Directory.Exists(Path.Join(absDir, ".git")))
        {
            // Checkout existente: só troca de branch (fetch + switch + pull).
            // Refspec explícito — clones --single-branch não criam refs/remotes
            // das outras branches, e o switch --track depende dela.
            error = await RunGitLoggedAsync(absDir, ct, token,
                "fetch", "origin", "--depth", "50",
                $"+refs/heads/{branch}:refs/remotes/origin/{branch}");
            error ??= await SwitchBranchAsync(absDir, branch, token, ct);
        }
        else if (Directory.Exists(absDir) && Directory.EnumerateFileSystemEntries(absDir).Any())
        {
            return (dir, absDir, $"Diretório '{dir}' já existe com outro conteúdo.");
        }
        else
        {
            error = await CloneAsync(absDir, cloneUrl, branch, token, ct);
        }

        return (dir, absDir, error);
    }

    /// <summary>Desvincula o repositório (o checkout permanece no disco).</summary>
    public async Task UnbindAsync(string userId, CancellationToken ct) =>
        await config.SetAsync<WorkspaceRepoBinding?>(BindingKey(userId), null, ct);

    /// <summary>
    /// Define ou limpa o comando de teste customizado do binding
    /// (override da detecção por manifesto — SPEC-20261009-ide-mentions-tests RF-003).
    /// </summary>
    public async Task SetTestCommandAsync(string userId, string? testCommand, CancellationToken ct)
    {
        var binding = await GetBindingAsync(userId, ct);
        if (binding is null)
        {
            return;
        }
        await config.SetAsync(BindingKey(userId),
            binding with { TestCommand = string.IsNullOrWhiteSpace(testCommand) ? null : testCommand.Trim() }, ct);
    }

    /// <summary>
    /// Igual a <see cref="SetTestCommandAsync(string, string?, CancellationToken)"/>
    /// mas resolve o binding efetivo do chat (SPEC-20261010-workspace-chatid-scope):
    /// escreve na chave <c>chat:{id}</c> quando a origem é o chat, senão na do usuário.
    /// </summary>
    public async Task SetTestCommandAsync(
        string userId, string? chatId, string? testCommand, CancellationToken ct)
    {
        var (binding, source) = await ResolveBindingAsync(userId, chatId, ct);
        if (binding is null)
        {
            return;
        }
        var updated = binding with
        {
            TestCommand = string.IsNullOrWhiteSpace(testCommand) ? null : testCommand.Trim(),
        };
        await config.SetAsync(
            source == "chat" ? ChatBindingKey(chatId!) : BindingKey(userId), updated, ct);
    }

    /// <summary>
    /// Define ou limpa o format hook por-repo do binding
    /// (override do global <c>Format:Command</c> — SPEC-20261009-worktree-format-hooks RF-004).
    /// </summary>
    public async Task SetFormatCommandAsync(string userId, string? formatCommand, CancellationToken ct)
    {
        var binding = await GetBindingAsync(userId, ct);
        if (binding is null)
        {
            return;
        }
        await config.SetAsync(BindingKey(userId),
            binding with { FormatCommand = string.IsNullOrWhiteSpace(formatCommand) ? null : formatCommand.Trim() }, ct);
    }

    private static async Task<string?> CloneAsync(
        string absDir, string url, string branch, string? token, CancellationToken ct)
    {
        var error = await RunGitLoggedAsync(null, ct, token,
            "clone", "--depth", "50", "--branch", branch, "--single-branch",
            url, absDir);
        if (error is not null)
        {
            return error;
        }

        // Limpa a URL gravada no clone (nunca carrega credencial).
        var uri = new Uri(url);
        var clean = uri.Scheme is "http" or "https"
            ? $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}"
            : url;
        return await RunGitLoggedAsync(absDir, ct, null,
            "remote", "set-url", "origin", clean);
    }

    private static async Task<string?> SwitchBranchAsync(
        string absDir, string branch, string? token, CancellationToken ct)
    {
        // Branch já existe localmente → switch; senão cria a partir de origin.
        var local = await RunGitAsync(absDir, ct,
            "rev-parse", "--verify", $"refs/heads/{branch}");
        var error = local is not null
            ? await RunGitLoggedAsync(absDir, ct, null, "switch", branch)
            : await RunGitLoggedAsync(absDir, ct, null,
                "switch", "-c", branch, $"refs/remotes/origin/{branch}");
        error ??= await RunGitLoggedAsync(absDir, ct, token,
            "pull", "--ff-only", "origin", branch);
        if (error is not null)
        {
            // Checkout base divergiu do remoto (commits locais/espelho
            // stale) — ele é cache compartilhado do sistema; o trabalho
            // real vive nos worktrees por chat. Preserva o HEAD em branch
            // de backup e alinha o base a origin/{branch}. Se houver
            // mudança não commitada, não toca — devolve o erro do pull.
            var dirty = await RunGitAsync(absDir, ct, "status", "--porcelain");
            if (!string.IsNullOrEmpty(dirty))
            {
                return error;
            }
            var backup = $"backup/{Sanitize(branch)}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            _ = await RunGitAsync(absDir, ct, "branch", backup);
            return await RunGitLoggedAsync(absDir, ct, null,
                "reset", "--hard", $"refs/remotes/origin/{branch}");
        }
        return error;
    }

    /// <summary>Roda git e devolve stderr enxuto em falha (sem token no texto).</summary>
    private static async Task<string?> RunGitLoggedAsync(
        string? workdir, CancellationToken ct, string? token, params string[] args)
    {
        var (code, _, err) = await RunAsync(workdir, ct, token, args);
        if (code == 0)
        {
            return null;
        }

        var text = (err ?? string.Empty).Trim();
        var first = text.Split('\n').FirstOrDefault(l => l.Length > 0) ?? "falha no git";
        return $"git {args[0]}: {first}";
    }

    private static Task<string?> RunGitAsync(string workdir, CancellationToken ct, params string[] args) =>
        RunAsync(workdir, ct, null, args)
            .ContinueWith(t => t.Result.ExitCode == 0 ? t.Result.Stdout : null, ct);

    private static async Task<(int ExitCode, string Stdout, string? Stderr)> RunAsync(
        string? workdir, CancellationToken ct, string? token, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExecutableResolver.Resolve("git"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workdir is not null)
        {
            psi.WorkingDirectory = workdir;
        }
        // Volume DATA_ROOT populado por outro UID (container root × bind-mount)
        // faz o git recusar o repo ("detected dubious ownership") — a exceção
        // por invocação evita depender de config global do ambiente.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.directory=*");
        // Credencial só no header dessa invocação — nada é gravado em .git/config.
        if (token is not null)
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(
                "http.https://github.com/.extraheader=AUTHORIZATION: basic "
                + Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{token}")));
        }
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).WaitAsync(GitTimeout, ct);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException oce)
        {
            // Normaliza TaskCanceledException → OperationCanceledException (cancelamento do request propaga).
            throw new OperationCanceledException(oce.Message, oce, ct);
        }
        catch (Win32Exception ex) { return (-1, string.Empty, ex.Message); }
        catch (ObjectDisposedException ex) { return (-1, string.Empty, ex.Message); }
        catch (InvalidOperationException ex) { return (-1, string.Empty, ex.Message); }
        catch (IOException ex) { return (-1, string.Empty, ex.Message); }
        catch (TimeoutException ex) { return (-1, string.Empty, ex.Message); }
    }

    /// <summary>Nome determinístico do diretório do repo (<c>{owner}__{repo}</c> sanitizado).</summary>
    private static string RepoDirName(string slug)
    {
        var idx = slug.IndexOf('/');
        return $"{Sanitize(slug[..idx])}__{Sanitize(slug[(idx + 1)..])}";
    }

    /// <summary>
    /// <c>.git</c> presente — diretório (checkout normal) ou arquivo
    /// (worktree, cujo .git aponta para o gitdir do repo base).
    /// </summary>
    private static bool IsGitDir(string dir) =>
        Directory.Exists(Path.Join(dir, ".git")) || File.Exists(Path.Join(dir, ".git"));

    private static void TryDeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"WorkspaceRepoService: falha ao remover '{dir}': {ex.Message}");
        }
    }

    private static bool IsValidSlug(string slug)
    {
        var parts = slug.Split('/');
        return parts.Length == 2
            && parts.All(p => p.Length > 0 && p.Length <= 100
                && p.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }
        return sb.ToString();
    }
}
