# SPEC-20261114-user-message-markdown

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `user-message-markdown` |
| Type | `Frontend` |
| Stack | `.NET 10 / Blazor WebAssembly / Markdig` |
| Repository | `OpenWebUiCode` |
| Branch | `feature/Devin-20261114-user-message-markdown` |
| Ticket | `GH#728` |
| Status | `Completed` |

---

## 1. User Story

> Como usuário do Open WebUI, quero que as mensagens que eu envio no chat sejam
> renderizadas com formatação Markdown (negrito, itálico, listas, links,
> blocos de código, etc.) para que a leitura e a troca de informações fiquem
> mais ricas e claras — da mesma forma como as respostas do assistente já são
> exibidas.

---

## 2. Escopo

### Dentro do escopo
- Renderizar mensagens do usuário (`role == "user"`) via Markdown → HTML sanitizado.
- Reutilizar o pipeline existente (`MarkdownService.ToHtml`) — zero nova dependência.
- Ajustar classes CSS do balão do usuário para suportar prosa formatada.
- Manter o cache por conteúdo (evitar re-parse a cada re-render).
- Toggle view/source para mensagens do usuário (paridade com assistant).
- Paridade em todas as superfícies: `MessageBubble.razor`, `SharedChat.razor`, `ChannelPage.razor`.

### Fora do escopo
- Editor WYSIWYG / live-preview no composer (textarea continua plain-text).
- Alteração do pipeline de envio (conteúdo é armazenado como Markdown cru).
- Syntax highlighting de blocos de código (se já funciona no assistant, funciona aqui — sem trabalho extra).

---

## 3. Requisitos Funcionais

### RF-001 — Renderização Markdown nas mensagens do usuário
O conteúdo de mensagens com `role == "user"` deve ser convertido de Markdown
para HTML sanitizado usando o mesmo `MarkdownService.ToHtml()` já utilizado
para mensagens do assistente, e renderizado via `@((MarkupString)…)`.

### RF-002 — Estilização do balão do usuário com prosa
O balão do usuário deve receber as classes Tailwind de prosa
(`prose prose-sm dark:prose-invert`) para que headings, listas, tabelas,
blocos de código e outros elementos HTML sejam exibidos corretamente, mantendo
a identidade visual do balão (fundo, arredondamento, padding, alinhamento à
direita).

### RF-003 — Cache de HTML renderizado por conteúdo
A renderização deve usar cache por conteúdo (mesmo padrão de
`MessageBubble.razor` linhas 268-278) para evitar re-parsing Markdig a cada
re-render — especialmente durante streaming de outras mensagens.

### RF-004 — Toggle view/source para mensagens do usuário
O botão de alternância entre markdown cru e HTML renderizado, já disponível
para mensagens do assistente (`ViewSource` parameter), deve funcionar também
para mensagens do usuário.

### RF-005 — Paridade nas superfícies de exibição
As seguintes páginas/componentes que exibem mensagens devem renderizar
mensagens do usuário como Markdown:
- `MessageBubble.razor` (chat principal)
- `SharedChat.razor` (chat compartilhado)
- `ChannelPage.razor` (canais / threads)

### RF-006 — Sanitização de segurança
Todo HTML gerado pela renderização Markdown de mensagens do usuário deve
passar pelo `HtmlSanitizer` existente (Ganss.Xss), removendo scripts,
event handlers e tags perigosas — a mesma garantia já aplicada nas respostas
do assistente.

---

## 4. Requisitos Não-Funcionais

### RNF-001 — Performance
A renderização Markdown não deve introduzir jank perceptível ao rolar o chat.
O cache por conteúdo (RF-003) é o mecanismo.

### RNF-002 — Compatibilidade visual
O balão do usuário deve manter fundo (`bg-gray-100 dark:bg-gray-850`),
arredondamento (`rounded-3xl`), alinhamento à direita e largura máxima
(`max-w-[85%]`). Apenas o interior ganha formatação.

### RNF-003 — Zero nova dependência
Nenhum pacote NuGet / npm deve ser adicionado. Reutilizar Markdig + Ganss.Xss.

---

## 5. Critérios de Aceitação (BDD)

### AC-01 — Markdown simples renderizado (RF-001)
```gherkin
Dado que o usuário envia uma mensagem contendo "**negrito** e *itálico*"
Quando a mensagem aparece no chat
Então o texto "negrito" está dentro de <strong> e "itálico" dentro de <em>
```

### AC-02 — Bloco de código renderizado (RF-001)
```gherkin
Dado que o usuário envia uma mensagem contendo "```python\nprint('hi')\n```"
Quando a mensagem aparece no chat
Então o bloco de código é exibido em <pre><code> com a classe de linguagem
```

### AC-03 — Link renderizado e abre em nova aba (RF-001, RF-006)
```gherkin
Dado que o usuário envia "[Google](https://google.com)"
Quando a mensagem aparece no chat
Então um <a href="https://google.com" target="_blank" rel="noopener"> é exibido
```

