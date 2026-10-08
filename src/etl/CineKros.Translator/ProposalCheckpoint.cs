using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CineKros.Translator;

public sealed record ProposalIdentity(
    string ModelId,
    string Revision,
    string TargetToken,
    DecodingMetadata Decoding,
    string RuntimeLockSha256,
    string RuntimeFingerprint,
    string NormalizationVersion,
    IReadOnlyDictionary<string, string> ArtifactHashes)
{
    [JsonIgnore]
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));

    public static ProposalIdentity Create(ModelManifest model, string runtimeLockSha256, string runtimeFingerprint) => new(
        TranslatorConstants.ModelId, TranslatorConstants.ModelRevision, TranslatorConstants.TargetToken,
        new DecodingMetadata(false, TranslatorConstants.NumBeams, TranslatorConstants.MaxNewTokens, TranslatorConstants.MaxSourceTokens, "cpu"),
        runtimeLockSha256, runtimeFingerprint, TranslatorConstants.NormalizationVersion,
        new SortedDictionary<string, string>(model.ArtifactHashes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal));
}

public sealed record CheckpointProposal(string En, string Machine, string Sr, bool Completed, string? Error);

internal sealed class ProposalCheckpoint
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _path;
    private readonly string _identityHash;
    private readonly ProposalIdentity _identity;

    private ProposalCheckpoint(string path, ProposalIdentity identity, string identityHash, Dictionary<string, CheckpointProposal> completed)
    {
        _path = path;
        _identity = identity;
        _identityHash = identityHash;
        Completed = completed;
    }

    public Dictionary<string, CheckpointProposal> Completed { get; }

    public static ProposalCheckpoint Open(string path, ProposalIdentity identity)
    {
        path = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(parent);
        var hash = identity.Sha256;
        var completed = new Dictionary<string, CheckpointProposal>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            Rewrite(path, identity, hash, completed);
            return new ProposalCheckpoint(path, identity, hash, completed);
        }

        var bytes = File.ReadAllBytes(path);
        var terminated = bytes.Length == 0 || bytes[^1] == (byte)'\n';
        var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        var completeLength = terminated ? bytes.Length : lastNewline + 1;
        var lines = DecodeRecords(bytes, completeLength);

        var headerCompatible = false;
        var invalidRowFound = false;
        var rowIdentityCompatible = new List<CheckpointProposal>();
        if (lines.Count > 0)
        {
            try
            {
                using var header = JsonDocument.Parse(lines[0] ?? string.Empty);
                var root = header.RootElement;
                headerCompatible = HasExactProperties(root, "kind", "identitySha256", "identity") &&
                    root.GetProperty("kind").GetString() == "header" &&
                    root.GetProperty("identitySha256").GetString() == hash &&
                    JsonSerializer.Deserialize<ProposalIdentity>(root.GetProperty("identity"), JsonOptions)?.Sha256 == hash;
            }
            catch (JsonException) { invalidRowFound = true; }
            catch (KeyNotFoundException) { }
            catch (InvalidOperationException) { }
        }

        foreach (var line in lines.Skip(1))
        {
            if (line is null || string.IsNullOrWhiteSpace(line))
            {
                invalidRowFound = true;
                continue;
            }
            try
            {
                using var row = JsonDocument.Parse(line);
                var root = row.RootElement;
                if (!HasExactProperties(root, "kind", "identitySha256", "identity", "proposalSha256", "proposal") ||
                    root.GetProperty("kind").GetString() != "proposal" || root.GetProperty("identitySha256").GetString() != hash)
                {
                    invalidRowFound = true;
                    continue;
                }
                var rowIdentity = JsonSerializer.Deserialize<ProposalIdentity>(root.GetProperty("identity"), JsonOptions);
                if (rowIdentity is null || rowIdentity.Sha256 != hash)
                {
                    invalidRowFound = true;
                    continue;
                }
                var proposal = JsonSerializer.Deserialize<CheckpointProposal>(root.GetProperty("proposal"), JsonOptions)!;
                if (root.GetProperty("proposalSha256").GetString() != ComputeProposalHash(hash, rowIdentity, proposal))
                {
                    invalidRowFound = true;
                    continue;
                }
                if (proposal.Completed)
                {
                    if (string.IsNullOrWhiteSpace(proposal.En) || string.IsNullOrWhiteSpace(proposal.Machine) || string.IsNullOrWhiteSpace(proposal.Sr) || proposal.Error is not null)
                    {
                        invalidRowFound = true;
                        continue;
                    }
                    rowIdentityCompatible.Add(proposal);
                }
                else if (string.IsNullOrWhiteSpace(proposal.En) || string.IsNullOrWhiteSpace(proposal.Error))
                    invalidRowFound = true;
            }
            catch (JsonException) { invalidRowFound = true; }
            catch (System.Text.DecoderFallbackException) { invalidRowFound = true; }
            catch (KeyNotFoundException) { invalidRowFound = true; }
            catch (InvalidOperationException) { invalidRowFound = true; }
        }

        foreach (var proposal in rowIdentityCompatible)
            completed[proposal.En] = proposal;

        if (!headerCompatible || !terminated || invalidRowFound || completed.Count != rowIdentityCompatible.Count)
        {
            Backup(path);
            Rewrite(path, identity, hash, completed);
        }
        return new ProposalCheckpoint(path, identity, hash, completed);
    }

    public async Task AppendAsync(CheckpointProposal proposal, CancellationToken cancellationToken)
    {
        var proposalHash = ComputeProposalHash(_identityHash, _identity, proposal);
        var row = new { kind = "proposal", identitySha256 = _identityHash, identity = _identity, proposalSha256 = proposalHash, proposal };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(row, JsonOptions);
        await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
        if (proposal.Completed && !string.IsNullOrWhiteSpace(proposal.Machine) && !string.IsNullOrWhiteSpace(proposal.Sr))
            Completed[proposal.En] = proposal;
    }

    private static void Rewrite(string path, ProposalIdentity identity, string hash, IReadOnlyDictionary<string, CheckpointProposal> proposals)
    {
        var temp = path + ".rewrite-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            WriteLine(stream, new { kind = "header", identitySha256 = hash, identity });
            foreach (var proposal in proposals.Values.OrderBy(item => item.En, StringComparer.Ordinal))
                WriteLine(stream, new { kind = "proposal", identitySha256 = hash, identity, proposalSha256 = ComputeProposalHash(hash, identity, proposal), proposal });
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static void WriteLine<T>(Stream stream, T item)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions);
        stream.Write(json);
        stream.WriteByte((byte)'\n');
    }

    private static void Backup(string path)
    {
        var backup = path + ".backup-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture);
        File.Copy(path, backup, overwrite: false);
    }

    private static string ComputeProposalHash(string identityHash, ProposalIdentity identity, CheckpointProposal proposal)
    {
        var payload = new { identitySha256 = identityHash, identity, proposal };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions)));
    }

    private static bool HasExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name))
                return false;
        return names.SetEquals(expected);
    }

    private static List<string?> DecodeRecords(byte[] bytes, int length)
    {
        var result = new List<string?>();
        var start = 0;
        for (var index = 0; index < length; index++)
        {
            if (bytes[index] != (byte)'\n')
                continue;
            var size = index - start;
            if (size > 0 && bytes[index - 1] == (byte)'\r') size--;
            try { result.Add(Utf8.GetString(bytes, start, size)); }
            catch (System.Text.DecoderFallbackException) { result.Add(null); }
            start = index + 1;
        }
        if (start < length)
        {
            try { result.Add(Utf8.GetString(bytes, start, length - start)); }
            catch (System.Text.DecoderFallbackException) { result.Add(null); }
        }
        return result;
    }
}
