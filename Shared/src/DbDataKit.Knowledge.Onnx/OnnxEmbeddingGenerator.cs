using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace DbDataKit.Knowledge.Onnx;

/// <summary>
/// A sentence-embedding model run in this process (plan 27a KN06): the text is cut into WordPiece tokens with the model's own vocabulary (lower case, the model's <c>[CLS]</c> and <c>[SEP]</c>), read by ONNX Runtime on the CPU, the vectors of the tokens are averaged over the real tokens (the padding does not count) and the
/// result is scaled to length one, as the model's authors do it, so the dot product of two vectors is their cosine. A text longer than the model reads (<see cref="BuiltInModel.MaxTokens"/>) is cut at that length, keeping the closing <c>[SEP]</c>.
/// No network and no server: the files are checked against their hash before they are loaded (<see cref="BuiltInEmbeddingModels.Verify"/>), so a damaged or substituted file is refused, not run.
/// </summary>
public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly BertTokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly BuiltInModel _model;
    private readonly object _gate = new();
    private readonly bool _needsTokenTypes;

    /// <summary>Opens the model from a folder that holds its files; the files are checked first.</summary>
    /// <exception cref="InvalidDataException">A file is missing or is not the file of the model.</exception>
    public OnnxEmbeddingGenerator(string folder, BuiltInModel model, int threads = 2)
    {
        _model = model;
        if (BuiltInEmbeddingModels.Verify(folder, model) is { Count: > 0 } problems) throw new InvalidDataException(string.Join(" ", problems));
        using (var vocabulary = File.OpenRead(Path.Combine(folder, model.Vocabulary.Name)))
            _tokenizer = BertTokenizer.Create(vocabulary, new BertOptions { LowerCaseBeforeTokenization = true });
        var options = new SessionOptions { IntraOpNumThreads = Math.Max(1, threads), InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(Path.Combine(folder, model.Model.Name), options);
        _needsTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
    }

    public string ModelId => _model.Id;

    public int Dimensions => _model.Dimension;

    public EmbeddingGeneratorMetadata Metadata => new("onnx", null, _model.Id, _model.Dimension);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();
        var result = new GeneratedEmbeddings<Embedding<float>>(texts.Count);
        // the model is read in small batches, so one long text does not pad many short ones to its length
        for (var start = 0; start < texts.Count; start += 16)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var vector in Embed(texts.Skip(start).Take(16).ToList())) result.Add(new Embedding<float>(vector));
        }
        return Task.FromResult(result);
    }

    /// <summary>The ids of a text: <c>[CLS]</c>, the word pieces, <c>[SEP]</c>, cut at the model's length with the closing token kept.</summary>
    public IReadOnlyList<int> Tokens(string text)
    {
        var ids = _tokenizer.EncodeToIds(text, _model.MaxTokens, out _, out _).ToList();
        return ids;
    }

    private float[][] Embed(IReadOnlyList<string> texts)
    {
        var tokens = texts.Select(Tokens).ToList();
        var length = Math.Max(2, tokens.Max(t => t.Count));
        var ids = new DenseTensor<long>([texts.Count, length]);
        var mask = new DenseTensor<long>([texts.Count, length]);
        var types = new DenseTensor<long>([texts.Count, length]);
        for (var i = 0; i < tokens.Count; i++)
            for (var j = 0; j < tokens[i].Count; j++) { ids[i, j] = tokens[i][j]; mask[i, j] = 1; }
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input_ids", ids), NamedOnnxValue.CreateFromTensor("attention_mask", mask) };
        if (_needsTokenTypes) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));

        Tensor<float> hidden;
        lock (_gate)
        {
            using var outputs = _session.Run(inputs);
            hidden = outputs.First().AsTensor<float>().Clone();
        }
        // mean over the real tokens of each text, then length one
        var dimension = hidden.Dimensions[2];
        var vectors = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            var sum = new double[dimension];
            for (var j = 0; j < tokens[i].Count; j++)
                for (var d = 0; d < dimension; d++) sum[d] += hidden[i, j, d];
            var count = Math.Max(1, tokens[i].Count);
            var norm = Math.Sqrt(sum.Sum(x => x / count * (x / count)));
            vectors[i] = [.. sum.Select(x => norm == 0 ? 0f : (float)(x / count / norm))];
        }
        return vectors;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(EmbeddingGeneratorMetadata) ? Metadata : serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _session.Dispose();
}
