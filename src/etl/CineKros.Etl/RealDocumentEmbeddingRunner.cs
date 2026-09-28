using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CineKros.Etl;

internal sealed record RealEmbeddingOptions(
    string CatalogPath, string ManifestPath, string OutputDirectory, string CheckpointPath,
    string ModelId = "gemini-embedding-2", string Profile = "b3-embed2-text-search-v1", int Dimension = 768,
    string FormattingVersion = "b3-document-title-text-v1", int MaxTotalAttempts = 11000,
    long MaxInputBytes = 5_000_000, long MaxInputTokens = 5_000_000, int MaxDailyAttempts = 900,
    int MaxAttemptsPerMinute = 90, int MaxTokensPerMinute = 25_000, TimeSpan? MaxRunTime = null,
    string ExpectedCatalogSha256 = "8b2bad0a22fef45842176a1d9f3730be1568e367b9fc230fa0aa398bb5c26946",
    string ExpectedCatalogFingerprint = "2ffad7ba703cb80543db617e742a61c88871332910185767ee96fe08a77a0be7", int ExpectedCatalogRecords = 9730,
    bool AllowQuotaDayAdvance = false);

internal sealed record RealEmbeddingRunResult(int RecordCount, int Generated, int Reused, int Failed, int Attempts, long InputBytes, long InputTokens, string OutputSha256);

internal static class RealDocumentEmbeddingRunner
{
    internal const string CatalogVersion = "B05a-combined-catalog-v1";
    internal const string CatalogSha256 = "8b2bad0a22fef45842176a1d9f3730be1568e367b9fc230fa0aa398bb5c26946";
    internal const string CatalogFingerprint = "2ffad7ba703cb80543db617e742a61c88871332910185767ee96fe08a77a0be7";
    internal const int CatalogRecords = 9730;
    private const string CheckpointFormat = "real-document-vectors-jsonl-v1";
    private const string ArtifactFormat = "real-document-vectors-jsonl-v1";
    private const string InputFormat = "b3-document-title-text-v1";
    private const string FingerprintTask = "search-document-prefix-v1";

    internal static string FormatInput(string title, string semanticText) => $"title: {title} | text: {semanticText}";

