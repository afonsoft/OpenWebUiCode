using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>Cliente HTTP tipado para a API do backend .NET.</summary>
public class ApiService(HttpClient http, AuthService auth)
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    // ---------------- Chats ----------------

    /// <summary>Lista os chats do usuário, opcionalmente filtrados por texto.</summary>
    /// <param name="attention">Quando true, só chats aguardando ação do usuário (D2).</param>
    public async Task<List<ChatSummaryResponse>> GetChatsAsync(
        string? query = null, bool includeFolders = false, bool attention = false)
    {
        var uri = "/api/v1/chats/?includeFolders=" + (includeFolders ? "true" : "false");
        if (!string.IsNullOrWhiteSpace(query))
        {
            uri += $"&query={Uri.EscapeDataString(query)}";
        }
        if (attention)
        {
            uri += "&attention=1";
        }

        return await SendAsync<List<ChatSummaryResponse>>(HttpMethod.Get, uri) ?? [];
    }

    /// <summary>Contagem de chats aguardando ação do usuário (D2) — poll leve do badge.</summary>
    public async Task<int> GetAttentionCountAsync()
    {
        var result = await SendAsync<AttentionCountResponse>(
            HttpMethod.Get, "/api/v1/chats/attention/count");
        return result?.Count ?? 0;
    }

    /// <summary>Lista chats arquivados.</summary>
    public async Task<List<ChatSummaryResponse>> GetArchivedChatsAsync() =>
        await SendAsync<List<ChatSummaryResponse>>(HttpMethod.Get, "/api/v1/chats/archived") ?? [];

    /// <summary>Lista chats fixados.</summary>
    public async Task<List<ChatSummaryResponse>> GetPinnedChatsAsync() =>
        await SendAsync<List<ChatSummaryResponse>>(HttpMethod.Get, "/api/v1/chats/pinned") ?? [];

    /// <summary>Obtém um chat completo com mensagens.</summary>
    public Task<ChatResponse?> GetChatAsync(string id) =>
        SendAsync<ChatResponse>(HttpMethod.Get, $"/api/v1/chats/{id}");

    /// <summary>Obtém um chat com apenas a cauda de <paramref name="tail"/>
    /// mensagens do caminho ativo (SPEC-20261019-chat-window-perf RF-001).</summary>
    public Task<ChatResponse?> GetChatTailAsync(string id, int tail) =>
        SendAsync<ChatResponse>(HttpMethod.Get, $"/api/v1/chats/{id}?tail={tail}");

    /// <summary>Página de mensagens anteriores a <paramref name="beforeId"/> no
    /// caminho ativo (SPEC-20261019-chat-window-perf RF-001).</summary>
    public Task<ChatMessagesPage?> GetChatMessagesAsync(string id, string? beforeId, int take = 50) =>
        SendAsync<ChatMessagesPage>(HttpMethod.Get,
            $"/api/v1/chats/{id}/messages?take={take}{(beforeId is null ? "" : $"&before={Uri.EscapeDataString(beforeId)}")}");

    /// <summary>Obtém um chat compartilhado publicamente (sem autenticação).</summary>
    public async Task<SharedChatResponse?> GetSharedChatAsync(string shareId)
    {
        using var response = await http.GetAsync($"/api/v1/chats/share/{Uri.EscapeDataString(shareId)}");
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<SharedChatResponse>(JsonOptions)
            : null;
    }

    /// <summary>Cria um chat e retorna o recurso criado.</summary>
    public Task<ChatResponse?> CreateChatAsync(ChatUpsertRequest request) =>
        SendAsync<ChatResponse>(HttpMethod.Post, "/api/v1/chats/", request);

    /// <summary>Atualiza título, modelos e mensagens de um chat.</summary>
    /// <summary>Lista paginada de todos os chats (admin).</summary>
    public Task<AdminChatListResponse?> GetAllChatsAdminAsync(string? query = null, int page = 1)
    {
        var uri = $"/api/v1/chats/all?page={page}"
            + (string.IsNullOrWhiteSpace(query) ? "" : $"&query={Uri.EscapeDataString(query)}");
        return SendAsync<AdminChatListResponse>(HttpMethod.Get, uri);
    }

    /// <summary>Versões anteriores de uma mensagem.</summary>
    public Task<List<ChatMessageVersionModel>?> GetMessageVersionsAsync(string chatId, string messageId) =>
        SendAsync<List<ChatMessageVersionModel>>(
            HttpMethod.Get, $"/api/v1/chats/{chatId}/messages/{messageId}/versions");

    public Task<ChatResponse?> UpdateChatAsync(string id, ChatUpsertRequest request) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{id}", request);

    /// <summary>
    /// Atualizações parciais do chat (SPEC-20261007-chat-tool-streaming):
    /// <paramref name="approvalPreset"/> e/ou <paramref name="title"/>
    /// (rename leve, sem enviar o chat inteiro).
    /// </summary>
    public Task<ChatResponse?> PatchChatAsync(
        string id, string? approvalPreset = null, string? title = null,
        string? mode = null, bool? advisorEnabled = null) =>
        SendAsync<ChatResponse>(HttpMethod.Patch,
            $"/api/v1/chats/{id}",
            new ChatPatchRequest(approvalPreset, title, mode, advisorEnabled));

    /// <summary>Alterna o estado de fixado de um chat.</summary>
    public Task<ChatResponse?> TogglePinChatAsync(string id) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{id}/pin");

    /// <summary>Alterna o estado de arquivado de um chat.</summary>
    public Task<ChatResponse?> ToggleArchiveChatAsync(string id) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{id}/archive");

    /// <summary>Gera (ou reutiliza) o link público de compartilhamento do chat.</summary>
    public Task<ChatResponse?> ShareChatAsync(string id) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{id}/share");

    /// <summary>Remove o compartilhamento público do chat.</summary>
    public async Task<bool> UnshareChatAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/chats/{id}/share");

    /// <summary>Clona um chat inteiro.</summary>
    public Task<ChatResponse?> CloneChatAsync(string id) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{id}/clone");

    /// <summary>Move o chat para uma pasta (folderId vazio remove da pasta).</summary>
    public Task<ChatResponse?> SetChatFolderAsync(string id, string? folderId) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{id}/folder",
            new { folderId });

    /// <summary>Define as tags de um chat.</summary>
    public Task<ChatResponse?> SetChatTagsAsync(string id, IReadOnlyList<string> tags) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{id}/tags",
            new { tags });

    /// <summary>Atualiza o conteúdo de uma mensagem.</summary>
    public Task<ChatResponse?> UpdateMessageAsync(string chatId, string messageId, string content) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{chatId}/messages/{messageId}",
            new MessageUpdateRequest(content));

    /// <summary>Exclui uma mensagem e as seguintes.</summary>
    public Task<ChatResponse?> DeleteMessageAsync(string chatId, string messageId) =>
        SendAsync<ChatResponse>(HttpMethod.Delete, $"/api/v1/chats/{chatId}/messages/{messageId}");

    /// <summary>Leafs divergentes do chat (SPEC-20261013-chat-branching RF-003).</summary>
    public async Task<List<BranchLeafResponse>> GetBranchesAsync(string chatId) =>
        await SendAsync<List<BranchLeafResponse>>(
            HttpMethod.Get, $"/api/v1/chats/{chatId}/branches") ?? [];

    /// <summary>Ativa uma mensagem como leaf — retorna o caminho renderizado (RF-004).</summary>
    public Task<ChatResponse?> ActivateBranchAsync(string chatId, string leafId) =>
        SendAsync<ChatResponse>(HttpMethod.Post,
            $"/api/v1/chats/{chatId}/branches/activate",
            new ActivateBranchRequest(leafId));

    /// <summary>Remove um chat.</summary>
    public async Task<bool> DeleteChatAsync(string id)
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, $"/api/v1/chats/{id}");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Exporta todos os chats do usuário.</summary>
    public Task<object?> ExportChatsAsync() =>
        SendAsync<object>(HttpMethod.Get, "/api/v1/chats/all/db");

    // ---------------- Pastas ----------------

    /// <summary>Lista pastas do usuário com os chats de cada uma.</summary>
    public async Task<List<FolderWithChats>> GetFoldersAsync()
    {
        var raw = await SendAsync<List<System.Text.Json.JsonElement>>(
            HttpMethod.Get, "/api/v1/folders/");
        if (raw is null)
        {
            return [];
        }

        var result = new List<FolderWithChats>();
        foreach (var element in raw)
        {
            var folder = new FolderWithChats
            {
                Id = element.GetProperty("id").GetString() ?? string.Empty,
                Name = element.GetProperty("name").GetString() ?? string.Empty,
            };
            if (element.TryGetProperty("items", out var items)
                && items.TryGetProperty("chats", out var chats))
            {
                foreach (var chat in chats.EnumerateArray())
                {
                    folder.Chats.Add(new ChatSummaryResponse(
                        chat.GetProperty("id").GetString() ?? string.Empty,
                        chat.GetProperty("title").GetString() ?? string.Empty,
                        chat.TryGetProperty("pinned", out var p) && p.GetBoolean(),
                        chat.TryGetProperty("folderId", out var f) ? f.GetString() : null,
                        [],
                        chat.GetProperty("createdAt").GetInt64(),
                        chat.GetProperty("updatedAt").GetInt64(),
                        chat.TryGetProperty("awaiting", out var a) && a.GetBoolean()));
                }
            }

            result.Add(folder);
        }

        return result;
    }

    /// <summary>Cria uma pasta.</summary>
    public Task<FolderResponse?> CreateFolderAsync(string name) =>
        SendAsync<FolderResponse>(HttpMethod.Post, "/api/v1/folders/",
            new FolderUpsertRequest(name, null));

    /// <summary>Renomeia uma pasta.</summary>
    public Task<FolderResponse?> UpdateFolderAsync(string id, string name) =>
        SendAsync<FolderResponse>(HttpMethod.Post, $"/api/v1/folders/{id}/update",
            new FolderUpsertRequest(name, null));

    /// <summary>Remove uma pasta.</summary>
    public async Task<bool> DeleteFolderAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/folders/{id}");

    // ---------------- Modelos ----------------

    /// <summary>Lista modelos disponíveis em todos os provedores.</summary>
    public async Task<List<ModelInfo>> GetModelsAsync()
    {
        var result = await SendAsync<ModelListResponse>(HttpMethod.Get, "/api/models");
        return result?.Data.ToList() ?? [];
    }

    /// <summary>Lista modelos personalizados do workspace.</summary>
    public async Task<List<ModelEntryResponse>> GetCustomModelsAsync() =>
        await SendAsync<List<ModelEntryResponse>>(HttpMethod.Get, "/api/v1/models/") ?? [];

    /// <summary>Catálogo completo com a flag de visibilidade por usuário
    /// (GET /api/v1/models/all — aba Models das settings).</summary>
    public async Task<List<CatalogModel>> GetAllModelsAsync()
    {
        var result = await SendAsync<CatalogModelListResponse>(HttpMethod.Get, "/api/v1/models/all");
        return result?.Data.ToList() ?? [];
    }

    /// <summary>Papéis de modelo do usuário (SPEC-20261013-model-roles):
    /// papel → cadeia de ids em ordem de fallback.</summary>
    public async Task<Dictionary<string, List<string>>> GetModelRolesAsync()
    {
        var result = await SendAsync<ModelRolesResponse>(
            HttpMethod.Get, "/api/v1/configs/user/model-roles");
        return result?.Roles.ToDictionary(kv => kv.Key, kv => kv.Value) ?? [];
    }

    /// <summary>Grava os papéis de modelo do usuário (mapa completo;
    /// cadeia vazia remove o papel — validado no servidor).</summary>
    public async Task<bool> UpdateModelRolesAsync(Dictionary<string, List<string>> roles) =>
        await SendStatusAsync(HttpMethod.Put, "/api/v1/configs/user/model-roles",
            new UpdateModelRolesRequest(roles));

    /// <summary>Cria um modelo personalizado.</summary>
    public Task<ModelEntryResponse?> CreateCustomModelAsync(ModelEntryUpsertRequest request) =>
        SendAsync<ModelEntryResponse>(HttpMethod.Post, "/api/v1/models/create", request);

    /// <summary>Atualiza um modelo personalizado.</summary>
    public Task<ModelEntryResponse?> UpdateCustomModelAsync(string id, ModelEntryUpsertRequest request) =>
        SendAsync<ModelEntryResponse>(HttpMethod.Post, "/api/v1/models/model/update",
            new { id, request.Name, request.BaseModelId, request.SystemPrompt, request.ParamsJson, request.ProfileImageUrl });

    /// <summary>Remove um modelo personalizado.</summary>
    public async Task<bool> DeleteCustomModelAsync(string id) =>
        await SendStatusAsync(HttpMethod.Post, $"/api/v1/models/model/delete", new { id });

    /// <summary>Alterna visibilidade de um modelo personalizado.</summary>
    public Task<ModelEntryResponse?> ToggleCustomModelAsync(string id) =>
        SendAsync<ModelEntryResponse>(HttpMethod.Post, "/api/v1/models/model/toggle", new { id });

    // ---------------- Tools ----------------

    /// <summary>Lista tools do usuário.</summary>
    public async Task<List<ToolResponse>> GetToolsAsync() =>
        await SendAsync<List<ToolResponse>>(HttpMethod.Get, "/api/v1/tools/") ?? [];

    /// <summary>Cria uma tool.</summary>
    public Task<ToolResponse?> CreateToolAsync(ToolUpsertRequest request) =>
        SendAsync<ToolResponse>(HttpMethod.Post, "/api/v1/tools/", request);

    /// <summary>Atualiza uma tool.</summary>
    public Task<ToolResponse?> UpdateToolAsync(string id, ToolUpsertRequest request) =>
        SendAsync<ToolResponse>(HttpMethod.Put, $"/api/v1/tools/{id}", request);

    /// <summary>Remove uma tool.</summary>
    public async Task<bool> DeleteToolAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/tools/{id}");

    // ---------------- MCP servers (admin) ----------------

    /// <summary>Lista servidores MCP registrados (headers mascarados).</summary>
    public async Task<List<McpServerResponse>> GetMcpServersAsync() =>
        await SendAsync<List<McpServerResponse>>(HttpMethod.Get, "/api/v1/mcp/servers/") ?? [];

    /// <summary>Registra um servidor MCP.</summary>
    public Task<McpServerResponse?> CreateMcpServerAsync(McpServerUpsertRequest request) =>
        SendAsync<McpServerResponse>(HttpMethod.Post, "/api/v1/mcp/servers/", request);

    /// <summary>Atualiza um servidor MCP.</summary>
    public Task<McpServerResponse?> UpdateMcpServerAsync(string id, McpServerUpsertRequest request) =>
        SendAsync<McpServerResponse>(HttpMethod.Put, $"/api/v1/mcp/servers/{id}", request);

    /// <summary>Remove um servidor MCP (e suas tools virtuais).</summary>
    public async Task<bool> DeleteMcpServerAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/mcp/servers/{id}");

    /// <summary>Re-descobre as tools do servidor (tools/list).</summary>
    public async Task<int> RefreshMcpServerAsync(string id)
    {
        var result = await SendAsync<JsonNode>(HttpMethod.Post, $"/api/v1/mcp/servers/{id}/refresh", null);
        return result?["tools"]?.GetValue<int>() ?? -1;
    }

    /// <summary>Lista as tools descobertas de um servidor.</summary>
    public async Task<List<McpToolResponse>> GetMcpToolsAsync(string serverId) =>
        await SendAsync<List<McpToolResponse>>(HttpMethod.Get, $"/api/v1/mcp/servers/{serverId}/tools") ?? [];

    // ---------------- Camadas de config (SPEC-20261014-config-layers) ----------------

    /// <summary>Papéis do usuário + cadeias globais do admin (provenance, RF-004).</summary>
    public Task<ModelRolesResponse?> GetModelRolesFullAsync() =>
        SendAsync<ModelRolesResponse>(HttpMethod.Get, "/api/v1/configs/user/model-roles");

    /// <summary>Cadeias globais de papéis (admin, RF-004).</summary>
    public Task<ModelRolesResponse?> GetGlobalModelRolesAsync() =>
        SendAsync<ModelRolesResponse>(HttpMethod.Get, "/api/v1/configs/model-roles");

    /// <summary>Grava as cadeias globais de papéis (admin).</summary>
    public async Task<bool> UpdateGlobalModelRolesAsync(Dictionary<string, List<string>> roles) =>
        await SendStatusAsync(HttpMethod.Put, "/api/v1/configs/model-roles",
            new UpdateModelRolesRequest(roles));

    /// <summary>Status do PAT global do GitHub (admin; valor nunca retorna).</summary>
    public Task<GitHubConfigResponse?> GetGlobalGitHubAsync() =>
        SendAsync<GitHubConfigResponse>(HttpMethod.Get, "/api/v1/configs/github");

    /// <summary>Valida e grava o PAT default global (admin).</summary>
    public Task<GitHubConfigResponse?> SetGlobalGitHubTokenAsync(string token) =>
        SendAsync<GitHubConfigResponse>(HttpMethod.Put, "/api/v1/configs/github",
            new GitHubTokenRequest(token));

    /// <summary>Remove o PAT default global (admin).</summary>
    public async Task<bool> ClearGlobalGitHubTokenAsync() =>
        await SendStatusAsync(HttpMethod.Delete, "/api/v1/configs/github");

    /// <summary>Flag BYOK (admin): usuários podem guardar chaves próprias.</summary>
    public Task<FeatureToggle?> GetUserProviderKeysFlagAsync() =>
        SendAsync<FeatureToggle>(HttpMethod.Get, "/api/v1/configs/user_provider_keys");

    /// <summary>Liga/desliga BYOK (admin).</summary>
    public async Task<bool> SetUserProviderKeysFlagAsync(bool enabled) =>
        await SendStatusAsync(HttpMethod.Post, "/api/v1/configs/user_provider_keys",
            new FeatureToggle(enabled));

    /// <summary>Provedores com chave do usuário (só hasKey — valores nunca voltam).</summary>
    public Task<UserProviderKeysResponse?> GetUserProviderKeysAsync() =>
        SendAsync<UserProviderKeysResponse>(HttpMethod.Get, "/api/v1/configs/user/provider-keys");

    /// <summary>Grava a chave BYOK do usuário para um provider.</summary>
    public async Task<bool> SetUserProviderKeyAsync(string provider, string key) =>
        await SendStatusAsync(HttpMethod.Put,
            $"/api/v1/configs/user/provider-keys/{provider}", new UserProviderKeyRequest(key));

    /// <summary>Remove a chave BYOK do usuário para um provider.</summary>
    public async Task<bool> DeleteUserProviderKeyAsync(string provider) =>
        await SendStatusAsync(HttpMethod.Delete,
            $"/api/v1/configs/user/provider-keys/{provider}");

    // --------- Secrets do agente (SPEC-20261017-agent-secrets-env) ---------

    /// <summary>Secrets resolvidos do usuário (nomes + origem; valores nunca voltam).</summary>
    public Task<List<SecretEntryResponse>?> GetSecretsAsync() =>
        SendAsync<List<SecretEntryResponse>>(HttpMethod.Get, "/api/v1/secrets");

    /// <summary>Upsert de secret do usuário — devolve o detail do 400 ou null no OK.</summary>
    public async Task<string?> PutSecretAsync(string name, string value)
    {
        using var request = auth.CreateRequest(HttpMethod.Put,
            $"/api/v1/secrets/{Uri.EscapeDataString(name)}");
        request.Content = JsonContent.Create(new SecretUpsertRequest(value), options: JsonOptions);
        using var response = await http.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            return null;
        }
        var node = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        return node?["detail"]?.GetValue<string>() ?? "Erro ao salvar o secret.";
    }

    /// <summary>Remove o secret do usuário (o global de mesmo nome volta a valer).</summary>
    public Task<bool> DeleteSecretAsync(string name) =>
        SendStatusAsync(HttpMethod.Delete, $"/api/v1/secrets/{Uri.EscapeDataString(name)}");

    /// <summary>Secrets globais (admin; nomes + timestamps, sem valores).</summary>
    public Task<List<SecretEntryResponse>?> GetGlobalSecretsAsync() =>
        SendAsync<List<SecretEntryResponse>>(HttpMethod.Get, "/api/v1/configs/secrets");

    /// <summary>Upsert de secret global (admin) — devolve o detail do 400 ou null.</summary>
    public async Task<string?> PutGlobalSecretAsync(string name, string value)
    {
        using var request = auth.CreateRequest(HttpMethod.Put,
            $"/api/v1/configs/secrets/{Uri.EscapeDataString(name)}");
        request.Content = JsonContent.Create(new SecretUpsertRequest(value), options: JsonOptions);
        using var response = await http.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            return null;
        }
        var node = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        return node?["detail"]?.GetValue<string>() ?? "Erro ao salvar o secret.";
    }

    /// <summary>Remove o secret global (admin).</summary>
    public Task<bool> DeleteGlobalSecretAsync(string name) =>
        SendStatusAsync(HttpMethod.Delete,
            $"/api/v1/configs/secrets/{Uri.EscapeDataString(name)}");

    /// <summary>Auto-sync de memória: valores efetivos do usuário + camada global.</summary>
    public Task<MemorySyncResponse?> GetMemorySyncAsync() =>
        SendAsync<MemorySyncResponse>(HttpMethod.Get, "/api/v1/configs/user/memory-sync");

    /// <summary>Override do usuário do auto-sync (null num campo = herda do global).</summary>
    public Task<MemorySyncResponse?> SetMemorySyncAsync(MemorySyncPrefs prefs) =>
        SendAsync<MemorySyncResponse>(HttpMethod.Put, "/api/v1/configs/user/memory-sync", prefs);

    /// <summary>Camada global do auto-sync de memória (admin).</summary>
    public async Task<bool> SetGlobalMemorySyncAsync(MemorySyncPrefs prefs) =>
        await SendStatusAsync(HttpMethod.Put, "/api/v1/configs/memory-sync", prefs);

    /// <summary>Loop do agente: valores efetivos do usuário + camadas cruas.</summary>
    public Task<AgentLoopResponse?> GetAgentLoopAsync() =>
        SendAsync<AgentLoopResponse>(HttpMethod.Get, "/api/v1/configs/user/agent-loop");

    /// <summary>Override do usuário do loop do agente (null num campo = herda do global).</summary>
    public Task<AgentLoopResponse?> SetAgentLoopAsync(AgentLoopPrefs prefs) =>
        SendAsync<AgentLoopResponse>(HttpMethod.Put, "/api/v1/configs/user/agent-loop", prefs);

    /// <summary>Catálogo efetivo de MCP do usuário (globais + próprios, com provenance).</summary>
    public async Task<List<UserMcpServerEntry>> GetUserMcpServersAsync() =>
        await SendAsync<List<UserMcpServerEntry>>(
            HttpMethod.Get, "/api/v1/users/user/mcp-servers/") ?? [];

    /// <summary>Cria servidor MCP do usuário (transporte http remoto apenas).</summary>
    public Task<McpServerResponse?> CreateUserMcpServerAsync(McpServerUpsertRequest request) =>
        SendAsync<McpServerResponse>(HttpMethod.Post, "/api/v1/users/user/mcp-servers/", request);

    /// <summary>Remove servidor MCP do próprio usuário.</summary>
    public async Task<bool> DeleteUserMcpServerAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/users/user/mcp-servers/{id}");

    /// <summary>Redescobre as tools de um servidor MCP do usuário.</summary>
    public async Task<int> RefreshUserMcpServerAsync(string id)
    {
        var result = await SendAsync<JsonNode>(HttpMethod.Post,
            $"/api/v1/users/user/mcp-servers/{id}/refresh", null);
        return result?["tools"]?.GetValue<int>() ?? -1;
    }

    /// <summary>
    /// Toggle no escopo do usuário: servidor próprio liga/desliga;
    /// servidor global grava o tombstone de desligamento do usuário.
    /// </summary>
    public async Task<bool> SetUserMcpServerEnabledAsync(string id, bool enabled) =>
        await SendStatusAsync(HttpMethod.Put,
            $"/api/v1/users/user/mcp-servers/{id}/enabled", new McpServerEnabledRequest(enabled));

    /// <summary>Liga/desliga uma repo-skill só para o usuário (deny admin é absoluto).</summary>
    public async Task<bool> SetRepoSkillEnabledAsync(string name, bool enabled, string? chatId = null) =>
        await SendStatusAsync(HttpMethod.Put,
            $"/api/v1/workspace/repo/skills/{Uri.EscapeDataString(name)}/enabled"
                + (chatId is null ? "" : $"?chatId={chatId}"),
            new McpServerEnabledRequest(enabled));

    // ---------------- Prompts ----------------

    /// <summary>Lista prompts do usuário.</summary>
    public async Task<List<PromptResponse>> GetPromptsAsync() =>
        await SendAsync<List<PromptResponse>>(HttpMethod.Get, "/api/v1/prompts/") ?? [];

    /// <summary>Cria um prompt.</summary>
    public Task<PromptResponse?> CreatePromptAsync(PromptUpsertRequest request) =>
        SendAsync<PromptResponse>(HttpMethod.Post, "/api/v1/prompts/create", request);

    /// <summary>Atualiza um prompt.</summary>
    public Task<PromptResponse?> UpdatePromptAsync(string id, PromptUpsertRequest request) =>
        SendAsync<PromptResponse>(HttpMethod.Post, $"/api/v1/prompts/id/{id}/update", request);

    /// <summary>Remove um prompt.</summary>
    public async Task<bool> DeletePromptAsync(string id)
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, $"/api/v1/prompts/id/{id}/delete");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    // ---------------- Memórias ----------------

    /// <summary>Lista memórias do usuário.</summary>
    public async Task<List<MemoryResponse>> GetMemoriesAsync() =>
        await SendAsync<List<MemoryResponse>>(HttpMethod.Get, "/api/v1/memories/") ?? [];

    /// <summary>Adiciona uma memória.</summary>
    public Task<MemoryResponse?> AddMemoryAsync(string content) =>
        SendAsync<MemoryResponse>(HttpMethod.Post, "/api/v1/memories/add",
            new MemoryUpsertRequest(content));

    /// <summary>Atualiza uma memória.</summary>
    public Task<MemoryResponse?> UpdateMemoryAsync(string id, string content) =>
        SendAsync<MemoryResponse>(HttpMethod.Post, $"/api/v1/memories/{id}/update",
            new MemoryUpsertRequest(content));

    /// <summary>Remove uma memória.</summary>
    public async Task<bool> DeleteMemoryAsync(string id)
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, $"/api/v1/memories/{id}");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    // ---------------- Notas ----------------

    /// <summary>Lista notas do usuário.</summary>
    public async Task<List<NoteResponse>> GetNotesAsync() =>
        await SendAsync<List<NoteResponse>>(HttpMethod.Get, "/api/v1/notes/") ?? [];

    /// <summary>Cria uma nota.</summary>
    public Task<NoteResponse?> CreateNoteAsync(string title, string content) =>
        SendAsync<NoteResponse>(HttpMethod.Post, "/api/v1/notes/create",
            new NoteUpsertRequest(title, content));

    /// <summary>Obtém uma nota.</summary>
    public Task<NoteResponse?> GetNoteAsync(string id) =>
        SendAsync<NoteResponse>(HttpMethod.Get, $"/api/v1/notes/{id}");

    /// <summary>Atualiza uma nota.</summary>
    public Task<NoteResponse?> UpdateNoteAsync(string id, string title, string content) =>
        SendAsync<NoteResponse>(HttpMethod.Post, $"/api/v1/notes/{id}/update",
            new NoteUpsertRequest(title, content));

    /// <summary>Remove uma nota.</summary>
    public async Task<bool> DeleteNoteAsync(string id)
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, $"/api/v1/notes/{id}/delete");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    // ---------------- Arquivos ----------------

    /// <summary>Lista arquivos enviados.</summary>
    public async Task<List<FileResponse>> GetFilesAsync() =>
        await SendAsync<List<FileResponse>>(HttpMethod.Get, "/api/v1/files/") ?? [];

    /// <summary>Envia um arquivo via multipart/form-data.</summary>
    public async Task<FileResponse?> UploadFileAsync(Stream content, string fileName, string? contentType)
    {
        using var form = new MultipartFormDataContent();
        using var streamContent = new StreamContent(content);
        if (!string.IsNullOrEmpty(contentType))
        {
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        form.Add(streamContent, "file", fileName);

        using var request = auth.CreateRequest(HttpMethod.Post, "/api/v1/files/");
        request.Content = form;
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<FileResponse>(JsonOptions)
            : null;
    }

    /// <summary>Remove um arquivo.</summary>
    public async Task<bool> DeleteFileAsync(string id)
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, $"/api/v1/files/{id}");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    // ---------------- Tarefas de IA ----------------

    /// <summary>Gera título automático para o chat.</summary>
    public async Task<string?> GenerateTitleAsync(string model, List<ChatMessageModel> messages, string? chatId)
    {
        var result = await SendAsync<TaskTitleResponse>(HttpMethod.Post,
            "/api/v1/tasks/title/completions",
            new TaskGenerationRequest(
                model,
                messages.Select(m => new ChatCompletionMessage(m.Role, m.Content)).ToList(),
                chatId));
        return result?.Title;
    }

    /// <summary>Gera sugestões de follow-up para a conversa.</summary>
    public async Task<List<string>> GenerateFollowUpsAsync(string model, List<ChatMessageModel> messages, string? chatId)
    {
        var result = await SendAsync<TaskFollowUpsResponse>(HttpMethod.Post,
            "/api/v1/tasks/follow_up/completions",
            new TaskGenerationRequest(
                model,
                messages.Select(m => new ChatCompletionMessage(m.Role, m.Content)).ToList(),
                chatId));
        return result?.FollowUps.ToList() ?? [];
    }

    /// <summary>Gera tags automáticas para o chat.</summary>
    public async Task<List<string>> GenerateTagsAsync(string model, List<ChatMessageModel> messages, string? chatId)
    {
        var result = await SendAsync<TaskTagsResponse>(HttpMethod.Post,
            "/api/v1/tasks/tags/completions",
            new TaskGenerationRequest(
                model,
                messages.Select(m => new ChatCompletionMessage(m.Role, m.Content)).ToList(),
                chatId));
        return result?.Tags.ToList() ?? [];
    }

    // ---------------- Avaliações ----------------

    /// <summary>Registra avaliação (joinha) em uma mensagem.</summary>
    public async Task<bool> SaveFeedbackAsync(
        string chatId, string messageId, string? modelId, int rating, string? reason)
    {
        var result = await SendAsync<FeedbackResponse>(HttpMethod.Post,
            "/api/v1/evaluations/feedback",
            new FeedbackUpsertRequest(chatId, messageId, modelId, rating, reason));
        return result is not null;
    }

    /// <summary>Lista as avaliações do usuário.</summary>
    public async Task<List<FeedbackResponse>> GetMyFeedbacksAsync() =>
        await SendAsync<List<FeedbackResponse>>(HttpMethod.Get, "/api/v1/evaluations/feedbacks/user") ?? [];

    // ---------------- Usuários / Admin ----------------

    /// <summary>Lista usuários paginada com busca/filtro/ordenação (somente admin).</summary>
    public async Task<UsersPage> GetUsersAsync(
        string? query = null, string? filter = null,
        string? orderBy = null, string? direction = null,
        int page = 1, int perPage = 20)
    {
        var uri = $"/api/v1/users/?page={page}&per_page={perPage}"
            + (string.IsNullOrWhiteSpace(query) ? "" : $"&query={Uri.EscapeDataString(query)}")
            + (string.IsNullOrWhiteSpace(filter) ? "" : $"&filter={Uri.EscapeDataString(filter)}")
            + (string.IsNullOrWhiteSpace(orderBy) ? "" : $"&order_by={Uri.EscapeDataString(orderBy)}")
            + (string.IsNullOrWhiteSpace(direction) ? "" : $"&direction={Uri.EscapeDataString(direction)}");
        var result = await SendAsync<UsersListResponse>(HttpMethod.Get, uri);
        return new UsersPage(result?.Users.ToList() ?? [], result?.Total ?? 0, result?.Page ?? page);
    }

    /// <summary>Permissões granulares de um usuário (somente admin).</summary>
    public async Task<JsonObject?> GetUserPermissionsAsync(string id) =>
        await SendAsync<JsonObject>(HttpMethod.Get, $"/api/v1/users/{id}/permissions");

    /// <summary>Atualiza permissões granulares de um usuário (somente admin).</summary>
    public Task<UserResponse?> UpdateUserPermissionsAsync(string id, JsonObject permissions) =>
        SendAsync<UserResponse>(HttpMethod.Put, $"/api/v1/users/{id}/permissions", permissions);

    /// <summary>Atualiza um usuário (somente admin).</summary>
    public Task<UserResponse?> UpdateUserAsync(string id, AdminUpdateUserRequest request) =>
        SendAsync<UserResponse>(HttpMethod.Post, $"/api/v1/users/{id}/update", request);

    /// <summary>Cadastra um usuário (somente admin).</summary>
    public async Task<bool> AddUserAsync(AddUserRequest request) =>
        await SendStatusAsync(HttpMethod.Post, "/api/v1/auths/add", request);

    /// <summary>Remove um usuário (somente admin).</summary>
    public async Task<bool> DeleteUserAsync(string id)
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, $"/api/v1/users/{id}");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Obtém as configurações de UI persistidas do usuário.</summary>
    public async Task<Dictionary<string, object>?> GetUserSettingsAsync() =>
        await SendAsync<Dictionary<string, object>>(HttpMethod.Get, "/api/v1/users/user/settings");

    /// <summary>Atualiza as configurações de UI do usuário.</summary>
    public async Task<bool> UpdateUserSettingsAsync(object settings) =>
        await SendStatusAsync(HttpMethod.Post, "/api/v1/users/user/settings/update", settings);

    /// <summary>
    /// Catálogo de aprovações por tool: tier declarado + override vigente
    /// do usuário (SPEC-20261013-approval-tiers).
    /// </summary>
    public async Task<ToolApprovalsResponse?> GetToolApprovalsAsync() =>
        await SendAsync<ToolApprovalsResponse>(
            HttpMethod.Get, "/api/v1/users/user/tool-approvals");

    /// <summary>Mescla overrides de aprovação por tool (null/"" remove).</summary>
    public async Task<ToolApprovalsResponse?> UpdateToolApprovalsAsync(
        Dictionary<string, string?> overrides) =>
        await SendAsync<ToolApprovalsResponse>(
            HttpMethod.Put, "/api/v1/users/user/tool-approvals",
            new UpdateToolApprovalsRequest(overrides));

    /// <summary>Chave pública VAPID para pushManager.subscribe (null quando push desligado).</summary>
    public async Task<string?> GetVapidPublicKeyAsync()
    {
        var response = await SendAsync<VapidPublicKeyResponse>(
            HttpMethod.Get, "/api/v1/notifications/push/vapid-key");
        return response?.PublicKey;
    }

    /// <summary>Obtém a configuração pública da aplicação.</summary>
    public Task<AppConfigResponse?> GetAppConfigAsync() =>
        SendAsync<AppConfigResponse>(HttpMethod.Get, "/api/config");

    private AppConfigResponse? _appConfigCache;

    /// <summary>Obtém a configuração pública com cache em memória (uma chamada por sessão).</summary>
    public async Task<AppConfigResponse?> GetAppConfigCachedAsync() =>
        _appConfigCache ??= await GetAppConfigAsync();

    /// <summary>Obtém a configuração administrativa (somente admin).</summary>
    public Task<AdminConfig?> GetAdminConfigAsync() =>
        SendAsync<AdminConfig>(HttpMethod.Get, "/api/v1/auths/admin/config");

    /// <summary>Atualiza a configuração administrativa (somente admin).</summary>
    public Task<AdminConfig?> UpdateAdminConfigAsync(AdminConfig config) =>
        SendAsync<AdminConfig>(HttpMethod.Post, "/api/v1/auths/admin/config", config);

    /// <summary>Atualiza nome e imagem de perfil do usuário.</summary>
    public Task<UserResponse?> UpdateProfileAsync(string name, string? profileImageUrl) =>
        SendAsync<UserResponse>(HttpMethod.Post, "/api/v1/auths/update/profile",
            new { name, profileImageUrl });

    /// <summary>Atualiza a senha do usuário.</summary>
    public async Task<bool> UpdatePasswordAsync(string current, string password) =>
        await SendStatusAsync(HttpMethod.Post, "/api/v1/auths/update/password",
            new UpdatePasswordRequest(current, password));

    /// <summary>Gera uma nova chave de API do usuário.</summary>
    public Task<ApiKeyCreatedResponse?> CreateApiKeyAsync() =>
        SendAsync<ApiKeyCreatedResponse>(HttpMethod.Post, "/api/v1/auths/api_key");

    /// <summary>Obtém metadados da chave de API ativa.</summary>
    public Task<ApiKeyInfoResponse?> GetApiKeyAsync() =>
        SendAsync<ApiKeyInfoResponse>(HttpMethod.Get, "/api/v1/auths/api_key");

    /// <summary>Obtém o webhook de notificação do usuário.</summary>
    public Task<NotificationWebhookResponse?> GetWebhookAsync() =>
        SendAsync<NotificationWebhookResponse>(HttpMethod.Get, "/api/v1/notifications/webhook");

    /// <summary>Salva o webhook de notificação do usuário.</summary>
    public Task<NotificationWebhookResponse?> SaveWebhookAsync(NotificationWebhookRequest request) =>
        SendAsync<NotificationWebhookResponse>(HttpMethod.Post, "/api/v1/notifications/webhook", request);

    /// <summary>Remove o webhook de notificação do usuário.</summary>
    public async Task<bool> DeleteWebhookAsync()
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, "/api/v1/notifications/webhook");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Dispara um evento de teste para o webhook do usuário.</summary>
    public Task<WebhookTestResponse?> TestWebhookAsync() =>
        SendAsync<WebhookTestResponse>(HttpMethod.Post, "/api/v1/notifications/webhook/test");

    /// <summary>Obtém o webhook global (somente admin).</summary>
    public Task<NotificationWebhookResponse?> GetAdminWebhookAsync() =>
        SendAsync<NotificationWebhookResponse>(HttpMethod.Get, "/api/v1/notifications/admin/webhook");

    /// <summary>Salva o webhook global (somente admin).</summary>
    public Task<NotificationWebhookResponse?> SaveAdminWebhookAsync(NotificationWebhookRequest request) =>
        SendAsync<NotificationWebhookResponse>(HttpMethod.Post, "/api/v1/notifications/admin/webhook", request);

    /// <summary>Remove o webhook global (somente admin).</summary>
    public async Task<bool> DeleteAdminWebhookAsync()
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, "/api/v1/notifications/admin/webhook");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Dispara um evento de teste para o webhook global (somente admin).</summary>
    public Task<WebhookTestResponse?> TestAdminWebhookAsync() =>
        SendAsync<WebhookTestResponse>(HttpMethod.Post, "/api/v1/notifications/admin/webhook/test");

    /// <summary>Lista terminal servers configurados.</summary>
    public Task<List<TerminalServerResponse>?> GetTerminalServersAsync() =>
        SendAsync<List<TerminalServerResponse>>(HttpMethod.Get, "/api/v1/terminals/");

    /// <summary>Cria/atualiza um terminal server (admin).</summary>
    public Task<TerminalServerResponse?> SaveTerminalServerAsync(TerminalServerRequest request) =>
        SendAsync<TerminalServerResponse>(HttpMethod.Post, "/api/v1/terminals/config", request);

    /// <summary>Remove um terminal server (admin).</summary>
    public Task<bool> DeleteTerminalServerAsync(string id) =>
        SendStatusAsync(HttpMethod.Delete, $"/api/v1/terminals/config/{id}");

    // ---------------- Terminal PTY (SPEC-20261007-chat-agent-tools) ----------------

    /// <summary>Lista os jobs de background do usuário (aba Jobs do painel; <c>chatId</c> filtra).</summary>
    public async Task<List<ChatJobResponse>> GetJobsAsync(string? chatId = null, bool all = false)
    {
        var query = $"?all={(all ? "true" : "false")}"
            + (chatId is null ? "" : $"&chatId={Uri.EscapeDataString(chatId)}");
        return await SendAsync<List<ChatJobResponse>>(
            HttpMethod.Get, $"/api/v1/jobs/{query}") ?? [];
    }

    /// <summary>Mata um job de background em execução (aba Jobs do painel).</summary>
    public Task<bool> KillJobAsync(string jobId) =>
        SendStatusAsync(HttpMethod.Post, $"/api/v1/jobs/{jobId}/kill");

    /// <summary>
    /// Snapshot git do workspace do chat (git bar / aba Changes — RF-018).
    /// Null em falha de rede; <c>Git=false</c> quando o workdir não é repo.
    /// </summary>
    public Task<WorkspaceGitResponse?> GetRunDiffAsync(string chatId, string runId) =>
        SendAsync<WorkspaceGitResponse>(
            HttpMethod.Get, $"/api/v1/chats/{chatId}/runs/{runId}/diff");

    /// <summary>
    /// Lista consolidada das runs do usuário (console de runs paralelas —
    /// SPEC-20261010-parallel-runs-console): status + chat + hierarquia.
    /// </summary>
    public async Task<List<ParallelRunResponse>> GetRunsConsoleAsync() =>
        await SendAsync<List<ParallelRunResponse>>(HttpMethod.Get, "/api/v1/chats/runs") ?? [];

    /// <summary>Lê a feature flag do terminal PTY (off por default).</summary>
    public async Task<bool> GetTerminalEnabledAsync()
    {
        var node = await SendAsync<JsonObject>(HttpMethod.Get, "/api/v1/terminal/config");
        return node?["enabled"]?.GetValue<bool>() == true;
    }

    /// <summary>Lista as sessões de terminal PTY do usuário.</summary>
    public async Task<List<TerminalSessionInfoResponse>> GetTerminalSessionsAsync() =>
        await SendAsync<List<TerminalSessionInfoResponse>>(
            HttpMethod.Get, "/api/v1/terminal/sessions") ?? [];

    /// <summary>Cria uma sessão de terminal PTY; devolve o id ou null.</summary>
    public async Task<string?> CreateTerminalSessionAsync(int cols = 120, int rows = 30)
    {
        var node = await SendAsync<JsonObject>(
            HttpMethod.Post, "/api/v1/terminal/sessions", new { cols, rows });
        return node?["id"]?.GetValue<string>();
    }

    /// <summary>Encerra uma sessão de terminal PTY.</summary>
    public Task<bool> KillTerminalSessionAsync(string id) =>
        SendStatusAsync(HttpMethod.Delete, $"/api/v1/terminal/sessions/{id}");

    /// <summary>Configuração SAML (admin).</summary>
    public Task<SamlConfigResponse?> GetSamlConfigAsync() =>
        SendAsync<SamlConfigResponse>(HttpMethod.Get, "/api/v1/configs/saml");

    /// <summary>Salva a configuração SAML (admin).</summary>
    public Task<SamlConfigResponse?> SaveSamlConfigAsync(SamlConfigRequest request) =>
        SendAsync<SamlConfigResponse>(HttpMethod.Post, "/api/v1/configs/saml", request);

    /// <summary>Configuração SCIM (admin).</summary>
    public Task<ScimConfigResponse?> GetScimConfigAsync() =>
        SendAsync<ScimConfigResponse>(HttpMethod.Get, "/api/v1/configs/scim");

    /// <summary>Salva a configuração SCIM (admin).</summary>
    public Task<ScimConfigResponse?> SaveScimConfigAsync(ScimConfigRequest request) =>
        SendAsync<ScimConfigResponse>(HttpMethod.Post, "/api/v1/configs/scim", request);

    /// <summary>Lista skills do usuário (admin vê todas).</summary>
    public Task<List<SkillResponse>?> GetSkillsAsync() =>
        SendAsync<List<SkillResponse>>(HttpMethod.Get, "/api/v1/skills/");

    /// <summary>Cria uma skill.</summary>
    public Task<SkillResponse?> CreateSkillAsync(SkillRequest request) =>
        SendAsync<SkillResponse>(HttpMethod.Post, "/api/v1/skills/", request);

    /// <summary>Atualiza uma skill.</summary>
    public Task<SkillResponse?> UpdateSkillAsync(string id, SkillRequest request) =>
        SendAsync<SkillResponse>(HttpMethod.Post, $"/api/v1/skills/{id}", request);

    /// <summary>Remove uma skill.</summary>
    public Task<bool> DeleteSkillAsync(string id) =>
        SendStatusAsync(HttpMethod.Delete, $"/api/v1/skills/{id}");

    /// <summary>Lista functions registradas (admin).</summary>
    public Task<List<FunctionResponse>?> GetFunctionsAsync() =>
        SendAsync<List<FunctionResponse>>(HttpMethod.Get, "/api/v1/functions/");

    /// <summary>Cria uma function (admin).</summary>
    public Task<FunctionResponse?> CreateFunctionAsync(FunctionRequest request) =>
        SendAsync<FunctionResponse>(HttpMethod.Post, "/api/v1/functions/", request);

    /// <summary>Atualiza uma function (admin).</summary>
    public Task<FunctionResponse?> UpdateFunctionAsync(string id, FunctionRequest request) =>
        SendAsync<FunctionResponse>(HttpMethod.Post, $"/api/v1/functions/{id}", request);

    /// <summary>Alterna ativação de uma function (admin).</summary>
    public Task<FunctionResponse?> ToggleFunctionAsync(string id) =>
        SendAsync<FunctionResponse>(HttpMethod.Post, $"/api/v1/functions/{id}/toggle");

    /// <summary>Salva os valves de uma function (admin).</summary>
    public Task<FunctionResponse?> SaveFunctionValvesAsync(string id, string valvesJson) =>
        SendAsync<FunctionResponse>(HttpMethod.Post, $"/api/v1/functions/{id}/valves",
            new FunctionValvesRequest(valvesJson));

    /// <summary>Remove uma function (admin).</summary>
    public Task<bool> DeleteFunctionAsync(string id) =>
        SendStatusAsync(HttpMethod.Delete, $"/api/v1/functions/{id}");

    /// <summary>Lista servidores de pipelines (admin).</summary>
    public Task<List<PipelineServerResponse>?> GetPipelineServersAsync() =>
        SendAsync<List<PipelineServerResponse>>(HttpMethod.Get, "/api/v1/pipelines/");

    /// <summary>Registra/atualiza um servidor de pipelines (admin).</summary>
    public Task<PipelineServerResponse?> SavePipelineServerAsync(PipelineServerRequest request) =>
        SendAsync<PipelineServerResponse>(HttpMethod.Post, "/api/v1/pipelines/", request);

    /// <summary>Remove um servidor de pipelines (admin).</summary>
    public Task<bool> DeletePipelineServerAsync(string id) =>
        SendStatusAsync(HttpMethod.Delete, $"/api/v1/pipelines/{id}");

    /// <summary>Lista pipes descobertos em todos os servidores.</summary>
    public Task<List<PipelinePipeResponse>?> GetPipelinePipesAsync() =>
        SendAsync<List<PipelinePipeResponse>>(HttpMethod.Get, "/api/v1/pipelines/list");

    /// <summary>Revoga a chave de API do usuário.</summary>
    public async Task<bool> DeleteApiKeyAsync()
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, "/api/v1/auths/api_key");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    // ---------------- Conexões e versão ----------------

    /// <summary>Obtém a configuração de conexões (somente admin).</summary>
    public Task<ConnectionsConfigResponse?> GetConnectionsAsync() =>
        SendAsync<ConnectionsConfigResponse>(HttpMethod.Get, "/api/v1/configs/connections");

    /// <summary>Atualiza a configuração de conexões (somente admin).</summary>
    public Task<ConnectionsConfigResponse?> UpdateConnectionsAsync(ConnectionsConfig config) =>
        SendAsync<ConnectionsConfigResponse>(HttpMethod.Post, "/api/v1/configs/connections", config);

    /// <summary>Lista os modelos de uma conexão cadastrada (somente admin).</summary>
    public Task<ModelListResponse?> GetConnectionModelsAsync(string type, int index) =>
        SendAsync<ModelListResponse>(HttpMethod.Get,
            $"/api/v1/configs/connections/models?type={Uri.EscapeDataString(type)}&index={index}");

    /// <summary>
    /// Capacidades detectadas de UMA conexão (somente admin) — os combos de
    /// STT/TTS/imagem/vídeo exibem apenas os modelos compatíveis do provider
    /// selecionado, não o catálogo inteiro.
    /// </summary>
    public Task<DetectedCapabilities?> GetConnectionCapabilitiesAsync(string type, int index) =>
        SendAsync<DetectedCapabilities>(HttpMethod.Get,
            $"/api/v1/configs/connections/capabilities?type={Uri.EscapeDataString(type)}&index={index}");

    /// <summary>Obtém a versão do backend.</summary>
    public Task<VersionResponse?> GetVersionAsync() =>
        SendAsync<VersionResponse>(HttpMethod.Get, "/api/version");

    // ---------------- Automação (admin): n8n + webhooks ----------------

    /// <summary>Configuração da integração n8n (somente admin).</summary>
    public Task<N8nConfigResponse?> GetN8nConfigAsync() =>
        SendAsync<N8nConfigResponse>(HttpMethod.Get, "/api/v1/n8n/config");

    /// <summary>Atualiza a configuração n8n; ApiKey nula mantém a atual (somente admin).</summary>
    public Task<N8nConfigResponse?> UpdateN8nConfigAsync(N8nConfigUpdateRequest request) =>
        SendAsync<N8nConfigResponse>(HttpMethod.Put, "/api/v1/n8n/config", request);

    /// <summary>Lista workflows do n8n via proxy (somente admin).</summary>
    public async Task<List<N8nWorkflowResponse>> GetN8nWorkflowsAsync() =>
        await SendAsync<List<N8nWorkflowResponse>>(HttpMethod.Get, "/api/v1/n8n/workflows") ?? [];

    /// <summary>Lista os webhooks de automação do usuário.</summary>
    public async Task<List<AutomationHookResponse>> GetAutomationHooksAsync() =>
        await SendAsync<List<AutomationHookResponse>>(HttpMethod.Get, "/api/v1/hooks/") ?? [];

    /// <summary>Cria um webhook de automação vinculado a um chat.</summary>
    public Task<AutomationHookResponse?> CreateAutomationHookAsync(AutomationHookCreateRequest request) =>
        SendAsync<AutomationHookResponse>(HttpMethod.Post, "/api/v1/hooks/", request);

    /// <summary>Remove um webhook de automação.</summary>
    public Task<bool> DeleteAutomationHookAsync(string id) =>
        SendStatusAsync(HttpMethod.Delete, $"/api/v1/hooks/{Uri.EscapeDataString(id)}");

    // ---------------- Mídia e browser (admin) ----------------

    /// <summary>Configuração de geração de vídeo (somente admin; ApiKey mascarada).</summary>
    public Task<VideoConfig?> GetVideoConfigAsync() =>
        SendAsync<VideoConfig>(HttpMethod.Get, "/api/v1/videos/config");

    /// <summary>Atualiza a configuração de vídeo (somente admin).</summary>
    public Task<VideoConfig?> UpdateVideoConfigAsync(VideoConfig config) =>
        SendAsync<VideoConfig>(HttpMethod.Post, "/api/v1/videos/config", config);

    /// <summary>Motores de vídeo disponíveis (somente admin).</summary>
    public async Task<List<string>> GetVideoEnginesAsync() =>
        await SendAsync<List<string>>(HttpMethod.Get, "/api/v1/videos/config/engines") ?? [];

    /// <summary>Testa a configuração de vídeo com uma geração curta (somente admin).</summary>
    public Task<ImageTestResponse?> TestVideoConfigAsync() =>
        SendAsync<ImageTestResponse>(HttpMethod.Post, "/api/v1/videos/config/test");

    /// <summary>Flag da tool builtin browser_screenshot (somente admin).</summary>
    public Task<BrowserConfigResponse?> GetBrowserConfigAsync() =>
        SendAsync<BrowserConfigResponse>(HttpMethod.Get, "/api/v1/browser/config");

    /// <summary>Liga/desliga a tool builtin browser_screenshot (somente admin).</summary>
    public Task<BrowserConfigResponse?> UpdateBrowserConfigAsync(bool enabled) =>
        SendAsync<BrowserConfigResponse>(HttpMethod.Put, "/api/v1/browser/config", new { enabled });

    /// <summary>Resposta de /api/v1/browser/config (shape anônimo do endpoint).</summary>
    public sealed record BrowserConfigResponse(bool Enabled, string? BrowserPath);

    // ---------------- Gerenciamento de modelos Ollama (passthrough) ----------------

    /// <summary>Lista os modelos instalados na conexão Ollama configurada.</summary>
    public Task<OllamaTagsResponse?> GetOllamaTagsAsync() =>
        SendAsync<OllamaTagsResponse>(HttpMethod.Get, "/ollama/api/tags");

    /// <summary>Baixa um modelo no Ollama (aguarda o stream de progresso terminar).</summary>
    public async Task<bool> PullOllamaModelAsync(string name)
    {
        using var request = auth.CreateRequest(HttpMethod.Post, "/ollama/api/pull");
        request.Content = JsonContent.Create(new { name }, options: JsonOptions);
        using var response = await http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        await response.Content.CopyToAsync(Stream.Null);
        return true;
    }

    /// <summary>Remove um modelo do Ollama pelo nome.</summary>
    public async Task<bool> DeleteOllamaModelAsync(string name)
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, "/ollama/api/delete");
        request.Content = JsonContent.Create(new { name }, options: JsonOptions);
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    // ---------------- Geração de imagens ----------------

    /// <summary>Obtém a configuração de geração de imagens (somente admin, chave mascarada).</summary>
    public Task<ImagesConfig?> GetImagesConfigAsync() =>
        SendAsync<ImagesConfig>(HttpMethod.Get, "/api/v1/images/config");

    /// <summary>Atualiza a configuração de geração de imagens (somente admin).</summary>
    public Task<ImagesConfig?> UpdateImagesConfigAsync(ImagesConfig config) =>
        SendAsync<ImagesConfig>(HttpMethod.Post, "/api/v1/images/config", config);

    /// <summary>Motores de imagem disponíveis (somente admin).</summary>
    public async Task<List<string>> GetImageEnginesAsync() =>
        await SendAsync<List<string>>(HttpMethod.Get, "/api/v1/images/config/engines") ?? [];

    /// <summary>Testa a conectividade do motor de imagens configurado (admin).</summary>
    public Task<ImageTestResponse?> TestImagesConfigAsync() =>
        SendAsync<ImageTestResponse>(HttpMethod.Post, "/api/v1/images/config/test");

    /// <summary>Modelos detectados por capacidade nas conexões cadastradas (admin).</summary>
    public Task<DetectedCapabilities?> GetDetectedCapabilitiesAsync() =>
        SendAsync<DetectedCapabilities>(HttpMethod.Get, "/api/v1/configs/capabilities");

    /// <summary>Testa o TTS configurado sintetizando uma palavra (admin).</summary>
    public Task<ImageTestResponse?> TestAudioConfigAsync() =>
        SendAsync<ImageTestResponse>(HttpMethod.Post, "/api/v1/audio/config/test");

    /// <summary>Edita uma imagem existente com um prompt (engines com suporte).</summary>
    public Task<GeneratedImage?> EditImageAsync(string imageId, string prompt, string? size = null) =>
        SendAsync<GeneratedImage>(HttpMethod.Post, "/api/v1/images/edit",
            new ImageEditRequest(imageId, prompt, size));

    /// <summary>Gera imagens a partir de um prompt; nulo quando falha ou feature off.</summary>
    public Task<List<GeneratedImage>?> GenerateImagesAsync(string prompt, string? size = null) =>
        SendAsync<List<GeneratedImage>>(HttpMethod.Post, "/api/v1/images/generations",
            new ImageGenerationRequest(prompt, 1, size));

    // ---------------- Admin: avaliações e flags ----------------

    /// <summary>Lista todas as avaliações com identificação do usuário (somente admin).</summary>
    public Task<List<AdminFeedbackResponse>?> GetAllFeedbacksAsync() =>
        SendAsync<List<AdminFeedbackResponse>>(HttpMethod.Get, "/api/v1/evaluations/feedbacks/list");

    /// <summary>Persiste feature flags administráveis (somente admin).</summary>
    public Task<AdminConfig?> UpdateAppConfigAsync(AdminConfig config) =>
        SendAsync<AdminConfig>(HttpMethod.Post, "/api/config", config);

    // ---------------- GitHub / workspace repo ----------------

    /// <summary>Status da integração GitHub do usuário (token mascarado).</summary>
    public Task<GitHubConfigResponse?> GetGitHubConfigAsync() =>
        SendAsync<GitHubConfigResponse>(HttpMethod.Get, "/api/v1/github/config");

    /// <summary>Valida e salva o PAT do usuário; null em token inválido.</summary>
    public Task<GitHubConfigResponse?> SetGitHubTokenAsync(string token) =>
        SendAsync<GitHubConfigResponse>(HttpMethod.Put, "/api/v1/github/config",
            new GitHubTokenRequest(token));

    /// <summary>Remove o PAT do usuário.</summary>
    public async Task<bool> ClearGitHubTokenAsync() =>
        await SendStatusAsync(HttpMethod.Delete, "/api/v1/github/config");

    /// <summary>Repositórios do usuário (precisa de PAT configurado).</summary>
    public async Task<List<GitHubRepoResponse>> GetGitHubReposAsync() =>
        await SendAsync<List<GitHubRepoResponse>>(HttpMethod.Get, "/api/v1/github/repos") ?? [];

    /// <summary>Branches + default do repositório.</summary>
    public Task<GitHubBranchesResponse?> GetGitHubBranchesAsync(string owner, string repo) =>
        SendAsync<GitHubBranchesResponse>(HttpMethod.Get,
            $"/api/v1/github/repos/{owner}/{repo}/branches");

    /// <summary>Binding repo↔workspace atual (campos null quando não vinculado).</summary>
    public Task<WorkspaceRepoResponse?> GetWorkspaceRepoAsync() =>
        SendAsync<WorkspaceRepoResponse>(HttpMethod.Get, "/api/v1/workspace/repo/");

    /// <summary>Clona (ou troca de branch) o repo no workspace.</summary>
    public Task<WorkspaceRepoResponse?> OpenWorkspaceRepoAsync(string repo, string branch) =>
        SendAsync<WorkspaceRepoResponse>(HttpMethod.Post, "/api/v1/workspace/repo/open",
            new WorkspaceRepoOpenRequest(repo, branch));

    /// <summary>Desvincula o repo do workspace.</summary>
    public async Task<bool> UnbindWorkspaceRepoAsync() =>
        await SendStatusAsync(HttpMethod.Delete, "/api/v1/workspace/repo/");

    /// <summary>
    /// Binding repo↔workspace DO CHAT (SPEC-20261010-chat-repo-binding):
    /// devolve binding efetivo + origem (chat|user|none).
    /// </summary>
    public Task<ChatWorkspaceRepoResponse?> GetChatWorkspaceRepoAsync(string chatId) =>
        SendAsync<ChatWorkspaceRepoResponse>(
            HttpMethod.Get, $"/api/v1/chats/{chatId}/workspace-repo");

    /// <summary>Abre repo por chat; <paramref name="repo"/> nulo limpa o binding do chat.</summary>
    public Task<ChatWorkspaceRepoResponse?> PutChatWorkspaceRepoAsync(
        string chatId, string? repo, string? branch) =>
        SendAsync<ChatWorkspaceRepoResponse>(
            HttpMethod.Put, $"/api/v1/chats/{chatId}/workspace-repo",
            repo is null ? null : new WorkspaceRepoOpenRequest(repo, branch));

    /// <summary>
    /// Aplica o diff do worktree da run no workdir compartilhado (E16 S9):
    /// conflitos voltam listados no response — nunca aplicados à força.
    /// </summary>
    public Task<MergeWorktreeResponse?> MergeWorktreeAsync(string runId) =>
        SendAsync<MergeWorktreeResponse>(HttpMethod.Post,
            "/api/v1/workspace/repo/merge-worktree", new MergeWorktreeRequest(runId));

    // ---------------- Workspace file API + IDE (SPEC-20261009-web-ide-surface) ----------------

    /// <summary>Tree lazy do workdir; null em erro/404 (sem repo vinculado).</summary>
    public Task<WorkspaceFileTreeResponse?> GetWorkspaceTreeAsync(
        string? path = null, int? depth = null, string? cursor = null, string? chatId = null)
    {
        var q = "?path=" + Uri.EscapeDataString(path ?? "")
            + (depth is null ? "" : $"&depth={depth}")
            + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")
            + (chatId is null ? "" : "&chatId=" + Uri.EscapeDataString(chatId));
        return SendAsync<WorkspaceFileTreeResponse>(HttpMethod.Get,
            $"/api/v1/workspace/repo/tree{q}");
    }

    /// <summary>Catálogo de skills do repo vinculado; vazio sem repo/erro.</summary>
    public async Task<List<RepoSkillItemResponse>> GetRepoSkillsAsync(string? chatId = null) =>
        await SendAsync<List<RepoSkillItemResponse>>(HttpMethod.Get,
            "/api/v1/workspace/repo/skills"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId))) ?? [];

    /// <summary>Corpo de uma skill do repo; null quando não existe.</summary>
    public Task<RepoSkillDetailResponse?> GetRepoSkillAsync(string name, string? chatId = null) =>
        SendAsync<RepoSkillDetailResponse>(HttpMethod.Get,
            $"/api/v1/workspace/repo/skills/{Uri.EscapeDataString(name)}"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)));

    /// <summary>Catálogo de commands markdown do repo vinculado.</summary>
    public async Task<List<RepoCommandItemResponse>> GetRepoCommandsAsync(string? chatId = null) =>
        await SendAsync<List<RepoCommandItemResponse>>(HttpMethod.Get,
            "/api/v1/workspace/repo/commands"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId))) ?? [];

    /// <summary>Corpo (template) de um command do repo; null quando não existe.</summary>
    public Task<RepoCommandDetailResponse?> GetRepoCommandAsync(string name, string? chatId = null) =>
        SendAsync<RepoCommandDetailResponse>(HttpMethod.Get,
            $"/api/v1/workspace/repo/commands/{Uri.EscapeDataString(name)}"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)));

    // ---------------- Agentes / combos / manutenção (settings UX) ----------------

    /// <summary>Catálogo efetivo de agentes declarativos + flags de workspace.</summary>
    public Task<AgentCatalogResponse?> GetAgentCatalogAsync() =>
        SendAsync<AgentCatalogResponse>(HttpMethod.Get, "/api/v1/agents");

    /// <summary>Liga/desliga o opt-in do usuário a agentes do workdir.</summary>
    public async Task<bool> SetAgentWorkspaceEnabledAsync(bool enabled) =>
        await SendAsync<StatusResponse>(HttpMethod.Put,
            "/api/v1/agents/workspace-enabled",
            new McpServerEnabledRequest(enabled)) is not null;

    /// <summary>Detecção de agentes externos (CLIs no PATH) do usuário.</summary>
    public async Task<List<ExternalAgentStatusResponse>> GetExternalAgentsAsync() =>
        await SendAsync<List<ExternalAgentStatusResponse>>(HttpMethod.Get,
            "/api/v1/external-agents/detect") ?? [];

    /// <summary>Seleciona/deseleciona um agente externo como executor.</summary>
    public async Task<bool> SetExternalAgentSelectedAsync(string id, bool enabled) =>
        await SendAsync<bool>(HttpMethod.Put,
            $"/api/v1/external-agents/{Uri.EscapeDataString(id)}/selected",
            new ExternalAgentSelectRequest(enabled));

    /// <summary>Personas de agente do usuário (CRUD em /api/v1/agent-personas).</summary>
    public async Task<List<AgentPersonaResponse>> GetAgentPersonasAsync() =>
        await SendAsync<List<AgentPersonaResponse>>(HttpMethod.Get,
            "/api/v1/agent-personas/") ?? [];

    /// <summary>Cria uma persona de agente.</summary>
    public Task<AgentPersonaResponse?> CreateAgentPersonaAsync(AgentPersonaUpsertRequest request) =>
        SendAsync<AgentPersonaResponse>(HttpMethod.Post, "/api/v1/agent-personas/", request);

    /// <summary>Atualiza uma persona de agente.</summary>
    public Task<AgentPersonaResponse?> UpdateAgentPersonaAsync(string id, AgentPersonaUpsertRequest request) =>
        SendAsync<AgentPersonaResponse>(HttpMethod.Put,
            $"/api/v1/agent-personas/{Uri.EscapeDataString(id)}", request);

    /// <summary>Remove uma persona de agente.</summary>
    public async Task<bool> DeleteAgentPersonaAsync(string id) =>
        await SendAsync<StatusResponse>(HttpMethod.Delete,
            $"/api/v1/agent-personas/{Uri.EscapeDataString(id)}") is not null;

    /// <summary>Combos de modelo do usuário (+ globais do admin).</summary>
    public async Task<List<ComboResponse>> GetCombosAsync() =>
        await SendAsync<List<ComboResponse>>(HttpMethod.Get, "/api/v1/combos/") ?? [];

    /// <summary>Cria um combo de modelo.</summary>
    public Task<ComboResponse?> CreateComboAsync(ComboUpsertRequest request) =>
        SendAsync<ComboResponse>(HttpMethod.Post, "/api/v1/combos/", request);

    /// <summary>Atualiza um combo de modelo.</summary>
    public Task<ComboResponse?> UpdateComboAsync(string id, ComboUpsertRequest request) =>
        SendAsync<ComboResponse>(HttpMethod.Put,
            $"/api/v1/combos/{Uri.EscapeDataString(id)}", request);

    /// <summary>Remove um combo de modelo.</summary>
    public async Task<bool> DeleteComboAsync(string id) =>
        await SendAsync<StatusResponse>(HttpMethod.Delete,
            $"/api/v1/combos/{Uri.EscapeDataString(id)}") is not null;

    /// <summary>Status runtime de um combo (modelo efetivo + cooldowns).</summary>
    public Task<ComboStatusResponse?> GetComboStatusAsync(string id) =>
        SendAsync<ComboStatusResponse>(HttpMethod.Get,
            $"/api/v1/combos/{Uri.EscapeDataString(id)}/status");

    /// <summary>Status da manutenção de sessões (último ciclo + próxima execução).</summary>
    public Task<MaintenanceStatusResponse?> GetMaintenanceStatusAsync() =>
        SendAsync<MaintenanceStatusResponse>(HttpMethod.Get,
            "/api/v1/maintenance/status");

    /// <summary>Dispara um ciclo de manutenção sob demanda (admin).</summary>
    public Task<MaintenanceCycleResponse?> RunMaintenanceAsync(bool dryRun) =>
        SendAsync<MaintenanceCycleResponse>(HttpMethod.Post,
            "/api/v1/maintenance/run", new MaintenanceRunRequest(dryRun));

    /// <summary>Grava a janela de retenção de chats em dias (admin).</summary>
    public async Task<bool> SetMaintenanceChatDaysAsync(int chatDays) =>
        await SendAsync<bool>(HttpMethod.Put,
            "/api/v1/maintenance/config", new MaintenanceConfigRequest(chatDays));

    /// <summary>
    /// Expande um command no servidor (SPEC-20261016-commands-v2): interpola
    /// <c>$ARGUMENTS</c>/<c>$N</c>, avalia <c>!`shell`</c> (quando o servidor
    /// tem o opt-in ligado) e devolve os overrides declarados
    /// (<c>agent</c>/<c>model</c>/<c>mode</c>/<c>subagent</c>). null em
    /// 404/400 — o caller faz fallback para a interpolação local.
    /// </summary>
    public Task<RepoCommandExpandResponse?> ExpandRepoCommandAsync(
        string name, string? args, string? chatId = null) =>
        SendAsync<RepoCommandExpandResponse>(HttpMethod.Post,
            $"/api/v1/workspace/repo/commands/{Uri.EscapeDataString(name)}/expand"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)),
            new RepoCommandExpandRequest(args));

    /// <summary>Lê um arquivo do workdir (fatia de linhas); null em 404/binário/grande.</summary>
    public Task<WorkspaceFileReadResponse?> GetWorkspaceFileAsync(
        string path, int? startLine = null, int? maxLines = null, string? chatId = null)
    {
        var q = "?path=" + Uri.EscapeDataString(path)
            + (startLine is null ? "" : $"&startLine={startLine}")
            + (maxLines is null ? "" : $"&maxLines={maxLines}")
            + (chatId is null ? "" : "&chatId=" + Uri.EscapeDataString(chatId));
        return SendAsync<WorkspaceFileReadResponse>(HttpMethod.Get,
            $"/api/v1/workspace/repo/file{q}");
    }

    /// <summary>Leitura com status — a IDE distingue 404, binário(415) e grande(413).</summary>
    public async Task<IdeFileResult> GetWorkspaceFileStatusAsync(
        string path, int? startLine = null, int? maxLines = null, string? chatId = null)
    {
        var q = "?path=" + Uri.EscapeDataString(path)
            + (startLine is null ? "" : $"&startLine={startLine}")
            + (maxLines is null ? "" : $"&maxLines={maxLines}")
            + (chatId is null ? "" : "&chatId=" + Uri.EscapeDataString(chatId));
        using var request = auth.CreateRequest(HttpMethod.Get,
            $"/api/v1/workspace/repo/file{q}");
        using var response = await http.SendAsync(request);
        var code = (int)response.StatusCode;
        if (code == 200)
        {
            var body = await response.Content.ReadFromJsonAsync<WorkspaceFileReadResponse>(JsonOptions);
            return new IdeFileResult(200, body);
        }
        return new IdeFileResult(code, null);
    }

    /// <summary>Grava arquivo com If-Match opcional; 409 devolve o etag atual para o dialog.</summary>
    public async Task<IdeSaveResult> PutWorkspaceFileAsync(
        string path, string content, string? ifMatch = null, string? chatId = null)
    {
        using var request = auth.CreateRequest(HttpMethod.Put, "/api/v1/workspace/repo/file"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)));
        request.Content = JsonContent.Create(
            new WorkspaceFileWriteRequest(path, content), options: JsonOptions);
        if (!string.IsNullOrEmpty(ifMatch))
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var response = await http.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<WorkspaceFileWriteResponse>(JsonOptions);
            return new IdeSaveResult(true, body?.ETag, null);
        }
        if ((int)response.StatusCode == 409)
        {
            var node = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
            return new IdeSaveResult(false, null, node?["etag"]?.GetValue<string>());
        }
        return new IdeSaveResult(false, null, null);
    }

    /// <summary>Cria diretório (recursivo) no workdir.</summary>
    public Task<bool> WorkspaceMkdirAsync(string path, string? chatId = null) =>
        SendStatusAsync(HttpMethod.Post, "/api/v1/workspace/repo/mkdir"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)),
            new WorkspaceFileMkdirRequest(path));

    /// <summary>Renomeia/move dentro do workdir; false em 404/409.</summary>
    public Task<bool> WorkspaceRenameAsync(string from, string to, string? chatId = null) =>
        SendStatusAsync(HttpMethod.Post, "/api/v1/workspace/repo/rename"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)),
            new WorkspaceFileRenameRequest(from, to));

    /// <summary>Remove arquivo ou diretório (recursivo) do workdir.</summary>
    public Task<bool> WorkspaceDeleteAsync(string path, string? chatId = null) =>
        SendStatusAsync(HttpMethod.Post, "/api/v1/workspace/repo/delete"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)),
            new WorkspaceFileDeleteRequest(path));

    /// <summary>Snapshot git do workdir (aba Changes do /ide); null em falha/404.</summary>
    public Task<WorkspaceGitResponse?> GetWorkspaceGitAsync(string? chatId = null) =>
        SendAsync<WorkspaceGitResponse>(HttpMethod.Get, "/api/v1/workspace/repo/git"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)));

    /// <summary>Busca global no workdir (SPEC-20261011-ide-search-scm); null em erro.</summary>
    public Task<WorkspaceSearchResponse?> SearchWorkspaceAsync(
        string q, string? path = null, string? chatId = null)
    {
        var qs = "?q=" + Uri.EscapeDataString(q)
            + (path is null ? "" : "&path=" + Uri.EscapeDataString(path))
            + (chatId is null ? "" : "&chatId=" + Uri.EscapeDataString(chatId));
        return SendAsync<WorkspaceSearchResponse>(HttpMethod.Get,
            $"/api/v1/workspace/repo/search{qs}");
    }

    /// <summary>Commit SCM (flag ide.git_write); resposta com Ok/Commit/Error.</summary>
    public Task<WorkspaceCommitResponse?> CommitWorkspaceAsync(string message, string? chatId = null) =>
        SendAsync<WorkspaceCommitResponse>(HttpMethod.Post,
            "/api/v1/workspace/repo/commit"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)),
            new WorkspaceCommitRequest(message));

    /// <summary>Outline LSP do arquivo (documentSymbol); null em erro.</summary>
    public Task<LspSymbolsResponse?> GetLspSymbolsAsync(string path, string? chatId = null) =>
        SendAsync<LspSymbolsResponse>(HttpMethod.Get,
            "/api/v1/workspace/lsp/symbols?path=" + Uri.EscapeDataString(path)
            + (chatId is null ? "" : "&chatId=" + Uri.EscapeDataString(chatId)));

    /// <summary>Format Document via LSP — devolve texto novo (sem escrever disco).</summary>
    public Task<LspFormatResponse?> FormatLspDocumentAsync(string path, string? chatId = null) =>
        SendAsync<LspFormatResponse>(HttpMethod.Post,
            "/api/v1/workspace/lsp/format"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)),
            new LspFormatRequest(path));

    /// <summary>Completion LSP — resposta vai direto pro CM autocomplete via JS.</summary>
    public Task<LspCompletionResponse?> LspCompleteAsync(
        string path, int line, int col, string? chatId = null) =>
        SendAsync<LspCompletionResponse>(HttpMethod.Post,
            "/api/v1/workspace/lsp/completion"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)),
            new LspPosRequest(path, line, col));

    /// <summary>Go to Definition (F12) → locations.</summary>
    public Task<LspLocationsResponse?> GetLspDefinitionAsync(
        string path, int line, int col, string? chatId = null) =>
        SendAsync<LspLocationsResponse>(HttpMethod.Get,
            "/api/v1/workspace/lsp/definition?path=" + Uri.EscapeDataString(path)
            + "&line=" + line + "&col=" + col
            + (chatId is null ? "" : "&chatId=" + Uri.EscapeDataString(chatId)));

    /// <summary>Find References (Shift+F12) → locations (inclui declaração).</summary>
    public Task<LspLocationsResponse?> GetLspReferencesAsync(
        string path, int line, int col, string? chatId = null) =>
        SendAsync<LspLocationsResponse>(HttpMethod.Get,
            "/api/v1/workspace/lsp/references?path=" + Uri.EscapeDataString(path)
            + "&line=" + line + "&col=" + col
            + (chatId is null ? "" : "&chatId=" + Uri.EscapeDataString(chatId)));

    /// <summary>Rename Symbol (F2) — edits do arquivo ativo voltam pro
    /// editor; demais arquivos escritos no disco.</summary>
    public Task<LspRenameResponse?> LspRenameAsync(
        string path, int line, int col, string newName, string? activePath,
        string? chatId = null) =>
        SendAsync<LspRenameResponse>(HttpMethod.Post,
            "/api/v1/workspace/lsp/rename"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)),
            new LspRenameRequest(path, line, col, newName, activePath));

    /// <summary>Conteúdo do arquivo no HEAD (diff do IDE); null em 404.</summary>
    public async Task<WorkspaceFileReadResponse?> GetFileHeadAsync(
        string path, string? chatId = null)
    {
        using var request = auth.CreateRequest(HttpMethod.Get,
            "/api/v1/workspace/repo/file-head?path=" + Uri.EscapeDataString(path)
            + (chatId is null ? "" : "&chatId=" + Uri.EscapeDataString(chatId)));
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<WorkspaceFileReadResponse>(JsonOptions)
            : null;
    }

    /// <summary>PRs abertos do repo vinculado (SPEC-20261009-pr-ci-panel); null em 404/erro.</summary>
    public Task<WorkspacePullsResponse?> GetWorkspacePullsAsync(string? chatId = null) =>
        SendAsync<WorkspacePullsResponse>(HttpMethod.Get, "/api/v1/workspace/repo/pulls"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)));

    // ---------------- Checkpoints do workdir (S6) ----------------

    /// <summary>Lista os checkpoints do workdir (mais novo primeiro).</summary>
    public async Task<IReadOnlyList<WorkspaceCheckpointItem>> GetCheckpointsAsync(string? chatId = null) =>
        await SendAsync<List<WorkspaceCheckpointItem>>(
            HttpMethod.Get, "/api/v1/workspace/repo/checkpoints"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId))) ?? [];

    /// <summary>Detalhe do checkpoint: arquivos cobertos + diff de preview.</summary>
    public Task<WorkspaceCheckpointDetailResponse?> GetCheckpointAsync(string hash, string? chatId = null) =>
        SendAsync<WorkspaceCheckpointDetailResponse>(
            HttpMethod.Get,
            $"/api/v1/workspace/repo/checkpoints/{Uri.EscapeDataString(hash)}"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)));

    /// <summary>
    /// Reverte o workdir ao checkpoint. Devolve o resultado (reverted/
    /// conflicts) ou <c>(null, 409)</c> quando há run ativa no workspace.
    /// </summary>
    public async Task<(WorkspaceCheckpointRevertResponse? Result, int Status)> RevertCheckpointAsync(
        string hash, bool force = false, string? chatId = null)
    {
        using var request = auth.CreateRequest(
            HttpMethod.Post,
            $"/api/v1/workspace/repo/checkpoints/{Uri.EscapeDataString(hash)}/revert"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)));
        request.Content = JsonContent.Create(
            new WorkspaceCheckpointRevertRequest(force), options: JsonOptions);
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return (null, (int)response.StatusCode);
        }
        return (await response.Content
            .ReadFromJsonAsync<WorkspaceCheckpointRevertResponse>(JsonOptions), 200);
    }

    /// <summary>Lê a feature flag da superfície /ide (on por default).</summary>
    public async Task<bool> GetIdeEnabledAsync()
    {
        var node = await SendAsync<JsonObject>(HttpMethod.Get, "/api/v1/ide/config");
        return node?["enabled"]?.GetValue<bool>() != false;
    }

    /// <summary>Lê a feature flag do port preview (on por default — E16 D4).</summary>
    public async Task<bool> GetPreviewEnabledAsync()
    {
        var node = await SendAsync<JsonObject>(HttpMethod.Get, "/api/v1/preview/config");
        return node?["enabled"]?.GetValue<bool>() != false;
    }

    /// <summary>Probe autenticado em <c>/preview/{port}/</c>: devolve o status
    /// HTTP (qualquer resposta prova que algo escuta na porta) ou null se o
    /// próprio proxy/host falhou na conexão.</summary>
    public async Task<HttpStatusCode?> ProbePreviewAsync(int port)
    {
        try
        {
            using var request = auth.CreateRequest(HttpMethod.Get, $"/preview/{port}/");
            using var response = await http.SendAsync(request);
            return response.StatusCode;
        }
        catch (Exception ex) when (ClientErrors.IsTransient(ex))
        {
            return null;
        }
    }

    // --------- LSP do editor (SPEC-20261009-lsp-diagnostics, E16 S8) ---------

    /// <summary>Status LSP do arquivo (linguagem/estado do servidor); null em erro.</summary>
    public Task<LspStatusResponse?> GetLspStatusAsync(string? path, string? chatId = null) =>
        SendAsync<LspStatusResponse>(HttpMethod.Get,
            $"/api/v1/workspace/lsp/status?path={Uri.EscapeDataString(path ?? "")}"
            + (chatId is null ? "" : "&chatId=" + Uri.EscapeDataString(chatId)));

    /// <summary>Sync do documento: kind open|change|close (didOpen/didChange/didClose).</summary>
    public Task<bool> PostLspDocAsync(string path, string kind, string? text, string? chatId = null) =>
        SendStatusAsync(HttpMethod.Post, "/api/v1/workspace/lsp/doc"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)),
            new LspDocSyncRequest(path, kind, text));

    /// <summary>Diagnostics do arquivo (ou workdir sem path); null em erro.</summary>
    public Task<LspDiagnosticsResponse?> GetLspDiagnosticsAsync(string? path, string? chatId = null) =>
        SendAsync<LspDiagnosticsResponse>(HttpMethod.Get,
            $"/api/v1/workspace/lsp/diagnostics?path={Uri.EscapeDataString(path ?? "")}"
            + (chatId is null ? "" : "&chatId=" + Uri.EscapeDataString(chatId)));

    /// <summary>Hover na posição (1-based); null quando servidor não responde.</summary>
    public async Task<string?> GetLspHoverAsync(string path, int line, int col, string? chatId = null)
    {
        var node = await SendAsync<JsonObject>(HttpMethod.Get,
            $"/api/v1/workspace/lsp/hover?path={Uri.EscapeDataString(path)}&line={line}&col={col}"
            + (chatId is null ? "" : "&chatId=" + Uri.EscapeDataString(chatId)));
        return node?["hover"]?.GetValue<string>();
    }

    /// <summary>Resultado do PUT de arquivo da IDE (etag novo ou o atual em conflito).</summary>
    public sealed record IdeSaveResult(bool Ok, string? ETag, string? ConflictETag);

    /// <summary>Resultado da leitura de arquivo da IDE: status http + body quando 200.</summary>
    public sealed record IdeFileResult(int Status, WorkspaceFileReadResponse? Body);

    // --------- Test runs do workspace (SPEC-20261009-ide-mentions-tests) ---------

    /// <summary>Resultado cru do POST de test-run (422 carrega detail/suggested).</summary>
    public sealed record IdeTestRunStartResult(
        int Status, TestRunStartResponse? Body, string? Detail, string? Suggested);

    /// <summary>
    /// Inicia um test run do repo vinculado (RF-003). <paramref name="confirmed"/>
    /// confirma comandos WorkspaceWrite depois do cartão de aprovação.
    /// </summary>
    public async Task<IdeTestRunStartResult> StartTestRunAsync(bool confirmed = false, string? chatId = null)
    {
        using var request = auth.CreateRequest(HttpMethod.Post, "/api/v1/workspace/repo/test-run"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)));
        request.Content = JsonContent.Create(new TestRunStartRequest(confirmed), options: JsonOptions);
        using var response = await http.SendAsync(request);
        var node = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        if (!response.IsSuccessStatusCode || node is null)
        {
            return new IdeTestRunStartResult(
                (int)response.StatusCode, null,
                node?["detail"]?.GetValue<string>(),
                node?["suggested"]?.GetValue<string>());
        }
        var body = System.Text.Json.JsonSerializer.Deserialize<TestRunStartResponse>(node.ToJsonString(), JsonOptions);
        return new IdeTestRunStartResult((int)response.StatusCode, body, null, null);
    }

    /// <summary>Estado + resumo + cauda do log de um test run; null em falha/404.</summary>
    public Task<TestRunStatusResponse?> GetTestRunAsync(string jobId) =>
        SendAsync<TestRunStatusResponse>(HttpMethod.Get,
            $"/api/v1/workspace/repo/test-run/{jobId}");

    /// <summary>Define/limpa o TestCommand customizado do binding (override do manifesto).</summary>
    public Task<bool> SetTestCommandAsync(string? command, string? chatId = null) =>
        SendStatusAsync(HttpMethod.Put, "/api/v1/workspace/repo/test-command"
            + (chatId is null ? "" : "?chatId=" + Uri.EscapeDataString(chatId)),
            new TestCommandRequest(command));

    // ---------------- Internos ----------------

    private async Task<bool> SendStatusAsync(HttpMethod method, string uri, object? body = null)
    {
        using var request = auth.CreateRequest(method, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string uri, object? body = null)
    {
        using var request = auth.CreateRequest(method, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
    }

    // ---------------- Feed de notificações (D3) ----------------

    /// <summary>Página do feed in-app; <paramref name="unreadOnly"/> filtra só não-lidas.</summary>
    public async Task<NotificationPageResponse?> GetNotificationsAsync(
        int page = 1, bool unreadOnly = false, int pageSize = 20) =>
        await SendAsync<NotificationPageResponse>(HttpMethod.Get,
            $"/api/v1/notifications?page={page}&pageSize={pageSize}&unread={(unreadOnly ? "true" : "false")}");

    /// <summary>Marca uma notificação como lida.</summary>
    public async Task<bool> MarkNotificationReadAsync(string id) =>
        await SendStatusAsync(HttpMethod.Post, $"/api/v1/notifications/{id}/read");

    /// <summary>Marca todas as notificações do usuário como lidas.</summary>
    public async Task<bool> MarkAllNotificationsReadAsync() =>
        await SendStatusAsync(HttpMethod.Post, "/api/v1/notifications/read-all");

    // ---------------- Analytics (admin) ----------------

    /// <summary>Dashboard de analytics agregado (admin, apenas contagens).</summary>
    public Task<AnalyticsResponse?> GetAnalyticsAsync(int days = 30) =>
        SendAsync<AnalyticsResponse>(HttpMethod.Get, $"/api/v1/analytics?days={days}");

    // ---------------- Policies admin (SPEC-20261018-policies-admin) ----------------

    /// <summary>Lista statements de policy administrativa.</summary>
    public Task<List<AdminPolicyStatementResponse>?> GetAdminPoliciesAsync() =>
        SendAsync<List<AdminPolicyStatementResponse>>(HttpMethod.Get, "/api/v1/admin/policies");

    /// <summary>Cria statement de policy.</summary>
    public Task<AdminPolicyStatementResponse?> CreateAdminPolicyAsync(
        string action, string resource, string? note) =>
        SendAsync<AdminPolicyStatementResponse>(HttpMethod.Post,
            "/api/v1/admin/policies", new { action, resource, note });

    /// <summary>Remove statement de policy.</summary>
    public Task<bool> DeleteAdminPolicyAsync(string id) =>
        SendStatusAsync(HttpMethod.Delete, $"/api/v1/admin/policies/{id}");

    /// <summary>Dry-run: qual statement casaria para (action, resource).</summary>
    public Task<AdminPolicySimulateResponse?> SimulateAdminPolicyAsync(
        string action, string resource) =>
        SendAsync<AdminPolicySimulateResponse>(HttpMethod.Post,
            "/api/v1/admin/policies/simulate", new { action, resource });

    /// <summary>Últimos bloqueios por policy (auditoria).</summary>
    public Task<List<AdminPolicyDenialResponse>?> GetAdminPolicyDenialsAsync() =>
        SendAsync<List<AdminPolicyDenialResponse>>(HttpMethod.Get, "/api/v1/admin/policies/denials");

    // ---------------- Grupos (RBAC) ----------------

    /// <summary>Lista os grupos visíveis ao usuário (admin vê todos).</summary>
    public async Task<List<GroupResponse>> GetGroupsAsync() =>
        await SendAsync<List<GroupResponse>>(HttpMethod.Get, "/api/v1/groups") ?? [];

    /// <summary>Cria um grupo (admin).</summary>
    public Task<GroupResponse?> CreateGroupAsync(CreateGroupRequest request) =>
        SendAsync<GroupResponse>(HttpMethod.Post, "/api/v1/groups", request);

    /// <summary>Atualiza nome/descrição/permissões de um grupo.</summary>
    public Task<GroupResponse?> UpdateGroupAsync(string id, UpdateGroupRequest request) =>
        SendAsync<GroupResponse>(HttpMethod.Put, $"/api/v1/groups/{id}", request);

    /// <summary>Exclui um grupo (admin).</summary>
    public async Task<bool> DeleteGroupAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/groups/{id}");

    /// <summary>Adiciona membros a um grupo.</summary>
    public Task<GroupResponse?> AddGroupMembersAsync(string id, UpdateGroupMembersRequest request) =>
        SendAsync<GroupResponse>(HttpMethod.Post, $"/api/v1/groups/{id}/members", request);

    /// <summary>Remove um membro do grupo.</summary>
    public async Task<bool> RemoveGroupMemberAsync(string id, string userId) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/groups/{id}/members/{userId}");

    // ---------------- Knowledge (RAG) ----------------

    /// <summary>Reindexa os arquivos de uma coleção com o provider atual.</summary>
    public Task<ReindexKnowledgeResponse?> ReindexKnowledgeAsync(string id) =>
        SendAsync<ReindexKnowledgeResponse>(HttpMethod.Post, $"/api/v1/knowledge/{id}/reindex");

    /// <summary>Remove coleções em lote (somente as do usuário/admin).</summary>
    public async Task<int> BatchDeleteKnowledgeAsync(IReadOnlyList<string> ids) =>
        (await SendAsync<DeletedCountResponse>(HttpMethod.Post,
            "/api/v1/knowledge/batch/delete", new BatchKnowledgeRequest(ids)))?.Deleted ?? 0;

    /// <summary>Lista as coleções de knowledge do usuário.</summary>
    public async Task<List<KnowledgeResponse>> GetKnowledgeAsync() =>
        await SendAsync<List<KnowledgeResponse>>(HttpMethod.Get, "/api/v1/knowledge") ?? [];

    /// <summary>Cria uma coleção de knowledge.</summary>
    public Task<KnowledgeResponse?> CreateKnowledgeAsync(string name, string? description = null) =>
        SendAsync<KnowledgeResponse>(HttpMethod.Post, "/api/v1/knowledge",
            new CreateKnowledgeRequest(name, description));

    /// <summary>Obtém uma coleção com seus arquivos.</summary>
    public Task<KnowledgeDetailResponse?> GetKnowledgeDetailAsync(string id) =>
        SendAsync<KnowledgeDetailResponse>(HttpMethod.Get, $"/api/v1/knowledge/{id}");

    /// <summary>Exclui uma coleção de knowledge.</summary>
    public async Task<bool> DeleteKnowledgeAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/knowledge/{id}");

    /// <summary>Vincula um arquivo já enviado a uma coleção.</summary>
    public Task<bool> AddKnowledgeFileAsync(string id, string fileId) =>
        SendStatusAsync(HttpMethod.Post, $"/api/v1/knowledge/{id}/files",
            new AddKnowledgeFileRequest(fileId));

    /// <summary>Remove um arquivo de uma coleção.</summary>
    public async Task<bool> RemoveKnowledgeFileAsync(string id, string fileId) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/knowledge/{id}/files/{fileId}");

    // ---------------- Canais ----------------

    /// <summary>Lista os canais onde o usuário é membro.</summary>
    public async Task<List<ChannelResponse>> GetChannelsAsync() =>
        await SendAsync<List<ChannelResponse>>(HttpMethod.Get, "/api/v1/channels") ?? [];

    /// <summary>Cria um canal e retorna o resumo.</summary>
    public Task<ChannelResponse?> CreateChannelAsync(string name, string? description = null) =>
        SendAsync<ChannelResponse>(HttpMethod.Post, "/api/v1/channels",
            new CreateChannelRequest(name, description, null));

    /// <summary>Obtém um canal com seus membros.</summary>
    public Task<ChannelDetailResponse?> GetChannelAsync(string id) =>
        SendAsync<ChannelDetailResponse>(HttpMethod.Get, $"/api/v1/channels/{id}");

    /// <summary>Lista o histórico de mensagens do canal.</summary>
    public async Task<List<ChannelMessageResponse>> GetChannelMessagesAsync(string id) =>
        await SendAsync<List<ChannelMessageResponse>>(
            HttpMethod.Get, $"/api/v1/channels/{id}/messages?take=200") ?? [];

    /// <summary>Envia mensagem ao canal (retorna a mensagem persistida).</summary>
    public Task<ChannelMessageResponse?> PostChannelMessageAsync(
        string id, string content, string? parentId = null) =>
        SendAsync<ChannelMessageResponse>(HttpMethod.Post,
            $"/api/v1/channels/{id}/messages",
            new CreateChannelMessageRequest(content, parentId));

    /// <summary>Cria ou obtém o DM com outro usuário.</summary>
    public Task<ChannelResponse?> CreateDmAsync(string userId) =>
        SendAsync<ChannelResponse>(HttpMethod.Post,
            "/api/v1/channels/dm", new CreateDmRequest(userId));

    /// <summary>Marca o canal como lido.</summary>
    public Task<bool> MarkChannelReadAsync(string id) =>
        SendStatusAsync(HttpMethod.Post, $"/api/v1/channels/{id}/read");

    /// <summary>Lista respostas (thread) de uma mensagem.</summary>
    public async Task<List<ChannelMessageResponse>> GetChannelRepliesAsync(
        string id, string messageId) =>
        await SendAsync<List<ChannelMessageResponse>>(
            HttpMethod.Get, $"/api/v1/channels/{id}/messages/{messageId}/replies") ?? [];

    /// <summary>Adiciona reação; retorna o agregado atualizado.</summary>
    public async Task<List<ChannelReactionResponse>> AddChannelReactionAsync(
        string id, string messageId, string emoji) =>
        await SendAsync<List<ChannelReactionResponse>>(HttpMethod.Post,
            $"/api/v1/channels/{id}/messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}") ?? [];

    /// <summary>Remove reação; retorna o agregado atualizado.</summary>
    public async Task<List<ChannelReactionResponse>> RemoveChannelReactionAsync(
        string id, string messageId, string emoji) =>
        await SendAsync<List<ChannelReactionResponse>>(HttpMethod.Delete,
            $"/api/v1/channels/{id}/messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}") ?? [];

    /// <summary>Fixa/desfixa mensagem.</summary>
    public Task<bool> SetChannelMessagePinnedAsync(string id, string messageId, bool pinned) =>
        SendStatusAsync(pinned ? HttpMethod.Post : HttpMethod.Delete,
            $"/api/v1/channels/{id}/messages/{messageId}/pin");

    /// <summary>Lista mensagens fixadas.</summary>
    public async Task<List<ChannelMessageResponse>> GetChannelPinnedAsync(string id) =>
        await SendAsync<List<ChannelMessageResponse>>(
            HttpMethod.Get, $"/api/v1/channels/{id}/pinned") ?? [];

    /// <summary>Adiciona um usuário ao canal.</summary>
    public Task<bool> AddChannelMemberAsync(string id, string userId) =>
        SendStatusAsync(HttpMethod.Post, $"/api/v1/channels/{id}/members",
            new AddChannelMembersRequest([userId], null));

    /// <summary>Lista automações do usuário.</summary>
    public async Task<List<AutomationResponse>> GetAutomationsAsync() =>
        await SendAsync<List<AutomationResponse>>(HttpMethod.Get, "/api/v1/automations") ?? [];

    /// <summary>Cria automação.</summary>
    public Task<AutomationResponse?> CreateAutomationAsync(AutomationUpsertRequest request) =>
        SendAsync<AutomationResponse>(HttpMethod.Post, "/api/v1/automations", request);

    /// <summary>Atualiza automação.</summary>
    public Task<AutomationResponse?> UpdateAutomationAsync(string id, AutomationUpsertRequest request) =>
        SendAsync<AutomationResponse>(HttpMethod.Put, $"/api/v1/automations/{id}", request);

    /// <summary>Remove automação.</summary>
    public Task<bool> DeleteAutomationAsync(string id) =>
        SendStatusAsync(HttpMethod.Delete, $"/api/v1/automations/{id}");

    /// <summary>Lista runs de uma automação.</summary>
    public async Task<List<AutomationRunResponse>> GetAutomationRunsAsync(string id) =>
        await SendAsync<List<AutomationRunResponse>>(
            HttpMethod.Get, $"/api/v1/automations/{id}/runs") ?? [];

    /// <summary>Lista runs do usuário num intervalo epoch (calendário).</summary>
    public async Task<List<AutomationRunResponse>> GetAutomationRunsInRangeAsync(long from, long to) =>
        await SendAsync<List<AutomationRunResponse>>(
            HttpMethod.Get, $"/api/v1/automations/runs?from={from}&to={to}") ?? [];

    /// <summary>Dispara uma execução imediata.</summary>
    public Task<AutomationRunResponse?> RunAutomationNowAsync(string id) =>
        SendAsync<AutomationRunResponse>(HttpMethod.Post, $"/api/v1/automations/{id}/run-now");

    // ---------------- Rotas dedicadas (detalhe por id) ----------------

    /// <summary>Obtém um prompt pelo id.</summary>
    public Task<PromptResponse?> GetPromptAsync(string id) =>
        SendAsync<PromptResponse>(HttpMethod.Get, $"/api/v1/prompts/id/{Uri.EscapeDataString(id)}");

    /// <summary>Obtém uma automação pelo id.</summary>
    public Task<AutomationResponse?> GetAutomationAsync(string id) =>
        SendAsync<AutomationResponse>(HttpMethod.Get, $"/api/v1/automations/{Uri.EscapeDataString(id)}");

    /// <summary>Obtém um modelo customizado pelo id.</summary>
    public Task<ModelEntryResponse?> GetCustomModelAsync(string id) =>
        SendAsync<ModelEntryResponse>(HttpMethod.Get,
            $"/api/v1/models/model?id={Uri.EscapeDataString(id)}");

    /// <summary>Obtém uma pasta pelo id.</summary>
    public Task<FolderResponse?> GetFolderAsync(string id) =>
        SendAsync<FolderResponse>(HttpMethod.Get, $"/api/v1/folders/{Uri.EscapeDataString(id)}");

    /// <summary>Lista os chats de uma pasta.</summary>
    public async Task<List<ChatSummaryResponse>> GetFolderChatsAsync(string id) =>
        await SendAsync<List<ChatSummaryResponse>>(
            HttpMethod.Get, $"/api/v1/chats/folder/{Uri.EscapeDataString(id)}") ?? [];

    /// <summary>Atualiza nome/descrição de uma coleção de knowledge.</summary>
    public Task<KnowledgeResponse?> UpdateKnowledgeAsync(string id, string name, string? description) =>
        SendAsync<KnowledgeResponse>(HttpMethod.Put, $"/api/v1/knowledge/{Uri.EscapeDataString(id)}",
            new UpdateKnowledgeRequest(name, description));

    // ---------------- Configurações admin (/api/v1/configs) ----------------

    /// <summary>Lista banners ativos (qualquer usuário autenticado).</summary>
    public async Task<List<BannerResponse>> GetBannersAsync() =>
        await SendAsync<List<BannerResponse>>(HttpMethod.Get, "/api/v1/configs/banners") ?? [];

    /// <summary>Cria um banner (somente admin).</summary>
    public Task<BannerResponse?> CreateBannerAsync(BannerRequest request) =>
        SendAsync<BannerResponse>(HttpMethod.Post, "/api/v1/configs/banners", request);

    /// <summary>Atualiza um banner (somente admin).</summary>
    public Task<BannerResponse?> UpdateBannerAsync(string id, BannerRequest request) =>
        SendAsync<BannerResponse>(HttpMethod.Put, $"/api/v1/configs/banners/{id}", request);

    /// <summary>Remove um banner (somente admin).</summary>
    public async Task<bool> DeleteBannerAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/configs/banners/{id}");

    /// <summary>Obtém default models + sugestões de prompt.</summary>
    public Task<ModelsConfig?> GetModelsConfigAsync() =>
        SendAsync<ModelsConfig>(HttpMethod.Get, "/api/v1/configs/models");

    /// <summary>Atualiza default models + sugestões (somente admin).</summary>
    public Task<ModelsConfig?> UpdateModelsConfigAsync(ModelsConfig config) =>
        SendAsync<ModelsConfig>(HttpMethod.Post, "/api/v1/configs/models", config);

    /// <summary>Lê um toggle/config de feature ("channels", "direct_connections").</summary>
    public async Task<bool> GetFeatureToggleAsync(string name, bool fallback = false) =>
        (await SendAsync<FeatureToggle>(HttpMethod.Get, $"/api/v1/configs/{name}"))?.Enabled ?? fallback;

    /// <summary>Grava um toggle de feature (somente admin).</summary>
    public async Task<bool> SetFeatureToggleAsync(string name, bool enabled) =>
        await SendAsync<FeatureToggle>(HttpMethod.Post, $"/api/v1/configs/{name}",
            new FeatureToggle(enabled)) is not null;

    /// <summary>Obtém a configuração de cadastro (somente admin para escrita).</summary>
    public Task<SignupConfig?> GetSignupConfigAsync() =>
        SendAsync<SignupConfig>(HttpMethod.Get, "/api/v1/configs/signup");

    /// <summary>Atualiza a configuração de cadastro (somente admin).</summary>
    public Task<SignupConfig?> UpdateSignupConfigAsync(SignupConfig config) =>
        SendAsync<SignupConfig>(HttpMethod.Post, "/api/v1/configs/signup", config);

    /// <summary>Obtém a configuração de execução de código.</summary>
    public Task<CodeExecutionConfig?> GetCodeExecutionConfigAsync() =>
        SendAsync<CodeExecutionConfig>(HttpMethod.Get, "/api/v1/configs/code_execution");

    /// <summary>Atualiza a configuração de execução de código (somente admin).</summary>
    public Task<CodeExecutionConfig?> UpdateCodeExecutionConfigAsync(CodeExecutionConfig config) =>
        SendAsync<CodeExecutionConfig>(HttpMethod.Post, "/api/v1/configs/code_execution", config);

    /// <summary>Obtém a duração configurada do JWT.</summary>
    public Task<JwtExpiryConfig?> GetJwtExpiryAsync() =>
        SendAsync<JwtExpiryConfig>(HttpMethod.Get, "/api/v1/configs/jwt");

    /// <summary>Atualiza a duração do JWT (somente admin).</summary>
    public Task<JwtExpiryConfig?> UpdateJwtExpiryAsync(JwtExpiryConfig config) =>
        SendAsync<JwtExpiryConfig>(HttpMethod.Post, "/api/v1/configs/jwt", config);

    /// <summary>Obtém a config de áudio (admin).</summary>
    public Task<AudioConfig?> GetAudioConfigAsync() =>
        SendAsync<AudioConfig>(HttpMethod.Get, "/api/v1/audio/config");

    /// <summary>Atualiza a config de áudio (admin).</summary>
    public Task<AudioConfig?> UpdateAudioConfigAsync(AudioConfig config) =>
        SendAsync<AudioConfig>(HttpMethod.Post, "/api/v1/audio/config", config);
    /// <summary>Obtém a config de retrieval (admin).</summary>
    public Task<RetrievalConfig?> GetRetrievalConfigAsync() =>
        SendAsync<RetrievalConfig>(HttpMethod.Get, "/api/v1/retrieval/config");

    /// <summary>Atualiza a config de retrieval (admin).</summary>
    public Task<RetrievalConfig?> UpdateRetrievalConfigAsync(RetrievalConfig config) =>
        SendAsync<RetrievalConfig>(HttpMethod.Post, "/api/v1/retrieval/config/update", config);

    // ---------- Access grants + calendários ----------

    private sealed record AccessGrantsResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_grants")]
        List<AccessGrant> AccessGrants);

    /// <summary>Obtém os grants de acesso de um recurso (somente dono/admin).</summary>
    public async Task<List<AccessGrant>?> GetAccessGrantsAsync(string entity, string id)
    {
        var response = await SendAsync<AccessGrantsResponse>(HttpMethod.Get,
            $"/api/v1/{entity}/{id}/access");
        return response?.AccessGrants;
    }

    /// <summary>Atualiza os grants de acesso de um recurso (knowledge|notes|models|calendars).</summary>
    public async Task<bool> UpdateAccessGrantsAsync(string entity, string id, List<AccessGrant> grants) =>
        await SendStatusAsync(HttpMethod.Post, $"/api/v1/{entity}/{id}/access/update",
            new AccessUpdateRequest(grants));

    /// <summary>Lista calendários visíveis (próprios + compartilhados).</summary>
    public async Task<List<CalendarResponse>> GetCalendarsAsync() =>
        await SendAsync<List<CalendarResponse>>(HttpMethod.Get, "/api/v1/calendars/") ?? [];

    /// <summary>Cria um calendário.</summary>
    public Task<CalendarResponse?> CreateCalendarAsync(string name, string? color) =>
        SendAsync<CalendarResponse>(HttpMethod.Post, "/api/v1/calendars/",
            new CreateCalendarRequest(name, color));

    /// <summary>Exclui um calendário (somente dono/admin).</summary>
    public async Task<bool> DeleteCalendarAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/calendars/{id}");

    /// <summary>Lista eventos de todos os calendários visíveis num intervalo.</summary>
    public async Task<List<CalendarEventResponse>> GetCalendarEventsAsync(long? from = null, long? to = null)
    {
        var q = from is not null || to is not null ? $"?from={from}&to={to}" : string.Empty;
        return await SendAsync<List<CalendarEventResponse>>(HttpMethod.Get,
            $"/api/v1/calendars/events{q}") ?? [];
    }

    /// <summary>Cria um evento num calendário.</summary>
    public Task<CalendarEventResponse?> CreateCalendarEventAsync(
        string calendarId, string title, long startTs, long endTs, string? color = null, string? notes = null) =>
        SendAsync<CalendarEventResponse>(HttpMethod.Post, "/api/v1/calendars/events",
            new CreateEventRequest(calendarId, title, startTs, endTs, color, notes));

    /// <summary>Exclui um evento.</summary>
    public async Task<bool> DeleteCalendarEventAsync(string eventId) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/calendars/events/{eventId}");

    // ---------- Arena ----------

    /// <summary>Registra o voto de uma batalha de arena e revela os modelos.</summary>
    public Task<ArenaFeedbackResponse?> VoteArenaAsync(string battleId, string winner) =>
        SendAsync<ArenaFeedbackResponse>(HttpMethod.Post,
            "/api/v1/evaluations/arena/feedback",
            new ArenaFeedbackRequest(battleId, winner));

    /// <summary>Leaderboard de arena (ELO) — admin.</summary>
    public async Task<List<LeaderboardEntryResponse>> GetLeaderboardAsync() =>
        await SendAsync<List<LeaderboardEntryResponse>>(
            HttpMethod.Get, "/api/v1/evaluations/leaderboard") ?? [];

    private sealed record UsersListResponse(List<AdminUserResponse> Users, int Total, int Page = 1);
}

