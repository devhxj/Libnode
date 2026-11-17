#:property PublishAot=false

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (args.Length is not (1 or 2) || !Regex.IsMatch(args[0], @"^[1-9]\d*$"))
    throw new ArgumentException("Pass the numeric Windows candidate run ID.");
var runId = args[0];
var rid = args.Length == 2 ? args[1] : "win-x64";
if (rid is not ("win-x64" or "win-arm64")) throw new ArgumentException("Select win-x64 or win-arm64.");
for (var attempt = 0; attempt < 350; attempt++)
{
    var metadata = JsonNode.Parse(await Api($"repos/devhxj/Libnode/actions/runs/{runId}"))!;
    if (metadata["path"]!.GetValue<string>() is not (".github/workflows/windows-candidate.yml" or ".github/workflows/arm-candidate.yml"))
        throw new InvalidDataException("Native inputs must come from the Windows candidate workflow.");
    var status = metadata["status"]!.GetValue<string>();
    Console.WriteLine($"Windows candidate {runId}: {status}");
    var artifacts = JsonNode.Parse(await Api($"repos/devhxj/Libnode/actions/runs/{runId}/artifacts"))!;
    if (artifacts["artifacts"]!.AsArray().Any(item => item!["name"]!.GetValue<string>() == "libnode-" + rid + "-build" && item["expired"]!.GetValue<bool>() == false))
    {
        Console.WriteLine("Windows native artifact is ready. Import and fresh verification are required.");
        return;
    }
    if (status == "completed")
    {
        throw new InvalidDataException("Windows candidate completed without reusable native outputs; inspect its build log.");
    }
    await Task.Delay(TimeSpan.FromSeconds(60));
}
throw new TimeoutException("Windows candidate did not complete within the release wait budget.");

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
