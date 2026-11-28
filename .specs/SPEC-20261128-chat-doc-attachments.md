# SPEC-20261128-chat-doc-attachments — card de documento em anexos de chat (estilo Devin web)

**Status**: Approved — pedido do usuário 2026-11-28

## Motivação

Anexos de texto/markdown hoje caem no chip genérico de
`AttachmentThumb.razor` (só mídia ganha preview). No Devin web, um doc
anexado à mensagem vira uma **miniatura clicável**: mostra nome/tipo,
permite copiar o conteúdo e baixar o arquivo, e clicar abre o documento
renderizado para visualização.

## Design

### `DocAttachmentCard.razor` (novo componente)

Card de documento usado em `MessageBubble` (e onde `AttachmentThumb`
hoje renderiza chip para tipos texto):

- Tipos cobertos: `markdown` (`.md`, `text/markdown`), `text` (`.txt`,
  `.log`, `.csv`, `.json`, `.yaml`, `.xml`, `.cs`, `.py`…) — detectado
  por extensão/`ContentType` com fallback ao chip atual.
- Layout: thumbnail à esquerda (ícone doc + preview das primeiras
  linhas do arquivo renderizado em miniatura, degradê no rodapé),
  nome + tamanho + tipo à direita; ações: copiar (clipboard via JS
  interop `copyText`), download (`GET /api/v1/files/{id}/content` com
  auth → object URL → `<a download>`), e clique no card abre o viewer.
- Carregamento: fetch lazy do `GET /api/v1/files/{id}/content`
  (mesmo pipeline `openwebui.media` do thumb, já autenticado) — primeiro
  ~4 KB para a miniatura; conteúdo completo só ao abrir o viewer ou
  copiar/baixar.

### `DocViewerModal.razor` (novo)

- Modal fullscreen-ish (mobile: full-screen sheet; desktop: ~80vw/80vh)
  com o conteúdo renderizado: markdown → HTML sanitizado (mesmo pipeline
  de render markdown da `MessageBubble`); demais textos → `<pre>` com
  syntax highlight leve se já existir no projeto, senão monospace.
- Header: nome, ações copiar/download/fechar; scroll interno;
  `Escape`/backdrop fecha. i18n via `L[...]` flat nos 8 locales.

### Integração

- `MessageBubble.razor`: onde hoje cai no chip de texto, trocar por
  `DocAttachmentCard` quando `IsDoc` (nova flag em
  `ChatMessageFileModel` calculada no client por extensão/type —
  sem mudança de schema).
- Sem alteração de backend: usa `FileEndpoints` existente.

### Testes

- Unit: classificação `IsDoc` por extensão/type (cobertura dos casos +
  fallback não-doc).
- E2E/Componente: anexar `.md` numa mensagem → card com nome; copiar
  chama clipboard; download gera blob; clique abre modal com conteúdo.

## Critérios de aceite

1. `.md` anexado aparece como miniatura com nome/tipo.
2. Botões copiar e baixar funcionam.
3. Clique abre visualização renderizada (markdown → HTML).
4. Mobile: modal full-screen, ações acessíveis por toque.
