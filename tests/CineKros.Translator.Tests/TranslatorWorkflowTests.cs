using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.Translator;

namespace CineKros.Translator.Tests;

[TestClass]
public sealed class TranslatorWorkflowTests
{
    private static readonly string[] ArtifactHashes = ["a", "b", "c"];

    [TestMethod]
    public void CliRejectsMissingUnknownDuplicateRelativeAndOverLimitOptions()
    {
        Assert.Throws<CommandLineException>(() => CommandArguments.Parse(["extract-tags", "--catalog", "C:\\in.jsonl"]));
        Assert.Throws<CommandLineException>(() => CommandArguments.Parse(["extract-tags", "--catalog", "C:\\in", "--output-dir", "C:\\out", "--wat", "x"]));
        Assert.Throws<CommandLineException>(() => CommandArguments.Parse(["extract-tags", "--catalog", "C:\\in", "--catalog", "C:\\in2", "--output-dir", "C:\\out"]));
        Assert.Throws<CommandLineException>(() => CommandArguments.Parse(["extract-tags", "--catalog", "in.jsonl", "--output-dir", "C:\\out"]));
        Assert.Throws<CommandLineException>(() => CommandArguments.Parse(["propose", "--tags", "C:\\tags", "--python", "C:\\python.exe", "--model-dir", "C:\\model", "--checkpoint", "C:\\checkpoint", "--output-dir", "C:\\out", "--max-items", "419"]));
        var boundedPoc = CommandArguments.Parse(["propose", "--tags", "C:\\tags", "--python", "C:\\python.exe", "--model-dir", "C:\\model", "--checkpoint", "C:\\checkpoint", "--output-dir", "C:\\out", "--max-items", "418"]);
        Assert.AreEqual("418", boundedPoc["max-items"]);
    }

    [TestMethod]
    public void ExtractionProducesOrdinalExactUniqueUnicodeTagsAndStableHash()
    {
        using var temp = new TempDirectory();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny-catalog.jsonl");
        var first = Path.Combine(temp.Path, "out-a");
        var second = Path.Combine(temp.Path, "out-b");
        TagWorkflow.Extract(fixture, first);
        TagWorkflow.Extract(fixture, second);
        var tags = TagWorkflow.ReadTagArray(Path.Combine(first, "source-tags.json"));
        CollectionAssert.AreEqual(new[] { "a  b", "dark", "zürich", "Љубав" }, tags.ToArray());
        Assert.AreEqual(TagWorkflow.HashCanonical(tags), TagWorkflow.HashCanonical(TagWorkflow.ReadTagArray(Path.Combine(second, "source-tags.json"))));
    }

