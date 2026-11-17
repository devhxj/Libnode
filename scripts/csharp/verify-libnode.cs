#:property PublishAot=false

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

var root = Directory.GetCurrentDirectory();
var windows = OperatingSystem.IsWindows();
if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
    throw new PlatformNotSupportedException("Verification supports native x64 and ARM64 processes.");
var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
var rid = (windows ? "win-" : "linux-") + architecture;
var libraryName = windows ? "libnode.dll" : "libnode.so";
var prepareOnly = args.Contains("--prepare-upstream", StringComparer.Ordinal);
var inputsPrepared = args.Contains("--inputs-prepared", StringComparer.Ordinal);
if (args.Any(argument => argument is not ("--prepare-upstream" or "--inputs-prepared")))
    throw new ArgumentException("Usage: verify-libnode.cs [--prepare-upstream] [--inputs-prepared]");
var inputs = Path.Combine(root, "artifacts/inputs");
var sourceLock = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "native/libnode/source.lock.json")))!;
if (!inputsPrepared)
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
    if (windows)
        await Run("python", ["-c", "import sys,tarfile; tarfile.open(sys.argv[1]).extractall(sys.argv[2],filter='data')", Path.Combine(inputs, "node-api-dotnet.tar.gz"), Path.GetDirectoryName(upstream)!], root);
    else
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
if (windows)
{
    var tests = Path.Combine(upstream, "test/NodejsEmbeddingTests.cs");
    var text = await File.ReadAllTextAsync(tests);
    text = text.Replace("\"candidate\", \"libnode.so\"", "\"candidate\", \"libnode.dll\"", StringComparison.Ordinal);
    if (!text.Contains("\"candidate\", \"libnode.dll\"", StringComparison.Ordinal))
        throw new InvalidDataException("Windows test candidate selection could not be applied.");
    await File.WriteAllTextAsync(tests, text);
}
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

var candidateDirectory = Path.Combine(root, "artifacts/native", rid);
var library = Path.Combine(candidateDirectory, libraryName);
await Run("dotnet", ["run", "--file", "scripts/csharp/verify-native-architecture.cs", "--", "--rid", rid, "--library", library], root);
var hash = await Hash(library);
var nativeRecord = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(candidateDirectory, "native-build.json")))!;
if (nativeRecord["sourceLockSha256"]!.GetValue<string>() != await Hash(Path.Combine(root, "native/libnode/source.lock.json")))
    throw new InvalidDataException("Candidate was built from a different source lock.");
if (nativeRecord["librarySha256"]!.GetValue<string>() != hash) throw new InvalidDataException("Candidate library hash differs from the build manifest.");
if (windows && nativeRecord["windowsHarnessPatchSha256"]?.GetValue<string>() != await Hash(Path.Combine(root, "native/libnode/patches/windows-shared-harness.patch")))
    throw new InvalidDataException("Windows harness patch differs from its native build record.");
if (windows && nativeRecord["windowsPointerPatchSha256"]?.GetValue<string>() != await Hash(Path.Combine(root, "native/libnode/patches/windows-pointer-exchange.patch")))
    throw new InvalidDataException("Windows pointer patch differs from its native build record.");
if (windows && nativeRecord["windowsSnapshotPatchSha256"]?.GetValue<string>() != await Hash(Path.Combine(root, "native/libnode/patches/windows-snapshot-exports.patch")))
    throw new InvalidDataException("Windows snapshot patch differs from its native build record.");
var nodeSource = Path.Combine(root, "artifacts/sources/node-" + sourceLock["node"]!["tag"]!.GetValue<string>());
var harness = Path.Combine(nodeSource, "out/Release", windows ? "embedtest.exe" : "embedtest");
var linkage = windows ? "" : await Run("ldd", [harness], root, "native-harness-linkage.log");
var originalLibrary = windows ? Path.Combine(nodeSource, "out/Release/libnode.dll") : Directory.GetFiles(Path.Combine(nodeSource, "out/Release"), "libnode.so.*").Single();
if (await Hash(originalLibrary) != hash || (!windows && !linkage.Contains(originalLibrary, StringComparison.Ordinal)))
    throw new InvalidDataException("Native harness does not link the selected candidate library.");
