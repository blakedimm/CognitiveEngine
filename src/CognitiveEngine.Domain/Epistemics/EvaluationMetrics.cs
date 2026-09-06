namespace CognitiveEngine.Domain.Epistemics;

using CognitiveEngine.Domain.Enums;

/// <summary>
/// Извлечённый переход между двумя узлами, заявленный моделью в ответе.
/// </summary>
public sealed record ClaimedTransition(
    string Source,
    string Target,
    string? StatedRelation = null,
    bool ExistsInAst = false,
    RelationType? ActualRelation = null,
    float ActualWeight = 0f
);

/// <summary>
/// Метрика Path Validity Ratio (PVR): процент заявленных моделью связей,
/// которые физически подтверждены в графе AST.
/// </summary>
public sealed record PathValidityMetrics(
    int TotalClaimedTransitions,
    int ValidTransitions,
    int InvalidTransitions,
    float Pvr, // ValidTransitions / Max(1, TotalClaimedTransitions)
    IReadOnlyList<ClaimedTransition> Transitions,
    IReadOnlyList<string> HallucinatedNodes
);

/// <summary>
/// Результат оценки многозвенной транзитивности (Multi-Hop Reasoning, >= 3 хопов).
/// </summary>
public sealed record MultiHopEvaluationResult(
    string StartNode,
    string TargetNode,
    int RequiredMinHops,
    bool PathFound,
    int ActualHops,
    IReadOnlyList<string> Trajectory,
    float HopCompletenessRatio
);

/// <summary>
/// Оценка направленности вызовов (In-Degree vs Out-Degree).
/// Проверяет, не путает ли модель "кто вызывает X" и "кого вызывает X".
/// </summary>
public sealed record DirectionalAccuracyResult(
    string SubjectNode,
    bool IsCallerQuery, // true: запрос входящих (кто вызывает), false: запрос исходящих (что вызывает)
    int CorrectDirectionCount,
    int InvertedDirectionCount,
    float DirectionalAccuracyScore
);

/// <summary>
/// Оценка детекции изоляции (Refusal Accuracy / Distractor Test).
/// Проверяет, заявляет ли модель честное "связи нет" вместо выдумывания моста между независимыми узлами.
/// </summary>
public sealed record RefusalEvaluationResult(
    bool ShouldRefuse,
    bool ActuallyRefused,
    float RefusalConfidence,
    string ReasoningSnippet
);

/// <summary>
/// Комплексный отчёт сравнительного теста для одного тестового сценария.
/// </summary>
public sealed record BenchmarkScenarioReport(
    string Question,
    string Category, // "MultiHop", "DirectionalCallers", "DistractorRefusal", "Overview"
    PathValidityMetrics PvrA1,
    PathValidityMetrics PvrB,
    long LatencyMsA0,
    long LatencyMsA1,
    long LatencyMsB,
    float FsrA1,
    float EntropyCollapseA1,
    MultiHopEvaluationResult? MultiHopA1 = null,
    MultiHopEvaluationResult? MultiHopB = null,
    DirectionalAccuracyResult? DirectionalA1 = null,
    DirectionalAccuracyResult? DirectionalB = null,
    RefusalEvaluationResult? RefusalA1 = null,
    RefusalEvaluationResult? RefusalB = null
);