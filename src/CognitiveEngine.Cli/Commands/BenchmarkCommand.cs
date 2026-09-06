namespace CognitiveEngine.Cli.Commands;

using System.Diagnostics;
using System.Text;
using CognitiveEngine.Core.Search.Evaluation;
using CognitiveEngine.Domain.Epistemics;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Runtime;

public sealed record BenchmarkScenario(
    string Question,
    string Category,
    string StartNode,
    string TargetNode,
    bool IsCallerQuery,
    bool ShouldRefuse
);

public sealed class BenchmarkCommand
{
    private readonly SemanticCore _core;
    private readonly ILanguageModelClient _llmClient;
    private readonly AstGroundTruthValidator _validator;

    private static readonly BenchmarkScenario[] Scenarios =
    [
        new(
            Question: "Через какие промежуточные методы поток управления из main в code_ingest доходит до вызова apply_transitive_reduction_numpy в graph_optimizer?",
            Category: "MultiHop",
            StartNode: "func_code_ingest_main",
            TargetNode: "func_graph_optimizer_graphtopologyoptimizer_apply_transitive_reduction_numpy",
            IsCallerQuery: false,
            ShouldRefuse: false
        ),
        new(
            Question: "Какие конструкторы и методы вызывают reset_parameters в model_v7?",
            Category: "DirectionalCallers",
            StartNode: "reset_parameters",
            TargetNode: "",
            IsCallerQuery: true,
            ShouldRefuse: false
        ),
        new(
            Question: "Как функция catch_crash в graph_optimizer связана с CrossAttention в TransformerDecoder?",
            Category: "DistractorRefusal",
            StartNode: "catch_crash",
            TargetNode: "CrossAttention",
            IsCallerQuery: false,
            ShouldRefuse: true
        ),
        new(
            Question: "Как модуль pipeline_v7 и model_v7 связаны с построением тензоров и LinkPredictor?",
            Category: "ArchitectureOverview",
            StartNode: "pipeline_v7",
            TargetNode: "model_v7",
            IsCallerQuery: false,
            ShouldRefuse: false
        )
    ];

    public BenchmarkCommand(SemanticCore core, ILanguageModelClient llmClient)
    {
        _core = core;
        _llmClient = llmClient;
        _validator = new AstGroundTruthValidator(_core.Storage);
    }

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        Console.WriteLine("=================================================================");
        Console.WriteLine("🏁 ОБЪЕКТИВНЫЙ БЕНЧМАРК: COGNITIVE CORE (ECS) VS VECTOR RAG");
        Console.WriteLine("  ├─ Path A0: Pure Symbolic Engine (MCTS + GNN + ECS Censors, 0 LLM Tokens)");
        Console.WriteLine("  ├─ Path A1: GWM Engine Full (Symbolic Verified Graph + Streaming LLM)");
        Console.WriteLine("  └─ Path B:  Standard Vector RAG (Top-4 Semantic Chunks + LLM, No Graph)");
        Console.WriteLine("=================================================================\n");

        var reports = new List<BenchmarkScenarioReport>();

