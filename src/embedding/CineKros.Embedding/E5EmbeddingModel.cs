using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.DotNet;

namespace CineKros.Embedding;

public sealed class E5EmbeddingModel : IDisposable
{
    public const string Revision = "f52bf8ec8c7124536f0efb74aca902b2995e5bcd";
    public const string Profile = "e5-base-v2-int8-onnx-v1";
    public const int Dimension = 768;
    public const int MaxTokens = 512;
    private const string ModelFile = "model_qint8_avx512_vnni.onnx";
    private readonly Tokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly string _modelHash;
    private readonly string _tokenizerHash;
    private long _truncationCount;
    private long _inferenceRunCount;
    private int _lastInferenceBatchSize;

    public string ProfileFingerprint { get; }
    public TimeSpan LoadTime { get; }
    public string ModelDirectory { get; }
    public long TruncationCount => Interlocked.Read(ref _truncationCount);
    public long InferenceRunCount => Interlocked.Read(ref _inferenceRunCount);
    public int LastInferenceBatchSize => Volatile.Read(ref _lastInferenceBatchSize);

    public E5EmbeddingModel(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        var timer = Stopwatch.StartNew();
        ModelDirectory = Path.GetFullPath(modelDirectory);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ModelFile] = "f2ff55f62dfca9ce0f4a5656ae0b1571b9fbc5e15eda3b2c56dd32f329b2005e",
            ["tokenizer.json"] = "d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66",
            ["tokenizer_config.json"] = "ae83fa6ca0333117ff12606020af925d648667ef70d92ff7f27d781ba0ca4544",
            ["special_tokens_map.json"] = "b6d346be366a7d1d48332dbc9fdf3bf8960b5d879522b7799ddba59e76237ee3",
            ["vocab.txt"] = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"
        };
        foreach (var (name, expected) in files)
        {
            var path = Path.Combine(ModelDirectory, name);
            if (!File.Exists(path) || Hash(path) != expected)
                throw new InvalidDataException($"Pinned E5 artifact missing or checksum mismatch: {name}");
        }
        _modelHash = files[ModelFile];
        _tokenizerHash = files["tokenizer.json"];
        _tokenizer = new Tokenizer(vocabPath: Path.Combine(ModelDirectory, "tokenizer.json"));
        _session = new InferenceSession(Path.Combine(ModelDirectory, ModelFile));
        ValidateGraph(_session);
        ProfileFingerprint = ComputeProfileFingerprint();
        if (ProfileFingerprint != "9411a2620fc30e348aa80c9d4e54ca0db5a00d94a92175c82ccdd47ad03b13e1")
            throw new InvalidDataException("Pinned E5 profile fingerprint mismatch.");
        LoadTime = timer.Elapsed;
    }

    public float[][] EmbedDocuments(IReadOnlyList<string> semanticTexts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(semanticTexts);
        if (semanticTexts.Count == 0) throw new ArgumentException("At least one document is required.", nameof(semanticTexts));
        return RunBatch(semanticTexts.Select(FormatDocumentInput).ToArray(), cancellationToken);
    }

    public float[] EmbedDocument(string semanticText, CancellationToken cancellationToken = default) =>
        RunBatch(new[] { FormatDocumentInput(semanticText) }, cancellationToken)[0];

    public float[] EmbedQuery(string semanticQuery, CancellationToken cancellationToken = default) =>
        RunBatch(new[] { FormatQueryInput(semanticQuery) }, cancellationToken)[0];

    public static string FormatDocumentInput(string semanticText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticText);
        return "passage: " + semanticText;
    }

    public static string FormatQueryInput(string semanticQuery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticQuery);
        return "query: " + semanticQuery.Trim();
    }

    public EncodedInput Tokenize(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var ids = EncodeIds(text);
        return new EncodedInput(ids, ids.Select(_ => 1L).ToArray());
    }

    public EncodedBatch TokenizeBatch(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) throw new ArgumentException("At least one text is required.", nameof(texts));
        var encoded = texts.Select(Tokenize).ToArray();
        var length = encoded.Max(x => x.InputIds.Length);
        var ids = new long[texts.Count, length]; var masks = new long[texts.Count, length];
        for (var row = 0; row < encoded.Length; row++)
            for (var column = 0; column < encoded[row].InputIds.Length; column++)
            { ids[row, column] = encoded[row].InputIds[column]; masks[row, column] = 1; }
        return new EncodedBatch(ids, masks);
    }

    public static float[] MaskedMeanPool(float[,,] hidden, long[,] mask, int batchIndex = 0)
    {
        var batch = hidden.GetLength(0); var sequence = hidden.GetLength(1); var dimension = hidden.GetLength(2);
        if (batchIndex < 0 || batchIndex >= batch || mask.GetLength(0) != batch || mask.GetLength(1) != sequence || dimension != Dimension)
            throw new ArgumentException("Hidden state and mask shapes do not match the E5 contract.");
        var vector = new float[dimension]; var count = 0;
        for (var t = 0; t < sequence; t++)
        {
            if (mask[batchIndex, t] == 0) continue;
            count++;
            for (var d = 0; d < dimension; d++) vector[d] += hidden[batchIndex, t, d];
        }
        if (count == 0) throw new InvalidDataException("Attention mask has no active tokens.");
        double normSquared = 0;
        for (var d = 0; d < dimension; d++)
        {
            vector[d] /= count;
            if (!float.IsFinite(vector[d])) throw new InvalidDataException("ONNX returned non-finite values.");
            normSquared += (double)vector[d] * vector[d];
        }
        var norm = Math.Sqrt(normSquared);
        if (!double.IsFinite(norm) || norm <= 0) throw new InvalidDataException("ONNX returned a zero or invalid vector.");
        for (var d = 0; d < dimension; d++) vector[d] = (float)(vector[d] / norm);
        return vector;
    }

    private float[][] RunBatch(string[] formattedInputs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encoded = formattedInputs.Select(EncodeIds).ToArray();
        var sequenceLength = encoded.Max(row => row.Length);
        var ids = new long[encoded.Length, sequenceLength]; var mask = new long[encoded.Length, sequenceLength]; var types = new long[encoded.Length, sequenceLength];
        for (var row = 0; row < encoded.Length; row++)
            for (var column = 0; column < encoded[row].Length; column++)
            { ids[row, column] = encoded[row][column]; mask[row, column] = 1; }
        cancellationToken.ThrowIfCancellationRequested();
        _runGate.Wait(cancellationToken);
        try
        {
            Interlocked.Increment(ref _inferenceRunCount);
            Interlocked.Exchange(ref _lastInferenceBatchSize, encoded.Length);
            using var results = _session.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", ToTensor(ids)),
                NamedOnnxValue.CreateFromTensor("attention_mask", ToTensor(mask)),
                NamedOnnxValue.CreateFromTensor("token_type_ids", ToTensor(types))
            }, new[] { "last_hidden_state" });
            var tensor = results.Single().AsTensor<float>();
            if (tensor.Rank != 3 || tensor.Dimensions[0] != encoded.Length || tensor.Dimensions[1] != sequenceLength || tensor.Dimensions[2] != Dimension)
                throw new InvalidDataException("Pinned E5 graph returned an unexpected output shape.");
            var hidden = new float[encoded.Length, sequenceLength, Dimension];
            for (var row = 0; row < encoded.Length; row++)
                for (var t = 0; t < sequenceLength; t++)
                    for (var d = 0; d < Dimension; d++) hidden[row, t, d] = tensor[row, t, d];
            var vectors = new float[encoded.Length][];
            for (var row = 0; row < vectors.Length; row++) vectors[row] = MaskedMeanPool(hidden, mask, row);
            return vectors;
        }
        finally { _runGate.Release(); }
    }

    private long[] EncodeIds(string text)
    {
        var all = _tokenizer.Encode(text).Select(x => checked((long)x)).ToArray();
        if (all.Length < 2) throw new InvalidDataException("E5 tokenizer did not produce special tokens.");
        if (all[0] != 101 || all[^1] != 102) throw new InvalidDataException("Pinned BERT tokenizer omitted required [CLS]/[SEP] tokens.");
        if (all.Length > MaxTokens)
        {
            Interlocked.Increment(ref _truncationCount);
            return all.Take(MaxTokens - 1).Append(all[^1]).ToArray();
        }
        return all;
    }

    public string ComputeDocumentFingerprint(string semanticText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticText);
        var json = JsonSerializer.Serialize(new DocumentFingerprint(FormatDocumentInput(semanticText), "intfloat/e5-base-v2", Revision, _modelHash, _tokenizerHash, Dimension, "passage", "e5-passage-semantictext-v1", MaxTokens, "mask-mean-v1", "l2-f32-v1"), FingerprintJson.Options);
        return Sha256(Encoding.UTF8.GetBytes(json));
    }

    private string ComputeProfileFingerprint()
    {
        var json = JsonSerializer.Serialize(new ProfileFingerprintData(Profile, "intfloat/e5-base-v2", Revision, _modelHash, _tokenizerHash, Dimension, "e5-passage-semantictext-v1", "e5-query-english-v1", MaxTokens, "mask-mean-v1", "l2-f32-v1", "cosine"), FingerprintJson.Options);
        return Sha256(Encoding.UTF8.GetBytes(json));
    }

    private static void ValidateGraph(InferenceSession session)
    {
        var expected = new Dictionary<string, NodeMetadata>(StringComparer.Ordinal)
        {
            ["input_ids"] = null!, ["attention_mask"] = null!, ["token_type_ids"] = null!
        };
        if (session.InputMetadata.Count != 3 || session.OutputMetadata.Count != 1 || !session.OutputMetadata.ContainsKey("last_hidden_state"))
            throw new InvalidDataException("Pinned E5 ONNX graph tensor names do not match the contract.");
        foreach (var (name, metadata) in session.InputMetadata)
        {
            if (!expected.ContainsKey(name) || metadata.ElementType != typeof(long) || metadata.Dimensions.Length != 2)
                throw new InvalidDataException($"Pinned E5 ONNX input mismatch: {name}");
        }
        var output = session.OutputMetadata["last_hidden_state"];
        if (output.ElementType != typeof(float) || output.Dimensions.Length != 3 || output.Dimensions[2] != Dimension)
            throw new InvalidDataException("Pinned E5 ONNX output type or shape mismatch.");
    }

    private static DenseTensor<long> ToTensor(long[,] source) => new(source.Cast<long>().ToArray(), new[] { source.GetLength(0), source.GetLength(1) });
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose() { _session.Dispose(); _runGate.Dispose(); }
}

public sealed record EncodedInput(long[] InputIds, long[] AttentionMask);
public sealed record EncodedBatch(long[,] InputIds, long[,] AttentionMask);
internal sealed record ProfileFingerprintData(string Profile, string ModelId, string Revision, string OnnxSha256, string TokenizerSha256, int Dimension, string DocumentFormat, string QueryFormat, int MaxTokens, string Pooling, string Normalization, string Distance);
internal sealed record DocumentFingerprint(string FormattedText, string ModelId, string Revision, string OnnxSha256, string TokenizerSha256, int Dimension, string Task, string FormattingVersion, int MaxTokens, string Pooling, string Normalization);
internal static class FingerprintJson { internal static readonly JsonSerializerOptions Options = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }; }
