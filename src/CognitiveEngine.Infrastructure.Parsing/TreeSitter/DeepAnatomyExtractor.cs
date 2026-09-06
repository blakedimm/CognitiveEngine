namespace CognitiveEngine.Infrastructure.Parsing.TreeSitter;

using System.Text.RegularExpressions;
using CognitiveEngine.Domain.Models;

/// <summary>
/// Синтаксический анализатор глубокой анатомии функций (Deep Code Anatomy).
/// Извлекает входные аргументы, ветвления, guard-условия, генерируемые исключения
/// и SQL-синки (SELECT, UPDATE, INSERT, DELETE) для создания функционального паспорта узла.
/// </summary>
public sealed partial class DeepAnatomyExtractor
{
    [GeneratedRegex(@"def\s+\w+\s*\(([^)]*)\)", RegexOptions.Compiled)]
    private static partial Regex ArgsRegex();

    [GeneratedRegex(@"(\w+)\s*\(", RegexOptions.Compiled)]
    private static partial Regex CallRegex();

    [GeneratedRegex(@"if\s+(.+?):", RegexOptions.Compiled)]
    private static partial Regex BranchRegex();

    [GeneratedRegex(@"raise\s+([A-Za-z0-9_]+(?:\([^)]*\))?)", RegexOptions.Compiled)]
    private static partial Regex RaiseRegex();

    [GeneratedRegex(@"[""']\s*(SELECT|INSERT|UPDATE|DELETE)\s+[\s\S]+?[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SqlSinkRegex();

    public static FunctionAnatomy Extract(string functionSource)
    {
        var inputs = new List<string>();
        var calls = new List<string>();
        var branches = new List<string>();
        var exceptions = new List<string>();
        var sinks = new List<string>();

        // 1. Извлечение входных аргументов
        var argsMatch = ArgsRegex().Match(functionSource);
        if (argsMatch.Success)
        {
            var rawArgs = argsMatch.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var arg in rawArgs)
            {
                string cleanArg = arg.Split(':', '=')[0].Trim();
                if (!string.IsNullOrEmpty(cleanArg) && cleanArg != "self" && cleanArg != "cls")
                {
                    inputs.Add(cleanArg);
                }
            }
        }

        // 2. Извлечение статических вызовов
        foreach (Match match in CallRegex().Matches(functionSource))
        {
            string callName = match.Groups[1].Value;
            if (callName is not ("def" or "if" or "while" or "for" or "return" or "with") && !calls.Contains(callName))
            {
                calls.Add(callName);
            }
        }

        // 3. Извлечение веток ветвления (Guard conditions)
        foreach (Match match in BranchRegex().Matches(functionSource))
        {
            branches.Add($"if {match.Groups[1].Value.Trim()}");
        }

        // 4. Извлечение генерируемых исключений
        foreach (Match match in RaiseRegex().Matches(functionSource))
        {
            exceptions.Add(match.Groups[1].Value.Trim());
        }

        // 5. Поиск SQL-силков в строковых литералах
        foreach (Match match in SqlSinkRegex().Matches(functionSource))
        {
            string cleanSql = Regex.Replace(match.Value.Trim('"', '\''), @"\s+", " ").Trim();
            sinks.Add(cleanSql);
        }

        return new FunctionAnatomy(inputs, calls, branches, exceptions, sinks);
    }
}