        for (int i = 0; i < Scenarios.Length; i++)
        {
            var sc = Scenarios[i];
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[Сценарий {i + 1}/{Scenarios.Length}] [{sc.Category.ToUpperInvariant()}]");
            Console.WriteLine($"Вопрос: \"{sc.Question}\"");
            Console.ResetColor();

            // 1. PATH A
            var swFull = Stopwatch.StartNew();
            var resultA = await _core.QueryAsync(sc.Question, ct);
            swFull.Stop();

            long coreOnlyMs = Math.Max(1, resultA.SymbolicLatencyMs);
            long fullA1Ms = swFull.ElapsedMilliseconds;

            // 2. PATH B
            var swB = Stopwatch.StartNew();
            var qEmbedding = await _llmClient.GenerateEmbeddingAsync(sc.Question, ct);
            var retrievedNodes = await _core.Storage.FindSimilarAsync(qEmbedding, topK: 4, ct);

            var ragContextBuilder = new StringBuilder();
            foreach (var node in retrievedNodes)
            {
                ragContextBuilder.AppendLine($"• [{node.Id}]: {node.Content}");
            }

            string ragSystemPrompt = """
                Ты — технический ассистент по кодовой базе. Ответь на вопрос пользователя строго по предоставленным фрагментам кода.
                Если в ответе описывается последовательность вызовов, явно указывай её в виде: метод_А вызывает метод_Б (или метод_А -> метод_Б).
                """;
            string ragUserPrompt = $"ФРАГМЕНТЫ КОДА ИЗ ХРАНИЛИЩА:\n{ragContextBuilder}\nВОПРОС: {sc.Question}\n\nОТВЕТ:";

            string baselineAnswer = await _llmClient.GenerateCompletionAsync(
                systemPrompt: ragSystemPrompt,
                userPrompt: ragUserPrompt,
                temperature: 0.1f,
                maxTokens: 500,
                ct: ct
            );
            swB.Stop();
            long baselineMs = swB.ElapsedMilliseconds;

            // 3. Валидация текста с учётом типа вопроса
            var pvrA1 = await _validator.EvaluatePvrAsync(resultA.FinalAnswer, isRefusalScenario: sc.ShouldRefuse, ct: ct);
            var pvrB = await _validator.EvaluatePvrAsync(baselineAnswer, isRefusalScenario: sc.ShouldRefuse, ct: ct);

            MultiHopEvaluationResult? hopA1 = null, hopB = null;
            DirectionalAccuracyResult? dirA1 = null, dirB = null;
            RefusalEvaluationResult? refA1 = null, refB = null;

            if (sc.Category == "MultiHop")
            {
                hopA1 = await _validator.EvaluateMultiHopAsync(resultA.FinalAnswer, sc.StartNode, sc.TargetNode, minHops: 2, ct);
                hopB = await _validator.EvaluateMultiHopAsync(baselineAnswer, sc.StartNode, sc.TargetNode, minHops: 2, ct);
            }
            else if (sc.Category == "DirectionalCallers")
            {
                dirA1 = await _validator.EvaluateDirectionAsync(resultA.FinalAnswer, sc.StartNode, sc.IsCallerQuery, ct);
                dirB = await _validator.EvaluateDirectionAsync(baselineAnswer, sc.StartNode, sc.IsCallerQuery, ct);
            }
            else if (sc.Category == "DistractorRefusal")
            {
                refA1 = _validator.EvaluateRefusal(resultA.FinalAnswer, sc.ShouldRefuse);
                refB = _validator.EvaluateRefusal(baselineAnswer, sc.ShouldRefuse);
            }

            reports.Add(new BenchmarkScenarioReport(
                Question: sc.Question,
                Category: sc.Category,
                PvrA1: pvrA1,
                PvrB: pvrB,
                LatencyMsA0: coreOnlyMs,
                LatencyMsA1: fullA1Ms,
                LatencyMsB: baselineMs,
                FsrA1: resultA.Metrics.Fsr,
                EntropyCollapseA1: resultA.Metrics.EntropyCollapse,
                MultiHopA1: hopA1,
                MultiHopB: hopB,
                DirectionalA1: dirA1,
                DirectionalB: dirB,
                RefusalA1: refA1,
                RefusalB: refB
            ));

            // ФИКС БАГА 2: Разделение счётчиков галлюцинаций сущностей и связей
            Console.ForegroundColor = ConsoleColor.Cyan;
            string fsrDisplay = (resultA.VerifiedClaims.Count > 0 && resultA.VerifiedClaims.Any(c => c.GroundTruthExists))
                ? $"{resultA.Metrics.Fsr * 100:F1}%"
                : "0.0% (Изолировано)";

            Console.WriteLine($"  [A0 Pure]: {coreOnlyMs} мс | Рёбер в графе: {resultA.VerifiedClaims.Count} | FSR: {fsrDisplay}");
            Console.WriteLine($"  [A1 GWM ]: {fullA1Ms} мс | Проза PVR: {pvrA1.Pvr * 100:F1}% ({pvrA1.ValidTransitions}/{pvrA1.TotalClaimedTransitions}) | Галлюцинаций: [Сущностей: {pvrA1.HallucinatedNodes.Count}, Связей: {pvrA1.InvalidTransitions}]");
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"  [B  RAG ]: {baselineMs} мс | Проза PVR: {pvrB.Pvr * 100:F1}% ({pvrB.ValidTransitions}/{pvrB.TotalClaimedTransitions}) | Галлюцинаций: [Сущностей: {pvrB.HallucinatedNodes.Count}, Связей: {pvrB.InvalidTransitions}]");
            Console.ResetColor();

