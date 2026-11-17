#:property PublishAot=false

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
    throw new PlatformNotSupportedException("Import and verify the Windows candidate on Windows x64.");
var root = Directory.GetCurrentDirectory();
var directory = Path.Combine(root, "artifacts/native/win-x64");
var recordPath = Path.Combine(directory, "native-build.json");
var nativeRecord = JsonNode.Parse(await File.ReadAllTextAsync(recordPath))!;
var sourceLockPath = Path.Combine(root, "native/libnode/source.lock.json");
var sourceLock = JsonNode.Parse(await File.ReadAllTextAsync(sourceLockPath))!;
var patch = Path.Combine(root, "native/libnode/patches/windows-shared-harness.patch");
var pointerPatch = Path.Combine(root, "native/libnode/patches/windows-pointer-exchange.patch");
if (nativeRecord["rid"]!.GetValue<string>() != "win-x64" ||
    nativeRecord["librarySha256"]!.GetValue<string>() != await Hash(Path.Combine(directory, "libnode.dll")) ||
    nativeRecord["sourceLockSha256"]!.GetValue<string>() != await Hash(sourceLockPath) ||
    nativeRecord["windowsHarnessPatchSha256"]!.GetValue<string>() != await Hash(patch) ||
    nativeRecord["windowsPointerPatchSha256"]?.GetValue<string>() != await Hash(pointerPatch))
    throw new InvalidDataException("Windows artifact identity or source lock does not match this checkout.");
await Run("dotnet", ["run", "--file", "scripts/csharp/build-libnode.cs", "--", "--prepare-only"], root);
var source = Path.Combine(root, "artifacts/sources/node-" + sourceLock["node"]!["tag"]!.GetValue<string>());
var marker = Path.Combine(source, ".libnode-windows-harness");
if (!File.Exists(marker))
{
    await Run("git", ["apply", "--check", patch], source);
    await Run("git", ["apply", patch], source);
    await File.WriteAllTextAsync(marker, await Hash(patch));
}
if ((await File.ReadAllTextAsync(marker)).Trim() != await Hash(patch))
    throw new InvalidDataException("Windows source harness differs from its locked patch.");
var pointerMarker = Path.Combine(source, ".libnode-windows-pointer-exchange");
if (!File.Exists(pointerMarker))
{
    await Run("git", ["apply", "--check", pointerPatch], source);
    await Run("git", ["apply", pointerPatch], source);
    await File.WriteAllTextAsync(pointerMarker, await Hash(pointerPatch));
}
if ((await File.ReadAllTextAsync(pointerMarker)).Trim() != await Hash(pointerPatch))
    throw new InvalidDataException("Windows pointer patch differs from its source record.");
var release = Path.Combine(source, "out/Release");
Directory.CreateDirectory(release);
foreach (var name in new[] { "libnode.dll", "libnode.lib", "embedtest.exe" })
    File.Copy(Path.Combine(directory, name), Path.Combine(release, name), true);
nativeRecord["previousVerification"] = nativeRecord["verification"]?.DeepClone();
nativeRecord.AsObject().Remove("verification");
nativeRecord["nativeCompatibilityVerified"] = false;
await File.WriteAllTextAsync(recordPath, nativeRecord.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Imported Windows build without compiling V8. Fresh compatibility verification is required.");

static async Task<string> Hash(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}

static async Task Run(string executable, string[] arguments, string directory)
{
    var start = new ProcessStartInfo(executable) { WorkingDirectory = directory };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} exited {process.ExitCode}.");
}
