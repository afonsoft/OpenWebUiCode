# SPEC-20261013-chat-media-attachments

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-media-attachments` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261013-chat-media-attachments` |
| Ticket | [#349](https://github.com/afonsoft/open-webui/issues/349) |
| Status | `Completed` |

## 1. User Story

**As a** usuário do chat
**I want** anexar imagens e vídeos vendo uma miniatura no composer, tê-los renderizados inline na mensagem enviada com ampliação por clique, e que imagens anexadas sejam enviadas ao modelo como conteúdo multimodal junto do meu prompt
**So that** eu consiga discutir o conteúdo visual com o modelo com contexto real da imagem, mantendo paridade com o upstream Open WebUI.

**Problem context:**
Hoje o composer já anexa arquivos (`ChatView.razor` → `_attachedFiles` → `POST /api/v1/files/`), mas:
- anexos aparecem só como chip de texto com o nome — sem miniatura de imagem/vídeo;
- `ChatMessage` persistido não guarda os anexos — no reload só resta o texto `📎 nome`;
- `MessageBubble` renderiza a mensagem do usuário como texto puro — sem mídia;
- `ChatCompletionMessage.Content` é `string` — a imagem **nunca chega ao modelo** (`BuildFileContextAsync` só injeta `ExtractedText`, que é nulo para binários).

## 2. Scope

**In scope:**
- Miniatura no composer para anexos `image/*` e `video/*` (demais tipos continuam chip de texto).
- Renderização inline da mídia na mensagem do usuário em tamanho contido.
- Lightbox fullscreen ao clicar (Esc/✕/backdrop fecham; vídeo com `controls`).
- Persistência dos anexos na mensagem (`FilesJson` em `ChatMessage` + `Files` no `ChatMessageModel`) para reaparecerem após reload/reattach.
- Envio multimodal das imagens anexadas ao modelo, por provider:
  - Ollama (`/api/chat`): campo `images: [base64]` na mensagem;
  - OpenAI (`/chat/completions`): `content` como array `[{type:"text"}, {type:"image_url", image_url:{url:"data:<mime>;base64,..."}}]`;
  - Anthropic: `content` como blocos `[{type:"text"}, {type:"image", source:{type:"base64", media_type, data}}]`.
- Erros do provider (ex.: modelo sem visão) propagados à bolha/run — nunca valor enlatado.
- Novas chaves i18n nos 8 locales + regen de `tailwind.css` + drift guards.

**Out of scope:**
- Análise de vídeo pelo modelo (frames, Gemini video) — providers atuais não aceitam vídeo no chat.
- Zoom/pan no lightbox.
- Downscale/compressão de imagem antes do envio ao LLM (envia-se o binário original; revisitar se custo/latência pesar).
- Capability-check de visão por modelo (SPEC futura).
- Anexos em mensagens editadas/regeneradas retroativamente (edição segue fluxo atual).
- Áudio/documentos com preview rico (pdf viewer etc.) — continuam chip de texto.

## 3. Technical Context

**Where the change happens:**
Três camadas. **Domain/Contracts**: `ChatMessage` ganha `FilesJson`; `ChatMessageModel` ganha `Files`; `ChatCompletionMessage` ganha lista de imagens resolvidas (server-side). **API/Pipeline**: `EnqueueMessageAsync` persiste `FilesJson` a partir de `FileIds`; `ChatEndpoints` serializa `Files` no GET/upsert; `ChatPipeline` resolve imagens dos anexos (request `FileIds` → última mensagem do usuário; `FilesJson` histórico → a própria mensagem) e anexa ao payload; `ProviderService` serializa por provider. **Client**: composer com thumbs via object URL; `MessageBubble` com faixa de anexos; novo `MediaLightbox`.

**Decisões aprovadas pelo usuário (2026-10-13):**
- Mídia carregada por fetch autenticado (`Bearer`) → `URL.createObjectURL` cacheado por fileId (auth é Bearer-only, sem cookie; nada de token em query string).
- Mudança de schema permitida: migration EF `ChatMessageFiles`.
- Modelo sem visão: envia mesmo assim; erro do provider aparece na bolha/run (regra do repo: erro real, nunca enlatado).
- Limites atuais mantidos: 100 MB/arquivo, 10 arquivos por envio.

**Files to read before implementing:**
- `CLAUDE.md` · `.claude/rules/` (global + blazor/ef-core quando houver)
- `src/OpenWebUI.Client/Components/ChatView.razor` (composer ~L348-419, `OnFileSelected` ~L1637, `SendAsync` ~L1662)
- `src/OpenWebUI.Client/Components/MessageBubble.razor` (render user ~L28-52)
- `src/OpenWebUI.Application/Contracts/ChatContracts.cs` (`ChatMessageModel` L16)
- `src/OpenWebUI.Application/Contracts/ModelContracts.cs` (`ChatCompletionMessage` L27)
- `src/OpenWebUI.Api/Endpoints/FileEndpoints.cs` (`UploadFileAsync`, `GetFileContentAsync`)
- `src/OpenWebUI.Api/Endpoints/ChatRunEndpoints.cs` (`EnqueueMessageAsync` L42-116)
- `src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs` (mapeamento de mensagens ~L300, upsert ~L1096)
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs` (`FileIds` → contexto L137-155)
- `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` (`BuildPayload` L1582 compartilhado Ollama/OpenAI — precisa discriminar; `BuildAnthropicPayload` L809)
- `src/OpenWebUI.Domain/Entities.cs` (`ChatMessage`)
- `tests/OpenWebUI.Api.Tests/FileEndpointsTests.cs` (padrões de teste)

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities.cs                              # ChatMessage.FilesJson
src/OpenWebUI.Application/Contracts/ChatContracts.cs          # ChatMessageFileModel + ChatMessageModel.Files
src/OpenWebUI.Application/Contracts/ModelContracts.cs         # ChatCompletionMessage.Images (ChatImageData: mime+base64)
src/OpenWebUI.Api/Endpoints/ChatRunEndpoints.cs               # resolve FileIds → FilesJson no enqueue
src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs                  # Files no GET/upsert de mensagens
src/OpenWebUI.Api/Completions/ChatPipeline.cs                 # anexa imagens às mensagens do usuário
src/OpenWebUI.Infrastructure/Services/ProviderService.cs      # payloads por provider
src/OpenWebUI.Infrastructure/Data/AppDbContext.cs             # mapeamento da coluna
src/OpenWebUI.Infrastructure/Migrations/*_ChatMessageFiles*   # nova migration (dotnet ef)
src/OpenWebUI.Client/Components/ChatView.razor                # thumbs no composer + Files no envio
src/OpenWebUI.Client/Components/MessageBubble.razor           # faixa de anexos + abre lightbox
src/OpenWebUI.Client/Components/MediaLightbox.razor           # NOVO — overlay fullscreen
src/OpenWebUI.Client/Components/AttachmentThumb.razor         # NOVO — thumb reusável (composer+bolha)
src/OpenWebUI.Client/Services/ApiService.cs                   # GetFileBytesAsync(id) se ausente
src/OpenWebUI.Client/wwwroot/js/app.js (ou js/media.js)       # cache de object URLs por fileId
src/OpenWebUI.Client/wwwroot/i18n/{en-US,pt-BR,es-ES,fr-FR,de-DE,it-IT,ja-JP,zh-CN}.json
src/OpenWebUI.Client/wwwroot/css/tailwind.css                 # regenerado (não editar à mão)
tests/OpenWebUI.Api.Tests/                                    # novos testes NUnit
```

## 4. Requirements

### RF-001: Miniatura de mídia no composer
- **Description:** Anexos com `ContentType` `image/*` exibem miniatura (~`size-16`, `object-cover`, cantos arredondados) no lugar do chip de texto; `video/*` exibe `<video muted preload="metadata">` na mesma moldura; demais tipos mantêm o chip atual.
- **Rules:** thumb é precedido do nome truncado apenas em tooltip/title; botão × remove o anexo (e revoga o object URL); estado `_uploading` mostra skeleton/spinner; drag ordenado não é exigido.
- **Input → Output:** `InputFile` selecionado → upload concluído → thumb visível sob a textarea.

### RF-002: Entrega autenticada de mídia (object URL)
- **Description:** O cliente obtém bytes de `GET /api/v1/files/{id}/content` via `HttpClient` autenticado e cria object URL via JS interop; URLs são cacheadas por `fileId` na sessão e revogadas ao remover o anexo/descartar o componente.
- **Rules:** nunca colocar token em URL/query; falha de fetch (401/404/rede) → placeholder de anexo indisponível (chip com nome), não imagem quebrada; cache bounded (revogar em `Dispose`/logout).
- **Input → Output:** `fileId` → `blob:<url>` reutilizável em `<img>/<video>`.

### RF-003: Persistência dos anexos na mensagem
- **Description:** `ChatMessage` ganha coluna `FilesJson` (JSON de `ChatMessageFileModel{Id,Name,ContentType,Size,Type}` com `Type` ∈ `image|video|file`); `ChatMessageModel` ganha `IReadOnlyList<ChatMessageFileModel>? Files`; enqueue e upsert serializam, GET devolve.
- **Rules:** migration EF `ChatMessageFiles` obrigatória; mensagens legadas sem a coluna leem `Files=null` sem quebrar; `Type` derivado do `ContentType` no servidor; `EnqueueMessageAsync` resolve `FileIds` → metadados do dono (ignora ids inexistentes/de outro usuário); o texto `📎 nomes` atual pode ser mantido como fallback textual ou removido — decidir no code review e registrar aqui.
- **Input → Output:** `EnqueueChatRunRequest{Content, FileIds}` → `ChatMessage.FilesJson` persistido → `ChatMessageModel.Files` no GET.

### RF-004: Mídia inline na mensagem
- **Description:** `MessageBubble` renderiza `Message.Files` acima do texto: imagens em faixa flex-wrap com `max-w` contido (~`max-h-64`/`max-w-xs`), vídeos com `<video controls preload="metadata">` contido; tipo `file` mantém chip `📎 nome` (download via object URL).
- **Rules:** usuário e assistant usam a mesma faixa se `Files` presente; bolha continua texto puro (sem markdown em msg de usuário); a11y: `alt` = nome do arquivo, `role="button"`/teclado abre lightbox.
- **Input → Output:** `ChatMessageModel.Files` → thumbs visíveis na bolha.

### RF-005: Lightbox de mídia
- **Description:** Novo `MediaLightbox.razor`: overlay fullscreen `fixed inset-0 z-*` fundo escuro; imagem `object-contain` em `max-h/max-w` da viewport; vídeo `controls autoplay`; fecha por Esc, botão ✕ e clique no backdrop; foco fica preso ao overlay enquanto aberto.
- **Rules:** sem zoom/pan em v1; um lightbox por vez; ao fechar o vídeo pausa; `aria-modal`, `role="dialog"`, `aria-label` i18n.
- **Input → Output:** clique/teclado na mídia → overlay aberto → fecha → foco volta à mídia.

### RF-006: Envio multimodal ao modelo (visão)
- **Description:** `ChatCompletionMessage` ganha `IReadOnlyList<ChatImageData>? Images` (`{MimeType, Base64Data}`); `ChatPipeline` resolve imagens — `request.FileIds` de tipo imagem entram na **última mensagem do usuário**, e `FilesJson` histórico anexa às respectivas mensagens; `ProviderService` serializa por provider.
- **Rules:** só `image/*` vira `Images`; vídeo/outros nunca viram parte de imagem; arquivo apagado do disco → imagem silenciosamente omitida (log `Debug`, sem PII); bytes lidos do storage do dono (reuso da checagem de `UserId`); serialização: Ollama `images:[b64]` na mensagem, OpenAI `content[]` com `image_url` data-url, Anthropic bloco `image` base64 — mensagem sem imagens mantém `content` string (compat); erro do provider propaga (sem fallback enlatado); `BuildPayload` compartilhado Ollama/OpenAI precisa de discriminação (param `provider` ou pós-processamento no call-site Ollama convertendo `image_url`→`images`).
- **Input → Output:** `FileIds`/`FilesJson` com imagem → mensagem multimodal no payload do provider → resposta que cita o conteúdo visual.

### RF-007: i18n e assets
- **Description:** Novas chaves (ex.: `chat.attachment_remove`, `chat.open_attachment`, `chat.close_preview`, `chat.attachment_unavailable`) nos 8 locales; `tailwind.css` regenerado após classes novas.
- **Rules:** paridade de chaves validada por `tools/check-i18n-parity.py`; não editar `tailwind.css` à mão.

**Business rules / invariants:**
- Auth é Bearer-only: nenhum recurso de mídia pode depender de cookie ou token em URL.
- `FilesJson` nunca contém paths de storage nem dados base64 — só metadados.
- Anexo só é visível/enviado se pertencer ao usuário dono da sessão (reuso da regra de `FileEndpoints`).
- Composer envia com texto vazio se houver anexos (fluxo atual com `chat.files_attached` segue válido).

## 5. API Contract

Sem endpoints novos. Contratos alterados:

**`POST /api/v1/chats/{id}/messages`** (existente) — `EnqueueChatRunRequest` inalterado no wire (`FileIds` já existe); comportamento novo: persiste `FilesJson` na `ChatMessage` criada.

**`GET /api/v1/chats/{id}`** e **`POST /api/v1/chats/{id}` (upsert)** — `ChatMessageModel` passa a serializar `files`:

```json
{
  "messages": [{
    "id": "...", "role": "user", "content": "analisa isso", "model": null,
    "timestamp": 1760300000,
    "files": [{ "id": "<fileId>", "name": "foto.png", "contentType": "image/png",
                "size": 81234, "type": "image" }]
  }]
}
```

**`POST /api/chat/completions`** — `ChatCompletionMessage` serializa `images` apenas server→provider (interno; o cliente nunca envia base64 — o servidor resolve de `FileIds`/`FilesJson`).

**Expected errors:** `401` sem auth; `404` arquivo de outro usuário/apagado (GET content); `400` >100MB (upload) — formatos atuais mantidos.

## 6. Acceptance Criteria

- [ ] **Dado** uma imagem anexada no composer **quando** o upload conclui **então** vejo a miniatura (não só o nome) com botão × que a remove.
- [ ] **Dado** um vídeo anexado **quando** o upload conclui **então** vejo o primeiro frame em moldura de thumb equivalente.
- [ ] **Dado** mensagem enviada com imagem **quando** a bolha do usuário renderiza **então** a imagem aparece em tamanho contido acima do texto.
- [ ] **Dado** imagem na bolha **quando** clico (ou Enter com foco) **então** abre overlay fullscreen; **quando** aperto Esc/✕/clico fora **então** fecha e o foco retorna.
- [ ] **Dado** vídeo na bolha **quando** abro o lightbox **então** o player tem `controls` e pausa ao fechar.
- [ ] **Dado** chat com imagem **quando** recarrego a página e reabro o chat **então** a imagem reaparece (persistida em `FilesJson`).
- [ ] **Dado** imagem anexada + prompt **quando** envio para modelo com visão (Ollama/OpenAI/Anthropic) **então** o provider recebe a imagem no formato nativo e a resposta considera o conteúdo visual junto do prompt.
- [ ] **Dado** modelo sem suporte a visão **quando** envio imagem **então** o erro do provider aparece na UI/run (sem resposta inventada).
- [ ] **Dado** arquivo `pdf`/`txt` anexado **quando** renderiza no composer e na bolha **então** continua chip de texto (sem thumbnail de mídia).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Arquivo deletado após envio | `GET content` → 404 | Bolha mostra chip `nome (indisponível)`, sem imagem quebrada |
| Sessão expirada | fetch de mídia → 401 | Mesmo placeholder; fluxo de reauth normal do app |
| Imagem corrompida | bytes inválidos | `onerror` → fallback chip; lightbox não abre |
| Anexo removido antes do envio | clique no × | object URL revogada; envio não inclui o fileId |
| Upload falha | `UploadFileAsync` null | erro `chat.upload_failed` (existente); sem chip/thumb |
| Chat legado sem `FilesJson` | coluna null | `Files=null`; bolha renderiza só texto |
| fileId de outro usuário no enqueue | ids alheios | ignorados ao montar `FilesJson` (regra de posse) |
| Modelo/provider rejeita imagem | erro upstream | erro real na bolha/run (paridade CLAUDE.md) |
| Mobile/touch | toque na thumb | abre lightbox; thumbs do composer rolam horizontal |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** ler arquivos da seção 3; confirmar mapeamento atual de `ChatMessage` no `AppDbContext` e padrão das migrations existentes.
- [ ] **T2 — Contratos + schema:** `ChatMessageFileModel`, `ChatMessageModel.Files`, `ChatMessage.FilesJson`, `ChatCompletionMessage.Images`/`ChatImageData`; migration `dotnet ef migrations add ChatMessageFiles --project src/OpenWebUI.Infrastructure --startup-project src/OpenWebUI.Api`.
- [ ] **T3 — Persistência API:** `EnqueueMessageAsync` resolve `FileIds`→`FilesJson`; `ChatEndpoints` (GET/upsert) serializam `Files` nas duas direções.
- [ ] **T4 — Multimodal:** `ChatPipeline` anexa imagens (FileIds→última user msg; FilesJson→msg dona); `ProviderService` serializa Ollama `images[]` / OpenAI `image_url` / Anthropic `image` com discriminação por provider.
- [ ] **T5 — Client mídia:** `ApiService.GetFileBytesAsync` (se ausente), helper JS de object URL cacheado, `AttachmentThumb` (img/video/chip), thumbs no composer de `ChatView`, `Files` na montagem da mensagem local.
- [ ] **T6 — Bolha + lightbox:** faixa de anexos em `MessageBubble`, `MediaLightbox` com Esc/backdrop/focus, placeholders de indisponível.
- [ ] **T7 — i18n + CSS:** chaves nos 8 locales; `tailwindcss -i ... -o ... --minify`; rodar `tools/check-css-classes.py`, `check-form-a11y.py`, `check-i18n-parity.py`.
- [ ] **T8 — Testes NUnit:** (a) enqueue persiste `FilesJson` e GET retorna `files`; (b) payload OpenAI emite `image_url` data-url; (c) Ollama emite `images[]`; (d) Anthropic emite bloco `image`; (e) fileId alheio/apagado é ignorado; (f) legado sem `FilesJson` não quebra.
- [ ] **T9 — Validation:** `dotnet build --configuration Release`, `dotnet test tests/OpenWebUI.Api.Tests`, drift guards, smoke manual (anexar img+vídeo → enviar → reload → lightbox), preencher DoD.
- [ ] **T10 — Done + PR:** DoD completo → `Status = Done` → PR em `feature/Devin-20261013-chat-media-attachments`.

**7.1 Validation strategy (.NET / Feature):**
- Unit tests para regras (serialização de payload por provider, derivação de `Type`, posse de arquivo).
- Integration tests para fluxos API/DB (enqueue→persist→GET; files de usuários isolados).
- Cobertura mínima **80%** nas unidades tocadas; drift guards do client verdes.

## 8. Organization Guardrails

- **Branches:** nunca commitar em `main`; usar `feature/Devin-20261013-chat-media-attachments`.
- **Workflows:** não modificar `.github/workflows/`.
- **Migrations:** schema novo só via `dotnet ef migrations add` (Soft Rule 2).
- **CSS:** `tailwind.css` é artefato gerado — regenerar, não editar à mão.
- **WASM boot:** não usar `UseStaticFiles`/`MapFallbackToFile` p/ framework assets (regra do repo).
- **Security:** sem token em URL; sem PII/base64 em logs; `FilesJson` só metadados; posse de arquivo checada no servidor.
- **Scope:** sem zoom, sem análise de vídeo, sem resize — fora de escopo aprovado.
- **Upstream parity:** layout/comportamento fiéis ao Open WebUI (thumbs no composer, mídia inline, lightbox).

## 9. Definition of Done

- [ ] Todos os requisitos (RF-001 a RF-007) implementados.
- [ ] Todos os critérios de aceite cobertos por testes/evidência (seção 7.1).
- [ ] Edge cases tratados (tabela da seção 6).
- [ ] `dotnet build` Release + `dotnet test` + 3 drift guards passando; cobertura ≥80% no tocado.
- [ ] Migration aplicada via `DatabaseMigrator` no startup; bases legadas sem `__EFMigrationsHistory` fazem baseline.
- [ ] Guardrails da seção 8 respeitados.
- [ ] Logs sem PII/tokens/base64; erros no formato genérico atual.

**Next action after DoD:** `Status = Done` e PR na branch `feature/Devin-20261013-chat-media-attachments` referenciando o ticket.

## Open Questions / Pending Ambiguity

- Nenhuma bloqueante. Decisão menor registrada em RF-003 (manter ou não o sufixo textual `📎 nomes` no `Content` — resolver em code review).

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/360 |
| Issue | https://github.com/afonsoft/open-webui/issues/349 |
