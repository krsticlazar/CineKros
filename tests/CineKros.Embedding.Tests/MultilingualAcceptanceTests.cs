using System.Security.Cryptography;
using System.Text.Json;
using CineKros.Embedding;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.Embedding.Tests;

[TestClass]
public sealed class MultilingualAcceptanceTests
{
    private static readonly string ModelDir = Environment.GetEnvironmentVariable("CINEKROS_MULTILINGUAL_E5_MODEL_ROOT")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/data/models/multilingual-e5-base/d128750597153bb5987e10b1c3493a34e5a4502a"));

    [TestMethod]
    public void AcceptedInt8ProfileMatchesFrozenTokenizerAndPythonOnnxAcrossAllCasesBatchOne()
    {
        const string fixtureSha = "c9023afeaef4710da94866d22d197a2c2c25deb5e6fedb96707d747d4e3c5488";
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "multilingual-e5-int8-accepted.json");
        Assert.AreEqual(fixtureSha, HashFile(fixturePath), "Accepted INT8 fixture changed after freeze.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        var root = doc.RootElement;
        Assert.AreEqual("intfloat/multilingual-e5-base", root.GetProperty("modelId").GetString());
        Assert.AreEqual("d128750597153bb5987e10b1c3493a34e5a4502a", root.GetProperty("revision").GetString());

        var profile = EmbeddingProfileDescriptor.MultilingualE5Base;
        Assert.AreEqual("eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe", profile.ProfileFingerprint);
        Assert.AreEqual("single-sequence-unpadded-v1", profile.InferenceShapePolicy);
        Assert.AreEqual("9411a2620fc30e348aa80c9d4e54ca0db5a00d94a92175c82ccdd47ad03b13e1", EmbeddingProfileDescriptor.LegacyEnglish.ProfileFingerprint);

        using var model = new E5EmbeddingModel(ModelDir, profile);
        Assert.AreEqual(profile.ProfileFingerprint, model.ProfileFingerprint);
        Assert.AreEqual(2, model.GraphInputs.Count);
        CollectionAssert.AreEquivalent(new[] { "input_ids", "attention_mask" }, model.GraphInputs.Select(x => x.Name).ToArray());
        Assert.AreEqual("last_hidden_state", model.GraphOutputs.Single().Name);
        CollectionAssert.AreEqual(new[] { -1, -1, 768 }, model.GraphOutputs.Single().Dimensions);

        var evidence = new List<object>();
        var before = model.InferenceRunCount;
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var kind = item.GetProperty("kind").GetString();
            var raw = item.GetProperty("raw").GetString()!;
            var formatted = kind == "query" ? E5EmbeddingModel.FormatQueryInput(raw) : E5EmbeddingModel.FormatDocumentInput(raw);
            Assert.AreEqual(item.GetProperty("formattedText").GetString(), formatted.Normalize(System.Text.NormalizationForm.FormC), $"NFC formatted input changed for {id}.");
            var uncapped = model.TokenizeUncapped(formatted).InputIds;
            var expectedUncapped = Longs(item.GetProperty("uncappedTokenIds"));
            CollectionAssert.AreEqual(expectedUncapped, uncapped, $"Uncapped token IDs changed for {id}.");
            Assert.AreEqual(item.GetProperty("uncappedTokenCount").GetInt32(), uncapped.Length, id);
            var tokens = model.Tokenize(formatted);
            var expectedTokens = Longs(item.GetProperty("tokenIds"));
            CollectionAssert.AreEqual(expectedTokens, tokens.InputIds, $"Capped token IDs changed for {id}.");
            Assert.AreEqual(expectedTokens.Length, tokens.AttentionMask.Length, id);
            Assert.IsTrue(tokens.AttentionMask.All(x => x == 1), $"Batch-1 mask has padding for {id}.");
            Assert.AreEqual(0L, tokens.InputIds[0], $"BOS ID changed for {id}.");
            Assert.AreEqual(2L, tokens.InputIds[^1], $"EOS ID was not preserved for {id}.");
            var actuallyTruncated = uncapped.Length > 512;
            Assert.AreEqual(item.GetProperty("truncated").GetBoolean(), actuallyTruncated, $"Truncation label mismatch for {id}.");
            var expectedCapped = actuallyTruncated ? uncapped.Take(511).Append(2L).ToArray() : uncapped;
            CollectionAssert.AreEqual(expectedCapped, tokens.InputIds, $"512-token EOS-preserving truncation changed for {id}.");
            var vector = kind == "query" ? model.EmbedQuery(raw) : model.EmbedDocument(raw);
            var expectedVector = Floats(item.GetProperty("normalizedVector"));
            var cosine = Cosine64(vector, expectedVector);
            var maxAbs = MaxAbs(vector, expectedVector);
            var normError = Math.Abs(Math.Sqrt(vector.Sum(x => (double)x * x)) - 1d);
            evidence.Add(new { id, kind, formatted, uncappedTokens = uncapped.Length, cappedTokens = tokens.InputIds.Length, actuallyTruncated, batchSize = model.LastInferenceBatchSize, cosine, maxAbs, normError });
            Assert.AreEqual(1, model.LastInferenceBatchSize, id);
            Assert.AreEqual(768, vector.Length, id);
            Assert.IsTrue(vector.All(float.IsFinite), id);
            Assert.IsTrue(cosine >= 0.999999d, $"{id} trusted INT8 cosine {cosine:R} < 0.999999.");
            Assert.IsTrue(maxAbs <= 1e-5d, $"{id} trusted INT8 maxAbs {maxAbs:R} > 1e-5.");
            Assert.IsTrue(normError <= 1e-4d, $"{id} norm error {normError:R} > 1e-4.");
        }
        Assert.AreEqual(11L, model.InferenceRunCount - before, "One actual graph execution is required per frozen case.");
        Assert.ThrowsExactly<ArgumentException>(() => model.TokenizeBatch(new[] { "one", "two" }));
        var truncCase = root.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "P11");
        Assert.IsTrue(truncCase.GetProperty("truncated").GetBoolean());
        Assert.AreEqual(512, truncCase.GetProperty("tokenCount").GetInt32());
        Assert.IsFalse(root.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "P10").GetProperty("truncated").GetBoolean(), "A capped length of 512 alone is not proof of truncation.");

        var docs = root.GetProperty("cases").EnumerateArray().Where(x => x.GetProperty("kind").GetString() == "passage").ToDictionary(x => x.GetProperty("id").GetString()!, x => x, StringComparer.Ordinal);
        var repeatedDocuments = new[] { docs["P02"].GetProperty("raw").GetString()!, docs["P04"].GetProperty("raw").GetString()!, docs["P02"].GetProperty("raw").GetString()! };
        var collectionBefore = model.InferenceRunCount;
        var collectionVectors = model.EmbedDocuments(repeatedDocuments);
        Assert.AreEqual(collectionBefore + repeatedDocuments.Length, model.InferenceRunCount, "Each target collection document must execute independently.");
        Assert.AreEqual(1, model.LastInferenceBatchSize);
        Assert.AreEqual(3, collectionVectors.Length);
        Assert.IsTrue(Cosine64(collectionVectors[0], collectionVectors[2]) > 0.999999999d, "Repeated text changed across one collection call.");
        foreach (var pair in new[] { (0, "P02"), (1, "P04") })
        {
            var reference = Floats(docs[pair.Item2].GetProperty("normalizedVector"));
            Assert.IsTrue(Cosine64(collectionVectors[pair.Item1], reference) >= 0.999999d);
            Assert.IsTrue(MaxAbs(collectionVectors[pair.Item1], reference) <= 1e-5d);
        }
        var emptyBefore = model.InferenceRunCount;
        Assert.ThrowsExactly<ArgumentException>(() => model.EmbedQuery(" \t\n"));
        Assert.AreEqual(emptyBefore, model.InferenceRunCount, "Whitespace query must be rejected before graph execution.");

        const string documentFingerprint = "59c3b0e47de999813ccc0c7dbd95dd09e660170e04122e477fb7d06a106c3a49";
        Assert.AreEqual(documentFingerprint, model.ComputeDocumentFingerprint("A film text."));
        Assert.AreEqual(model.ComputeDocumentFingerprint("Cafe\u0301"), model.ComputeDocumentFingerprint("Café"), "NFC-equivalent formatted passage fingerprints should agree.");
        Assert.AreNotEqual(model.ComputeDocumentFingerprint("A film text."), model.ComputeDocumentFingerprint("A different film text."));

        var reportPath = Environment.GetEnvironmentVariable("CINEKROS_PHASE1_FINAL_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { fixtureSha256 = HashFile(fixturePath), profile = profile.ProfileVersion, profileFingerprint = profile.ProfileFingerprint, cases = evidence }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [TestMethod]
    public void ExplicitWrongProfileAndLegacyDefaultRemainSeparated()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => new E5EmbeddingModel(ModelDir, EmbeddingProfileDescriptor.LegacyEnglish));
        var oldDir = Environment.GetEnvironmentVariable("CINEKROS_E5_MODEL_ROOT")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/data/models/e5-base-v2/f52bf8ec8c7124536f0efb74aca902b2995e5bcd"));
        Assert.ThrowsExactly<InvalidDataException>(() => new E5EmbeddingModel(oldDir, EmbeddingProfileDescriptor.MultilingualE5Base));
        using var legacy = new E5EmbeddingModel(oldDir);
        Assert.AreEqual(EmbeddingProfileDescriptor.LegacyEnglish.ProfileFingerprint, legacy.ProfileFingerprint);
        var before = legacy.InferenceRunCount;
        var values = legacy.EmbedDocuments(new[] { "English legacy one", "English legacy two" });
        Assert.AreEqual(2, values.Length);
        Assert.AreEqual(before + 1, legacy.InferenceRunCount, "Legacy default retains its original batched inference behavior.");
        Assert.AreEqual(2, legacy.LastInferenceBatchSize);
    }

    private static long[] Longs(JsonElement array) => array.EnumerateArray().Select(x => x.GetInt64()).ToArray();
    private static float[] Floats(JsonElement array) => array.EnumerateArray().Select(x => x.GetSingle()).ToArray();
    private static double Cosine64(float[] a, float[] b)
    {
        Assert.AreEqual(a.Length, b.Length); double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++) { dot += (double)a[i] * b[i]; aa += (double)a[i] * a[i]; bb += (double)b[i] * b[i]; }
        return dot / Math.Sqrt(aa * bb);
    }
    private static double MaxAbs(float[] a, float[] b) { Assert.AreEqual(a.Length, b.Length); return a.Select((v, i) => Math.Abs((double)v - b[i])).Max(); }
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
}
