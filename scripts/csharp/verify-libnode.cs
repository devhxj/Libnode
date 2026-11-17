#:property PublishAot=false

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

var root = Directory.GetCurrentDirectory();
var prepareOnly = args.SequenceEqual(new[] { "--prepare-upstream" });
if (args.Length != 0 && !prepareOnly) throw new ArgumentException("Usage: verify-libnode.cs [--prepare-upstream]");
var inputs = Path.Combine(root, "artifacts/inputs");
var sourceLock = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "native/libnode/source.lock.json")))!;
await Run("dotnet", ["run", "--file", "scripts/csharp/prepare-libnode.cs", "--", "--offline"], root);
var revision = sourceLock["nodeApi"]!["commit"]!.GetValue<string>();
var upstream = Path.Combine(root, "artifacts/sources/node-api-dotnet-" + revision);
var adaptation = Path.Combine(root, "native/libnode/patches/nodeapi-tests.patch");
var adaptationHash = await Hash(adaptation);
if (adaptationHash != sourceLock["testAdaptation"]!["sha256"]!.GetValue<string>())
    throw new InvalidDataException("Upstream test adaptation hash differs from source.lock.json.");
var marker = Path.Combine(upstream, ".jazor-adaptation");
if (!Directory.Exists(upstream))
{
    Directory.CreateDirectory(Path.GetDirectoryName(upstream)!);
    await Run("tar", ["-xzf", Path.Combine(inputs, "node-api-dotnet.tar.gz"), "-C", Path.GetDirectoryName(upstream)!], root);
}
if (!File.Exists(marker))
{
    await Run("git", ["init", "--quiet"], upstream);
    await Run("git", ["apply", "--check", adaptation], upstream);
    await Run("git", ["apply", adaptation], upstream);
    await File.WriteAllTextAsync(marker, adaptationHash);
}
if ((await File.ReadAllTextAsync(marker)).Trim() != adaptationHash)
    throw new InvalidOperationException("Upstream test adaptation changed. Prepare a fresh upstream checkout.");
var project = Path.Combine(upstream, "test/NodeApi.Test.csproj");
var frameworkArgs = new[] { "-p:TargetFramework=net11.0", "-p:TargetFrameworks=net11.0", "-p:CETCompat=false" };
await Run("dotnet", ["build", project, .. frameworkArgs], upstream, "upstream-build.log");
var filter = "FullyQualifiedName~NodejsEmbeddingTests|FullyQualifiedName~GCTests";
var discovery = await Run("dotnet", ["test", project, "--no-build", .. frameworkArgs, "--list-tests", "--filter", filter], upstream, "upstream-discovery.log");
var discoveredTests = discovery.Split('\n').Select(line => line.Trim())
    .Where(line => line.StartsWith("Microsoft.JavaScript.NodeApi.Test.NodejsEmbeddingTests.", StringComparison.Ordinal) ||
        line.StartsWith("Microsoft.JavaScript.NodeApi.Test.GCTests.", StringComparison.Ordinal)).ToArray();
if (discoveredTests.Length != 22)
    throw new InvalidDataException($"Expected 22 embedding/GC cases at {revision}, discovered {discoveredTests.Length}.");
if (prepareOnly) return;

var candidateDirectory = Path.Combine(root, "artifacts/native/linux-x64");
var library = Path.Combine(candidateDirectory, "libnode.so");
var hash = await Hash(library);
var nativeRecord = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(candidateDirectory, "native-build.json")))!;
if (nativeRecord["sourceLockSha256"]!.GetValue<string>() != await Hash(Path.Combine(root, "native/libnode/source.lock.json")))
    throw new InvalidDataException("Candidate was built from a different source lock.");
if (nativeRecord["librarySha256"]!.GetValue<string>() != hash) throw new InvalidDataException("Candidate library hash differs from the build manifest.");
var nodeSource = Path.Combine(root, "artifacts/sources/node-" + sourceLock["node"]!["tag"]!.GetValue<string>());
var harness = Path.Combine(nodeSource, "out/Release/embedtest");
var linkage = await Run("ldd", [harness], root, "native-harness-linkage.log");
var originalLibrary = Directory.GetFiles(Path.Combine(nodeSource, "out/Release"), "libnode.so.*").Single();
if (await Hash(originalLibrary) != hash || !linkage.Contains(originalLibrary, StringComparison.Ordinal))
    throw new InvalidDataException("Native harness does not link the selected candidate library.");
var driver = Path.Combine(nodeSource, "test/embedding/test-embedding-c-api.js");
var nativeLog = await Run(harness, ["c-api", "require(" + JsonSerializer.Serialize(driver) + ")"], nodeSource, "native-tests.log");
var nativeTestCount = nativeLog.Split('\n').Count(line => line.StartsWith("Run test: ", StringComparison.Ordinal) && line.EndsWith("ok", StringComparison.Ordinal));
if (nativeTestCount != 60) throw new InvalidDataException($"Expected 60 reused native cases, observed {nativeTestCount}.");

Directory.CreateDirectory(Path.Combine(upstream, "candidate"));
File.Copy(library, Path.Combine(upstream, "candidate/libnode.so"), true);
var results = Path.Combine(root, "artifacts/verification");
Directory.CreateDirectory(results);
await Run("dotnet", ["test", project, "--no-build", .. frameworkArgs, "--filter", filter,
    "--logger", "trx;LogFileName=upstream.trx", "--results-directory", results], upstream, "upstream-tests.log");
