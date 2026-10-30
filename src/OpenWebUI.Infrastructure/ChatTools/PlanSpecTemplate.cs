using System.Text;
using System.Text.RegularExpressions;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Contrato SPEC SDD do modo plan (SPEC-20261015-plan-spec-sdd): o
/// template condensado vai no system prompt do pipeline e a mesma fonte
/// alimenta a validação estrutural de <c>plan_exit</c> — uma definição
/// única evita divergência entre o que é pedido e o que é aceito.
/// </summary>
public static partial class PlanSpecTemplate
{
    /// <summary>
    /// Instrução injetada no system prompt do modo plan: análise do
    /// requisito + saída obrigatória no formato SPEC SDD detalhado.
    /// </summary>
    public const string Prompt = """
        A saída do plano é uma SPEC SDD completa em markdown — outro agente
        deve conseguir implementar lendo apenas o documento. Antes de escrever,
        analise o requisito: leia os arquivos relevantes do workspace, rode
        build/testes quando ajudarem a fundamentar o plano (via aprovação),
        entenda os padrões existentes e resolva ambiguidades pelo contexto;
        o que continuar bloqueante vai para "Open Questions".

        Estrutura obrigatória (siga exatamente estas seções):

        # SPEC-{YYYYMMDD}-{feature-name}
        ## 0. Metadata — tabela Field/Value: Feature, Type (Feature|Bugfix|
        Refactor|API|Infra|Frontend), Stack, Repository, Branch
        (feature/{Agent}-{YYYYMMDD}-{desc}), Ticket, Status (Draft).
        ## 1. User Story — As a / I want / So that + Problem context.
        ## 2. Scope — In scope e Out of scope (listas).
        ## 3. Technical Context — onde a mudança acontece, arquivos a ler e
        arquivos a criar/modificar (paths reais investigados por você).
        ## 4. Requirements — RF-001, RF-002... cada um com Description,
        Rules e Input → Output; feche com Business rules / invariants.
        ## 5. API Contract (se aplicável) — método, rota, auth, request,
        response e erros esperados.
        ## 6. Acceptance Criteria — cenários BDD testáveis (Given/When/Then)
        + tabela de Edge cases (Scenario | Input | Expected behavior).
        ## 7. Task Plan — tarefas pequenas ordenadas T1..Tn (discovery,
        implementação, verificação, validação, PR) + estratégia de
        validação por stack/type.
        ## 8. Organization Guardrails — não commitar em main/master/develop,
        não tocar .github/workflows/, sem secrets/.env, sem inventar
        requisitos, camadas respeitadas.
        ## 9. Definition of Done — checklist de entrega.
        ## Open Questions / Pending Ambiguity — dúvidas bloqueantes.

        Seja detalhado: requisitos numerados, paths concretos, critérios
        observáveis — nada de checklist superficial. Apresente a SPEC na
        resposta e chame builtin_plan_exit com o markdown completo em `plan`.
        """;

    /// <summary>Seções obrigatórias (match case-insensitive).</summary>
    public static readonly string[] RequiredSections =
    [
        "Metadata", "User Story", "Scope", "Requirements",
        "Acceptance Criteria", "Task Plan",
    ];

    /// <summary>
    /// Valida a estrutura mínima do plano: todas as seções obrigatórias e
    /// ao menos um requisito <c>RF-\d</c>. Devolve a mensagem de erro para o
    /// modelo corrigir, ou <c>null</c> quando válido.
    /// </summary>
    public static string? Validate(string plan)
    {
        var missing = RequiredSections
            .Where(s => !plan.Contains(s, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var needsRf = !RequirementIdRegex().IsMatch(plan);
        if (missing.Count == 0 && !needsRf)
        {
            return null;
        }

        var sb = new StringBuilder(
            "Erro: o plano precisa seguir o template SPEC SDD.");
        if (missing.Count > 0)
        {
            sb.Append(" Seções ausentes: ").Append(string.Join(", ", missing)).Append('.');
        }
        if (needsRf)
        {
            sb.Append(" Numere os requisitos como RF-001, RF-002...");
        }
        sb.Append(" Reenvie a SPEC completa em `plan`.");
        return sb.ToString();
    }

    [GeneratedRegex(@"RF-\d", RegexOptions.IgnoreCase)]
    private static partial Regex RequirementIdRegex();
}
