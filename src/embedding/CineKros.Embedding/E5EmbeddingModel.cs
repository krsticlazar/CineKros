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
    private readonly EmbeddingProfileDescriptor _profile;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly string _modelHash;
    private readonly string _tokenizerHash;
    private long _truncationCount;
    private long _inferenceRunCount;
    private int _lastInferenceBatchSize;

    public string ProfileFingerprint { get; }
    public EmbeddingProfileDescriptor ProfileDescriptor => _profile;
    public string ProfileId => _profile.ProfileVersion;
    public IReadOnlyList<EmbeddingGraphNode> GraphInputs => _session.InputMetadata
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => new EmbeddingGraphNode(pair.Key, pair.Value.ElementType.FullName ?? pair.Value.ElementType.Name, pair.Value.Dimensions.ToArray()))
        .ToArray();
    public IReadOnlyList<EmbeddingGraphNode> GraphOutputs => _session.OutputMetadata
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => new EmbeddingGraphNode(pair.Key, pair.Value.ElementType.FullName ?? pair.Value.ElementType.Name, pair.Value.Dimensions.ToArray()))
        .ToArray();
    public TimeSpan LoadTime { get; }
    public string ModelDirectory { get; }
    public long TruncationCount => Interlocked.Read(ref _truncationCount);
    public long InferenceRunCount => Interlocked.Read(ref _inferenceRunCount);
    public int LastInferenceBatchSize => Volatile.Read(ref _lastInferenceBatchSize);

    public E5EmbeddingModel(string modelDirectory) : this(modelDirectory, EmbeddingProfileDescriptor.LegacyEnglish) { }

    public E5EmbeddingModel(string modelDirectory, EmbeddingProfileDescriptor profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentNullException.ThrowIfNull(profile);
        if (!ReferenceEquals(profile, EmbeddingProfileDescriptor.LegacyEnglish) && !ReferenceEquals(profile, EmbeddingProfileDescriptor.MultilingualE5Base))
            throw new ArgumentException("Only the locked legacy and multilingual E5 profiles are supported.", nameof(profile));
        var timer = Stopwatch.StartNew();
        _profile = profile;
        ModelDirectory = Path.GetFullPath(modelDirectory);
        foreach (var artifact in _profile.Artifacts)
        {
            var path = Path.Combine(ModelDirectory, artifact.FileName);
            if (!File.Exists(path) || Hash(path) != artifact.Sha256)
                throw new InvalidDataException($"Pinned E5 artifact missing or checksum mismatch: {artifact.FileName}");
        }
        _modelHash = _profile.Artifact(ModelFile).Sha256;
        _tokenizerHash = _profile.Artifact("tokenizer.json").Sha256;
        _tokenizer = new Tokenizer(vocabPath: Path.Combine(ModelDirectory, "tokenizer.json"));
        _session = new InferenceSession(Path.Combine(ModelDirectory, ModelFile));
        ValidateGraph(_session, _profile);
        ProfileFingerprint = _profile == EmbeddingProfileDescriptor.LegacyEnglish
            ? ComputeLegacyProfileFingerprint()
            : _profile.ProfileFingerprint;
        if (_profile == EmbeddingProfileDescriptor.LegacyEnglish && ProfileFingerprint != "9411a2620fc30e348aa80c9d4e54ca0db5a00d94a92175c82ccdd47ad03b13e1")
            throw new InvalidDataException("Pinned E5 profile fingerprint mismatch.");
        LoadTime = timer.Elapsed;
    }

    public float[][] EmbedDocuments(IReadOnlyList<string> semanticTexts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(semanticTexts);
        if (semanticTexts.Count == 0) throw new ArgumentException("At least one document is required.", nameof(semanticTexts));
        return RunBatch(semanticTexts.Select(FormatDocument).ToArray(), cancellationToken);
    }

    public float[] EmbedDocument(string semanticText, CancellationToken cancellationToken = default) =>
        RunBatch(new[] { FormatDocument(semanticText) }, cancellationToken)[0];

    public float[] EmbedQuery(string semanticQuery, CancellationToken cancellationToken = default) =>
        RunBatch(new[] { FormatQuery(semanticQuery) }, cancellationToken)[0];

    private string FormatDocument(string semanticText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticText);
        return ReferenceEquals(_profile, EmbeddingProfileDescriptor.LegacyEnglish)
            ? FormatDocumentInput(semanticText) : _profile.PassagePrefix + semanticText;
    }

    private string FormatQuery(string semanticQuery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticQuery);
        return ReferenceEquals(_profile, EmbeddingProfileDescriptor.LegacyEnglish)
            ? FormatQueryInput(semanticQuery) : _profile.QueryPrefix + semanticQuery.Trim();
    }

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
        if (_profile.InferenceShapePolicy == "single-sequence-unpadded-v1" && texts.Count != 1)
            throw new ArgumentException("The multilingual E5 profile accepts only one unpadded sequence per graph input.", nameof(texts));
        var encoded = texts.Select(Tokenize).ToArray();
        var length = encoded.Max(x => x.InputIds.Length);
        var ids = new long[texts.Count, length]; var masks = new long[texts.Count, length];
        for (var row = 0; row < texts.Count; row++)
            for (var column = 0; column < length; column++) ids[row, column] = _profile.PadTokenId;
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
        if (_profile.InferenceShapePolicy == "single-sequence-unpadded-v1")
        {
            var vectors = new float[formattedInputs.Length][];
            for (var index = 0; index < formattedInputs.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                vectors[index] = RunSingleSequence(EncodeIds(formattedInputs[index]), cancellationToken);
            }
            return vectors;
        }

        var encoded = formattedInputs.Select(EncodeIds).ToArray();
        var sequenceLength = encoded.Max(row => row.Length);
        var ids = new long[encoded.Length, sequenceLength]; var mask = new long[encoded.Length, sequenceLength];
        for (var row = 0; row < encoded.Length; row++)
            for (var column = 0; column < sequenceLength; column++) ids[row, column] = _profile.PadTokenId;
        for (var row = 0; row < encoded.Length; row++)
            for (var column = 0; column < encoded[row].Length; column++)
            { ids[row, column] = encoded[row][column]; mask[row, column] = 1; }
        cancellationToken.ThrowIfCancellationRequested();
        _runGate.Wait(cancellationToken);
        try
        {
            Interlocked.Increment(ref _inferenceRunCount);
            Interlocked.Exchange(ref _lastInferenceBatchSize, encoded.Length);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", ToTensor(ids)),
                NamedOnnxValue.CreateFromTensor("attention_mask", ToTensor(mask))
            };
            if (_session.InputMetadata.ContainsKey("token_type_ids"))
                inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", ToTensor(new long[encoded.Length, sequenceLength])));
            using var results = _session.Run(inputs, new[] { "last_hidden_state" });
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

    public EncodedInput TokenizeUncapped(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var normalized = _profile == EmbeddingProfileDescriptor.MultilingualE5Base ? text.Normalize(NormalizationForm.FormC) : text;
        var ids = _tokenizer.Encode(normalized).Select(x => checked((long)x)).ToArray();
        if (ids.Length < 2 || ids[0] != _profile.BosTokenId || ids[^1] != _profile.EosTokenId)
            throw new InvalidDataException("Pinned tokenizer omitted its required beginning/end special tokens.");
        return new EncodedInput(ids, ids.Select(_ => 1L).ToArray());
    }

    private float[] RunSingleSequence(long[] encoded, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = new long[1, encoded.Length];
        var mask = new long[1, encoded.Length];
        for (var column = 0; column < encoded.Length; column++) { ids[0, column] = encoded[column]; mask[0, column] = 1; }
        _runGate.Wait(cancellationToken);
        try
        {
            Interlocked.Increment(ref _inferenceRunCount);
            Interlocked.Exchange(ref _lastInferenceBatchSize, 1);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", ToTensor(ids)),
                NamedOnnxValue.CreateFromTensor("attention_mask", ToTensor(mask))
            };
            using var results = _session.Run(inputs, new[] { "last_hidden_state" });
            var tensor = results.Single().AsTensor<float>();
            if (tensor.Rank != 3 || tensor.Dimensions[0] != 1 || tensor.Dimensions[1] != encoded.Length || tensor.Dimensions[2] != _profile.Dimension)
                throw new InvalidDataException("Pinned E5 graph returned an unexpected single-sequence output shape.");
            var hidden = new float[1, encoded.Length, _profile.Dimension];
            for (var t = 0; t < encoded.Length; t++)
                for (var d = 0; d < _profile.Dimension; d++) hidden[0, t, d] = tensor[0, t, d];
            return MaskedMeanPool(hidden, mask);
        }
        finally { _runGate.Release(); }
    }

    private long[] EncodeIds(string text)
    {
        var normalized = _profile == EmbeddingProfileDescriptor.MultilingualE5Base ? text.Normalize(NormalizationForm.FormC) : text;
        var all = _tokenizer.Encode(normalized).Select(x => checked((long)x)).ToArray();
        if (all.Length < 2) throw new InvalidDataException("E5 tokenizer did not produce special tokens.");
        if (all[0] != _profile.BosTokenId || all[^1] != _profile.EosTokenId)
            throw new InvalidDataException("Pinned tokenizer omitted its required beginning/end special tokens.");
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
        if (_profile.InferenceShapePolicy == "single-sequence-unpadded-v1")
        {
            var targetJson = JsonSerializer.Serialize(new MultilingualDocumentFingerprint(
                FormatDocument(semanticText).Normalize(NormalizationForm.FormC), _profile.ModelId, _profile.Revision,
                _modelHash, _tokenizerHash, _profile.Dimension, "passage", "e5-passage-semantictext-v1", _profile.MaxTokens,
                _profile.Pooling, _profile.Normalization, _profile.InferenceShapePolicy), FingerprintJson.Options);
            return Sha256(Encoding.UTF8.GetBytes(targetJson));
        }
        var json = JsonSerializer.Serialize(new DocumentFingerprint(FormatDocument(semanticText), "intfloat/e5-base-v2", Revision, _modelHash, _tokenizerHash, Dimension, "passage", "e5-passage-semantictext-v1", MaxTokens, "mask-mean-v1", "l2-f32-v1"), FingerprintJson.Options);
        return Sha256(Encoding.UTF8.GetBytes(json));
    }

    private string ComputeLegacyProfileFingerprint()
    {
        var json = JsonSerializer.Serialize(new ProfileFingerprintData(Profile, "intfloat/e5-base-v2", Revision, _modelHash, _tokenizerHash, Dimension, "e5-passage-semantictext-v1", "e5-query-english-v1", MaxTokens, "mask-mean-v1", "l2-f32-v1", "cosine"), FingerprintJson.Options);
        return Sha256(Encoding.UTF8.GetBytes(json));
    }

    private static void ValidateGraph(InferenceSession session, EmbeddingProfileDescriptor profile)
    {
        var expectedInputs = profile == EmbeddingProfileDescriptor.LegacyEnglish
            ? new HashSet<string>(["input_ids", "attention_mask", "token_type_ids"], StringComparer.Ordinal)
            : new HashSet<string>(["input_ids", "attention_mask"], StringComparer.Ordinal);
        if (!session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedInputs) || session.OutputMetadata.Count != 1 || !session.OutputMetadata.ContainsKey("last_hidden_state"))
            throw new InvalidDataException("Pinned E5 ONNX graph tensor names do not match the contract.");
        foreach (var (name, metadata) in session.InputMetadata)
        {
            if (!expectedInputs.Contains(name) || metadata.ElementType != typeof(long) || metadata.Dimensions.Length != 2)
                throw new InvalidDataException($"Pinned E5 ONNX input mismatch: {name}");
        }
        var output = session.OutputMetadata["last_hidden_state"];
        if (output.ElementType != typeof(float) || output.Dimensions.Length != 3 || output.Dimensions[2] != profile.Dimension)
            throw new InvalidDataException("Pinned E5 ONNX output type or shape mismatch.");
    }

    private static DenseTensor<long> ToTensor(long[,] source) => new(source.Cast<long>().ToArray(), new[] { source.GetLength(0), source.GetLength(1) });
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose() { _session.Dispose(); _runGate.Dispose(); }
}

public sealed record EncodedInput(long[] InputIds, long[] AttentionMask);
public sealed record EncodedBatch(long[,] InputIds, long[,] AttentionMask);
public sealed record EmbeddingGraphNode(string Name, string ElementType, int[] Dimensions);
internal sealed record ProfileFingerprintData(string Profile, string ModelId, string Revision, string OnnxSha256, string TokenizerSha256, int Dimension, string DocumentFormat, string QueryFormat, int MaxTokens, string Pooling, string Normalization, string Distance);
internal sealed record DocumentFingerprint(string FormattedText, string ModelId, string Revision, string OnnxSha256, string TokenizerSha256, int Dimension, string Task, string FormattingVersion, int MaxTokens, string Pooling, string Normalization);
internal sealed record MultilingualDocumentFingerprint(string FormattedText, string ModelId, string Revision, string OnnxSha256, string TokenizerSha256, int Dimension, string Task, string FormattingVersion, int MaxTokens, string Pooling, string Normalization, string InferenceShapePolicy);
internal static class FingerprintJson { internal static readonly JsonSerializerOptions Options = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }; }
