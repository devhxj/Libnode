#:property PublishAot=false

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

string? tag = null;
var directory = Path.GetFullPath("artifacts/release/packages");
var notes = Path.GetFullPath("artifacts/release/release-notes.md");
var verifyOnly = false;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--tag": tag = args[++index]; break;
        case "--package-directory": directory = Path.GetFullPath(args[++index]); break;
        case "--notes": notes = Path.GetFullPath(args[++index]); break;
        case "--verify-only": verifyOnly = true; break;
        default: throw new ArgumentException($"Unknown option: {args[index]}");
    }
}
if (tag is null || !Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"))
    throw new ArgumentException("Pass --tag with the semantic version release tag.");
var version = tag[1..];
var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "release-manifest.json")))!;
var commit = (await Run("git", ["rev-parse", "HEAD"])).Output.Trim();
if (manifest["packageVersion"]!.GetValue<string>() != version || manifest["commit"]!.GetValue<string>() != commit ||
    manifest["sourceLockSha256"]!.GetValue<string>() != await Hash("native/libnode/source.lock.json") ||
    manifest["consumerVerified"]?.GetValue<bool>() != true)
    throw new InvalidDataException("Release artifact version, commit, source lock or consumer evidence differs from the checked-out release.");
var generatedNotes = await Run("dotnet", ["run", "--file", "scripts/csharp/release-notes.cs", "--", "--tag", tag, "--require-tag"]);
if ((await File.ReadAllTextAsync(notes)).Trim() != generatedNotes.Output.Trim())
    throw new InvalidDataException("Release notes artifact differs from the curated notes at the release tag.");
var rids = XDocument.Load("src/Packaging/LibNodePackages.props").Descendants("LibNodeRuntimePackage").Select(item => item.Attribute("Include")!.Value).ToArray();
if (!manifest["rids"]!.AsArray().Select(item => item!.GetValue<string>()).Order().SequenceEqual(rids.Order()))
    throw new InvalidDataException("Release artifact RID set differs from the package catalog.");
var records = manifest["packages"]!.AsArray();
var expectedIds = rids.Select(rid => "Jazor.LibNode." + rid).Append("Jazor.LibNode").Order().ToArray();
if (!records.Select(item => item!["id"]!.GetValue<string>()).Order().SequenceEqual(expectedIds))
    throw new InvalidDataException("Release artifact package IDs differ from the package catalog.");
var files = new List<string>();
foreach (var package in records)
{
    var id = package!["id"]!.GetValue<string>();
    var name = id + "." + version + ".nupkg";
    if (package["version"]!.GetValue<string>() != version || package["file"]!.GetValue<string>() != name)
        throw new InvalidDataException("Release package version/file does not match its ID and tag.");
    var file = Path.Combine(directory, name);
    if (await Hash(file) != package["sha256"]!.GetValue<string>())
        throw new InvalidDataException($"Release package checksum mismatch: {name}");
    files.Add(file);
}
if (!Directory.GetFiles(directory, "*.nupkg").Order().SequenceEqual(files.Order()))
    throw new InvalidDataException("Unexpected package files in release upload directory.");
Console.WriteLine($"Verified {files.Count} packages at {tag}, commit {commit}.");
if (verifyOnly) return;

var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "";
var workflow = Environment.GetEnvironmentVariable("GITHUB_WORKFLOW_REF") ?? "";
if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
    !workflow.StartsWith(repository + "/.github/workflows/nuget-publish-ref.yml@", StringComparison.Ordinal))
    throw new InvalidOperationException("Official uploads are restricted to .github/workflows/nuget-publish-ref.yml. Use --verify-only locally.");
var nugetKey = Environment.GetEnvironmentVariable("NUGET_API_KEY");
var githubToken = Environment.GetEnvironmentVariable("GH_TOKEN");
if (string.IsNullOrWhiteSpace(nugetKey) || string.IsNullOrWhiteSpace(githubToken))
    throw new InvalidOperationException("The release workflow must supply NuGet trusted-publishing authentication and its GitHub token.");
foreach (var file in files)
{
    Console.WriteLine($"Publishing {Path.GetFileName(file)} to nuget.org");
    await Run("dotnet", ["nuget", "push", file, "--api-key", nugetKey, "--source", "https://api.nuget.org/v3/index.json", "--skip-duplicate"]);
    Console.WriteLine($"Publishing {Path.GetFileName(file)} to GitHub Packages");
    await Run("dotnet", ["nuget", "push", file, "--api-key", githubToken, "--source", $"https://nuget.pkg.github.com/{repository.Split('/')[0]}/index.json", "--skip-duplicate"]);
}
var exists = await Run("gh", ["release", "view", tag, "--repo", repository], allowFailure: true);
var releaseArgs = new List<string> { "release", exists.ExitCode == 0 ? "edit" : "create", tag, "--repo", repository, "--title", tag, "--notes-file", notes };
if (version.Contains('-')) releaseArgs.AddRange(["--prerelease", "--latest=false"]);
else releaseArgs.Add("--latest");
await Run("gh", releaseArgs.ToArray());
await Run("gh", ["release", "upload", tag, "--repo", repository, .. files, Path.Combine(directory, "release-manifest.json"), "--clobber"]);
Console.WriteLine($"Published {tag} to NuGet, GitHub Packages and GitHub Releases.");

static async Task<string> Hash(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}

static async Task<(int ExitCode, string Output)> Run(string executable, string[] arguments, bool allowFailure = false)
{
    // Do not log command arguments: NuGet receives short-lived credentials here.
    var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var result = await stdout + await stderr;
    if (!allowFailure && process.ExitCode != 0) throw new InvalidOperationException($"{executable} exited {process.ExitCode}: {result}");
    return (process.ExitCode, result);
}
