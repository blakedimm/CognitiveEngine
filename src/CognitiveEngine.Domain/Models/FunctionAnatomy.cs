namespace CognitiveEngine.Domain.Models;

/// <summary>
/// Структурный анатомический паспорт функции, извлечённый через AST-анализ.
/// </summary>
public sealed record FunctionAnatomy(
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Calls,
    IReadOnlyList<string> Branches,
    IReadOnlyList<string> Exceptions,
    IReadOnlyList<string> SqlSinks
);