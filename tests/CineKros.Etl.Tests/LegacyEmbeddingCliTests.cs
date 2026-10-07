using System.Diagnostics;
using CineKros.Etl;

namespace CineKros.Etl.Tests;

[TestClass]
public sealed class LegacyEmbeddingCliTests
{
    [TestMethod]
    public async Task RealEmbed_RejectsAsSupersededBeforeCredentialOrHttpSetup()
    {
        var assemblyPath = typeof(MetadataExporter).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add("real-embed");
        start.Environment.Remove("GEMINI_API_KEY");

        using var process = Process.Start(start) ?? throw new AssertFailedException("Could not start ETL CLI process.");
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stderr = await stderrTask;
        var stdout = await stdoutTask;

        Assert.AreEqual(1, process.ExitCode);
        StringAssert.Contains(stderr, "real-embed command is superseded and disabled");
        StringAssert.Contains(stderr, "CineKros.E5.Generator CLI");
        Assert.IsFalse(stderr.Contains("GEMINI_API_KEY", StringComparison.Ordinal));
        Assert.AreEqual(string.Empty, stdout);
    }
}