var trx = XDocument.Load(Path.Combine(results, "upstream.trx"));
var counters = trx.Descendants().Single(element => element.Name.LocalName == "Counters");
var total = int.Parse(counters.Attribute("total")!.Value);
var passed = int.Parse(counters.Attribute("passed")!.Value);
if (total != discoveredTests.Length || passed != total) throw new InvalidDataException($"Upstream suite not fully passed: {counters}");
await Run("dotnet", ["test", project, "--no-build", .. frameworkArgs, "--filter",
    "FullyQualifiedName~JSReferenceTests|FullyQualifiedName~JSValueScopeTests|FullyQualifiedName~TsfnCallGateTests",
    "--logger", "trx;LogFileName=managed-only.trx", "--results-directory", results], upstream, "managed-only-tests.log");
var mockTrx = XDocument.Load(Path.Combine(results, "managed-only.trx"));
var mockCounters = mockTrx.Descendants().Single(element => element.Name.LocalName == "Counters");
var managedOnlyTotal = int.Parse(mockCounters.Attribute("total")!.Value);
if (managedOnlyTotal == 0 || int.Parse(mockCounters.Attribute("passed")!.Value) != managedOnlyTotal)
    throw new InvalidDataException($"Managed-only suite not fully passed: {mockCounters}");
var assets = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(upstream, "out/obj/Debug/NodeApi.Test/project.assets.json")))!;
if (assets["libraries"]!.AsObject().Any(pair => pair.Key.StartsWith("Microsoft.JavaScript.LibNode", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidDataException("Upstream test graph still contains Microsoft's native package.");

// A local, unpublished candidate version; official packages use Jazor's tag.
var version = "0.0.0-local." + hash[..12];
var packages = Path.Combine(root, "artifacts/packages");
await Run("dotnet", ["pack", "src/Jazor.LibNode.Runtime", "-p:LibNodeRid=linux-x64", "-p:JazorReleaseVersion=" + version, "-o", packages], root, "runtime-pack.log");
var consumer = Path.Combine(root, "tests/NuGetConsumer/NuGetConsumer.csproj");
var packageCache = Path.Combine(root, "artifacts/consumer-packages");
await Run("dotnet", ["restore", consumer, "-p:CandidatePackageVersion=" + version, "--packages", packageCache,
    "--source", packages, "--source", "https://api.nuget.org/v3/index.json", "--force"], root, "consumer-restore.log");
var managedVersion = sourceLock["nodeApi"]!["version"]!.GetValue<string>();
var managedNuspec = XDocument.Load(Path.Combine(packageCache, "microsoft.javascript.nodeapi", managedVersion, "microsoft.javascript.nodeapi.nuspec"));
var packageRevision = managedNuspec.Descendants().Single(element => element.Name.LocalName == "repository").Attribute("commit")?.Value;
if (packageRevision != revision) throw new InvalidDataException($"Managed NuGet source revision differs: {packageRevision}");
var consumerAssets = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "tests/NuGetConsumer/obj/project.assets.json")))!;
if (consumerAssets["libraries"]!.AsObject().Any(pair => pair.Key.StartsWith("Microsoft.JavaScript.LibNode", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidDataException("Consumer graph contains competing native runtime assets.");
await Run("dotnet", ["run", "--project", consumer, "--no-restore", "-p:CandidatePackageVersion=" + version,
    "--", hash, Path.Combine(results, "nuget-consumer.json")], root, "consumer-run.log");
var report = new
{
    rid = "linux-x64", librarySha256 = hash,
    upstreamRevision = revision, adaptationSha256 = adaptationHash,
    nativeTestCount, managedTotal = total, managedPassed = passed,
    discoveredTests, managedOnlyTotal,
    mockTestsCountedAsNativeEvidence = 0,
    actualNuGetConsumer = "nuget-consumer.json",
    releaseAcceptanceComplete = false,
    pending = new[] { "Other candidate RIDs", "Minimum deployment baseline", "Jazor integration and release gates", "Additional old RID migration decisions" }
};
await File.WriteAllTextAsync(Path.Combine(results, "compatibility.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
nativeRecord["runtimeIdentity"] = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(results, "nuget-consumer.json")))!["runtime"]!.DeepClone();
nativeRecord["verification"] = JsonSerializer.SerializeToNode(report);
nativeRecord["nativeCompatibilityVerified"] = true;
await File.WriteAllTextAsync(Path.Combine(candidateDirectory, "native-build.json"), nativeRecord.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
// Repack the same tested binary with its runtime identity and verification record.
await Run("dotnet", ["pack", "src/Jazor.LibNode.Runtime", "-p:LibNodeRid=linux-x64", "-p:JazorReleaseVersion=" + version, "-o", packages], root, "runtime-pack-verified.log");
Console.WriteLine($"Linux candidate passed {nativeTestCount} native cases, {passed} managed tests and the actual NuGet consumer.");

static async Task<string> Hash(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}

async Task<string> Run(string executable, string[] arguments, string directory, string? logName = null)
{
    Console.WriteLine($"{executable} {string.Join(' ', arguments)}");
    var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {executable}");
    Directory.CreateDirectory(Path.Combine(root, "artifacts/verification"));
    using var writer = logName is null ? null : new StreamWriter(Path.Combine(root, "artifacts/verification", logName)) { AutoFlush = true };
    var outputTask = Capture(process.StandardOutput);
    var errorsTask = Capture(process.StandardError);
    await process.WaitForExitAsync();
    var result = await outputTask + await errorsTask;
    if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} exited {process.ExitCode}: {logName ?? result}");
    return result;

    async Task<string> Capture(StreamReader reader)
    {
        var text = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (writer is not null) { lock (writer) writer.WriteLine(line); }
            text.AppendLine(line);
        }
        return text.ToString();
    }
}
