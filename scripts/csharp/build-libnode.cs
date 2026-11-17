#:property PublishAot=false

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var root = Directory.GetCurrentDirectory();
var jobs = Math.Min(Environment.ProcessorCount, 8);
var prepareOnly = false;
var inputsPrepared = false;
var importCachedBuild = false;
var toolchain = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/opt/jazor-toolchain/usr/bin");
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--jobs": jobs = int.Parse(args[++index]); break;
        case "--toolchain": toolchain = Path.GetFullPath(args[++index]); break;
        case "--prepare-only": prepareOnly = true; break;
        case "--inputs-prepared": inputsPrepared = true; break;
        case "--import-cached-build": importCachedBuild = true; break;
        default: throw new ArgumentException($"Unknown argument: {args[index]}");
    }
}
if (jobs < 1) throw new ArgumentOutOfRangeException(nameof(jobs));
if ((!OperatingSystem.IsLinux() && !(prepareOnly && OperatingSystem.IsWindows())) ||
    RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
    throw new PlatformNotSupportedException("Build Linux natively on x64 or ARM64; source preparation also supports Windows.");
var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
var rid = "linux-" + architecture;
if (!inputsPrepared)
    await Run("dotnet", ["run", "--file", "scripts/csharp/prepare-libnode.cs", "--", "--download-node"], root);
else
{
    var preparedInventory = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "artifacts/inputs/abi-inventory.json")))!;
    await using var sourceLockInput = File.OpenRead(Path.Combine(root, "native/libnode/source.lock.json"));
    if (preparedInventory["sourceLockSha256"]!.GetValue<string>() != Convert.ToHexStringLower(await SHA256.HashDataAsync(sourceLockInput)))
        throw new InvalidDataException("Prepared source input inventory differs from source.lock.json.");
}
var lockBytes = await File.ReadAllBytesAsync(Path.Combine(root, "native/libnode/source.lock.json"));
var lockHash = Convert.ToHexStringLower(SHA256.HashData(lockBytes));
var manifest = JsonNode.Parse(lockBytes)!;
var tag = manifest["node"]!["tag"]!.GetValue<string>();
var sourceRoot = Path.Combine(root, "artifacts/sources");
var source = Path.Combine(sourceRoot, "node-" + tag);
var marker = Path.Combine(source, ".jazor-source-lock");
Directory.CreateDirectory(sourceRoot);
if (!Directory.Exists(source))
{
    var archive = Path.Combine(root, $"artifacts/inputs/node-{tag}.tar.xz");
    if (OperatingSystem.IsWindows())
        await Run("python", ["-c", "import sys,tarfile; t=tarfile.open(sys.argv[1]); print('Extracting',len(t.getmembers()),'source entries',flush=True); t.extractall(sys.argv[2],filter='data'); print('Source extraction complete',flush=True)", archive, sourceRoot], root);
    else
        await Run("tar", ["-xJf", archive, "-C", sourceRoot], root);
    // Keep git apply rooted in the extracted source, even inside this repository.
    await Run("git", ["init", "--quiet"], source);
    foreach (var patch in manifest["patches"]!.AsArray())
        await Run("git", ["apply", "--whitespace=nowarn", Path.Combine(root, patch!["path"]!.GetValue<string>())], source);
    await File.WriteAllTextAsync(marker, lockHash);
}
if (!File.Exists(marker) || (await File.ReadAllTextAsync(marker)).Trim() != lockHash)
    throw new InvalidOperationException($"Source inputs changed or preparation was interrupted. Move {source} aside and rerun to prepare a fresh source tree.");
if (prepareOnly) return;
if (importCachedBuild)
{
    // Use the original run's artifact metadata together with its exact out/
    // cache. This also bootstraps reusable ZIPs for the first ARM candidate run.
    var staged = Path.Combine(root, "artifacts/native", rid);
    var cachedRelease = Path.Combine(source, "out/Release");
    var recordPath = Path.Combine(staged, "native-build.json");
    var cachedRecord = JsonNode.Parse(await File.ReadAllTextAsync(recordPath))!;
    var cachedLibrary = Directory.GetFiles(cachedRelease, "libnode.so.*").Single();
    await using var cachedInput = File.OpenRead(cachedLibrary);
    var cachedHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(cachedInput));
    await using var stagedInput = File.OpenRead(Path.Combine(staged, "libnode.so"));
    if (cachedRecord["rid"]!.GetValue<string>() != rid || cachedRecord["sourceLockSha256"]!.GetValue<string>() != lockHash ||
        cachedRecord["librarySha256"]!.GetValue<string>() != cachedHash ||
        Convert.ToHexStringLower(await SHA256.HashDataAsync(stagedInput)) != cachedHash ||
        !File.Exists(Path.Combine(cachedRelease, "embedtest")))
        throw new InvalidDataException("Cached library/harness and candidate artifact do not match this host and source lock.");
    cachedRecord["previousVerification"] = cachedRecord["verification"]?.DeepClone();
    cachedRecord.AsObject().Remove("verification");
    cachedRecord["nativeCompatibilityVerified"] = false;
    await File.WriteAllTextAsync(recordPath, cachedRecord.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Imported matching cached native build. Fresh verification is required; no compiler invoked.");
    return;
}

