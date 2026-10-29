# SPEC-20261002-retrieval-advanced

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `retrieval-advanced` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261002-retrieval-advanced` |
| Ticket | Issue #53 |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** buscar na web, carregar URLs/YouTube e ter busca híbrida com reranking nas minhas coleções
**So that** o RAG tenha a mesma cobertura do upstream.

**Problem context:**
Hoje só existe embeddings + cosseno em SQLite (SPEC-20261001-rag-knowledge). Upstream tem `retrieval.py` (17 endpoints): `/process/{file,text,url,youtube,web}`, `/process/web/search` com ~12 engines, reranking, híbrido BM25+vetorial, engines de extração (tika/docling), `/ef/{text}` debug, `/reset/{db,uploads}`.

## 2. Scope

**In scope:**
- `POST /api/v1/retrieval/process/{file,text,url,web}` — loaders: URL (HTTP + extrair texto), arquivo, texto direto, batch.
- `POST /api/v1/retrieval/process/web/search` — engines configuráveis: SearXNG, DuckDuckGo, Tavily, Brave (subset viável sem SDK pesado; demais engines documentadas).
- Hybrid search BM25 + vetorial com peso configurável + reranking simples (ordenador local ou via provider).
- `GET /api/v1/retrieval/config` + `POST .../config/update` (engine de busca, engine de embedding, top-k, chunk size, hybrid on/off).
- `POST /api/v1/retrieval/reset/{db,uploads}` admin.
- YouTube: transcrição via endpoint público quando possível; fallback documentado.

**Out of scope:**
- Tika/Docling/Datalab (engines de extração externas pesadas) — documentar deferral.
- Vector DBs externos (Chroma/Qdrant/Milvus).

## 3. Technical Context

**Where:** `Infrastructure` (WebLoaderService, SearchEngineFactory, HybridSearchService, RerankService), `Api` (`RetrievalEndpoints`), `Application` (contratos), `Client` (settings de retrieval admin + toggle web-search no chat).

**Files to read:**
- `src/OpenWebUI.Infrastructure/Services/{RagService,EmbeddingService,ConfigService}.cs`
- `src/OpenWebUI.Api/Endpoints/KnowledgeEndpoints.cs`, `ChatEndpoints.cs`
- `docs/MIGRACAO-DOTNET.md` (linha retrieval)

**Files to create/modify:**
```text
src/OpenWebUI.Application/Contracts/RetrievalContracts.cs
src/OpenWebUI.Application/Interfaces/{IWebLoaderService,ISearchService,IRerankService}.cs
src/OpenWebUI.Infrastructure/Services/{WebLoaderService,SearchService,HybridSearchService}.cs
src/OpenWebUI.Infrastructure/Services/Search/{SearxngSearch,DuckDuckGoSearch,TavilySearch,BraveSearch}.cs
src/OpenWebUI.Api/Endpoints/RetrievalEndpoints.cs
src/OpenWebUI.Client/Components/SettingsModal.razor (aba retrieval)
tests/OpenWebUI.Api.Tests/RetrievalEndpointsTests.cs
```

## 4. Requirements

### RF-001: Process de fontes
- **Description:** `/process/url` e `/process/web` baixam, extraem texto e indexam chunks; `/process/text` indexa texto direto; `/process/file` indexa arquivo já enviado.
- **Rules:** timeouts e tamanho máx. configuráveis; falha de fetch → 502/400 com `detail` claro.
- **Input → Output:** `{url}` → `{chunks: n, ids: [...]}`

### RF-002: Web search
- **Description:** `/process/web/search` consulta engine configurada e indexa resultados.
- **Rules:** engine indisponível → `503 {detail}`; resultados deduplicados por URL; API key de engine nunca exposta.
- **Input → Output:** `{query, count}` → `[{title,url,snippet}]` indexados

### RF-003: Hybrid + reranking
- **Description:** `BM25 + vetorial` combinado com peso `hybrid_weight` (0..1); reranking opcional reordena top-k.
- **Rules:** `hybrid=false` → comportamento atual idêntico.
- **Input → Output:** query + collection → top-k reordenado

### RF-004: Config admin
- **Description:** Config de retrieval persistida em `ConfigService` (engines, keys mascaradas, top-k, chunk, hybrid).
- **Input → Output:** GET → config mascarada; POST → persiste

**Invariants:** segredos de engine nunca retornam ao cliente; falha de loader não corrompe chunks existentes.

## 5. API Contract

`POST /api/v1/retrieval/process/{file|text|url|web}` · `POST /process/web/search` · `GET|POST /api/v1/retrieval/config[/update]` · `POST /api/v1/retrieval/reset/{db|uploads}`
**Auth:** Bearer (config/reset = admin)

**Response:** `200 {...}` · `401` · `403` não-admin · `502` engine down · `503` web search desabilitada

## 6. Critérios de Aceite

- [ ] Indexar URL pública produz chunks consultáveis via retrieval.
- [ ] Web search com engine mock retorna e indexa resultados; sem engine → 503.
- [ ] Hybrid on/off produz rankings diferentes em teste controlado.
- [ ] Config admin persiste e mascara chaves.
- [ ] Testes NUnit com providers mock (HttpListener).

## 7. Notas

`[A DEFINIR]` ordem de implementação das engines de busca — recomendado: SearXNG → DuckDuckGo → Tavily → Brave.
YouTube transcripts podem exigir biblioteca ou scrape — avaliar viabilidade antes de prometer endpoint.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/68 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/53
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/48
