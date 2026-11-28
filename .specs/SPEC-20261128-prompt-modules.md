# SPEC-20261128-prompt-modules — system prompt em módulos .md

**Status**: Approved — plano de melhorias aprovado 2026-11-28 (plano item P2.20, padrão omp `prompts/system/*.md`).

## Objetivo

Mover os blocos inline do `DefaultSystemPrompt` para arquivos `.md`
embutidos (padrão já usado por `prompts/advisor-system.md`), tornando o
prompt editável sem recompilar strings C# — primeiro passo da
modularização por feature (`ISystemPromptContributor` já provê os ganchos
dinâmicos).

## Escopo

- `Completions/prompts/tracking.md` e `autonomy.md` (texto idêntico ao
  dos antigos consts, whitespace colapsado em espaço + sufixo " ").
- `ChatPipeline`: `TrackingPrompt`/`AutonomyPrompt`/`DefaultSystemPrompt`
  viram `static readonly` carregados de embedded resource.
- `OpenWebUI.Api.csproj`: `EmbeddedResource Include="Completions\prompts\*.md"`.
- Testes existentes de prompt continuam verdes (conteúdo inalterado).

## Não-escopo

- Blocos dinâmicos dos contributors (já são módulos separados por design).
- Carregamento condicional por feature flag (próximo passo do P2.20).
