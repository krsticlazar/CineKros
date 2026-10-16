using System.Diagnostics;
using System.Text.Json;
using CineKros.Api.Search;
using Npgsql;

namespace CineKros.Api.Diagnostics;

/// <summary>Explicit Development-only trace of executed SQL; vectors and credentials are never serialized.</summary>
public sealed class DevelopmentSqlTrace(bool enabled)
{
    public static bool ResolveEnabled(bool isDevelopment, bool actorV6, string? flag)
    {
        if (flag is null or "false") return false;
        if (flag != "true" || !isDevelopment || !actorV6)
            throw new InvalidOperationException("V6 SQL diagnostics require the isolated V6 Development profile and exact true.");
        return true;
    }

    public void Write(NpgsqlCommand command, SearchLanguage? language)
    {
        if (!enabled) return;
        Console.WriteLine(Format(command, language));
    }

    public static string Format(NpgsqlCommand command, SearchLanguage? language)
    {
        string[] allowed = ["yearMin", "yearMax", "runtimeMin", "runtimeMax", "genresAll", "genresAny", "ratingMin", "originalLanguage", "actorsAll", "actorsAny", "actorsExclude"];
        var parameters = command.Parameters.Cast<NpgsqlParameter>().Where(p => allowed.Contains(p.ParameterName, StringComparer.Ordinal))
            .Select(p => new { name = p.ParameterName, type = p.NpgsqlDbType.ToString(), value = p.Value }).ToArray();
        return "V6 SQL (Development) " + JsonSerializer.Serialize(new { correlationId = Activity.Current?.TraceId.ToString(), selectedLanguage = language?.ToString(), commandText = command.CommandText, parameters });
    }
}
