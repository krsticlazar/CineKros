using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CineKros.Translator;

public sealed record ModelManifest(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("targetToken")] string TargetToken,
    [property: JsonPropertyName("artifactHashes")] IReadOnlyDictionary<string, string> ArtifactHashes,
    [property: JsonPropertyName("expectedGitOids")] IReadOnlyDictionary<string, string> ExpectedGitOids,
    [property: JsonPropertyName("license")] string License)
{
    public static ModelManifest Load(string modelDirectory)
    {
        var path = Path.Combine(modelDirectory, "model-manifest.json");
        TagWorkflow.RequireFile(path, "model manifest");
        var manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("model manifest is invalid");
        if (manifest.SchemaVersion != "opus-mt-pinned-model-v1" || manifest.ModelId != TranslatorConstants.ModelId ||
            manifest.Revision != TranslatorConstants.ModelRevision || manifest.TargetToken != TranslatorConstants.TargetToken ||
            manifest.License != "Apache-2.0")
            throw new InvalidDataException("model manifest does not match the locked model identity");

        var expectedNames = ModelBootstrap.ExpectedArtifacts.Keys.Order(StringComparer.Ordinal).ToArray();
        if (!manifest.ArtifactHashes.Keys.Order(StringComparer.Ordinal).SequenceEqual(expectedNames, StringComparer.Ordinal) ||
            !manifest.ExpectedGitOids.Keys.Order(StringComparer.Ordinal).SequenceEqual(expectedNames, StringComparer.Ordinal))
            throw new InvalidDataException("model manifest artifact list is incomplete or unexpected");
        foreach (var (name, expected) in ModelBootstrap.ExpectedArtifacts)
        {
            var file = Path.Combine(modelDirectory, name);
            TagWorkflow.RequireFile(file, "pinned model artifact");
            if (!manifest.ExpectedGitOids.TryGetValue(name, out var expectedOid) || !StringComparer.Ordinal.Equals(expectedOid, expected.GitOid))
                throw new InvalidDataException($"model manifest pins an unexpected Git object: {name}");
            var expectedHash = manifest.ArtifactHashes[name];
            if (!StringComparer.Ordinal.Equals(HashFile(file), expectedHash))
                throw new InvalidDataException($"pinned model artifact hash mismatch: {name}");
            if (new FileInfo(file).Length != expected.Size)
                throw new InvalidDataException($"pinned model artifact size mismatch: {name}");
            if (expected.LfsSha256 is not null)
            {
                if (!StringComparer.Ordinal.Equals(expectedHash, expected.LfsSha256))
                    throw new InvalidDataException("pinned model LFS SHA-256 mismatch");
            }
            else if (!StringComparer.Ordinal.Equals(HashGitBlob(file), expected.GitOid))
                throw new InvalidDataException($"pinned model Git object mismatch: {name}");
        }
        return manifest;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string HashGitBlob(string path)
    {
        var length = new FileInfo(path).Length;
        var prefix = System.Text.Encoding.ASCII.GetBytes($"blob {length}\0");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(prefix);
        using var stream = File.OpenRead(path);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            hash.AppendData(buffer, 0, read);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

public static class ModelBootstrap
{
    public sealed record ExpectedArtifact(long Size, string GitOid, string? LfsSha256 = null);

    public static readonly IReadOnlyDictionary<string, ExpectedArtifact> ExpectedArtifacts = new Dictionary<string, ExpectedArtifact>(StringComparer.Ordinal)
    {
        ["config.json"] = new(1395, "d9cc6c5300415e8f1b97b335a9f00c692d34b791"),
        ["generation_config.json"] = new(293, "b5578ddff28fb5bcc5f5ca959fa9df6c2b310230"),
        ["pytorch_model.bin"] = new(299524665, "bed73c55e2a1a84d8d522448619379e588801876", "1352ae4ef442420c47e9a9693a4baa2628be4579b167e04a801e57096f390196"),
        ["source.spm"] = new(791358, "c232215fa17d9fe3927d8f6220337bf48379396a"),
        ["target.spm"] = new(860404, "c749c776ad99a3f9ede35b4a08e303e59b874cbc"),
        ["tokenizer_config.json"] = new(44, "d30e15f9c8d41fa0f1467b3693cfbb92280ff708"),
        ["vocab.json"] = new(1714992, "dafdb4bdc9b04b5feb48592010ff2efb5201226d")
    };
}
