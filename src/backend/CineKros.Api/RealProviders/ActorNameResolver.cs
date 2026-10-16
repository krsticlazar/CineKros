using System.Text;

namespace CineKros.Api.RealProviders;

/// <summary>Immutable, catalog-scoped resolution. No transliteration or inferred aliases.</summary>
public sealed class ActorNameResolver
{
    private readonly HashSet<string> canonical = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> normalized = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);

    public ActorNameResolver(IEnumerable<string> canonicalNames, IReadOnlyDictionary<string, string>? reviewedAliases = null)
    {
        ArgumentNullException.ThrowIfNull(canonicalNames);
        foreach (var name in canonicalNames)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || !name.IsNormalized(NormalizationForm.FormC) || name.Any(char.IsControl))
                throw new ArgumentException("Canonical actor names must be nonblank, trimmed NFC strings.", nameof(canonicalNames));
            if (!canonical.Add(name)) continue;
            var key = Normalize(name);
            if (normalized.TryGetValue(key, out var previous) && previous != name) normalized[key] = null;
            else normalized.TryAdd(key, name);
        }
        if (reviewedAliases is null) return;
        foreach (var (alias, target) in reviewedAliases)
        {
            if (string.IsNullOrWhiteSpace(alias) || alias.Any(char.IsControl) || target is null || !canonical.Contains(target))
                throw new ArgumentException("Reviewed aliases require valid names and exact canonical targets.", nameof(reviewedAliases));
            var key = Normalize(alias);
            // A reviewed alias cannot replace a registry key, even an ambiguous one.
            if (normalized.ContainsKey(key) || !aliases.TryAdd(key, target))
                throw new ArgumentException("Reviewed actor alias collides with another registry key.", nameof(reviewedAliases));
        }
    }

    public bool IsCanonical(string name) => name is not null && canonical.Contains(name);

    public string? Resolve(string proposedName)
    {
        if (proposedName is null || string.IsNullOrWhiteSpace(proposedName) || proposedName.Any(char.IsControl)) return null;
        if (canonical.Contains(proposedName)) return proposedName;
        var key = Normalize(proposedName);
        if (normalized.TryGetValue(key, out var match)) return match;
        return aliases.GetValueOrDefault(key);
    }

    private static string Normalize(string name) => name.Trim().Normalize(NormalizationForm.FormC);
}
