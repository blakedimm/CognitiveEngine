namespace CognitiveEngine.Infrastructure.ML.Clients;

using System.Net.Http.Json;
using System.Text.Json.Serialization;

public sealed record EmbeddingRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] IReadOnlyList<string> Input
);

public sealed record EmbeddingItem(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("embedding")] float[] Embedding
);

public sealed record EmbeddingResponse(
    [property: JsonPropertyName("data")] List<EmbeddingItem>? Data,
    [property: JsonPropertyName("error")] string? Error
);

/// <summary>
/// Клиент BGE-M3 (1024-dim) с поддержкой безопасного батчинга,
/// строгим разделением пространств search_document/search_query и защитой от OOM.
/// </summary>
public sealed class EmbeddingClient
{
    private readonly HttpClient _httpClient;
    private readonly string _modelName;
    private readonly SemaphoreSlim _concurrencyThrottler = new(2, 2);

    private const int SafeBatchSize = 16;
    private const int EmbeddingDimension = 1024;

    public EmbeddingClient(
        HttpClient httpClient,
        string modelName = "text-embedding-baai-bge-m3-568m")
    {
        _httpClient = httpClient;
        _modelName = modelName;
    }

    /// <summary>
    /// Одиночный расчёт эмбеддинга для поискового запроса RouterAgent (search_query).
    /// </summary>
    public async Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var batch = await GenerateBatchAsync([text], isQuery: true, ct);
        return batch.Count > 0 ? batch[0] : new float[EmbeddingDimension];
    }

    /// <summary>
    /// Батчевый расчёт эмбеддингов с автоматическим чанкованием.
    /// По умолчанию маркирует тексты как search_document (для наполнения базы знаний).
    /// </summary>
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        return await GenerateBatchAsync(texts, isQuery: false, ct);
    }

    /// <summary>
    /// Полный метод с явным указанием пространства эмбеддингов.
    /// </summary>
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateBatchAsync(
        IReadOnlyList<string> texts,
        bool isQuery,
        CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];

        var results = new List<ReadOnlyMemory<float>>(texts.Count);
        string prefix = isQuery ? "search_query: " : "search_document: ";

        await _concurrencyThrottler.WaitAsync(ct);
        try
        {
            // Дробим массив на безопасные порции по 16 элементов, чтобы не перегружать VRAM
            for (int i = 0; i < texts.Count; i += SafeBatchSize)
            {
                var chunk = texts.Skip(i).Take(SafeBatchSize)
                    .Select(t => $"{prefix}{t.Trim()}")
                    .ToList();

                var chunkResults = await ProcessChunkWithRetryAsync(chunk, ct);
                results.AddRange(chunkResults);
            }

            return results;
        }
        finally
        {
            _concurrencyThrottler.Release();
        }
    }

    private async Task<IReadOnlyList<ReadOnlyMemory<float>>> ProcessChunkWithRetryAsync(
        List<string> chunk,
        CancellationToken ct)
    {
        int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var payload = new EmbeddingRequest(_modelName, chunk);
                using var response = await _httpClient.PostAsJsonAsync("/v1/embeddings", payload, ct);
                response.EnsureSuccessStatusCode();

                var data = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: ct);

                if (data?.Data != null && data.Data.Count > 0)
                {
                    return data.Data
                        .OrderBy(d => d.Index)
                        .Select(d => (ReadOnlyMemory<float>)d.Embedding)
                        .ToList();
                }

                if (!string.IsNullOrEmpty(data?.Error))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n[Embedding Error] Сервер вернул ошибку: {data.Error}");
                    Console.ResetColor();
                }
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[Embedding Warning] Сбой батча ({ex.Message}). Повтор попытки через 500 мс...");
                Console.ResetColor();
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[Embedding Critical] Не удалось получить эмбеддинги батча: {ex.Message}");
                Console.ResetColor();
                break;
            }
        }

        // Если все попытки исчерпаны — возвращаем нулевые векторы строго с логированием
        return chunk.Select(_ => (ReadOnlyMemory<float>)new float[EmbeddingDimension]).ToList();
    }
}