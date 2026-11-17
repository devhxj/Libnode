#:property PublishAot=false

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

var root = Directory.GetCurrentDirectory();
string? version = null;
var output = Path.Combine(root, "artifacts/release/packages");
var nativeRoot = Path.Combine(root, "artifacts/native");
var skipPush = false;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--package-version": version = args[++index]; break;
        case "--output-directory": output = Path.GetFullPath(args[++index]); break;
        case "--native-directory": nativeRoot = Path.GetFullPath(args[++index]); break;
        case "--skip-push": skipPush = true; break;
        default: throw new ArgumentException($"Unknown option: {args[index]}");
    }
}
if (!skipPush) throw new InvalidOperationException("Local packaging requires --skip-push. Official uploads belong to .github/workflows/nuget-publish-ref.yml; no local API key is required.");
if (version is null || !Regex.IsMatch(version, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"))
    throw new ArgumentException("Pass --package-version with the semantic version selected by the release tag.");
var catalog = XDocument.Load(Path.Combine(root, "src/Packaging/LibNodePackages.props"));
var rids = catalog.Descendants("LibNodeRuntimePackage").Select(item => item.Attribute("Include")!.Value).ToArray();
if (rids.Length == 0 || rids.Distinct().Count() != rids.Length) throw new InvalidDataException("Release catalog must contain distinct, nonempty RIDs.");
var template = XDocument.Load(Path.Combine(root, "src/Jazor.LibNode/Jazor.LibNode.nuspec"));
var templateIds = template.Descendants().Where(element => element.Name.LocalName == "dependency").Select(element => element.Attribute("id")!.Value).Order().ToArray();
if (!templateIds.SequenceEqual(rids.Select(rid => "Jazor.LibNode." + rid).Order()))
    throw new InvalidDataException("Metapackage dependency IDs must match the release catalog exactly.");
var lockHash = await Hash(Path.Combine(root, "native/libnode/source.lock.json"));
var identities = new Dictionary<string, string>();
foreach (var rid in rids)
{
    var directory = Path.Combine(nativeRoot, rid);
    var libraryName = LibraryName(rid);
    foreach (var name in new[] { libraryName, "native-build.json", "NODE-LICENSE", "EMBEDDING-LICENSE", "NOTICE" })
        if (!File.Exists(Path.Combine(directory, name))) throw new FileNotFoundException($"Missing native release input for {rid}: {name}");
    var record = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "native-build.json")))!;
    var hash = await Hash(Path.Combine(directory, libraryName));
    if (record["rid"]!.GetValue<string>() != rid || record["sourceLockSha256"]!.GetValue<string>() != lockHash || record["librarySha256"]!.GetValue<string>() != hash)
        throw new InvalidDataException($"Native build provenance differs from release inputs for {rid}.");
    var verification = record["verification"];
    if (record["nativeCompatibilityVerified"]?.GetValue<bool>() != true ||
        verification?["librarySha256"]?.GetValue<string>() != hash ||
        verification?["nativeTestCount"]?.GetValue<int>() != 60 ||
        verification?["managedTotal"]?.GetValue<int>() != 22 ||
        verification?["managedPassed"]?.GetValue<int>() != 22 ||
        verification?["managedOnlyTotal"]?.GetValue<int>() != 38)
        throw new InvalidDataException($"Native, managed and consumer compatibility evidence is incomplete for {rid}.");
    identities.Add(rid, hash);
}
// The current issue lane runs on the Linux candidate. Expected managed failures
// are explicit results; unexpected native regressions must block publication.
var issues = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "artifacts/upstream-issues/results.json")))!;
if (issues["librarySha256"]!.GetValue<string>() != identities["linux-x64"] ||
    issues["unexpected"]!.AsArray().Count != 0 || issues["results"]!.AsArray().Count != 14 ||
    issues["results"]!.AsArray().Any(result => result!["expected"]?.GetValue<bool>() != true))
    throw new InvalidDataException("Upstream issue regression evidence does not match the release candidate.");
Directory.CreateDirectory(output);
// An exact closure prevents old locally packed packages from joining an upload.
if (Directory.GetFiles(output, "*.nupkg").Any(path => !rids.Append("").Select(rid => PackagePath(rid)).Contains(path)))
    throw new InvalidDataException("Release output contains packages outside the selected version/catalog. Use a fresh output directory.");
foreach (var rid in rids)
    await Run("dotnet", ["pack", "src/Jazor.LibNode.Runtime", "-c", "Release", "-p:LibNodeRid=" + rid,
        "-p:JazorReleaseVersion=" + version, "-p:NativeArtifactsRoot=" + nativeRoot, "-o", output]);
