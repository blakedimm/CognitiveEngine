namespace CognitiveEngine.Domain.Interfaces;

/// <summary>
/// Контракт нейросетевого скоринга вероятностей связей графа.
/// </summary>
public interface IGnnPredictor
{
    bool IsActive { get; }
    Task<float> PredictLinkProbabilityAsync(string sourceId, string targetId, CancellationToken ct = default);
}