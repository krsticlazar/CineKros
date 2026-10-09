using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.Translator;

namespace CineKros.Translator.Tests;

[TestClass]
public sealed class FullLockWorkflowTests
{
    [TestMethod]
    public void FullLockFixturePreservesBaselineProvenanceAndPublishesDeterministically()
    {
        using var fixture = new FullLockFixture();
        var first = fixture.NewOutput("release-a");
        var second = fixture.NewOutput("release-b");
        FullLockWorkflow.Lock(fixture.Paths(first), fixture.Policy);
        FullLockWorkflow.Lock(fixture.Paths(second), fixture.Policy);

        var files = new[] { "tag-translations-sr.json", "content.sha256", "manifest.json", "review-provenance.json", "qa-report.json", "compatibility-report.json" };
        foreach (var name in files)
            Assert.AreEqual(Hash(Path.Combine(first, name)), Hash(Path.Combine(second, name)), name);

        using var locked = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(first, "tag-translations-sr.json")));
        var root = locked.RootElement;
        Assert.AreEqual(5, root.EnumerateObject().Count());
        Assert.IsFalse(root.TryGetProperty("datasetRelease", out _));
        Assert.AreEqual(993, root.GetProperty("entries").GetArrayLength());
        var outputByKey = root.GetProperty("entries").EnumerateArray().ToDictionary(item => item.GetProperty("en").GetString()!, StringComparer.Ordinal);
        using var baseline = JsonDocument.Parse(File.ReadAllBytes(fixture.BaselinePath));
        foreach (var old in baseline.RootElement.GetProperty("entries").EnumerateArray())
        {
            var current = outputByKey[old.GetProperty("en").GetString()!];
            foreach (var property in new[] { "en", "machine", "sr", "reviewStatus", "manualOverride" })
                Assert.AreEqual(old.GetProperty(property).ToString(), current.GetProperty(property).ToString());
        }
        Assert.IsTrue(outputByKey["new003"].GetProperty("manualOverride").GetBoolean());
        Assert.AreEqual("izmenjen prevod 003", outputByKey["new003"].GetProperty("sr").GetString());
        using var originalBaseline = JsonDocument.Parse(File.ReadAllBytes(fixture.BaselinePath));
        foreach (var old in originalBaseline.RootElement.GetProperty("entries").EnumerateArray())
        {
            var current = outputByKey[old.GetProperty("en").GetString()!];
            foreach (var extra in new[] { "previousAccepted", "correctionProvenance" })
                if (old.TryGetProperty(extra, out var expected))
                    Assert.AreEqual(expected.ToString(), current.GetProperty(extra).ToString(), extra);
        }
        using var provenance = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(first, "review-provenance.json")));
        Assert.AreEqual(418, provenance.RootElement.GetProperty("baselineEntries").GetArrayLength());
        Assert.AreEqual(24, provenance.RootElement.GetProperty("baselineEntries").EnumerateArray()
            .Count(item => item.TryGetProperty("previousAccepted", out _) && item.TryGetProperty("correctionProvenance", out _)));
        Assert.AreEqual("poc-v2", provenance.RootElement.GetProperty("baselineSourceMetadata").GetProperty("datasetRelease").GetString());
        Assert.AreEqual(9730, JsonDocument.Parse(File.ReadAllBytes(Path.Combine(first, "compatibility-report.json")))
            .RootElement.GetProperty("movieCount").GetInt32());
        using var compatibility = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(first, "compatibility-report.json")));
        Assert.AreEqual("sr-phase-07-catalog-compatibility-v2", compatibility.RootElement.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(17, compatibility.RootElement.GetProperty("sourceMovieFieldCount").GetInt32());
        Assert.AreEqual(19, compatibility.RootElement.GetProperty("phase8MovieFieldCount").GetInt32());
        Assert.IsTrue(compatibility.RootElement.GetProperty("serbianTextValidated").GetBoolean());
        Assert.AreEqual(2, JsonDocument.Parse(File.ReadAllBytes(Path.Combine(first, "manifest.json")))
            .RootElement.GetProperty("correctedNewEntryCount").GetInt32());
    }

    [TestMethod]
    public void Phase8ProjectionAppendsRealSerbianTextAndPreservesAllEnglishFields()
    {
        var source = new JsonObject
        {
            ["movieLensId"] = 9, ["rawTitle"] = "Original title (2001)", ["title"] = "Original title", ["year"] = 2001,
            ["imdbId"] = "tt0000009", ["movieLensAvgRating"] = 4.2, ["directedByRaw"] = "Raw Director",
            ["starringRaw"] = "Raw Cast", ["relevantTags"] = new JsonArray(
                new JsonObject { ["name"] = "first", ["score"] = 0.9 },
                new JsonObject { ["name"] = "second", ["score"] = 0.7 }),
            ["semanticText"] = "English source text", ["tmdbId"] = 900, ["genres"] = new JsonArray("GenreMustNotLeak"),
            ["averageRating"] = 4.1, ["ratingCount"] = 12, ["runtimeMinutes"] = 101,
            ["originalLanguage"] = "en", ["posterPath"] = "/poster.jpg"
        };
        var sourceSnapshot = (JsonObject)source.DeepClone();

        var mapped = FullLockWorkflow.BuildPhase8Projection(source,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["first"] = "prvi", ["second"] = "drugi" });

        Assert.AreEqual(17, source.Count);
        Assert.AreEqual(19, mapped.Count);
        Assert.AreEqual("tagsSr", mapped.ElementAt(17).Key);
        Assert.AreEqual("semanticTextSr", mapped.ElementAt(18).Key);
        foreach (var field in sourceSnapshot)
            Assert.IsTrue(JsonNode.DeepEquals(field.Value, mapped[field.Key]), $"source field changed: {field.Key}");
        Assert.AreEqual("first", mapped["relevantTags"]![0]!["name"]!.GetValue<string>());
        Assert.AreEqual(0.9, mapped["relevantTags"]![0]!["score"]!.GetValue<double>());
        CollectionAssert.AreEqual(new[] { "prvi", "drugi" }, mapped["tagsSr"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.AreEqual("Naslov: Original title. Godina: 2001. Režija: Raw Director. Glumci: Raw Cast. Tagovi: prvi, drugi.",
            mapped["semanticTextSr"]!.GetValue<string>());
        Assert.IsFalse(mapped["semanticTextSr"]!.GetValue<string>().Contains("GenreMustNotLeak", StringComparison.Ordinal));

        source["directedByRaw"] = " ";
        source["starringRaw"] = null;
        source["relevantTags"] = new JsonArray();
        var emptyMapped = FullLockWorkflow.BuildPhase8Projection(source, new Dictionary<string, string>());
        Assert.AreEqual("Naslov: Original title. Godina: 2001. Režija: nepoznato. Glumci: nepoznato. Tagovi: nema.",
            emptyMapped["semanticTextSr"]!.GetValue<string>());
        Assert.AreEqual(0, emptyMapped["tagsSr"]!.AsArray().Count);
    }

    [TestMethod]
    public void FullLockRejectsBaselineMutationAndProposalKeySetDamage()
    {
        using var fixture = new FullLockFixture();
        var baselineTampered = fixture.Read(fixture.CandidatePath);
        var baselineRow = baselineTampered["entries"]!.AsArray().First()!.AsObject();
        baselineRow["sr"] = "izmenjeno";
        fixture.WriteCandidate(baselineTampered);
        var changedCandidatePolicy = fixture.Policy with { CandidateSha = Hash(fixture.CandidatePath) };
        Assert.Throws<InvalidDataException>(() => FullLockWorkflow.Lock(fixture.Paths(fixture.NewOutput("bad-baseline")), changedCandidatePolicy));

        fixture.RestoreFrozenCandidate();
        var proposals = fixture.Read(fixture.ProposalsPath);
        proposals["entries"]!.AsArray().RemoveAt(0);
        fixture.Write(fixture.ProposalsPath, proposals);
        var changedProposalsPolicy = fixture.Policy with { ProposalSha = Hash(fixture.ProposalsPath) };
        Assert.Throws<InvalidDataException>(() => FullLockWorkflow.Lock(fixture.Paths(fixture.NewOutput("bad-proposals")), changedProposalsPolicy));

        fixture.RestoreFrozenProposals();
        proposals = fixture.Read(fixture.ProposalsPath);
        proposals["entries"]!.AsArray()[0]!["en"] = "not-in-source";
        fixture.Write(fixture.ProposalsPath, proposals);
        changedProposalsPolicy = fixture.Policy with { ProposalSha = Hash(fixture.ProposalsPath) };
        Assert.Throws<InvalidDataException>(() => FullLockWorkflow.Lock(fixture.Paths(fixture.NewOutput("extra-proposal-key")), changedProposalsPolicy));

        fixture.RestoreFrozenProposals();
        var reversed = JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(fixture.SourcePath))!.Reverse().ToArray();
        File.WriteAllBytes(fixture.SourcePath, JsonSerializer.SerializeToUtf8Bytes(reversed));
        Assert.Throws<InvalidDataException>(() => FullLockWorkflow.Lock(fixture.Paths(fixture.NewOutput("unsorted-source")), fixture.Policy));
    }

    [TestMethod]
    public void FullLockRejectsDuplicateUnknownUnresolvedAndIdentityMismatchedReview()
    {
        using var fixture = new FullLockFixture();
        var original = fixture.Read(fixture.ReviewPath);

        var duplicate = (JsonObject)original.DeepClone();
        duplicate["decisions"]!.AsArray().Add(duplicate["decisions"]!.AsArray()[0]!.DeepClone());
        fixture.WriteReview(duplicate);
        Assert.Throws<InvalidDataException>(() => FullLockWorkflow.Lock(fixture.Paths(fixture.NewOutput("duplicate")), fixture.Policy));

        var unknown = (JsonObject)original.DeepClone();
        unknown["unexpected"] = true;
        fixture.WriteReview(unknown);
        Assert.Throws<InvalidDataException>(() => FullLockWorkflow.Lock(fixture.Paths(fixture.NewOutput("unknown")), fixture.Policy));

        fixture.RestoreValidReview();
        var duplicateJsonProperty = File.ReadAllText(fixture.ReviewPath).Replace(
            "\"confidence\": \"high\"", "\"confidence\": \"high\", \"confidence\": \"low\"", StringComparison.Ordinal);
        File.WriteAllText(fixture.ReviewPath, duplicateJsonProperty, new UTF8Encoding(false));
        Assert.Throws<InvalidDataException>(() => FullLockWorkflow.Lock(fixture.Paths(fixture.NewOutput("duplicate-property")), fixture.Policy));

        fixture.RestoreValidReview();
        var unresolved = (JsonObject)original.DeepClone();
        unresolved["decisions"]!.AsArray().RemoveAt(0);
        fixture.WriteReview(unresolved);
        Assert.Throws<InvalidDataException>(() => FullLockWorkflow.Lock(fixture.Paths(fixture.NewOutput("unresolved")), fixture.Policy));

        fixture.RestoreValidReview();
        unresolved = fixture.Read(fixture.ReviewPath);
        var contextDecision = unresolved["decisions"]!.AsArray().Single(node => node!["en"]!.GetValue<string>() == "new002");
        unresolved["decisions"]!.AsArray().Remove(contextDecision);
        fixture.WriteReview(unresolved);
        Assert.Throws<InvalidDataException>(() => FullLockWorkflow.Lock(fixture.Paths(fixture.NewOutput("unreviewed-context-risk")), fixture.Policy));

        var corruptIdentity = (JsonObject)original.DeepClone();
        corruptIdentity["candidateSha256"] = new string('0', 64);
        fixture.WriteReview(corruptIdentity);
        Assert.Throws<InvalidDataException>(() => FullLockWorkflow.Lock(fixture.Paths(fixture.NewOutput("corrupt-identity")), fixture.Policy));

        fixture.RestoreValidReview();
        var candidate = fixture.Read(fixture.CandidatePath);
        candidate["translator"]!["revision"] = "unexpected-revision";
        fixture.WriteCandidate(candidate);
        var changedCandidatePolicy = fixture.Policy with { CandidateSha = Hash(fixture.CandidatePath) };
        var matchingReview = fixture.Read(fixture.ReviewPath);
        matchingReview["candidateSha256"] = Hash(fixture.CandidatePath);
        fixture.WriteReview(matchingReview);
        Assert.Throws<InvalidDataException>(() => FullLockWorkflow.Lock(fixture.Paths(fixture.NewOutput("wrong-model-identity")), changedCandidatePolicy));
    }

    [TestMethod]
    public void FullLockRejectsBadSerbianAndPostReviewUncoveredFlags()
    {
        using var fixture = new FullLockFixture();
        var review = fixture.Read(fixture.ReviewPath);
        review["decisions"]!.AsArray()[0]!["approvedSr"] = "тест";
        fixture.WriteReview(review);
        Assert.Throws<InvalidDataException>(() => FullLockWorkflow.Lock(fixture.Paths(fixture.NewOutput("non-latin")), fixture.Policy));

        fixture.RestoreValidReview();
        var decision = review["decisions"]!.AsArray()[0]!.DeepClone().AsObject();
        decision["en"] = "new099";
        decision["approvedSr"] = "nova reč 010";
        decision["disposition"] = "corrected";
        decision["rationale"] = "fixture correction creates a length flag";
        review = fixture.Read(fixture.ReviewPath);
        review["decisions"]!.AsArray().Add(decision);
        fixture.WriteReview(review);
        Assert.Throws<InvalidDataException>(() => FullLockWorkflow.Lock(fixture.Paths(fixture.NewOutput("uncovered-final-qa")), fixture.Policy));
    }

    [TestMethod]
    public void FullLockRejectsFrozenHashDriftAndExistingOutputWithoutMutation()
    {
        using var fixture = new FullLockFixture();
        var wrongPolicy = fixture.Policy with { CandidateSha = new string('0', 64) };
        Assert.Throws<InvalidDataException>(() => FullLockWorkflow.Lock(fixture.Paths(fixture.NewOutput("wrong-hash")), wrongPolicy));

        var existing = fixture.NewOutput("existing-file");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "preserve");
        Assert.Throws<IOException>(() => FullLockWorkflow.Lock(fixture.Paths(existing), fixture.Policy));
        Assert.AreEqual("preserve", File.ReadAllText(existing));
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class FullLockFixture : IDisposable
    {
        private const int SourceCount = 993;
        private const int BaselineCount = 418;
        private const int ProposalCount = 575;
        private const int MovieCount = 9730;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "translator-full-lock-tests-" + Guid.NewGuid().ToString("N"));
        private readonly string[] _tags;
        private readonly string[] _baselineTags;
        private readonly string[] _proposalTags;
        private readonly JsonObject _baseline;
        private readonly JsonObject _candidate;
        private readonly JsonObject _proposals;
        private readonly JsonObject _review;
        private readonly string _sourceHash;
        private readonly string _proposalSubsetHash;
        private string? _originalReview;

        public string BaselinePath => Path.Combine(_root, "baseline.json");
        public string CandidatePath => Path.Combine(_root, "candidate.json");
        public string ProposalsPath => Path.Combine(_root, "proposals", "batch-1", "proposal-dictionary.json");
        public string SourcePath => Path.Combine(_root, "source-tags.json");
        public string CatalogPath => Path.Combine(_root, "catalog.jsonl");
        public string ReviewPath => Path.Combine(_root, "review.json");
        public FullLockWorkflow.Policy Policy { get; }

        public FullLockFixture()
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(Path.GetDirectoryName(ProposalsPath)!);
            _baselineTags = Enumerable.Range(0, BaselineCount).Select(i => $"base{i:D3}").ToArray();
            _baselineTags[0] = "dark";
            _proposalTags = Enumerable.Range(0, ProposalCount).Select(i => $"new{i:D3}").ToArray();
            _proposalTags[0] = "camp";
            _proposalTags[1] = "gloomy";
            _tags = _baselineTags.Concat(_proposalTags).Order(StringComparer.Ordinal).ToArray();
            _sourceHash = TagWorkflow.HashCanonical(_tags);
            _proposalSubsetHash = TagWorkflow.HashCanonical(_proposalTags);
            var translator = MakeTranslator();
            _baseline = MakeRoot(TagWorkflow.HashCanonical(_baselineTags), translator);
            var baselineEntries = new JsonArray();
            for (var i = 0; i < _baselineTags.Length; i++)
            {
                var key = _baselineTags[i];
                var sr = key == "dark" ? "mračan" : $"stara reč {i:D3}";
                var item = MakeEntry(key, $"raw {key}", sr, "auto_pass", false);
                if (i < 24)
                {
                    item["previousAccepted"] = $"older value {i:D3}";
                    item["correctionProvenance"] = new JsonObject { ["revision"] = "phase-6t", ["reason"] = $"fixture {i}" };
                }
                baselineEntries.Add(item);
            }
            _baseline["entries"] = baselineEntries;
            _baseline["datasetRelease"] = "poc-v2";
            _baseline["correctionProposalSha256"] = "f98e2468d047feebe9546b20e1e64c6d9a4ff46c6fa454075165352c3dcc1be1";

            _proposals = MakeRoot(_proposalSubsetHash, translator);
            var proposalEntries = new JsonArray();
            for (var i = 0; i < _proposalTags.Length; i++)
            {
                var key = _proposalTags[i];
                var sr = key switch { "camp" => "kamp", "gloomy" => "mračan", _ => $"nova reč {i:D3}" };
                proposalEntries.Add(MakeEntry(key, sr, sr, "auto_pass", false));
            }
            _proposals["entries"] = proposalEntries;

            _candidate = MakeRoot(_sourceHash, translator);
            var candidateEntries = new JsonArray();
            foreach (var item in baselineEntries)
                candidateEntries.Add(item!.DeepClone());
            foreach (var item in proposalEntries)
                candidateEntries.Add(item!.DeepClone());
            _candidate["entries"] = candidateEntries;
            _candidate["datasetRelease"] = "poc-v2";
            _candidate["correctionProposalSha256"] = "f98e2468d047feebe9546b20e1e64c6d9a4ff46c6fa454075165352c3dcc1be1";

            Write(BaselinePath, _baseline);
            Write(CandidatePath, _candidate);
            Write(ProposalsPath, _proposals);
            Write(SourcePath, JsonSerializer.SerializeToNode(_tags)!);
            WriteCatalog();
            Write(Path.Combine(Path.GetDirectoryName(ProposalsPath)!, "proposal-summary.json"),
                JsonSerializer.SerializeToNode(new { schemaVersion = "translator-proposal-v1", count = ProposalCount, reused = 0, inferred = ProposalCount, runtimeFingerprint = new string('a', 64) })!);
            WriteCheckpoint();
            _review = BuildReview([]);
            _review["decisions"] = AsNodeArray(BuildDecisions());
            WriteReview(_review);
            _originalReview = File.ReadAllText(ReviewPath);
            Policy = new FullLockWorkflow.Policy(SourceCount, BaselineCount, ProposalCount, MovieCount,
                Hash(CandidatePath), Hash(BaselinePath), Hash(ProposalsPath), _sourceHash, Hash(CatalogPath),
                _sourceHash, _proposalSubsetHash, false);
        }

        public FullLockWorkflow.Paths Paths(string output) => new(CandidatePath, BaselinePath, ProposalsPath, SourcePath, CatalogPath, ReviewPath, output);
        public string NewOutput(string name) => Path.Combine(_root, name);
        public JsonObject Read(string path) => JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        public void Write(string path, JsonNode node) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(node, new JsonSerializerOptions { WriteIndented = true }));
        public void WriteCandidate(JsonObject node) => Write(CandidatePath, node);
        public void WriteReview(JsonObject node) => Write(ReviewPath, node);
        public void RestoreFrozenCandidate() => Write(CandidatePath, _candidate);
        public void RestoreFrozenProposals() => Write(ProposalsPath, _proposals);
        public void RestoreValidReview() => File.WriteAllText(ReviewPath, _originalReview!, new UTF8Encoding(false));

        private JsonObject BuildReview(JsonArray decisions) => new()
        {
            ["schemaVersion"] = FullLockWorkflow.ReviewSchema,
            ["candidateSha256"] = Hash(CandidatePath),
            ["baselineSha256"] = Hash(BaselinePath),
            ["proposalsSha256"] = Hash(ProposalsPath),
            ["sourceTagsSha256"] = _sourceHash,
            ["decisions"] = decisions
        };

        private IEnumerable<JsonNode> BuildDecisions()
        {
            foreach (var item in _baseline["entries"]!.AsArray())
                yield return MakeDecision(item!["en"]!.GetValue<string>(), item["sr"]!.GetValue<string>(), "retained_baseline");
            yield return MakeDecision("camp", "kamp", "accepted_unchanged");
            yield return MakeDecision("gloomy", "mračan", "accepted_unchanged");
            yield return MakeDecision("new002", "nova reč 002", "accepted_unchanged");
            yield return MakeDecision("new003", "izmenjen prevod 003", "corrected");
            yield return MakeDecision("new004", "alternativna reč 004", "ambiguous_general");
        }

        private static JsonObject MakeDecision(string key, string sr, string disposition) => new()
        {
            ["en"] = key, ["approvedSr"] = sr, ["disposition"] = disposition,
            ["rationale"] = "fixture-only explicit review", ["confidence"] = "high"
        };

        private void WriteCatalog()
        {
            using var stream = new FileStream(CatalogPath, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            for (var i = 0; i < MovieCount; i++)
            {
                var name = _tags[i < _tags.Length ? i : 0];
                var row = new
                {
                    movieLensId = i + 1,
                    rawTitle = $"Fixture film {i:D4} ({2000 + i % 25})",
                    title = i == 0 ? "new002" : $"Fixture film {i:D4}",
                    year = 2000 + i % 25,
                    imdbId = (string?)null,
                    movieLensAvgRating = 3.5,
                    directedByRaw = i == 0 ? "" : "Fixture Director",
                    starringRaw = i == 0 ? null : "Fixture Cast",
                    relevantTags = new[] { new { name, score = i / 10.0 } },
                    semanticText = $"English fixture {i}",
                    tmdbId = (int?)null,
                    genres = new[] { "Drama" },
                    averageRating = (double?)null,
                    ratingCount = 0,
                    runtimeMinutes = (int?)null,
                    originalLanguage = "en",
                    posterPath = (string?)null
                };
                writer.WriteLine(JsonSerializer.Serialize(row));
            }
        }

        private void WriteCheckpoint()
        {
            var fingerprint = new string('a', 64);
            var artifactHashes = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["model"] = new string('b', 64) };
            var identity = new ProposalIdentity(TranslatorConstants.ModelId, TranslatorConstants.ModelRevision,
                TranslatorConstants.TargetToken, new DecodingMetadata(false, 4, 32, 512, "cpu"),
                new string('c', 64), fingerprint, TranslatorConstants.NormalizationVersion, artifactHashes);
            var identityHash = identity.Sha256;
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var lines = new List<string>
            {
                JsonSerializer.Serialize(new { kind = "header", identitySha256 = identityHash, identity }, options)
            };
            foreach (var item in _proposals["entries"]!.AsArray())
            {
                var proposal = new CheckpointProposal(item!["en"]!.GetValue<string>(), item["machine"]!.GetValue<string>(),
                    item["sr"]!.GetValue<string>(), true, null);
                var payload = new { identitySha256 = identityHash, identity, proposal };
                var proposalHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, options)));
                lines.Add(JsonSerializer.Serialize(new { kind = "proposal", identitySha256 = identityHash, identity,
                    proposalSha256 = proposalHash, proposal }, options));
            }
            File.WriteAllText(Path.Combine(_root, "proposals", "remaining-575.jsonl"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        }

        private static JsonObject MakeRoot(string sourceHash, JsonNode translator) => new()
        {
            ["schemaVersion"] = "tag-translations-sr-v1",
            ["sourceTagsSha256"] = sourceHash,
            ["translator"] = translator.DeepClone(),
            ["normalizationVersion"] = TranslatorConstants.NormalizationVersion
        };

        private static JsonObject MakeEntry(string en, string machine, string sr, string status, bool manual) => new()
        {
            ["en"] = en, ["machine"] = machine, ["sr"] = sr, ["reviewStatus"] = status, ["manualOverride"] = manual
        };

        private static JsonNode MakeTranslator() => JsonSerializer.SerializeToNode(new
        {
            modelId = TranslatorConstants.ModelId,
            revision = TranslatorConstants.ModelRevision,
            targetToken = TranslatorConstants.TargetToken,
            artifactHashes = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["model"] = new string('b', 64) },
            runtimeLockSha256 = new string('c', 64),
            decoding = new { doSample = false, numBeams = 4, maxNewTokens = 32, sourceMaxTokens = 512, device = "cpu" }
        })!;

        private static JsonArray AsNodeArray(IEnumerable<JsonNode> values)
        {
            var array = new JsonArray();
            foreach (var value in values) array.Add(value);
            return array;
        }

        private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
