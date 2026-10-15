namespace OpenWebUI.Client.Services;

/// <summary>
/// Estado da inbox "aguardando você" (D2, SPEC-20261009-attention-inbox):
/// quantos chats do usuário têm run bloqueada esperando ação dele
/// (aprovação ou pergunta). O badge aparece na sidebar (expandida/rail) e
/// na barra mobile — o estado é compartilhado para os três renderizarem a
/// mesma contagem e para o clique mobile ligar o filtro da sidebar.
/// Poll de 30s mantém o número fresco sem SignalR; cada consumidor pode
/// chamar <see cref="RefreshAsync"/> para atualizar já (ex.: após decidir
/// uma aprovação). Quando o count muda, <see cref="Changed"/> dispara —
/// a sidebar recarrega a lista para atualizar os marcadores dos itens.
/// </summary>
public sealed class AttentionInboxState(ApiService api) : IAsyncDisposable
{
    /// <summary>Intervalo do poll de contagem.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _loop;
    private bool _started;

    /// <summary>Chats aguardando ação do usuário (0 = badge oculto).</summary>
    public int Count { get; private set; }

    /// <summary>Filtro "aguardando" ativo na lista de chats da sidebar.</summary>
    public bool FilterActive { get; private set; }

    /// <summary>Disparado quando <see cref="Count"/> ou <see cref="FilterActive"/> muda.</summary>
    public event Action? Changed;

    /// <summary>
    /// Inicia o poll (idempotente). O primeiro refresh é imediato.
    /// </summary>
    public async Task EnsureStartedAsync()
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _loop = new CancellationTokenSource();
            _timer = new PeriodicTimer(PollInterval);
            _ = PollLoopAsync(_loop.Token);
        }
        finally
        {
            _gate.Release();
        }

        await RefreshAsync();
    }

    /// <summary>Liga/desliga o filtro "aguardando" da sidebar.</summary>
    public void SetFilter(bool active)
    {
        if (FilterActive == active)
        {
            return;
        }

        FilterActive = active;
        Changed?.Invoke();
    }

    /// <summary>Busca a contagem agora; dispara <see cref="Changed"/> se mudou.</summary>
    public async Task RefreshAsync()
    {
        int count;
        try
        {
            count = await api.GetAttentionCountAsync();
        }
        catch (Exception ex) when (ClientErrors.IsTransient(ex))
        {
            // Sem rede — mantém o último número conhecido.
            return;
        }
        catch (Exception ex)
        {
            // Refresh falha de forma inesperada — loga e mantém o último número.
            await Console.Error.WriteLineAsync($"[AttentionInboxState] refresh falhou: {ex.Message}");
            return;
        }

        if (count == Count)
        {
            return;
        }

        Count = count;
        if (count == 0 && FilterActive)
        {
            FilterActive = false;
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // Subscriber (componente) falhando no desmonte não pode matar o
            // estado nem o poll — loga e segue.
            await Console.Error.WriteLineAsync(
                $"[AttentionInboxState] subscriber falhou: {ex.Message}");
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            while (_timer is not null && await _timer.WaitForNextTickAsync(ct))
            {
                await RefreshAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose encerra o loop.
        }
        catch (Exception ex)
        {
            // Loop fire-and-forget: nada pode escapar para a blazor-error-ui.
            await Console.Error.WriteLineAsync($"[AttentionInboxState] poll falhou: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _loop?.Cancel();
        _timer?.Dispose();
        _loop?.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
