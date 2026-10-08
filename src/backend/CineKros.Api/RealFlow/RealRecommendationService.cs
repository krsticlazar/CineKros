using CineKros.Api.Database;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using CineKros.TextNormalization;

namespace CineKros.Api.RealFlow;

public interface IRealQueryParser
{
    Task<RealParserResult> ParseAsync(string language, string message, CancellationToken cancellationToken);
}

public interface IRealQueryEmbeddingProvider
{
    Task<float[]> EmbedQueryAsync(string semanticQuery, CancellationToken cancellationToken);
}

public interface IRealMovieSearch
{
    Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector, CancellationToken cancellationToken);
    Task EnsureSelectedLanguageReadyAsync(SearchLanguage language, CancellationToken cancellationToken) =>
        Task.FromException(new RealProviderException("SEARCH_UNAVAILABLE"));
    Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector, SearchLanguage language, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<FilteredMovie>>(new RealProviderException("SEARCH_UNAVAILABLE"));
    Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(RealHardFilters hardFilters, CancellationToken cancellationToken);
}

public sealed record RealRecommendationRequest(ParserInput OriginalRequest, SearchLanguage SelectedLanguage);

public sealed class GeminiQueryParserAdapter(GeminiRealQueryParser parser) : IRealQueryParser
{
    public Task<RealParserResult> ParseAsync(string language, string message, CancellationToken cancellationToken) => parser.ParseAsync(language, message, cancellationToken);
}

public sealed class E5QueryEmbeddingAdapter(CineKros.Embedding.E5EmbeddingModel model) : IRealQueryEmbeddingProvider
{
    public Task<float[]> EmbedQueryAsync(string semanticQuery, CancellationToken cancellationToken)
    {
        try { return Task.FromResult(model.EmbedQuery(semanticQuery, cancellationToken)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new RealProviderException("SEARCH_UNAVAILABLE"); }
    }
}

public sealed class MovieSearchAdapter(MovieSearchRepository repository) : IRealMovieSearch
{
    public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector, CancellationToken cancellationToken) => repository.SearchHybridAsync(hardFilters, queryVector, cancellationToken);
    public Task EnsureSelectedLanguageReadyAsync(SearchLanguage language, CancellationToken cancellationToken) => repository.EnsureSelectedLanguageReadyAsync(language, cancellationToken);
    public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector, SearchLanguage language, CancellationToken cancellationToken) => repository.SearchHybridAsync(hardFilters, queryVector, language, cancellationToken);
    public Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(RealHardFilters hardFilters, CancellationToken cancellationToken) => repository.SearchHardOnlyAsync(hardFilters, cancellationToken);
}

public sealed record RealRecommendationResult(string? AlertCode, IReadOnlyList<FilteredMovie> Movies, RealParserResult ParsedResult);

public sealed class RealRecommendationService(
    IRealQueryParser parser,
    RealParsedQueryValidator validator,
    IRealQueryEmbeddingProvider embeddings,
    IRealMovieSearch search,
    bool languageAwarePoc = false)
{
    public bool LanguageAwarePoc { get; } = languageAwarePoc;

    public Task<RealRecommendationResult> RecommendAsync(ParserInput input, CancellationToken cancellationToken)
    {
        if (!LanguageAwarePoc) return RecommendCoreAsync(input, null, cancellationToken);
        ArgumentNullException.ThrowIfNull(input);
        var language = input.Language switch
        {
            "en" => SearchLanguage.English,
            "sr" => SearchLanguage.Serbian,
            _ => throw new RealProviderException("INVALID_REQUEST")
        };
        return RecommendAsync(new RealRecommendationRequest(input, language), cancellationToken);
    }

    public Task<RealRecommendationResult> RecommendAsync(RealRecommendationRequest input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!LanguageAwarePoc) return Task.FromException<RealRecommendationResult>(new RealProviderException("SEARCH_UNAVAILABLE"));
        if (!IsConsistentLanguageRequest(input))
            return Task.FromException<RealRecommendationResult>(new RealProviderException("INVALID_REQUEST"));
        return RecommendCoreAsync(input.OriginalRequest, input.SelectedLanguage, cancellationToken);
    }

    private static bool IsConsistentLanguageRequest(RealRecommendationRequest input)
    {
        if (input.OriginalRequest is null || string.IsNullOrWhiteSpace(input.OriginalRequest.Message)) return false;
        return input.SelectedLanguage switch
        {
            SearchLanguage.English => input.OriginalRequest.Language == "en",
            SearchLanguage.Serbian => input.OriginalRequest.Language == "sr",
            _ => false
        };
    }

    private async Task<RealRecommendationResult> RecommendCoreAsync(ParserInput input, SearchLanguage? selectedLanguage, CancellationToken cancellationToken)
    {
        RealParserResult parsed;
        try { parsed = await parser.ParseAsync(input.Language, input.Message, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RealProviderException) { throw; }
        catch { throw new RealProviderException("PROVIDER_UNAVAILABLE"); }
        if (selectedLanguage is null) validator.ValidateResult(parsed);
        else validator.ValidateResultV5(parsed, selectedLanguage.Value);
        if (parsed.Type == "alert")
            return new RealRecommendationResult(parsed.AlertCode, [], parsed);

        var query = parsed.Query!;
        IReadOnlyList<FilteredMovie> movies;
        if (query.SemanticQuery is null)
        {
            try { movies = await search.SearchHardOnlyAsync(query.HardFilters, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { throw new RealProviderException("SEARCH_UNAVAILABLE"); }
        }
        else
        {
            if (selectedLanguage is not null)
            {
                try { await search.EnsureSelectedLanguageReadyAsync(selectedLanguage.Value, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (RealProviderException) { throw new RealProviderException("SEARCH_UNAVAILABLE"); }
                catch { throw new RealProviderException("SEARCH_UNAVAILABLE"); }
            }

            var semanticQuery = selectedLanguage == SearchLanguage.Serbian
                ? SerbianLatinNormalizer.Normalize(query.SemanticQuery!)
                : query.SemanticQuery!;
            float[] vector;
            try { vector = await embeddings.EmbedQueryAsync(semanticQuery, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (RealProviderException) { throw new RealProviderException("SEARCH_UNAVAILABLE"); }
            catch { throw new RealProviderException("SEARCH_UNAVAILABLE"); }
            try
            {
                movies = selectedLanguage is null
                    ? await search.SearchHybridAsync(query.HardFilters, vector, cancellationToken)
                    : await search.SearchHybridAsync(query.HardFilters, vector, selectedLanguage.Value, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { throw new RealProviderException("SEARCH_UNAVAILABLE"); }
        }

        if (movies.Count > 10 || movies.Select(movie => movie.MovieLensId).Distinct().Count() != movies.Count)
            throw new RealProviderException("SEARCH_UNAVAILABLE");
        return new RealRecommendationResult(null, movies, parsed);
    }
}