    [TestMethod]
    public void ProposalPreservesOriginalMachineAndUsesIndependentNormalizedSerbian()
    {
        using var temp = new TempDirectory();
        var dictionary = Dictionary([Entry("dark", "rough MACHINE", "mračan")]);
        var path = WriteDictionary(temp.Path, dictionary, "dictionary.json");
        var review = Path.Combine(temp.Path, "review.json");
        File.WriteAllText(review, "{\"decisions\":[{\"en\":\"dark\",\"approvedSr\":\" ЉУБАВ  \"}]}", new UTF8Encoding(false));
        var output = Path.Combine(temp.Path, "reviewed");
        TagWorkflow.ApplyReview(path, review, output);
        var reviewed = TagWorkflow.ReadDictionary(Path.Combine(output, "reviewed-dictionary.json"));
        Assert.AreEqual("rough MACHINE", reviewed.Entries[0].Machine);
        Assert.AreEqual("LJUBAV", reviewed.Entries[0].Sr);
        Assert.AreEqual("reviewed", reviewed.Entries[0].ReviewStatus);
        Assert.IsTrue(reviewed.Entries[0].ManualOverride);

        var secondReview = Path.Combine(temp.Path, "review-again.json");
        File.WriteAllText(secondReview, "{\"decisions\":[{\"en\":\"dark\",\"approvedSr\":\"ЉУБАВ\"}]}", new UTF8Encoding(false));
        var secondOutput = Path.Combine(temp.Path, "reviewed-again");
        TagWorkflow.ApplyReview(Path.Combine(output, "reviewed-dictionary.json"), secondReview, secondOutput);
        var repeated = TagWorkflow.ReadDictionary(Path.Combine(secondOutput, "reviewed-dictionary.json"));
        Assert.IsTrue(repeated.Entries[0].ManualOverride);
        using var evidence = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(secondOutput, "review-evidence.json")));
        Assert.IsFalse(string.IsNullOrWhiteSpace(evidence.RootElement.GetProperty("previousEvidenceSha256").GetString()));
        Assert.AreEqual(File.ReadAllText(secondReview), File.ReadAllText(Path.Combine(secondOutput, "review-source.json")));
    }

    [TestMethod]
    public void ReviewRejectsUnknownDuplicateAndDuplicateJsonProperties()
    {
        using var temp = new TempDirectory();
        var dictionary = Dictionary([Entry("dark", "mračan", "mračan")]);
        var dictionaryPath = WriteDictionary(temp.Path, dictionary, "dictionary.json");
        foreach (var json in new[]
        {
            "{\"decisions\":[{\"en\":\"other\",\"approvedSr\":\"drugo\"}]}",
            "{\"decisions\":[{\"en\":\"dark\",\"approvedSr\":\"jedan\"},{\"en\":\"dark\",\"approvedSr\":\"dva\"}]}",
            """{"decisions":[],"decisions":[]}"""
        })
        {
            var review = Path.Combine(temp.Path, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(review, json, new UTF8Encoding(false));
            Assert.Throws<InvalidDataException>(() => TagWorkflow.ApplyReview(dictionaryPath, review, Path.Combine(temp.Path, Guid.NewGuid().ToString("N"))));
        }
    }

    [TestMethod]
    public void LockRequiresExactResolvedKeysAndPublishesDeterministicHash()
    {
        using var temp = new TempDirectory();
        var tags = new[] { "dark", "friendship" };
        var tagsPath = Path.Combine(temp.Path, "tags.json");
        File.WriteAllBytes(tagsPath, JsonSerializer.SerializeToUtf8Bytes(tags));
        var dictionary = Dictionary([Entry("friendship", "prijateljstvo", "prijateljstvo"), Entry("dark", "mračan", "mračan")]);
        var dictionaryPath = WriteDictionary(temp.Path, dictionary, "dictionary.json");
        var one = Path.Combine(temp.Path, "locked-one");
        var two = Path.Combine(temp.Path, "locked-two");
        TagWorkflow.Lock(dictionaryPath, tagsPath, one);
        TagWorkflow.Lock(dictionaryPath, tagsPath, two);
        Assert.AreEqual(File.ReadAllText(Path.Combine(one, "content.sha256")), File.ReadAllText(Path.Combine(two, "content.sha256")));
        Assert.AreEqual(64, File.ReadAllText(Path.Combine(one, "content.sha256")).Trim().Length);
        var locked = TagWorkflow.ReadDictionary(Path.Combine(one, "tag-translations-sr.json"));
        CollectionAssert.AreEqual(new[] { "dark", "friendship" }, locked.Entries.Select(entry => entry.En).ToArray());

        var unresolved = Dictionary([Entry("dark", "dark", "dark", "review_required"), Entry("friendship", "prijateljstvo", "prijateljstvo")]);
        var unresolvedPath = WriteDictionary(temp.Path, unresolved, "unresolved.json");
        Assert.Throws<InvalidDataException>(() => TagWorkflow.Lock(unresolvedPath, tagsPath, Path.Combine(temp.Path, "locked-invalid")));

        var missingKey = Dictionary([Entry("dark", "mračan", "mračan")]) with { SourceTagsSha256 = TagWorkflow.HashCanonical(tags) };
        var missingPath = WriteDictionary(temp.Path, missingKey, "missing.json");
        Assert.Throws<InvalidDataException>(() => TagWorkflow.Lock(missingPath, tagsPath, Path.Combine(temp.Path, "locked-missing")));
        var extraTags = new[] { "dark", "friendship", "feel-good" };
        var extraEntries = extraTags.Select(tag => Entry(tag, tag == "dark" ? "mračan" : "approved", tag == "dark" ? "mračan" : "odobreno", "reviewed")).ToArray();
        var extra = Dictionary(extraEntries) with { SourceTagsSha256 = TagWorkflow.HashCanonical(tags) };
        var extraPath = WriteDictionary(temp.Path, extra, "extra.json");
        Assert.Throws<InvalidDataException>(() => TagWorkflow.Lock(extraPath, tagsPath, Path.Combine(temp.Path, "locked-extra")));

        var flaggedTags = new[] { "camp" };
        var flaggedTagsPath = Path.Combine(temp.Path, "flagged-tags.json");
        File.WriteAllBytes(flaggedTagsPath, JsonSerializer.SerializeToUtf8Bytes(flaggedTags));
        var flagged = Dictionary([Entry("camp", "camp", "kamp")]);
        var flaggedPath = WriteDictionary(temp.Path, flagged, "flagged.json");
        Assert.Throws<InvalidDataException>(() => TagWorkflow.Lock(flaggedPath, flaggedTagsPath, Path.Combine(temp.Path, "flag-bypass")));
        var flagReview = Path.Combine(temp.Path, "camp-review.json");
        File.WriteAllText(flagReview, "{\"decisions\":[{\"en\":\"camp\",\"approvedSr\":\"kamp\"}]}", new UTF8Encoding(false));
        var reviewedDir = Path.Combine(temp.Path, "camp-reviewed");
        TagWorkflow.ApplyReview(flaggedPath, flagReview, reviewedDir);
        TagWorkflow.Lock(Path.Combine(reviewedDir, "reviewed-dictionary.json"), flaggedTagsPath, Path.Combine(temp.Path, "flag-approved"));
    }

    [TestMethod]
    public void QaFlagsEachOfEightRulesButKeepsWhitelistAndLegitimateSynonyms()
    {
        AssertFlag(Entry("empty", " ", " "), "empty_or_incomplete");
        AssertFlag(Entry("same", "same", "same"), "unchanged_english");
        AssertFlag(Entry("long", "x", new string('a', 81)), "excessive_length");
        AssertFlag(Entry("script", "mix", "Ωmega"), "script_residue");
        AssertFlag(Entry("punctuation", "raw\u0001<unk>", "тест!!"), "malformed_output");
        AssertFlag(Entry("dark", "mraèno", "mraèno"), "malformed_output");
        AssertFlag(Entry("number 7", "број", "број"), "lost_number_or_negation");
        AssertFlag(Entry("number 7", "broj 17", "broj 17"), "lost_number_or_negation");
        Assert.IsFalse(TranslationQa.Evaluate(Entry("number 7", "broj 7", "broj 7")).Any(flag => flag.Code == "lost_number_or_negation"));
        AssertFlag(Entry("not happy", "срећан", "срећан"), "lost_number_or_negation");
        AssertFlag(Entry("camp", "камп", "камп"), "ambiguity_seed");

        var collisionDictionary = Dictionary([Entry("dark", "мрачан", "исти"), Entry("gloomy", "суморан", "ИСТИ")]);
        var collision = TranslationQa.Analyze(collisionDictionary);
        Assert.IsTrue(collision.Entries.All(entry => entry.Flags.Any(flag => flag.Code == "sr_collision")));
        Assert.IsFalse(TranslationQa.Evaluate(Entry("imdb", "imdb", "imdb")).Any(flag => flag.Code == "unchanged_english"));
        Assert.IsTrue(TranslationQa.Analyze(Dictionary([Entry("film", "movie", "film")])).Entries.Single().Flags.Count > 0);
        Assert.IsFalse(TranslationQa.Evaluate(Entry("synonym", "različito", "ista reč")).Any(flag => flag.Code == "sr_collision"));
    }

    [TestMethod]
    public void DictionaryRejectsWrongPropertyOrderUnknownStatusAndDuplicateKeys()
    {
        using var temp = new TempDirectory();
        var dictionary = Dictionary([Entry("dark", "mračan", "mračan")]);
        var good = JsonSerializer.Serialize(dictionary);
        var wrongOrder = JsonSerializer.Serialize(new
        {
            sourceTagsSha256 = dictionary.SourceTagsSha256,
            schemaVersion = dictionary.SchemaVersion,
            translator = dictionary.Translator,
            normalizationVersion = dictionary.NormalizationVersion,
            entries = dictionary.Entries
        });
        var badStatus = good.Replace("auto_pass", "automatic", StringComparison.Ordinal);
        var duplicateJsonKey = good.Replace("\"schemaVersion\":\"tag-translations-sr-v1\"", "\"schemaVersion\":\"tag-translations-sr-v1\",\"schemaVersion\":\"tag-translations-sr-v1\"");
        foreach (var text in new[] { wrongOrder, badStatus, duplicateJsonKey })
        {
            var path = Path.Combine(temp.Path, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            Assert.Throws<InvalidDataException>(() => TagWorkflow.ReadDictionary(path));
        }
    }

    [TestMethod]
    public void ProtocolRejectsMalformedUnknownDuplicateAndInconsistentResponses()
    {
        Assert.Throws<JsonException>(() => PythonTranslationProcess.ParseResponseLine("{"));
        Assert.Throws<InvalidDataException>(() => PythonTranslationProcess.ParseResponseLine("{\"en\":\"x\",\"machine\":\"x\",\"completed\":true,\"error\":null,\"extra\":0}"));
        Assert.Throws<InvalidDataException>(() => PythonTranslationProcess.ParseResponseLine("{\"en\":\"x\",\"en\":\"x\",\"machine\":\"x\",\"completed\":true,\"error\":null}"));
        Assert.Throws<InvalidDataException>(() => PythonTranslationProcess.ParseResponseLine("{\"en\":\"x\",\"machine\":null,\"completed\":true,\"error\":null}"));
        Assert.AreEqual("failure", PythonTranslationProcess.ParseResponseLine("{\"en\":\"x\",\"machine\":null,\"completed\":false,\"error\":\"failure\"}").Error);
    }

    [TestMethod]
    public async Task CheckpointSkipsOnlyCompatibleCompletedRowsAndRetriesFailures()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "run.jsonl");
        var identity = Identity();
        var checkpoint = ProposalCheckpoint.Open(path, identity);
        await checkpoint.AppendAsync(new CheckpointProposal("dark", "mračan", "mračan", true, null), CancellationToken.None);
        await checkpoint.AppendAsync(new CheckpointProposal("camp", "", "", false, "incomplete_generation"), CancellationToken.None);
        var resumed = ProposalCheckpoint.Open(path, identity);
        Assert.IsTrue(resumed.Completed.ContainsKey("dark"));
        Assert.IsFalse(resumed.Completed.ContainsKey("camp"));
        var records = File.ReadAllLines(path);
        Assert.AreEqual(3, records.Length);
        foreach (var record in records)
            using (JsonDocument.Parse(record)) { }
        using (var row = JsonDocument.Parse(records[1]))
        {
            Assert.AreEqual(identity.Sha256, row.RootElement.GetProperty("identitySha256").GetString());
            Assert.IsTrue(row.RootElement.TryGetProperty("proposalSha256", out _));
            Assert.AreEqual(TranslatorConstants.ModelRevision, row.RootElement.GetProperty("identity").GetProperty("revision").GetString());
            Assert.AreEqual(identity.RuntimeFingerprint, row.RootElement.GetProperty("identity").GetProperty("runtimeFingerprint").GetString());
        }

        var incompatible = ProposalCheckpoint.Open(path, identity with { Decoding = new DecodingMetadata(true, 4, 32, 512, "cpu") });
        Assert.AreEqual(0, incompatible.Completed.Count);
        Assert.IsTrue(Directory.GetFiles(temp.Path, "run.jsonl.backup-*").Length >= 1);
    }

    [TestMethod]
    public async Task CheckpointRejectsCompletedRowPayloadTampering()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "run.jsonl");
        var identity = Identity();
        var checkpoint = ProposalCheckpoint.Open(path, identity);
        await checkpoint.AppendAsync(new CheckpointProposal("dark", "mračan", "mračan", true, null), CancellationToken.None);
        var lines = File.ReadAllLines(path);
        var row = JsonNode.Parse(lines[1])!.AsObject();
        row["proposal"]!["machine"] = "potamnjen";
        lines[1] = row.ToJsonString();
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        var reopened = ProposalCheckpoint.Open(path, identity);
        Assert.AreEqual(0, reopened.Completed.Count);
        Assert.IsTrue(Directory.GetFiles(temp.Path, "run.jsonl.backup-*").Length >= 1);
    }

    [TestMethod]
    public async Task CheckpointRecoversCorruptHeaderAndDropsTruncatedLastRecordSafely()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "run.jsonl");
        var identity = Identity();
        var checkpoint = ProposalCheckpoint.Open(path, identity);
        await checkpoint.AppendAsync(new CheckpointProposal("dark", "mračan", "mračan", true, null), CancellationToken.None);
        var lines = File.ReadAllLines(path);
        var corruptHeaderAndPartialUtf8 = Encoding.UTF8.GetBytes("not-json\n" + lines[1] + "\n{\"partial\":\"");
        File.WriteAllBytes(path, corruptHeaderAndPartialUtf8.Concat(new byte[] { 0xC3 }).ToArray());
        var recovered = ProposalCheckpoint.Open(path, identity);
        Assert.IsTrue(recovered.Completed.ContainsKey("dark"));
        Assert.IsTrue(File.ReadAllText(path).EndsWith("\n", StringComparison.Ordinal));
        Assert.IsTrue(Directory.GetFiles(temp.Path, "run.jsonl.backup-*").Length >= 1);
        await recovered.AppendAsync(new CheckpointProposal("friendship", "prijateljstvo", "prijateljstvo", true, null), CancellationToken.None);
        var reopened = ProposalCheckpoint.Open(path, identity);
        CollectionAssert.AreEqual(new[] { "dark", "friendship" }, reopened.Completed.Keys.Order(StringComparer.Ordinal).ToArray());
        foreach (var record in File.ReadAllLines(path))
            using (JsonDocument.Parse(record)) { }
    }

    [TestMethod]
    public async Task RealFixtureProcessRejectsMalformedUnknownDuplicateExtraAndFailureResponses()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "ProtocolFixtureRunner.dll");
        Assert.IsTrue(File.Exists(fixture), fixture);
        const string executable = "dotnet";
        var one = new[] { new ProtocolJob("dark", "dark", TranslatorConstants.TargetToken, TranslatorConstants.DecodingId) };
        var two = new[] { one[0], new ProtocolJob("friendship", "friendship", TranslatorConstants.TargetToken, TranslatorConstants.DecodingId) };
        Func<ProtocolResponse, CancellationToken, Task> ignore = (_, _) => Task.CompletedTask;
        await Assert.ThrowsAsync<JsonException>(() => PythonTranslationProcess.RunFixtureForTestsAsync(executable, fixture, "malformed", one, ignore, TimeSpan.FromSeconds(5), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => PythonTranslationProcess.RunFixtureForTestsAsync(executable, fixture, "unrequested", one, ignore, TimeSpan.FromSeconds(5), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => PythonTranslationProcess.RunFixtureForTestsAsync(executable, fixture, "duplicate", two, ignore, TimeSpan.FromSeconds(5), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => PythonTranslationProcess.RunFixtureForTestsAsync(executable, fixture, "extra", one, ignore, TimeSpan.FromSeconds(5), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PythonTranslationProcess.RunFixtureForTestsAsync(executable, fixture, "exit", one, ignore, TimeSpan.FromSeconds(5), CancellationToken.None));
        ProtocolResponse? incomplete = null;
        await PythonTranslationProcess.RunFixtureForTestsAsync(executable, fixture, "incomplete", one,
            (response, _) => { incomplete = response; return Task.CompletedTask; }, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.IsNotNull(incomplete);
        Assert.IsFalse(incomplete.Completed);
        Assert.AreEqual("incomplete_generation", incomplete.Error);
        var overLimit = Enumerable.Range(0, 419).Select(index => new ProtocolJob($"selected tag {index}", $"selected tag {index}", TranslatorConstants.TargetToken, TranslatorConstants.DecodingId)).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => PythonTranslationProcess.RunFixtureForTestsAsync(executable, fixture, "valid", overLimit, ignore, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [TestMethod]
    public async Task ProposalRefusesToSilentlyTruncateTheSelectedTagSet()
    {
        using var temp = new TempDirectory();
        var tagsPath = Path.Combine(temp.Path, "tags.json");
        File.WriteAllText(tagsPath, "[\"one\",\"two\"]");
        var pythonPath = Path.Combine(temp.Path, "python.exe"); File.WriteAllText(pythonPath, string.Empty);
        var modelPath = Path.Combine(temp.Path, "model"); Directory.CreateDirectory(modelPath);
        var arguments = CommandArguments.Parse(["propose", "--tags", tagsPath, "--python", pythonPath, "--model-dir", modelPath,
            "--checkpoint", Path.Combine(temp.Path, "checkpoint.jsonl"), "--output-dir", Path.Combine(temp.Path, "out"), "--max-items", "1"]);
        await Assert.ThrowsAsync<InvalidDataException>(() => TagWorkflow.ProposeAsync(arguments, CancellationToken.None));
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "checkpoint.jsonl")));
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "out")));
    }

    [TestMethod]
    public async Task TimeoutAndCancellationKillOwnedProcessTreeAndPreserveCompletedCheckpointRows()
    {
        foreach (var mode in new[] { "timeout", "cancel" })
        {
            using var temp = new TempDirectory();
            var fixture = Path.Combine(AppContext.BaseDirectory, "ProtocolFixtureRunner.dll");
            var pidFile = Path.Combine(temp.Path, "pids.txt");
            var checkpointPath = Path.Combine(temp.Path, "run.jsonl");
            var checkpoint = ProposalCheckpoint.Open(checkpointPath, Identity());
            var jobs = new[]
            {
                new ProtocolJob("dark", "dark", TranslatorConstants.TargetToken, TranslatorConstants.DecodingId),
                new ProtocolJob("friendship", "friendship", TranslatorConstants.TargetToken, TranslatorConstants.DecodingId)
            };
            async Task Save(ProtocolResponse response, CancellationToken token)
            {
                if (response.Completed)
                    await checkpoint.AppendAsync(new CheckpointProposal(response.En, response.Machine!, response.Machine!, true, null), token);
            }
            using var cancellation = mode == "cancel" ? new CancellationTokenSource(TimeSpan.FromSeconds(2)) : new CancellationTokenSource();
            if (mode == "timeout")
                await Assert.ThrowsAsync<TimeoutException>(() => PythonTranslationProcess.RunFixtureForTestsAsync("dotnet", fixture, mode + "|" + pidFile, jobs, Save, TimeSpan.FromSeconds(2), cancellation.Token));
            else
                await Assert.ThrowsAsync<OperationCanceledException>(() => PythonTranslationProcess.RunFixtureForTestsAsync("dotnet", fixture, mode + "|" + pidFile, jobs, Save, TimeSpan.FromSeconds(20), cancellation.Token));
            var resumed = ProposalCheckpoint.Open(checkpointPath, Identity());
            Assert.IsTrue(resumed.Completed.ContainsKey("dark"));
            Assert.IsFalse(resumed.Completed.ContainsKey("friendship"));
            Assert.IsTrue(File.Exists(pidFile));
            var pids = File.ReadAllLines(pidFile).Select(int.Parse).ToArray();
            Assert.AreEqual(2, pids.Length);
            foreach (var pid in pids)
                await AssertProcessExited(pid);
        }
    }

    [TestMethod]
    public async Task FailedAtomicPublicationLeavesCheckpointReusableWithoutInference()
    {
        using var temp = new TempDirectory();
        var identity = Identity();
        var checkpointPath = Path.Combine(temp.Path, "run.jsonl");
        var checkpoint = ProposalCheckpoint.Open(checkpointPath, identity);
        var tags = new[] { "dark", "friendship" };
        foreach (var tag in tags)
            await checkpoint.AppendAsync(new CheckpointProposal(tag, "machine " + tag, "normalized " + tag, true, null), CancellationToken.None);
        var output = Path.Combine(temp.Path, "raced-output");
        Assert.Throws<IOException>(() => AtomicDirectory.PublishNew(output, stage =>
        {
            File.WriteAllText(Path.Combine(stage, "proposal.json"), "staged");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "owner.txt"), "preserve");
        }));
        Assert.AreEqual("preserve", File.ReadAllText(Path.Combine(output, "owner.txt")));
        Assert.AreEqual(0, Directory.GetDirectories(temp.Path, ".translator-stage-*").Length);
        var resumed = ProposalCheckpoint.Open(checkpointPath, identity);
        var missing = tags.Where(tag => !resumed.Completed.ContainsKey(tag)).ToArray();
        Assert.AreEqual(0, missing.Length);
        CollectionAssert.AreEqual(tags, resumed.Completed.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.AreEqual("machine dark", resumed.Completed["dark"].Machine);
        Assert.AreEqual("normalized friendship", resumed.Completed["friendship"].Sr);
    }

    [TestMethod]
    public void ExistingOutputIsNeverOverwritten()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "existing");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "user data");
        Assert.Throws<IOException>(() => AtomicDirectory.PublishNew(target, stage => File.WriteAllText(Path.Combine(stage, "new.txt"), "new")));
        Assert.AreEqual("user data", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }

    private static void AssertFlag(TranslationEntry entry, string code)
    {
        Assert.IsTrue(TranslationQa.Evaluate(entry).Any(flag => flag.Code == code), $"expected QA flag {code}");
    }

    private static TranslationEntry Entry(string en, string machine, string sr, string status = "auto_pass") => new(en, machine, sr, status, false);

    private static TranslationDictionary Dictionary(IReadOnlyList<TranslationEntry> entries) => new(
        "tag-translations-sr-v1", TagWorkflow.HashCanonical(entries.Select(entry => entry.En).ToArray()),
        new TranslatorMetadata(TranslatorConstants.ModelId, TranslatorConstants.ModelRevision, TranslatorConstants.TargetToken,
            new SortedDictionary<string, string>(StringComparer.Ordinal) { ["config.json"] = new string('a', 64) }, new string('b', 64),
            new DecodingMetadata(false, 4, 32, 512, "cpu")), TranslatorConstants.NormalizationVersion, entries);

    private static string WriteDictionary(string directory, TranslationDictionary dictionary, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(dictionary));
        return path;
    }

    private static ProposalIdentity Identity() => new(TranslatorConstants.ModelId, TranslatorConstants.ModelRevision, TranslatorConstants.TargetToken,
        new DecodingMetadata(false, 4, 32, 512, "cpu"), new string('1', 64), new string('2', 64), TranslatorConstants.NormalizationVersion,
        new SortedDictionary<string, string>(StringComparer.Ordinal) { ["config.json"] = new string('a', 64) });

    private static async Task AssertProcessExited(int pid)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return;
            }
            catch (ArgumentException) { return; }
            await Task.Delay(100);
        }
        Assert.Fail($"process {pid} was not terminated");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cinekros-translator-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
