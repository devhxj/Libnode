#:property PublishAot=false

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var root = Directory.GetCurrentDirectory();
var operation = args.FirstOrDefault() ?? "pack";
var rid = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
string? archiveOption = null;
string? expectedHash = null;
string? releaseId = null;
var reuseLockedSource = false;
const string repository = "devhxj/Libnode";

for (var index = 1; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--archive": archiveOption = args[++index]; break;
        case "--rid": rid = args[++index]; break;
        case "--reuse-locked-source": reuseLockedSource = true; break;
        case "--sha256": expectedHash = args[++index]; break;
        case "--release-id": releaseId = args[++index]; break;
        default: throw new ArgumentException($"Unknown option: {args[index]}");
    }
}
if (operation is not ("pack" or "upload" or "download" or "import"))
    throw new ArgumentException("Use native-bundle.cs pack|upload|download|import [--archive <zip>] [--sha256 <hash>] [--release-id <id>].");
if (rid is not ("linux-x64" or "linux-arm64")) throw new ArgumentException("Select linux-x64 or linux-arm64.");
var assetName = "libnode-" + rid + "-native.zip";
var archivePath = Path.GetFullPath(archiveOption ?? "artifacts/local-build/" + assetName);
var nativeRoot = Path.Combine(root, "artifacts/native", rid);
var lockPath = Path.Combine(root, "native/libnode/source.lock.json");
var sourceLock = JsonNode.Parse(await File.ReadAllTextAsync(lockPath))!;
var source = Path.Combine(root, "artifacts/sources/node-" + sourceLock["node"]!["tag"]!.GetValue<string>());
var release = Path.Combine(source, "out/Release");
var commit = (await Run("git", ["rev-parse", "HEAD"])).Output.Trim();
var files = new[] { "libnode.so", "native-build.json", "NODE-LICENSE", "EMBEDDING-LICENSE", "NOTICE", "exports.txt", "dependencies.txt" };

