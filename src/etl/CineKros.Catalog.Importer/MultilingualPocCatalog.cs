using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CineKros.Catalog.Importer;

/// <summary>Strict reader for the released 150-film bilingual POC. It deliberately has no database/API dependency.</summary>
public static class MultilingualPocCatalog
{
    public const string CatalogVersion = "B05a-bilingual-catalog-v2";
    public const string CatalogSha256 = "4881fb5699398c787f88496e2b803c58a743061e7e604a52dba312743291bbf7";
    public const string SourceCatalogSha256 = "8b2bad0a22fef45842176a1d9f3730be1568e367b9fc230fa0aa398bb5c26946";
    public const string SourceContentFingerprint = "2ffad7ba703cb80543db617e742a61c88871332910185767ee96fe08a77a0be7";
    public const string SelectionIdSetSha256 = "0960c2074b1848dbf9b7d374a82f345fc910f235434ef06002380949cf674529";
    public const string DictionarySha256 = "a6d90f69ca39b499621487f54c4a8824c9644e4b4c55401881d19f7dcf808f85";
    public const string EnCorpusSha256 = "5c5760576471547ea4d6db17acfd254ff1a0d4e7c47fe54b0f4db6a9795fdde0";
    public const string SrCorpusSha256 = "16a525a58795af3278c7a2741e1c06b2dd32c154835f532cf939b4f0da7133d3";
    public const string SrTextFormatVersion = "sr-title-year-director-cast-tags-latn-v1";
    public const int ExpectedCount = 150;
    public const string IdentityVersion = "sr-poc-catalog-identity-v1";
    public const string ProfileVersion = "multilingual-e5-base-int8-onnx-v1";
    public const string ProfileFingerprint = "eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe";
    public const string ModelRevision = "d128750597153bb5987e10b1c3493a34e5a4502a";
    public const string OnnxSha256 = "2523551878658b305550d8759443822dbfda9ed9c8012ef2c354ba2c5b9de503";
    public const string TokenizerSha256 = "62c24cdc13d4c9952d63718d6c9fa4c287974249e16b7ade6d5a85e7bbb75626";
    private static readonly string[] OriginalFields = ["movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw", "relevantTags", "semanticText", "tmdbId", "genres", "averageRating", "ratingCount", "runtimeMinutes", "originalLanguage", "posterPath"];
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<MultilingualPocCatalogDocument> LoadAsync(string catalogPath, string manifestPath, string dictionaryPath, string sourceCatalogPath, CancellationToken cancellationToken = default)
    {
        var catalogBytes = await File.ReadAllBytesAsync(catalogPath, cancellationToken);
        var dictionaryBytes = await File.ReadAllBytesAsync(dictionaryPath, cancellationToken);
        var catalogHash = Hash(catalogBytes);
        var dictionaryHash = Hash(dictionaryBytes);
        if (catalogHash != CatalogSha256 || dictionaryHash != DictionarySha256)
            throw new InvalidDataException("POC catalog or translation dictionary does not match its released hash.");

        using var manifestDoc = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken));
        var m = manifestDoc.RootElement;
        if (RequiredString(m, "schemaVersion") != "bilingual-catalog-manifest-v1" ||
            RequiredString(m, "catalogVersion") != CatalogVersion ||
            RequiredString(m, "sourceCatalog", "version") != CatalogValidator.CatalogVersion ||
            RequiredString(m, "sourceCatalog", "sha256") != SourceCatalogSha256 ||
            RequiredString(m, "sourceCatalog", "contentFingerprint") != SourceContentFingerprint ||
            RequiredString(m, "selection", "selectorVersion") != "sr-poc-selection-v1" ||
            RequiredString(m, "selection", "selectedIdSetSha256") != SelectionIdSetSha256 ||
            RequiredInt(m, "selection", "movieCount") != ExpectedCount ||
            RequiredInt(m, "selection", "tagCount") != 418 ||
            RequiredString(m, "selection", "sourceTagsSha256") != "37e53b1b7fcf06ddfa49be29017a5db85e32979aaf33e0f4a0f0f5201a1f9e80" ||
            RequiredString(m, "dictionary", "sha256") != DictionarySha256 ||
            RequiredString(m, "dictionary", "sourceTagsSha256") != "37e53b1b7fcf06ddfa49be29017a5db85e32979aaf33e0f4a0f0f5201a1f9e80" ||
            RequiredString(m, "dictionary", "formatVersion") != SrTextFormatVersion ||
            RequiredString(m, "output", "sha256") != CatalogSha256 ||
            RequiredInt(m, "output", "recordCount") != ExpectedCount ||
            RequiredInt(m, "output", "schemaFieldCount") != 19 ||
            RequiredString(m, "corpora", "enSemanticTextSha256") != EnCorpusSha256 ||
            RequiredString(m, "corpora", "srSemanticTextSha256") != SrCorpusSha256)
            throw new InvalidDataException("POC catalog manifest does not match the locked bilingual release.");

        using var dictionaryDoc = JsonDocument.Parse(dictionaryBytes);
        var translations = ReadDictionary(dictionaryDoc.RootElement);
        var sourceCatalog = await CatalogValidator.LoadAsync(sourceCatalogPath, cancellationToken);
        var sourceMetadata = sourceCatalog.Movies.ToDictionary(movie => movie.Id);
        var sourceRows = ReadSourceRows(sourceCatalogPath, cancellationToken);
        IReadOnlyList<MultilingualPocMovie> movies;
        try { movies = ReadPocRows(catalogBytes, sourceRows, sourceMetadata, translations, cancellationToken); }
        finally { foreach (var sourceRow in sourceRows.Values) sourceRow.Dispose(); }
        var enHash = HashCorpus(movies, "semanticText");
        var srHash = HashCorpus(movies, "semanticTextSr");
        if (enHash != EnCorpusSha256 || srHash != SrCorpusSha256)
            throw new InvalidDataException("POC semantic text corpus does not match its locked language hash.");
        var identity = new CatalogIdentityPayload(IdentityVersion, CatalogVersion, catalogHash, SourceCatalogSha256, SourceContentFingerprint,
            SelectionIdSetSha256, dictionaryHash, enHash, srHash, SrTextFormatVersion);
        var identityJson = JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions);
        return new MultilingualPocCatalogDocument(catalogPath, catalogHash, sourceCatalogPath, SourceCatalogSha256, SourceContentFingerprint,
            SelectionIdSetSha256, dictionaryPath, dictionaryHash, enHash, srHash,
            Hash(identityJson), Encoding.UTF8.GetString(identityJson), movies);
    }

    public static MultilingualPocArtifactValidation ValidateVectorArtifact(string vectorsPath, string manifestPath,
        MultilingualPocCatalogDocument catalog, string language)
    {
        if (language is not ("en" or "sr")) throw new InvalidDataException("Vector artifact language must be en or sr.");
        var bytes = File.ReadAllBytes(vectorsPath);
        using var manifestDoc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var manifest = manifestDoc.RootElement;
        var expectedCorpus = language == "en" ? catalog.EnCorpusSha256 : catalog.SrCorpusSha256;
        var expectedTextFormat = language == "en" ? "en-title-year-director-cast-tags-v1" : SrTextFormatVersion;
        var expectedDictionaryHash = language == "en" ? null : catalog.DictionarySha256;
        if (RequiredString(manifest, "format") != "multilingual-document-vectors-jsonl-v1" ||
            RequiredString(manifest, "language") != language || RequiredString(manifest, "catalogVersion") != CatalogVersion ||
            RequiredString(manifest, "catalogSha256") != catalog.CatalogSha256 ||
            RequiredString(manifest, "sourceCatalogSha256") != catalog.SourceCatalogSha256 ||
            RequiredString(manifest, "sourceContentFingerprint") != catalog.SourceContentFingerprint ||
            RequiredString(manifest, "catalogContentIdentitySha256") != catalog.IdentitySha256 ||
            RequiredString(manifest, "selectionIdSetSha256") != catalog.SelectionIdSetSha256 ||
            RequiredString(manifest, "corpusSha256") != expectedCorpus ||
            (expectedDictionaryHash is null ? !manifest.TryGetProperty("translationDictionarySha256", out var dict) || dict.ValueKind != JsonValueKind.Null : RequiredString(manifest, "translationDictionarySha256") != expectedDictionaryHash) ||
            RequiredString(manifest, "textFormatVersion") != expectedTextFormat ||
            RequiredString(manifest, "inputNormalizationVersion") != "unicode-nfc-case-preserving-v1" ||
            RequiredString(manifest, "profile") != ProfileVersion || RequiredString(manifest, "profileFingerprint") != ProfileFingerprint ||
            RequiredString(manifest, "modelRevision") != ModelRevision || RequiredString(manifest, "onnxSha256") != OnnxSha256 ||
            RequiredString(manifest, "tokenizerSha256") != TokenizerSha256 || RequiredInt(manifest, "dimension") != 768 ||
            RequiredString(manifest, "documentPrefix") != "passage: " ||
            RequiredString(manifest, "fingerprintAlgorithm") != "multilingual-document-fingerprint-v1" ||
            RequiredString(manifest, "order") != "movieLensId ascending" || RequiredInt(manifest, "recordCount") != ExpectedCount ||
            RequiredInt(manifest, "batchSize") != 1 || RequiredInt(manifest, "failedRecords") != 0 ||
            RequiredString(manifest, "pooling") != "masked-mean-v1" || RequiredString(manifest, "normalization") != "l2-float32-v1" ||
            RequiredString(manifest, "inferenceShapePolicy") != "single-sequence-unpadded-v1" ||
            RequiredString(manifest, "outputSha256") != Hash(bytes))
            throw new InvalidDataException("Multilingual vector artifact manifest is incompatible with the selected POC language.");

        var textFormat = expectedTextFormat;
        var rows = new List<MultilingualPocArtifactRow>(ExpectedCount);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        string? line; var index = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            if (index >= catalog.Movies.Count) throw new InvalidDataException("Vector artifact contains extra rows.");
            using var rowDoc = JsonDocument.Parse(line); var row = rowDoc.RootElement;
            var names = new HashSet<string>(["movieLensId", "fingerprint", "semanticTextSha256", "vector"], StringComparer.Ordinal);
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 4 || row.EnumerateObject().Any(p => !names.Remove(p.Name)) || names.Count != 0)
                throw new InvalidDataException("Multilingual vector row does not match its exact four-field contract.");
            var movie = catalog.Movies[index++];
            var id = row.GetProperty("movieLensId").GetInt64();
            var selectedText = language == "en" ? movie.SemanticText : movie.SemanticTextSr;
            var expectedFingerprint = ComputeDocumentFingerprint(language, textFormat, selectedText);
            var expectedTextHash = Hash(Encoding.UTF8.GetBytes(selectedText));
            if (id != movie.MovieLensId || RequiredString(row, "fingerprint") != expectedFingerprint || RequiredString(row, "semanticTextSha256") != expectedTextHash)
                throw new InvalidDataException("Vector row ID, language, document fingerprint, or selected source text hash is incompatible.");
            var vectorElement = row.GetProperty("vector");
            if (vectorElement.ValueKind != JsonValueKind.Array || vectorElement.GetArrayLength() != 768)
                throw new InvalidDataException("Multilingual vector must contain exactly 768 components.");
            double normSquared = 0;
            foreach (var value in vectorElement.EnumerateArray())
            {
                if (!value.TryGetSingle(out var component) || !float.IsFinite(component)) throw new InvalidDataException("Vector contains a non-finite component.");
                normSquared += (double)component * component;
            }
            if (!double.IsFinite(normSquared) || Math.Abs(Math.Sqrt(normSquared) - 1d) > 0.0001)
                throw new InvalidDataException("Multilingual vector is not L2 normalized within 1e-4.");
            rows.Add(new MultilingualPocArtifactRow(id, expectedFingerprint, expectedTextHash));
        }
        if (index != ExpectedCount) throw new InvalidDataException("Multilingual vector artifact is incomplete.");
        return new MultilingualPocArtifactValidation(language, Hash(bytes), rows.Count, rows);
    }

    public static string ComputeDocumentFingerprint(string language, string textFormatVersion, string semanticText)
    {
        if (language is not ("en" or "sr")) throw new ArgumentException("Language must be en or sr.", nameof(language));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new DocumentFingerprint("multilingual-document-fingerprint-v1", language, textFormatVersion,
            ProfileFingerprint, "passage: " + semanticText.Normalize(System.Text.NormalizationForm.FormC)), JsonOptions);
        return Hash(payload);
    }

    private static Dictionary<string, string> ReadDictionary(JsonElement root)
    {
        if (RequiredString(root, "schemaVersion") != "tag-translations-sr-v1" || !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != 418)
            throw new InvalidDataException("POC translation dictionary structure is invalid.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            var en = RequiredString(entry, "en");
            var sr = RequiredString(entry, "sr");
            if (string.IsNullOrWhiteSpace(en) || string.IsNullOrWhiteSpace(sr) || !result.TryAdd(en, sr))
                throw new InvalidDataException("POC translation dictionary has a blank or duplicate source key.");
        }
        return result;
    }

    private static Dictionary<long, JsonDocument> ReadSourceRows(string path, CancellationToken ct)
    {
        var rows = new Dictionary<long, JsonDocument>();
        using var reader = new StreamReader(path, new UTF8Encoding(false, true));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            var doc = JsonDocument.Parse(line);
            var id = doc.RootElement.GetProperty("movieLensId").GetInt64();
            if (!rows.TryAdd(id, doc)) { doc.Dispose(); throw new InvalidDataException("Source catalog has duplicate IDs."); }
        }
        return rows;
    }

    private static IReadOnlyList<MultilingualPocMovie> ReadPocRows(byte[] bytes, IReadOnlyDictionary<long, JsonDocument> sourceRows,
        IReadOnlyDictionary<long, MovieRow> sourceMetadata, IReadOnlyDictionary<string, string> dictionary, CancellationToken ct)
    {
        var result = new List<MultilingualPocMovie>(ExpectedCount);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        string? line; long previous = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(line); var row = doc.RootElement;
            var expectedNames = OriginalFields.Concat(["tagsSr", "semanticTextSr"]).ToHashSet(StringComparer.Ordinal);
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 19 || row.EnumerateObject().Any(p => !expectedNames.Remove(p.Name)) || expectedNames.Count != 0)
                throw new InvalidDataException("Bilingual POC catalog row does not match the exact 19-field contract.");
            var id = row.GetProperty("movieLensId").GetInt64();
            if (id <= previous || !sourceRows.TryGetValue(id, out var source)) throw new InvalidDataException("POC catalog IDs are not the frozen ordered source subset.");
            foreach (var field in OriginalFields)
                if (source.RootElement.GetProperty(field).GetRawText() != row.GetProperty(field).GetRawText())
                    throw new InvalidDataException($"POC catalog changed original source field {field} for a selected movie.");
            var en = row.GetProperty("semanticText").GetString();
            var sr = row.GetProperty("semanticTextSr").GetString();
            if (string.IsNullOrWhiteSpace(en) || string.IsNullOrWhiteSpace(sr)) throw new InvalidDataException("POC selected semantic text is blank.");
            var tags = row.GetProperty("relevantTags"); var translated = row.GetProperty("tagsSr");
            if (tags.ValueKind != JsonValueKind.Array || translated.ValueKind != JsonValueKind.Array || tags.GetArrayLength() != translated.GetArrayLength())
                throw new InvalidDataException("POC Serbian tag list does not match its source tag positions.");
            var tagIndex = 0;
            foreach (var tag in tags.EnumerateArray())
            {
                var key = RequiredString(tag, "name");
                if (!dictionary.TryGetValue(key, out var expectedSr) || translated[tagIndex++].GetString() != expectedSr)
                    throw new InvalidDataException("POC Serbian tags do not match the locked translation dictionary.");
            }
            if (!sourceMetadata.TryGetValue(id, out var metadata)) throw new InvalidDataException("POC movie metadata is missing from its validated source catalog.");
            result.Add(new MultilingualPocMovie(metadata, en, sr)); previous = id;
        }
        if (result.Count != ExpectedCount) throw new InvalidDataException("POC catalog must contain exactly the frozen 150 IDs.");
        return result;
    }

    private static string HashCorpus(IReadOnlyList<MultilingualPocMovie> movies, string languageField)
    {
        using var stream = new MemoryStream();
        foreach (var movie in movies)
        {
            var text = languageField == "semanticText" ? movie.SemanticText : movie.SemanticTextSr;
            var row = JsonSerializer.SerializeToUtf8Bytes(new CorpusRow(movie.MovieLensId, text), JsonOptions);
            stream.Write(row); stream.WriteByte((byte)'\n');
        }
        return Hash(stream.ToArray());
    }

    private static string RequiredString(JsonElement element, params string[] path)
    {
        foreach (var key in path) if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out element)) throw new InvalidDataException("POC manifest/dictionary is incomplete.");
        return element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new InvalidDataException("POC manifest/dictionary string is malformed.");
    }
    private static int RequiredInt(JsonElement element, params string[] path)
    {
        foreach (var key in path) if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out element)) throw new InvalidDataException("POC manifest is incomplete.");
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n) ? n : throw new InvalidDataException("POC manifest integer is malformed.");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed record CatalogIdentityPayload(string IdentityVersion, string CatalogVersion, string CatalogSha256, string SourceCatalogSha256,
        string SourceContentFingerprint, string SelectionIdSetSha256, string DictionarySha256, string EnCorpusSha256, string SrCorpusSha256, string TextFormatVersionSr);
    private sealed record CorpusRow(long MovieLensId, string SemanticText);
    private sealed record DocumentFingerprint(string FingerprintVersion, string Language, string TextFormatVersion, string ProfileFingerprint, string FormattedInput);
}

public sealed record MultilingualPocMovie(MovieRow Metadata, string SemanticText, string SemanticTextSr)
{
    public long MovieLensId => Metadata.Id;
}
public sealed record MultilingualPocCatalogDocument(string CatalogPath, string CatalogSha256, string SourceCatalogPath, string SourceCatalogSha256,
    string SourceContentFingerprint, string SelectionIdSetSha256, string DictionaryPath, string DictionarySha256, string EnCorpusSha256,
    string SrCorpusSha256, string IdentitySha256, string IdentityPayloadJson, IReadOnlyList<MultilingualPocMovie> Movies);
public sealed record MultilingualPocArtifactRow(long MovieLensId, string Fingerprint, string SemanticTextSha256);
public sealed record MultilingualPocArtifactValidation(string Language, string ArtifactSha256, int RecordCount, IReadOnlyList<MultilingualPocArtifactRow> Rows);
