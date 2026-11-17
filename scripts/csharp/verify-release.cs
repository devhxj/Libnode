#:property PublishAot=false

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

var root = Directory.GetCurrentDirectory();
var runRoot = Path.Combine(root, "artifacts/release-verification", Guid.NewGuid().ToString("N"));
var packages = Path.Combine(runRoot, "packages");
var fixture = Path.Combine(runRoot, "fixture");
const string version = "0.1.0-releasecheck";
const string tag = "v" + version;
Directory.CreateDirectory(fixture);
var results = new List<object>();
await Expect("local packaging requires --skip-push", root,
    ["run", "--file", "scripts/csharp/publish-nuget.cs", "--", "--package-version", version], "Local packaging requires --skip-push");
await Expect("missing native RID blocks packing", root,
    ["run", "--file", "scripts/csharp/publish-nuget.cs", "--", "--package-version", version, "--native-directory", Path.Combine(runRoot, "missing-native"), "--skip-push"], "Missing native release input");
await Success("release package closure and metapackage consumer", root,
    ["run", "--file", "scripts/csharp/publish-nuget.cs", "--", "--package-version", version, "--output-directory", packages, "--skip-push"]);

// Tags used by these tests live only in an ignored, isolated repository. No
// release tag is created in the real checkout and no command uploads anything.
foreach (var path in new[] { "global.json", "native/libnode/source.lock.json", "src/Packaging/LibNodePackages.props", "scripts/csharp/release-notes.cs" })
{
    var destination = Path.Combine(fixture, path);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.Copy(Path.Combine(root, path), destination);
}
var changelog = $$"""
    # Changelog

    ## 2026-11-17

    ### Libnode {{version}}

    中文 | English

    #### 问题修复

    - 验证原生包发布闭包。by @devhxj

    Bug Fixes

    - Verify the native release package closure. by @devhxj
    """;
await File.WriteAllTextAsync(Path.Combine(fixture, "CHANGELOG.md"), changelog);
await Git("init", "--quiet", fixture);
await Git("-C", fixture, "add", ".");
await Git("-C", fixture, "-c", "user.name=Libnode release test", "-c", "user.email=release-test@example.invalid", "commit", "--quiet", "-m", "Release fixture");
await Git("-C", fixture, "tag", tag);
var fixtureCommit = await Git("-C", fixture, "rev-parse", "HEAD");
var releaseManifestPath = Path.Combine(packages, "release-manifest.json");
var release = JsonNode.Parse(await File.ReadAllTextAsync(releaseManifestPath))!;
release["commit"] = fixtureCommit;
await File.WriteAllTextAsync(releaseManifestPath, release.ToJsonString());
var notes = Path.Combine(runRoot, "notes.md");
await Success("tag and curated notes", fixture,
    ["run", "--file", Path.Combine(root, "scripts/csharp/release-notes.cs"), "--", "--tag", tag, "--require-tag", "--output", notes]);
var uploadCheck = new[] { "run", "--file", Path.Combine(root, "scripts/csharp/publish-release.cs"), "--", "--tag", tag, "--package-directory", packages, "--notes", notes, "--verify-only" };
await Success("verified upload artifact set", fixture, uploadCheck);
await Expect("uncurated release is rejected", fixture,
    ["run", "--file", Path.Combine(root, "scripts/csharp/release-notes.cs"), "--", "--tag", "v9.9.9"], "Missing curated CHANGELOG section");
await Expect("invalid semantic tag is rejected", fixture,
    ["run", "--file", Path.Combine(root, "scripts/csharp/release-notes.cs"), "--", "--tag", "v01.0.0"], "Pass --tag vMAJOR.MINOR.PATCH");

var originalNotes = await File.ReadAllTextAsync(notes);
await File.AppendAllTextAsync(notes, "\nModified notes\n");
await Expect("changed release notes are rejected", fixture, uploadCheck, "Release notes artifact differs");
await File.WriteAllTextAsync(notes, originalNotes);
var package = Directory.GetFiles(packages, "*.nupkg").First();
var length = new FileInfo(package).Length;
await using (var altered = new FileStream(package, FileMode.Append)) altered.WriteByte(1);
await Expect("altered package hash is rejected", fixture, uploadCheck, "Release package checksum mismatch");
await using (var restored = new FileStream(package, FileMode.Open, FileAccess.Write)) restored.SetLength(length);
var unexpected = Path.Combine(packages, "Unexpected.1.0.0.nupkg");
await File.WriteAllTextAsync(unexpected, "Unexpected package");
await Expect("unexpected upload package is rejected", fixture, uploadCheck, "Unexpected package files");
File.Delete(unexpected);
var originalCommit = release["commit"]!.GetValue<string>();
release["commit"] = new string('0', 40);
await File.WriteAllTextAsync(releaseManifestPath, release.ToJsonString());
await Expect("wrong source commit is rejected", fixture, uploadCheck, "Release artifact version, commit");
release["commit"] = originalCommit;
await File.WriteAllTextAsync(releaseManifestPath, release.ToJsonString());
await Git("-C", fixture, "tag", "--delete", tag);
await Expect("missing git tag is rejected", fixture, uploadCheck, "git exited");
await Git("-C", fixture, "tag", tag);
await Success("restored artifact set still verifies", fixture, uploadCheck);

await File.WriteAllTextAsync(Path.Combine(runRoot, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Release verification passed {results.Count} checks. Evidence: {runRoot}");

async Task Success(string name, string directory, string[] arguments)
{
    var result = await Run("dotnet", directory, arguments);
    await Record(name, result.Output, result.ExitCode == 0);
}

async Task Expect(string name, string directory, string[] arguments, string error)
{
    var result = await Run("dotnet", directory, arguments);
    await Record(name, result.Output, result.ExitCode != 0 && result.Output.Contains(error, StringComparison.Ordinal));
}

async Task Record(string name, string output, bool passed)
{
    var log = (results.Count + 1).ToString("D2") + ".log";
    await File.WriteAllTextAsync(Path.Combine(runRoot, log), output);
    results.Add(new { name, passed, log });
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {name}");
    if (!passed) throw new InvalidOperationException($"Release verification failed: {name}. {output}");
}

async Task<string> Git(params string[] arguments)
{
    var result = await Run("git", root, arguments);
    if (result.ExitCode != 0) throw new InvalidOperationException(result.Output);
    return result.Output.Trim();
}

static async Task<(int ExitCode, string Output)> Run(string executable, string directory, string[] arguments)
{
    var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, await stdout + await stderr);
}
