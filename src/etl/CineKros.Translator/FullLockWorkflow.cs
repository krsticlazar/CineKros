using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CineKros.TextNormalization;

namespace CineKros.Translator;

internal static class FullLockWorkflow
{
    internal const string ReviewSchema = "sr-phase-07-main-review-v1";
    internal const string CandidateSha = "8bb9ea7a3617b370a7224973093b8c2344d615ada295cdfc25322eb5eef235d6";
    internal const string BaselineSha = "09bd0afbde2b7b8a0afee502d7718f8dad6cc6320c226b437074ebb4b341ea4e";
    internal const string ProposalSha = "d4ef26c67d46da2274dbb88fd484b3bdfc62587274d57e4981f30a3802f265c5";
    internal const string SourceTagsSha = "aebef2332312b06f0494bbf70535a15bad77b9f3ba4ffaf9340ad9fdab3148a4";
    internal const string CatalogSha = "8b2bad0a22fef45842176a1d9f3730be1568e367b9fc230fa0aa398bb5c26946";
    private const string ProposalSubsetSha = "2b7c21cddb037cbf1812f7121ab30b4b59f4ee1370bffbe1103fc13a30d6ff9b";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions CompactJson = new();
    private static readonly HashSet<string> DecisionDispositions = ["corrected", "accepted_unchanged", "ambiguous_general", "retained_baseline"];
    private static readonly HashSet<string> ConfidenceValues = ["high", "medium", "low"];

    internal sealed record Paths(string Candidate, string Baseline, string Proposals, string SourceTags, string Catalog, string Review, string Output);
    internal sealed record Policy(int SourceCount, int BaselineCount, int ProposalCount, int MovieCount,
        string? CandidateSha, string? BaselineSha, string? ProposalSha, string? SourceTagsSha, string? CatalogSha,
        string SourceTagsCanonicalHash, string ProposalSubsetHash, bool Production);

    private sealed record ReviewDecision(string En, string ApprovedSr, string Disposition, string Rationale, string Confidence);
    private sealed record ReviewDocument(string SchemaVersion, string CandidateSha256, string BaselineSha256,
        string ProposalsSha256, string SourceTagsSha256, IReadOnlyList<ReviewDecision> Decisions);
    private sealed record RuntimeEvidence(string RuntimeFingerprint, string CheckpointSha256);
    private sealed record Manifest(string SchemaVersion, string ReleaseId, string SourceCatalogSha256, string SourceTagsSha256,
        string CandidateSha256, string BaselineSha256, string ProposalsSha256, string ReviewSha256, string DictionarySha256,
        string CheckpointSha256, string QaReportSha256, string CompatibilityReportSha256,
        string ModelId, string ModelRevision, string TargetToken, string RuntimeLockSha256, string RuntimeFingerprint,
        string NormalizationVersion, int MovieCount, int TagOccurrenceCount, int EntryCount, int BaselineEntryCount,
        int NewEntryCount, int ReviewedDecisionCount, int CorrectedNewEntryCount);
    private sealed record Compatibility(string SchemaVersion, string SourceCatalogSha256, int MovieCount,
        int TagOccurrenceCount, int MappedTagOccurrenceCount, int MissingTranslationCount,
        string Phase8InMemoryProjectionSha256, bool EnglishTagsPreserved, bool MetadataPreserved,
        bool SelectedTagScoresPreserved, bool GenresKeptUntouched, int UniqueEnglishTagCount,
        int SourceMovieFieldCount, int Phase8MovieFieldCount, bool SerbianTextValidated);
    private sealed record Provenance(string SchemaVersion, string CandidateSha256, string BaselineSha256, string ProposalsSha256,
        string ReviewSha256, JsonNode BaselineSourceMetadata, JsonArray BaselineEntries,
        IReadOnlyList<ReviewDecision> Decisions);

    internal static void Lock(CommandArguments args) => Lock(new Paths(
        args["candidate"], args["baseline"], args["proposals"], args["source-tags"],
        args["catalog"], args["review"], args["output-dir"]),
        new Policy(993, 418, 575, 9730, CandidateSha, BaselineSha, ProposalSha, SourceTagsSha, CatalogSha,
            SourceTagsSha, ProposalSubsetSha, true));

    internal static void Lock(Paths paths, Policy policy)
    {
        TagWorkflow.RequireFile(paths.Candidate, "full candidate");
        TagWorkflow.RequireFile(paths.Baseline, "pinned baseline");
        TagWorkflow.RequireFile(paths.Proposals, "missing-tag proposals");
        TagWorkflow.RequireFile(paths.SourceTags, "full source tags");
        TagWorkflow.RequireFile(paths.Catalog, "protected source catalog");
        TagWorkflow.RequireFile(paths.Review, "MAIN review");

        var candidateHash = TagWorkflow.HashFile(paths.Candidate);
        var baselineHash = TagWorkflow.HashFile(paths.Baseline);
        var proposalHash = TagWorkflow.HashFile(paths.Proposals);
        var catalogHash = TagWorkflow.HashFile(paths.Catalog);
        var sourceFileHash = TagWorkflow.HashFile(paths.SourceTags);
        CheckPinned(candidateHash, policy.CandidateSha, "candidate");
        CheckPinned(baselineHash, policy.BaselineSha, "baseline");
        CheckPinned(proposalHash, policy.ProposalSha, "proposals");
        CheckPinned(catalogHash, policy.CatalogSha, "source catalog");

        var tags = TagWorkflow.ReadTagArray(paths.SourceTags);
        var rawTags = JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(paths.SourceTags)) ??
            throw new InvalidDataException("full source tags must be a JSON array");
        if (!rawTags.SequenceEqual(tags, StringComparer.Ordinal))
            throw new InvalidDataException("full source tags must already be in exact ordinal order");
        TagWorkflow.ValidateFullDictionarySize(tags.Count);
        var sourceHash = TagWorkflow.HashCanonical(tags);
        CheckPinned(sourceHash, policy.SourceTagsSha, "source tags");
        if (sourceHash != policy.SourceTagsCanonicalHash)
            throw new InvalidDataException("source-tag canonical hash differs from the current full-lock policy");
        if (tags.Count != policy.SourceCount)
            throw new InvalidDataException("full source-tag count does not match the locked release contract");

