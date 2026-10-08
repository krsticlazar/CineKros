using System.Diagnostics;
using System.Text.Json;

namespace CineKros.Translator;

public static class PythonTranslationProcess
{
    private static readonly TimeSpan PerJobTimeout = TimeSpan.FromMinutes(3);
    private const int MaxDiagnosticCharacters = 8192;

    public static async Task RunAsync(string pythonPath, string runnerPath, string modelDirectory,
        IReadOnlyList<ProtocolJob> jobs, Func<ProtocolResponse, CancellationToken, Task> onResponse,
        CancellationToken cancellationToken)
        => await RunCoreAsync(pythonPath, runnerPath, modelDirectory, jobs, onResponse, PerJobTimeout, cancellationToken);

    internal static async Task RunForTestsAsync(string pythonPath, string runnerPath, string modelDirectory,
        IReadOnlyList<ProtocolJob> jobs, Func<ProtocolResponse, CancellationToken, Task> onResponse,
        TimeSpan timeout, CancellationToken cancellationToken)
        => await RunCoreAsync(pythonPath, runnerPath, modelDirectory, jobs, onResponse, timeout, cancellationToken);

    internal static async Task RunFixtureForTestsAsync(string executablePath, string fixtureDll, string mode,
        IReadOnlyList<ProtocolJob> jobs, Func<ProtocolResponse, CancellationToken, Task> onResponse,
        TimeSpan timeout, CancellationToken cancellationToken)
        => await RunCoreAsync(executablePath, fixtureDll, mode, jobs, onResponse, timeout, cancellationToken, fixture: true);

    private static async Task RunCoreAsync(string pythonPath, string runnerPath, string modelDirectory,
        IReadOnlyList<ProtocolJob> jobs, Func<ProtocolResponse, CancellationToken, Task> onResponse,
        TimeSpan timeout, CancellationToken cancellationToken, bool fixture = false)
    {
        if (jobs.Count > 6 || jobs.Select(job => job.En).Distinct(StringComparer.Ordinal).Count() != jobs.Count)
            throw new InvalidDataException("Python process received duplicate or over-limit smoke jobs");

        var start = new ProcessStartInfo(pythonPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(false, true),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false)
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        if (fixture)
        {
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(runnerPath);
            start.ArgumentList.Add(modelDirectory);
        }
        else
        {
            start.ArgumentList.Add("-u");
            start.ArgumentList.Add(runnerPath);
            start.ArgumentList.Add("--model-dir");
            start.ArgumentList.Add(modelDirectory);
        }
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Python runner could not start");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException("Python runner process could not start");
        }

        var diagnosticsTask = DrainBoundedAsync(process.StandardError, MaxDiagnosticCharacters, cancellationToken);
        var responses = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var job in jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var json = JsonSerializer.Serialize(job);
                await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
                var line = await ReadLineWithTimeoutAsync(process.StandardOutput, timeout, cancellationToken);
                if (line is null)
                    throw new InvalidDataException("Python runner ended before returning every response");
                var response = ParseResponseLine(line);
                if (!StringComparer.Ordinal.Equals(response.En, job.En) || !responses.Add(response.En))
                    throw new InvalidDataException("Python runner returned an unrequested or duplicate key");
                await onResponse(response, cancellationToken);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
            var extra = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(extra))
                throw new InvalidDataException("Python runner emitted unsolicited stdout data");
            _ = await diagnosticsTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Python runner exited unsuccessfully");
        }
        catch (TimeoutException)
        {
            KillOwnedProcessTree(process);
            await DrainAfterKillAsync(process, diagnosticsTask);
            throw new TimeoutException("Python runner timed out; completed checkpoint rows were preserved");
        }
        catch
        {
            KillOwnedProcessTree(process);
            await DrainAfterKillAsync(process, diagnosticsTask);
            throw;
        }
    }

    public static ProtocolResponse ParseResponseLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Python response must be a JSON object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!seen.Add(property.Name))
                throw new InvalidDataException("Python response contains duplicate properties");
        if (!seen.SetEquals(["en", "machine", "completed", "error"]))
            throw new InvalidDataException("Python response has missing or unknown properties");

        var en = root.GetProperty("en").GetString();
        var machineElement = root.GetProperty("machine");
        var machine = machineElement.ValueKind == JsonValueKind.Null ? null : machineElement.GetString();
        var completed = root.GetProperty("completed").GetBoolean();
        var errorElement = root.GetProperty("error");
        var error = errorElement.ValueKind == JsonValueKind.Null ? null : errorElement.GetString();
        if (string.IsNullOrWhiteSpace(en) || (completed && string.IsNullOrWhiteSpace(machine)) || (completed && error is not null) || (!completed && string.IsNullOrWhiteSpace(error)))
            throw new InvalidDataException("Python response fields are inconsistent");
        return new ProtocolResponse(en, machine, completed, error);
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await reader.ReadLineAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private static async Task<string> DrainBoundedAsync(StreamReader reader, int maximum, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var captured = new System.Text.StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (captured.Length < maximum)
                captured.Append(buffer, 0, Math.Min(read, maximum - captured.Length));
        }
        return captured.ToString();
    }

    private static void KillOwnedProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task DrainAfterKillAsync(Process process, Task<string> diagnosticsTask)
    {
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
        try { _ = await diagnosticsTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }
}
