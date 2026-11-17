#:property PublishAot=false

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (args.Length != 1 || !Regex.IsMatch(args[0], @"^[1-9]\d*$"))
    throw new ArgumentException("Pass the numeric Linux ARM64 candidate run ID.");
var runId = args[0];
for (var attempt = 0; attempt < 350; attempt++)
{
    var run = JsonNode.Parse(await Api($"repos/devhxj/Libnode/actions/runs/{runId}"))!;
    if (run["path"]!.GetValue<string>() != ".github/workflows/arm-candidate.yml")
        throw new InvalidDataException("Linux ARM64 inputs must come from the ARM64 candidate workflow.");
    var artifacts = JsonNode.Parse(await Api($"repos/devhxj/Libnode/actions/runs/{runId}/artifacts"))!;
    if (artifacts["artifacts"]!.AsArray().Any(item => item!["name"]!.GetValue<string>() == "libnode-linux-arm64-candidate" && item["expired"]!.GetValue<bool>() == false))
    {
        Console.WriteLine("Linux ARM64 candidate artifact is ready. Import, hash checks and fresh verification are required.");
        return;
    }
    if (run["status"]!.GetValue<string>() == "completed")
        throw new InvalidDataException("Linux ARM64 candidate completed without reusable native outputs; inspect the build log.");
    Console.WriteLine($"Waiting for Linux ARM64 candidate {runId}.");
    await Task.Delay(TimeSpan.FromSeconds(60));
}
throw new TimeoutException("Linux ARM64 candidate did not complete within the release wait budget.");

static async Task<string> Api(string endpoint)
{
    var start = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add("api");
    start.ArgumentList.Add(endpoint);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException(await stderr);
    return await stdout;
}
