#:property PublishAot=false

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

var root = Directory.GetCurrentDirectory();
if (!OperatingSystem.IsLinux())
{
    Console.WriteLine("The addon issue lane is Linux-specific; Windows native compatibility is covered by verify-libnode.cs.");
    return;
}
var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" :
    RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x64" : throw new PlatformNotSupportedException("Linux issue probes support x64 and ARM64.");
var rid = "linux-" + architecture;
var output = Path.Combine(root, "artifacts/upstream-issues", rid);
Directory.CreateDirectory(output);
var library = Path.Combine(root, "artifacts/native/" + rid + "/libnode.so");
var project = Path.Combine(root, "tests/UpstreamIssues/UpstreamIssues.csproj");
var build = await Run("dotnet", ["build", project, "--nologo"], TimeSpan.FromMinutes(2));
if (build.ExitCode != 0) throw new InvalidOperationException(build.Output);
var localGcc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/opt/jazor-toolchain/usr/bin/gcc");
var addon = Path.Combine(output, "issue426.node");
var compile = await Run(File.Exists(localGcc) ? localGcc : "gcc", ["-shared", "-fPIC", "-I" + Path.Combine(root, "artifacts/sources/node-v26.13.1/src"),
    Path.Combine(root, "tests/UpstreamIssues/addon.c"), "-o", addon], TimeSpan.FromSeconds(30));
if (compile.ExitCode != 0) throw new InvalidOperationException(compile.Output);
var assembly = Path.Combine(root, "tests/UpstreamIssues/bin/Debug/net11.0/UpstreamIssues.dll");
using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "tests/UpstreamIssues/fixtures.lock.json")));
var fixture = fixtures.RootElement.GetProperty(architecture == "arm64" ? "rolldownArm64" : "rolldown");
var archive = Path.Combine(output, "rolldown.tgz");
if (!File.Exists(archive))
{
    using var http = new HttpClient();
    await File.WriteAllBytesAsync(archive, await http.GetByteArrayAsync(fixture.GetProperty("url").GetString()!));
}
var fixtureBytes = await File.ReadAllBytesAsync(archive);
if ("sha512-" + Convert.ToBase64String(SHA512.HashData(fixtureBytes)) != fixture.GetProperty("integrity").GetString())
    throw new InvalidDataException("Rolldown fixture checksum mismatch.");
var rustAddon = Path.Combine(output, "rolldown.node");
using (var compressed = new GZipStream(new MemoryStream(fixtureBytes), CompressionMode.Decompress))
using (var tar = new TarReader(compressed))
{
    var found = 0;
    while (tar.GetNextEntry() is { } entry)
    {
        if (!entry.Name.EndsWith(".node", StringComparison.Ordinal)) continue;
        await using var target = File.Create(rustAddon);
        await entry.DataStream!.CopyToAsync(target);
        found++;
    }
    if (found != 1) throw new InvalidDataException($"Expected one Rolldown native addon, found {found}.");
}
var scenarios = new[] { "concurrent-default", "concurrent-configured", "timer-dispose", "timer-clear", "timer-terminate", "reference", "linked-module", "linked-main", "typed-error", "addon", "addon-typed-error", "rust-addon", "no-wait", "workers" };
var results = new List<object>();
var unexpected = new List<string>();
foreach (var scenario in scenarios)
{
    // Crash/hang repros run in their own process: one platform per process, and
    // a broken teardown must not take down or stall the rest of the audit.
    var result = await Run("dotnet", [assembly, scenario, library, scenario == "rust-addon" ? rustAddon : addon], TimeSpan.FromSeconds(10));
    await File.WriteAllTextAsync(Path.Combine(output, scenario + ".log"), result.Output);
    var expected = scenario switch
    {
        "timer-dispose" => result.TimedOut,
        "linked-module" => !result.TimedOut && result.ExitCode == 134 && result.Output.Contains("A Root scope cannot be created within another scope.", StringComparison.Ordinal),
        "linked-main" => !result.TimedOut && result.ExitCode == 134 && result.Output.Contains("A Callback scope cannot be created without a parent scope.", StringComparison.Ordinal),
        "typed-error" => !result.TimedOut && result.ExitCode == 2 && result.Output.Contains("\"observed\":\"Error\"", StringComparison.Ordinal),
        _ => !result.TimedOut && result.ExitCode == 0
    };
    if (!expected) unexpected.Add(scenario);
    var knownManagedLimitation = scenario is "timer-dispose" or "linked-module" or "linked-main" or "typed-error";
    results.Add(new { scenario, result.ExitCode, result.TimedOut, result.ElapsedMs, expected, knownManagedLimitation });
    Console.WriteLine($"{scenario}: exit={result.ExitCode}, timeout={result.TimedOut}, elapsed={result.ElapsedMs}ms");
}
await using var stream = File.OpenRead(library);
var report = new { rid, librarySha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)), fixture = fixture.Clone(), results, unexpected };
await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
// Preserve the initial x64 report location for existing local evidence/tools.
if (rid == "linux-x64") File.Copy(Path.Combine(output, "results.json"), Path.Combine(root, "artifacts/upstream-issues/results.json"), true);
if (unexpected.Count != 0) throw new InvalidOperationException("Unexpected issue regression results: " + string.Join(", ", unexpected));

async Task<ProcessResult> Run(string executable, string[] arguments, TimeSpan timeout)
{
    var start = new ProcessStartInfo(executable) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {executable}.");
    var timer = Stopwatch.StartNew();
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    var timedOut = false;
    using var cancellation = new CancellationTokenSource(timeout);
    try { await process.WaitForExitAsync(cancellation.Token); }
    catch (OperationCanceledException)
    {
        timedOut = true;
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }
    return new(process.ExitCode, timedOut, timer.ElapsedMilliseconds, await stdout + await stderr);
}

record ProcessResult(int ExitCode, bool TimedOut, long ElapsedMs, string Output);
