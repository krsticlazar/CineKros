using CineKros.Api.RealProviders;
using System.Text.Json;

namespace CineKros.Evaluation;

public sealed record ProposedQuerySet(string SchemaVersion, IReadOnlyList<EvaluationCase> Cases);
public sealed record EvaluationCase(string Id, string Language, string Query, string Category, string Note,
    bool ParserEval, bool RetrievalEval, JsonElement? ParserExpected, RetrievalQuery? RetrievalQuery,
    IReadOnlyList<string> ApplicableModes, bool? ExpectedNoResults = null);
public sealed record RetrievalQuery(RealHardFilters HardFilters, string? SemanticQuery);
public sealed record HumanJudgment(string QueryId, long MovieLensId, int Score);
public sealed record MovieResult(long MovieLensId, string Title, int Year, int? HumanScore);
public sealed record ParserComparison(string QueryId, bool ProviderChecklistMatch, bool CanonicalDtoMatch,
    bool SemanticTextLiteralMatch, string? AlertCode, double ProviderDurationMs);
public sealed record RetrievalResult(string QueryId, string Mode, RetrievalQuery CanonicalQuery,
    RealHardFilters ReferenceHardFilters, RealHardFilters AppliedHardFilters,
    int ResultCount, bool Partial, IReadOnlyList<MovieResult> Movies, double? EmbedDurationMs,
    double SearchDurationMs, double TotalDurationMs, int ViolationCount, double? CompliancePercent,
    bool? ExpectedNoResultsCorrect, double? ProposedMeanRelevance, double? ProposedPrecisionAt10,
    double? ProposedNdcgAt10);
public sealed record EvaluationReport(string SchemaVersion, string Mode, DateTimeOffset GeneratedAt,
    string CatalogVersion, string CatalogJsonlSha256, string CatalogContentFingerprint,
    string? E5ProfileFingerprint, IReadOnlyList<string> SelectedCaseIds,
    IReadOnlyList<RetrievalResult> RetrievalResults, IReadOnlyList<ParserComparison> ParserResults);

public static class EvaluationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
