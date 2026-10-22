using OpenWebUI.Mcp;

// OpenWebUI.Mcp — servidor MCP stdio (SPEC-20261022-ade-surfaces cap. B).
// O cliente MCP (Claude Code, OpenCode, etc.) spawna este processo e fala
// JSON-RPC 2.0 newline-delimited no stdin/stdout. Config por ambiente:
//   OPENWEBUI_URL      — base do app (default http://localhost:8080)
//   OPENWEBUI_API_KEY  — chave sk-* do usuário (tools falham fechado sem ela)
// Logs nunca vão pro stdout (é protocolo) — erros de boot vão pro stderr.

var baseUrl = Environment.GetEnvironmentVariable("OPENWEBUI_URL")
    ?? "http://localhost:8080";
var apiKey = Environment.GetEnvironmentVariable("OPENWEBUI_API_KEY");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var api = new OwuiApiClient(http, baseUrl, apiKey);
var server = new McpServer();

using var reader = Console.In;
await using var writer = Console.Out;

while (await reader.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    var response = await server.HandleLineAsync(line, api, CancellationToken.None);
    if (response is not null)
    {
        await writer.WriteLineAsync(response);
        await writer.FlushAsync();
    }
}

return 0;
