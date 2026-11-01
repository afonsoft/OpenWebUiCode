using System.Diagnostics;
using System.Text;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Entrada do registry estático de agentes externos (RF-001).</summary>
public sealed record ExternalAgentDef(string Id, string Name, string Bin, string SkillsDir);

/// <summary>Status de um agente externo exposto por /detect.</summary>
public sealed record ExternalAgentStatus(
    string Id, string Name, bool Installed, string? Version,
    bool SkillInstalled, bool Selected);

/// <summary>
/// Registry de CLIs de agente externos (SPEC-20261016-external-agent-registry):
/// detecção via PATH walk (sem spawn), instalação da skill ponte
/// <c>openwebui-cli</c> no skillsDir de cada agente selecionado e execução
/// headless para <c>delegate_task agent:"ext:{id}"</c>.
/// </summary>
public sealed class ExternalAgentService(ConfigService config)
{
    /// <summary>Cap do stdout devolvido ao delegante.</summary>
    public const int MaxStdoutChars = 50 * 1024;

    /// <summary>Timeout do processo externo.</summary>
    public static readonly TimeSpan ExecTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Cache de detecção (5min, RF-002).</summary>
    private static readonly TimeSpan DetectTtl = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan DetectBudget = TimeSpan.FromSeconds(2);
    private static readonly Dictionary<string, (DateTimeOffset At, bool Found, string? Path)> DetectCache = new();
    private static readonly object DetectLock = new();

    /// <summary>Registry estático (RF-001).</summary>
    public static readonly IReadOnlyList<ExternalAgentDef> Known =
    [
        new("claude", "Claude Code", "claude", ".claude/skills"),
        new("codex", "Codex CLI", "codex", ".codex/skills"),
        new("opencode", "OpenCode", "opencode", ".opencode/skills"),
        new("devin", "Devin CLI", "devin", ".devin/skills"),
        new("gemini", "Gemini CLI", "gemini", ".gemini/skills"),
    ];

    /// <summary>Override do home (isolamento de testes — mesmo padrão do SkillDiscoveryService).</summary>
    public static Func<string> HomeResolver { get; set; } =
        () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Override do PATH (isolamento de testes).</summary>
    public static Func<string?> PathResolver { get; set; } =
        () => Environment.GetEnvironmentVariable("PATH");

    /// <summary>Limpa o cache de detecção (testes).</summary>
    public static void ResetDetectCache()
    {
        lock (DetectLock)
        {
            DetectCache.Clear();
        }
    }

    /// <summary>
    /// Detecta agentes conhecidos: PATH walk sem spawn (RF-002) +
    /// skill ponte instalada + flag de seleção do usuário.
    /// </summary>
    public async Task<List<ExternalAgentStatus>> DetectAsync(
        string userId, CancellationToken ct = default)
    {
        var selected = await SelectedIdsAsync(userId, ct);
        var home = HomeResolver();
        return
        [
            .. Known.Select(def => new ExternalAgentStatus(
                def.Id, def.Name,
                FoundOnPath(def.Bin), null,
                File.Exists(SkillPath(home, def)),
                selected?.Contains(def.Id) ?? FoundOnPath(def.Bin))),
        ];
    }

