using System.Collections.Concurrent;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Rate limiting e lockout de login em memória (single-instance; o backplane
/// distribuído fica para a camada multi-instância). Chaves de login são
/// <c>email|ip</c>; janelas de requisição são por usuário autenticado.
/// </summary>
public sealed class RateLimitService
{
    private sealed class LoginState
    {
        public int Failures;
        public long LockedUntil;
    }

    private sealed class SlidingWindow
    {
        public long WindowStart;
        public int Count;
    }

    private readonly ConcurrentDictionary<string, LoginState> _logins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SlidingWindow> _requests = new(StringComparer.Ordinal);

    /// <summary>Indica se a chave <c>email|ip</c> está bloqueada neste instante.</summary>
    public bool IsLoginLocked(string key, RateLimitConfig config, long now)
    {
        if (!_logins.TryGetValue(key, out var state))
        {
            return false;
        }
        lock (state)
        {
            if (state.LockedUntil > now)
            {
                return true;
            }
            if (state.LockedUntil != 0 && state.LockedUntil <= now)
            {
                // Lockout expirado — zera contagem e recomeça.
                state.Failures = 0;
                state.LockedUntil = 0;
            }
            return false;
        }
    }

    /// <summary>Registra uma falha de login; ao atingir o máximo, aplica o lockout.</summary>
    public void RecordLoginFailure(string key, RateLimitConfig config, long now)
    {
        var state = _logins.GetOrAdd(key, _ => new LoginState());
        lock (state)
        {
            if (state.LockedUntil != 0 && state.LockedUntil <= now)
            {
                state.Failures = 0;
                state.LockedUntil = 0;
            }
            state.Failures++;
            if (state.Failures >= config.LoginMaxFailures)
            {
                state.LockedUntil = now + config.LoginLockoutSeconds;
            }
        }
    }

    /// <summary>Limpa falhas/lockout da chave (login bem-sucedido).</summary>
    public void ResetLogin(string key) => _logins.TryRemove(key, out _);

    /// <summary>Limpa o lockout inteiro de um e-mail (todas as origens IP) — uso administrativo.</summary>
    /// <returns>Quantidade de entradas removidas.</returns>
    public int ResetLoginByEmail(string email)
    {
        var removed = 0;
        foreach (var key in _logins.Keys.Where(k =>
                     k.StartsWith(email + "|", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(k, email, StringComparison.OrdinalIgnoreCase)))
        {
            if (_logins.TryRemove(key, out _))
            {
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// Consome uma requisição da janela deslizante do usuário.
    /// Retorna <c>null</c> quando permitido, ou os segundos até liberar (Retry-After).
    /// </summary>
    public long? TryAcquire(string userId, RateLimitConfig config, long now)
    {
        var window = _requests.GetOrAdd(userId, _ => new SlidingWindow { WindowStart = now });
        lock (window)
        {
            if (now - window.WindowStart >= config.WindowSeconds)
            {
                window.WindowStart = now;
                window.Count = 0;
            }
            if (window.Count >= config.PermitLimit)
            {
                return window.WindowStart + config.WindowSeconds - now;
            }
            window.Count++;
            return null;
        }
    }
}
