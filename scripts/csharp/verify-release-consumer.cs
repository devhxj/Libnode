#:property PublishAot=false

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml.Linq;

if (args.Length != 2 || args[0] != "--package-version") throw new ArgumentException("Pass --package-version <version>.");
var version = args[1];
var root = Directory.GetCurrentDirectory();
var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
var libraryName = OperatingSystem.IsWindows() ? "libnode.dll" : "libnode.so";
var packages = Path.Combine(root, "artifacts/release/packages");
var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(packages, "release-manifest.json")))!;
if (manifest["packageVersion"]!.GetValue<string>() != version || !manifest["rids"]!.AsArray().Any(item => item!.GetValue<string>() == rid))
    throw new InvalidDataException("Release manifest differs from requested package version or platform.");
foreach (var record in manifest["packages"]!.AsArray())
{
    var name = record!["file"]!.GetValue<string>();
    if (name != record["id"]!.GetValue<string>() + "." + version + ".nupkg")
        throw new InvalidDataException("Unexpected release package name.");
    await using var input = File.OpenRead(Path.Combine(packages, name));
    if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input)) != record["sha256"]!.GetValue<string>())
        throw new InvalidDataException("Release package checksum mismatch.");
}
using var runtime = ZipFile.OpenRead(Path.Combine(packages, "Jazor.LibNode." + rid + "." + version + ".nupkg"));
await using var native = runtime.GetEntry($"runtimes/{rid}/native/{libraryName}")!.Open();
var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(native));
var consumer = "tests/NuGetConsumer/NuGetConsumer.csproj";
var cache = Path.Combine(root, "artifacts/release-consumers", Guid.NewGuid().ToString("N"));
var properties = new[] { "-p:CandidateNativePackageId=Jazor.LibNode", "-p:CandidatePackageVersion=" + version };
var config = Path.Combine(root, "artifacts/release-consumer.NuGet.Config");
new XDocument(new XElement("configuration", new XElement("packageSources",
    new XElement("clear"),
    new XElement("add", new XAttribute("key", "release"), new XAttribute("value", packages)),
    new XElement("add", new XAttribute("key", "nuget.org"), new XAttribute("value", "https://api.nuget.org/v3/index.json")))))
    .Save(config);
await Run(["restore", consumer, .. properties, "--packages", cache, "--configfile", config, "--force"]);
var assets = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "tests/NuGetConsumer/obj/project.assets.json")))!;
if (assets["libraries"]!.AsObject().Any(item => item.Key.StartsWith("Microsoft.JavaScript.LibNode", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidDataException("Release consumer contains competing native runtime assets.");
await Run(["run", "--project", consumer, "-c", "Release", "--no-restore", .. properties, "--", hash, Path.Combine(packages, "release-consumer-" + rid + ".json")]);

static async Task Run(string[] arguments)
{
    var start = new ProcessStartInfo("dotnet");
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException($"dotnet exited {process.ExitCode}.");
}
