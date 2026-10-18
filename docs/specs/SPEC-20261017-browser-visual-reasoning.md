# SPEC-20261017-browser-visual-reasoning

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `browser-visual-reasoning` |
| Type | `Feature` |
| Stack | .NET 10, Microsoft.Playwright, tools built-in do chat, OCR local (tesseract) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/agent-20261017-browser-visual-loop` |
| Status | `Completed` |
| Ticket | [#446](https://github.com/afonsoft/open-webui/issues/446) |
| Origem | pedido do usuário 2026-10-17 — portar para o fork a ferramenta "Visual Browser Reasoning Loop com Playwright e OCR" (padrão OpenHands/Devin) |

## 1. User Story

**As a** usuário rodando o agente numa UI web (app que ele mesmo subiu)
**I want** que o agente navegue, interaja e observe visualmente páginas — com screenshots automáticos, snapshot de DOM/acessibilidade e OCR — se curando quando um seletor falha
**So that** ele valide fluxos de UI de forma autônoma em vez de só tirar um print estático.

**Problem context:** hoje existe apenas `browser_screenshot` (`BrowserScreenshotBuiltinTool` + `BrowserScreenshotService`) — one-shot `chrome --headless --screenshot`, **sem** sessão, clique, digitação, leitura de DOM ou OCR. O fluxo "raciocinar sobre a tela" (padrão browser_use do OpenHands/Devin) exige contexto de browser persistente e observação visual a cada ação — nada disso existe.

## 2. Scope

**In scope:**
- `BrowserSessionService` (Playwright): `IBrowser`/`IBrowserContext` por chat (isolamento de cookies/storage), lifecycle preso à conversa; auto-install do Chromium sob demanda.
- Novas tools built-in (mesma feature flag `browser.enabled`/`BrowserTools:Enabled`):
  - `browser_navigate` `{url}` — goto + observação pós-navegação.
  - `browser_act` `{action, selector?, text?, key?, value?, x?, y?}` — click/fill/press/select/scroll; falha de seletor → self-healing (RF-004).
  - `browser_observe` `{}` — screenshot + snapshot acessibilidade/DOM compacto (+ OCR quando disponível).
  - `browser_close` `{}` — fecha o contexto do chat.
- Observação visual pós-ação: PNG → `FileEntry` (padrão já usado pelo screenshot) + resumo textual (título, url, elementos interativos numerados).
- Self-healing de seletor: seletor falho → coleta candidatos (role/text/placeholder/fuzzy no accessibility tree) → retry 1× → se persistir, devolve candidatos rankeados ao modelo (nunca erro seco).
- OCR opcional: `tesseract` resolvido no host (mesmo espírito `ResolvePath`); ausente → omite bloco OCR sem falhar.
- SSRF guard reutilizado (`SsrfGuard`, loopback liberado); `browser_act` no tier `Exec` (efeitos colaterais), demais `Read`.
- Budgets: `BrowserTools:MaxActionsPerRun` (default 50), timeout por ação, viewport configurável.

**Out of scope:**
- Streaming contínuo de vídeo/screencast ao vivo (screenshots por ação bastam nesta fase).
- Multi-browser (Firefox/WebKit) — só Chromium.
- OCR gerenciado em NuGet (Tesseract wrapper pesado) — usa o binário do host.
- Login/SSO interativo dentro do browser (perfil persistente por usuário — follow-up).

## 3. Technical Context

- `src/OpenWebUI.Infrastructure/ChatTools/BrowserScreenshotService.cs` — resolução de binário (config `BrowserTools:Path` → PATH → `~/.cache/ms-playwright`) e spawn headless; a nova impl pode **preferir o bundle do Playwright** e manter o fallback chrome do PATH.
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/BrowserScreenshotBuiltinTool.cs` — padrão de tool: flag `browser.enabled`, `FileEntry` de PNG, `SsrfGuard` com `allowLoopback: true`.
- `src/OpenWebUI.Infrastructure/ChatTools/SsrfGuard.cs`, `PermissionRuleset.cs` (`ApprovalTier`), `BuiltinToolContext` (UserId/ChatId/UploadDir).
- `src/OpenWebUI.Api/Endpoints/BrowserToolEndpoints.cs` — `GET/PUT /api/v1/browser/config` (admin flag + `browserPath`).
- Referência de implementação (outro repo do usuário): `PlaywrightBrowserService` com retry "Executable doesn't exist" → `Program.Main(["install","chromium"])` — portar o padrão.
- Nova dependência NuGet `Microsoft.Playwright` (verificar breaking changes; versão estável ≥7 dias).
- `ChatView.razor`/`ToolCallCard.razor` — payload `imagePath` já renderiza screenshot inline; reutilizar.

