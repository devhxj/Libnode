#:property PublishAot=false

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

// Exercise the real local binary handoff in a clean checkout. No native build
// runs here; existing compatibility/issue/release scripts test the imported build.
var root = Directory.GetCurrentDirectory();
var runRoot = Path.Combine(root, "artifacts/native-bundle-verification", Guid.NewGuid().ToString("N"));
var fixture = Path.Combine(runRoot, "checkout");
var archivePath = Path.Combine(runRoot, "native.zip");
var script = Path.Combine(root, "scripts/csharp/native-bundle.cs");
Directory.CreateDirectory(runRoot);
var results = new List<object>();
await Success("pack verified local build", root, ["pack", "--archive", archivePath]);
var hash = await Hash(archivePath);
await Success("repeat pack replaces the archive", root, ["pack", "--archive", archivePath]);
Check("bundle is deterministic", hash == await Hash(archivePath));
await Command("git", root, ["worktree", "add", "--detach", fixture, "HEAD"]);
// Reuse only downloaded inputs, never the original configured/built source tree.
var inputs = Path.Combine(root, "artifacts/inputs");
foreach (var file in Directory.GetFiles(inputs, "*", SearchOption.AllDirectories))
{
    var destination = Path.Combine(fixture, "artifacts/inputs", Path.GetRelativePath(inputs, file));
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.Copy(file, destination);
}
await Failure("wrong archive SHA256 is rejected", ["import", "--archive", archivePath, "--sha256", new string('0', 64)], "Native bundle SHA256 mismatch");
await Alter("wrong checkout commit is rejected", archive => EditManifest(archive, node => node["repositoryCommit"] = new string('0', 40)), "does not match this checkout");
await Alter("wrong source lock is rejected", archive => EditManifest(archive, node => node["sourceLockSha256"] = new string('0', 64)), "does not match this checkout");
await Alter("changed file is rejected", archive =>
{
    archive.GetEntry("embedtest")!.Delete();
    using var writer = new StreamWriter(archive.CreateEntry("embedtest").Open());
    writer.Write("changed harness");
}, "file checksum mismatch: embedtest");
await Alter("unexpected entry is rejected", archive => archive.CreateEntry("../unexpected"), "file list does not match");
await Alter("inconsistent build identity is rejected", archive => EditManifest(archive, node => node["librarySha256"] = new string('0', 64)), "build record differs");
Check("rejections did not install native inputs", !Directory.Exists(Path.Combine(fixture, "artifacts/native")));
await Success("import into a clean checkout without compiling V8", fixture, ["import", "--archive", archivePath, "--sha256", hash]);
var native = Path.Combine(fixture, "artifacts/native/linux-x64");
var record = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(native, "native-build.json")))!;
Check("local verification cannot authorize packaging", record["nativeCompatibilityVerified"]!.GetValue<bool>() == false && record["verification"] is null && record["localVerification"] is not null);
Check("local provenance is retained", record["distributionOrigin"]!["archiveSha256"]!.GetValue<string>() == hash);
Check("imported binary is identical", await Hash(Path.Combine(native, "libnode.so")) == record["librarySha256"]!.GetValue<string>());
var sourceLock = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "native/libnode/source.lock.json")))!;
var release = Path.Combine(fixture, "artifacts/sources/node-" + sourceLock["node"]!["tag"]!.GetValue<string>(), "out/Release");
Check("source preparation produced no V8 build outputs", Directory.GetFileSystemEntries(release).Select(Path.GetFileName).Order().SequenceEqual(new[] { "embedtest", "libnode.so." + record["runtimeIdentity"]!["modules"]!.GetValue<string>() }.Order()));
var packResult = await Run("dotnet", fixture, ["run", "--file", Path.Combine(root, "scripts/csharp/publish-nuget.cs"), "--", "--package-version", "0.1.0-bundlecheck", "--skip-push"]);
await Record("packaging requires fresh verification", packResult.ExitCode != 0 && packResult.Output.Contains("compatibility evidence is incomplete", StringComparison.OrdinalIgnoreCase), packResult.Output);
await File.WriteAllTextAsync(Path.Combine(runRoot, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Native bundle verification passed {results.Count} checks. Clean imported checkout retained at {fixture}");

async Task Alter(string name, Action<ZipArchive> mutate, string error)
{
    var changed = Path.Combine(runRoot, "changed.zip");
    File.Copy(archivePath, changed, overwrite: true);
    using (var archive = ZipFile.Open(changed, ZipArchiveMode.Update)) mutate(archive);
    await Failure(name, ["import", "--archive", changed, "--sha256", await Hash(changed)], error);
    File.Delete(changed);
}

static void EditManifest(ZipArchive archive, Action<JsonNode> mutate)
{
    var entry = archive.GetEntry("native-bundle.json")!;
    JsonNode node;
    using (var reader = new StreamReader(entry.Open())) node = JsonNode.Parse(reader.ReadToEnd())!;
    mutate(node);
    entry.Delete();
    using var writer = new StreamWriter(archive.CreateEntry("native-bundle.json").Open());
    writer.Write(node.ToJsonString());
}

async Task Success(string name, string directory, string[] arguments)
{
    var result = await Run("dotnet", directory, ["run", "--file", script, "--", .. arguments]);
    await Record(name, result.ExitCode == 0, result.Output);
}

async Task Failure(string name, string[] arguments, string error)
{
    var result = await Run("dotnet", fixture, ["run", "--file", script, "--", .. arguments]);
    await Record(name, result.ExitCode != 0 && result.Output.Contains(error, StringComparison.OrdinalIgnoreCase), result.Output);
}

void Check(string name, bool passed)
{
    results.Add(new { name, passed });
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {name}");
    if (!passed) throw new InvalidOperationException(name);
}

async Task Record(string name, bool passed, string output)
{
    await File.WriteAllTextAsync(Path.Combine(runRoot, (results.Count + 1).ToString("D2") + ".log"), output);
    Check(name, passed);
}

static async Task Command(string executable, string directory, string[] arguments)
{
    var result = await Run(executable, directory, arguments);
    if (result.ExitCode != 0) throw new InvalidOperationException(result.Output);
}

static async Task<string> Hash(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}

static async Task<(int ExitCode, string Output)> Run(string executable, string directory, string[] arguments)
{
    var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, await stdout + await stderr);
}