        var baselineRoot = ReadStrictJsonObject(paths.Baseline, "baseline");
        var candidateRoot = ReadStrictJsonObject(paths.Candidate, "full candidate");
        var proposalRoot = ReadStrictJsonObject(paths.Proposals, "proposals");
        var reviewRoot = ReadStrictJsonObject(paths.Review, "MAIN review");
        ValidateBaselineRoot(baselineRoot);
        ValidateCandidateRoot(candidateRoot, policy.SourceTagsCanonicalHash);
        ValidateProposalRoot(proposalRoot, policy.ProposalSubsetHash);
        var baselineEntries = ReadEntries(baselineRoot, "baseline", allowProvenance: true);
        var candidateEntries = ReadEntries(candidateRoot, "candidate", allowProvenance: true);
        var proposalEntries = ReadEntries(proposalRoot, "proposals", allowProvenance: false);
        var review = ParseReview(reviewRoot);
        ValidateReviewHashes(review, candidateHash, baselineHash, proposalHash, sourceHash);

        if (baselineEntries.Count != policy.BaselineCount || proposalEntries.Count != policy.ProposalCount ||
            candidateEntries.Count != policy.SourceCount)
            throw new InvalidDataException("baseline, proposal, or candidate entry count does not match the locked contract");
        if (policy.Production && (baselineEntries.Count != 418 || proposalEntries.Count != 575 || candidateEntries.Count != 993))
            throw new InvalidDataException("full lock accepts exactly 418 baseline, 575 new, and 993 combined entries");

        var baselineByKey = IndexEntries(baselineEntries, "baseline");
        var candidateByKey = IndexEntries(candidateEntries, "candidate");
        var proposalByKey = IndexEntries(proposalEntries, "proposals");
        var sourceSet = tags.ToHashSet(StringComparer.Ordinal);
        ValidateExactCoverage(sourceSet, baselineByKey, proposalByKey, candidateByKey);
        PreserveBaselineExactly(baselineByKey, candidateByKey);

        var baselineDictionary = DeserializeDictionary(baselineRoot, baselineEntries);
        var proposalDictionary = TagWorkflow.ReadDictionary(paths.Proposals);
        var candidateDictionary = DeserializeDictionary(candidateRoot, candidateEntries);
        if (TagWorkflow.HashCanonical(baselineEntries.Select(item => item["en"]!.GetValue<string>()).ToArray()) != baselineDictionary.SourceTagsSha256)
            throw new InvalidDataException("pinned baseline source-tag hash does not match its exact keys");
        ValidateModelIdentity(baselineDictionary, proposalDictionary, candidateDictionary);
        ValidateProposalSubsetHash(proposalDictionary, policy.ProposalSubsetHash);
        ValidateProposalAgreement(candidateByKey, proposalByKey);
        var runtimeEvidence = ValidateProposalRuntimeEvidence(paths.Proposals, proposalDictionary, policy.ProposalCount);
        if (runtimeEvidence.RuntimeFingerprint.Length != 64)
            throw new InvalidDataException("proposal runtime fingerprint is malformed");

        var qaBefore = TranslationQa.Analyze(candidateDictionary);
        var properNameRisk = FindProperNameRisks(candidateDictionary.Entries.Where(entry => !baselineByKey.ContainsKey(entry.En)), paths.Catalog);
        var flaggedBefore = qaBefore.Entries.Where(entry => entry.Flags.Count > 0).Select(entry => entry.En)
            .Concat(properNameRisk).ToHashSet(StringComparer.Ordinal);
        var decisionByKey = ValidateDecisions(review.Decisions, sourceSet, baselineByKey, proposalByKey, flaggedBefore);
        var approved = ApplyDecisions(candidateByKey, baselineByKey, proposalByKey, decisionByKey);
        var approvedDictionary = candidateDictionary with { Entries = approved.OrderBy(entry => entry.En, StringComparer.Ordinal).ToArray() };
        var qaFinal = TranslationQa.Analyze(approvedDictionary);
        var finalFlagged = qaFinal.Entries.Where(entry => entry.Flags.Count > 0).Select(entry => entry.En).ToArray();
        var uncovered = finalFlagged.Where(key => !decisionByKey.ContainsKey(key)).ToArray();
        if (uncovered.Length > 0)
            throw new InvalidDataException($"post-review QA introduced uncovered keys requiring MAIN context review: {string.Join(", ", uncovered.Order(StringComparer.Ordinal))}");
        if (approved.Any(entry => string.IsNullOrWhiteSpace(entry.Sr) || entry.ReviewStatus == "review_required"))
            throw new InvalidDataException("full release contains blank or unresolved translations");

