using System.Security.Cryptography;
using System.Text.Json;
using CineKros.Catalog.Importer;
using CineKros.Embedding;

namespace CineKros.VectorImporter;

/// <summary>Strict, read-only reader for MAIN's current Phase 9 database baseline.</summary>
public sealed record FullImportBaseline(
    string SourceDatabase,
    string ServerSystemIdentifier,
    int PostgresMajor,
    string PgvectorVersion,
    int MovieCount,
    int VectorCount,
    string LegacyCatalogVersion,
    string LegacyCatalogSha256,
    string LegacyCatalogContentFingerprint,
    string LegacyVectorArtifactSha256,
    string LegacyProfileFingerprint,
    string Migration001Sha256,
    string Migration002Sha256,
    long BaselineDatabaseBytes,
    bool ApplicationPortsFree,
    int ProductionConcurrentSessionsOtherThanInspector,
    IReadOnlyDictionary<string, string> BaselineDigests,
    IReadOnlyList<string> MigrationVersions,
    bool SrColumnsAbsent,
    bool EmbeddingSetStateAbsent,
    string DescriptorSha256)
{
    private static readonly string[] Fields =
    [
            "schemaVersion", "sourceDatabase", "serverSystemIdentifier", "postgresMajor", "pgvectorVersion",
            "movieCount", "vectorCount", "legacyCatalogVersion", "legacyCatalogSha256", "legacyCatalogContentFingerprint",
            "legacyVectorArtifactSha256", "legacyProfileFingerprint", "migration001Sha256", "migration002Sha256",
        "baselineDigests", "baselineDatabaseBytes", "applicationPortsFree", "productionConcurrentSessionsOtherThanInspector",
        "baselineMigrationVersions", "srColumnsAbsent", "embeddingSetStateAbsent"
    ];

    public static async Task<FullImportBaseline> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Baseline descriptor path must be absolute.", nameof(path));
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(x => x.Name).SequenceEqual(Fields, StringComparer.Ordinal) ||
            S(root, "schemaVersion") != "sr-p9-cutover-baseline-v1")
            throw new InvalidDataException("Phase 9 baseline descriptor schema is not the exact reviewed format.");

        var digestsNode = root.GetProperty("baselineDigests");
        var digestFields = new[] { "movies", "vectors", "catalogState", "migrations" };
        if (digestsNode.ValueKind != JsonValueKind.Object || !digestsNode.EnumerateObject().Select(x => x.Name).SequenceEqual(digestFields, StringComparer.Ordinal))
            throw new InvalidDataException("Phase 9 baseline digests are incomplete or reordered.");
        var digests = digestFields.ToDictionary(name => name, name => S(digestsNode, name), StringComparer.Ordinal);
        foreach (var digest in digests.Values) RequireHex(digest, 16, "baseline digest");
        var versionsNode = root.GetProperty("baselineMigrationVersions");
        if (versionsNode.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Migration baseline list is malformed.");
        var versions = versionsNode.EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException("Migration version is malformed.")).ToArray();
        if (!versions.SequenceEqual(["001_initial_schema"], StringComparer.Ordinal))
            throw new InvalidDataException("The recorded legacy baseline must contain exactly migration 001.");

        var baseline = new FullImportBaseline(S(root, "sourceDatabase"), S(root, "serverSystemIdentifier"), I(root, "postgresMajor"),
            S(root, "pgvectorVersion"), I(root, "movieCount"), I(root, "vectorCount"), S(root, "legacyCatalogVersion"),
            S(root, "legacyCatalogSha256"), S(root, "legacyCatalogContentFingerprint"), S(root, "legacyVectorArtifactSha256"),
            S(root, "legacyProfileFingerprint"), S(root, "migration001Sha256"), S(root, "migration002Sha256"),
            L(root, "baselineDatabaseBytes"), B(root, "applicationPortsFree"), I(root, "productionConcurrentSessionsOtherThanInspector"), digests,
            Array.AsReadOnly(versions), B(root, "srColumnsAbsent"), B(root, "embeddingSetStateAbsent"), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        baseline.ValidatePinnedFacts();
        return baseline;
    }

    public void ValidatePinnedFacts()
    {
        if (SourceDatabase != "cinekros" || ServerSystemIdentifier != "7690124133367640101" || PostgresMajor != 17 ||
            PgvectorVersion != "0.8.6" || MovieCount != CatalogValidator.ExpectedCount || VectorCount != CatalogValidator.ExpectedCount ||
            LegacyCatalogVersion != CatalogValidator.CatalogVersion || LegacyCatalogSha256 != CatalogValidator.ExpectedHash ||
            LegacyCatalogContentFingerprint != VectorArtifactValidator.CatalogFingerprint ||
            LegacyVectorArtifactSha256 != "712f990185d2db34c55165081a9b80819ede7d2872f24989394ffa9eb13164c7" ||
            LegacyProfileFingerprint != EmbeddingProfileDescriptor.LegacyEnglish.ProfileFingerprint ||
            Migration001Sha256 != "14e5ae626c2288596d563482a510dcb8c79320dd5d41955cc5f06579dd59f238" ||
            Migration002Sha256 != "857b675a33f5ef67670b6f0e2b3c5383e5454494cafe88e707ce12f50eb3b8e3" ||
            BaselineDatabaseBytes != 54343347 || !ApplicationPortsFree || ProductionConcurrentSessionsOtherThanInspector != 0 ||
            BaselineDigests.GetValueOrDefault("movies") != "3dcf005cd50da4092183b8fc07234148" ||
            BaselineDigests.GetValueOrDefault("vectors") != "dfd482457f0985a3c8b681c8210ac9d0" ||
            BaselineDigests.GetValueOrDefault("catalogState") != "42e6ac2a07a4c39e0742c763481d7605" ||
            BaselineDigests.GetValueOrDefault("migrations") != "cd83d5c35331203b0ebca32e8cdb7408" ||
            MigrationVersions.Count != 1 || MigrationVersions[0] != "001_initial_schema" || !SrColumnsAbsent || !EmbeddingSetStateAbsent)
            throw new InvalidDataException("Phase 9 baseline does not match the pinned current legacy production facts.");
    }

    public void RequireExpectedDatabase(string actualDatabase, string expectedDatabase, bool mainProductionApproval = false)
    {
        if (!IsAllowedExecutionTarget(actualDatabase, expectedDatabase, mainProductionApproval))
            throw new InvalidOperationException("Actual and explicitly expected database must match an approved Phase 9 rehearsal/test target; production requires separate MAIN approval.");
    }

    public static bool IsAllowedExecutionTarget(string actualDatabase, string expectedDatabase, bool mainProductionApproval)
    {
        if (string.IsNullOrWhiteSpace(actualDatabase) || string.IsNullOrWhiteSpace(expectedDatabase) || actualDatabase != expectedDatabase) return false;
        if (actualDatabase == "cinekros") return mainProductionApproval;
        return System.Text.RegularExpressions.Regex.IsMatch(actualDatabase,
            "^cinekros_sr_p9_(?:rehearsal_20261009|test_[a-z0-9_]+)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new InvalidDataException($"Baseline field {n} is missing or malformed.");
    private static int I(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.TryGetInt32(out var x) ? x : throw new InvalidDataException($"Baseline field {n} is missing or malformed.");
    private static long L(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.TryGetInt64(out var x) ? x : throw new InvalidDataException($"Baseline field {n} is missing or malformed.");
    private static bool B(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw new InvalidDataException($"Baseline field {n} is missing or malformed.");
    private static void RequireHex(string value, int bytes, string field)
    {
        if (value.Length != bytes * 2 || value.Any(c => !Uri.IsHexDigit(c)) || value.Any(char.IsUpper)) throw new InvalidDataException($"{field} is not canonical lowercase hexadecimal.");
    }
}