## 4. Requirements

- **RF-001 — Sessão por chat:** `BrowserSessionService` mantém `IPlaywright`/`IBrowser` singleton de processo e `IBrowserContext` por `ChatId` (cookies/storage isolados entre conversas); `browser_close`/fim da run descarta o contexto.
- **RF-002 — Auto-install:** ao lançar, `PlaywrightException` "Executable doesn't exist" → `Microsoft.Playwright.Program.Main(["install","chromium"])` → retry 1×; flag `BrowserTools:AutoInstall` (default true) desliga. Sem browser nem auto-install → erro orientando instalação (mesmo espírito do screenshot atual).
- **RF-003 — Observação pós-ação:** toda `browser_navigate`/`browser_act` termina capturando screenshot (→ `FileEntry`+`imagePath` no payload) e snapshot textual: `title`, `url`, e lista numerada `[i]` de elementos interativos (role/name/href) extraída via accessibility snapshot/DOM pruning — é esse índice que o modelo usa em `browser_act` sem precisar de seletor CSS.
- **RF-004 — Self-healing:** seletor não achou elemento → (a) se o call veio com `text`, localizar por `GetByText`/role aproximado; (b) OCR do último screenshot localiza o label e mapeia para bounding box → clique por coordenada; (c) ainda falhou → retorna top-N candidatos ("elements like: …") como texto para o modelo escolher — nunca exceção crua.
- **RF-005 — OCR:** `browser_observe` inclui `ocr_text` quando `tesseract` está instalado e `BrowserTools:Ocr=true` (default false — extra bytes); ausente → campo omitido. `browser_act` usa OCR só no caminho de healing.
- **RF-006 — Segurança:** mesmas regras do `browser_screenshot` — `http/https` only, `SsrfGuard` com loopback permitido, LAN/cloud metadata bloqueados; `browser_act` requer tier `Exec` (gate de aprovação existente); screenshots passam por `SecretScrubber`? não — imagem binária, mas a URL/texto resultante sim.
- **RF-007 — Budgets:** `BrowserTools:MaxActionsPerRun` ações por run (contador no contexto); timeout por ação `BrowserTools:ActionTimeoutSeconds` (default 30); viewport default 1280×800.
- **RF-008 — UI admin:** `GET /api/v1/browser/config` passa a expor `playwrightReady`/`ocrAvailable` além de `enabled`/`browserPath` — aviso antes de habilitar.

## 5. API Contract

Tools (function calling, padrão `builtin:*`):

- `browser_navigate` → `{ "url": "https://…" }` → texto: `Navigated to … — title. Elements: [0] button "Login" …` + payload `{imagePath, fileId, url, title}`.
- `browser_act` → `{ "action":"click|fill|press|select|scroll", "selector"?, "index"?, "text"?, "key"?, "value"?, "x"?, "y"? }` → resultado + observação pós-ação (mesmo formato de `browser_observe`).
- `browser_observe` → `{}` → `{imagePath, title, url, elements[], ocr_text?}`.
- `browser_close` → `{}` → `StatusResponse`.

Endpoint existente `GET /api/v1/browser/config` resposta ampliada:

```json
{ "enabled": true, "browserPath": "…", "playwrightReady": true, "ocrAvailable": false }
```

