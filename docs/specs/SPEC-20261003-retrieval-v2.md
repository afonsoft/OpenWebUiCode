# SPEC-20261003-retrieval-v2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `retrieval-v2` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261003-retrieval-v2` |
| Ticket | Issue #84 |
| Status | `Completed` |

## 1. User Story

**As a** admin/usuário RAG
**I want** engines de busca restantes e reranking por provider
**So that** `retrieval` upstream (17 eps) fecha paridade.

**Problem context:**
Entregue: searxng/duckduckgo/tavily/brave + híbrido BM25+vetor + rerank por cobertura. Falta: google_pse, jina, exa, kagi, perplexity + reranking via provider externo (Cohere/Jina reranker/colbert API).

## 2. Scope

**In scope:**
- Web search engines: `google_pse` (cx+key), `jina`, `exa`, `kagi`, `perplexity` — contrato comum retorna [{title,link,snippet}].
- Rerank provider: `RAG_RERANKING_ENGINE=external` → POST `{query, docs}` a endpoint configurável; fallback = rerank local atual.
- Config admin: selects + campos por engine.

**Out of scope:**
- Rerank local por modelo ML embutido; boaa/search1api etc.

## 3. Technical Context

**Files to read:**
- `src/OpenWebUI.Api/Endpoints/RetrievalEndpoints.cs`
- `src/OpenWebUI.Infrastructure/Services/{RagService,WebSearchService}.cs`

## 4. Requirements

### RF-001: Engines adicionais
- **Rules:** mesma assinatura de engine; sem key → engine indisponível no select.

### RF-002: Rerank externo
- **Input → Output:** docs → scores; timeout + fallback para rerank local em erro.

## 5. API Contract

`/api/v1/retrieval/config` ganha campos de engines/rerank; `process/web/search` aceita novas engines.

## 6. Critérios de Aceite

- [ ] Cada engine nova testada com provider mock.
- [ ] Rerank externo altera ordem; erro → fallback local.
- [ ] Config mascarada idem.

## Delivered

- **PR**: https://github.com/afonsoft/OpenWebUiCode/pull/100
- **Issue**: https://github.com/afonsoft/OpenWebUiCode/issues/84
- **Epic**: https://github.com/afonsoft/OpenWebUiCode/issues/79

Entregue: 5 engines novas (`google_pse`, `jina`, `exa`, `kagi`, `perplexity`)
com base URL configurável por engine (permite mocks e endpoints regionais),
rerank externo `local|external` via POST `{query, documents[]}` → `{scores[]}`
com fallback automático para o rerank local, whitelist ampliada no endpoint
de config, merge mascarado das 7 novas chaves, sub-abas Audio e Retrieval
materializadas no admin (antes dead code) com UI por engine, e i18n das
labels (pt-BR/en-US).
