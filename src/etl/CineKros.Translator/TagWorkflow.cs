using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.TextNormalization;

namespace CineKros.Translator;

public static class TagWorkflow
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IReadOnlyList<string> ReadTagArray(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var tags = JsonSerializer.Deserialize<string[]>(bytes) ?? throw new InvalidDataException("tag input must be a JSON string array");
        if (tags.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("tag input contains a blank key");
        var distinct = tags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (distinct.Length != tags.Length)
            throw new InvalidDataException("tag input contains duplicate exact keys");
        return distinct;
    }

    public static void Extract(string catalogPath, string outputDirectory)
    {
        RequireFile(catalogPath, "catalog");
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(catalogPath, StrictUtf8))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var row = JsonDocument.Parse(line);
            if (!row.RootElement.TryGetProperty("relevantTags", out var tagList) || tagList.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("catalog row must contain a relevantTags array");
            foreach (var tag in tagList.EnumerateArray())
            {
                if (tag.ValueKind != JsonValueKind.Object || !tag.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("each relevantTags item must contain a string name");
                var value = name.GetString()!;
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidDataException("catalog contains a blank tag name");
                tags.Add(value);
            }
        }

        if (tags.Count == 0)
            throw new InvalidDataException("catalog contains no relevant tags");
        var ordered = tags.ToArray();
        var hash = HashCanonical(ordered);
        AtomicDirectory.PublishNew(outputDirectory, stage =>
        {
            WriteJson(Path.Combine(stage, "source-tags.json"), ordered);
            WriteJson(Path.Combine(stage, "source-tags-manifest.json"), new { schemaVersion = "translator-source-tags-v1", sourceTagsSha256 = hash, count = ordered.Length });
        });
    }

    public static async Task ProposeAsync(CommandArguments arguments, CancellationToken cancellationToken)
    {
        RequireFile(arguments["tags"], "tags");
        if (!File.Exists(arguments["python"]))
            throw new FileNotFoundException("Python interpreter was not found", arguments["python"]);
        if (!Directory.Exists(arguments["model-dir"]))
            throw new DirectoryNotFoundException("pinned model directory was not found");

        var tags = ReadTagArray(arguments["tags"]);
        var maximum = arguments.Values.TryGetValue("max-items", out var cap) ? int.Parse(cap, System.Globalization.CultureInfo.InvariantCulture) : 6;
        var approvedSmoke = TranslatorConstants.SmokePhrases.Order(StringComparer.Ordinal).ToArray();
        if (tags.Count > 6 || tags.Except(approvedSmoke, StringComparer.Ordinal).Any())
            throw new InvalidDataException("real model inference is limited to the six frozen Phase 2 smoke phrases");
        if (maximum < tags.Count)
            tags = tags.Take(maximum).ToArray();

        var model = ModelManifest.Load(arguments["model-dir"]);
        var projectRoot = FindProjectRoot();
        var runtimeLock = Path.Combine(projectRoot, "locks", "requirements.lock");
        RequireFile(runtimeLock, "runtime lock");
        var lockHash = HashFile(runtimeLock);
        var runtimeIdentity = RuntimeManifest.Verify(arguments["python"], runtimeLock, Path.Combine(projectRoot, "bootstrap", "validate_runtime.py"));
        var pythonRunner = Path.Combine(projectRoot, "python", "translate.py");
        RequireFile(pythonRunner, "Python runner");
        var identity = ProposalIdentity.Create(model, lockHash, runtimeIdentity.RuntimeFingerprint);
        var checkpoint = ProposalCheckpoint.Open(arguments["checkpoint"], identity);
        var jobs = tags.Where(tag => !checkpoint.Completed.ContainsKey(tag)).Select(tag =>
            new ProtocolJob(tag, tag, TranslatorConstants.TargetToken, TranslatorConstants.DecodingId)).ToArray();

        if (jobs.Length != 0)
        {
            await PythonTranslationProcess.RunAsync(arguments["python"], pythonRunner, arguments["model-dir"], jobs,
                async (response, token) =>
                {
                    if (!response.Completed)
                    {
                        await checkpoint.AppendAsync(new CheckpointProposal(response.En, response.Machine ?? string.Empty, string.Empty, false, response.Error), token);
                        return;
                    }
                    var machine = response.Machine!;
                    var normalized = SerbianLatinNormalizer.Normalize(machine);
                    await checkpoint.AppendAsync(new CheckpointProposal(response.En, machine, normalized, true, null), token);
                }, cancellationToken);
        }

        if (tags.Any(tag => !checkpoint.Completed.ContainsKey(tag)))
            throw new InvalidDataException("one or more model outputs were incomplete; failed keys remain retryable in the checkpoint");

        var entries = tags.Select(tag =>
        {
            var proposal = checkpoint.Completed[tag];
            return new TranslationEntry(tag, proposal.Machine, proposal.Sr,
                proposal.Completed ? "auto_pass" : "review_required", false);
        }).ToArray();
        var dictionary = new TranslationDictionary("tag-translations-sr-v1", HashCanonical(tags),
            new TranslatorMetadata(TranslatorConstants.ModelId, TranslatorConstants.ModelRevision, TranslatorConstants.TargetToken,
                new SortedDictionary<string, string>(model.ArtifactHashes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal), lockHash,
                new DecodingMetadata(false, TranslatorConstants.NumBeams, TranslatorConstants.MaxNewTokens, TranslatorConstants.MaxSourceTokens, "cpu")),
            TranslatorConstants.NormalizationVersion, entries);
        dictionary = TranslationQa.MarkReviewStatuses(dictionary);
        AtomicDirectory.PublishNew(arguments["output-dir"], stage =>
        {
            WriteJson(Path.Combine(stage, "proposal-dictionary.json"), dictionary);
            WriteJson(Path.Combine(stage, "proposal-summary.json"), new { schemaVersion = "translator-proposal-v1", sourceTagsSha256 = dictionary.SourceTagsSha256, runtimeFingerprint = runtimeIdentity.RuntimeFingerprint, count = entries.Length, autoPass = dictionary.Entries.Count(entry => entry.ReviewStatus == "auto_pass"), reviewRequired = dictionary.Entries.Count(entry => entry.ReviewStatus == "review_required") });
        });
    }

    public static void Qa(string dictionaryPath, string outputDirectory)
    {
        var dictionary = ReadDictionary(dictionaryPath);
        var report = TranslationQa.Analyze(dictionary);
        var marked = TranslationQa.MarkReviewStatuses(dictionary);
        AtomicDirectory.PublishNew(outputDirectory, stage =>
        {
            WriteJson(Path.Combine(stage, "qa-report.json"), report);
            WriteJson(Path.Combine(stage, "qa-marked-dictionary.json"), marked);
        });
    }

    public static void ApplyReview(string dictionaryPath, string reviewPath, string outputDirectory)
    {
        var dictionary = ReadDictionary(dictionaryPath);
        RequireFile(reviewPath, "review");
        EnsureNoDuplicateJsonKeys(reviewPath);
        var root = JsonNode.Parse(File.ReadAllBytes(reviewPath)) as JsonObject ?? throw new InvalidDataException("review must be a JSON object");
        EnsureUniqueProperties(root);
        if (!root.TryGetPropertyValue("decisions", out var decisionsNode) || decisionsNode is not JsonArray decisions || root.Count != 1)
            throw new InvalidDataException("review must contain only a decisions array");

        var sourceEntries = dictionary.Entries.ToDictionary(entry => entry.En, StringComparer.Ordinal);
        var updated = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in decisions)
        {
            var decision = node as JsonObject ?? throw new InvalidDataException("review decisions must be objects");
            EnsureUniqueProperties(decision);
            if (decision.Count != 2 || !decision.TryGetPropertyValue("en", out var enNode) || !decision.TryGetPropertyValue("approvedSr", out var srNode))
                throw new InvalidDataException("each review decision requires exactly en and approvedSr");
            var en = enNode?.GetValue<string>() ?? throw new InvalidDataException("review en must be a string");
            var sr = srNode?.GetValue<string>() ?? throw new InvalidDataException("approvedSr must be a string");
            if (!sourceEntries.ContainsKey(en))
                throw new InvalidDataException($"review contains unknown key '{en}'");
            if (!updated.TryAdd(en, sr))
                throw new InvalidDataException($"review contains duplicate key '{en}'");
        }
        if (updated.Count == 0)
            throw new InvalidDataException("review must approve at least one explicit key");

        var entries = dictionary.Entries.Select(entry =>
        {
            if (!updated.TryGetValue(entry.En, out var approved))
                return entry;
            var normalized = SerbianLatinNormalizer.Normalize(approved);
            if (string.IsNullOrWhiteSpace(normalized))
                throw new InvalidDataException($"review contains a blank accepted value for '{entry.En}'");
            return entry with { Sr = normalized, ReviewStatus = "reviewed", ManualOverride = entry.ManualOverride || !StringComparer.Ordinal.Equals(normalized, entry.Sr) };
        }).ToArray();
        var result = dictionary with { Entries = entries };
        var sourceReview = File.ReadAllBytes(reviewPath);
        var previousEvidencePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dictionaryPath))!, "review-evidence.json");
        var evidence = new
        {
            schemaVersion = "translator-review-evidence-v1",
            previousEvidenceSha256 = File.Exists(previousEvidencePath) ? HashFile(previousEvidencePath) : null,
            sourceReviewSha256 = Convert.ToHexStringLower(SHA256.HashData(sourceReview)),
            decisions = updated.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { en = pair.Key, approvedSr = SerbianLatinNormalizer.Normalize(pair.Value) }).ToArray()
        };
        AtomicDirectory.PublishNew(outputDirectory, stage =>
        {
            WriteJson(Path.Combine(stage, "reviewed-dictionary.json"), result);
            File.WriteAllBytes(Path.Combine(stage, "review-source.json"), sourceReview);
            WriteJson(Path.Combine(stage, "review-evidence.json"), evidence);
        });
    }

    public static void Lock(string dictionaryPath, string sourceTagsPath, string outputDirectory)
    {
        var dictionary = ReadDictionary(dictionaryPath);
        RequireFile(sourceTagsPath, "source tags");
        var sourceTags = ReadTagArray(sourceTagsPath);
        var approvedSmoke = TranslatorConstants.SmokePhrases.Order(StringComparer.Ordinal).ToArray();
        if (sourceTags.Count > 6 || sourceTags.Except(approvedSmoke, StringComparer.Ordinal).Any())
            throw new InvalidDataException("Phase 2 lock output is limited to the six frozen smoke phrases");
        var expectedHash = HashCanonical(sourceTags);
        if (!StringComparer.Ordinal.Equals(expectedHash, dictionary.SourceTagsSha256))
            throw new InvalidDataException("dictionary sourceTagsSha256 does not match source tags");
        var actual = dictionary.Entries.Select(entry => entry.En).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(sourceTags, StringComparer.Ordinal))
            throw new InvalidDataException("dictionary keys do not exactly match source tags");
        if (dictionary.Entries.Any(entry => string.IsNullOrWhiteSpace(entry.Sr) || entry.ReviewStatus == "review_required"))
            throw new InvalidDataException("dictionary has blank or unresolved entries");
        if (dictionary.Entries.Select(entry => entry.En).Distinct(StringComparer.Ordinal).Count() != dictionary.Entries.Count)
            throw new InvalidDataException("dictionary contains duplicate keys");
        var flagged = TranslationQa.Analyze(dictionary).Entries.Where(entry => entry.Flags.Count != 0)
            .Select(entry => entry.En).ToHashSet(StringComparer.Ordinal);
        if (dictionary.Entries.Any(entry => flagged.Contains(entry.En) && entry.ReviewStatus != "reviewed"))
            throw new InvalidDataException("QA-flagged entries require explicit reviewed status before lock");

        var ordered = dictionary with { Entries = dictionary.Entries.OrderBy(entry => entry.En, StringComparer.Ordinal).ToArray() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ordered);
        var contentHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        AtomicDirectory.PublishNew(outputDirectory, stage =>
        {
            File.WriteAllBytes(Path.Combine(stage, "tag-translations-sr.json"), bytes);
            File.WriteAllText(Path.Combine(stage, "content.sha256"), contentHash + "\n", StrictUtf8);
        });
    }

    public static TranslationDictionary ReadDictionary(string path)
    {
        RequireFile(path, "dictionary");
        EnsureNoDuplicateJsonKeys(path);
        var root = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject ?? throw new InvalidDataException("dictionary root must be an object");
        EnsureUniqueProperties(root);
        var expectedRoot = new[] { "schemaVersion", "sourceTagsSha256", "translator", "normalizationVersion", "entries" };
        if (!root.Select(pair => pair.Key).SequenceEqual(expectedRoot, StringComparer.Ordinal))
            throw new InvalidDataException("dictionary root keys must be exact and in contract order");
        var entries = root["entries"] as JsonArray ?? throw new InvalidDataException("dictionary entries must be an array");
        foreach (var node in entries)
        {
            var item = node as JsonObject ?? throw new InvalidDataException("dictionary entry must be an object");
            EnsureUniqueProperties(item);
            if (!item.Select(pair => pair.Key).SequenceEqual(["en", "machine", "sr", "reviewStatus", "manualOverride"], StringComparer.Ordinal))
                throw new InvalidDataException("dictionary entry keys must be exact and in contract order");
        }

        if (root["translator"] is not JsonObject translator || !translator.Select(pair => pair.Key).SequenceEqual(
                ["modelId", "revision", "targetToken", "artifactHashes", "runtimeLockSha256", "decoding"], StringComparer.Ordinal))
            throw new InvalidDataException("translator metadata keys must be exact and in contract order");
        EnsureUniqueProperties(translator);
        if (translator["decoding"] is not JsonObject decoding || !decoding.Select(pair => pair.Key).SequenceEqual(
                ["doSample", "numBeams", "maxNewTokens", "sourceMaxTokens", "device"], StringComparer.Ordinal))
            throw new InvalidDataException("decoding metadata keys must be exact and in contract order");
        EnsureUniqueProperties(decoding);

        var dictionary = root.Deserialize<TranslationDictionary>() ?? throw new InvalidDataException("dictionary is invalid");
        if (dictionary.SchemaVersion != "tag-translations-sr-v1" || dictionary.Translator.ModelId != TranslatorConstants.ModelId ||
            dictionary.Translator.Revision != TranslatorConstants.ModelRevision || dictionary.Translator.TargetToken != TranslatorConstants.TargetToken)
            throw new InvalidDataException("dictionary model/schema identity does not match the locked Phase 2 contract");
        if (dictionary.Entries.Any(entry => entry.ReviewStatus is not ("auto_pass" or "review_required" or "reviewed")))
            throw new InvalidDataException("dictionary contains an unsupported review status");
        return dictionary;
    }

    public static string HashCanonical(IReadOnlyList<string> tags) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(tags.Order(StringComparer.Ordinal))));

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static void WriteJson<T>(string path, T value) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    internal static void RequireFile(string path, string label)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"{label} file was not found", path);
    }

    private static void EnsureUniqueProperties(JsonObject value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in value)
            if (!names.Add(pair.Key))
                throw new InvalidDataException($"duplicate JSON key '{pair.Key}'");
    }

    private static void EnsureNoDuplicateJsonKeys(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var pending = new Stack<JsonElement>();
        pending.Push(document.RootElement);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException($"JSON document contains duplicate key '{property.Name}'");
                    pending.Push(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    pending.Push(item);
            }
        }
    }

    private static string FindProjectRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate is not null && !File.Exists(Path.Combine(candidate.FullName, "CineKros.Translator.csproj")))
            candidate = candidate.Parent;
        return candidate?.FullName ?? throw new DirectoryNotFoundException("could not locate Translator project root");
    }
}

internal static class AtomicDirectory
{
    public static void PublishNew(string target, Action<string> produce)
    {
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("output path already exists; refusing overwrite");
        var parent = Path.GetDirectoryName(Path.GetFullPath(target)) ?? throw new IOException("output directory must have a parent");
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".translator-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            produce(stage);
            Directory.Move(stage, target);
        }
        catch
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            throw;
        }
    }
}