## 6. Acceptance Criteria

- **Cenário 1:** dado app local em `http://localhost:8080`, quando o agente chama `browser_navigate` + `browser_act` click por índice, então a ação acontece e ele recebe screenshot + nova lista de elementos.
- **Cenário 2:** dado seletor `#btn-old` inexistente mas botão "Save" visível, quando `browser_act` falha no seletor, então o healing encontra o botão por texto/role e executa — ou devolve candidatos rankeados.
- **Cenário 3 (auto-install):** container sem Chromium → primeira chamada instala via Playwright CLI e prossegue (1 retry); falha de instalação devolve instrução clara.
- **Cenário 4:** `browser_act` aparece no gate de aprovação (tier Exec); `browser_observe`/`navigate` não exigem aprovação além do preset vigente.
- **Cenário 5:** dois chats simultâneos não compartilham cookies/storage.

| Edge case | Entrada | Esperado |
| --- | --- | --- |
| Sem tesseract | `browser_observe` | resposta sem `ocr_text`, sem erro |
| Ação além do budget | 51ª ação na run | resultado de erro amigável "budget esgotado" |
| URL LAN privada | `http://192.168.x.x` | bloqueado por SSRF (loopback só) |
| PlaywrightException persistente | crash de página | contexto do chat recriado na próxima chamada |

## 7. Task Plan

- [x] T1 — adicionar `Microsoft.Playwright` ao Infrastructure (versão estável ≥7d); `BrowserSessionService` com singleton browser + contexto por chat + auto-install (testes unitários do resolvedor).
- [x] T2 — tools `browser_navigate`/`browser_observe`/`browser_close` com observação padrão (screenshot FileEntry + elements indexados); registrar no `BuiltinToolRegistry`/`PermissionRuleset`.
- [x] T3 — `browser_act` com healing (text/role → OCR bbox → candidatos); tier Exec.
- [x] T4 — OCR via `tesseract` (resolver binário, flag `BrowserTools:Ocr`); config endpoint ampliado (`playwrightReady`, `ocrAvailable`).
- [x] T5 — budgets/timeout; testes NUnit (guard, healing com DOM fake, config); `dotnet test` + build Release.
- [x] T6 — documentar em `docs/` requisito do Chromium (Docker: `playwright install --with-deps chromium` ou env `BrowserTools__AutoInstall`) — **mudança de Dockerfile exige confirmação do usuário** (soft rule).

## 8. Organization Guardrails

- Nova dep NuGet: verificar breaking changes antes (soft rule).
- Dockerfile/`docker-compose`: alteração só com aprovação explícita do usuário — cobrir por documentação + auto-install em runtime primeiro.
- Nunca logar HTML/screenshot bruto em log de aplicação; artefatos vão a `FileEntry` do usuário.
- Cookies/sessão do browser ficam no contexto por chat — nada persiste em disco entre restarts (a menos que SPEC futura defina perfil).

## 9. Definition of Done

- [x] Agente navega+clina+observa numa página local com evidência visual no transcript.
- [x] Healing demonstrado (seletor quebrado recupera por texto/OCR ou lista candidatos).
- [x] Flags, budgets e SSRF respeitados; testes + build verdes; PR aberto.

## 10. Open Questions

- Manter `browser_screenshot` (one-shot) como tool separada ou fazê-la delegar à sessão? ➡️ recomendado: manter (zero-dep, mais leve para print único) — `browser_*` novo para sessões interativas.
- Persistir perfil do browser por usuário (logins reutilizáveis)? ➡️ recomendado: follow-up — exige criptografia de storage state.
- Instalar Chromium no Dockerfile? ➡️ recomendado: auto-install em runtime já cobre; decidir depois (precisa aprovação para mexer no Dockerfile).

## Delivered

| Item | Link |
| --- | --- |
| Issue | https://github.com/afonsoft/open-webui/issues/446 |
| PR/commit | #447 · 72e1bcabe |
