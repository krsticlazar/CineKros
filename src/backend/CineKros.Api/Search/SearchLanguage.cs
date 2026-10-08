namespace CineKros.Api.Search;

public enum SearchLanguage { English, Serbian }

internal static class SearchLanguageColumn
{
    internal static string For(SearchLanguage language) => language switch
    {
        SearchLanguage.English => "me.embedding",
        SearchLanguage.Serbian => "me.embedding_sr",
        _ => throw new ArgumentOutOfRangeException(nameof(language))
    };
}
