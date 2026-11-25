using Microsoft.AspNetCore.SignalR.Client;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Conexão realtime (SignalR /ws) para canais: recebe message:new,
/// user:typing e presence; envia indicador de digitação.
/// Reconecta automaticamente; falha do hub não quebra o REST.
/// </summary>
public class RealtimeService : IAsyncDisposable
{
    private HubConnection? _connection;
    private string? _token;

    /// <summary>Nova mensagem de canal recebida.</summary>
    public event Action<ChannelMessageResponse>? OnMessage;

    /// <summary>Indicador de digitação (channelId, userId, name).</summary>
    public event Action<string, string, string>? OnTyping;

    /// <summary>Presença atualizada (channelId, userIds online).</summary>
    public event Action<string, string[]>? OnPresence;

    /// <summary>Reações de uma mensagem atualizadas (channelId, messageId, agregado).</summary>
    public event Action<string, string, List<ChannelReactionResponse>>? OnReaction;

    /// <summary>Mensagem atualizada (channelId, messageId, isPinned).</summary>
    public event Action<string, string, bool>? OnMessageUpdate;

    /// <summary>Mensagem de chat editada em outra aba/sessão (chatId, messageId, novo conteúdo).</summary>
    public event Action<string, string, string>? OnChatMessageUpdated;

    /// <summary>Mensagem de chat deletada em outra aba/sessão (chatId, messageId).</summary>
    public event Action<string, string>? OnChatMessageDeleted;

    /// <summary>Edição remota de nota aceita (noteId, userId, texto, nova versão).</summary>
    public event Action<string, string, string, long>? OnNoteUpdate;

    /// <summary>Edição local rejeitada (noteId, texto atual, versão atual) — resincronizar.</summary>
    public event Action<string, string, long>? OnNoteRejected;

    /// <summary>Roster de awareness da nota (noteId, entradas).</summary>
    public event Action<string, List<NotePresenceInfo>>? OnNotePresence;

    /// <summary>Cursor remoto em movimento (noteId, userId, name, color, offset).</summary>
    public event Action<string, string, string, string, int>? OnNoteCursor;

    /// <summary>Run de chat terminou no servidor (SPEC-20261007-chat-notifications).</summary>
    public event Action<RunCompletedInfo>? OnRunCompleted;

    /// <summary>Se a conexão com o hub está ativa.</summary>
    public bool Connected => _connection?.State == HubConnectionState.Connected;