    /// <summary>Ids selecionados do kv; null = "todos os detectados".</summary>
    public async Task<HashSet<string>?> SelectedIdsAsync(
        string userId, CancellationToken ct)
    {
        var csv = await config.GetAsync<string?>(
            $"u:{userId}:extagents.selected", null, ct);
        return csv is null
            ? null
            : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)];
    }

    /// <summary>
    /// Toggle de seleção (RF-004): instala a skill ponte ao habilitar um
    /// agente detectado e remove ao desabilitar. Devolve erro legível.
    /// </summary>
    public async Task<(bool Ok, string? Error)> SetSelectedAsync(
        string userId, string agentId, bool enabled, CancellationToken ct)
    {
        var def = Known.FirstOrDefault(
            d => d.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
        if (def is null)
        {
            return (false, "Agente externo desconhecido.");
        }
        if (enabled && !FoundOnPath(def.Bin))
        {
            return (false, $"Binário '{def.Bin}' não encontrado no PATH.");
        }

        var selected = await SelectedIdsAsync(userId, ct)
            ?? [.. Known.Where(d => FoundOnPath(d.Bin)).Select(d => d.Id)];
        if (enabled)
        {
            selected.Add(def.Id);
            InstallBridgeSkill(def);
        }
        else
        {
            selected.Remove(def.Id);
            RemoveBridgeSkill(def);
        }
        await config.SetAsync(
            $"u:{userId}:extagents.selected", string.Join(",", selected.Order()), ct);
        return (true, null);
    }

    /// <summary>
    /// Executa o agente externo headless para <c>delegate_task</c> (RF-005):
    /// <c>{bin} -p {prompt}</c> (override <c>extagents.cmd.{id}</c>) no
    /// workdir, timeout 5min, stdout capado. Erros viram texto legível.
    /// </summary>
    public async Task<ExternalRunResult> RunDelegateAsync(
        string agentId, string prompt, string? workdir, string userId,
        CancellationToken ct)
    {
        var def = Known.FirstOrDefault(
            d => d.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
        if (def is null)
        {
            return new ExternalRunResult(
                false, $"Agente externo '{agentId}' desconhecido. "
                    + $"Disponíveis: {string.Join(", ", Known.Select(k => k.Id))}.");
        }

        var binPath = FindOnPath(def.Bin);
        if (binPath is null)
        {
            return new ExternalRunResult(
                false, $"Agente externo indisponível — '{def.Bin}' não está no PATH.");
        }

        var cmdTemplate = await config.GetAsync<string?>(
            $"u:{userId}:extagents.cmd.{def.Id}", null, ct);

        var stdout = new StringBuilder(MaxStdoutChars + 1024);
        var stderr = new StringBuilder(8192);
        try
        {
            using var process = new Process();
            var psi = process.StartInfo;
            psi.FileName = binPath;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            if (!string.IsNullOrWhiteSpace(workdir) && Directory.Exists(workdir))
            {
                psi.WorkingDirectory = workdir;
            }

            // Template override "cmd: args…" (ex.: "codex exec") ou default
            // "{bin} -p {prompt}" estilo claude/gemini.
            if (!string.IsNullOrWhiteSpace(cmdTemplate))
            {
                foreach (var arg in cmdTemplate.Split(' ',
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    psi.ArgumentList.Add(arg == "{prompt}" ? prompt : arg);
                }
                if (!cmdTemplate.Contains("{prompt}", StringComparison.Ordinal))
                {
                    psi.ArgumentList.Add(prompt);
                }
            }
            else
            {
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(prompt);
            }

            process.Start();
            var readOut = DrainAsync(process.StandardOutput, stdout, MaxStdoutChars);
            var readErr = DrainAsync(process.StandardError, stderr, 8192);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ExecTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* processo já encerrado */ }
                return new ExternalRunResult(
                    false, $"Agente '{def.Id}' excedeu o timeout de 5min.");
            }
            await Task.WhenAll(readOut, readErr);

            var outText = stdout.ToString();
            if (process.ExitCode != 0)
            {
                var errTail = stderr.ToString();
                return new ExternalRunResult(
                    false, $"Agente '{def.Id}' saiu com código {process.ExitCode}. "
                        + (errTail.Length == 0 ? "" : $"Stderr: {errTail[..Math.Min(500, errTail.Length)]}"),
                    outText);
            }
            return new ExternalRunResult(true, outText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ExternalRunResult(false, $"Agente '{def.Id}' falhou: {ex.Message}");
        }
    }

    /// <summary>Caminho do SKILL.md ponte de um agente.</summary>
    private static string SkillPath(string home, ExternalAgentDef def) =>
        Path.Join(home, def.SkillsDir, "openwebui-cli", "SKILL.md");

    /// <summary>Instala a skill ponte (RF-004) — sem segredos embutidos.</summary>
    private static void InstallBridgeSkill(ExternalAgentDef def)
    {
        var path = SkillPath(HomeResolver(), def);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, BridgeSkillMarkdown);
    }

    /// <summary>Remove a skill ponte ao desselecionar.</summary>
    private static void RemoveBridgeSkill(ExternalAgentDef def)
    {
        var dir = Path.GetDirectoryName(SkillPath(HomeResolver(), def));
        try
        {
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Diretório com outros arquivos — remove só o SKILL.md.
            var file = SkillPath(HomeResolver(), def);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>PATH walk sem spawn (RF-002): executável presente em algum dir do PATH.</summary>
    private static bool FoundOnPath(string bin) => FindOnPath(bin) is not null;

    private static string? FindOnPath(string bin)
    {
        lock (DetectLock)
        {
            if (DetectCache.TryGetValue(bin, out var hit)
                && DateTimeOffset.UtcNow - hit.At < DetectTtl)
            {
                return hit.Found ? hit.Path : null;
            }
        }

        var deadline = DateTime.UtcNow + DetectBudget;
        var pathEnv = PathResolver();
        string? found = null;
        if (!string.IsNullOrEmpty(pathEnv))
        {
            var names = OperatingSystem.IsWindows()
                ? new[] { bin, $"{bin}.exe", $"{bin}.cmd", $"{bin}.bat" }
                : new[] { bin };
            foreach (var dir in pathEnv.Split(Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (DateTime.UtcNow > deadline)
                {
                    break; // budget total de 2s (RF-002)
                }
                try
                {
                    foreach (var name in names)
                    {
                        var candidate = Path.Join(dir.Trim(), name);
                        if (File.Exists(candidate))
                        {
                            found = candidate;
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    // Dir ilegível — segue para o próximo.
                }
                if (found is not null)
                {
                    break;
                }
            }
        }

        lock (DetectLock)
        {
            DetectCache[bin] = (DateTimeOffset.UtcNow, found is not null, found);
        }
        return found;
    }

    private static async Task DrainAsync(
        StreamReader reader, StringBuilder sink, int cap)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            if (sink.Length < cap)
            {
                sink.Append(buffer, 0, Math.Min(read, cap - sink.Length));
            }
        }
    }


    // Skill ponte (RF-004): ensina o agente externo a chamar nossa API —
    // o token vem de env, nunca embutido no arquivo.
    private const string BridgeSkillMarkdown = """
        ---
        name: openwebui-cli
        description: Chama a API do Open WebUI (.NET) para criar chats/runs — use quando precisar consultar modelos, chats ou disparar uma run no servidor open-webui do usuário.
        ---

        # openwebui-cli bridge

        A instalação local do open-webui expõe a API em `http://localhost:8080`
        (ou `OPENWEBUI_BASE_URL`). Autentique com uma chave `sk-*` do usuário via
        `OPENWEBUI_TOKEN` — nunca escreva o token em arquivos.

        ## Disparar uma run num chat

        ```bash
        curl -s "$OPENWEBUI_BASE_URL/api/v1/chats/<chatId>/runs" \
          -H "Authorization: Bearer $OPENWEBUI_TOKEN" \
          -H "Content-Type: application/json" \
          -d '{"model":"<model>","messages":[{"role":"user","content":"<prompt>"}]}'
        ```

        ## Listar modelos

        ```bash
        curl -s "$OPENWEBUI_BASE_URL/api/v1/models" \
          -H "Authorization: Bearer $OPENWEBUI_TOKEN"
        ```

        Respostas SSE terminam em `data: [DONE]`; erros vêm como `{"error": ...}`.
        """;
}

/// <summary>Resultado da execução headless de um agente externo.</summary>
public sealed record ExternalRunResult(bool Ok, string Text, string? Output = null)
{
    /// <summary>Texto devolvido ao chamador (stdout ou erro legível).</summary>
    public string Result => Ok ? (Output ?? Text) : Text;
}
