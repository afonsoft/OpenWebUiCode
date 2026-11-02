using NUnit.Framework;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes unitários para o monitor de ociosidade em PTY.
/// # Covers RF-003
/// </summary>
[TestFixture]
public class PtyIdleWatchdogTests
{
    [Test]
    public async Task Watchdog_ShouldDetectIdleAfterThreshold()
    {
        var watchdog = new PtyIdleWatchdog(idleThreshold: TimeSpan.FromMilliseconds(50), maxTimeout: TimeSpan.FromSeconds(10));
        
        // Inicialmente não deve estar ocioso
        var triggered = watchdog.CheckStatus(out var timedOut);
        Assert.That(triggered, Is.False);
        Assert.That(timedOut, Is.False);

        // Esperar passar do threshold
        await Task.Delay(80);

        triggered = watchdog.CheckStatus(out timedOut);
        Assert.That(triggered, Is.True);
        Assert.That(timedOut, Is.False);
    }

    [Test]
    public async Task Watchdog_RecordActivity_ResetsIdleState()
    {
        var watchdog = new PtyIdleWatchdog(idleThreshold: TimeSpan.FromMilliseconds(50), maxTimeout: TimeSpan.FromSeconds(10));
        await Task.Delay(80);
        
        watchdog.RecordActivity();
        var triggered = watchdog.CheckStatus(out var timedOut);
        Assert.That(triggered, Is.False);
    }
}
