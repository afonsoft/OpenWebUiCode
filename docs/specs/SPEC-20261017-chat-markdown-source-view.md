# SPEC-20261017-chat-markdown-source-view

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-markdown-source-view` |
| Type | `Frontend` |
| Stack | Blazor WASM (.NET 10), Markdig + HtmlSanitizer, Tailwind v4 |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/agent-20261017-markdown-source-view` |
| Status | `Completed` |
| Ticket | [#443](https://github.com/afonsoft/open-webui/issues/443) |
| Origem | pedido do usuário 2026-10-17 — "exibir opção de visualizar o markdown (view, source) e copiar" |

## 1. User Story

**As a** usuário do chat
**I want** alternar cada bolha de resposta do assistente entre renderizado e fonte Markdown, e copiar o conteúdo
**So that** eu consiga inspecionar/copiar o markdown cru (como no GitHub/Devin) sem depender de reenvio ou devtools.

**Problem context:** `MessageBubble.razor` renderiza a resposta do assistente como `MarkupString` sanitizado via `MarkdownService` (Markdig). Já existe ação de copiar (`CopyAsync` → `openwebui.copyText` copia `DisplayContent`, o markdown cru), porém **não há como visualizar a fonte** — usuário não enxerga o markdown gerado nem confere sintaxe antes de colar num editor.

## 2. Scope

**In scope:**
- Toggle `View | Source` por bolha de assistente (estado local, não persistido).
- Modo source: `<pre>` monoespaçado com o markdown cru de `DisplayContent` (respeita `_versionOffset`).
- Botão copiar existente passa a funcionar nos dois modos e mostra feedback visual ("copiado").
- i18n nas 8 locales + regeneração de `tailwind.css` se novas classes forem usadas.

**Out of scope:**
- Edição inline do markdown (upstream tem "edit" só em mensagem do usuário — fora).
- Toggle em mensagens do usuário (elas já são `whitespace-pre-wrap` puro).
- Syntax highlight da fonte markdown (fonte é texto puro).

## 3. Technical Context

- `src/OpenWebUI.Client/Components/MessageBubble.razor` — bolha, action bar (`ShowActions && !Streaming`, linhas 67-137), `RenderedHtml` cacheado (212-229), `CopyAsync` (272-273).
- `src/OpenWebUI.Client/Services/MarkdownService.cs` — `ToHtml` Markdig+sanitizer.
- `src/OpenWebUI.Client/wwwroot/js/app.js` — `openwebui.copyText`.
- `src/OpenWebUI.Client/wwwroot/i18n/*.json` — chaves `chat.*` (8 locales, guard `tools/check-i18n-parity.py`).
- Mensagens de canal usam renderização paralela em `ChannelPage.razor` — avaliar reuso; se o markup for duplicado lá, aplicar o mesmo toggle ou extrair componente `[A DEFINIR — ver T1]`.

## 4. Requirements

- **RF-001 — Toggle view/source:** botão na action bar da bolha de assistente alterna entre HTML renderizado e fonte markdown. Default = view renderizada. Ícone `</>` ou olho; `aria-pressed` reflete o estado; tooltip+i18n.
- **RF-002 — Modo source:** `<pre class="…font-mono whitespace-pre-wrap break-all">` exibindo `DisplayContent` cru (sem escapar como HTML — `<pre>` com `@DisplayContent` já escapa). Scroll contido (`max-h-96 overflow-auto`).
- **RF-003 — Copy:** `CopyAsync` continua copiando `DisplayContent` (markdown cru) em ambos os modos; após copiar, trocar ícone por check ~1,5s (`aria-live` discreto ou título "copiado").
- **RF-004 — Streaming:** toggle e ação indisponíveis enquanto `Streaming` (action bar já é `!Streaming`); bolha segue renderizando normalmente.
- **RF-005 — Versões:** modo source exibe a versão selecionada (`_versionOffset`), não sempre `Message.Content`.
- **RF-006 — i18n + CSS:** novas chaves `chat.view_rendered`, `chat.view_source`, `chat.copied` (ou similares) nas 8 locales; `python3 tools/check-i18n-parity.py` verde; `tailwind.css` regenerado se houver classe nova (`tools/check-css-classes.py`).

## 5. Acceptance Criteria

- **Cenário 1:** dado resposta do assistente com markdown, quando clico no toggle, então vejo a fonte crua; clicando de novo, volto ao renderizado.
- **Cenário 2:** dado modo source ativo, quando clico copiar, então o clipboard recebe o markdown cru idêntico ao exibido.
- **Cenário 3:** dada mensagem com versões, quando navego para versão anterior em modo source, então a fonte exibida é a daquela versão.

| Edge case | Entrada | Esperado |
| --- | --- | --- |
| Conteúdo vazio | `Content=""` | Toggle renderiza `<pre>` vazio; sem erro |
| Conteúdo muito longo | >100KB | `<pre>` com scroll contido, sem travar (texto puro) |
| Streaming ativo | `Streaming=true` | Action bar oculta — toggle não aparece |

## 6. Task Plan

- [x] T1 — inspecionar `ChannelPage.razor`/`SharedChat.razor`: decidir reuso do toggle ou escopo só `MessageBubble`.
- [x] T2 — implementar toggle + `<pre>` em `MessageBubble.razor` + feedback de copiado.
- [x] T3 — adicionar chaves i18n nas 8 locales; rodar `check-i18n-parity.py`, `check-form-a11y.py`, `check-css-classes.py`; regenerar `tailwind.css` se preciso.
- [x] T4 — build (`dotnet build OpenWebUI.slnx -c Release`) + teste manual da bolha.

## 7. Organization Guardrails

- Sem commit direto em `main`; PR via `feature/agent-20261017-markdown-source-view`.
- `wwwroot/css/tailwind.css` só via gerador — nunca editar à mão.
- Conteúdo source é texto puro escapado pelo Razor — não reintroduzir `MarkupString` fora do pipeline sanitizado.

## 8. Definition of Done

- [x] Toggle funcional por bolha, acessível (teclado/aria).
- [x] Copy copia markdown cru em ambos os modos com feedback.
- [x] Drift guards verdes + build Release ok.

## 9. Open Questions

- Reusar o toggle em `ChannelPage.razor`/`SharedChat.razor` agora ou em follow-up? ➡️ recomendado: incluir se o markup for o mesmo componente; senão follow-up.

## Delivered

| Item | Link |
| --- | --- |
| Issue | https://github.com/afonsoft/open-webui/issues/443 |
| PR/commit | #447 · 72e1bcabe |
