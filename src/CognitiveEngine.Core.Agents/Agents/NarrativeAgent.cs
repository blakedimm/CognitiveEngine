namespace CognitiveEngine.Core.Agents.Agents;

using System.Text;
using System.Text.RegularExpressions;
using CognitiveEngine.Core.Agents.Bus;
using CognitiveEngine.Domain.Enums;
using CognitiveEngine.Domain.Epistemics;
using CognitiveEngine.Domain.Interfaces;

public sealed record NarrativeTokenPayload(string SessionId, string Token);

public sealed record CognitiveExecutionResult(
    string SessionId,
    string Question,
    string FinalAnswer,
    string MermaidDiagram,
    EpistemicMetrics Metrics,
    IReadOnlyList<VerifiedClaim> VerifiedClaims,
    long SymbolicLatencyMs = 0
);

public sealed partial class NarrativeAgent
{
    private readonly ICognitiveBus _bus;
    private readonly ILanguageModelClient _llmClient;

    [GeneratedRegex(@"[^a-zA-Z0-9_]", RegexOptions.Compiled)]
    private static partial Regex SanitizeNodeRegex();

    public NarrativeAgent(ICognitiveBus bus, ILanguageModelClient llmClient)
    {
        _bus = bus;
        _llmClient = llmClient;

        _bus.Subscribe("facts_verified", RenderReportAsync, CognitivePriority.Normal);
    }

    private async Task RenderReportAsync(object payload, CancellationToken ct)
    {
        if (payload is not FactsVerifiedPayload data)
            return;

        string sessionId = data.SessionId;
        string question = data.Question;
        var champion = data.Champion;
        var claims = data.Claims;
        var metrics = data.Metrics;
        var activeNodesMap = data.ActiveNodesMap;

        // ФИКС БАГА 1: Mermaid выводится ТОЛЬКО если есть подтверждённые рёбра ВЫЗОВА
        bool hasFunctionalConnections = claims.Any(c => c.GroundTruthExists && c.Relation is RelationType.Calls or RelationType.ExecTrace);
        string mermaidDsl = hasFunctionalConnections ? CompileMermaidTopology(claims, champion.Seeds) : string.Empty;
        var (gaugeFsr, gaugeEntropy, stateDescription) = InterpretEpistemicGauges(metrics, hasFunctionalConnections);

        var headerBuilder = new StringBuilder();
        headerBuilder.AppendLine("### Архитектурный отчёт ECS");
        headerBuilder.AppendLine($"- **Фактологическая точность (FSR):** {gaugeFsr}");
        headerBuilder.AppendLine($"- **Стабильность убеждений (Коллапс):** {gaugeEntropy}");
        headerBuilder.AppendLine($"- **Статус контура:** {stateDescription}\n");

        if (!string.IsNullOrWhiteSpace(mermaidDsl))
        {
            headerBuilder.AppendLine(mermaidDsl);
            headerBuilder.AppendLine();
        }

        string deterministicHeader = headerBuilder.ToString();

        // 2. Сборка фактов
        var verifiedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in claims)
        {
            verifiedNodes.Add(claim.Source);
            verifiedNodes.Add(claim.Target);
        }

        var factsBuilder = new StringBuilder();
        foreach (var vNode in verifiedNodes)
        {
            if (activeNodesMap.TryGetValue(vNode, out var content))
            {
                factsBuilder.AppendLine($"• [ВЕРИФИЦИРОВАННЫЙ УЗЕЛ: {vNode}]: {content}");
            }
        }

        if (verifiedNodes.Count == 0)
        {
            foreach (var seed in champion.Seeds)
            {
                if (activeNodesMap.TryGetValue(seed, out var content))
                {
                    factsBuilder.AppendLine($"• [ОПОРНЫЙ УЗЕЛ: {seed}]: {content}");
                }
            }
        }

        int contextAdded = 0;
        foreach (var (nodeId, content) in activeNodesMap.OrderByDescending(kv => kv.Value.Length))
        {
            if (!verifiedNodes.Contains(nodeId) && !champion.Seeds.Contains(nodeId) && contextAdded < 4)
            {
                factsBuilder.AppendLine($"• [КОНТЕКСТ: {nodeId}]: {content}");
                contextAdded++;
            }
        }

        // ФИКС БАГА 3: Экранируем синтетические токены '_to_', чтобы LLM не воспринимала их как реальные модули
        var conflictsBuilder = new StringBuilder();
        if (champion.Conflicts.Count > 0)
        {
            conflictsBuilder.AppendLine("\nЗАФИКСИРОВАННЫЕ РИСКИ ТОПОЛОГИИ:");
            foreach (var conflict in champion.Conflicts)
            {
                string cleanConflict = Regex.Replace(conflict, @"\b[a-zA-Z0-9_]+_to_[a-zA-Z0-9_]+\b", m =>
                {
                    var parts = m.Value.Split("_to_");
                    return $"между {parts[0]} и {parts[1]}";
                });
                conflictsBuilder.AppendLine($"⚠️ {cleanConflict}");
            }
        }

        bool hasGroundTruthEdges = claims.Any(c => c.GroundTruthExists);
        string proofContext = $"""
            ВЕРИФИЦИРОВАННЫЕ СВЯЗИ В БАЗЕ:
            {(hasGroundTruthEdges ? string.Join("\n", claims.Where(c => c.GroundTruthExists).Select(c => $"{c.Source} --[{c.Relation}, p={c.FusedScore:F2}]--> {c.Target}")) : "Прямых функциональных связей между узлами не обнаружено (компоненты изолированы).")}

            ФАКТЫ ИЗ КОДА:
            {factsBuilder}
            {conflictsBuilder}
            """;

