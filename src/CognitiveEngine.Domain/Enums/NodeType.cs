namespace CognitiveEngine.Domain.Enums;

/// <summary>
/// Категория узла в архитектурно-семантическом графе.
/// </summary>
public enum NodeType : byte
{
    Unknown = 0,
    Module = 1,
    Class = 2,
    Function = 3,
    Argument = 4,
    Concept = 5,
    AbstractCluster = 6
}