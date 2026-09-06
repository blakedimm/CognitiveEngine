namespace CognitiveEngine.Domain.Epistemics;

/// <summary>
/// Агрегированные метрики надежности и энтропии рассуждения контура ECS.
/// </summary>
public readonly record struct EpistemicMetrics(
    float Fsr,
    float ShannonEntropy,
    float EntropyCollapse,
    float StabilityScore
);