if (operation == "download")
{
    RequireHash();
    if (releaseId is null || !Regex.IsMatch(releaseId, @"^[1-9]\d*$"))
        throw new ArgumentException("Pass --release-id with the numeric draft candidate release ID.");
    // Read assets from this repository's authenticated API; a draft is storage
    // for build inputs and does not publish a product release or a version tag.
    var metadata = JsonNode.Parse((await Run("gh", ["api", $"repos/{repository}/releases/{releaseId}"])).Output)!;
    var asset = metadata["assets"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == assetName)!;
    var assetId = asset["id"]!.GetValue<long>();
    Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
    var temporary = archivePath + ".download";
    try
    {
        var start = Start("gh", ["api", $"repos/{repository}/releases/assets/{assetId}", "--header", "Accept: application/octet-stream"]);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        await using (var destination = File.Create(temporary)) await process.StandardOutput.BaseStream.CopyToAsync(destination);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(await errors);
        if (await Hash(temporary) != expectedHash) throw new InvalidDataException("Native bundle SHA256 mismatch.");
        File.Move(temporary, archivePath, overwrite: true);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
    Console.WriteLine($"Downloaded verified local native bundle: {archivePath}");
    return;
}

if (operation is "pack" or "upload")
{
    // Ignore generated artifacts, but do not label uncommitted tracked inputs
    // as the source commit that Actions will check out.
    if ((await Run("git", ["status", "--porcelain", "--untracked-files=no"])).Output.Length != 0)
        throw new InvalidOperationException("Commit tracked changes before packing a native bundle.");
    var record = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(nativeRoot, "native-build.json")))!;
    var libraryHash = await Hash(Path.Combine(nativeRoot, "libnode.so"));
    if (record["sourceLockSha256"]!.GetValue<string>() != await Hash(lockPath) ||
        record["librarySha256"]!.GetValue<string>() != libraryHash ||
        record["rid"]!.GetValue<string>() != rid || record["nativeCompatibilityVerified"]?.GetValue<bool>() != true)
        throw new InvalidDataException("Local candidate must match the source lock and have completed compatibility verification.");
    var runtimeName = "libnode.so." + record["runtimeIdentity"]!["modules"]!.GetValue<string>();
    if (await Hash(Path.Combine(release, runtimeName)) != libraryHash)
        throw new InvalidDataException("Native harness library differs from the verified candidate.");
    var sources = files.ToDictionary(name => "native/" + name, name => Path.Combine(nativeRoot, name));
    sources.Add("embedtest", Path.Combine(release, "embedtest"));
    var hashes = new Dictionary<string, string>();
    foreach (var pair in sources) hashes.Add(pair.Key, await Hash(pair.Value));
    var manifest = new
    {
        schemaVersion = 1, rid, repositoryCommit = commit,
        sourceLockSha256 = await Hash(lockPath), librarySha256 = libraryHash,
        runtimeLibraryName = runtimeName, files = hashes
    };
    Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
    using (var archive = new ZipArchive(File.Create(archivePath), ZipArchiveMode.Create))
    {
        foreach (var pair in sources)
        {
            var entry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await using var input = File.OpenRead(pair.Value);
            await using var output = entry.Open();
            await input.CopyToAsync(output);
        }
        var descriptor = archive.CreateEntry("native-bundle.json", CompressionLevel.Optimal);
        descriptor.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var stream = descriptor.Open();
        await JsonSerializer.SerializeAsync(stream, manifest, new JsonSerializerOptions { WriteIndented = true });
    }
    var bundleHash = await Hash(archivePath);
    await File.WriteAllTextAsync(archivePath + ".sha256", bundleHash + "\n");
    Console.WriteLine($"Local native bundle: {archivePath}\nSHA256: {bundleHash}\nSource commit: {commit}");
    if (operation == "pack") return;

    var tag = "native-inputs-" + bundleHash[..16];
    var view = await Run("gh", ["release", "view", tag, "--repo", repository, "--json", "databaseId,isDraft"], allowFailure: true);
    if (view.ExitCode == 0 && JsonNode.Parse(view.Output)!["isDraft"]!.GetValue<bool>() != true)
        throw new InvalidOperationException("Native input storage must remain a draft release.");
    if (view.ExitCode != 0)
    {
        var notes = archivePath + ".md";
        await File.WriteAllTextAsync(notes, $"Local Linux x64 native input bundle.\n\nSource commit: `{commit}`\nLibrary SHA256: `{libraryHash}`\nArchive SHA256: `{bundleHash}`\n\nThis draft stores verified build inputs for Actions; it is not a product release.\n");
        await Run("gh", ["release", "create", tag, "--repo", repository, "--draft", "--target", commit, "--title", "Local native inputs " + bundleHash[..16], "--notes-file", notes]);
    }
    // gh's #suffix sets an asset label, not its filename.
    var uploadPath = Path.Combine(Path.GetDirectoryName(archivePath)!, "upload", assetName);
    Directory.CreateDirectory(Path.GetDirectoryName(uploadPath)!);
    File.Copy(archivePath, uploadPath, overwrite: true);
    await Run("gh", ["release", "upload", tag, "--repo", repository, uploadPath, "--clobber"]);
    var uploaded = JsonNode.Parse((await Run("gh", ["release", "view", tag, "--repo", repository, "--json", "databaseId"])).Output)!;
    Console.WriteLine($"native_release_id={uploaded["databaseId"]}\nnative_archive_sha256={bundleHash}");
    return;
}

RequireHash();
var hostRid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64) || rid != hostRid)
    throw new PlatformNotSupportedException("Import the Linux bundle on its matching x64 or ARM64 host.");
