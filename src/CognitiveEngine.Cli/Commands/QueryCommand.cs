namespace CognitiveEngine.Cli.Commands;

using System.Diagnostics;
using CognitiveEngine.Runtime;

public sealed class QueryCommand
{
    private readonly SemanticCore _core;

    public QueryCommand(SemanticCore core)
    {
        _core = core;
    }

    public async Task ExecuteAsync(string? initialQuery = null, CancellationToken ct = default)
    {
        Console.WriteLine("=================================================================");
        Console.WriteLine("🧠 GWM / ECS CORE: Интерактивный терминал когнитивного рантайма");
        Console.WriteLine("Команды: 'exit' или 'quit' для выхода, 'stats' для сводки телеметрии.");
        Console.WriteLine("=================================================================\n");

        if (!string.IsNullOrWhiteSpace(initialQuery))
        {
            await ProcessSingleQueryAsync(initialQuery, ct);
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("Query > ");
            Console.ResetColor();

            string? line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) continue;

            string cmd = line.Trim();
            if (cmd.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                cmd.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

            if (cmd.Equals("stats", StringComparison.OrdinalIgnoreCase))
            {
                var (avgFsr, avgEntropy, avgLatency) = _core.Telemetry.GetAggregateMetrics();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"\n[Telemetry Snapshot]");
                Console.WriteLine($"  ├─ Скользящее среднее FSR: {avgFsr * 100:F1}%");
                Console.WriteLine($"  ├─ Коллапс энтропии: {avgEntropy * 100:F1}%");
                Console.WriteLine($"  └─ Средняя задержка сессии: {avgLatency:F0} мс\n");
                Console.ResetColor();
                continue;
            }

            await ProcessSingleQueryAsync(cmd, ct);
        }
    }

    private async Task ProcessSingleQueryAsync(string question, CancellationToken ct)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  [1/3] Построение подграфа в ОЗУ...");
        Console.WriteLine("  [2/3] Запуск встречного MCTS и турнира гипотез...");
        Console.ResetColor();

        var sw = Stopwatch.StartNew();

        try
        {
            var result = await _core.QueryAsync(question, ct);
            sw.Stop();

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Контур доказательства сошёлся за {sw.ElapsedMilliseconds} мс");
            Console.ResetColor();

            Console.WriteLine("\n" + result.FinalAnswer);

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("\n-----------------------------------------------------------------");
            Console.WriteLine($"[Верифицировано связей: {result.VerifiedClaims.Count} | FSR: {result.Metrics.Fsr * 100:F1}% | Энтропия: {result.Metrics.EntropyCollapse * 100:F1}%]");
            Console.WriteLine("-----------------------------------------------------------------\n");
            Console.ResetColor();
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n[Запрос прерван]");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[Ошибка исполнения]: {ex.Message}");
            Console.ResetColor();
        }
    }
}