namespace CognitiveEngine.Domain.Models;

using CognitiveEngine.Domain.Enums;

/// <summary>
/// Направленная типизированная связь между узлами графа.
/// </summary>
public sealed record GraphEdge(
    Guid Id,
    string Source,
    string Target,
    RelationType Relation,
    EpistemicClass Epistemic = EpistemicClass.Soft,
    float Weight = 1.0f,
    int Valence = 0,
    int Activations = 0,
    int Confirmations = 0,
    float SourceTrust = 1.0f
);