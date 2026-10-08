using System.Diagnostics;
using System.Text.Json;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--grandchild")
        {
            while (true) await Task.Delay(TimeSpan.FromSeconds(30));
        }

        var modeAndPath = args.Length > 0 ? args[0] : "valid";
        var parts = modeAndPath.Split('|', 2);
        var mode = parts[0];
        var processCount = 0;
        while (Console.ReadLine() is { } line)
        {
            processCount++;
            using var job = JsonDocument.Parse(line);
            var en = job.RootElement.GetProperty("en").GetString()!;
            if ((mode is "timeout" or "cancel") && processCount == 2)
            {
                if (parts.Length == 2)
                {
                    using var child = Process.Start(new ProcessStartInfo("dotnet")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        ArgumentList = { Environment.GetCommandLineArgs()[0], "--grandchild" }
                    });
                    File.WriteAllLines(parts[1], [Environment.ProcessId.ToString(), child!.Id.ToString()]);
                }
                while (true) await Task.Delay(TimeSpan.FromSeconds(30));
            }

            if (mode == "malformed")
            {
                Console.WriteLine("{broken-json");
                continue;
            }

            var responseKey = mode switch
            {
                "unrequested" => "unrequested-key",
                "duplicate" when processCount > 1 => "dark",
                _ => en
            };
            var response = mode == "incomplete"
                ? new { en = responseKey, machine = (string?)null, completed = false, error = (string?)"incomplete_generation" }
                : new { en = responseKey, machine = (string?)"fixture output", completed = true, error = (string?)null };
            Console.WriteLine(JsonSerializer.Serialize(response));
            Console.Out.Flush();

            if (mode == "extra")
            {
                Console.WriteLine("unexpected protocol output");
                Console.Out.Flush();
            }
            if (mode == "exit") return 7;
        }
        return 0;
    }
}