/// <summary>Página de usuários administráveis.</summary>
/// <param name="Users">Usuários da página.</param>
/// <param name="Total">Total de usuários que casam com o filtro.</param>
/// <param name="Page">Página retornada.</param>
public sealed record UsersPage(List<AdminUserResponse> Users, int Total, int Page);

/// <summary>Pasta com os chats contidos (forma da resposta de /api/v1/folders).</summary>
public class FolderWithChats
{
    /// <summary>Identificador da pasta.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Nome da pasta.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Chats dentro da pasta.</summary>
    public List<ChatSummaryResponse> Chats { get; } = [];

    /// <summary>Se a pasta está expandida na UI.</summary>
    public bool Expanded { get; set; } = true;

}

/// <summary>Chat compartilhado publicamente.</summary>
/// <param name="Id">Identificador do chat.</param>
/// <param name="Title">Título.</param>
/// <param name="User">Autor do chat.</param>
/// <param name="Models">Modelos usados.</param>
/// <param name="Messages">Mensagens.</param>
/// <param name="CreatedAt">Criação.</param>
/// <param name="UpdatedAt">Última atualização.</param>
public sealed record SharedChatResponse(
    string Id,
    string Title,
    SharedChatUser? User,
    IReadOnlyList<string> Models,
    IReadOnlyList<ChatMessageModel> Messages,
    long CreatedAt,
    long UpdatedAt);