        var qaSerialized = JsonSerializer.SerializeToUtf8Bytes(qaFinal, PrettyJson);
        var finalRoot = JsonSerializer.SerializeToNode(approvedDictionary, PrettyJson)!.AsObject();
        var finalEntries = new JsonArray();
        foreach (var entry in approvedDictionary.Entries)
        {
            if (baselineByKey.TryGetValue(entry.En, out var original))
                finalEntries.Add(original.DeepClone());
            else
                finalEntries.Add(JsonSerializer.SerializeToNode(entry, PrettyJson));
        }
        finalRoot["entries"] = finalEntries;
        var dictionaryBytes = JsonSerializer.SerializeToUtf8Bytes(finalRoot, PrettyJson);
        var dictionaryHash = Convert.ToHexStringLower(SHA256.HashData(dictionaryBytes));
        var reviewHash = TagWorkflow.HashFile(paths.Review);
        var catalogReport = ValidateCatalogInMemory(paths.Catalog, sourceSet, approvedDictionary.Entries, policy.MovieCount, policy.Production);
        if (catalogReport.SourceCatalogSha256 != catalogHash)
            throw new InvalidDataException("source catalog changed during compatibility validation");
        var compatibilitySerialized = JsonSerializer.SerializeToUtf8Bytes(catalogReport, PrettyJson);
        var provenance = BuildProvenance(baselineRoot, baselineEntries, review.Decisions, candidateHash, baselineHash, proposalHash, reviewHash);
        var manifest = new Manifest(
            "sr-latn-full-release-v1", "sr-latn-v1", catalogHash, sourceHash, candidateHash, baselineHash,
            proposalHash, reviewHash, dictionaryHash, runtimeEvidence.CheckpointSha256,
            Convert.ToHexStringLower(SHA256.HashData(qaSerialized)),
            Convert.ToHexStringLower(SHA256.HashData(compatibilitySerialized)),
            approvedDictionary.Translator.ModelId, approvedDictionary.Translator.Revision,
            approvedDictionary.Translator.TargetToken, approvedDictionary.Translator.RuntimeLockSha256,
            runtimeEvidence.RuntimeFingerprint, approvedDictionary.NormalizationVersion, catalogReport.MovieCount,
            catalogReport.TagOccurrenceCount, approvedDictionary.Entries.Count, baselineEntries.Count,
            proposalEntries.Count, review.Decisions.Count, approved.Count(entry => !baselineByKey.ContainsKey(entry.En) &&
                proposalByKey.TryGetValue(entry.En, out var initial) && entry.Sr != initial["sr"]!.GetValue<string>()));

        if (TagWorkflow.HashFile(paths.Candidate) != CandidateShaForPublication(candidateHash, policy) ||
            TagWorkflow.HashFile(paths.Baseline) != baselineHash || TagWorkflow.HashFile(paths.Proposals) != proposalHash ||
            TagWorkflow.HashFile(paths.SourceTags) != sourceFileHash || TagWorkflow.HashFile(paths.Catalog) != catalogHash ||
            TagWorkflow.HashFile(paths.Review) != reviewHash)
            throw new InvalidDataException("a frozen full-lock input changed during validation");

