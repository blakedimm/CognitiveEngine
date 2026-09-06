namespace CognitiveEngine.Domain.Enums;

/// <summary>
/// Типология связей (рёбер) онтологического графа.
/// </summary>
public enum RelationType : byte
{
    Associated = 0,
    Calls = 1,
    Imports = 2,
    Inherits = 3,
    Implements = 4,
    Defines = 5,
    Contains = 6,
    Mutates = 7,
    ExecTrace = 8,
    Causes = 9,
    Triggers = 10,
    Inhibits = 11,
    Transforms = 12,
    Contradicts = 13,
    Precedes = 14
}