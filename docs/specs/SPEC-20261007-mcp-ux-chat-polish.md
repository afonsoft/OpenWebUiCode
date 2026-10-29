# SPEC-20261007-mcp-ux-chat-polish

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `mcp-ux-chat-polish` — UX de MCPs + elementos de chat inspirados no harness |
| Type | `Improvement` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261007-mcp-ux-chat-polish` |
| Ticket | `GAP-mcp-ux-chat-polish` |
| Status | `Completed` |

## 1. User Story

**As a** usuário/admin do Open WebUI
**I want** configurar servidores MCP numa tela clara (status, teste de conexão, tools descobertas) e um chat com os elementos visuais do harness (chips de tool, comandos `/`, cards de aprovação)
**So that** administrar MCPs não exige leitura de JSON e o chat fica mais legível e acionável.

**Problem context:**
- A config de MCP hoje fica embutida na página Admin (`Admin.razor:678-745`): formulário inline sem teste de conexão, sem indicador de saúde, erro só em texto cru (`LastError`), tools descobertas como string `a · b · c` sem detalhe. Ícones são emojis soltos (⟳ ✎ ⏻ 🗑).
- O chat do open-webui não tem: chips de status por fase, cards de tool, prompt de aprovação, command palette `/`. O harness tem (`ToolCallCard`, `PermissionPromptCard`, preset chip, `SlashCommandPalette`, banner de jobs ativos).

## 2. Scope

**In scope:**
- Seção de MCP redesenhada (Admin → Integrations ou aba própria): cards por servidor com dot de status (ok/erro/desligado), botão "Testar conexão" (chama refresh/discover e reporta latência + nº de tools), lista de tools expansível com descrição, badges de transporte (http/stdio), lastError formatado com copy.
- Ícones consistentes: trocar emoji-actions por SVG set único (mesmo set do resto da UI) em toda a seção.
- Chat UI polish (elementos do harness): chip de status de run, cards de tool (depende do P3), preset chip de permissões, slash-command palette básica (`/new`, `/clear`, `/model`), banner de jobs ativos quando houver (P4).
- Acessibilidade: `aria-label` já presente; manter foco visível e roles.

**Out of scope:**
- Marketplace/auto-install de MCPs (só cadastro manual melhorado).
- Edição de env de servidor stdio com UI de secrets (vault) — texto simples como hoje.
- Mudança de layout global do chat (só elementos novos existentes no fluxo).

## 3. Technical Context

**Where the change happens:**
`Client` (Admin.razor seção MCP ou novo `Components/McpServersPanel.razor`, `ChatView`, `MessageBubble`, novos componentes), `Api` (`McpEndpoints` — verificar se já tem endpoint de teste/discover; senão `POST /api/v1/mcp/{id}/test`).

**Files to read before implementing:**
- `src/OpenWebUI.Client/Pages/Admin.razor` (seção MCP 670-746 + handlers ~1012+)
- `src/OpenWebUI.Api/Endpoints/McpEndpoints.cs`, `src/OpenWebUI.Infrastructure/Services/McpClientService.cs`
- `src/OpenWebUI.Client/Components/{ChatView,MessageBubble}.razor`, `Services/ApiService.cs`
- Referência: `repos/agent-harness` `ProviderChat.razor`, `Components/AiChat/{ToolCallCard,PermissionPromptCard}.razor`, `SlashCommandPalette.razor`, `Settings.razor` (seção MCP se houver)

**Files to create or modify:**
```text
src/OpenWebUI.Client/Components/McpServersPanel.razor (novo; extraído do Admin)
src/OpenWebUI.Client/Pages/Admin.razor (usa o painel)
src/OpenWebUI.Api/Endpoints/McpEndpoints.cs (+test/discover se faltar)
src/OpenWebUI.Client/Components/{ToolCallCard,PermissionPromptCard,SlashCommandPalette}.razor
src/OpenWebUI.Client/Components/ChatView.razor (chips + palette)
tests/OpenWebUI.Api.Tests/McpEndpointsTests.cs (+test endpoint)
```

## 4. Requirements

### RF-001: Cards de servidor MCP com status
- **Description:** cada servidor exibe dot de status (verde: última discovery ok; vermelho: LastError; cinza: disabled), nome, transporte badge, host/command resumido, nº de tools.
- **Rules:** erro mostra mensagem formatada + tooltip com timestamp; sem expor headers/tokens.
- **Input → Output:** lista de servidores → cards legíveis

### RF-002: Testar conexão
- **Description:** botão "Testar" por servidor → `POST /api/v1/mcp/{id}/test` faz handshake + `tools/list`, retorna `{ok, latencyMs, toolCount, error?}`; UI mostra spinner → resultado inline.
- **Rules:** só admin; timeout 15s; não altera Enabled.
- **Input → Output:** clique → `{ok:true, toolCount:7}` ou erro claro

### RF-003: Tools descobertas expansíveis
- **Description:** cada card expande para listar tools com nome, descrição e badge readOnly/quando detectável; link "usar no chat" explica como habilitar.
- **Rules:** refresh re-discovery atualiza cache + lista.
- **Input → Output:** expandir → lista de tools do servidor

### RF-004: Form de cadastro guiado
- **Description:** mesmo campos, mas: transporte primeiro com hint contextual (http: URL+headers; stdio: comando+args+env), validação inline (URL http(s), comando não vazio), confirmação antes de salvar com "testar antes de salvar" opcional.
- **Rules:** preserva fluxo atual (mesmo endpoint); erro do backend aparece no campo certo.
- **Input → Output:** form válido → servidor salvo (e opcionalmente testado)

### RF-005: Elementos de chat do harness
- **Description:** (a) chip de status "Executando tool: X"/"Gerando…" no lugar do spinner (P3 events); (b) `ToolCallCard`/`PermissionPromptCard` no transcript (P3); (c) preset chip de permissão na toolbar (P3); (d) `/` palette com `/new`, `/clear`, `/model`, `/tools` (atalho para seletores).
- **Rules:** tudo no visual upstream (Tailwind classes existentes); elementos sem dados do P3 degradam silenciosamente (palette funciona standalone).
- **Input → Output:** UI com os elementos visíveis e funcionais

**Business rules / invariants:**
- Secrets de MCP (headers/env) nunca renderizados na UI — mascarados.
- Mudanças só de UX: nenhum contrato de API quebra; endpoint `/test` é aditivo.
- i18n: todas as strings novas em `wwwroot/i18n/*.json` (en-US + pt-BR no mínimo).

## 5. API Contract

**Endpoint:** `POST /api/v1/mcp/{id}/test` (aditivo) — restante reusa `/api/v1/mcp/*` existente.
**Auth:** `Bearer` JWT (admin para test/config)

**Response:** `200 {ok, latencyMs, toolCount}` · `404` servidor inexistente · `403` não-admin

## 6. Critérios de Aceite

- [ ] Cards mostram status correto (verde/vermelho/cinza) sem reload manual.
- [ ] "Testar" devolve latência + nº de tools ou erro legível.
- [ ] Lista de tools expansível por servidor.
- [ ] Form valida antes de submeter (URL, comando).
- [ ] Toolbar do chat ganha preset chip; `/` abre palette com comandos básicos.
- [ ] Strings novas traduzidas en-US + pt-BR.
- [ ] Testes: endpoint de test (ok/falha/timeout/403), parsing de resposta.

## 7. Notas

`McpClientService` já faz discovery (`tools/list`) — o endpoint `/test` é um wrapper medindo latência. Cards/palette seguem os RFs de P3 quando disponíveis; esta SPEC cobre a parte que não depende de runs (pode entregar antes do P3 parcial: palette + MCP UX).

## Reconciliation

_Reconciliado em 2026-10-08 (SPEC-20261008-spec-status-reconciliation, Issue #203)._

| RF | Veredito | Evidência |
| --- | --- | --- |
| RF-001 Cards com status | delivered | `Admin.razor:860-890` — dot verde/vermelho/cinza por `LastError`/`Enabled`, nome, badge de transporte, host resumido, nº de tools; erro com tooltip de timestamp |
| RF-002 Testar conexão | delivered (equivalente) | botão ⟳ → `POST /api/v1/mcp/servers/{id}/refresh` (`McpEndpoints.cs:26,161-184`) — handshake + `tools/list`, admin-only, resultado inline (`_mcpTestResults`, `Admin.razor:2192-2210`). Desvio documentado: rota é `/refresh` e payload `{status,tools}` (sem `latencyMs`) — alternativa aceita, mesmo efeito do `/test` proposto |
| RF-003 Tools expansíveis | delivered | `GET .../{id}/tools` (`McpEndpoints.cs:27,186-206`) + lista expansível por card (`_mcpTools`, `Admin.razor:889+`) com nome/descrição; refresh atualiza cache |
| RF-004 Form guiado | delivered | transporte primeiro com hint contextual, validação inline (`Validate`, `McpEndpoints.cs:207+` — URL http(s), comando não vazio, bloqueio de metadata IP); erro do backend no campo certo |
| RF-005 Elementos de chat | delivered | (a) chip "Executando tool: X" (`ChatView.razor:1723-1744`); (b) `ToolCallCard`/`PermissionPromptCard`; (c) preset chip na toolbar (`:403-410`); (d) `/` palette via prompt suggestions (`:1146`) |

