using System.Text;
using System.Text.Json;

namespace CineKros.Etl;

public static class SrPocReviewContext
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static int Write(string selectedSourcePath, string qaReportPath, string outputPath)
    {
        RequireAbsolute(selectedSourcePath, "selected-source"); RequireAbsolute(qaReportPath, "qa-report"); RequireAbsolute(outputPath, "output");
        if (!File.Exists(selectedSourcePath) || !File.Exists(qaReportPath)) throw new FileNotFoundException("Selected source or QA report is missing.");
        if (File.Exists(outputPath) || Directory.Exists(outputPath)) throw new IOException("Context output path already exists.");
        using var qa = JsonDocument.Parse(File.ReadAllBytes(qaReportPath)); var qaRoot = qa.RootElement;
        if (qaRoot.GetProperty("Version").GetString() != "tag-translation-qa-v1") throw new ExportValidationException("QA report version mismatch.");
        var qaEntries = qaRoot.GetProperty("Entries").EnumerateArray().Where(entry => entry.GetProperty("Flags").GetArrayLength() > 0)
            .ToDictionary(entry => entry.GetProperty("En").GetString()!, StringComparer.Ordinal);
        var allTags = new HashSet<string>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        using (var reader = new StreamReader(selectedSourcePath, Utf8, false, 65536))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                using var row = JsonDocument.Parse(line); var root = row.RootElement;
                var id = root.GetProperty("movieLensId").GetInt64(); var title = root.GetProperty("title").GetString()!;
                var tags = root.GetProperty("relevantTags").EnumerateArray().Select(tag => tag.GetProperty("name").GetString()!).ToArray();
                foreach (var tag in tags)
                {
                    allTags.Add(tag);
                    if (!qaEntries.ContainsKey(tag)) continue;
                    if (!occurrences.TryGetValue(tag, out var contexts)) occurrences[tag] = contexts = [];
                    if (contexts.Count < 3) contexts.Add(new { movieLensId = id, title, otherTags = tags.Where(other => !StringComparer.Ordinal.Equals(other, tag)).ToArray() });
                }
            }
        }
        var sourceHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(allTags.Order(StringComparer.Ordinal))));
        if (!StringComparer.Ordinal.Equals(sourceHash, qaRoot.GetProperty("SourceTagsSha256").GetString())) throw new ExportValidationException("QA report does not match selected source tags.");
        var entries = qaEntries.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
        {
            en = pair.Key,
            machine = pair.Value.GetProperty("Machine").GetString(),
            normalizedSr = pair.Value.GetProperty("Sr").GetString(),
            flags = pair.Value.GetProperty("Flags").EnumerateArray().Select(flag => new { code = flag.GetProperty("Code").GetString(), message = flag.GetProperty("Message").GetString() }).ToArray(),
            contexts = occurrences.GetValueOrDefault(pair.Key) ?? []
        }).ToArray();
        if (entries.Length != qaEntries.Count || qaEntries.Keys.Any(tag => !occurrences.ContainsKey(tag))) throw new ExportValidationException("A flagged tag has no selected movie context.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "sr-poc-review-context-v1", sourceTagsSha256 = sourceHash, flaggedCount = entries.Length, entries }, new JsonSerializerOptions { WriteIndented = true });
        var outputParent = Path.GetDirectoryName(Path.GetFullPath(outputPath))!; Directory.CreateDirectory(outputParent);
        using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(payload); stream.Flush(true);
        return entries.Length;
    }

    private static void RequireAbsolute(string path, string option) { if (!Path.IsPathFullyQualified(path)) throw new ArgumentException($"Option --{option} requires an absolute path."); }
}
