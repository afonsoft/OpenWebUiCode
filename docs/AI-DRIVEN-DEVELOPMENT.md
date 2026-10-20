# AI-Driven Development (ADD)

## O que é

**ADD** é o modelo de execução deste repositório: **agentes de IA executam o ciclo de desenvolvimento de ponta a ponta** — contexto → plano → branch → implementação → testes → PR → correção de feedback — enquanto **humanos direcionam e aprovam**.

Relação com **SDD** (Spec-Driven Development):

- **SDD é a metodologia** — SPECs em `.specs/` são a fonte de verdade governante: definem *o quê* e *por quê* antes de qualquer linha de código.
- **ADD é o modo de operação** — *quem* executa o SPEC é o agente. O SPEC é o contrato; o agente é o executor.

Ou seja: o projeto trabalha **SDD na especificação, ADD na execução** — o foco operacional é o agente, não a mão de obra humana.

## Papéis

| Ator | Responsabilidade |
|---|---|
| **Humano** | Escreve/aprova SPEC, revisa PR, aprova merge, define direção e prioridade |
| **Agente** | Carrega harness → plano de execução → branch `feature/{Agent}-*` → código + testes → `build → test` → PR → responde a review |
| **CI (gates objetivos)** | Substitui verificação manual repetitiva: `Gate / CI` (admission-verdict), `pr-test-policy`, `env-drift`, `gitleaks`, coverage ratchet |

## Ciclo de vida ADD

```
SPEC aprovado (humano)
  → agente lê SPEC + harness (CLAUDE.md, rules, memory)
  → plano → branch → implementação → testes
  → gates locais (build, test, drift checks)
  → PR → gates CI (Gate/CI, test-policy, security-scan)
  → revisão humana → merge (humano)
  → SPEC arquivado em docs/specs/
```

## Onde vive cada peça

| Caminho | Papel no ADD |
|---|---|
| `.specs/` | SPECs ativas — o contrato que o agente executa (lado SDD) |
| `docs/specs/` | SPECs entregues (arquivo morto, histórico de decisões) |
| `CLAUDE.md` | Entry point do agente — stack, comandos, hard/soft rules |
| `.claude/` | Harness do agente: `rules/`, `agents/`, `skills/`, `memory/`, `CONTEXT.md`, `RULES.md` |
| `.devin/` | `blueprint.yaml` (ambiente reproduzível do agente) + skills instaladas |
| `.github/` | Gates que tornam a execução autônoma segura (workflows, ISSUE_TEMPLATE, dependabot) |
| `tests/` | Rede de segurança — `pr-test-policy` exige teste quando `src/*.cs` muda |

## Princípios

1. **Humanos dirigem, agentes executam** — aprovação de SPEC e merge são gates humanos; todo o resto é do agente.
2. **O SPEC governa** — o agente não improvisa fora do escopo aprovado; divergência de contrato é documentada no SPEC.
3. **Gates objetivos > revisão manual** — o que a CI consegue verificar (testes, secrets, drift, coverage) ela verifica; a revisão humana foca em arquitetura e intenção.
4. **Contexto como código** — o harness (`.claude/`, `CLAUDE.md`, memory) é versionado e evolui como parte do produto; um agente novo deve conseguir operar o repo só lendo o harness.
5. **Auditoria total** — branch nomeada pelo agente (`feature/{Agent}-{YYYYMMDD}-{slug}`), commits com co-autoria, SPEC arquivado em `docs/specs/`.

## Referências

- [`.specs/`](../.specs/) — SPECs ativas · [`docs/specs/`](./specs/) — entregues
- [`CLAUDE.md`](../CLAUDE.md) — entry point e regras do agente
- [`.claude/`](../.claude/) — harness (rules, skills, memory)
- [`README.md`](./README.md) — documentação do sistema