        string systemPrompt = """
            Ты — ведущий инженер-архитектор когнитивного ядра ECS.
            Твоя задача — дать предельно краткий, строгий технический анализ на основе фактов кода.

            ПРАВИЛА:
            1. НЕ выводи диаграммы Mermaid и текстовые шкалы (они уже сгенерированы ядром).
            2. ЕСЛИ СВЯЗЕЙ НЕТ: Прямо констатируй, что компоненты изолированы и не вызывают друг друга.
            3. СТРОГИЙ ЗАПРЕТ ГАЛЛЮЦИНАЦИЙ: Опирайся только на факты кода из контекста. Не выдумывай вызовы, если они явно не подтверждены.
            4. СВЯЗИ И ЦЕПОЧКИ: Описывая цепочки вызовов, обязательно используй точные имена методов и явные связки: 'метод_А вызывает метод_Б' или 'метод_А -> метод_Б'.
            5. ФОРМАТ: 2-3 коротких абзаца. Строго до 150 слов.
            """;

        string userPrompt = $"КОНТЕКСТ ГРАФА:\n{proofContext}\n\nВОПРОС ПОЛЬЗОВАТЕЛЯ: {question}\n\nТЕХНИЧЕСКИЙ АНАЛИЗ:";

        await _bus.PublishAsync("narrative_token_streamed", new NarrativeTokenPayload(sessionId, deterministicHeader), CognitivePriority.BestEffort, ct);

        var proseBuilder = new StringBuilder();

        await foreach (var token in _llmClient.StreamCompletionAsync(
            systemPrompt,
            userPrompt,
            temperature: 0.05f,
            maxTokens: 400,
            ct: ct))
        {
            proseBuilder.Append(token);
            await _bus.PublishAsync("narrative_token_streamed", new NarrativeTokenPayload(sessionId, token), CognitivePriority.BestEffort, ct);
        }

        string proseAnswer = proseBuilder.ToString();

        if (string.IsNullOrWhiteSpace(proseAnswer))
        {
            proseAnswer = await _llmClient.GenerateCompletionAsync(
                systemPrompt,
                userPrompt,
                temperature: 0.05f,
                maxTokens: 400,
                ct: ct
            );
        }

        string fullFinalAnswer = deterministicHeader + proseAnswer;

        var result = new CognitiveExecutionResult(
            SessionId: sessionId,
            Question: question,
            FinalAnswer: fullFinalAnswer,
            MermaidDiagram: mermaidDsl,
            Metrics: metrics,
            VerifiedClaims: claims
        );

        await _bus.PublishAsync("pipeline_completed", result, CognitivePriority.Critical, ct);
    }

    private static string CompileMermaidTopology(IReadOnlyList<VerifiedClaim> claims, IReadOnlyList<string> seeds)
    {
        if (claims.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("```mermaid");
        sb.AppendLine("graph TD");
        sb.AppendLine("    classDef seed fill:#1e3a8a,stroke:#3b82f6,stroke-width:2px,color:#fff;");
        sb.AppendLine("    classDef verified fill:#1f2937,stroke:#10b981,stroke-width:1.5px,color:#fff;");

        var seenNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seedSet = new HashSet<string>(seeds, StringComparer.OrdinalIgnoreCase);

        foreach (var claim in claims)
        {
            seenNodes.Add(claim.Source);
            seenNodes.Add(claim.Target);

            string safeSrc = SanitizeNodeRegex().Replace(claim.Source, "_");
            string safeTgt = SanitizeNodeRegex().Replace(claim.Target, "_");

            string edgeDsl = claim.GroundTruthExists && claim.FusedScore >= 0.70f
                ? $"== \"{claim.Relation} (p={claim.FusedScore:F2})\" ==>"
                : $"-. \"{claim.Relation} (p={claim.FusedScore:F2})\" .->";

            sb.AppendLine($"    {safeSrc}[\"{claim.Source}\"] {edgeDsl} {safeTgt}[\"{claim.Target}\"]");
        }

        foreach (var node in seenNodes)
        {
            string safeNode = SanitizeNodeRegex().Replace(node, "_");
            if (seedSet.Contains(node))
            {
                sb.AppendLine($"    class {safeNode} seed;");
            }
            else
            {
                sb.AppendLine($"    class {safeNode} verified;");
            }
        }

        sb.AppendLine("```");
        return sb.ToString();
    }

    private static (string FsrGauge, string EntropyGauge, string Description) InterpretEpistemicGauges(EpistemicMetrics metrics, bool hasFunctionalConnections)
    {
        if (!hasFunctionalConnections)
        {
            return (
                "[░░░░░░░░░░] 0.0% (Изолировано)",
                "[░░░░░░░░░░] 0.0%",
                "Компоненты топологически изолированы. Прямых маршрутов в кодовой базе не обнаружено."
            );
        }

        int fsrBlocks = Math.Clamp((int)(metrics.Fsr * 10), 0, 10);
        string gaugeFsr = $"[{new string('█', fsrBlocks)}{new string('░', 10 - fsrBlocks)}] {metrics.Fsr * 100:F1}%";

        int entropyBlocks = Math.Clamp((int)(metrics.EntropyCollapse * 10), 0, 10);
        string gaugeEntropy = $"[{new string('█', entropyBlocks)}{new string('░', 10 - entropyBlocks)}] {metrics.EntropyCollapse * 100:F1}%";

        string description = metrics switch
        {
            { Fsr: >= 0.75f, EntropyCollapse: >= 0.50f } => "Монолитная детерминированность. Альтернативные пути подавлены.",
            { Fsr: >= 0.55f } => "Умеренная уверенность при наличии контекстных связей.",
            _ => "Компоненты изолированы либо фактологическая опора недостаточна."
        };

        return (gaugeFsr, gaugeEntropy, description);
    }
}