await Run("dotnet", ["pack", "src/Jazor.LibNode", "-c", "Release", "-p:JazorReleaseVersion=" + version,
    "-p:RuntimePackageDirectory=" + output, "-o", output]);
var packageRecords = new List<object>();
foreach (var rid in rids.Append(""))
{
    var path = PackagePath(rid);
    using var archive = ZipFile.OpenRead(path);
    var manifest = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
    using var stream = manifest.Open();
    var nuspec = XDocument.Load(stream);
    var metadata = nuspec.Descendants().Single(element => element.Name.LocalName == "metadata");
    string Property(string name) => metadata.Elements().Single(element => element.Name.LocalName == name).Value;
    var id = "Jazor.LibNode" + (rid.Length == 0 ? "" : "." + rid);
    if (Property("id") != id || Property("version") != version) throw new InvalidDataException($"Package ID/version differs: {path}");
    if (rid.Length == 0)
    {
        var dependencies = metadata.Descendants().Where(element => element.Name.LocalName == "dependency").ToArray();
        if (!dependencies.Select(element => element.Attribute("id")!.Value).Order().SequenceEqual(templateIds) ||
            dependencies.Any(element => element.Attribute("version")?.Value != "[" + version + "]"))
            throw new InvalidDataException("Metapackage does not pin the complete runtime closure to the same version.");
    }
    else
    {
        var native = archive.GetEntry($"runtimes/{rid}/native/{LibraryName(rid)}") ?? throw new InvalidDataException("Native runtime asset missing from package.");
        await using var nativeStream = native.Open();
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(nativeStream)) != identities[rid])
            throw new InvalidDataException("Packed native asset differs from the tested binary.");
        foreach (var name in new[] { "native-build.json", "NODE-LICENSE", "EMBEDDING-LICENSE", "NOTICE", "LICENSE.txt", "README.md" })
            if (archive.GetEntry(name) is null) throw new InvalidDataException($"Missing packaged file: {name}");
    }
    packageRecords.Add(new { id, version, file = Path.GetFileName(path), sha256 = await Hash(path) });
}
// Exercise the release metapackage and release version, not only the local RID
// package checked by verify-libnode. NuGet selects the native asset from the graph.
var consumer = "tests/NuGetConsumer/NuGetConsumer.csproj";
var packageCache = Path.Combine(root, "artifacts/release-consumers", Guid.NewGuid().ToString("N"));
await Run("dotnet", ["restore", consumer, "-p:CandidatePackageVersion=" + version, "-p:CandidateNativePackageId=Jazor.LibNode",
    "--packages", packageCache, "--source", output, "--source", "https://api.nuget.org/v3/index.json", "--force"]);
var assets = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "tests/NuGetConsumer/obj/project.assets.json")))!;
if (assets["libraries"]!.AsObject().Any(item => item.Key.StartsWith("Microsoft.JavaScript.LibNode", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidDataException("Release consumer contains competing Microsoft native assets.");
await Run("dotnet", ["run", "--project", consumer, "-c", "Release", "--no-restore", "-p:CandidatePackageVersion=" + version,
    "-p:CandidateNativePackageId=Jazor.LibNode", "--", identities["linux-x64"], Path.Combine(output, "release-consumer.json")]);
var commit = (await Run("git", ["rev-parse", "HEAD"])).Trim();
var release = new { packageVersion = version, commit, sourceLockSha256 = lockHash, rids, packages = packageRecords, consumerVerified = true };
await File.WriteAllTextAsync(Path.Combine(output, "release-manifest.json"), JsonSerializer.Serialize(release, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Packed and verified {packageRecords.Count} packages at {version}. --skip-push: nothing uploaded.");

string PackagePath(string rid) => Path.Combine(output, "Jazor.LibNode" + (rid.Length == 0 ? "" : "." + rid) + "." + version + ".nupkg");

static string LibraryName(string rid) => rid switch
{
    "linux-x64" => "libnode.so",
    "win-x64" => "libnode.dll",
    "osx-x64" or "osx-arm64" => "libnode.dylib",
    _ => throw new InvalidDataException($"Unsupported release RID: {rid}")
};

static async Task<string> Hash(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}

async Task<string> Run(string executable, string[] arguments)
{
    Console.WriteLine($"{executable} {string.Join(' ', arguments)}");
    var start = new ProcessStartInfo(executable) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var result = await stdout + await stderr;
    if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} exited {process.ExitCode}: {result}");
    return result;
}
