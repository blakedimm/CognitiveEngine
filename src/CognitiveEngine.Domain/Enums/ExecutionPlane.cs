namespace CognitiveEngine.Domain.Enums;

/// <summary>
/// Режим когнитивной обработки запроса.
/// </summary>
public enum ExecutionPlane : byte
{
    /// <summary>
    /// Быстрый структурный вывод без разворачивания дерева поиска (System 1).
    /// </summary>
    System1_Intuition = 0,

    /// <summary>
    /// Медленный делиберативный MCTS-поиск и доказательство (System 2).
    /// </summary>
    System2_Deliberation = 1
}