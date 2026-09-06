namespace CognitiveEngine.Domain.Enums;

/// <summary>
/// Уровень эпистемической стабильности связи в графе знаний.
/// </summary>
public enum EpistemicClass : byte
{
    /// <summary>
    /// Детерминированный факт (синтаксис AST, проверенный код). Не угасает со временем.
    /// </summary>
    Hard = 0,

    /// <summary>
    /// Вероятностное предположение или ассоциация. Подвержено затуханию (annealing).
    /// </summary>
    Soft = 1,

    /// <summary>
    /// Мета-связь о топологической структуре и взаимосвязи гипотез.
    /// </summary>
    Meta = 2
}