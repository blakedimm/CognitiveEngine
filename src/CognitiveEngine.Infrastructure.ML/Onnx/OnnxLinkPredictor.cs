namespace CognitiveEngine.Infrastructure.ML.Onnx;

using CognitiveEngine.Domain.Interfaces;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

/// <summary>
/// Билинейный многоголовый предиктор вызовов (LinkPredictor v11.0).
/// Оценивает вероятность существования архитектурного вызова или зависимости между компонентами.
/// </summary>
public sealed class OnnxLinkPredictor : IGnnPredictor, IDisposable
{
    private readonly InferenceSession? _session;
    private readonly Dictionary<string, int> _nodeIndexMap = new();

    public bool IsActive => _session != null;

    public OnnxLinkPredictor(string modelPath = "gnn_predictor.onnx")
    {
        if (File.Exists(modelPath))
        {
            var sessionOptions = new SessionOptions();
            sessionOptions.AppendExecutionProvider_CPU();
            _session = new InferenceSession(modelPath, sessionOptions);
        }
    }

    public void UpdateNodeMapping(IEnumerable<string> nodeIds)
    {
        _nodeIndexMap.Clear();
        int idx = 0;
        foreach (var id in nodeIds)
        {
            _nodeIndexMap[id] = idx++;
        }
    }

    public Task<float> PredictLinkProbabilityAsync(string sourceId, string targetId, CancellationToken ct = default)
    {
        if (_session == null ||
            !_nodeIndexMap.TryGetValue(sourceId, out int srcIdx) ||
            !_nodeIndexMap.TryGetValue(targetId, out int tgtIdx))
        {
            return Task.FromResult(0.50f); // Нейтральный априорный фоллбэк
        }

        var edgeTensor = new DenseTensor<long>(new long[] { srcIdx, tgtIdx }, new[] { 2, 1 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("edge_index", edgeTensor)
        };

        try
        {
            using var results = _session.Run(inputs);
            var logitTensor = results.First().AsTensor<float>();
            float logit = logitTensor.GetValue(0);

            // Сигмоидальная нормализация логита в вероятность
            float probability = 1.0f / (1.0f + MathF.Exp(-logit));
            return Task.FromResult(Math.Clamp(probability, 0.01f, 0.99f));
        }
        catch
        {
            return Task.FromResult(0.50f);
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}