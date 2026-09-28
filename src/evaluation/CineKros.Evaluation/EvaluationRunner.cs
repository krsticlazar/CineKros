using System.Diagnostics;
using System.Text.Json;
using CineKros.Api.Database;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using CineKros.Embedding;
using Npgsql;

namespace CineKros.Evaluation;

public sealed class EvaluationRunner(MovieSearchRepository search, NpgsqlDataSource dataSource, E5EmbeddingModel embedding)
{
    public async Task<IReadOnlyList<RetrievalResult>> RunRetrievalAsync(ProposedQuerySet set,
        IReadOnlyDictionary<string, IReadOnlyDictionary<long, int>>? judgments, CancellationToken cancellationToken)
    {
        var results = new List<RetrievalResult>();
        foreach (var item in set.Cases.Where(c => c.RetrievalEval))
        foreach (var mode in item.ApplicableModes)
        {
            var total = Stopwatch.StartNew();
            var filters = mode == "semantic" ? new RealHardFilters() : item.RetrievalQuery!.HardFilters;
            float[]? vector = null;
            double? embedMs = null;
            if (mode is "semantic" or "hybrid")
            {
                var timer = Stopwatch.StartNew();
                vector = embedding.EmbedQuery(item.RetrievalQuery!.SemanticQuery!, cancellationToken);
                embedMs = timer.Elapsed.TotalMilliseconds;
            }
            var searchTimer = Stopwatch.StartNew();
            var movies = mode == "structured"
                ? await search.SearchHardOnlyAsync(filters, cancellationToken)
                : await search.SearchHybridAsync(filters, vector!, cancellationToken);
            searchTimer.Stop();
            if (movies.Count > MovieSearchRepository.ResultLimit || movies.Select(m => m.MovieLensId).Distinct().Count() != movies.Count)
                throw new InvalidDataException("Search returned an invalid result set.");
            var metadata = await ReadMetadataAsync(movies.Select(m => m.MovieLensId).ToArray(), cancellationToken);
            var compliance = FilterCompliance.Evaluate(item.RetrievalQuery!.HardFilters, metadata);
            IReadOnlyDictionary<long, int>? caseJudgments = null;
            if (judgments is not null) judgments.TryGetValue(item.Id, out caseJudgments);
            var rows = movies.Select(m => new MovieResult(m.MovieLensId, m.Title, m.Year,
                caseJudgments is not null && caseJudgments.TryGetValue(m.MovieLensId, out var score) ? score : null)).ToArray();
            var metrics = HumanScoring.Calculate(rows, caseJudgments);
            total.Stop();
            var canonical = new RetrievalQuery(filters, mode == "structured" ? null : item.RetrievalQuery!.SemanticQuery);
            results.Add(new RetrievalResult(item.Id, mode, canonical, item.RetrievalQuery.HardFilters, filters, rows.Length, rows.Length is > 0 and < 10,
                rows, embedMs, searchTimer.Elapsed.TotalMilliseconds, total.Elapsed.TotalMilliseconds,
                compliance.ViolationCount, compliance.CompliancePercent,
                item.ExpectedNoResults is { } expected ? expected == (rows.Length == 0) : null,
                metrics.MeanRelevance, metrics.PrecisionAt10, metrics.NdcgAt10));
        }
        return results;
    }

    private async Task<IReadOnlyList<MovieMetadata>> ReadMetadataAsync(long[] ids, CancellationToken cancellationToken)
    {
        if (ids.Length == 0) return [];
        const string sql = "SELECT movie_lens_id, year, runtime_minutes, genres, average_rating, original_language FROM movies WHERE movie_lens_id = ANY (@ids)";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint, ids);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<MovieMetadata>(ids.Length);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new MovieMetadata(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<string[]>(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        if (result.Count != ids.Length) throw new InvalidDataException("Returned movie metadata is incomplete.");
        return result;
    }
}

public static class EvaluationReports
{
    public static void Write(EvaluationReport report, string outputBase)
    {
        var fullBase = Path.GetFullPath(outputBase);
        Directory.CreateDirectory(Path.GetDirectoryName(fullBase)!);
        File.WriteAllText(fullBase + ".json", JsonSerializer.Serialize(report, EvaluationJson.Options));
        var lines = new List<string> { "# CineKros Phase D evaluation run", "", $"Mode: {report.Mode}", $"Generated UTC: {report.GeneratedAt:O}",
            $"Selected cases: {string.Join(", ", report.SelectedCaseIds)}", $"Catalog: {report.CatalogVersion}",
            $"Catalog JSONL SHA-256: {report.CatalogJsonlSha256}", $"Catalog content fingerprint: {report.CatalogContentFingerprint}",
            $"E5 profile fingerprint: {report.E5ProfileFingerprint ?? "not loaded in parser mode"}", "" };
        if (report.RetrievalResults.Count > 0)
        {
            lines.Add("| Query | Mode | Count | Partial | Violations | Compliance % | Embed ms | Search ms | Total ms | Mean relevance* | P@10* | nDCG@10* |");
            lines.Add("|---|---|---:|:---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var r in report.RetrievalResults)
                lines.Add($"| {Cell(r.QueryId)} | {r.Mode} | {r.ResultCount} | {r.Partial} | {r.ViolationCount} | {Fmt(r.CompliancePercent)} | {Fmt(r.EmbedDurationMs)} | {r.SearchDurationMs:F2} | {r.TotalDurationMs:F2} | {Fmt(r.ProposedMeanRelevance)} | {Fmt(r.ProposedPrecisionAt10)} | {Fmt(r.ProposedNdcgAt10)} |");
            lines.Add("");
            lines.Add("* Proposed exploratory human metrics; null means not fully judged. Precision denominator is the number returned up to ten.");
        }
        if (report.ParserResults.Count > 0)
        {
            lines.Add(""); lines.Add("| Query | Checklist exact | Canonical hard fields exact | Semantic text literal (exploratory) | Alert | Provider ms |");
            lines.Add("|---|:---:|:---:|:---:|---|---:|");
            foreach (var p in report.ParserResults)
                lines.Add($"| {Cell(p.QueryId)} | {p.ProviderChecklistMatch} | {p.CanonicalDtoMatch} | {p.SemanticTextLiteralMatch} | {Cell(p.AlertCode ?? "")} | {p.ProviderDurationMs:F2} |");
        }
        File.WriteAllText(fullBase + ".md", string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }
    private static string Cell(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    private static string Fmt(double? value) => value?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "";
}