if (await Hash(archivePath) != expectedHash) throw new InvalidDataException("Native bundle SHA256 mismatch.");
using (var archive = ZipFile.OpenRead(archivePath))
{
    var descriptor = archive.GetEntry("native-bundle.json") ?? throw new InvalidDataException("Native bundle manifest is missing.");
    await using var manifestStream = descriptor.Open();
    var manifest = (await JsonNode.ParseAsync(manifestStream))!;
    if (manifest["schemaVersion"]!.GetValue<int>() != 1 || manifest["rid"]!.GetValue<string>() != rid ||
        (!reuseLockedSource && manifest["repositoryCommit"]!.GetValue<string>() != commit) || manifest["sourceLockSha256"]!.GetValue<string>() != await Hash(lockPath))
        throw new InvalidDataException("Native bundle commit, RID or source lock does not match this checkout.");
    var expectedFiles = files.Select(name => "native/" + name).Append("embedtest").Order().ToArray();
    if (!manifest["files"]!.AsObject().Select(pair => pair.Key).Order().SequenceEqual(expectedFiles) ||
        !archive.Entries.Select(entry => entry.FullName).Order().SequenceEqual(expectedFiles.Append("native-bundle.json").Order()))
        throw new InvalidDataException("Native bundle file list does not match its format.");
    foreach (var name in expectedFiles)
    {
        await using var stream = archive.GetEntry(name)!.Open();
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)) != manifest["files"]![name]!.GetValue<string>())
            throw new InvalidDataException($"Native bundle file checksum mismatch: {name}");
    }
    var runtimeName = manifest["runtimeLibraryName"]!.GetValue<string>();
    if (!Regex.IsMatch(runtimeName, @"^libnode\.so\.[1-9]\d*$")) throw new InvalidDataException("Invalid native runtime filename.");
    await using var recordStream = archive.GetEntry("native/native-build.json")!.Open();
    var record = (await JsonNode.ParseAsync(recordStream))!;
    if (record["rid"]!.GetValue<string>() != rid ||
        record["sourceLockSha256"]!.GetValue<string>() != manifest["sourceLockSha256"]!.GetValue<string>() ||
        record["librarySha256"]!.GetValue<string>() != manifest["librarySha256"]!.GetValue<string>() ||
        manifest["files"]!["native/libnode.so"]!.GetValue<string>() != manifest["librarySha256"]!.GetValue<string>() ||
        runtimeName != "libnode.so." + record["runtimeIdentity"]!["modules"]!.GetValue<string>())
        throw new InvalidDataException("Native build record differs from the bundle manifest.");
    // Download/extract/patch the locked source and fixtures only. This path never
    // invokes configure, Make, Rust or a V8 compiler; the bundled harness runs it.
    await Run("dotnet", ["run", "--file", "scripts/csharp/build-libnode.cs", "--", "--prepare-only"]);
    Directory.CreateDirectory(nativeRoot);
    Directory.CreateDirectory(release);
    foreach (var name in files)
        archive.GetEntry("native/" + name)!.ExtractToFile(Path.Combine(nativeRoot, name), overwrite: true);
    archive.GetEntry("embedtest")!.ExtractToFile(Path.Combine(release, "embedtest"), overwrite: true);
    File.Copy(Path.Combine(nativeRoot, "libnode.so"), Path.Combine(release, runtimeName), overwrite: true);
    foreach (var path in new[] { Path.Combine(nativeRoot, "libnode.so"), Path.Combine(release, runtimeName), Path.Combine(release, "embedtest") })
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    if (await Hash(Path.Combine(nativeRoot, "libnode.so")) != manifest["librarySha256"]!.GetValue<string>())
        throw new InvalidDataException("Native bundle library identity differs from its manifest.");
    var recordPath = Path.Combine(nativeRoot, "native-build.json");
    record["distributionOrigin"] = new JsonObject
    {
        ["kind"] = "local-build", ["repositoryCommit"] = manifest["repositoryCommit"]!.GetValue<string>(),
        ["verificationCommit"] = commit, ["archiveSha256"] = expectedHash
    };
    // A local result is provenance, not a substitute for testing this library on
    // the Actions runner. Existing release gates require fresh verification.
    record["localVerification"] = record["verification"]?.DeepClone();
    record.AsObject().Remove("verification");
    record["nativeCompatibilityVerified"] = false;
    await File.WriteAllTextAsync(recordPath, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine("Imported local native build. Run verify-libnode.cs and verify-upstream-issues.cs before packaging.");

void RequireHash()
{
    if (expectedHash is null || !Regex.IsMatch(expectedHash, @"^[a-f0-9]{64}$"))
        throw new ArgumentException("Pass --sha256 with the expected native bundle SHA256.");
}

static async Task<string> Hash(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}

static ProcessStartInfo Start(string executable, string[] arguments)
{
    var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return start;
}

static async Task<CommandResult> Run(string executable, string[] arguments, bool allowFailure = false)
{
    using var process = Process.Start(Start(executable, arguments))!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = await stdout;
    var errors = await stderr;
    if (!allowFailure && process.ExitCode != 0) throw new InvalidOperationException($"{executable} exited {process.ExitCode}: {output}{errors}");
    return new(process.ExitCode, output);
}

record CommandResult(int ExitCode, string Output);
