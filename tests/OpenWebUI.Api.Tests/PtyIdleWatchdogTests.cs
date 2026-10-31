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
    public void Watchdog_ShouldDetectIdleAfterThreshold()
    {
        var watchdog = new PtyIdleWatchdog(idleThreshold: TimeSpan.FromMilliseconds(50), maxTimeout: TimeSpan.FromSeconds(10));
        
        // Inicialmente não deve estar ocioso
        var triggered = watchdog.CheckStatus(out var timedOut);
        Assert.That(triggered, Is.False);
        Assert.That(timedOut, Is.False);

        // Esperar passar do threshold
        Thread.Sleep(80);

        triggered = watchdog.CheckStatus(out timedOut);
        Assert.That(triggered, Is.True);
        Assert.That(timedOut, Is.False);
    }

    [Test]
    public void Watchdog_RecordActivity_ResetsIdleState()
    {
        var watchdog = new PtyIdleWatchdog(idleThreshold: TimeSpan.FromMilliseconds(50), maxTimeout: TimeSpan.FromSeconds(10));
        Thread.Sleep(80);
        
        watchdog.RecordActivity();
        var triggered = watchdog.CheckStatus(out var timedOut);
        Assert.That(triggered, Is.False);
    }
}
