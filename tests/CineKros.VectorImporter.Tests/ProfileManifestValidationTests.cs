using System.Text.Json;
using CineKros.Embedding;
using CineKros.VectorImporter;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.VectorImporter.Tests;

[TestClass]
public sealed class ProfileManifestValidationTests
{
    [TestMethod]
    public void PureProfileValidationAcceptsOnlyExactLockedIdentityAndShapePolicy()
    {
        var legacy = EmbeddingProfileDescriptor.LegacyEnglish;
        var target = EmbeddingProfileDescriptor.MultilingualE5Base;
        using var legacyJson = JsonDocument.Parse(Manifest(legacy, includeShape: false));
        using var targetJson = JsonDocument.Parse(Manifest(target, includeShape: true));
        Assert.IsTrue(VectorArtifactValidator.ValidateProfileManifest(legacyJson.RootElement, legacy));
        Assert.IsTrue(VectorArtifactValidator.ValidateProfileManifest(targetJson.RootElement, target));
        Assert.IsFalse(VectorArtifactValidator.ValidateProfileManifest(targetJson.RootElement, legacy));
        Assert.IsFalse(VectorArtifactValidator.ValidateProfileManifest(legacyJson.RootElement, target));

        using var altered = JsonDocument.Parse(Manifest(target, includeShape: true, shape: "padded-batch-v1"));
        Assert.IsFalse(VectorArtifactValidator.ValidateProfileManifest(altered.RootElement, target));
    }

    private static string Manifest(EmbeddingProfileDescriptor profile, bool includeShape, string? shape = null)
    {
        var onnx = profile.Artifact("model_qint8_avx512_vnni.onnx").Sha256;
        var tokenizer = profile.Artifact("tokenizer.json").Sha256;
        var manifest = new Dictionary<string, object?>
        {
            ["format"] = "e5-document-vectors-jsonl-v1", ["catalogVersion"] = "catalog-v1", ["catalogSha256"] = new string('a', 64),
            ["catalogContentFingerprint"] = new string('b', 64), ["profile"] = profile.ProfileVersion, ["profileFingerprint"] = profile.ProfileFingerprint,
            ["modelId"] = profile.ModelId, ["modelRevision"] = profile.Revision, ["onnxSha256"] = onnx, ["tokenizerSha256"] = tokenizer,
            ["dimension"] = profile.Dimension, ["documentInputFormat"] = "e5-passage-semantictext-v1", ["fingerprintAlgorithm"] = "sha256-compact-json-e5-v1",
            ["pooling"] = profile.Pooling, ["normalization"] = profile.Normalization, ["maxTokens"] = profile.MaxTokens,
            ["outputSha256"] = new string('c', 64), ["recordCount"] = 1, ["order"] = "movieLensId ascending", ["failedRecords"] = 0,
            ["truncatedRecords"] = 0, ["batchSize"] = 1, ["elapsedMilliseconds"] = 1, ["provenance"] = "offline test"
        };
        if (includeShape) manifest["inferenceShapePolicy"] = shape ?? profile.InferenceShapePolicy;
        return JsonSerializer.Serialize(manifest);
    }
}