/// <summary>Autor de um chat compartilhado.</summary>
/// <param name="Name">Nome de exibição.</param>
public sealed record SharedChatUser(string? Name);


/// <summary>Resposta {deleted: n} de operações em lote.</summary>
public sealed record DeletedCountResponse(int Deleted);

/// <summary>Resposta do /ollama/api/tags (modelos instalados).</summary>
/// <param name="Models">Modelos presentes na conexão.</param>
public sealed record OllamaTagsResponse(IReadOnlyList<OllamaModelInfo> Models);

/// <summary>Item do /ollama/api/tags.</summary>
/// <param name="Name">Nome do modelo (ex.: llama3.2:latest).</param>
/// <param name="Size">Tamanho em bytes.</param>
/// <param name="ModifiedAt">Última modificação (ISO-8601 do Ollama).</param>
public sealed record OllamaModelInfo(string? Name, long? Size, string? ModifiedAt);


/// <summary>Statement de policy administrativa.</summary>
public sealed record AdminPolicyStatementResponse(
    string Id, int Order, string Action, string Resource, string Effect,
    string? Note, long CreatedAt);

/// <summary>Bloqueio auditado por policy.</summary>
public sealed record AdminPolicyDenialResponse(
    string Id, string? UserId, string Action, string Resource,
    string StatementId, long CreatedAt);

/// <summary>Resultado do dry-run de policy.</summary>
public sealed record AdminPolicySimulateResponse(
    bool Denied, AdminPolicyStatementResponse? Matched);