        AtomicDirectory.PublishNew(paths.Output, stage =>
        {
            File.WriteAllBytes(Path.Combine(stage, "tag-translations-sr.json"), dictionaryBytes);
            File.WriteAllText(Path.Combine(stage, "content.sha256"), dictionaryHash + "\n", StrictUtf8);
            File.WriteAllBytes(Path.Combine(stage, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, PrettyJson));
            File.WriteAllBytes(Path.Combine(stage, "review-provenance.json"), JsonSerializer.SerializeToUtf8Bytes(provenance, PrettyJson));
            File.WriteAllBytes(Path.Combine(stage, "qa-report.json"), qaSerialized);
            File.WriteAllBytes(Path.Combine(stage, "compatibility-report.json"), compatibilitySerialized);
        });
    }

    private static string CandidateShaForPublication(string actual, Policy policy) => policy.CandidateSha ?? actual;

    private static void CheckPinned(string actual, string? expected, string label)
    {
        if (expected is not null && !StringComparer.Ordinal.Equals(actual, expected))
            throw new InvalidDataException($"{label} SHA-256 does not match the locked review contract");
    }

    private static JsonObject ReadStrictJsonObject(string path, string label)
    {
        TagWorkflow.EnsureNoDuplicateJsonKeys(path);
        return JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject ??
            throw new InvalidDataException($"{label} root must be an object");
    }

    private static void ValidateCandidateRoot(JsonObject root, string sourceHash)
    {
        var keys = root.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var required = new HashSet<string>(["schemaVersion", "sourceTagsSha256", "translator", "normalizationVersion", "entries", "datasetRelease", "correctionProposalSha256"], StringComparer.Ordinal);
        if (!keys.SetEquals(required))
            throw new InvalidDataException("candidate root has missing or unknown properties");
        if (root["schemaVersion"]?.GetValue<string>() != "tag-translations-sr-v1" ||
            root["sourceTagsSha256"]?.GetValue<string>() != sourceHash ||
            root["normalizationVersion"]?.GetValue<string>() != TranslatorConstants.NormalizationVersion)
            throw new InvalidDataException("candidate schema/source/normalizer identity is incompatible");
        if (root["datasetRelease"]?.GetValue<string>() != "poc-v2" ||
            root["correctionProposalSha256"]?.GetValue<string>() != "f98e2468d047feebe9546b20e1e64c6d9a4ff46c6fa454075165352c3dcc1be1")
            throw new InvalidDataException("candidate baseline root provenance differs from the frozen artifact");
    }

    private static void ValidateBaselineRoot(JsonObject root)
    {
        var required = new HashSet<string>(["schemaVersion", "sourceTagsSha256", "translator", "normalizationVersion",
            "entries", "datasetRelease", "correctionProposalSha256"], StringComparer.Ordinal);
        if (!root.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(required) ||
            root["schemaVersion"]?.GetValue<string>() != "tag-translations-sr-v1" ||
            root["normalizationVersion"]?.GetValue<string>() != TranslatorConstants.NormalizationVersion ||
            root["datasetRelease"]?.GetValue<string>() != "poc-v2" ||
            root["correctionProposalSha256"]?.GetValue<string>() != "f98e2468d047feebe9546b20e1e64c6d9a4ff46c6fa454075165352c3dcc1be1")
            throw new InvalidDataException("baseline root schema or preserved provenance is incompatible");
    }

    private static void ValidateProposalRoot(JsonObject root, string subsetHash)
    {
        var expected = new[] { "schemaVersion", "sourceTagsSha256", "translator", "normalizationVersion", "entries" };
        if (!root.Select(pair => pair.Key).SequenceEqual(expected, StringComparer.Ordinal) ||
            root["schemaVersion"]?.GetValue<string>() != "tag-translations-sr-v1" ||
            root["sourceTagsSha256"]?.GetValue<string>() != subsetHash ||
            root["normalizationVersion"]?.GetValue<string>() != TranslatorConstants.NormalizationVersion)
            throw new InvalidDataException("proposal root does not match the frozen proposal contract");
    }

    private static List<JsonObject> ReadEntries(JsonObject root, string label, bool allowProvenance)
    {
        if (root["entries"] is not JsonArray array)
            throw new InvalidDataException($"{label} entries must be an array");
        var result = new List<JsonObject>(array.Count);
        foreach (var node in array)
        {
            if (node is not JsonObject item)
                throw new InvalidDataException($"{label} entry must be an object");
            var fields = item.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
            var required = new HashSet<string>(["en", "machine", "sr", "reviewStatus", "manualOverride"], StringComparer.Ordinal);
            if (allowProvenance)
            {
                if (!fields.IsSupersetOf(required) || fields.Except(required).Any(key => key is not ("previousAccepted" or "correctionProvenance")))
                    throw new InvalidDataException($"{label} entry contains unknown properties");
            }
            else if (!fields.SetEquals(required))
                throw new InvalidDataException($"{label} entry has missing or unknown properties");
            if (item["en"]?.GetValueKind() != JsonValueKind.String || item["machine"]?.GetValueKind() != JsonValueKind.String ||
                item["sr"]?.GetValueKind() != JsonValueKind.String || item["reviewStatus"]?.GetValueKind() != JsonValueKind.String ||
                item["manualOverride"]?.GetValueKind() != JsonValueKind.True && item["manualOverride"]?.GetValueKind() != JsonValueKind.False)
                throw new InvalidDataException($"{label} entry has an invalid field type");
            result.Add(item);
        }
        return result;
    }

    private static Dictionary<string, JsonObject> IndexEntries(IEnumerable<JsonObject> entries, string label)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = entry["en"]!.GetValue<string>();
            if (!map.TryAdd(key, entry))
                throw new InvalidDataException($"{label} contains duplicate English keys");
        }
        return map;
    }

    private static void ValidateExactCoverage(HashSet<string> source, Dictionary<string, JsonObject> baseline,
        Dictionary<string, JsonObject> proposals, Dictionary<string, JsonObject> candidate)
    {
        if (baseline.Keys.Any(key => !source.Contains(key)) || proposals.Keys.Any(key => !source.Contains(key)) ||
            !candidate.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(source))
            throw new InvalidDataException("baseline/candidate keys are not exact members of the full source");
        if (baseline.Keys.Intersect(proposals.Keys, StringComparer.Ordinal).Any() ||
            !baseline.Keys.Concat(proposals.Keys).ToHashSet(StringComparer.Ordinal).SetEquals(source))
            throw new InvalidDataException("575 proposals are not the exact missing-key complement of the baseline");
    }

    private static void PreserveBaselineExactly(Dictionary<string, JsonObject> baseline, Dictionary<string, JsonObject> candidate)
    {
        foreach (var (key, value) in baseline)
        {
            if (!candidate.TryGetValue(key, out var actual) || !JsonNode.DeepEquals(value, actual))
                throw new InvalidDataException("full candidate modifies a protected baseline entry");
        }
    }

    private static TranslationDictionary DeserializeDictionary(JsonObject root, IReadOnlyList<JsonObject> rawEntries)
    {
        var entries = rawEntries.Select(item => new TranslationEntry(
            item["en"]!.GetValue<string>(), item["machine"]!.GetValue<string>(), item["sr"]!.GetValue<string>(),
            item["reviewStatus"]!.GetValue<string>(), item["manualOverride"]!.GetValue<bool>())).ToArray();
        var translator = root["translator"]?.Deserialize<TranslatorMetadata>() ??
            throw new InvalidDataException("translator identity is missing");
        return new TranslationDictionary(root["schemaVersion"]!.GetValue<string>(), root["sourceTagsSha256"]!.GetValue<string>(),
            translator, root["normalizationVersion"]!.GetValue<string>(), entries);
    }

    private static void ValidateModelIdentity(TranslationDictionary baseline, TranslationDictionary proposals, TranslationDictionary candidate)
    {
        if (!SameModel(baseline.Translator, proposals.Translator) || !SameModel(baseline.Translator, candidate.Translator))
            throw new InvalidDataException("baseline, proposals, and candidate have incompatible model/runtime identity");
        if (baseline.Translator.ModelId != TranslatorConstants.ModelId ||
            baseline.Translator.Revision != TranslatorConstants.ModelRevision ||
            baseline.Translator.TargetToken != TranslatorConstants.TargetToken ||
            baseline.Translator.Decoding != new DecodingMetadata(false, 4, 32, 512, "cpu"))
            throw new InvalidDataException("translation metadata is outside the pinned model contract");
    }

    private static bool SameModel(TranslatorMetadata left, TranslatorMetadata right) =>
        left.ModelId == right.ModelId && left.Revision == right.Revision && left.TargetToken == right.TargetToken &&
        left.RuntimeLockSha256 == right.RuntimeLockSha256 && left.ArtifactHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SequenceEqual(right.ArtifactHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal)) && left.Decoding == right.Decoding;

    private static void ValidateProposalSubsetHash(TranslationDictionary proposals, string expectedHash)
    {
        var subsetHash = TagWorkflow.HashCanonical(proposals.Entries.Select(entry => entry.En).ToArray());
        if (subsetHash != expectedHash)
            throw new InvalidDataException("proposal keys do not match the frozen 575-key subset");
    }

    private static void ValidateProposalAgreement(Dictionary<string, JsonObject> candidate, Dictionary<string, JsonObject> proposals)
    {
        foreach (var (key, proposal) in proposals)
        {
            var row = candidate[key];
            if (row["machine"]!.GetValue<string>() != proposal["machine"]!.GetValue<string>() ||
                row["sr"]!.GetValue<string>() != proposal["sr"]!.GetValue<string>())
                throw new InvalidDataException("candidate changes a pinned proposal machine/text value");
        }
    }

    private static ReviewDocument ParseReview(JsonObject root)
    {
        var expected = new[] { "schemaVersion", "candidateSha256", "baselineSha256", "proposalsSha256", "sourceTagsSha256", "decisions" };
        if (!root.Select(pair => pair.Key).SequenceEqual(expected, StringComparer.Ordinal) ||
            RequiredString(root, "schemaVersion") != ReviewSchema || root["decisions"] is not JsonArray decisions)
            throw new InvalidDataException("MAIN review has invalid schema or fields");
        var rows = new List<ReviewDecision>(decisions.Count);
        foreach (var node in decisions)
        {
            if (node is not JsonObject item || !item.Select(pair => pair.Key).SequenceEqual(
                    ["en", "approvedSr", "disposition", "rationale", "confidence"], StringComparer.Ordinal))
                throw new InvalidDataException("MAIN review decision has missing, unknown, or reordered fields");
            rows.Add(new ReviewDecision(RequiredString(item, "en"), RequiredString(item, "approvedSr"),
                RequiredString(item, "disposition"), RequiredString(item, "rationale"), RequiredString(item, "confidence")));
        }
        return new ReviewDocument(RequiredString(root, "schemaVersion"), RequiredString(root, "candidateSha256"),
            RequiredString(root, "baselineSha256"), RequiredString(root, "proposalsSha256"),
            RequiredString(root, "sourceTagsSha256"), rows);
    }

    private static string RequiredString(JsonObject value, string name) =>
        value[name] is JsonValue node && node.TryGetValue<string>(out var text) ?
            text : throw new InvalidDataException($"review property '{name}' must be a string");

    private static void ValidateReviewHashes(ReviewDocument review, string candidate, string baseline, string proposals, string source)
    {
        if (review.CandidateSha256 != candidate || review.BaselineSha256 != baseline ||
            review.ProposalsSha256 != proposals || review.SourceTagsSha256 != source)
            throw new InvalidDataException("MAIN review input hashes do not match current frozen artifacts");
    }

    private static HashSet<string> FindProperNameRisks(IEnumerable<TranslationEntry> newEntries, string catalogPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(catalogPath, StrictUtf8))
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            foreach (var property in new[] { "title", "rawTitle", "directedByRaw" })
                if (row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    names.Add(value.GetString()!);
            if (row.TryGetProperty("starringRaw", out var cast) && cast.ValueKind == JsonValueKind.String)
                foreach (var person in cast.GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    names.Add(person);
        }
        return newEntries.Where(entry => names.Contains(entry.En) &&
                (!SameNameIgnoringCaseAndSpacing(entry.En, entry.Machine) ||
                 !SameNameIgnoringCaseAndSpacing(entry.En, entry.Sr)))
            .Select(entry => entry.En).ToHashSet(StringComparer.Ordinal);
    }

    private static bool SameNameIgnoringCaseAndSpacing(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(Regex.Replace(left, @"\s+", string.Empty, RegexOptions.CultureInvariant),
            Regex.Replace(right, @"\s+", string.Empty, RegexOptions.CultureInvariant));

    private static Dictionary<string, ReviewDecision> ValidateDecisions(IReadOnlyList<ReviewDecision> decisions,
        HashSet<string> source, Dictionary<string, JsonObject> baseline, Dictionary<string, JsonObject> proposals,
        HashSet<string> flagged)
    {
        var result = new Dictionary<string, ReviewDecision>(StringComparer.Ordinal);
        foreach (var decision in decisions)
        {
            if (!source.Contains(decision.En))
                throw new InvalidDataException("MAIN review contains an unknown English key");
            if (!result.TryAdd(decision.En, decision))
                throw new InvalidDataException("MAIN review contains duplicate English keys");
            if (!DecisionDispositions.Contains(decision.Disposition) || !ConfidenceValues.Contains(decision.Confidence) ||
                string.IsNullOrWhiteSpace(decision.Rationale))
                throw new InvalidDataException("MAIN review disposition, rationale, or confidence is invalid");
            ValidateApprovedSerbian(decision.ApprovedSr);
            if (baseline.TryGetValue(decision.En, out var old))
            {
                if (decision.Disposition != "retained_baseline" || decision.ApprovedSr != old["sr"]!.GetValue<string>())
                    throw new InvalidDataException("baseline decisions must explicitly retain the exact accepted Serbian value");
            }
            else
            {
                if (!proposals.TryGetValue(decision.En, out var proposal))
                    throw new InvalidDataException("MAIN review key is outside the new proposal subset");
                if (decision.Disposition == "retained_baseline")
                    throw new InvalidDataException("retained_baseline disposition is valid only for pinned baseline keys");
                if (decision.Disposition == "accepted_unchanged" &&
                    decision.ApprovedSr != proposal["sr"]!.GetValue<string>())
                    throw new InvalidDataException("accepted_unchanged must retain the exact initial proposal");
            }
        }
        var missing = flagged.Where(key => !result.ContainsKey(key)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException("MAIN review does not explicitly disposition every flagged/context key");
        var missingBaseline = baseline.Keys.Where(key => !result.ContainsKey(key)).ToArray();
        if (missingBaseline.Length > 0)
            throw new InvalidDataException("MAIN review must explicitly retain every pinned baseline key");
        return result;
    }

    private static void ValidateApprovedSerbian(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != SerbianLatinNormalizer.Normalize(value))
            throw new InvalidDataException("approved Serbian must be nonblank and already NFC/whitespace normalized");
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var scalar = rune.Value;
            if (Rune.IsLetter(rune))
            {
                if (!(scalar is >= 0x0041 and <= 0x007A || scalar is >= 0x00C0 and <= 0x024F ||
                      scalar is >= 0x1E00 and <= 0x1EFF || scalar is >= 0xAB30 and <= 0xAB6F))
                    throw new InvalidDataException("approved Serbian contains a non-Latin letter");
            }
            else if (!(category is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.SpaceSeparator or
                       UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or
                       UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or
                       UnicodeCategory.OtherPunctuation))
                throw new InvalidDataException("approved Serbian contains a control, symbol, or unsupported character");
        }
    }

    private static TranslationEntry[] ApplyDecisions(Dictionary<string, JsonObject> candidate,
        Dictionary<string, JsonObject> baseline, Dictionary<string, JsonObject> proposals,
        Dictionary<string, ReviewDecision> decisions)
    {
        return candidate.Values.Select(raw =>
        {
            var en = raw["en"]!.GetValue<string>();
            var initial = new TranslationEntry(en, raw["machine"]!.GetValue<string>(), raw["sr"]!.GetValue<string>(),
                raw["reviewStatus"]!.GetValue<string>(), raw["manualOverride"]!.GetValue<bool>());
            if (baseline.ContainsKey(en))
                return initial;
            if (decisions.TryGetValue(en, out var decision))
                return initial with { Sr = decision.ApprovedSr, ReviewStatus = "reviewed",
                    ManualOverride = initial.ManualOverride || decision.ApprovedSr != proposals[en]["sr"]!.GetValue<string>() };
            return initial with { ReviewStatus = "auto_pass", ManualOverride = false };
        }).ToArray();
    }

    private static Compatibility ValidateCatalogInMemory(string catalogPath, HashSet<string> source,
        IReadOnlyList<TranslationEntry> entries, int expectedMovieCount, bool production)
    {
        var translations = entries.ToDictionary(entry => entry.En, entry => entry.Sr, StringComparer.Ordinal);
        using var projectionHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var movies = 0;
        var occurrences = 0;
        var mapped = 0;
        foreach (var line in File.ReadLines(catalogPath, StrictUtf8))
        {
            if (string.IsNullOrWhiteSpace(line))
                throw new InvalidDataException("source catalog contains an empty row");
            var original = JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException("source catalog row is not an object");
            if (original["relevantTags"] is not JsonArray tags)
                throw new InvalidDataException("source catalog row is missing relevantTags");
            var mappedMovie = BuildPhase8Projection(original, translations);
            if (production && (original.Count != 17 || mappedMovie.Count != 19))
                throw new InvalidDataException("source catalog must retain its 17-field shape and append exactly tagsSr/semanticTextSr");
            for (var index = 0; index < tags.Count; index++)
            {
                if (tags[index] is not JsonObject tag || tag["name"] is not JsonValue nameNode ||
                    !nameNode.TryGetValue<string>(out var name) || tag["score"] is null || tag.ContainsKey("serbianName"))
                    throw new InvalidDataException("source relevantTag must retain a string name and score");
                occurrences++;
                if (!source.Contains(name) || !translations.ContainsKey(name))
                    throw new InvalidDataException("source catalog contains a tag without an exact translation mapping");
                mapped++;
            }
            foreach (var field in original)
                if (!JsonNode.DeepEquals(field.Value, mappedMovie[field.Key]))
                    throw new InvalidDataException("in-memory mapping altered source movie metadata");
            if (mappedMovie["tagsSr"] is not JsonArray srTags || srTags.Count != tags.Count ||
                srTags.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)))
                throw new InvalidDataException("in-memory Serbian tags do not preserve source order/count or contain blank values");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(mappedMovie, CompactJson);
            projectionHash.AppendData(bytes);
            projectionHash.AppendData([0x0A]);
            movies++;
        }
        if (movies != expectedMovieCount || (production && movies != 9730) || occurrences != mapped)
            throw new InvalidDataException("source catalog row/order/tag-occurrence compatibility check failed");
        return new Compatibility("sr-phase-07-catalog-compatibility-v2", TagWorkflow.HashFile(catalogPath),
            movies, occurrences, mapped, 0, Convert.ToHexStringLower(projectionHash.GetHashAndReset()),
            true, true, true, true, source.Count, 17, 19, true);
    }

    internal static JsonObject BuildPhase8Projection(JsonObject original, IReadOnlyDictionary<string, string> translations)
    {
        if (original["relevantTags"] is not JsonArray tags || original["title"] is not JsonValue titleNode ||
            !titleNode.TryGetValue<string>(out var title) || original["year"] is not JsonValue yearNode ||
            !yearNode.TryGetValue<int>(out var year))
            throw new InvalidDataException("source movie is missing title, year, or relevantTags");
        var srTags = new JsonArray();
        var tagValues = new List<string>(tags.Count);
        foreach (var node in tags)
        {
            if (node is not JsonObject tag || tag["name"] is not JsonValue nameNode ||
                !nameNode.TryGetValue<string>(out var name) || tag["score"] is null || tag.ContainsKey("serbianName") ||
                !translations.TryGetValue(name, out var translated) || string.IsNullOrWhiteSpace(translated))
                throw new InvalidDataException("source relevantTag must have a mapped name and score");
            srTags.Add(translated);
            tagValues.Add(translated);
        }
        var mappedMovie = (JsonObject)original.DeepClone();
        mappedMovie["tagsSr"] = srTags;
        mappedMovie["semanticTextSr"] = FormatSemanticTextSr(title, year,
            RawName(original["directedByRaw"]), RawName(original["starringRaw"]), tagValues);
        if (mappedMovie["semanticTextSr"] is not JsonValue semanticNode ||
            !semanticNode.TryGetValue<string>(out var semanticText) || string.IsNullOrWhiteSpace(semanticText) ||
            semanticText != FormatSemanticTextSr(title, year, RawName(original["directedByRaw"]),
                RawName(original["starringRaw"]), tagValues))
            throw new InvalidDataException("in-memory Serbian semantic text failed the approved template validation");
        if (mappedMovie.Count != original.Count + 2 || !JsonNode.DeepEquals(original["relevantTags"], mappedMovie["relevantTags"]))
            throw new InvalidDataException("in-memory mapping altered source tags or failed to append exactly two fields");
        return mappedMovie;
    }

    private static string FormatSemanticTextSr(string title, int year, string director, string cast, IReadOnlyList<string> tags)
    {
        var tagText = tags.Count == 0 ? "nema" : string.Join(", ", tags);
        return $"Naslov: {title}. Godina: {year.ToString(CultureInfo.InvariantCulture)}. Režija: {director}. Glumci: {cast}. Tagovi: {tagText}.";
    }

    private static string RawName(JsonNode? node) => node is not JsonValue value || !value.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name)
        ? "nepoznato" : name;

    private static JsonNode BuildBaselineMetadata(JsonObject root)
    {
        var metadata = (JsonObject)root.DeepClone();
        metadata.Remove("entries");
        return metadata;
    }

    private static Provenance BuildProvenance(JsonObject baselineRoot, IReadOnlyList<JsonObject> baselineEntries,
        IReadOnlyList<ReviewDecision> decisions, string candidateHash, string baselineHash, string proposalHash, string reviewHash)
    {
        var entries = new JsonArray();
        foreach (var entry in baselineEntries)
            entries.Add(entry.DeepClone());
        return new Provenance("sr-phase-07-review-provenance-v1", candidateHash, baselineHash, proposalHash, reviewHash,
            BuildBaselineMetadata(baselineRoot), entries,
            decisions.OrderBy(item => item.En, StringComparer.Ordinal).ToArray());
    }

    private static RuntimeEvidence ValidateProposalRuntimeEvidence(string proposalPath, TranslationDictionary proposals, int expectedProposalCount)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(proposalPath))!;
        var summaryPath = Path.Combine(directory, "proposal-summary.json");
        TagWorkflow.RequireFile(summaryPath, "proposal summary");
        var summary = ReadStrictJsonObject(summaryPath, "proposal summary");
        var fingerprint = summary["runtimeFingerprint"]?.GetValue<string>();
        if (summary["count"]?.GetValue<int>() != expectedProposalCount || summary["reused"]?.GetValue<int>() != 0 ||
            summary["inferred"]?.GetValue<int>() != expectedProposalCount || string.IsNullOrWhiteSpace(fingerprint))
            throw new InvalidDataException("proposal summary does not prove the complete non-reused proposal batch");

        var proposalDirectory = Directory.GetParent(directory)?.FullName ??
            throw new InvalidDataException("proposal dictionary is not in a batch output directory");
        var checkpointPath = Path.Combine(proposalDirectory, "remaining-575.jsonl");
        TagWorkflow.RequireFile(checkpointPath, "complete proposal checkpoint");
        var checkpointBytes = File.ReadAllBytes(checkpointPath);
        var checkpointText = StrictUtf8.GetString(checkpointBytes);
        if (checkpointText.Length == 0 || checkpointText[^1] != '\n')
            throw new InvalidDataException("proposal checkpoint has an incomplete final record");
        var lines = checkpointText.Split('\n');
        if (lines.Length != expectedProposalCount + 2 || lines[^1].Length != 0)
            throw new InvalidDataException("proposal checkpoint row count differs from the exact proposal set");

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using var headerDocument = JsonDocument.Parse(lines[0]);
        var header = headerDocument.RootElement;
        if (!HasExactProperties(header, "kind", "identitySha256", "identity") ||
            header.GetProperty("kind").GetString() != "header")
            throw new InvalidDataException("proposal checkpoint header is malformed");
        ValidateIdentityShape(header.GetProperty("identity"));
        var identity = JsonSerializer.Deserialize<ProposalIdentity>(header.GetProperty("identity"), jsonOptions) ??
            throw new InvalidDataException("proposal checkpoint runtime identity is invalid");
        var identityHash = header.GetProperty("identitySha256").GetString();
        if (identityHash != identity.Sha256 || identity.RuntimeFingerprint != fingerprint ||
            identity.RuntimeLockSha256 != proposals.Translator.RuntimeLockSha256 ||
            identity.ModelId != proposals.Translator.ModelId || identity.Revision != proposals.Translator.Revision ||
            identity.TargetToken != proposals.Translator.TargetToken || identity.NormalizationVersion != TranslatorConstants.NormalizationVersion ||
            identity.Decoding != proposals.Translator.Decoding ||
            !identity.ArtifactHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SequenceEqual(proposals.Translator.ArtifactHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
            throw new InvalidDataException("proposal checkpoint identity differs from the dictionary/runtime summary");

        var expected = proposals.Entries.ToDictionary(entry => entry.En, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1).Take(expectedProposalCount))
        {
            using var rowDocument = JsonDocument.Parse(line);
            var row = rowDocument.RootElement;
            if (!HasExactProperties(row, "kind", "identitySha256", "identity", "proposalSha256", "proposal") ||
                row.GetProperty("kind").GetString() != "proposal" ||
                row.GetProperty("identitySha256").GetString() != identityHash)
                throw new InvalidDataException("proposal checkpoint row identity is malformed");
            ValidateIdentityShape(row.GetProperty("identity"));
            var rowIdentity = JsonSerializer.Deserialize<ProposalIdentity>(row.GetProperty("identity"), jsonOptions) ??
                throw new InvalidDataException("proposal checkpoint row runtime identity is invalid");
            if (rowIdentity.Sha256 != identityHash)
                throw new InvalidDataException("proposal checkpoint row uses an incompatible runtime identity");
            var proposal = JsonSerializer.Deserialize<CheckpointProposal>(row.GetProperty("proposal"), jsonOptions) ??
                throw new InvalidDataException("proposal checkpoint payload is malformed");
            var expectedRowHash = ComputeCheckpointProposalHash(identityHash!, rowIdentity, proposal, jsonOptions);
            if (row.GetProperty("proposalSha256").GetString() != expectedRowHash ||
                !proposal.Completed || proposal.Error is not null || string.IsNullOrWhiteSpace(proposal.Machine) ||
                string.IsNullOrWhiteSpace(proposal.Sr) || !seen.Add(proposal.En) ||
                !expected.TryGetValue(proposal.En, out var dictionaryEntry) ||
                dictionaryEntry.Machine != proposal.Machine || dictionaryEntry.Sr != proposal.Sr ||
                SerbianLatinNormalizer.Normalize(proposal.Machine) != proposal.Sr)
                throw new InvalidDataException("proposal checkpoint has a failed, duplicate, altered, or unexpected output row");
        }
        if (!seen.SetEquals(expected.Keys))
            throw new InvalidDataException("proposal checkpoint does not contain the exact completed English key set");
        return new RuntimeEvidence(fingerprint, Convert.ToHexStringLower(SHA256.HashData(checkpointBytes)));
    }

    private static void ValidateIdentityShape(JsonElement identity)
    {
        if (!HasExactProperties(identity, "modelId", "revision", "targetToken", "decoding", "runtimeLockSha256",
                "runtimeFingerprint", "normalizationVersion", "artifactHashes"))
            throw new InvalidDataException("proposal checkpoint identity has unknown or missing fields");
        if (!HasExactProperties(identity.GetProperty("decoding"), "doSample", "numBeams", "maxNewTokens", "sourceMaxTokens", "device"))
            throw new InvalidDataException("proposal checkpoint decoding identity has unknown or missing fields");
    }

    private static bool HasExactProperties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == expected.Length && names.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    private static string ComputeCheckpointProposalHash(string identityHash, ProposalIdentity identity,
        CheckpointProposal proposal, JsonSerializerOptions options)
    {
        var payload = new { identitySha256 = identityHash, identity, proposal };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, options)));
    }

}
