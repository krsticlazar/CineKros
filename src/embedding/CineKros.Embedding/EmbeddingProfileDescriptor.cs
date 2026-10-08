using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CineKros.Embedding;

public sealed class EmbeddingProfileDescriptor
{
    private static readonly JsonSerializerOptions FingerprintOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string ProfileVersion { get; }
    public string ModelId { get; }
    public string Revision { get; }
    public ImmutableArray<EmbeddingArtifact> Artifacts { get; }
    public string TokenizerContractVersion { get; }
    public string GraphContractVersion { get; }
    public string InputNormalizationVersion { get; }
    public string? InferenceShapePolicy { get; }
    public string QueryPrefix { get; }
    public string PassagePrefix { get; }
    public int MaxTokens { get; }
    public int Dimension { get; }
    public string Pooling { get; }
    public string Normalization { get; }
    public string Metric { get; }
    public long BosTokenId { get; }
    public long EosTokenId { get; }
    public long PadTokenId { get; }

    private EmbeddingProfileDescriptor(string profileVersion, string modelId, string revision,
        ImmutableArray<EmbeddingArtifact> artifacts, string tokenizerContractVersion, string graphContractVersion,
        string inputNormalizationVersion, string? inferenceShapePolicy, string queryPrefix, string passagePrefix, int maxTokens, int dimension,
        string pooling, string normalization, string metric, long bosTokenId, long eosTokenId, long padTokenId)
    {
        ProfileVersion = profileVersion;
        ModelId = modelId;
        Revision = revision;
        Artifacts = artifacts;
        TokenizerContractVersion = tokenizerContractVersion;
        GraphContractVersion = graphContractVersion;
        InputNormalizationVersion = inputNormalizationVersion;
        InferenceShapePolicy = inferenceShapePolicy;
        QueryPrefix = queryPrefix;
        PassagePrefix = passagePrefix;
        MaxTokens = maxTokens;
        Dimension = dimension;
        Pooling = pooling;
        Normalization = normalization;
        Metric = metric;
        BosTokenId = bosTokenId;
        EosTokenId = eosTokenId;
        PadTokenId = padTokenId;
    }

    public string ProfileFingerprint
    {
        get
        {
            if (ReferenceEquals(this, LegacyEnglish))
                return "9411a2620fc30e348aa80c9d4e54ca0db5a00d94a92175c82ccdd47ad03b13e1";
            var payload = new ProfileFingerprintPayload(ProfileVersion, ModelId, Revision,
                Artifacts.OrderBy(x => x.FileName, StringComparer.Ordinal).ToArray(), TokenizerContractVersion,
                GraphContractVersion, InputNormalizationVersion, InferenceShapePolicy, QueryPrefix, PassagePrefix, MaxTokens,
                Dimension, Pooling, Normalization, Metric);
            return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, FingerprintOptions)));
        }
    }

    public static EmbeddingProfileDescriptor LegacyEnglish { get; } = new(
        "e5-base-v2-int8-onnx-v1", "intfloat/e5-base-v2", "f52bf8ec8c7124536f0efb74aca902b2995e5bcd",
        [new("model_qint8_avx512_vnni.onnx", "f2ff55f62dfca9ce0f4a5656ae0b1571b9fbc5e15eda3b2c56dd32f329b2005e"),
         new("tokenizer.json", "d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66"),
         new("tokenizer_config.json", "ae83fa6ca0333117ff12606020af925d648667ef70d92ff7f27d781ba0ca4544"),
         new("special_tokens_map.json", "b6d346be366a7d1d48332dbc9fdf3bf8960b5d879522b7799ddba59e76237ee3"),
         new("vocab.txt", "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3")],
        "e5-bert-wordpiece-v1", "e5-bert-inputs-last-hidden-state-v1", "identity-v1", null, "query: ", "passage: ",
        512, 768, "mask-mean-v1", "l2-f32-v1", "cosine", 101, 102, 0);

    public static EmbeddingProfileDescriptor MultilingualE5Base { get; } = new(
        "multilingual-e5-base-int8-onnx-v1", "intfloat/multilingual-e5-base", "d128750597153bb5987e10b1c3493a34e5a4502a",
        [new("model_qint8_avx512_vnni.onnx", "2523551878658b305550d8759443822dbfda9ed9c8012ef2c354ba2c5b9de503"),
         new("tokenizer.json", "62c24cdc13d4c9952d63718d6c9fa4c287974249e16b7ade6d5a85e7bbb75626"),
         new("config.json", "4c27930e59106027abab56f7531c1fa6b14bbf31e8229ec36d68affa4e869bcd"),
         new("tokenizer_config.json", "efb5c0d09722e5fe59a462cd2a9976ee216d55b037597d997cd3fe833216da15"),
         new("special_tokens_map.json", "06e405a36dfe4b9604f484f6a1e619af1a7f7d09e34a8555eb0b77b66318067f")],
        "xlm-roberta-tokenizer-json-v1", "xlm-roberta-input-ids-attention-mask-last-hidden-state-v1", "unicode-nfc-case-preserving-v1", "single-sequence-unpadded-v1",
        "query: ", "passage: ", 512, 768, "masked-mean-v1", "l2-float32-v1", "cosine", 0, 2, 1);

    public EmbeddingArtifact Artifact(string fileName) =>
        Artifacts.FirstOrDefault(x => string.Equals(x.FileName, fileName, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Artifact is not part of profile {ProfileVersion}: {fileName}");

    public bool IsCompatibleWith(EmbeddingProfileDescriptor other) =>
        other is not null && string.Equals(ProfileFingerprint, other.ProfileFingerprint, StringComparison.Ordinal);

    private sealed record ProfileFingerprintPayload(string ProfileVersion, string ModelId, string Revision,
        EmbeddingArtifact[] Artifacts, string TokenizerContractVersion, string GraphContractVersion,
        string InputNormalizationVersion,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InferenceShapePolicy,
        string QueryPrefix, string PassagePrefix, int MaxTokens, int Dimension,
        string Pooling, string Normalization, string Metric);
}

public sealed record EmbeddingArtifact(string FileName, string Sha256);
