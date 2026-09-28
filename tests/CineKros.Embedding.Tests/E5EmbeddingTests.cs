using CineKros.Embedding;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.Embedding.Tests;

[TestClass]
public sealed class E5EmbeddingTests
{
    private static readonly string ModelDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/data/models/e5-base-v2/f52bf8ec8c7124536f0efb74aca902b2995e5bcd"));
    [TestMethod]
    public void LoadsPinnedAssetsTokenizesAndComputesStableFingerprints()
    {
        using var model = new E5EmbeddingModel(ModelDir);
        Assert.AreEqual("9411a2620fc30e348aa80c9d4e54ca0db5a00d94a92175c82ccdd47ad03b13e1", model.ProfileFingerprint);
        var tokenized = model.Tokenize("PASSAGE: Amélie — café");
        Assert.IsTrue(tokenized.InputIds.Length >= 4);
        Assert.AreEqual(101L, tokenized.InputIds[0]);
        Assert.AreEqual(102L, tokenized.InputIds[^1]);
        Assert.AreEqual(tokenized.InputIds.Length, tokenized.AttentionMask.Length);
        var batch = model.TokenizeBatch(new[] { "short", string.Join(' ', Enumerable.Repeat("longer", 700)) });
        Assert.AreEqual(512, batch.InputIds.GetLength(1));
        Assert.AreEqual(1L, batch.AttentionMask[0, 0]);
        Assert.AreEqual(0L, batch.AttentionMask[0, 20]);
        Assert.AreEqual(1, model.TruncationCount);
        Assert.AreEqual(102L, batch.InputIds[1, 511], "Truncation must preserve terminal [SEP].");
        const string paddedText = "  unchanged document text  ";
        Assert.AreEqual("passage: " + paddedText, E5EmbeddingModel.FormatDocumentInput(paddedText));
        Assert.AreEqual(ExpectedDocumentFingerprint(paddedText), model.ComputeDocumentFingerprint(paddedText));
        Assert.AreNotEqual(model.ComputeDocumentFingerprint(paddedText), model.ComputeDocumentFingerprint(paddedText.Trim()));
        Assert.AreEqual(model.ComputeDocumentFingerprint("A film text."), model.ComputeDocumentFingerprint("A film text."));
        Assert.AreNotEqual(model.ComputeDocumentFingerprint("A film text."), model.ComputeDocumentFingerprint("Other text."));
        Assert.ThrowsExactly<ArgumentException>(() => model.EmbedQuery("  "));
    }
    [TestMethod]
    public void PoolsOnlyAttendedTokensAndNormalizes()
    {
        var h = new float[1, 3, 768]; h[0,0,0]=3; h[0,1,0]=1; h[0,2,0]=100; h[0,0,1]=4; h[0,1,1]=0; h[0,2,1]=100;
        var v = E5EmbeddingModel.MaskedMeanPool(h, new long[,] { { 1, 1, 0 } });
        Assert.AreEqual(0.70710677f, v[0], 0.0001f); Assert.AreEqual(0.70710677f, v[1], 0.0001f);
        Assert.AreEqual(1d, Math.Sqrt(v.Sum(x => (double)x*x)), 0.00001d);
    }
    [TestMethod]
    public void RunsPinnedOnnxAndRejectsTampering()
    {
        using var model = new E5EmbeddingModel(ModelDir);
        AssertVector(model.EmbedDocument("A quiet science fiction film about memory."));
        AssertVector(model.EmbedQuery("quiet science fiction about memory"));
        var priorRuns = model.InferenceRunCount;
        var documents = model.EmbedDocuments(new[] { "  Preserve document whitespace  ", "A second document for the same ONNX batch." });
        Assert.AreEqual(priorRuns + 1, model.InferenceRunCount, "A document batch must execute exactly one ONNX Run.");
        Assert.AreEqual(2, model.LastInferenceBatchSize);
        Assert.AreEqual(2, documents.Length);
        foreach (var vector in documents) AssertVector(vector);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 3; i++) model.EmbedQuery("quiet science fiction about memory");
        sw.Stop(); Assert.IsTrue(sw.Elapsed > TimeSpan.Zero);
        Assert.IsTrue(model.LoadTime > TimeSpan.Zero);
        var damaged = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(damaged);
        try { File.Copy(Path.Combine(ModelDir, "tokenizer.json"), Path.Combine(damaged, "tokenizer.json")); Assert.ThrowsExactly<InvalidDataException>(() => new E5EmbeddingModel(damaged)); }
        finally { Directory.Delete(damaged, true); }
    }
    private static string ExpectedDocumentFingerprint(string semanticText)
    {
        var record = new
        {
            formattedText = "passage: " + semanticText,
            modelId = "intfloat/e5-base-v2",
            revision = "f52bf8ec8c7124536f0efb74aca902b2995e5bcd",
            onnxSha256 = "f2ff55f62dfca9ce0f4a5656ae0b1571b9fbc5e15eda3b2c56dd32f329b2005e",
            tokenizerSha256 = "d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66",
            dimension = 768,
            task = "passage",
            formattingVersion = "e5-passage-semantictext-v1",
            maxTokens = 512,
            pooling = "mask-mean-v1",
            normalization = "l2-f32-v1"
        };
        var json = System.Text.Json.JsonSerializer.Serialize(record, new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
    }
    private static void AssertVector(float[] v) { Assert.AreEqual(768,v.Length); Assert.IsTrue(v.All(float.IsFinite)); Assert.AreEqual(1d,Math.Sqrt(v.Sum(x=>(double)x*x)),0.00001d); }
}
