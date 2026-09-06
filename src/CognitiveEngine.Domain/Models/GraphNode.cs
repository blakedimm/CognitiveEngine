namespace CognitiveEngine.Domain.Models;

using CognitiveEngine.Domain.Enums;

/// <summary>
/// Неизменяемый узел топологического графа знаний.
/// </summary>
public sealed record GraphNode(
    string Id,
    string Content,
    NodeType Type,
    string Epoch = "",
    bool IsFrozen = false,
    ReadOnlyMemory<float> Vector = default,
    float StructuralPressure = 0.0f
);