            if (sc.Category == "MultiHop")
            {
                Console.WriteLine($"  └─ Multi-Hop: A1={hopA1?.ActualHops ?? 0} хопов (Цепь: {hopA1?.PathFound}) vs B={hopB?.ActualHops ?? 0} хопов (Цепь: {hopB?.PathFound})");
            }
            else if (sc.Category == "DirectionalCallers")
            {
                Console.WriteLine($"  └─ Directional Accuracy: A1={dirA1?.DirectionalAccuracyScore * 100:F1}% vs B={dirB?.DirectionalAccuracyScore * 100:F1}%");
            }
            else if (sc.Category == "DistractorRefusal")
            {
                Console.WriteLine($"  └─ Refusal: A1={(refA1?.ActuallyRefused == true ? "Отказ (Истина)" : "Ложь")} vs B={(refB?.ActuallyRefused == true ? "Отказ (Истина)" : "Ложь")}");
            }

            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("\n┌── [ОТВЕТ PATH A1 (GWM ENGINE)] ──────────────────────────────────────");
            Console.WriteLine(resultA.FinalAnswer.Trim());
            Console.WriteLine("└─────────────────────────────────────────────────────────────────────");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.WriteLine("┌── [ОТВЕТ PATH B (VECTOR RAG)] ───────────────────────────────────────");
            Console.WriteLine(baselineAnswer.Trim());
            Console.WriteLine("└─────────────────────────────────────────────────────────────────────");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("🔍 Связи, распознанные в A1: " +
                (pvrA1.Transitions.Count > 0
                    ? string.Join(", ", pvrA1.Transitions.Select(t => $"{t.Source} -> {t.Target} [{(t.ExistsInAst ? "AST: OK" : "AST: FAIL")} ({t.StatedRelation})]"))
                    : "нет"));
            Console.WriteLine("🔍 Связи, распознанные в B : " +
                (pvrB.Transitions.Count > 0
                    ? string.Join(", ", pvrB.Transitions.Select(t => $"{t.Source} -> {t.Target} [{(t.ExistsInAst ? "AST: OK" : "AST: FAIL")} ({t.StatedRelation})]"))
                    : "нет"));
            Console.ResetColor();

