namespace CognitiveEngine.Cli.Commands;

using System.Diagnostics;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Domain.Models;
using CognitiveEngine.Infrastructure.Parsing.TreeSitter;
using CognitiveEngine.Infrastructure.Storage.Graph;
using CognitiveEngine.Infrastructure.Storage.Persistence;

/// <summary>
/// CLI-команда пакетного индексирования кодовой базы:
/// рекурсивный сбор сигнатур с контекстным разрешением коллизий,
/// извлечение глубокой анатомии (AST) и атомарная фиксация снимка на диск.
/// </summary>
public sealed class IngestCommand
{
    private readonly InMemoryGraphStore _graphStore;
    private readonly VectorDatabaseAdapter _dbAdapter;
    private readonly AstCallGraphVisitor _astVisitor;

    public IngestCommand(
        InMemoryGraphStore graphStore,
        VectorDatabaseAdapter dbAdapter,
        AstCallGraphVisitor astVisitor)
    {
        _graphStore = graphStore;
        _dbAdapter = dbAdapter;
        _astVisitor = astVisitor;
    }

    public async Task ExecuteAsync(string targetDirectory, CancellationToken ct = default)
    {
        if (!Directory.Exists(targetDirectory))
        {
            Console.WriteLine($"[Ошибка] Каталог не найден: {targetDirectory}");
            return;
        }

        Console.WriteLine("=================================================================");
        Console.WriteLine($"⚡ БАТЧЕВЫЙ АСТ-ИНЖЕСТОР: Сканирование каталога {targetDirectory}");
        Console.WriteLine("=================================================================");

        var sw = Stopwatch.StartNew();

        var codeFiles = Directory.GetFiles(targetDirectory, "*.py", SearchOption.AllDirectories)
            .Where(p => !p.Contains(".venv") && !p.Contains("venv") && !p.Contains("__pycache__"))
            .ToList();

        Console.WriteLine($"🔍 Найдено файлов для анализа: {codeFiles.Count}");

        // Проход 1: Сбор глобальной многоуровневой карты сигнатур функций
        var globalFunctionLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in codeFiles)
        {
            string moduleName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            string source = await File.ReadAllTextAsync(file, ct);
            var blocks = TreeSitterParser.ParseModuleFunctions(source);

            foreach (var block in blocks)
            {
                string funcName = block.Name.ToLowerInvariant();
                string funcNodeId = !string.IsNullOrEmpty(block.ParentClass)
                    ? $"func_{moduleName}_{block.ParentClass.ToLowerInvariant()}_{funcName}"
                    : $"func_{moduleName}_{funcName}";

                // 1. Полная квалификация: module.class.func или module.func
                if (!string.IsNullOrEmpty(block.ParentClass))
                {
                    string clsName = block.ParentClass.ToLowerInvariant();
                    globalFunctionLookup[$"{moduleName}.{clsName}.{funcName}"] = funcNodeId;
                    globalFunctionLookup[$"{clsName}.{funcName}"] = funcNodeId;
                }
                globalFunctionLookup[$"{moduleName}.{funcName}"] = funcNodeId;

                // 2. Короткое имя регистрируем только если нет коллизий
                if (!ambiguousFunctions.Contains(funcName))
                {
                    if (globalFunctionLookup.ContainsKey(funcName))
                    {
                        // Обнаружена коллизия (например, несколько __init__ или run)
                        globalFunctionLookup.Remove(funcName);
                        ambiguousFunctions.Add(funcName);
                    }
                    else
                    {
                        globalFunctionLookup[funcName] = funcNodeId;
                    }
                }
            }
        }

        Console.WriteLine($"🗺️ Глобальная картография завершена: зарегистрировано {globalFunctionLookup.Count} однозначных маршрутов вызовов.");

        // Проход 2: Глубокое извлечение узлов, рёбер и пакетный расчёт векторных паспортов
        int totalNodes = 0;
        int totalEdges = 0;

        foreach (var file in codeFiles)
        {
            string fileName = Path.GetFileName(file);
            Console.WriteLine($"  -> Анализ структуры: {fileName}...");

            string source = await File.ReadAllTextAsync(file, ct);
            var result = await _astVisitor.ProcessModuleAsync(file, source, globalFunctionLookup, ct);

            await _graphStore.AddNodesBatchAsync(result.Nodes, ct);
            await _graphStore.AddEdgesBatchAsync(result.Edges, ct);

            totalNodes += result.Nodes.Count;
            totalEdges += result.Edges.Count;
        }

        // Кристаллизация снимка на диск
        Console.WriteLine("\n💾 Сохранение снимка графа на диск через VectorDatabaseAdapter...");
        await _dbAdapter.PersistSnapshotAsync(_graphStore, ct);

        sw.Stop();
        Console.WriteLine("=================================================================");
        Console.WriteLine($"✅ Индексация завершена успешно за {sw.Elapsed.TotalSeconds:F2} сек.");
        Console.WriteLine($"📊 Зафиксировано узлов: {totalNodes} | рёбер: {totalEdges}");
        Console.WriteLine("=================================================================");
    }
}