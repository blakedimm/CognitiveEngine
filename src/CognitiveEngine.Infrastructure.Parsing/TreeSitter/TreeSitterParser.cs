namespace CognitiveEngine.Infrastructure.Parsing.TreeSitter;

using System.Text.RegularExpressions;

public sealed record AstFunctionBlock(
    string Name,
    string Signature,
    IReadOnlyList<string> CallTokens,
    IReadOnlyList<string> BranchConditions,
    IReadOnlyList<string> HandledExceptions,
    string? ParentClass = null,
    string SourceCode = ""
);

public static partial class TreeSitterParser
{
    [GeneratedRegex(@"^[ \t]*class\s+([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.Compiled)]
    private static partial Regex ClassDefRegex();

    [GeneratedRegex(@"^[ \t]*(?:async\s+)?def\s+([a-zA-Z_][a-zA-Z0-9_]*)\s*\(([\s\S]*?)\)(?:\s*->\s*[^:]+)?\s*:", RegexOptions.Compiled)]
    private static partial Regex SingleLineDefRegex();

    [GeneratedRegex(@"^[ \t]*(?:async\s+)?def\s+([a-zA-Z_][a-zA-Z0-9_]*)\s*\(", RegexOptions.Compiled)]
    private static partial Regex MultiLineDefStartRegex();

    // Захватывает только чистые латинские вызовы программного кода
    [GeneratedRegex(@"\b([a-zA-Z_][a-zA-Z0-9_]{1,})\s*\(", RegexOptions.Compiled)]
    private static partial Regex CallTokenRegex();

    [GeneratedRegex(@"\b(if|elif|while)\s+(.+?):", RegexOptions.Compiled)]
    private static partial Regex BranchRegex();

    [GeneratedRegex(@"except(?:\s+([a-zA-Z_][a-zA-Z0-9_]*))?", RegexOptions.Compiled)]
    private static partial Regex ExceptionRegex();

    public static IReadOnlyList<AstFunctionBlock> ParseModuleFunctions(string sourceCode)
    {
        var result = new List<AstFunctionBlock>();
        if (string.IsNullOrWhiteSpace(sourceCode)) return result;

        var lines = sourceCode.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        bool inTripleDouble = false;
        bool inTripleSingle = false;

        string? currentClass = null;
        int currentClassIndent = -1;

        string? pendingFuncName = null;
        int pendingFuncIndent = -1;
        var pendingSignature = new System.Text.StringBuilder();
        var pendingBody = new System.Text.StringBuilder();

        void FlushPendingFunction()
        {
            if (pendingFuncName == null) return;

            string bodyText = pendingBody.ToString();
            var rawCalls = CallTokenRegex().Matches(bodyText);
            var callTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match cm in rawCalls)
            {
                string token = cm.Groups[1].Value;
                // Строгий фильтр: только ASCII, без кириллицы и без служебных слов
                if (IsStrictAsciiIdentifier(token) &&
                    !token.Equals(pendingFuncName, StringComparison.OrdinalIgnoreCase) &&
                    !IsReservedKeyword(token))
                {
                    callTokens.Add(token);
                }
            }

            var branches = BranchRegex().Matches(bodyText)
                .Select(b => Regex.Replace(b.Groups[2].Value, @"\s+", " ").Trim())
                .Take(6)
                .ToList();

            var exceptions = ExceptionRegex().Matches(bodyText)
                .Select(e => e.Groups[1].Success ? e.Groups[1].Value.Trim() : "Exception")
                .ToList();

            result.Add(new AstFunctionBlock(
                Name: pendingFuncName,
                Signature: Regex.Replace(pendingSignature.ToString(), @"\s+", " ").Trim(),
                CallTokens: callTokens.ToList(),
                BranchConditions: branches,
                HandledExceptions: exceptions,
                ParentClass: currentClass,
                SourceCode: bodyText
            ));

            pendingFuncName = null;
            pendingFuncIndent = -1;
            pendingSignature.Clear();
            pendingBody.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            // Полная изоляция многострочных docstrings: строки docstring НЕ попадают в pendingBody
            int countTripleDouble = CountOccurrences(line, "\"\"\"");
            if (countTripleDouble % 2 != 0) inTripleDouble = !inTripleDouble;

            int countTripleSingle = CountOccurrences(line, "'''");
            if (countTripleSingle % 2 != 0) inTripleSingle = !inTripleSingle;

            if (inTripleDouble || inTripleSingle || trimmed.StartsWith("\"\"\"") || trimmed.StartsWith("'''"))
            {
                continue;
            }

            // Исключаем комментарии
            if (trimmed.StartsWith('#')) continue;

            int indent = line.TakeWhile(char.IsWhiteSpace).Count();

            if (currentClass != null && indent <= currentClassIndent && !string.IsNullOrWhiteSpace(trimmed))
            {
                currentClass = null;
                currentClassIndent = -1;
            }

            var classMatch = ClassDefRegex().Match(line);
            if (classMatch.Success)
            {
                FlushPendingFunction();
                currentClass = classMatch.Groups[1].Value;
                currentClassIndent = indent;
                continue;
            }

            var singleMatch = SingleLineDefRegex().Match(line);
            if (singleMatch.Success)
            {
                FlushPendingFunction();
                pendingFuncName = singleMatch.Groups[1].Value;
                pendingFuncIndent = indent;
                pendingSignature.Append(singleMatch.Groups[2].Value);
                continue;
            }

            var multiStart = MultiLineDefStartRegex().Match(line);
            if (multiStart.Success)
            {
                FlushPendingFunction();
                pendingFuncName = multiStart.Groups[1].Value;
                pendingFuncIndent = indent;

                while (i < lines.Length && !lines[i].Contains("):") && !lines[i].Contains("->"))
                {
                    pendingSignature.Append(lines[i]).Append(' ');
                    i++;
                }
                if (i < lines.Length) pendingSignature.Append(lines[i]);
                continue;
            }

            if (pendingFuncName != null)
            {
                if (!string.IsNullOrWhiteSpace(trimmed) && indent <= pendingFuncIndent)
                {
                    FlushPendingFunction();
                }
                else
                {
                    // Вырезаем строковые литералы и хвостовые комментарии из кода строки
                    string cleanLine = StripStringsAndComments(line);
                    pendingBody.AppendLine(cleanLine);
                }
            }
        }

        FlushPendingFunction();
        return result;
    }

    private static string StripStringsAndComments(string line)
    {
        int commentIdx = line.IndexOf('#');
        string code = commentIdx >= 0 ? line[..commentIdx] : line;
        // Замена строковых литералов на пробелы
        return Regex.Replace(code, @"(""[^""]*""|'[^']*')", " \"\" ");
    }

    private static bool IsStrictAsciiIdentifier(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        foreach (char c in token)
        {
            bool isAscii = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
            if (!isAscii) return false;
        }
        return true;
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static bool IsReservedKeyword(string token) => token.ToLowerInvariant() is
        "if" or "while" or "for" or "return" or "def" or "class" or
        "import" or "from" or "with" or "try" or "except" or "finally" or
        "print" or "len" or "range" or "str" or "int" or "float" or "set" or "dict" or "list" or "super" or "async" or "await";
}