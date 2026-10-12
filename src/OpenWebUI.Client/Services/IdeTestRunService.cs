using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Estado compartilhado do runner de testes do /ide
/// (SPEC-20261009-ide-mentions-tests, RF-004): inicia/confirma o run,
/// faz poll do status e avisa os painéis via <see cref="Changed"/>.
/// <see cref="AttentionWanted"/> pede aos painéis para saltar à aba Tests.
/// </summary>
public sealed class IdeTestRunService(ApiService api)
{
    private CancellationTokenSource? _pollCts;

    /// <summary>Último status conhecido do run (null antes do primeiro start).</summary>
    public TestRunStatusResponse? Run { get; private set; }

    /// <summary>Comando aguardando aprovação (WorkspaceWrite) — o cartão o exibe antes de rodar.</summary>
    public string? PendingCommand { get; private set; }

    /// <summary>Motivo do gate devolvido pelo classificador.</summary>
    public string? PendingReason { get; private set; }

    /// <summary>Erro de início (422: sem manifesto, comando negado, sem binding).</summary>
    public string? Error { get; private set; }

    /// <summary>POST em voo.</summary>
    public bool Starting { get; private set; }

    /// <summary>Pede aos painéis para abrir a aba Tests na próxima renderização.</summary>
    public bool AttentionWanted { get; set; }

    /// <summary>Cartão de aprovação pendente.</summary>
    public bool ApprovalPending => PendingCommand is not null;

    /// <summary>Run em execução (POST em voo ou job running).</summary>
    public bool Running => Starting || Run?.State == "running";

    /// <summary>Dispara quando qualquer propriedade muda (painéis fazem StateHasChanged).</summary>
    /// <summary>Chat cujo binding resolve o workdir do run (SPEC-20261010-workspace-chatid-scope); null = binding global.</summary>
    public string? ChatId { get; set; }

    public event Action? Changed;

    /// <summary>Inicia o run; <paramref name="confirmed"/> confirma o comando pendente.</summary>
    public async Task StartAsync(bool confirmed = false)
    {
        if (Starting)
        {
            return;
        }
        if (!confirmed)
        {
            Run = null;
            Error = null;
            PendingCommand = null;
            PendingReason = null;
        }
        Starting = true;
        AttentionWanted = true;
        Changed?.Invoke();
        try
        {
            var result = await api.StartTestRunAsync(confirmed, ChatId);
            if (result.Body is { RequiresApproval: true } gated)
            {
                PendingCommand = gated.Command;
                PendingReason = gated.Reason;
            }
            else if (result.Body is { JobId: { } jobId })
            {
                PendingCommand = null;
                PendingReason = null;
                Error = null;
                if (_pollCts is not null) { await _pollCts.CancelAsync(); }
                _pollCts = new CancellationTokenSource();
                _ = PollAsync(jobId, _pollCts.Token);
            }
            else
            {
                Error = result.Detail ?? "Não foi possível iniciar os testes.";
            }
        }
        catch (Exception ex) when (ClientErrors.IsTransient(ex))
        {
            Error = "Não foi possível iniciar os testes.";
        }
        finally
        {
            Starting = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Aprova o comando pendente (re-POST com confirmed=true).</summary>
    public Task ApproveAsync() => StartAsync(confirmed: true);

    /// <summary>Rejeita o comando pendente.</summary>
    public void Reject()
    {
        PendingCommand = null;
        PendingReason = null;
        Changed?.Invoke();
    }

    /// <summary>Para o polling do run atual (não mata o job no servidor).</summary>
    public void StopPolling() => _pollCts?.Cancel();

    private async Task PollAsync(string jobId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var run = await api.GetTestRunAsync(jobId);
                if (run is null)
                {
                    break;
                }
                Run = run;
                Changed?.Invoke();
                if (run.State != "running")
                {
                    break;
                }
                await Task.Delay(1500, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // stop/cancel esperado.
        }
        catch (Exception ex)
        {
            // Poll fire-and-forget: escape derrubaria a UI com o banner.
            await Console.Error.WriteLineAsync($"[IdeTestRunService] poll falhou: {ex.Message}");
        }
    }
}