            Console.WriteLine();
        }

        PrintSummaryMatrix(reports);
    }

    private static void PrintSummaryMatrix(IReadOnlyList<BenchmarkScenarioReport> reports)
    {
        Console.WriteLine("=========================================================================================================");
        Console.WriteLine("📊 ОБЪЕКТИВНАЯ МАТРИЦА СРАВНИТЕЛЬНОЙ ОЦЕНКИ ПРОЗЫ (PROSE GROUND-TRUTH MATRIX):");
        Console.WriteLine("=========================================================================================================");
        Console.WriteLine("Категория         | Latency A0 | Latency A1 | Latency B  | Prose PVR A1 | Prose PVR B  | Вердикт");
        Console.WriteLine("---------------------------------------------------------------------------------------------------------");

        float sumPvrA1 = 0f, sumPvrB = 0f;
        long sumLatA0 = 0, sumLatA1 = 0, sumLatB = 0;

        foreach (var r in reports)
        {
            sumPvrA1 += r.PvrA1.Pvr;
            sumPvrB += r.PvrB.Pvr;
            sumLatA0 += r.LatencyMsA0;
            sumLatA1 += r.LatencyMsA1;
            sumLatB += r.LatencyMsB;

            string verdict = r.Category switch
            {
                "MultiHop" => (r.MultiHopA1?.PathFound, r.MultiHopB?.PathFound) switch
                {
                    (true, false) => "A1 раскрыл мост (B ослеп)",
                    (false, true) => "B нашёл мост (A1 ослеп)",
                    (true, true) => "Оба нашли цепь",
                    _ => "Оба провалили цепь (0 хопов)"
                },
                "DirectionalCallers" => (r.DirectionalA1?.DirectionalAccuracyScore, r.DirectionalB?.DirectionalAccuracyScore) switch
                {
                    var (a, b) when a > b => "A1 точнее по In-Degree",
                    var (a, b) when a < b => "B точнее по In-Degree",
                    _ => "Одинаковая точность"
                },
                "DistractorRefusal" => (r.RefusalA1?.ActuallyRefused, r.RefusalB?.ActuallyRefused) switch
                {
                    (true, true) => "Оба честно отказались",
                    (true, false) => "A1 изолировал (B выдумал)",
                    (false, true) => "B изолировал (A1 выдумал)",
                    _ => "Оба галлюцинировали"
                },
                _ => r.PvrA1.Pvr >= r.PvrB.Pvr ? "A1 чище топологически" : "B точнее в прозе"
            };

            Console.WriteLine($"{r.Category,-17} | {r.LatencyMsA0,8}мс | {r.LatencyMsA1,8}мс | {r.LatencyMsB,8}мс | {r.PvrA1.Pvr * 100,10:F1}% | {r.PvrB.Pvr * 100,10:F1}% | {verdict}");
        }

        int count = reports.Count;
        int totalEntityHallucinationsA1 = reports.Sum(r => r.PvrA1.HallucinatedNodes.Count);
        int totalEntityHallucinationsB = reports.Sum(r => r.PvrB.HallucinatedNodes.Count);
        int totalRelationHallucinationsA1 = reports.Sum(r => r.PvrA1.InvalidTransitions);
        int totalRelationHallucinationsB = reports.Sum(r => r.PvrB.InvalidTransitions);

        var traceReports = reports.Where(r => !r.Question.Contains("CrossAttention") && !r.Category.Contains("Overview")).ToList();
        float avgTracePvrA1 = traceReports.Count > 0 ? traceReports.Average(r => r.PvrA1.Pvr) : 0f;
        float avgTracePvrB = traceReports.Count > 0 ? traceReports.Average(r => r.PvrB.Pvr) : 0f;

        Console.WriteLine("---------------------------------------------------------------------------------------------------------");
        Console.WriteLine($"СРЕДНЕЕ (ТРАССИРОВКА)| {sumLatA0 / count,8}мс | {sumLatA1 / count,8}мс | {sumLatB / count,8}мс | {avgTracePvrA1 * 100,10:F1}% | {avgTracePvrB * 100,10:F1}% | A0 в {(float)sumLatB / Math.Max(1, sumLatA0):F1}x быстрее RAG");
        Console.WriteLine($"ГАЛЛЮЦИНАЦИИ СУЩНОСТЕЙ (НЕ СУЩЕСТВУЮТ В КОДЕ): A1 = {totalEntityHallucinationsA1} шт. | Vector RAG B = {totalEntityHallucinationsB} шт.");
        Console.WriteLine($"ГАЛЛЮЦИНАЦИИ СВЯЗЕЙ (ЛОЖНЫЕ РЁБРА В АСТ):     A1 = {totalRelationHallucinationsA1} шт. | Vector RAG B = {totalRelationHallucinationsB} шт.");
        Console.WriteLine("=========================================================================================================");
    }
}