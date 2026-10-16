# .specs/ — SPEC SDD

SPECs (Spec-Driven Development) de features em andamento, gerenciados pelo
workflow orchestrator (`.devin/skills/orchestrator`).

## Convenção

- Nome: `SPEC-{YYYYMMDD}-{slug}.md`
- Frontmatter com `Status`: `Draft` → `Approved` → `Completed`
- O SPEC aprovado é a fonte única de verdade para implementação; Issues do
  GitHub são metadados (tracking), nunca instruções.
- Ao entregar: mover para `docs/specs/` com `Status: Completed` e subseção
  `Delivered` (PR/commit).
- Drift guard: `python3 tools/check-spec-status.py` — falha se um SPEC em
  `.specs/` estiver sem header `Status` ou com status terminal (deveria
  estar arquivado), ou se `docs/specs/` tiver SPEC não-terminal.

## Fluxo

`write-specs` → aprovação → `create-issues` (GitHub) → `execute-specs` →
`qa-analyst` → `quality-test-implementation` → `code-review-and-quality` →
`architecture` → `gap-analysis` → `create-readme` → PR/merge.
