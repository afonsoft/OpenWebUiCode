using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Isola o escopo global <c>~/.agents</c> da suíte (SPEC-20261016-global-
/// agents-scope): a máquina de dev/CI pode ter skills/commands reais
/// instalados, o que quebraria fixtures que esperam listas vazias. Toda a
/// assembly roda com home fake vazio; testes do próprio escopo passam
/// <c>home:</c> explícito que tem precedência.
/// </summary>
[SetUpFixture]
public class TestHomeIsolation
{
    private static string _home = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _home = Path.Join(Path.GetTempPath(), $"owui-test-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
        SkillDiscoveryService.HomeResolver = () => _home;
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        SkillDiscoveryService.HomeResolver = null;
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { }
    }
}