var driver = Path.Combine(nodeSource, "test/embedding/test-embedding-c-api.js");
var nativeLog = await Run(harness, ["c-api", "require(" + JsonSerializer.Serialize(driver) + ")"], nodeSource, "native-tests.log");
var nativeTestCount = nativeLog.Split('\n').Select(line => line.TrimEnd('\r'))
    .Count(line => line.StartsWith("Run test: ", StringComparison.Ordinal) && line.EndsWith("ok", StringComparison.Ordinal));
if (nativeTestCount != 60) throw new InvalidDataException($"Expected 60 reused native cases, observed {nativeTestCount}.");

Directory.CreateDirectory(Path.Combine(upstream, "candidate"));
File.Copy(library, Path.Combine(upstream, "candidate", libraryName), true);
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

// A local, unpublished candidate version; official packages use the release version.
var version = "0.0.0-local." + hash[..12];
var packages = Path.Combine(root, "artifacts/packages");
await Run("dotnet", ["pack", "src/Jazor.LibNode.Runtime", "-p:LibNodeRid=" + rid, "-p:JazorReleaseVersion=" + version, "-o", packages], root, "runtime-pack.log");
var consumer = Path.Combine(root, "tests/NuGetConsumer/NuGetConsumer.csproj");
var packageCache = Path.Combine(root, "artifacts/consumer-packages");
var nativePackage = "-p:CandidateNativePackageId=Jazor.LibNode." + rid;
var consumerConfig = Path.Combine(root, "artifacts/consumer.NuGet.Config");
new XDocument(new XElement("configuration", new XElement("packageSources",
    new XElement("clear"),
    new XElement("add", new XAttribute("key", "candidate"), new XAttribute("value", packages)),
    new XElement("add", new XAttribute("key", "nuget.org"), new XAttribute("value", "https://api.nuget.org/v3/index.json")))))
    .Save(consumerConfig);
await Run("dotnet", ["restore", consumer, "-p:CandidatePackageVersion=" + version, nativePackage, "--packages", packageCache,
    "--configfile", consumerConfig, "--force"], root, "consumer-restore.log");
var managedVersion = sourceLock["nodeApi"]!["version"]!.GetValue<string>();
var managedNuspec = XDocument.Load(Path.Combine(packageCache, "microsoft.javascript.nodeapi", managedVersion, "microsoft.javascript.nodeapi.nuspec"));
var packageRevision = managedNuspec.Descendants().Single(element => element.Name.LocalName == "repository").Attribute("commit")?.Value;
if (packageRevision != revision) throw new InvalidDataException($"Managed NuGet source revision differs: {packageRevision}");
var consumerAssets = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "tests/NuGetConsumer/obj/project.assets.json")))!;
if (consumerAssets["libraries"]!.AsObject().Any(pair => pair.Key.StartsWith("Microsoft.JavaScript.LibNode", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidDataException("Consumer graph contains competing native runtime assets.");
await Run("dotnet", ["run", "--project", consumer, "--no-restore", "-p:CandidatePackageVersion=" + version, nativePackage,
    "--", hash, Path.Combine(results, "nuget-consumer.json")], root, "consumer-run.log");
var report = new
{
    rid, librarySha256 = hash,
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
await Run("dotnet", ["pack", "src/Jazor.LibNode.Runtime", "-p:LibNodeRid=" + rid, "-p:JazorReleaseVersion=" + version, "-o", packages], root, "runtime-pack-verified.log");
Console.WriteLine($"{rid} candidate passed {nativeTestCount} native cases, {passed} managed tests and the actual NuGet consumer.");

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
