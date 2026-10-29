# SPEC-20261001-rag-knowledge

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `rag-knowledge` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261001-rag-knowledge` |
| Ticket | `GAP-implementation-rag-knowledge — Issue a criar via create-issues` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** fazer perguntas sobre meus documentos e bases de conhecimento com recuperação vetorial (RAG)
**So that** as respostas dos modelos usem o conteúdo real dos meus arquivos, como no upstream.

**Problem context:**
Hoje uploads injetam texto bruto no prompt (`ChatEndpoints`, parcial 🟡 em `docs/MIGRACAO-DOTNET.md:50`). Não existe vector store, embeddings nem coleções Knowledge (upstream: 9 vector DBs, `/workspace` aba Knowledge, web search RAG — `docs/MIGRACAO-DOTNET.md:60-61`).

## 2. Scope

**In scope:**
- Serviço de embeddings via provider configurável (Ollama `nomic-embed-text` por padrão; OpenAI `text-embedding-3` como alternativa).
- Vector store local simples (persistido em SQLite ou arquivo) com chunking de documentos.
- Entidade `Knowledge` (coleções nomeadas) + associação arquivo→coleção.
- RAG no pipeline de chat: `#arquivo` / `#coleção` e anexos geram contexto recuperado por similaridade, não texto integral.
- Aba Knowledge em `/workspace` (listar, criar, adicionar arquivos).
- Web search RAG: provider de busca configurável (`[A DEFINIR]` qual — upstream suporta vários), queries geradas por LLM (stub já existe).

**Out of scope:**
- Integração com vector DBs externos (Chroma, Qdrant, Milvus...) na primeira entrega — somente store local.
- Híbrido BM25+vetorial e reranking (fase 2).
- YouTube/web loaders avançados.

## 3. Technical Context

**Where the change happens:**
`Domain` (KnowledgeCollection, KnowledgeFile, EmbeddingChunk), `Application` (contratos IRagService/IEmbeddingService), `Infrastructure` (SQLite vector store, chamadas ao provider), `Api` (`/api/v1/knowledge`, retrieval no completions), `Client` (aba Knowledge, `#` autocomplete no input).

**Files to read before implementing:**
- `CLAUDE.md` · `.claude/rules/dotnet.md` · `.claude/rules/blazor.md`
- `src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs` (pipeline de prompt/contexto)
- `src/OpenWebUI.Api/Endpoints/FileEndpoints.cs` e `WorkspaceEndpoints.cs`
- `src/OpenWebUI.Infrastructure/Services/` (providers)
- `docs/MIGRACAO-DOTNET.md` (paridade)

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities/Knowledge*.cs
src/OpenWebUI.Application/Contracts/KnowledgeContracts.cs
src/OpenWebUI.Application/Interfaces/{IEmbeddingService,IRagService}.cs
src/OpenWebUI.Infrastructure/Services/{EmbeddingService,RagService,VectorStore}.cs
src/OpenWebUI.Api/Endpoints/KnowledgeEndpoints.cs
src/OpenWebUI.Client/Components/KnowledgePanel.razor, Pages/Workspace.razor, Components/ChatView.razor
tests/OpenWebUI.Api.Tests/KnowledgeEndpointsTests.cs
```

## 4. Requirements

### RF-001: Embeddings de arquivos
- **Description:** Ao subir um arquivo de texto, o sistema deve gerar chunks e embeddings persistidos.
- **Rules:** chunking por tamanho máximo configurável; falha do provider de embedding não pode quebrar o upload (fallback para injeção de texto atual).
- **Input → Output:** arquivo → chunks + vetores persistidos

### RF-002: Coleções Knowledge
- **Description:** CRUD de coleções nomeadas contendo arquivos, exposto em `/api/v1/knowledge` e na aba Knowledge do `/workspace`.
- **Rules:** coleção pertence ao usuário; admin vê as próprias; nome único por usuário.
- **Input → Output:** `{name, description?}` → coleção criada

### RF-003: Retrieval no chat
- **Description:** Referências `#arquivo`/`#coleção` e anexos devem injetar os top-K chunks mais similares ao prompt, dentro do budget de contexto.
- **Rules:** K e budget configuráveis; sem embeddings disponíveis → comportamento atual preservado.
- **Input → Output:** mensagem + referências → contexto relevante injetado

### RF-004: Web search RAG
- **Description:** Toggle de busca web no chat gera queries via LLM e injeta resultados recuperados.
- **Rules:** provider de busca configurável; sem provider → toggle desabilitado com aviso.
- **Input → Output:** mensagem + flag web → resultados resumidos no contexto

**Business rules / invariants:**
- Embeddings e chunks vivem por usuário e nunca vazam entre contas.
- Deleção de arquivo remove seus chunks.

## 5. API Contract

**Endpoint:** `GET|POST|DELETE /api/v1/knowledge`, `/api/v1/knowledge/{id}/files`
**Auth:** `Bearer` (JWT ou `sk-*`)

**Request:** `{"name": "...", "description": "..."}`
**Response:** `200 {"id","name","files":[...]}` · `401` sem auth · `409` nome duplicado

## 6. Critérios de Aceite

- [ ] Upload com provider de embedding ativo gera chunks e resposta usa contexto recuperado.
- [ ] `#coleção` no input injeta top-K chunks da coleção.
- [ ] Aba Knowledge lista/cria coleções e anexa arquivos.
- [ ] Sem provider de embedding → comportamento idêntico ao atual (sem quebra).
- [ ] Testes NUnit cobrem CRUD de knowledge e path de retrieval.

## 7. Notas

`[A DEFINIR]` vector store final (SQLite ext. vs arquivo vetorial próprio vs EF in-memory persistido) — recomendado: tabela `EmbeddingChunks` com busca por cosseno em C# (suficiente para volumes pessoais).
`[A DEFINIR]` provider de web search padrão.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/32 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/18
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/14
