using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CineKros.Etl;

internal sealed record FakeEmbeddingConfiguration(string ModelId, string ModelVersion, int Dimension, string DocumentTaskType, string FormattingVersion);
internal interface IFakeDocumentVectorSource { double[] Generate(string semanticText, string fingerprint, FakeEmbeddingConfiguration configuration); }
internal sealed record FakeEmbeddingResult(int OutputRecords, int Generated, int Reused, string OutputSha256);

internal sealed class DeterministicFakeDocumentVectorSource : IFakeDocumentVectorSource
{
    public double[] Generate(string semanticText, string fingerprint, FakeEmbeddingConfiguration configuration)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)); var values = new double[configuration.Dimension];
        for (var i = 0; i < values.Length; i++) values[i] = (seed[i % seed.Length] / 127.5) - 1.0;
        if (values.All(x => x == 0)) values[0] = 1;
        return values;
    }
}

internal static class FakeDocumentEmbeddingRunner
{
    private const string CatalogVersion = "B05a-combined-catalog-v1";
    internal static string Fingerprint(string semanticText, FakeEmbeddingConfiguration c)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["semanticText"] = semanticText, ["modelId"] = c.ModelId, ["modelVersion"] = c.ModelVersion, ["dimension"] = c.Dimension, ["documentTaskType"] = c.DocumentTaskType, ["formattingVersion"] = c.FormattingVersion });
        return Hash(Encoding.UTF8.GetBytes(json));
    }

    internal static FakeEmbeddingResult Run(string catalogPath, string manifestPath, string outputDirectory, string checkpointPath,
        FakeEmbeddingConfiguration config, IFakeDocumentVectorSource source, CancellationToken cancellationToken = default, Action? beforePublish = null)
    {
        ValidateConfiguration(config); ArgumentNullException.ThrowIfNull(source);
        if (!Path.IsPathFullyQualified(catalogPath) || !Path.IsPathFullyQualified(manifestPath) || !Path.IsPathFullyQualified(outputDirectory) || !Path.IsPathFullyQualified(checkpointPath)) throw new ArgumentException("All paths must be absolute.");
        if (!File.Exists(catalogPath) || !File.Exists(manifestPath)) throw new ExportValidationException("Catalog or manifest is missing.");
        var catalogHash = HashFile(catalogPath); var records = new List<(long Id, string Text, string Fingerprint)>(); long prior = 0;
        try
        {
            using var md = JsonDocument.Parse(File.ReadAllText(manifestPath)); var m = md.RootElement;
            if (GetString(m, "catalogVersion") != CatalogVersion || GetString(m.GetProperty("output"), "sha256") != catalogHash || GetInt(m.GetProperty("output"), "recordCount") < 1 || GetString(m.GetProperty("output"), "order") != "movieLensId ascending") throw new ExportValidationException("Catalog manifest does not validate its input.");
            using var reader = new StreamReader(catalogPath, new UTF8Encoding(false, true)); string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested(); using var row = JsonDocument.Parse(line); var r = row.RootElement;
                if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("movieLensId", out var idNode) || !idNode.TryGetInt64(out var id) || id <= prior || !r.TryGetProperty("semanticText", out var textNode) || textNode.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(textNode.GetString())) throw new ExportValidationException("Catalog IDs or required semanticText are invalid.");
                prior = id; var text = textNode.GetString()!; records.Add((id, text, Fingerprint(text, config)));
            }
            if (records.Count != GetInt(m.GetProperty("output"), "recordCount") || records.Count == 0) throw new ExportValidationException("Catalog record count mismatch.");
        }
        catch (ExportValidationException) { throw; }
        catch (Exception ex) when (ex is JsonException or IOException or DecoderFallbackException or InvalidOperationException or KeyNotFoundException) { throw new ExportValidationException("Catalog or manifest is malformed."); }

        var entries = ReadCheckpoint(checkpointPath, config);
        NormalizeCheckpoint(checkpointPath, config, records, entries);
        int generated = 0, reused = 0;
        foreach (var rec in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.TryGetValue(rec.Id, out var priorEntry) && priorEntry.Fingerprint == rec.Fingerprint && ValidVector(priorEntry.Vector, config.Dimension)) { reused++; continue; }
            try
            {
                var vector = source.Generate(rec.Text, rec.Fingerprint, config); ValidateVector(vector, config.Dimension);
                entries[rec.Id] = new Entry(rec.Fingerprint, vector, null); generated++; SaveCheckpoint(checkpointPath, config, rec.Id, entries[rec.Id]);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { entries[rec.Id] = new Entry(rec.Fingerprint, null, ex.GetType().Name); SaveCheckpoint(checkpointPath, config, rec.Id, entries[rec.Id]); }
        }
        if (records.Any(r => !entries.TryGetValue(r.Id, out var e) || e.Fingerprint != r.Fingerprint || !ValidVector(e.Vector, config.Dimension))) throw new ExportValidationException("One or more document vectors failed generation.");
        Publish(outputDirectory, checkpointPath, catalogHash, config, records, entries, cancellationToken, beforePublish);
        return new(records.Count, generated, reused, HashFile(Path.Combine(outputDirectory, "document-vectors.jsonl")));
    }

    private sealed record Entry(string Fingerprint, double[]? Vector, string? Error);
    private static Dictionary<long, Entry> ReadCheckpoint(string path, FakeEmbeddingConfiguration c)
    {
        var result = new Dictionary<long, Entry>(); if (!File.Exists(path)) return result;
        string[] lines;
        try { lines = File.ReadAllText(path, new UTF8Encoding(false, true)).Split('\n'); }
        catch { return result; }
        var validHeader = false;
        if (lines.Length > 0)
        {
            try { using var header = JsonDocument.Parse(lines[0].TrimEnd('\r')); var root = header.RootElement;
                validHeader = GetString(root, "fakeMarker") == "FAKE ONLY - NOT PRODUCTION VECTORS" && GetString(root, "checkpointFormat") == "fake-document-vectors-jsonl-v1" && SameConfig(root.GetProperty("configuration"), c);
            } catch { /* A malformed header does not make independently parseable journal records trustworthy by itself. */ }
        }
        // When the header is corrupt, later lines can still be recovered independently;
        // Run filters every one against current IDs, fingerprints, dimensions and vector validity.
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            try { using var doc = JsonDocument.Parse(lines[i].TrimEnd('\r')); var item = doc.RootElement;
                var id = GetInt64(item, "movieLensId"); var fp = GetString(item, "fingerprint");
                if (id <= 0 || fp.Length != 64 || fp.Any(ch => !Uri.IsHexDigit(ch)) || fp != fp.ToLowerInvariant()) continue;
                double[]? vector = null;
                if (item.TryGetProperty("vector", out var vectorNode) && vectorNode.ValueKind == JsonValueKind.Array)
                {
                    var values = new List<double>(); foreach (var value in vectorNode.EnumerateArray()) { if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)) { values.Clear(); break; } values.Add(number); } vector = values.ToArray();
                }
                string? error = item.TryGetProperty("error", out var errorNode) && errorNode.ValueKind == JsonValueKind.String ? errorNode.GetString() : null;
                if ((vector is null || !ValidVector(vector, c.Dimension)) && error is null) continue;
                result[id] = new(fp, vector, error);
            } catch { /* Ignore malformed records, including torn final lines and malformed middle entries. */ }
        }
        _ = validHeader; // Header validity controls config trust; record validity is independently checked above.
        return result;
    }
    private static void NormalizeCheckpoint(string path, FakeEmbeddingConfiguration c, List<(long Id, string Text, string Fingerprint)> records, Dictionary<long, Entry> entries)
    {
        var current = records.ToDictionary(x => x.Id, x => x.Fingerprint);
        foreach (var id in entries.Keys.ToArray())
            if (!current.TryGetValue(id, out var fp) || entries[id].Fingerprint != fp || (!ValidVector(entries[id].Vector, c.Dimension) && entries[id].Error is null)) entries.Remove(id);
        var temp = path + ".rewrite-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WriteCheckpointHeader(stream, c);
                foreach (var item in entries.OrderBy(x => x.Key)) WriteCheckpointEntry(stream, item.Key, item.Value);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void SaveCheckpoint(string path, FakeEmbeddingConfiguration c, long id, Entry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var lineStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read); WriteCheckpointEntry(lineStream, id, entry); lineStream.Flush(true);
    }
    private static void WriteCheckpointHeader(Stream stream, FakeEmbeddingConfiguration c)
    { using var w = new Utf8JsonWriter(stream); w.WriteStartObject(); w.WriteString("fakeMarker", "FAKE ONLY - NOT PRODUCTION VECTORS"); w.WriteString("checkpointFormat", "fake-document-vectors-jsonl-v1"); WriteConfig(w, c); w.WriteEndObject(); w.Flush(); stream.WriteByte((byte)'\n'); }
    private static void WriteCheckpointEntry(Stream stream, long id, Entry entry)
    { using var w = new Utf8JsonWriter(stream); w.WriteStartObject(); w.WriteNumber("movieLensId", id); w.WriteString("fingerprint", entry.Fingerprint); if (entry.Vector is not null) { w.WriteStartArray("vector"); foreach (var n in entry.Vector) w.WriteNumberValue(n); w.WriteEndArray(); } if (entry.Error is not null) w.WriteString("error", entry.Error); w.WriteEndObject(); w.Flush(); stream.WriteByte((byte)'\n'); }
    private static void Publish(string output, string checkpoint, string catalogHash, FakeEmbeddingConfiguration c, List<(long Id, string Text, string Fingerprint)> records, Dictionary<long, Entry> entries, CancellationToken token, Action? beforePublish)
    {
        var parent = Path.GetDirectoryName(output)!; Directory.CreateDirectory(parent); var staging = output + ".staging-" + Guid.NewGuid().ToString("N"); var backup = output + ".backup-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(staging);
        try { var jsonPath = Path.Combine(staging, "document-vectors.jsonl"); using (var fs = new FileStream(jsonPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) foreach (var r in records) { token.ThrowIfCancellationRequested(); using var w = new Utf8JsonWriter(fs); w.WriteStartObject(); w.WriteNumber("movieLensId", r.Id); w.WriteString("fingerprint", r.Fingerprint); w.WriteString("fakeMarker", "FAKE ONLY - NOT PRODUCTION VECTORS"); w.WriteStartArray("vector"); foreach (var v in entries[r.Id].Vector!) w.WriteNumberValue(v); w.WriteEndArray(); w.WriteEndObject(); w.Flush(); fs.WriteByte((byte)'\n'); } var hash = HashFile(jsonPath); using (var fs = File.Create(Path.Combine(staging, "manifest.json"))) using (var w = new Utf8JsonWriter(fs)) { w.WriteStartObject(); w.WriteString("fakeMarker", "FAKE ONLY - NOT PRODUCTION VECTORS"); w.WriteString("catalogVersion", CatalogVersion); w.WriteString("catalogSha256", catalogHash); w.WriteString("outputSha256", hash); w.WriteNumber("recordCount", records.Count); WriteConfig(w, c); w.WriteEndObject(); }
            beforePublish?.Invoke(); token.ThrowIfCancellationRequested(); if (Directory.Exists(output)) { Directory.Move(output, backup); try { Directory.Move(staging, output); } catch { Directory.Move(backup, output); throw; } Directory.Delete(backup, true); } else Directory.Move(staging, output);
        } finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); if (Directory.Exists(backup) && !Directory.Exists(output)) Directory.Move(backup, output); }
    }
    private static void WriteConfig(Utf8JsonWriter w, FakeEmbeddingConfiguration c) { w.WriteStartObject("configuration"); w.WriteString("modelId", c.ModelId); w.WriteString("modelVersion", c.ModelVersion); w.WriteNumber("dimension", c.Dimension); w.WriteString("documentTaskType", c.DocumentTaskType); w.WriteString("formattingVersion", c.FormattingVersion); w.WriteEndObject(); }
    private static bool SameConfig(JsonElement e, FakeEmbeddingConfiguration c) => GetString(e,"modelId")==c.ModelId && GetString(e,"modelVersion")==c.ModelVersion && GetInt(e,"dimension")==c.Dimension && GetString(e,"documentTaskType")==c.DocumentTaskType && GetString(e,"formattingVersion")==c.FormattingVersion;
    private static void ValidateConfiguration(FakeEmbeddingConfiguration c) { if (c is null || string.IsNullOrWhiteSpace(c.ModelId) || string.IsNullOrWhiteSpace(c.ModelVersion) || string.IsNullOrWhiteSpace(c.DocumentTaskType) || string.IsNullOrWhiteSpace(c.FormattingVersion) || c.Dimension <= 0) throw new ArgumentException("All fake embedding configuration values are required and dimension must be positive."); }
    private static bool ValidVector(double[]? v, int n) => v is { Length: var l } && l == n && v.All(double.IsFinite) && v.Any(x => x != 0);
    private static void ValidateVector(double[]? v, int n) { if (!ValidVector(v,n)) throw new ExportValidationException("Fake vector must have exact dimension, finite values and nonzero norm."); }
    private static string GetString(JsonElement e, string p) => e.GetProperty(p).GetString()!; private static int GetInt(JsonElement e, string p) => e.GetProperty(p).GetInt32(); private static long GetInt64(JsonElement e, string p) => e.GetProperty(p).GetInt64();
    private static string HashFile(string p) { using var s = File.OpenRead(p); return Convert.ToHexStringLower(SHA256.HashData(s)); } private static string Hash(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
}
