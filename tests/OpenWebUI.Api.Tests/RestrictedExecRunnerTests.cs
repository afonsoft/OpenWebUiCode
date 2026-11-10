using NUnit.Framework;
using OpenWebUI.Infrastructure.Context;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261031-agent-prompt-pack RF-003: allowlist de comandos do
/// <see cref="RestrictedExecRunner"/> — <c>rg</c>/<c>readfile</c>/<c>tree</c>
/// confinados ao workspace, com limites e erros por comando.
/// </summary>
[TestFixture]
public class RestrictedExecRunnerTests
{
    private string _workspace = null!;

    [SetUp]
    public void SetUp()
    {
        _workspace = Path.Join(Path.GetTempPath(), $"rex-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(Path.Join(_workspace, "src"));
        Directory.CreateDirectory(Path.Join(_workspace, "bin"));
        File.WriteAllText(Path.Join(_workspace, "src", "a.cs"),
            "linha um\nalvo aqui\nlinha tres\nalvo de novo\nfim");
        File.WriteAllText(Path.Join(_workspace, "src", "b.cs"),
            "outro arquivo\nalvo longe");
        File.WriteAllText(Path.Join(_workspace, "bin", "ignorado.dll"),
            "alvo dentro de bin");
        File.WriteAllText(Path.Join(_workspace, "top.txt"), "raiz alvo");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    /// <summary>rg casa em vários arquivos, ignora bin/ e agrupa linhas
    /// consecutivas no mesmo snippet.</summary>
    [Test]
    public void Rg_EncontraMatches_IgnoraBinEAgrupa()
    {
        var result = RestrictedExecRunner.Run(_workspace, ["rg alvo"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Is.Empty);
            // a.cs tem matches nas linhas 2 e 4 (não consecutivas → 2
            // grupos), b.cs e top.txt um cada: 4 snippets no total.
            Assert.That(result.Snippets, Has.Count.EqualTo(4));
            Assert.That(result.Snippets.Count(s => s.Path.EndsWith("a.cs")),
                Is.EqualTo(2));
            Assert.That(result.Snippets.Single(
                s => s.Path.EndsWith("b.cs")).Text, Does.Contain("alvo"));
            Assert.That(result.Snippets.Any(s => s.Path.Contains("bin")),
                Is.False, "bin/ é excluído da enumeração");
        });
    }

    /// <summary>rg sem padrão e regex inválida viram CommandError (não
    /// abortam o lote).</summary>
    [Test]
    public void Rg_ErrosDeUso_ViramCommandError()
    {
        var result = RestrictedExecRunner.Run(_workspace,
            ["rg", "rg [unclosed"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Snippets, Is.Empty);
            Assert.That(result.Errors, Has.Count.EqualTo(2));
            Assert.That(result.Errors[0].Error, Does.Contain("padrão"));
            Assert.That(result.Errors[1].Error, Does.Contain("regex"));
        });
    }

    /// <summary>readfile devolve range numerado; argumentos errados,
    /// arquivo inexistente e range inválido viram CommandError.</summary>
    [Test]
    public void ReadFile_RangeEErros()
    {
        var ok = RestrictedExecRunner.Run(_workspace,
            ["readfile src/a.cs 2 4"]);
        Assert.Multiple(() =>
        {
            Assert.That(ok.Errors, Is.Empty);
            var snippet = ok.Snippets.Single();
            Assert.That(snippet.Start, Is.EqualTo(2));
            Assert.That(snippet.Text, Does.Contain("2: alvo aqui"));
            Assert.That(snippet.Text, Does.Contain("4: alvo de novo"));
            Assert.That(snippet.Text, Does.Not.Contain("1: linha um"));
        });

        var errors = RestrictedExecRunner.Run(_workspace,
        [
            "readfile src/a.cs",
            "readfile nao-existe.txt 1 5",
            "readfile src/a.cs 0 0",
            "readfile src/a.cs 9 3",
        ]);
        Assert.Multiple(() =>
        {
            Assert.That(errors.Snippets, Is.Empty);
            Assert.That(errors.Errors, Has.Count.EqualTo(4));
            Assert.That(errors.Errors[0].Error, Does.Contain("uso:"));
            Assert.That(errors.Errors[1].Error,
                Does.Contain("não existe").Or.Contain("fora"));
            Assert.That(errors.Errors[2].Error, Does.Contain("range"));
            Assert.That(errors.Errors[3].Error, Does.Contain("range"));
        });
    }

    /// <summary>tree lista a árvore respeitando profundidade; diretório
    /// inexistente vira CommandError.</summary>
    [Test]
    public void Tree_ListaRespeitandoDepth()
    {
        var ok = RestrictedExecRunner.Run(_workspace, ["tree . 2"]);
        Assert.Multiple(() =>
        {
            Assert.That(ok.Errors, Is.Empty);
            var snippet = ok.Snippets.Single();
            Assert.That(snippet.Text, Does.Contain("a.cs"));
            Assert.That(snippet.Text, Does.Contain("top.txt"));
        });

        var bad = RestrictedExecRunner.Run(_workspace,
            ["tree diretorio-que-nao-existe"]);
        Assert.Multiple(() =>
        {
            Assert.That(bad.Snippets, Is.Empty);
            Assert.That(bad.Errors.Single().Error,
                Does.Contain("não existe").Or.Contain("fora"));
        });
    }

    /// <summary>Verbo fora da allowlist vira CommandError explicando o
    /// conjunto permitido.</summary>
    [Test]
    public void ComandoForaDaAllowlist_ViraErro()
    {
        var result = RestrictedExecRunner.Run(_workspace, ["curl x"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Snippets, Is.Empty);
            Assert.That(result.Errors.Single().Error,
                Does.Contain("não permitido"));
        });
    }

    /// <summary>Só os primeiros <see cref="RestrictedExecRunner.MaxCommands"/>
    /// comandos rodam — o resto do lote é ignorado.</summary>
    [Test]
    public void LimiteDeComandos_TruncaLote()
    {
        var comandos = Enumerable.Range(0, RestrictedExecRunner.MaxCommands + 2)
            .Select(_ => "tree . 1").ToArray();

        var result = RestrictedExecRunner.Run(_workspace, comandos);

        Assert.That(result.Snippets,
            Has.Count.EqualTo(RestrictedExecRunner.MaxCommands));
    }
}
