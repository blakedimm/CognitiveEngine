namespace CognitiveEngine.Infrastructure.ML.Clients;

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CognitiveEngine.Domain.Interfaces;

public sealed class LocalLlmClient : ILanguageModelClient
{
    private readonly HttpClient _httpClient;
    private readonly EmbeddingClient _embeddingClient;
    private readonly string _modelId;

    public LocalLlmClient(HttpClient httpClient, EmbeddingClient embeddingClient, string modelId = "qwen2.5-coder-14b")
    {
        _httpClient = httpClient;
        _embeddingClient = embeddingClient;
        _modelId = modelId;
    }

    public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
        => _embeddingClient.GenerateEmbeddingAsync(text, ct);

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateBatchEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        => _embeddingClient.GenerateBatchAsync(texts, isQuery: false, ct);

    public async Task<string> GenerateCompletionAsync(
        string systemPrompt,
        string userPrompt,
        float temperature = 0.1f,
        int maxTokens = 1000,
        CancellationToken ct = default)
    {
        var payload = new
        {
            model = _modelId,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = temperature,
            max_tokens = maxTokens,
            stream = false
        };

        using var response = await _httpClient.PostAsJsonAsync("/v1/chat/completions", payload, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    public async IAsyncEnumerable<string> StreamCompletionAsync(
        string systemPrompt,
        string userPrompt,
        float temperature = 0.1f,
        int maxTokens = 1500,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = new
        {
            model = _modelId,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = temperature,
            max_tokens = maxTokens,
            stream = true
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(payload)
        };

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("data: [DONE]")) break;
            if (!line.StartsWith("data: ")) continue;

            string json = line["data: ".Length..];
            string? delta = null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() > 0)
                {
                    var deltaElem = choices[0].GetProperty("delta");
                    if (deltaElem.TryGetProperty("content", out var contentElem))
                    {
                        delta = contentElem.GetString();
                    }
                }
            }
            catch (JsonException) { }

            if (!string.IsNullOrEmpty(delta))
            {
                yield return delta;
            }
        }
    }
}