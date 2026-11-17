#:property PublishAot=false

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
    throw new PlatformNotSupportedException("Build win-x64 on an x64 Windows host with Visual Studio C++/Clang, Python, NASM and Rust.");
var root = Directory.GetCurrentDirectory();
await Run("dotnet", ["run", "--file", "scripts/csharp/build-libnode.cs", "--", "--prepare-only"], root);
var lockPath = Path.Combine(root, "native/libnode/source.lock.json");
var manifest = JsonNode.Parse(await File.ReadAllTextAsync(lockPath))!;
var source = Path.Combine(root, "artifacts/sources/node-" + manifest["node"]!["tag"]!.GetValue<string>());
var patch = Path.Combine(root, "native/libnode/patches/windows-shared-harness.patch");
var patchHash = await Hash(patch);
if (manifest["windowsPatches"]![0]!["path"]!.GetValue<string>() != "native/libnode/patches/windows-shared-harness.patch" ||
    manifest["windowsPatches"]![0]!["sha256"]!.GetValue<string>() != patchHash)
    throw new InvalidDataException("Windows patch differs from source.lock.json.");
var marker = Path.Combine(source, ".libnode-windows-harness");
if (!File.Exists(marker))
{
    await Run("git", ["apply", "--check", patch], source);
    await Run("git", ["apply", patch], source);
    await File.WriteAllTextAsync(marker, patchHash);
}
if ((await File.ReadAllTextAsync(marker)).Trim() != patchHash)
    throw new InvalidDataException("Windows harness patch changed; prepare a fresh source tree.");
var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio/Installer/vswhere.exe");
var installation = (await Run(vswhere, ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Llvm.Clang", "-property", "installationPath"], root)).Trim();
if (string.IsNullOrWhiteSpace(installation)) throw new InvalidOperationException("Visual Studio C++ with Clang is required.");
var environmentScript = Path.Combine(root, "artifacts/windows-environment.cmd");
await File.WriteAllTextAsync(environmentScript, $"@echo off\r\ncall \"{installation}\\Common7\\Tools\\VsDevCmd.bat\" -arch=x64 -host_arch=x64 >nul\r\nif errorlevel 1 exit /b 1\r\nset\r\n");
var environmentText = await Run("cmd.exe", ["/d", "/c", environmentScript], root);
var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var line in environmentText.Split('\n'))
{
    var separator = line.IndexOf('=');
    if (separator > 0) environment[line[..separator]] = line[(separator + 1)..].TrimEnd('\r');
}
var compiler = Path.Combine(installation, "VC/Tools/Llvm/x64/bin/clang-cl.exe");
if (!File.Exists(compiler)) compiler = Path.Combine(installation, "VC/Tools/Llvm/bin/clang-cl.exe");
var compilerVersion = await Run(compiler, ["--version"], source, environment);
var rustc = await Run("rustc", ["--version"], source, environment);
var cargo = await Run("cargo", ["--version"], source, environment);
var python = await Run("python", ["--version"], source, environment);
var sourceCommit = (await Run("git", ["rev-parse", "HEAD"], root)).Trim();
// Default configuration is Release. Omitting the 'release' switch avoids
// enabling optional LTCG. Build only the shared library and embedding harness.
environment["msbuild_args"] = "/t:embedtest";
var buildArgs = new[] { "/d", "/c", "vcbuild.bat", "x64", "dll", "nonpm", "cctest", "vs2022" };
await Run("cmd.exe", buildArgs, source, environment, Path.Combine(root, "artifacts/windows-native-build.log"));
var release = Path.Combine(source, "out/Release");
var library = Path.Combine(release, "libnode.dll");
foreach (var name in new[] { "libnode.dll", "libnode.lib", "embedtest.exe" })
    if (!File.Exists(Path.Combine(release, name))) throw new FileNotFoundException("Windows build is missing " + name);
// Stage immediately so any later export/verification failure preserves the
// expensive native outputs for inspection instead of forcing another build.
var output = Path.Combine(root, "artifacts/native/win-x64");
Directory.CreateDirectory(output);
foreach (var name in new[] { "libnode.dll", "libnode.lib", "embedtest.exe" }) File.Copy(Path.Combine(release, name), Path.Combine(output, name), true);
var symbols = await Run("dumpbin.exe", ["/exports", library], source, environment);
var inventory = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "artifacts/inputs/abi-inventory.json")))!;
foreach (var import in inventory["imports"]!.AsArray())
    if (!symbols.Split('\n').Any(line => line.TrimEnd().EndsWith(" " + import!["name"]!.GetValue<string>(), StringComparison.Ordinal)))
        throw new InvalidDataException("Missing Windows embedding export: " + import!["name"]);
var dependencies = await Run("dumpbin.exe", ["/dependents", library], source, environment);
File.Copy(Path.Combine(source, "LICENSE"), Path.Combine(output, "NODE-LICENSE"), true);
File.Copy(Path.Combine(root, "native/libnode/licenses/embedding.LICENSE"), Path.Combine(output, "EMBEDDING-LICENSE"), true);
File.Copy(Path.Combine(root, "NOTICE.txt"), Path.Combine(output, "NOTICE"), true);
await File.WriteAllTextAsync(Path.Combine(output, "exports.txt"), symbols);
await File.WriteAllTextAsync(Path.Combine(output, "dependencies.txt"), dependencies);
var record = new
{
    sourceLockSha256 = await Hash(lockPath), windowsHarnessPatchSha256 = patchHash, sourceCommit,
    node = manifest["node"], nodeApi = manifest["nodeApi"], embedding = manifest["embedding"], patches = manifest["patches"],
    rid = "win-x64", compiler = compilerVersion, rustc, cargo, python, configureArgs = buildArgs,
    buildHost = RuntimeInformation.OSDescription, librarySha256 = await Hash(library),
    dependencies, nativeCompatibilityVerified = false
};
await File.WriteAllTextAsync(Path.Combine(output, "native-build.json"), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Windows shared library and native harness built; compatibility verification is required.");

static async Task<string> Hash(string path)
{
    await using var input = File.OpenRead(path);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
}

static async Task<string> Run(string executable, string[] arguments, string directory, Dictionary<string, string>? environment = null, string? log = null)
{
    if (!Path.IsPathRooted(executable) && environment?.TryGetValue("PATH", out var searchPath) == true)
        executable = searchPath.Split(Path.PathSeparator).Select(path => Path.Combine(path, executable)).FirstOrDefault(File.Exists) ?? executable;
    Console.WriteLine($"{executable} {string.Join(' ', arguments)}");
    var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
    using var process = Process.Start(start)!;
    using var writer = log is null ? null : new StreamWriter(log) { AutoFlush = true };
    var stdout = Capture(process.StandardOutput);
    var stderr = Capture(process.StandardError);
    await process.WaitForExitAsync();
    var result = await stdout + await stderr;
    if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} exited {process.ExitCode}: {log ?? result}");
    return result;

    async Task<string> Capture(StreamReader reader)
    {
        var output = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (writer is not null) { lock (writer) writer.WriteLine(line); Console.WriteLine(line); }
            else output.AppendLine(line);
        }
        return output.ToString();
    }
}