### AC-04 — Lista renderizada (RF-001)
```gherkin
Dado que o usuário envia "- item A\n- item B\n- item C"
Quando a mensagem aparece no chat
Então uma <ul> com 3 <li> é exibida
```

### AC-05 — Script injetado é removido (RF-006)
```gherkin
Dado que o usuário envia "<script>alert('xss')</script>texto"
Quando a mensagem aparece no chat
Então apenas "texto" é exibido, sem nenhum <script>
```

### AC-06 — Estilo do balão preservado (RNF-002)
```gherkin
Dado que o usuário envia "# Título\nParágrafo normal"
Quando a mensagem aparece no chat
Então o balão mantém fundo cinza, arredondamento e alinhamento à direita
E o heading <h1> é renderizado dentro do balão com estilo de prosa
```

### AC-07 — View/Source funciona para user (RF-004)
```gherkin
Dado que uma mensagem do usuário contém "**negrito**"
Quando o usuário clica no botão "view source"
Então o texto cru "**negrito**" é exibido em vez do HTML
Quando o usuário clica novamente
Então volta para a versão renderizada
```

### AC-08 — Texto simples sem formatação aparece normalmente (RF-001)
```gherkin
Dado que o usuário envia "Olá, tudo bem?"
Quando a mensagem aparece no chat
Então o texto é exibido como parágrafo simples, sem quebra visual
```

### AC-09 — SharedChat e ChannelPage (RF-005)
```gherkin
Dado que uma mensagem do usuário com Markdown foi compartilhada
Quando a página /s/{shareId} é aberta
Então a mensagem do usuário é renderizada com Markdown
```

### AC-10 — Performance (RNF-001)
```gherkin
Dado um chat com 50 mensagens do usuário contendo Markdown
Quando o usuário rola o chat de cima a baixo
Então não há jank perceptível e o FPS não cai abaixo de 30
```

---

## 6. Plano de Tarefas

| # | Tarefa | Arquivo(s) | RF |
|---|--------|------------|----|
| 1 | Remover a branch `user` que exibe texto puro e unificar com o caminho de rendering markdown (chamar `MarkdownService.ToHtml`) | `MessageBubble.razor` | RF-001, RF-003 |
| 2 | Ajustar classes CSS do balão: mesclar `prose prose-sm dark:prose-invert` com as classes de identidade visual do user (`rounded-3xl bg-gray-100 …`) | `MessageBubble.razor` | RF-002, RNF-002 |
| 3 | Garantir que `ViewSource` toggle funcione para `role == "user"` | `MessageBubble.razor` | RF-004 |
| 4 | Atualizar `SharedChat.razor` — renderizar mensagens do user com `MarkdownService.ToHtml` | `SharedChat.razor` | RF-005 |
| 5 | Atualizar `ChannelPage.razor` — idem | `ChannelPage.razor` | RF-005 |
| 6 | Regenerar `tailwind.css` (se novas classes Tailwind forem usadas) | `tailwind.input.css` → `tailwind.css` | — |
| 7 | Rodar drift guards (`check-css-classes`, `check-form-a11y`, `check-i18n-parity`) | — | — |
| 8 | Build Release (`dotnet build --configuration Release`) com 0 erros | — | — |

---

## 7. Definition of Done

- [x] Mensagens do usuário renderizadas como Markdown em `MessageBubble`, `SharedChat` e `ChannelPage`.
- [x] Sanitização XSS aplicada (HtmlSanitizer).
- [x] Cache por conteúdo ativo (sem re-parse por keystroke).
- [x] View/Source funcional para user.
- [x] Estilo visual do balão do usuário preservado.
- [x] Build Release com 0 erros.
- [x] Drift guards passando.
- [x] Nenhuma nova dependência adicionada.

---

## 8. Riscos e Mitigações

| Risco | Impacto | Mitigação |
|-------|---------|-----------|
| XSS via Markdown do usuário | Alto | `HtmlSanitizer` já configurado; cobertura por AC-05 |
| Quebra visual do balão do user com prosa | Médio | Testar headings, listas, tabelas, código dentro do balão; ajustar `max-w` e `overflow` |
| Jank com muitas mensagens | Médio | Cache por conteúdo (RF-003) elimina re-parse; validar AC-10 |

---

## 9. Open Questions

| # | Questão | Default aplicado |
|---|---------|-----------------|
| 1 | Habilitar syntax highlighting (highlight.js / Prism) nos blocos de código do user? | Não — fora do escopo; se o assistant já tem, o user herda automaticamente |
| 2 | Limitar o tamanho do heading renderizado dentro do balão (ex.: `h1` muito grande)? | Sim — aplicar `prose-sm` que já limita tamanhos |
| 3 | Exibir preview de Markdown no composer antes de enviar? | Não — fora do escopo desta SPEC |
