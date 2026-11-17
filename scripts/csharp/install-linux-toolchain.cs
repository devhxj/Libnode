#:property PublishAot=false

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
    throw new PlatformNotSupportedException("This installer uses Ubuntu x64 packages from archive.ubuntu.com.");
var root = Path.GetFullPath(args.Length == 0
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/opt/jazor-toolchain")
    : args[0]);
Directory.CreateDirectory(Path.Combine(root, "packages"));

// Unpack apt's dependency closure locally; no sudo or system package database changes.
var plan = await Run("apt-get", ["--print-uris", "--yes", "--download-only", "install", "g++", "make"]);
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
var packages = new List<object>();
foreach (Match match in Regex.Matches(plan, @"^'([^']+)' (\S+) (\d+) MD5Sum:([a-f0-9]+)$", RegexOptions.Multiline))
{
    var uri = new Uri(match.Groups[1].Value);
    var url = new UriBuilder(uri) { Scheme = "https", Port = -1, Host = "archive.ubuntu.com" }.Uri.AbsoluteUri;
    var file = Path.Combine(root, "packages", match.Groups[2].Value);
    Console.WriteLine($"Preparing {Path.GetFileName(file)}");
    if (!File.Exists(file))
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var target = File.Create(file);
        await response.Content.CopyToAsync(target);
    }
    await using (var input = File.OpenRead(file))
    {
        var md5 = Convert.ToHexStringLower(await MD5.HashDataAsync(input));
        if (md5 != match.Groups[4].Value) throw new InvalidDataException($"apt checksum mismatch: {file}");
    }
    await Run("dpkg-deb", ["--extract", file, root]);
    await using var hashInput = File.OpenRead(file);
    packages.Add(new { url, file = Path.GetFileName(file), sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(hashInput)) });
}
await File.WriteAllTextAsync(Path.Combine(root, "packages.json"), JsonSerializer.Serialize(packages, new JsonSerializerOptions { WriteIndented = true }));
// Ubuntu may already have cpp installed. Relocated gcc searches beside cc1plus
// for cc1, so link that installed dependency into the local toolchain as well.
foreach (var directory in Directory.EnumerateDirectories(Path.Combine(root, "usr/libexec/gcc"), "*", SearchOption.AllDirectories))
{
    if (!File.Exists(Path.Combine(directory, "cc1plus"))) continue;
    var cc1 = Path.Combine(directory, "cc1");
    var installed = "/" + Path.GetRelativePath(root, cc1);
    if (!File.Exists(cc1) && File.Exists(installed)) File.CreateSymbolicLink(cc1, installed);
}
foreach (var (name, target) in new[] { ("cc", "gcc"), ("c++", "g++") })
{
    var alias = Path.Combine(root, "usr/bin", name);
    if (!File.Exists(alias)) File.CreateSymbolicLink(alias, target);
}
Console.WriteLine(await Run(Path.Combine(root, "usr/bin/g++"), ["--version"]));
Console.WriteLine(await Run(Path.Combine(root, "usr/bin/make"), ["--version"]));
Console.WriteLine($"Toolchain installed in {root}/usr/bin. Add that directory to PATH for native builds.");

static async Task<string> Run(string executable, string[] arguments)
{
    var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {executable}.");
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var result = await output;
    var error = await errors;
    if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} exited {process.ExitCode}: {result}\n{error}");
    return result;
}