    internal static string Fingerprint(string formattedInput, RealEmbeddingOptions options)
    {
        // Property insertion order is part of the published fingerprint contract.
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["semanticText"] = formattedInput, ["modelId"] = options.ModelId, ["modelVersion"] = options.Profile,
            ["dimension"] = options.Dimension, ["documentTaskType"] = FingerprintTask, ["formattingVersion"] = InputFormat
        });
        return Hash(Encoding.UTF8.GetBytes(payload));
    }

    internal static async Task<RealEmbeddingRunResult> RunAsync(RealEmbeddingOptions options, HttpClient http, string apiKey,
        CancellationToken cancellationToken = default, Func<TimeSpan, CancellationToken, Task>? delay = null, Action? beforePublish = null)
    {
        ValidateOptions(options);
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("A process-provided Gemini API key is required.", nameof(apiKey));
        var records = ReadCatalog(options, cancellationToken, out var catalogSha);
        var checkpointExisted = File.Exists(options.CheckpointPath);
        var state = ReadCampaignState(options.CheckpointPath, options, checkpointExisted);
        var entries = ReadCheckpoint(options.CheckpointPath, options);
        NormalizeCheckpoint(options.CheckpointPath, options, entries, records);
        var runStarted = DateTimeOffset.UtcNow;
        var attemptTimes = state.MinuteHistory;
        var generated = 0; var reused = 0; var failed = 0; var invocationAttempts = 0; long invocationBytes = 0, invocationTokens = 0;
        var consecutiveMalformedResponses = 0;
        var currentQuotaDay = PacificDay(DateTimeOffset.UtcNow);
        if (state.QuotaDay != currentQuotaDay)
        {
            if (!options.AllowQuotaDayAdvance || state.CanaryVector is null || state.CanaryId <= 0)
                throw new RunBudgetException("Quota day changed; explicit approval and a saved canary are required before resuming.");
            var canary = records.SingleOrDefault(x => x.Id == state.CanaryId);
            if (canary is null || canary.Fingerprint != state.CanaryFingerprint) throw new CampaignStopException("Saved canary input no longer matches the reviewed catalog.");
            state.QuotaDay = currentQuotaDay; state.DailyAttempts = 0; PersistCampaignState(options.CheckpointPath, state);
            var observed = await GenerateWithRetry(canary, options, http, apiKey, state, attemptTimes, runStarted,
                () => { invocationAttempts++; invocationBytes += canary.Bytes; }, t => invocationTokens += t, cancellationToken, delay ?? Task.Delay);
            if (!CanaryMatches(state.CanaryVector, observed)) throw new CampaignStopException("Embedding canary drift detected; campaign stopped before mixing vectors.");
        }
        foreach (var row in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.TryGetValue(row.Id, out var saved) && saved.Vector is not null && saved.Fingerprint == row.Fingerprint && ValidVector(saved.Vector, options.Dimension))
            { reused++; SetCanaryIfMissing(state, row, saved.Vector, options.CheckpointPath); continue; }
            try
            {
                var vector = await GenerateWithRetry(row, options, http, apiKey, state, attemptTimes, runStarted,
                    () => { invocationAttempts++; invocationBytes += row.Bytes; }, t => invocationTokens += t,
                    cancellationToken, delay ?? Task.Delay);
                entries[row.Id] = new(row.Fingerprint, vector, null); SaveCheckpointEntry(options.CheckpointPath, row.Id, entries[row.Id]); generated++;
                consecutiveMalformedResponses = 0;
                SetCanaryIfMissing(state, row, vector, options.CheckpointPath);
            }
            catch (OperationCanceledException) { throw; }
            catch (RunBudgetException) { throw; }
            catch (CampaignStopException) { throw; }
            catch (Exception ex)
            {
                // One malformed/provider-rejected movie is isolated; only the exception class is persisted.
                entries[row.Id] = new(row.Fingerprint, null, SafeErrorClass(ex)); SaveCheckpointEntry(options.CheckpointPath, row.Id, entries[row.Id]); failed++;
                if (ex is ExportValidationException or JsonException)
                {
                    if (++consecutiveMalformedResponses >= 3) throw new CampaignStopException("Repeated malformed embedding responses indicate a campaign-wide provider problem; safe stop.");
                }
                else consecutiveMalformedResponses = 0;
            }
        }
        if (records.Any(r => !entries.TryGetValue(r.Id, out var e) || e.Fingerprint != r.Fingerprint || !ValidVector(e.Vector, options.Dimension)))
            throw new ExportValidationException("Real embedding run has unresolved records; the partial checkpoint is resumable and no final artifact was published.");
        if (DateTimeOffset.UtcNow - runStarted > (options.MaxRunTime ?? TimeSpan.FromMinutes(120))) throw new RunBudgetException("Invocation wall-time cap was reached before publication.");
        if (Directory.Exists(options.OutputDirectory))
        {
            var existingHash = ValidateExistingArtifact(options, catalogSha, records, entries);
            return new(records.Count, generated, reused, failed, invocationAttempts, invocationBytes, invocationTokens, existingHash);
        }
        Publish(options, catalogSha, records, entries, state, cancellationToken, beforePublish);
        var outputHash = HashFile(Path.Combine(options.OutputDirectory, "document-vectors.jsonl"));
        return new(records.Count, generated, reused, failed, invocationAttempts, invocationBytes, invocationTokens, outputHash);
    }

    private sealed record CatalogRow(long Id, string Title, string Text, string Input, string Fingerprint, long Bytes);
    private sealed record Entry(string Fingerprint, float[]? Vector, string? Error);
    private sealed class CampaignState
    {
        public long Attempts; public long Bytes; public long Tokens; public int DailyAttempts; public string QuotaDay = PacificDay(DateTimeOffset.UtcNow);
        public long CanaryId; public string? CanaryFingerprint; public float[]? CanaryVector;
        public List<MinuteUsage> MinuteHistory = [];
    }
    private sealed class MinuteUsage(DateTimeOffset at, int tokens) { public DateTimeOffset At = at; public int Tokens = tokens; }

    private static List<CatalogRow> ReadCatalog(RealEmbeddingOptions o, CancellationToken token, out string catalogSha)
    {
        if (!Path.IsPathFullyQualified(o.CatalogPath) || !Path.IsPathFullyQualified(o.ManifestPath) || !Path.IsPathFullyQualified(o.OutputDirectory) || !Path.IsPathFullyQualified(o.CheckpointPath)) throw new ArgumentException("All real embedding paths must be absolute.");
        if (!File.Exists(o.CatalogPath) || !File.Exists(o.ManifestPath)) throw new ExportValidationException("Reviewed real catalog or manifest is missing.");
        catalogSha = HashFile(o.CatalogPath);
        if (!string.Equals(catalogSha, o.ExpectedCatalogSha256, StringComparison.Ordinal)) throw new ExportValidationException("Catalog JSONL does not match the reviewed real B05-A SHA-256.");
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(o.ManifestPath)); var root = manifest.RootElement;
            if (String(root, "catalogVersion") != CatalogVersion || String(root, "contentFingerprint") != o.ExpectedCatalogFingerprint ||
                String(root.GetProperty("output"), "sha256") != o.ExpectedCatalogSha256 || Int(root.GetProperty("output"), "recordCount") != o.ExpectedCatalogRecords ||
                String(root.GetProperty("output"), "order") != "movieLensId ascending" || root.GetProperty("validated").ValueKind != JsonValueKind.True)
                throw new ExportValidationException("Real catalog manifest does not match the reviewed version, fingerprint, hash, count or order.");
            var rows = new List<CatalogRow>(o.ExpectedCatalogRecords); long prior = 0;
            using var reader = new StreamReader(o.CatalogPath, new UTF8Encoding(false, true)); string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                token.ThrowIfCancellationRequested(); using var document = JsonDocument.Parse(line); var item = document.RootElement;
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("movieLensId", out var idNode) || !idNode.TryGetInt64(out var id) || id <= prior ||
                    !item.TryGetProperty("title", out var titleNode) || titleNode.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(titleNode.GetString()) ||
                    !item.TryGetProperty("semanticText", out var textNode) || textNode.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(textNode.GetString()))
                    throw new ExportValidationException("Real catalog IDs, title or semanticText are invalid.");
                prior = id; var input = FormatInput(titleNode.GetString()!, textNode.GetString()!); rows.Add(new(id, titleNode.GetString()!, textNode.GetString()!, input, Fingerprint(input, o), Encoding.UTF8.GetByteCount(input)));
            }
            if (rows.Count != o.ExpectedCatalogRecords) throw new ExportValidationException("Real catalog record count does not match the declared reviewed input.");
            return rows;
        }
        catch (ExportValidationException) { throw; }
        catch (Exception ex) when (ex is JsonException or IOException or DecoderFallbackException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new ExportValidationException("Real catalog or manifest is malformed."); }
    }

    private static async Task<float[]> GenerateWithRetry(CatalogRow row, RealEmbeddingOptions o, HttpClient http, string key,
        CampaignState state, List<MinuteUsage> minute, DateTimeOffset started,
        Action countAttempt, Action<int> countTokens, CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task> delay)
    {
        for (var retry = 0; ; retry++)
        {
            var now = DateTimeOffset.UtcNow; var oneMinuteAgo = now.AddMinutes(-1);
            minute.RemoveAll(x => x.At <= oneMinuteAgo);
            EnforceCaps(o, state, minute, started, row.Bytes);
            if (minute.Count >= o.MaxAttemptsPerMinute) throw new RunBudgetException("Per-minute request cap reached; resume after the window.");
            state.Attempts++; state.DailyAttempts++; state.Bytes += row.Bytes; var estimated = EstimateTokens(row.Input); state.Tokens += estimated;
            countAttempt(); countTokens(estimated); var currentUsage = new MinuteUsage(now, estimated); minute.Add(currentUsage); PersistCampaignState(o.CheckpointPath, state);
            using var request = BuildRequest(o, row.Input, key);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                int usedTokens = estimated;
                if (response.Headers.TryGetValues("x-goog-usage-metadata-prompt-token-count", out var usage) && int.TryParse(usage.FirstOrDefault(), out var parsed) && parsed >= 0) usedTokens = parsed;
                state.Tokens += usedTokens - estimated; countTokens(usedTokens - estimated);
                // Update the token-minute accounting for the just completed attempt.
                currentUsage.Tokens = usedTokens;
                PersistCampaignState(o.CheckpointPath, state);
                if (minute.Sum(x => x.Tokens) > o.MaxTokensPerMinute) throw new RunBudgetException("Measured per-minute token total exceeded its configured cap.");
                if (state.Tokens > o.MaxInputTokens) throw new RunBudgetException("Measured token total exceeded the configured campaign cap.");
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(timeout.Token); var vector = ParseVector(body, o.Dimension);
                    var actualTokens = ReadUsageTokens(body) ?? usedTokens;
                    state.Tokens += actualTokens - usedTokens; countTokens(actualTokens - usedTokens);
                    currentUsage.Tokens = actualTokens;
                    PersistCampaignState(o.CheckpointPath, state);
                    if (minute.Sum(x => x.Tokens) > o.MaxTokensPerMinute) throw new RunBudgetException("Measured per-minute token total exceeded its configured cap.");
                    if (state.Tokens > o.MaxInputTokens) throw new RunBudgetException("Measured token total exceeded the configured campaign cap.");
                    return vector;
                }
                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500 && response.StatusCode is not HttpStatusCode.TooManyRequests)
                {
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                        throw new CampaignStopException("Gemini credentials or model endpoint rejected the campaign; safe stop.");
                    if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity) throw new InputRejectedException(response.StatusCode);
                    throw new CampaignStopException("Gemini returned a non-retryable campaign-level HTTP response.");
                }
                if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500) throw new CampaignStopException("Gemini returned an unclassified response; safe stop.");
                if (retry >= 2)
                {
                    if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new CampaignStopException("Repeated 429 responses may indicate quota exhaustion; safe stop.");
                    throw new InputRejectedException(response.StatusCode);
                }
                var wait = RetryDelay(response, retry);
                if (wait > TimeSpan.FromSeconds(60)) throw new RunBudgetException("Provider Retry-After exceeds the 60-second wait limit.");
                EnforceCaps(o, state, minute, started, row.Bytes, wait);
                await delay(wait, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                if (retry >= 2) throw new InputRejectedException(HttpStatusCode.RequestTimeout);
                var wait = TimeSpan.FromSeconds(1 << retry); EnforceCaps(o, state, minute, started, row.Bytes, wait); await delay(wait, cancellationToken);
            }
            catch (HttpRequestException)
            {
                if (retry >= 2) throw;
                var wait = TimeSpan.FromSeconds(1 << retry); EnforceCaps(o, state, minute, started, row.Bytes, wait); await delay(wait, cancellationToken);
            }
        }
    }

    internal static HttpRequestMessage BuildRequest(RealEmbeddingOptions o, string input, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(o.ModelId)}:embedContent");
        request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new { model = $"models/{o.ModelId}", content = new { parts = new[] { new { text = input } } }, output_dimensionality = o.Dimension }), Encoding.UTF8, "application/json");
        return request;
    }

    internal static float[] ParseVector(string json, int dimension)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        if (root.TryGetProperty("embeddings", out var many) && many.ValueKind == JsonValueKind.Array && many.GetArrayLength() != 0) throw new ExportValidationException("Response contains multiple independent embeddings.");
        if (!root.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Object || !embedding.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != dimension)
            throw new ExportValidationException("Response must contain exactly one embedding with the configured dimension.");
        var vector = new float[dimension]; var i = 0; double normSquared = 0;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) throw new ExportValidationException("Embedding contains a nonfinite or malformed value.");
            var converted = (float)number; if (!float.IsFinite(converted)) throw new ExportValidationException("Embedding value is not finite float32.");
            vector[i++] = converted; normSquared += (double)converted * converted;
        }
        if (!double.IsFinite(normSquared) || normSquared <= 0) throw new ExportValidationException("Embedding vector must have nonzero finite norm.");
        return vector;
    }

    private static Dictionary<long, Entry> ReadCheckpoint(string path, RealEmbeddingOptions o)
    {
        var result = new Dictionary<long, Entry>(); if (!File.Exists(path)) return result;
        string[] lines; try { lines = File.ReadAllText(path, new UTF8Encoding(false, true)).Split('\n'); } catch { return result; }
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            try
            {
                using var d = JsonDocument.Parse(lines[i].TrimEnd('\r')); var e = d.RootElement; var id = Int64(e, "movieLensId"); var fp = String(e, "fingerprint");
                if (id <= 0 || fp.Length != 64 || fp != fp.ToLowerInvariant()) continue;
                float[]? vector = null;
                if (e.TryGetProperty("vector", out var v) && v.ValueKind == JsonValueKind.Array)
                { vector = v.EnumerateArray().Select(x => x.TryGetSingle(out var n) ? n : float.NaN).ToArray(); }
                string? error = e.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : null;
                if ((vector is null || !ValidVector(vector, o.Dimension)) && error is null) continue;
                result[id] = new(fp, vector, error);
            }
            catch { /* malformed, torn and foreign-format records are ignored */ }
        }
        return result;
    }

    private static void NormalizeCheckpoint(string path, RealEmbeddingOptions o, Dictionary<long, Entry> entries, List<CatalogRow> records)
    {
        var current = records.ToDictionary(x => x.Id, x => x.Fingerprint);
        foreach (var id in entries.Keys.ToArray()) if (!current.TryGetValue(id, out var fp) || fp != entries[id].Fingerprint || (!ValidVector(entries[id].Vector, o.Dimension) && entries[id].Error is null)) entries.Remove(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var tmp = path + ".rewrite-" + Guid.NewGuid().ToString("N");
        try { using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { WriteHeader(fs, o); foreach (var p in entries.OrderBy(x => x.Key)) WriteEntry(fs, p.Key, p.Value); fs.Flush(true); } File.Move(tmp, path, true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static CampaignState ReadCampaignState(string checkpoint, RealEmbeddingOptions o, bool checkpointExists)
    {
        var state = new CampaignState(); var budgetPath = checkpoint + ".budget.json"; var budgetExists = File.Exists(budgetPath);
        if (checkpointExists != budgetExists) throw new ExportValidationException("Checkpoint and campaign budget journal are inconsistent; safe stop.");
        if (!checkpointExists) return state;
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllBytes(budgetPath)); var root = d.RootElement;
            if (String(root, "budgetFormat") != "real-document-embedding-budget-v2" || String(root, "modelId") != o.ModelId || String(root, "profile") != o.Profile)
                throw new ExportValidationException("Campaign budget journal format/profile mismatch; safe stop.");
            state.Attempts = Long(root, "totalAttempts"); state.Bytes = Long(root, "totalBytes"); state.Tokens = Long(root, "totalTokens"); state.DailyAttempts = Int(root, "dailyAttempts"); state.QuotaDay = String(root, "quotaDay");
            if (state.Attempts < 0 || state.Bytes < 0 || state.Tokens < 0 || state.DailyAttempts < 0 || !DateOnly.TryParseExact(state.QuotaDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ExportValidationException("Campaign budget journal counters are invalid; safe stop.");
            if (root.TryGetProperty("canaryId", out var id)) state.CanaryId = id.GetInt64();
            if (root.TryGetProperty("canaryFingerprint", out var fp) && fp.ValueKind == JsonValueKind.String) state.CanaryFingerprint = fp.GetString();
            if (root.TryGetProperty("canaryVector", out var vector) && vector.ValueKind == JsonValueKind.Array) state.CanaryVector = vector.EnumerateArray().Select(x => x.TryGetSingle(out var n) ? n : float.NaN).ToArray();
            var history = root.GetProperty("minuteHistory"); if (history.ValueKind != JsonValueKind.Array) throw new ExportValidationException("Campaign minute history is invalid; safe stop.");
            foreach (var item in history.EnumerateArray())
            {
                if (!DateTimeOffset.TryParse(String(item, "atUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) throw new ExportValidationException("Campaign minute timestamp is invalid; safe stop.");
                var tokens = Int(item, "tokens"); if (tokens < 0) throw new ExportValidationException("Campaign minute token count is invalid; safe stop.");
                state.MinuteHistory.Add(new(at, tokens));
            }
        }
        catch (ExportValidationException) { throw; }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new ExportValidationException("Campaign budget journal is corrupt; safe stop."); }
        return state;
    }

    private static void PersistCampaignState(string path, CampaignState state)
    {
        var tmp = path + ".state-" + Guid.NewGuid().ToString("N");
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var w = new Utf8JsonWriter(fs)) { w.WriteStartObject(); w.WriteString("budgetFormat", "real-document-embedding-budget-v2"); w.WriteString("modelId", "gemini-embedding-2"); w.WriteString("profile", "b3-embed2-text-search-v1"); w.WriteNumber("totalAttempts", state.Attempts); w.WriteNumber("totalBytes", state.Bytes); w.WriteNumber("totalTokens", state.Tokens); w.WriteNumber("dailyAttempts", state.DailyAttempts); w.WriteString("quotaDay", state.QuotaDay); w.WriteStartArray("minuteHistory"); foreach (var use in state.MinuteHistory) { w.WriteStartObject(); w.WriteString("atUtc", use.At.ToString("O", CultureInfo.InvariantCulture)); w.WriteNumber("tokens", use.Tokens); w.WriteEndObject(); } w.WriteEndArray(); if (state.CanaryVector is not null) { w.WriteNumber("canaryId", state.CanaryId); w.WriteString("canaryFingerprint", state.CanaryFingerprint); w.WriteStartArray("canaryVector"); foreach (var f in state.CanaryVector) w.WriteNumberValue(f); w.WriteEndArray(); } w.WriteEndObject(); w.Flush(); fs.Flush(true); }
        // Entry journals are append-only after normalization. Store campaign totals in a sibling file atomically.
        File.Move(tmp, path + ".budget.json", true);
    }

    private static void SaveCheckpointEntry(string path, long id, Entry entry)
    { using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read); WriteEntry(fs, id, entry); fs.Flush(true); }
    private static void WriteHeader(Stream fs, RealEmbeddingOptions o)
    { using var w = new Utf8JsonWriter(fs); w.WriteStartObject(); w.WriteString("checkpointFormat", CheckpointFormat); w.WriteString("profile", o.Profile); w.WriteString("modelId", o.ModelId); w.WriteNumber("dimension", o.Dimension); w.WriteString("formattingVersion", InputFormat); w.WriteEndObject(); w.Flush(); fs.WriteByte((byte)'\n'); }
    private static void WriteEntry(Stream fs, long id, Entry e)
    { using var w = new Utf8JsonWriter(fs); w.WriteStartObject(); w.WriteNumber("movieLensId", id); w.WriteString("fingerprint", e.Fingerprint); if (e.Vector is not null) { w.WriteStartArray("vector"); foreach (var f in e.Vector) w.WriteNumberValue(f); w.WriteEndArray(); } if (e.Error is not null) w.WriteString("error", e.Error); w.WriteEndObject(); w.Flush(); fs.WriteByte((byte)'\n'); }

    private static void Publish(RealEmbeddingOptions o, string catalogSha, List<CatalogRow> rows, Dictionary<long, Entry> entries, CampaignState state, CancellationToken token, Action? beforePublish)
    {
        var parent = Path.GetDirectoryName(o.OutputDirectory)!; Directory.CreateDirectory(parent); if (Directory.Exists(o.OutputDirectory)) throw new ExportValidationException("Real embedding output directory already exists; final artifacts are immutable.");
        var staging = o.OutputDirectory + ".staging-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(staging);
        try
        {
            var jsonPath = Path.Combine(staging, "document-vectors.jsonl");
            using (var fs = new FileStream(jsonPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) foreach (var row in rows)
            { token.ThrowIfCancellationRequested(); using var w = new Utf8JsonWriter(fs); w.WriteStartObject(); w.WriteNumber("movieLensId", row.Id); w.WriteString("fingerprint", row.Fingerprint); w.WriteString("semanticTextSha256", Hash(Encoding.UTF8.GetBytes(row.Text))); w.WriteStartArray("vector"); foreach (var v in entries[row.Id].Vector!) w.WriteNumberValue(v); w.WriteEndArray(); w.WriteEndObject(); w.Flush(); fs.WriteByte((byte)'\n'); }
            var outputHash = HashFile(jsonPath);
            using (var fs = File.Create(Path.Combine(staging, "manifest.json"))) using (var w = new Utf8JsonWriter(fs))
            { w.WriteStartObject(); w.WriteString("artifactFormat", ArtifactFormat); w.WriteString("catalogVersion", CatalogVersion); w.WriteString("catalogSha256", catalogSha); w.WriteString("catalogContentFingerprint", o.ExpectedCatalogFingerprint); w.WriteString("profile", o.Profile); w.WriteString("modelId", o.ModelId); w.WriteNumber("dimension", o.Dimension); w.WriteString("documentInputFormat", InputFormat); w.WriteString("fingerprintAlgorithm", "sha256-compact-json-six-field-formatted-input"); w.WriteString("outputSha256", outputHash); w.WriteNumber("recordCount", rows.Count); w.WriteString("order", "movieLensId ascending"); w.WriteNumber("requestAttempts", state.Attempts); w.WriteNumber("inputBytes", state.Bytes); w.WriteNumber("inputTokens", state.Tokens); w.WriteString("usageSource", "provider prompt token metadata when available; otherwise conservative UTF-8/4 estimate"); w.WriteNumber("failedRecords", 0); w.WriteString("provenance", "Gemini Developer API v1beta embedContent, one document per request"); w.WriteEndObject(); }
            beforePublish?.Invoke(); token.ThrowIfCancellationRequested(); Directory.Move(staging, o.OutputDirectory);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static string ValidateExistingArtifact(RealEmbeddingOptions o, string catalogSha, List<CatalogRow> rows, Dictionary<long, Entry> entries)
    {
        try
        {
            var jsonPath = Path.Combine(o.OutputDirectory, "document-vectors.jsonl"); var manifestPath = Path.Combine(o.OutputDirectory, "manifest.json");
            var hash = HashFile(jsonPath); using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath)); var m = manifest.RootElement;
            if (String(m, "artifactFormat") != ArtifactFormat || String(m, "catalogSha256") != catalogSha || String(m, "catalogContentFingerprint") != o.ExpectedCatalogFingerprint ||
                String(m, "profile") != o.Profile || String(m, "modelId") != o.ModelId || Int(m, "dimension") != o.Dimension || Int(m, "recordCount") != rows.Count || String(m, "outputSha256") != hash)
                throw new ExportValidationException("Existing real embedding artifact is incompatible or corrupt; immutable output was preserved.");
            using var reader = new StreamReader(jsonPath, new UTF8Encoding(false, true));
            foreach (var row in rows)
            {
                var line = reader.ReadLine(); if (line is null) throw new ExportValidationException("Existing real artifact is incomplete.");
                using var item = JsonDocument.Parse(line); var root = item.RootElement;
                if (Long(root, "movieLensId") != row.Id || String(root, "fingerprint") != row.Fingerprint ||
                    String(root, "semanticTextSha256") != Hash(Encoding.UTF8.GetBytes(row.Text)) || root.TryGetProperty("fakeMarker", out _) ||
                    !root.TryGetProperty("vector", out var v) || v.ValueKind != JsonValueKind.Array ||
                    !v.EnumerateArray().Select(x => x.TryGetSingle(out var n) ? n : float.NaN).SequenceEqual(entries[row.Id].Vector!))
                    throw new ExportValidationException("Existing real artifact records differ from the compatible checkpoint.");
            }
            if (reader.ReadLine() is not null) throw new ExportValidationException("Existing real artifact contains extra records.");
            return hash;
        }
        catch (ExportValidationException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or DecoderFallbackException or InvalidOperationException or KeyNotFoundException)
        { throw new ExportValidationException("Existing real embedding artifact is malformed; immutable output was preserved."); }
    }

    private static void EnforceCaps(RealEmbeddingOptions o, CampaignState s, List<MinuteUsage> minute, DateTimeOffset started, long nextBytes, TimeSpan extraWait = default)
    {
        var estimate = (nextBytes + 3) / 4;
        if (s.Attempts >= o.MaxTotalAttempts || s.DailyAttempts >= o.MaxDailyAttempts || s.Bytes + nextBytes > o.MaxInputBytes || s.Tokens + estimate > o.MaxInputTokens || DateTimeOffset.UtcNow - started + extraWait > (o.MaxRunTime ?? TimeSpan.FromMinutes(120))) throw new RunBudgetException("A configured cumulative, daily, byte, token or wall-time cap is reached.");
        if (s.QuotaDay != PacificDay(DateTimeOffset.UtcNow)) throw new RunBudgetException("Quota day changed; explicit campaign review is required before continuing.");
        var minuteTokens = minute.Where(x => x.At > DateTimeOffset.UtcNow.Add(extraWait).AddMinutes(-1)).Sum(x => x.Tokens);
        if (minuteTokens + estimate > o.MaxTokensPerMinute) throw new RunBudgetException("Per-minute token cap reached; resume after the window.");
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int retry)
    {
        var value = response.Headers.RetryAfter;
        if (value?.Delta is TimeSpan delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (value?.Date is DateTimeOffset date) return date <= DateTimeOffset.UtcNow ? TimeSpan.Zero : date - DateTimeOffset.UtcNow;
        return TimeSpan.FromSeconds(1 << retry);
    }
    private static int? ReadUsageTokens(string body)
    {
        try { using var d = JsonDocument.Parse(body); if (d.RootElement.TryGetProperty("usageMetadata", out var usage) && usage.TryGetProperty("promptTokenCount", out var n) && n.TryGetInt32(out var count) && count >= 0) return count; } catch { }
        return null;
    }
    private static void SetCanaryIfMissing(CampaignState state, CatalogRow row, float[] vector, string checkpoint)
    { if (state.CanaryVector is null) { state.CanaryId = row.Id; state.CanaryFingerprint = row.Fingerprint; state.CanaryVector = vector.ToArray(); PersistCampaignState(checkpoint, state); } }
    private static bool CanaryMatches(float[] expected, float[] actual) => expected.Length == actual.Length && expected.Zip(actual).All(p => Math.Abs(p.First - p.Second) <= 1e-6f);
    private static int EstimateTokens(string text) => Math.Max(1, (Encoding.UTF8.GetByteCount(text) + 3) / 4);
    private static string SafeErrorClass(Exception e) => e is InputRejectedException ir ? $"HTTP_{(int)ir.StatusCode}" : e.GetType().Name;
    private static string PacificDay(DateTimeOffset at) => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(at, "Pacific Standard Time").ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static bool ValidVector(float[]? v, int n) => v is { Length: var length } && length == n && v.All(float.IsFinite) && v.Any(x => x != 0);
    private static void ValidateOptions(RealEmbeddingOptions o)
    { if (o is null || o.ModelId != "gemini-embedding-2" || o.Profile != "b3-embed2-text-search-v1" || o.Dimension != 768 || o.FormattingVersion != InputFormat || o.MaxTotalAttempts <= 0 || o.MaxInputBytes <= 0 || o.MaxInputTokens <= 0 || o.MaxDailyAttempts <= 0 || o.MaxAttemptsPerMinute <= 0 || o.MaxTokensPerMinute <= 0) throw new ArgumentException("Real embedding options must match the locked B3 profile and positive safety caps."); }
    private static string String(JsonElement e, string key) => e.GetProperty(key).GetString()!;
    private static int Int(JsonElement e, string key) => e.GetProperty(key).GetInt32();
    private static long Int64(JsonElement e, string key) => e.GetProperty(key).GetInt64();
    private static long Long(JsonElement e, string key) => e.GetProperty(key).GetInt64();
    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    private static string HashFile(string path) { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }

    private sealed class RunBudgetException(string message) : Exception(message);
    private sealed class CampaignStopException(string message) : Exception(message);
    private sealed class InputRejectedException(HttpStatusCode status) : Exception("Input rejected by provider.") { internal HttpStatusCode StatusCode { get; } = status; }
}
