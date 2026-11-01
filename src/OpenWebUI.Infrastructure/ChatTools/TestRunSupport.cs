using System.Text.RegularExpressions;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Detecção do comando de teste por manifesto na raiz do workdir
/// (SPEC-20261009-ide-mentions-tests, RF-003). A prioridade segue a ordem
/// do spec: .NET → Node (bun &gt; npm) → Python → Go; um
/// <c>TestCommand</c> configurado no binding vence qualquer manifesto.
/// </summary>
public static class TestCommandDetector
{
    /// <summary>Retorna o comando de teste detectado, ou null quando nenhum manifesto conhecido existe.</summary>
    public static string? Detect(string workdir, string? testCommandOverride)
    {
        if (!string.IsNullOrWhiteSpace(testCommandOverride))
        {
            return testCommandOverride.Trim();
        }
        if (HasAny(workdir, "*.slnx") || HasAny(workdir, "*.sln") || HasAny(workdir, "*.csproj"))
        {
            return "dotnet test";
        }
        if (File.Exists(Path.Join(workdir, "package.json")))
        {
            // bun.lockb é o formato binário legado do Bun (<1.2).
            return File.Exists(Path.Join(workdir, "bun.lock"))
                || File.Exists(Path.Join(workdir, "bun.lockb"))
                    ? "bun test"
                    : "npm test";
        }
        if (File.Exists(Path.Join(workdir, "pyproject.toml")))
        {
            return "pytest";
        }
        if (File.Exists(Path.Join(workdir, "go.mod")))
        {
            return "go test ./...";
        }
        return null;
    }

    private static bool HasAny(string dir, string pattern) =>
        Directory.Exists(dir) && Directory.EnumerateFiles(dir, pattern).Any();
}

/// <summary>Resumo parseado da saída de um runner de testes; campos null quando desconhecidos.</summary>
public sealed record TestRunCounts(int? Passed, int? Failed, int? Skipped);

