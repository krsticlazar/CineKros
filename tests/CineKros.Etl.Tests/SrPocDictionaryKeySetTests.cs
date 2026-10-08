using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.Etl;

namespace CineKros.Etl.Tests;

[TestClass]
public sealed class SrPocDictionaryKeySetTests
{
    private const string AcceptedDictionary = "D:\\Repozitorijum\\CineKros\\database\\data\\derived\\serbian-search\\poc-v1\\translation\\tag-translations-sr.json";
    private const string SelectedTags = "D:\\Repozitorijum\\CineKros\\database\\data\\derived\\serbian-search\\poc-v1\\pre-review-catalog-01\\selection\\source-tags.json";

    [TestMethod]
    public void DictionaryMustContainExactlyTheExpected418KeysEvenWhenTamperedSidecarIsRecomputed()
    {
        var tags = JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(SelectedTags))!;
        Assert.AreEqual(418, tags.Length);
        using var temp = new TempDirectory();

        var valid = CopyAcceptedDictionary(temp.Path, "valid");
        Assert.AreEqual(418, SrPocCatalog.ReadDictionaryForTests(valid, tags).Count);

        var missing = CopyAcceptedDictionary(temp.Path, "missing");
        Mutate(missing, entries => entries.RemoveAt(0));
        AssertRejected(missing, tags);

        var replaced = CopyAcceptedDictionary(temp.Path, "replaced");
        Mutate(replaced, entries => entries[0]!["en"] = "not-a-selected-source-tag");
        AssertRejected(replaced, tags);

        var extra = CopyAcceptedDictionary(temp.Path, "extra");
        Mutate(extra, entries =>
        {
            var added = entries[0]!.DeepClone();
            added!["en"] = "unexpected-extra-tag";
            entries.Add(added);
        });
        AssertRejected(extra, tags);
    }

    private static string CopyAcceptedDictionary(string root, string name)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.Copy(AcceptedDictionary, Path.Combine(directory, "tag-translations-sr.json"));
        File.Copy(Path.Combine(Path.GetDirectoryName(AcceptedDictionary)!, "content.sha256"), Path.Combine(directory, "content.sha256"));
        return Path.Combine(directory, "tag-translations-sr.json");
    }

    private static void Mutate(string path, Action<JsonArray> mutateEntries)
    {
        var root = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        mutateEntries(root["entries"]!.AsArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "content.sha256"), Convert.ToHexStringLower(SHA256.HashData(bytes)) + "\n");
    }

    private static void AssertRejected(string path, string[] tags) =>
        Assert.ThrowsExactly<ExportValidationException>(() => SrPocCatalog.ReadDictionaryForTests(path, tags));

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cinekros-sr-dictionary-tests-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
