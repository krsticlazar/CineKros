using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.Etl;

namespace CineKros.Etl.Tests;

[TestClass]
public sealed class SrPocCatalogTests
{
    private const string CanonicalCatalog = "D:\\Repozitorijum\\CineKros\\database\\data\\derived\\final\\b05a-real-tmdb-01\\movies-catalog.jsonl";
    private const string SelectionIdHash = "0960c2074b1848dbf9b7d374a82f345fc910f235434ef06002380949cf674529";
    private static readonly JsonSerializerOptions CanonicalJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [TestMethod]
    public void RealCanonicalSelectorReproduces150Movies418TagsAndPreservesSourceRows()
    {
        using var temp = new TempDirectory();
        var first = SrPocCatalog.Select(CanonicalCatalog, Path.Combine(temp.Path, "selection-a"));
        var second = SrPocCatalog.Select(CanonicalCatalog, Path.Combine(temp.Path, "selection-b"));
        Assert.AreEqual(150, first.MovieCount); Assert.AreEqual(418, first.TagCount); Assert.AreEqual(SelectionIdHash, first.IdSetSha256);
        Assert.AreEqual(first.SelectedSourceSha256, second.SelectedSourceSha256);
        var source = File.ReadAllLines(CanonicalCatalog).Select(line => JsonDocument.Parse(line)).ToDictionary(x => x.RootElement.GetProperty("movieLensId").GetInt64());
        var selectedLines = File.ReadAllLines(Path.Combine(temp.Path, "selection-a", "selected-source.jsonl"));
        Assert.AreEqual(150, selectedLines.Length);
        foreach (var line in selectedLines)
        {
            using var selected = JsonDocument.Parse(line); var id = selected.RootElement.GetProperty("movieLensId").GetInt64();
            Assert.AreEqual(source[id].RootElement.GetRawText(), selected.RootElement.GetRawText());
        }
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(temp.Path, "selection-a", "selection-manifest.json")));
        Assert.AreEqual(418, manifest.RootElement.GetProperty("selectedTagCount").GetInt32());
        Assert.AreEqual(SelectionIdHash, manifest.RootElement.GetProperty("selectedIdSetSha256").GetString());
        Assert.AreEqual("sr-poc-selection-v1", manifest.RootElement.GetProperty("selectorVersion").GetString());
        Assert.AreEqual("B05a-combined-catalog-v1", manifest.RootElement.GetProperty("sourceCatalogVersion").GetString());
        Assert.AreEqual("8b2bad0a22fef45842176a1d9f3730be1568e367b9fc230fa0aa398bb5c26946", manifest.RootElement.GetProperty("sourceCatalogSha256").GetString());
        Assert.AreEqual("2ffad7ba703cb80543db617e742a61c88871332910185767ee96fe08a77a0be7", manifest.RootElement.GetProperty("sourceContentFingerprint").GetString());
    }

    [TestMethod]
    public void BilingualBuilderPreservesAllSourceValuesAndWritesStableNumericOrderedCorpora()
    {
        using var temp = new TempDirectory();
        var selection = Path.Combine(temp.Path, "selection"); SrPocCatalog.Select(CanonicalCatalog, selection);
        var dictionary = CreateLockedDictionary(Path.Combine(temp.Path, "dictionary"), File.ReadAllText(Path.Combine(selection, "source-tags.json")));
        var one = SrPocCatalog.Build(CanonicalCatalog, dictionary, Path.Combine(temp.Path, "catalog-one"));
        var two = SrPocCatalog.Build(CanonicalCatalog, dictionary, Path.Combine(temp.Path, "catalog-two"));
        Assert.AreEqual(150, one.MovieCount); Assert.AreEqual(one.OutputSha256, two.OutputSha256);
        Assert.AreEqual(one.EnCorpusSha256, two.EnCorpusSha256); Assert.AreEqual(one.SrCorpusSha256, two.SrCorpusSha256);

        var source = File.ReadAllLines(Path.Combine(selection, "selected-source.jsonl")).Select(line => JsonDocument.Parse(line)).ToArray();
        var outputPath = Path.Combine(temp.Path, "catalog-one", "movies-catalog.jsonl");
        var output = File.ReadAllLines(outputPath).Select(line => JsonDocument.Parse(line)).ToArray();
        Assert.AreEqual(150, output.Length);
        var expectedFields = new[] { "movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw", "relevantTags", "semanticText", "tmdbId", "genres", "averageRating", "ratingCount", "runtimeMinutes", "originalLanguage", "posterPath", "tagsSr", "semanticTextSr" };
        for (var i = 0; i < output.Length; i++)
        {
            var row = output[i].RootElement; var original = source[i].RootElement;
            CollectionAssert.AreEqual(expectedFields, row.EnumerateObject().Select(x => x.Name).ToArray());
            foreach (var field in expectedFields.Take(17)) AssertJsonValue(original.GetProperty(field), row.GetProperty(field), field);
            var srTags = row.GetProperty("tagsSr").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var enTags = row.GetProperty("relevantTags").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToArray();
            var sortedTags = File.ReadAllText(Path.Combine(selection, "source-tags.json"));
            using var tagDoc = JsonDocument.Parse(sortedTags); var allTags = tagDoc.RootElement.EnumerateArray().Select(x => x.GetString()!).ToArray();
            CollectionAssert.AreEqual(enTags.Select(tag => TranslationFor(tag, allTags)).ToArray(), srTags);
            Assert.AreEqual(enTags.Length, srTags.Length);
            StringAssert.StartsWith(row.GetProperty("semanticTextSr").GetString()!, "Naslov: ");
            var director = original.GetProperty("directedByRaw"); var cast = original.GetProperty("starringRaw");
            var expectedText = $"Naslov: {original.GetProperty("title").GetString()}. Godina: {original.GetProperty("year").GetInt32()}. Režija: {RawName(director)}. Glumci: {RawName(cast)}. Tagovi: {(srTags.Length == 0 ? "nema" : string.Join(", ", srTags))}.";
            Assert.AreEqual(expectedText, row.GetProperty("semanticTextSr").GetString());
            Assert.IsFalse(row.GetProperty("semanticTextSr").GetString()!.Contains("passage:", StringComparison.OrdinalIgnoreCase));
        }
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(temp.Path, "catalog-one", "manifest.json")));
        Assert.AreEqual("bilingual-catalog-manifest-v1", manifest.RootElement.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(Sha256(File.ReadAllBytes(outputPath)), manifest.RootElement.GetProperty("output").GetProperty("sha256").GetString());
        Assert.AreEqual(one.EnCorpusSha256, manifest.RootElement.GetProperty("corpora").GetProperty("enSemanticTextSha256").GetString());
        Assert.AreEqual(one.SrCorpusSha256, manifest.RootElement.GetProperty("corpora").GetProperty("srSemanticTextSha256").GetString());
        Assert.AreEqual("B05a-bilingual-catalog-v2", manifest.RootElement.GetProperty("catalogVersion").GetString());
        Assert.AreEqual("sr-title-year-director-cast-tags-latn-v1", manifest.RootElement.GetProperty("dictionary").GetProperty("formatVersion").GetString());
        Assert.AreEqual(19, manifest.RootElement.GetProperty("output").GetProperty("schemaFieldCount").GetInt32());
        var expectedEnCorpus = CorpusHash(output.Select(x => (x.RootElement.GetProperty("movieLensId").GetInt64(), x.RootElement.GetProperty("semanticText").GetString()!)));
        var expectedSrCorpus = CorpusHash(output.Select(x => (x.RootElement.GetProperty("movieLensId").GetInt64(), x.RootElement.GetProperty("semanticTextSr").GetString()!)));
        Assert.AreEqual(expectedEnCorpus, one.EnCorpusSha256); Assert.AreEqual(expectedSrCorpus, one.SrCorpusSha256);
        Assert.AreEqual(CorpusHash([(10, "ten"), (2, "two")]), CorpusHash([(2, "two"), (10, "ten")]));
        Assert.AreNotEqual(CorpusHash([(10, "ten"), (2, "two")]), CorpusHash([(10, "ten"), (2, "two")], preserveInputOrder: true));

        foreach (var doc in source) doc.Dispose(); foreach (var doc in output) doc.Dispose();
    }

    [TestMethod]
    public void DictionaryRejectsWrongModelMetadataUnresolvedEntriesAndUnreviewedCollisions()
    {
        using var temp = new TempDirectory();
        var tags = Enumerable.Range(0, 418).Select(index => $"tag-{index:D3}").ToArray();
        var badModel = WriteDictionary(temp.Path, tags, mutate: root => root["translator"]!["modelId"] = "wrong/model");
        Assert.ThrowsExactly<ExportValidationException>(() => SrPocCatalog.ReadDictionaryForTests(badModel, tags));
        var unresolved = WriteDictionary(temp.Path, tags, mutate: root => root["entries"]![0]!["reviewStatus"] = "review_required");
        Assert.ThrowsExactly<ExportValidationException>(() => SrPocCatalog.ReadDictionaryForTests(unresolved, tags));
        var collision = WriteDictionary(temp.Path, tags, mutate: root => { root["entries"]![0]!["sr"] = "isto"; root["entries"]![1]!["sr"] = "isto"; });
        Assert.ThrowsExactly<ExportValidationException>(() => SrPocCatalog.ReadDictionaryForTests(collision, tags));
        var reviewedCollision = WriteDictionary(temp.Path, tags, mutate: root => { root["entries"]![0]!["sr"] = "isto"; root["entries"]![1]!["sr"] = "isto"; root["entries"]![0]!["reviewStatus"] = "reviewed"; root["entries"]![1]!["reviewStatus"] = "reviewed"; });
        Assert.AreEqual(418, SrPocCatalog.ReadDictionaryForTests(reviewedCollision, tags).Count);
    }

    [TestMethod]
    public void ExistingCatalogOutputIsNeverOverwritten()
    {
        using var temp = new TempDirectory();
        var selection = Path.Combine(temp.Path, "selection"); SrPocCatalog.Select(CanonicalCatalog, selection);
        var dictionary = CreateLockedDictionary(Path.Combine(temp.Path, "dictionary"), File.ReadAllText(Path.Combine(selection, "source-tags.json")));
        var existing = Path.Combine(temp.Path, "existing"); Directory.CreateDirectory(existing); var sentinel = Path.Combine(existing, "keep.txt"); File.WriteAllText(sentinel, "preserve");
        Assert.ThrowsExactly<IOException>(() => SrPocCatalog.Build(CanonicalCatalog, dictionary, existing));
        Assert.AreEqual("preserve", File.ReadAllText(sentinel));
    }

    [TestMethod]
    public void FailedPublishRemovesStagingAndLeavesTargetAbsent()
    {
        using var temp = new TempDirectory();
        var selection = Path.Combine(temp.Path, "selection"); SrPocCatalog.Select(CanonicalCatalog, selection);
        var dictionary = CreateLockedDictionary(Path.Combine(temp.Path, "dictionary"), File.ReadAllText(Path.Combine(selection, "source-tags.json")));
        var target = Path.Combine(temp.Path, "failed-publish");
        Assert.ThrowsExactly<InvalidOperationException>(() => SrPocCatalog.BuildForTests(CanonicalCatalog, dictionary, target, () => throw new InvalidOperationException("injected publish failure")));
        Assert.IsFalse(Directory.Exists(target));
        Assert.AreEqual(0, Directory.GetDirectories(temp.Path, "failed-publish.staging-*").Length);
    }

    [TestMethod]
    public void ReviewedSynonymTranslationsPreserveTagOrderAndMultiplicity()
    {
        using var temp = new TempDirectory();
        var selection = Path.Combine(temp.Path, "selection"); SrPocCatalog.Select(CanonicalCatalog, selection);
        var tagsPath = Path.Combine(selection, "source-tags.json");
        using var tagDoc = JsonDocument.Parse(File.ReadAllBytes(tagsPath)); var tags = tagDoc.RootElement.EnumerateArray().Select(x => x.GetString()!).ToArray();
        var dictionary = WriteDictionary(Path.Combine(temp.Path, "synonyms"), tags, root =>
        {
            var first = root["entries"]![0]!; var second = root["entries"]![1]!;
            first["sr"] = "zajednicki prevod"; second["sr"] = "zajednicki prevod";
            first["reviewStatus"] = "reviewed"; second["reviewStatus"] = "reviewed";
        });
        var outputDir = Path.Combine(temp.Path, "catalog"); SrPocCatalog.Build(CanonicalCatalog, dictionary, outputDir);
        var sourceRows = File.ReadAllLines(Path.Combine(selection, "selected-source.jsonl"));
        var outputRows = File.ReadAllLines(Path.Combine(outputDir, "movies-catalog.jsonl"));
        var orderedTags = tags.Order(StringComparer.Ordinal).ToArray();
        var duplicateTags = orderedTags.Take(2).ToArray();
        var index = Array.FindIndex(sourceRows, line => duplicateTags.All(tag => line.Contains("\"name\":\"" + tag + "\"", StringComparison.Ordinal)));
        Assert.IsTrue(index >= 0, "The chosen synonym pair should co-occur in a selected movie to exercise duplicate positional output.");
        using var output = JsonDocument.Parse(outputRows[index]);
        var srTags = output.RootElement.GetProperty("tagsSr").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.AreEqual(2, srTags.Count(x => x == "zajednicki prevod"));
    }

    [TestMethod]
    public void SerbianSemanticFormatterUsesLiteralFallbacksAndPreservesRawNames()
    {
        using var nullValue = JsonDocument.Parse("null");
        using var emptyValue = JsonDocument.Parse("\"\"");
        using var cyrillicDirector = JsonDocument.Parse("\"Андреј Тарковски\"");
        using var originalCast = JsonDocument.Parse("\"Guy Pearce, Марк Руффало\"");
        Assert.AreEqual(
            "Naslov: Иваново детињство. Godina: 1962. Režija: Андреј Тарковски. Glumci: Guy Pearce, Марк Руффало. Tagovi: nema.",
            SrPocCatalog.FormatSemanticTextSr("Иваново детињство", 1962, cyrillicDirector.RootElement, originalCast.RootElement, []));
        Assert.AreEqual(
            "Naslov: Original title. Godina: 2000. Režija: nepoznato. Glumci: nepoznato. Tagovi: prvi, drugi.",
            SrPocCatalog.FormatSemanticTextSr("Original title", 2000, nullValue.RootElement, emptyValue.RootElement, ["prvi", "drugi"]));
    }

    private static string CreateLockedDictionary(string directory, string tagsJson)
    {
        Directory.CreateDirectory(directory); var tagsPath = Path.Combine(directory, "tags.json"); File.WriteAllText(tagsPath, tagsJson);
        using var parsed = JsonDocument.Parse(File.ReadAllBytes(tagsPath)); var tags = parsed.RootElement.EnumerateArray().Select(x => x.GetString()!).ToArray();
        return WriteDictionary(directory, tags);
    }

    private static string WriteDictionary(string directory, string[] tags, Action<JsonObject>? mutate = null)
    {
        Directory.CreateDirectory(directory);
        var root = JsonNode.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = "tag-translations-sr-v1", sourceTagsSha256 = CanonicalTagHash(tags),
            translator = new { modelId = "Helsinki-NLP/opus-mt-en-sla", revision = "0bc26914f2f82c3dd5b235e420aa2c711a5ed3d8", targetToken = ">>srp_Latn<<", artifactHashes = ExpectedArtifacts(), runtimeLockSha256 = "2f0264292bdd1193745bded964afaa1d24a91ac4e3d7336c5673f5834ea5e90c", decoding = new { doSample = false, numBeams = 4, maxNewTokens = 32, sourceMaxTokens = 512, device = "cpu" } },
            normalizationVersion = "serbian-latin-nfc-whitespace-v1",
            entries = tags.Order(StringComparer.Ordinal).Select((tag, index) => new { en = tag, machine = "proposal " + index, sr = "prevod-" + index.ToString("D3"), reviewStatus = "auto_pass", manualOverride = false }).ToArray()
        }));
        mutate?.Invoke(root!.AsObject());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions { WriteIndented = true });
        var path = Path.Combine(directory, "tag-translations-sr.json"); File.WriteAllBytes(path, bytes); File.WriteAllText(Path.Combine(directory, "content.sha256"), Sha256(bytes) + "\n");
        return path;
    }

    private static object ExpectedArtifacts() => new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["config.json"] = "7036ccee5fdc00229b0c4482c1b67d017d72584c741c113021836895ae30b53a",
        ["generation_config.json"] = "69225dc7987a2833f266577d320c514e3c547eec6b5f75ca399aa8ab4d12ecd7",
        ["pytorch_model.bin"] = "1352ae4ef442420c47e9a9693a4baa2628be4579b167e04a801e57096f390196",
        ["source.spm"] = "7e262c2e51f67f8ddc3b08ca37381938ecc5cf43b52165054bb965483c3a4e68",
        ["target.spm"] = "948df13e89a108c933ffbf25f620ebf9b817cd68596179284329429469f27f8c",
        ["tokenizer_config.json"] = "03f32bb54014cfc720743d250fc5a519f2823b1d0110fa4865c76ba96bb2b188",
        ["vocab.json"] = "7824a39e5b838c2e0e1b8ef9b7e6a47821c804015dc90da0e4773712723d09a6"
    };
    private static string CanonicalTagHash(string[] tags) => Sha256(JsonSerializer.SerializeToUtf8Bytes(tags.Order(StringComparer.Ordinal)));
    private static string TranslationFor(string tag, string[] tags) => "prevod-" + Array.IndexOf(tags.Order(StringComparer.Ordinal).ToArray(), tag).ToString("D3");
    private static string RawName(JsonElement value) => value.ValueKind == JsonValueKind.Null || (value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0) ? "nepoznato" : value.GetString()!;
    private static string CorpusHash(IEnumerable<(long Id, string Text)> rows, bool preserveInputOrder = false)
    {
        using var stream = new MemoryStream();
        foreach (var (id, text) in preserveInputOrder ? rows : rows.OrderBy(x => x.Id))
        {
            using var item = new MemoryStream(); using (var writer = new Utf8JsonWriter(item, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            { writer.WriteStartObject(); writer.WriteNumber("movieLensId", id); writer.WriteString("semanticText", text); writer.WriteEndObject(); }
            stream.Write(item.ToArray()); stream.WriteByte((byte)'\n');
        }
        return Sha256(stream.ToArray());
    }
    private static void AssertJsonValue(JsonElement expected, JsonElement actual, string path)
    {
        Assert.AreEqual(expected.ValueKind, actual.ValueKind, path);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                CollectionAssert.AreEqual(expected.EnumerateObject().Select(x => x.Name).ToArray(), actual.EnumerateObject().Select(x => x.Name).ToArray());
                foreach (var property in expected.EnumerateObject()) AssertJsonValue(property.Value, actual.GetProperty(property.Name), path + "." + property.Name);
                break;
            case JsonValueKind.Array:
                var left = expected.EnumerateArray().ToArray(); var right = actual.EnumerateArray().ToArray(); Assert.AreEqual(left.Length, right.Length, path);
                for (var i = 0; i < left.Length; i++) AssertJsonValue(left[i], right[i], path + "[" + i + "]");
                break;
            case JsonValueKind.String: Assert.AreEqual(expected.GetString(), actual.GetString(), path); break;
            case JsonValueKind.Number: Assert.AreEqual(expected.GetRawText(), actual.GetRawText(), path); break;
            default: Assert.AreEqual(expected.GetRawText(), actual.GetRawText(), path); break;
        }
    }
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cinekros-sr-poc-tests-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
