namespace CognitiveEngine.Cli;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CognitiveEngine.Cli.Commands;
using CognitiveEngine.Domain.Interfaces;
using CognitiveEngine.Infrastructure.ML.Clients;
using CognitiveEngine.Infrastructure.Parsing.TreeSitter;
using CognitiveEngine.Infrastructure.Storage.Graph;
using CognitiveEngine.Infrastructure.Storage.Persistence;
using CognitiveEngine.Runtime;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        var services = new ServiceCollection();
        ConfigureServices(services, configuration);

        await using var serviceProvider = services.BuildServiceProvider();

        var core = serviceProvider.GetRequiredService<SemanticCore>();
        var dbAdapter = serviceProvider.GetRequiredService<VectorDatabaseAdapter>();
        var graphStore = serviceProvider.GetRequiredService<InMemoryGraphStore>();

        // Восстановление снимка топологии с диска или начальное семенирование ядра
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("[Инициализация] Загрузка топологического графа из дискового хранилища...");
        Console.ResetColor();

        await dbAdapter.LoadSnapshotAsync(graphStore);
        var existingEdges = await graphStore.GetAllEdgesAsync();

        if (existingEdges.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("[Семенирование] База пуста. Прошивка базовой архитектуры SemanticCore...");
            Console.ResetColor();
            await core.SeedCoreArchitectureAsync();
        }

        // Маршрутизация CLI-команд
        if (args.Length > 0)
        {
            string command = args[0].ToLowerInvariant();
            switch (command)
            {
                case "ingest" when args.Length > 1:
                {
                    var ingestCmd = serviceProvider.GetRequiredService<IngestCommand>();
                    await ingestCmd.ExecuteAsync(args[1]);
                    return 0;
                }
                case "ingest":
                {
                    Console.WriteLine("Использование: CognitiveEngine.Cli ingest <путь_к_папке_проекта>");
                    return 1;
                }
                case "benchmark":
                {
                    var benchCmd = serviceProvider.GetRequiredService<BenchmarkCommand>();
                    await benchCmd.ExecuteAsync();
                    return 0;
                }
                case "query":
                {
                    var queryCmd = serviceProvider.GetRequiredService<QueryCommand>();
                    string queryText = string.Join(" ", args.Skip(1));
                    await queryCmd.ExecuteAsync(queryText);
                    return 0;
                }
            }
        }

        // Интерактивный REPL по умолчанию
        var defaultQueryCmd = serviceProvider.GetRequiredService<QueryCommand>();
        await defaultQueryCmd.ExecuteAsync();
        return 0;
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        string llmBaseUrl = configuration["CognitiveEngine:Llm:BaseUrl"] ?? "http://localhost:1234";
        string llmModel = configuration["CognitiveEngine:Llm:ModelId"] ?? "qwen2.5-coder-14b";
        string embedModel = configuration["CognitiveEngine:Llm:EmbeddingModelId"] ?? "text-embedding-baai-bge-m3-568m";
        string storageDir = configuration["CognitiveEngine:StorageDirectory"] ?? "./lance_store_v10";
        string gnnPredictorPath = configuration["CognitiveEngine:GnnPredictorPath"] ?? "gnn_predictor.onnx";

        // Сетевой клиент к локальной LLM / Embedder
        services.AddHttpClient("LlmClient", client =>
        {
            client.BaseAddress = new Uri(llmBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(120);
        });

        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient("LlmClient");
            return new EmbeddingClient(httpClient, embedModel);
        });

        services.AddSingleton<ILanguageModelClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient("LlmClient");
            var embeddingClient = sp.GetRequiredService<EmbeddingClient>();
            return new LocalLlmClient(httpClient, embeddingClient, llmModel);
        });

        // Хранилище графа и дисковый адаптер
        services.AddSingleton<InMemoryGraphStore>();
        services.AddSingleton<IGraphStorage>(sp => sp.GetRequiredService<InMemoryGraphStore>());
        services.AddSingleton(new VectorDatabaseAdapter(storageDir));

        // AST-парсер
        services.AddSingleton<AstCallGraphVisitor>();

        // Оркестратор SemanticCore
        services.AddSingleton(sp =>
        {
            var storage = sp.GetRequiredService<IGraphStorage>();
            var llmClient = sp.GetRequiredService<ILanguageModelClient>();
            return new SemanticCore(storage, llmClient, gnnPredictorPath);
        });

        // CLI-команды
        services.AddTransient<IngestCommand>();
        services.AddTransient<QueryCommand>();
        services.AddTransient<BenchmarkCommand>();
    }
}