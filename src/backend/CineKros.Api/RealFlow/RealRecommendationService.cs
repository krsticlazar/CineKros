using CineKros.Api.Database;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;

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
    Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(RealHardFilters hardFilters, CancellationToken cancellationToken);
}

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
    public Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(RealHardFilters hardFilters, CancellationToken cancellationToken) => repository.SearchHardOnlyAsync(hardFilters, cancellationToken);
}

public sealed record RealRecommendationResult(string? AlertCode, IReadOnlyList<FilteredMovie> Movies, RealParserResult ParsedResult);

public sealed class RealRecommendationService(
    IRealQueryParser parser,
    RealParsedQueryValidator validator,
    IRealQueryEmbeddingProvider embeddings,
    IRealMovieSearch search)
{
    public async Task<RealRecommendationResult> RecommendAsync(ParserInput input, CancellationToken cancellationToken)
    {
        RealParserResult parsed;
        try { parsed = await parser.ParseAsync(input.Language, input.Message, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RealProviderException) { throw; }
        catch { throw new RealProviderException("PROVIDER_UNAVAILABLE"); }
        validator.ValidateResult(parsed);
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
            float[] vector;
            try { vector = await embeddings.EmbedQueryAsync(query.SemanticQuery, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (RealProviderException) { throw new RealProviderException("SEARCH_UNAVAILABLE"); }
            catch { throw new RealProviderException("SEARCH_UNAVAILABLE"); }
            try { movies = await search.SearchHybridAsync(query.HardFilters, vector, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { throw new RealProviderException("SEARCH_UNAVAILABLE"); }
        }

        if (movies.Count > 10 || movies.Select(movie => movie.MovieLensId).Distinct().Count() != movies.Count)
            throw new RealProviderException("SEARCH_UNAVAILABLE");
        return new RealRecommendationResult(null, movies, parsed);
    }
}
