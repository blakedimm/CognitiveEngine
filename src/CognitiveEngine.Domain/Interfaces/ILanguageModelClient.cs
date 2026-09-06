namespace CognitiveEngine.Domain.Interfaces;

public interface ILanguageModelClient
{
    Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken ct = default);

    Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateBatchEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
    
    Task<string> GenerateCompletionAsync(
        string systemPrompt,
        string userPrompt,
        float temperature = 0.1f,
        int maxTokens = 1000,
        CancellationToken ct = default);

    IAsyncEnumerable<string> StreamCompletionAsync(
        string systemPrompt,
        string userPrompt,
        float temperature = 0.1f,
        int maxTokens = 1500,
        CancellationToken ct = default);
}