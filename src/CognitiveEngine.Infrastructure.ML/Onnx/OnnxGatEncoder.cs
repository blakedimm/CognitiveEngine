namespace CognitiveEngine.Infrastructure.ML.Onnx;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

/// <summary>
/// Инференс сверхглубокого графового энкодера GATv2 (GraphSAGE v11.4), экспортированного в ONNX.
/// Преобразует текстовые эмбеддинги компонентов и структурные паспорта AST в компактное латентное пространство (128-dim).
/// </summary>
public sealed class OnnxGatEncoder : IDisposable
{
    private readonly InferenceSession? _session;
    public bool IsModelLoaded => _session != null;

    public OnnxGatEncoder(string modelPath = "gnn_encoder.onnx")
    {
        if (File.Exists(modelPath))
        {
            var sessionOptions = new SessionOptions();
            sessionOptions.AppendExecutionProvider_CPU();
            _session = new InferenceSession(modelPath, sessionOptions);
        }
    }

    /// <summary>
    /// Выполняет прямой проход GNN с объединением семантических и топологических признаков.
    /// </summary>
    /// <param name="xText">Текстовые эмбеддинги компонентов [numNodes, 1024]</param>
    /// <param name="xStruct">Паспорта AST-сложности [numNodes, 6]</param>
    /// <param name="edgeIndex">Матрица направленных связей [2, numEdges]</param>
    /// <returns>Латентные векторы узлов Z [numNodes, 128]</returns>
    public ReadOnlyMemory<float> Encode(
        DenseTensor<float> xText,
        DenseTensor<float> xStruct,
        DenseTensor<long> edgeIndex)
    {
        if (_session == null)
            return ReadOnlyMemory<float>.Empty;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("x_text", xText),
            NamedOnnxValue.CreateFromTensor("x_struct", xStruct),
            NamedOnnxValue.CreateFromTensor("edge_index", edgeIndex)
        };

        using var results = _session.Run(inputs);
        var outputTensor = results.First().AsTensor<float>();

        return outputTensor.ToArray();
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}