/// <summary>
/// Parser das saídas de runners conhecidos (dotnet test, jest/vitest/npm,
/// bun test, pytest, go test). Comando customizado tenta todos os parsers
/// em ordem; sem correspondência retorna null — nunca inventa contagens.
/// </summary>
public static partial class TestRunOutputParser
{
    /// <summary>Extrai as contagens do output conforme o runner identificado pelo comando.</summary>
    public static TestRunCounts? Parse(string command, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }
        var binary = Path.GetFileName(command.Split(' ', 2)[0]).ToLowerInvariant();
        return binary switch
        {
            "dotnet" => ParseDotnet(output),
            "pytest" => ParsePytest(output),
            "go" => ParseGo(output),
            "bun" => ParseBun(output),
            "npm" or "yarn" or "pnpm" or "npx" or "jest" or "vitest" =>
                ParseJest(output) ?? ParseBun(output),
            _ => ParseDotnet(output) ?? ParsePytest(output) ?? ParseGo(output)
                 ?? ParseJest(output) ?? ParseBun(output),
        };
    }

    /// <summary>"Failed! - Failed: 3, Passed: 40, Skipped: 0" (agrega todas as linhas — multi-projeto).</summary>
    private static TestRunCounts? ParseDotnet(string output)
    {
        var matches = DotnetSummaryRegex().Matches(output);
        if (matches.Count > 0)
        {
            return new TestRunCounts(
                matches.Sum(m => int.Parse(m.Groups[2].Value)),
                matches.Sum(m => int.Parse(m.Groups[1].Value)),
                matches.Sum(m => int.Parse(m.Groups[3].Value)));
        }
        var total = Regex.Match(output,
            @"Total tests:\s*(\d+)\D*?Passed:\s*(\d+)\D*?Failed:\s*(\d+)(?:\D*?Skipped:\s*(\d+))?",
            RegexOptions.Singleline, RegexDefaults.MatchTimeout);
        if (!total.Success)
        {
            return null;
        }
        var skipped = total.Groups[4].Success
            ? int.Parse(total.Groups[4].Value)
            : (int?)null;
        return new TestRunCounts(int.Parse(total.Groups[2].Value),
            int.Parse(total.Groups[3].Value), skipped);
    }

    /// <summary>"==== 12 passed, 3 failed, 1 skipped in 4.56s ====".</summary>
    private static TestRunCounts? ParsePytest(string output)
    {
        var match = PytestSummaryRegex().Matches(output);
        if (match.Count == 0)
        {
            return null;
        }
        var tail = match[^1].Groups[1].Value;
        var failed = Num(tail, @"(\d+)\s+failed") ?? Num(tail, @"(\d+)\s+errors?");
        var skipped = Num(tail, @"(\d+)\s+skipped") ?? Num(tail, @"(\d+)\s+xfailed");
        return new TestRunCounts(Num(tail, @"(\d+)\s+passed"), failed, skipped);
    }

    /// <summary>"Tests:  1 failed, 9 passed, 10 total" (jest/vitest/npm test).</summary>
    private static TestRunCounts? ParseJest(string output)
    {
        var match = JestSummaryRegex().Matches(output);
        if (match.Count == 0)
        {
            return null;
        }
        var tail = match[^1].Groups[1].Value;
        return new TestRunCounts(
            Num(tail, @"(\d+)\s+passed"), Num(tail, @"(\d+)\s+failed"), Num(tail, @"(\d+)\s+skipped"));
    }

    /// <summary>"42 pass / 0 fail / 1 skip" (bun test).</summary>
    private static TestRunCounts? ParseBun(string output)
    {
        var passM = BunPassRegex().Match(output);
        var failM = BunFailRegex().Match(output);
        if (!passM.Success && !failM.Success)
        {
            return null;
        }
        static int? V(Match m) => m.Success ? int.Parse(m.Groups[1].Value) : null;
        return new TestRunCounts(V(passM), V(failM), V(BunSkipRegex().Match(output)));
    }

    /// <summary>Tally de "--- PASS:/--- FAIL:/--- SKIP:" (go test -v) ou ok/FAIL de pacote.</summary>
    private static TestRunCounts? ParseGo(string output)
    {
        var passed = GoPassRegex().Matches(output).Count;
        var failed = GoTallyFailRegex().Matches(output).Count;
        var skipped = GoSkipRegex().Matches(output).Count;
        if (passed + failed + skipped > 0)
        {
            return new TestRunCounts(passed, failed, skipped);
        }
        if (GoPackageRegex().IsMatch(output))
        {
            return new TestRunCounts(null,
                GoFailRegex().IsMatch(output) ? 1 : 0, null);
        }
        return null;
    }

    private static int? Num(string text, string pattern) =>
        Regex.Match(text, pattern, RegexOptions.None, RegexDefaults.MatchTimeout) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    [GeneratedRegex(@"Failed:\s*(\d+)\s*,\s*Passed:\s*(\d+)\s*,\s*Skipped:\s*(\d+)")]
    private static partial Regex DotnetSummaryRegex();

    [GeneratedRegex(@"=+\s*([\d,\s\w]+?)\s+in\s+[\d.]+s\s*=+")]
    private static partial Regex PytestSummaryRegex();

    [GeneratedRegex(@"Tests:\s*(.+?)\s+total", RegexOptions.Multiline)]
    private static partial Regex JestSummaryRegex();

    [GeneratedRegex(@"^\s*(\d+)\s+pass\b", RegexOptions.Multiline)]
    private static partial Regex BunPassRegex();

    [GeneratedRegex(@"^\s*(\d+)\s+fail\b", RegexOptions.Multiline)]
    private static partial Regex BunFailRegex();

    [GeneratedRegex(@"^\s*(\d+)\s+skip\b", RegexOptions.Multiline)]
    private static partial Regex BunSkipRegex();

    [GeneratedRegex(@"^---\s+PASS:", RegexOptions.Multiline)]
    private static partial Regex GoPassRegex();

    [GeneratedRegex(@"^---\s+FAIL:", RegexOptions.Multiline)]
    private static partial Regex GoTallyFailRegex();

    [GeneratedRegex(@"^---\s+SKIP:", RegexOptions.Multiline)]
    private static partial Regex GoSkipRegex();

    [GeneratedRegex(@"^(?:ok|FAIL)\s+\S+", RegexOptions.Multiline)]
    private static partial Regex GoPackageRegex();

    [GeneratedRegex(@"^FAIL\s+\S+", RegexOptions.Multiline)]
    private static partial Regex GoFailRegex();
}