    /// <summary>Conecta ao hub com o JWT do usuário (idempotente).</summary>
    /// <param name="baseUri">Endereço base do servidor.</param>
    /// <param name="token">JWT atual.</param>
    public async Task ConnectAsync(string baseUri, string? token)
    {
        if (token is null)
        {
            return;
        }
        if (_connection is not null)
        {
            if (_token == token
                && _connection.State is HubConnectionState.Connected
                    or HubConnectionState.Connecting
                    or HubConnectionState.Reconnecting)
            {
                return;
            }
            await _connection.DisposeAsync();
            _connection = null;
        }

        _token = token;
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(baseUri), "ws"), options =>
                options.AccessTokenProvider = () => Task.FromResult<string?>(_token))
            .WithAutomaticReconnect()
            .Build();

        _connection.On<ChannelMessageResponse>(
            "message:new", m => OnMessage?.Invoke(m));
        _connection.On<string, string, string>(
            "user:typing", (c, u, n) => OnTyping?.Invoke(c, u, n));
        _connection.On<string, string[]>(
            "presence", (c, users) => OnPresence?.Invoke(c, users));
        _connection.On<ReactionEvent>(
            "reaction", e => OnReaction?.Invoke(e.ChannelId, e.MessageId, e.Reactions));
        _connection.On<MessageUpdateEvent>(
            "message:update", e => OnMessageUpdate?.Invoke(e.ChannelId, e.Id, e.IsPinned));
        _connection.On<string, string, string>(
            "message:updated", (c, m, content) => OnChatMessageUpdated?.Invoke(c, m, content));
        _connection.On<string, string>(
            "message:deleted", (c, m) => OnChatMessageDeleted?.Invoke(c, m));
        _connection.On<string, string, string, long>(
            "note:update", (n, u, t, v) => OnNoteUpdate?.Invoke(n, u, t, v));
        _connection.On<string, string, long>(
            "note:rejected", (n, t, v) => OnNoteRejected?.Invoke(n, t, v));
        _connection.On<string, List<NotePresenceInfo>>(
            "note:presence", (n, roster) => OnNotePresence?.Invoke(n, roster));
        _connection.On<string, string, string, string, int>(
            "note:cursor", (n, u, name, color, c) => OnNoteCursor?.Invoke(n, u, name, color, c));
        _connection.On<RunCompletedInfo>(
            "run.completed", e => OnRunCompleted?.Invoke(e));

        try
        {
            await _connection.StartAsync();
        }
        catch (Exception)
        {
            // Hub indisponível: REST segue funcionando sem realtime.
        }
    }

    /// <summary>Entra no grupo de eventos de um chat (edições propagadas em tempo real).</summary>
    /// <param name="chatId">Chat a acompanhar.</param>
    public async Task JoinChatAsync(string chatId)
    {
        if (Connected)
        {
            try
            {
                await _connection!.InvokeAsync("JoinChat", chatId);
            }
            catch (Exception)
            {
                // Hub indisponível — join é best-effort.
            }
        }
    }

    /// <summary>Sai do grupo de eventos de um chat.</summary>
    /// <param name="chatId">Chat a deixar de acompanhar.</param>
    public async Task LeaveChatAsync(string chatId)
    {
        if (Connected)
        {
            try
            {
                await _connection!.InvokeAsync("LeaveChat", chatId);
            }
            catch (Exception)
            {
                // Hub indisponível — saída é best-effort.
            }
        }
    }

    /// <summary>Entra no grupo de colaboração de uma nota.</summary>
    /// <param name="noteId">Nota a acompanhar.</param>
    public Task JoinNoteAsync(string noteId) => TryInvokeAsync("JoinNote", noteId);

    /// <summary>Sai do grupo de colaboração de uma nota.</summary>
    /// <param name="noteId">Nota a deixar.</param>
    public Task LeaveNoteAsync(string noteId) => TryInvokeAsync("LeaveNote", noteId);

    /// <summary>
    /// Envia uma edição colaborativa (texto + versão vista).
    /// </summary>
    /// <param name="noteId">Nota editada.</param>
    /// <param name="text">Conteúdo completo proposto.</param>
    /// <param name="version"><c>UpdatedAt</c> visto por último.</param>
    /// <returns>Nova versão aceita pelo servidor; -1 quando rejeitada/sem conexão.</returns>
    public async Task<long> SendNoteUpdateAsync(string noteId, string text, long version)
    {
        if (!Connected)
        {
            return -1;
        }
        try
        {
            return await _connection!.InvokeAsync<long>("NoteUpdate", noteId, text, version);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>Propaga a posição do cursor local ao grupo da nota.</summary>
    /// <param name="noteId">Nota em edição.</param>
    /// <param name="cursor">Offset do cursor no texto.</param>
    public Task SendNotePresenceAsync(string noteId, int cursor) =>
        TryInvokeAsync("NotePresence", noteId, cursor);

    private async Task TryInvokeAsync(string method, params object?[] args)
    {
        if (Connected)
        {
            try
            {
                // InvokeCoreAsync aceita object?[]: InvokeAsync(method, args) resolve
                // para o overload (method, object? arg1) e mandaria o array inteiro
                // como um único argumento (binding do hub falhava com InvalidDataException).
                await _connection!.InvokeCoreAsync(method, args!, CancellationToken.None);
            }
            catch (Exception)
            {
                // Hub indisponível — broadcast é best-effort.
            }
        }
    }

    /// <summary>Envia indicador de digitação ao canal.</summary>
    /// <param name="channelId">Canal alvo.</param>
    public async Task SendTypingAsync(string channelId)
    {
        if (Connected)
        {
            try
            {
                // Hub indisponível — typing é best-effort.
                await _connection!.InvokeAsync("Typing", channelId);
            }
            catch (Exception)
            {
                // Hub indisponível — typing é best-effort.
            }
        }
    }

    private sealed record ReactionEvent(
        [property: System.Text.Json.Serialization.JsonPropertyName("channel_id")] string ChannelId,
        [property: System.Text.Json.Serialization.JsonPropertyName("message_id")] string MessageId,
        List<ChannelReactionResponse> Reactions);

    private sealed record MessageUpdateEvent(
        [property: System.Text.Json.Serialization.JsonPropertyName("channel_id")] string ChannelId,
        string Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("is_pinned")] bool IsPinned);

    /// <summary>Payload do evento run.completed (SPEC-20261007-chat-notifications).</summary>
    /// <param name="RunId">Run finalizada.</param>
    /// <param name="ChatId">Chat da run.</param>
    /// <param name="Title">Título do chat.</param>
    /// <param name="Status">Status terminal.</param>
    /// <param name="Error">Erro, quando failed.</param>
    /// <param name="Snippet">Trecho da resposta (≤160 chars).</param>
    public sealed record RunCompletedInfo(
        string RunId, string ChatId, string? Title,
        string Status, string? Error, string? Snippet);

    /// <summary>Entrada de awareness de uma nota (roster do hub).</summary>
    /// <param name="UserId">Usuário.</param>
    /// <param name="Name">Nome de exibição.</param>
    /// <param name="Color">Cor do cursor/presença.</param>
    /// <param name="Cursor">Offset do cursor no texto.</param>
    public sealed record NotePresenceInfo(
        [property: System.Text.Json.Serialization.JsonPropertyName("userId")] string UserId,
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("color")] string Color,
        [property: System.Text.Json.Serialization.JsonPropertyName("cursor")] int Cursor);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}