var environment = new Dictionary<string, string>();
foreach (var name in new[] { "CC", "CXX" })
{
    if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
        environment[name] = value;
}
if (Directory.Exists(toolchain))
{
    environment["PATH"] = toolchain + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
    environment.TryAdd("CC", Path.Combine(toolchain, "gcc"));
    environment.TryAdd("CXX", Path.Combine(toolchain, "g++"));
}
// Node 26 uses Rust for Temporal. Keep feature parity with the pinned source.
var cargoBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo/bin");
if (Directory.Exists(cargoBin)) environment["PATH"] = cargoBin + Path.PathSeparator + environment.GetValueOrDefault("PATH", Environment.GetEnvironmentVariable("PATH")!);
var cargo = await Run("cargo", ["--version"], source, environment);
var rustc = await Run("rustc", ["--version"], source, environment);
var python = await Run("python3", ["--version"], source, environment);
var compiler = await Run(environment.GetValueOrDefault("CXX", "g++"), ["--version"], source, environment);
var make = await Run("make", ["--version"], source, environment);
var configureArgs = new[] { "configure.py", "--shared", "--without-npm" };
await Run("python3", configureArgs, source, environment);
await Run("make", [$"-j{jobs}"], source, environment, Path.Combine(root, "artifacts/native-build.log"));
// The upstream default build includes embedtest. A second Make invocation
// regenerates snapshot intermediates unnecessarily on this Node revision.
if (!File.Exists(Path.Combine(source, "out/Release/embedtest")))
    throw new FileNotFoundException("The upstream build did not produce the embedding test harness.");

var release = Path.Combine(source, "out/Release");
var candidates = Directory.GetFiles(release, "libnode.so.*");
if (candidates.Length != 1) throw new InvalidOperationException($"Expected one versioned shared library, found {candidates.Length}.");
var candidate = candidates[0];
await Run("dotnet", ["run", "--file", Path.Combine(root, "scripts/csharp/verify-native-architecture.cs"), "--", "--rid", rid, "--library", candidate], root);
var symbols = await Run("nm", ["-D", "--defined-only", candidate], source);
var inventory = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "artifacts/inputs/abi-inventory.json")))!;
foreach (var import in inventory["imports"]!.AsArray())
{
    var name = import!["name"]!.GetValue<string>();
    if (!symbols.Split('\n').Any(line => line.EndsWith(" " + name, StringComparison.Ordinal)))
        throw new InvalidDataException($"Missing native export: {name}");
}
var dependencies = await Run("ldd", [candidate], source);
if (dependencies.Contains("not found", StringComparison.Ordinal)) throw new InvalidDataException(dependencies);
var dynamicSection = await Run("readelf", ["-d", candidate], source);
var symbolVersions = await Run("readelf", ["--version-info", candidate], source);
var requiredSymbolVersions = new Dictionary<string, string>();
foreach (var prefix in new[] { "GLIBC_", "GLIBCXX_", "CXXABI_" })
{
    var versions = Regex.Matches(symbolVersions, Regex.Escape(prefix) + @"([0-9]+(?:\.[0-9]+)+)")
        .Select(match => Version.Parse(match.Groups[1].Value)).ToArray();
    if (versions.Length != 0) requiredSymbolVersions[prefix.TrimEnd('_')] = versions.Max()!.ToString();
}
var output = Path.Combine(root, "artifacts/native", rid);
Directory.CreateDirectory(output);
File.Copy(candidate, Path.Combine(output, "libnode.so"), true);
File.Copy(Path.Combine(source, "LICENSE"), Path.Combine(output, "NODE-LICENSE"), true);
File.Copy(Path.Combine(root, "native/libnode/licenses/embedding.LICENSE"), Path.Combine(output, "EMBEDDING-LICENSE"), true);
File.Copy(Path.Combine(root, "NOTICE.txt"), Path.Combine(output, "NOTICE"), true);
await File.WriteAllTextAsync(Path.Combine(output, "exports.txt"), symbols);
await File.WriteAllTextAsync(Path.Combine(output, "dependencies.txt"), dependencies + "\n" + dynamicSection + "\n" + symbolVersions);
await using var libraryStream = File.OpenRead(candidate);
var record = new
{
    sourceLockSha256 = lockHash,
    node = manifest["node"], nodeApi = manifest["nodeApi"], embedding = manifest["embedding"], patches = manifest["patches"],
    rid, compiler, make, cargo, rustc, python, configureArgs, jobs,
    buildHost = RuntimeInformation.OSDescription,
    librarySha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(libraryStream)),
    dependencies, dynamicSection, symbolVersions, requiredSymbolVersions,
    nativeCompatibilityVerified = false
};
await File.WriteAllTextAsync(Path.Combine(output, "native-build.json"), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Native candidate staged at {output}. Compatibility and package consumer tests are still required.");

static async Task<string> Run(string executable, string[] arguments, string directory,
    Dictionary<string, string>? environment = null, string? log = null)
{
    Console.WriteLine($"{executable} {string.Join(' ', arguments)}");
    if (!Path.IsPathRooted(executable) && environment?.TryGetValue("PATH", out var searchPath) == true)
        executable = searchPath.Split(Path.PathSeparator).Select(path => Path.Combine(path, executable)).FirstOrDefault(File.Exists) ?? executable;
    var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {executable}");
    using var writer = log is null ? null : new StreamWriter(log) { AutoFlush = true };
    var outputTask = Capture(process.StandardOutput);
    var errorsTask = Capture(process.StandardError);
    await process.WaitForExitAsync();
    var output = await outputTask;
    var errors = await errorsTask;
    if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} exited {process.ExitCode}. {log ?? output + errors}");
    return output + errors;

    async Task<string> Capture(StreamReader reader)
    {
        var text = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (writer is not null) { lock (writer) writer.WriteLine(line); }
            else { text.AppendLine(line); Console.WriteLine(line); }
        }
        return text.ToString();
    }
}
