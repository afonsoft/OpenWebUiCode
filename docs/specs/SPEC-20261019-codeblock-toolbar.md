# SPEC-20261019-codeblock-toolbar

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `codeblock-toolbar` |
| Type | `Feature` |
| Stack | `.NET` (Blazor WASM + JS interop) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261019-codeblock-toolbar` |
| Status | `Delivered` |
| Parent | report do usuário — `<pre><code class="language-bash">` no chat renderiza sem barra de ferramentas e sem opção de copiar |

## 1. User Story

**As a** usuário do chat
**I want** todo bloco de código (`<pre><code>`) com uma barra de ferramentas (label da linguagem, botão **copiar** com feedback e opção de **formatar**) e `<code>` inline copiável
**So that** eu identifique a linguagem, copie o conteúdo em 1 clique e normalize a indentação quando fizer sentido — paridade com o code block do upstream.

## 2. Scope

**In scope:**
- Barra de ferramentas por bloco `pre > code` injetada via JS pós-render (mesmo padrão do `codeexec.js` existente): label da linguagem (`language-xxx` → `bash`, `json`, …), botão **Copiar** (clipboard + feedback "Copiado" temporário), botão **Formatar** e toggle **wrap** (quebra de linha).
- **Formatar**: `json`/`jsonc` → `JSON.parse` + re-serialização indentada (2 espaços); demais linguagens → o botão fica oculto/desabilitado (formatter seguro não existe; nunca reescrever código com heurística frágil). Bloco já formatado → no-op.
- **Wrap**: alterna `white-space: pre` ↔ `pre-wrap` no `pre` (default `pre` com scroll horizontal — paridade upstream).
- `<code>` **inline** (fora de `pre`): click-to-copy do conteúdo com affordance sutil (cursor + tooltip `chat.code.copy`).
- Aplicar também em `SharedChat.razor` (hoje renderiza markdown e **não** chama `enhance`).
- Labels localizados: dicionário `chat.code.*` passado do Blazor (`L[...]`) para a chamada JS — paridade i18n nos 8 locales.
- Regenerar `wwwroot/css/tailwind.css` — `@source './**/*.js'` já varre classes usadas em JS, mas o build precisa rodar.

**Out of scope:**
- Syntax highlighting (hljs/prism) — sem bundle de highlighter hoje; SPEC futura.
- Formatters para outras linguagens (prettier/js-beautify) — dependência pesada; v1 só JSON.
- Execução de código (já coberta por `codeexec.js` — o botão "▶ Executar" passa a morar na mesma barra).
- `_viewSource` (`MessageBubble:54`) — raw intencional.

## 3. Technical Context

**Where the change happens:**
`src/OpenWebUI.Client/wwwroot/js/` (novo módulo ou extensão do `codeexec.js`), `MessageBubble.razor` (única chamada `enhance` → unificada), `SharedChat.razor` (nova chamada), `wwwroot/i18n/*.json` (8 arquivos), `tailwind.input.css`/gerado.

**AS-IS evidenciado:**
- `MessageBubble.razor:58` injeta `@((MarkupString)RenderedHtml)` vindo de `MarkdownService.ToHtml` (Markdig → `HtmlSanitizer`; `class`/`id` preservados → `language-xxx` chega ao DOM).
- `codeexec.js:22-53` `enhance(root)`: varre `pre > code`, injeta `<button>▶ Executar</button>` só quando `RUNNABLE[lang]` (js/python) — **bash/json/etc. ficam sem nada**; guard `pre.dataset.codeexec`.
- `MessageBubble.razor:294-298` chama `codeExec.enhance` só quando `_codeExecEnabled` && role assistant && conteúdo mudou (`_enhancedFor`).
- `SharedChat.razor:46` usa `Markdown.ToHtml` sem nenhum enhance.
- Clipboard já existe: `openwebui.copyText` em `app.js:33` (`navigator.clipboard.writeText` com fallback silencioso).
- i18n: `chat.copy`/`chat.view_source` existem; chaves `chat.code.*` novas nos 8 locales (`pt-BR, en-US, es-ES, fr-FR, de-DE, it-IT, ja-JP, zh-CN`).

**Files to read before implementing:**
- `src/OpenWebUI.Client/wwwroot/js/codeexec.js` (padrão enhance + RUNNABLE/LABELS)
- `src/OpenWebUI.Client/Components/MessageBubble.razor` (`_contentRef`, `OnAfterRenderAsync`, `_codeExecEnabled`)
- `src/OpenWebUI.Client/Pages/SharedChat.razor` (onde chamar enhance)
- `src/OpenWebUI.Client/Services/MarkdownService.cs` (markup emitido)
- `src/OpenWebUI.Client/wwwroot/i18n/pt-BR.json` + `en-US.json` (padrão de chaves)

**Files to create or modify:**
```text
src/OpenWebUI.Client/wwwroot/js/codeblocks.js          # NOVO (ou merge em codeexec.js): toolbar por bloco
src/OpenWebUI.Client/wwwroot/js/codeexec.js            # mover botão Executar p/ a barra (se unificar)
src/OpenWebUI.Client/Components/MessageBubble.razor    # chamar openwebui.codeBlocks.enhance(_contentRef, labels)
src/OpenWebUI.Client/Pages/SharedChat.razor            # idem (enhance sempre — copy/format não dependem de codeExec)
src/OpenWebUI.Client/wwwroot/index.html ou boot        # carregar codeblocks.js (se módulo novo)
src/OpenWebUI.Client/wwwroot/i18n/*.json               # chaves chat.code.* (8 locales)
src/OpenWebUI.Client/wwwroot/css/tailwind.css          # regenerado (classes novas da barra)
tests/OpenWebUI.Client.Tests/*                         # cobertura do que dá para testar sem browser
```

## 4. Requirements

### RF-001: Barra de ferramentas por bloco
- **Description:** todo `pre > code` dentro do conteúdo renderizado ganha uma barra no topo do bloco: canto esquerdo = label da linguagem (derivado de `language-xxx`, fallback `text`/`code`), canto direito = botões [Formatar?] [Wrap] [Copiar] ([Executar] quando `codeExecEnabled` e linguagem em `RUNNABLE`). Estrutura: `pre` embrulhado num wrapper `div.codeblock` com header — não destruir o `pre` (execução/output do `codeexec` continua funcionando).
- **Rules:** idempotente via `pre.dataset.codeblock`; botões `type=button`, `aria-label` localizado, `title` com hint; dark/light via classes tailwind; feedback de cópia troca o label por ~1.5s ("Copiado"/"Copied") e não altera o conteúdo do bloco.

### RF-002: Copiar
- **Description:** botão copia `codeEl.innerText` (texto puro, sem HTML) via `openwebui.copyText`; fallback quando `navigator.clipboard` indisponível → seleção do range + `document.execCommand('copy')` ou no-op com feedback de erro no próprio botão.
- **Rules:** nunca incluir o texto dos botões; copiar o código **original**, não o formatado (formatação é só visual — ver RF-003).

### RF-003: Formatar
- **Description:** `language-json|jsonc` habilita "Formatar": `JSON.parse(innerText)` → `JSON.stringify(_, null, 2)` aplicado **no DOM** (`codeEl.textContent`); estado alterna "Formatar"↔"Minificar" (`JSON.stringify(_, null, 0)`). Parse falha → feedback inline no botão ("JSON inválido") sem alterar o bloco. Outras linguagens: botão não renderizado.
- **Rules:** formatação é view-only — copiar sempre retorna `innerText` corrente (estado atual do DOM é o que o usuário vê; decisão: copiar reflete o visível); bloco que já está indentado reindenta igual (idempotente).

### RF-004: Wrap toggle
- **Description:** "Wrap" alterna `white-space` do `pre` (`pre` ↔ `pre-wrap`) e mantém `overflow-x:auto` no modo `pre`; estado por bloco, não persiste.

### RF-005: Code inline copiável
- **Description:** `code` fora de `pre` ganha `cursor-pointer` + `title=chat.code.copy` + click→copia `textContent` com feedback no próprio elemento (sem layout shift). Excluir `code` dentro de `a`, `button` ou editable.
- **Rules:** não quebrar seleção de texto — usar `click` (não `mousedown`), sem `user-select:none`.

### RF-006: Integração e i18n
- **Description:** `MessageBubble` chama `openwebui.codeBlocks.enhance(_contentRef, labels)` em `OnAfterRenderAsync` (mesma condição `_enhancedFor`, mas **independente** de `_codeExecEnabled` — a flag só controla o botão Executar); `SharedChat` chama o mesmo após render. `labels` = `{copy, copied, format, minify, wrap, run, invalidJson}` vindos de `L["chat.code.*"]`.
- **Rules:** guard `_enhancedFor` evita re-injeção durante streaming parcial (conteúdo final dispara uma última vez); `data-codeblock` evita dupla barra em re-renders do mesmo conteúdo.

## 5. Acceptance Criteria

- Bloco ` ```bash ` do exemplo renderiza com barra: label `bash`, botões wrap+copiar visíveis.
- Copiar coloca exatamente o texto do bloco na área de transferência; label muda para "Copiado" e volta.
- Bloco ` ```json ` minificado → "Formatar" indeta; clique seguinte minifica de volta; JSON inválido → mensagem no botão, conteúdo intacto.
- ` ```javascript ` com code execution ON mostra Executar na mesma barra; OFF some — barra continua com copiar/wrap.
- `SharedChat` exibe a mesma barra.
- `python3 tools/check-css-classes.py`, `check-i18n-parity.py`, `check-form-a11y.py` OK; `tailwind.css` regenerado e commitado.
- Sem regressão visual em dark e light; `dotnet test` client + API verde.

## 6. Riscos

- **Sanitizer**: `class` já liberado; JS roda pós-sanitize → seguro. Não injetar HTML não sanitizado.
- **Streaming**: enhance durante streaming pode pegar bloco incompleto — rodar a cada `OnAfterRender` com guard por conteúdo já cobre; `data-codeblock` evita duplicar toolbar em re-render.
- **execCommand**: deprecated mas é o único fallback sem permissão de clipboard — isolar num try.
- **Format em bloco executável**: formatar muda `innerText` → o Executar roda o texto visível — JSON não é executável, sem conflito.

## 7. Delivered

- Branch `feature/Devin-20261019-codeblock-toolbar` (PR com `Closes #468`).
- `codeblocks.js` (barra: label + copiar + format JSON + wrap + Executar), `<code>` inline click-to-copy, `SharedChat` incluso.
- `codeexec.js` virou biblioteca (`isRunnable`/`runInBlock`) — o Executar vive na barra.
- i18n `chat.code.*` ×8 locales; `tailwind.css` regenerado; 2 testes bUnit novos (104/104 client).
- Guards: css-classes, form-a11y, i18n-parity, spec-status — verdes.
