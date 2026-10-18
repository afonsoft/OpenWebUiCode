# SPEC-20261018-attachments-v2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `attachments-v2` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261018-attachments-v2` |
| Status | `Approved` |
| Parent | `docs/analise-opencode-v2.md` (P1.5 + P3.5) |

## 1. User Story

**As a** usuário anexando conteúdo a um chat de agente
**I want** arquivos de texto/código entrando como contexto, anexar diretório inteiro (lista de paths), imagens redimensionadas automático e paste grande virando anexo
**So that** o agente receba o material de verdade no prompt — paridade com opencode v2 `attachments` (text/dir/images, `media.image` auto-resize, limite 20 MiB, paste→file) — hoje só `image/*` chega ao modelo.

## 2. Scope

**In scope:**
- `ChatPipeline`: `text/*`, `application/json|xml|csv|pdf-text`, extensões de código mapeadas (whitelist por extensão ou `ContentType` vazio com sniff) → carregam conteúdo como bloco de texto no user msg (limite por arquivo e total).
- Diretório: upload de pasta (ou path no workdir) → vira anexo "directory" com listing de paths (o agente lê os arquivos via tools, igual v2).
- `media.image` auto-resize: >20 MiB ou dimensão > limite do modelo → downscale (ImageSharp) antes de subir; mantém original no storage, envia o derivado.
- Paste grande no composer (>N chars, default 16KB) → vira anexo `pasted.txt` em vez de inline (toggle).
- Chips de anexo com ícone por tipo + contagem; anexos continuam dedupe/limpeza existente.

**Out of scope:**
- OCR/parse de PDF binário (reusa o extractor existente se houver; senão só texto). Zip upload descompactado (v2 não faz; anexar dir cobre).

## 3. Technical Context

**Where the change happens:**
`ChatPipeline.cs` (~L326-346, filtro `image/*`), `FileAttachmentService`/storage, composer (`ChatComposer.razor`) paste handler + chips, `MediaImageProcessor` novo (ImageSharp já é dep? verificar).

**Files to read before implementing:**
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs` (attachment loading ~L326)
- `src/OpenWebUI.Infrastructure/Services/FileAttachmentService.cs` ou equivalente
- `src/OpenWebUI.Client/Components/Chat/ChatComposer.razor` (paste, chips)
- `src/OpenWebUI.Domain/Entities.cs` (`ChatFile`/`Attachment`)

**Files to create or modify:**
```text
src/OpenWebUI.Api/Completions/ChatPipeline.cs              # inclui text/*+dir no contexto
src/OpenWebUI.Infrastructure/Services/AttachmentContentService.cs # NOVO: decode/sniff/limites
src/OpenWebUI.Infrastructure/Services/MediaImageProcessor.cs      # NOVO: resize ImageSharp
src/OpenWebUI.Client/Components/Chat/ChatComposer.razor           # paste→attachment, dir upload
src/OpenWebUI.Domain/Entities.cs                                # AttachmentKind {image,text,directory,paste}
tests/OpenWebUI.Api.Tests/AttachmentContentTests.cs             # NOVO
tests/OpenWebUI.Api.Tests/MediaImageProcessorTests.cs           # NOVO
```

## 4. Requirements

### RF-001: Texto e código no prompt
- **Description:** anexo com `text/*`, `application/json|xml|yaml|csv`, ou extensão da whitelist (`.cs,.razor,.py,.ts,.md,...`) é decodificado UTF-8 (fallback latin1) e injetado como `<file name="...">conteúdo</file>` no user message; binário detectado por NUL bytes → recusado com aviso "binary file".
- **Rules:** cap 256KB por arquivo e 1MB total por mensagem; truncamento marcado `[…truncated]`; `CacheFormat` marca blocos como cacheáveis se aplicável.

### RF-002: Diretório
- **Description:** upload multi-file com `webkitdirectory` (ou path relativo ao workdir) cria um `Attachment{Kind=directory}` cujo conteúdo é a listing `path\n` ordenada; cada arquivo individual também é persistido para leitura via tools.
- **Rules:** máx 2000 paths; symlink/out-of-tree segue regra `external_directory` (SPEC permissions-v2).

### RF-003: media.image resize
- **Description:** antes de enviar ao modelo, imagem >20 MiB ou >8192px de lado → resize mantendo AR para ≤2048px (ou limite do modelo) e reencode jpeg q85; <1MB permanece. Original guardado; `DerivedForLlm` aponta a versão enviada.

### RF-004: Paste → anexo
- **Description:** colar texto > `paste_threshold` (default 16KB, user-config) cria `pasted-YYYYmmdd-HHmmss.txt` anexado; toast informa. Toggle em Settings → Composer.

## 5. Acceptance Criteria

- Anexar `.cs` e `.md` → conteúdo aparece no prompt do modelo (verificável via run inspector).
- Anexar pasta com 50 arquivos → listing injetada + `file_read` lê cada um.
- PNG 30MB → enviado ao modelo com ≤2048px e sem falhar.
- Paste de 20KB → vira anexo automaticamente.
- Binário `.exe` recusado com mensagem clara; testes cobrem decode, caps, resize, paste.
