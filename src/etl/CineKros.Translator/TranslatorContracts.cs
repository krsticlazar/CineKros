using System.Text.Json.Serialization;

namespace CineKros.Translator;

public static class TranslatorConstants
{
    public const string ModelId = "Helsinki-NLP/opus-mt-en-sla";
    public const string ModelRevision = "0bc26914f2f82c3dd5b235e420aa2c711a5ed3d8";
    public const string TargetToken = ">>srp_Latn<<";
    public const string NormalizationVersion = "serbian-latin-nfc-whitespace-v1";
    public const string QaVersion = "tag-translation-qa-v1";
    public const int MaxSourceTokens = 512;
    public const int MaxNewTokens = 32;
    public const int NumBeams = 4;
    public const string DecodingId = "marian-do-sample-false-beam4-maxnew32-v1";

    public static readonly string[] SmokePhrases = ["dark", "friendship", "camp", "feel-good", "beautiful animation", "psychological mind games"];
}

public sealed record TranslatorMetadata(
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("targetToken")] string TargetToken,
    [property: JsonPropertyName("artifactHashes")] SortedDictionary<string, string> ArtifactHashes,
    [property: JsonPropertyName("runtimeLockSha256")] string RuntimeLockSha256,
    [property: JsonPropertyName("decoding")] DecodingMetadata Decoding);

public sealed record DecodingMetadata(
    [property: JsonPropertyName("doSample")] bool DoSample,
    [property: JsonPropertyName("numBeams")] int NumBeams,
    [property: JsonPropertyName("maxNewTokens")] int MaxNewTokens,
    [property: JsonPropertyName("sourceMaxTokens")] int SourceMaxTokens,
    [property: JsonPropertyName("device")] string Device);

public sealed record TranslationEntry(
    [property: JsonPropertyName("en")] string En,
    [property: JsonPropertyName("machine")] string Machine,
    [property: JsonPropertyName("sr")] string Sr,
    [property: JsonPropertyName("reviewStatus")] string ReviewStatus,
    [property: JsonPropertyName("manualOverride")] bool ManualOverride);

public sealed record TranslationDictionary(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("sourceTagsSha256")] string SourceTagsSha256,
    [property: JsonPropertyName("translator")] TranslatorMetadata Translator,
    [property: JsonPropertyName("normalizationVersion")] string NormalizationVersion,
    [property: JsonPropertyName("entries")] IReadOnlyList<TranslationEntry> Entries);

public sealed record QaFlag(string Code, string Message);

public sealed record QaEntry(string En, string Machine, string Sr, IReadOnlyList<QaFlag> Flags);

public sealed record QaReport(string Version, string SourceTagsSha256, IReadOnlyList<QaEntry> Entries, string ContentSha256);

public sealed record ProtocolJob(
    [property: JsonPropertyName("en")] string En,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("settingsId")] string SettingsId);

public sealed record ProtocolResponse(
    [property: JsonPropertyName("en")] string En,
    [property: JsonPropertyName("machine")] string? Machine,
    [property: JsonPropertyName("completed")] bool Completed,
    [property: JsonPropertyName("error")] string? Error);
