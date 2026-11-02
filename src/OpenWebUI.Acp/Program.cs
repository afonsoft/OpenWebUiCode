// OpenWebUI.Acp — host ACP (Agent Client Protocol) sobre stdio
// (SPEC-20261021-harness-seams RF-C3). JSON-RPC 2.0 newline-delimited:
//   initialize      → exige OPENWEBUI_ACP_TOKEN; devolve capabilities
//   session/new     → cria chat via POST /api/v1/rpc (chats.create)
//   session/prompt  → dispara run (runs.start) e faz relay do stream SSE
//                     da run como notificações session/update; responde
//                     com stopReason=end_turn ao terminal.
// Env: OPENWEBUI_ACP_TOKEN (obrigatório) · OPENWEBUI_URL (default
// http://localhost:8080). La lógica vive en <see cref="OpenWebUI.Acp.AcpHost"/>
// y <see cref="OpenWebUI.Acp.AcpDispatch"/>.
using OpenWebUI.Acp;

// S1075 won't-fix: default local documentado, sobrescrito por OPENWEBUI_URL.
const string DefaultBaseUrl = "http://localhost:8080"; // NOSONAR
var baseUrl = (Environment.GetEnvironmentVariable("OPENWEBUI_URL")
    ?? DefaultBaseUrl).TrimEnd('/');
var host = new AcpHost(baseUrl,
    Environment.GetEnvironmentVariable("OPENWEBUI_ACP_TOKEN"));

await AcpDispatch.RunStdinLoopAsync(
    host.Send,
    root => AcpDispatch.HandleAsync(
        root, host.Token, host.Reply, host.ReplyError,
        host.NewSessionAsync, host.PromptAsync));
