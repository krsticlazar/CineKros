using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CineKros.Translator;

public sealed record RuntimeManifest(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("pythonVersion")] string PythonVersion,
    [property: JsonPropertyName("runtimeLockSha256")] string RuntimeLockSha256,
    [property: JsonPropertyName("packages")] IReadOnlyDictionary<string, string> Packages,
    [property: JsonPropertyName("torchDevice")] string TorchDevice,
    [property: JsonPropertyName("runtimeFingerprint")] string RuntimeFingerprint)
{
    private static readonly HashSet<string> RuntimeTools = ["pip", "setuptools"];

    public static RuntimeManifest Verify(string pythonPath, string lockPath, string validatorPath)
    {
        var fullPython = Path.GetFullPath(pythonPath);
        var scriptsDirectory = Directory.GetParent(fullPython);
        var runtimeDirectory = scriptsDirectory?.Parent;
        if (scriptsDirectory is null || !StringComparer.OrdinalIgnoreCase.Equals(scriptsDirectory.Name, "Scripts") || runtimeDirectory is null)
            throw new InvalidDataException("Python interpreter must belong to the isolated translator venv");
        var markerPath = Path.Combine(runtimeDirectory.FullName, "runtime-manifest.json");
        TagWorkflow.RequireFile(markerPath, "isolated runtime manifest");
        TagWorkflow.RequireFile(lockPath, "runtime lock");
        TagWorkflow.RequireFile(validatorPath, "runtime validator");

        var start = new ProcessStartInfo(fullPython)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false, true),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false)
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(validatorPath);
        start.ArgumentList.Add("--lock");
        start.ArgumentList.Add(Path.GetFullPath(lockPath));
        start.ArgumentList.Add("--expected-python");
        start.ArgumentList.Add("3.12.14");
        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new InvalidOperationException("isolated runtime validator could not start");
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, 64 * 1024);
        var stderrTask = ReadBoundedAsync(process.StandardError, 4096);
        try
        {
            process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("isolated runtime validation timed out");
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        _ = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidDataException("isolated runtime package validation failed");

        var observed = JsonSerializer.Deserialize<RuntimeManifest>(stdout) ?? throw new InvalidDataException("runtime validator output is invalid");
        var recorded = JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllBytes(markerPath)) ?? throw new InvalidDataException("runtime manifest is invalid");
        var expectedLockHash = TagWorkflow.HashFile(lockPath);
        if (observed.SchemaVersion != "translator-python-runtime-v1" || observed.PythonVersion != "3.12.14" ||
            observed.TorchDevice != "cpu" || observed.RuntimeLockSha256 != expectedLockHash ||
            !StringComparer.Ordinal.Equals(observed.RuntimeFingerprint, recorded.RuntimeFingerprint) ||
            observed.RuntimeLockSha256 != recorded.RuntimeLockSha256 || observed.PythonVersion != recorded.PythonVersion ||
            !observed.Packages.OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(recorded.Packages.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
            throw new InvalidDataException("isolated runtime differs from the bootstrapped package manifest");

        var lockPackages = ReadPinnedPackages(lockPath);
        var actualPackages = observed.Packages.ToDictionary(pair => NormalizePackageName(pair.Key), pair => pair.Value, StringComparer.Ordinal);
        foreach (var (name, version) in lockPackages)
            if (!actualPackages.TryGetValue(name, out var actualVersion) || actualVersion != version)
                throw new InvalidDataException("isolated runtime package version does not match requirements.lock");
        if (actualPackages.Count != lockPackages.Count + RuntimeTools.Count || RuntimeTools.Any(name => !actualPackages.ContainsKey(name)))
            throw new InvalidDataException("isolated runtime package set contains additions or omissions");
        return observed;
    }

    private static IReadOnlyDictionary<string, string> ReadPinnedPackages(string path)
    {
        var packages = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var separator = line.IndexOf("==", StringComparison.Ordinal);
            if (separator <= 0)
                continue;
            var name = NormalizePackageName(line[..separator]);
            var rest = line[(separator + 2)..];
            var version = rest.Split(' ', '\\', '\t')[0];
            if (!packages.TryAdd(name, version))
                throw new InvalidDataException("runtime lock contains duplicate package pins");
        }
        return packages;
    }

    private static string NormalizePackageName(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "[-_.]+", "-").ToLowerInvariant();

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum)
    {
        var buffer = new char[1024];
        var content = new System.Text.StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) != 0)
        {
            if (content.Length < maximum)
                content.Append(buffer, 0, Math.Min(read, maximum - content.Length));
        }
        return content.ToString();
    }
}
