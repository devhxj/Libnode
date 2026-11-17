#:property PublishAot=false

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

string? tag = null;
var changelog = "CHANGELOG.md";
string? output = null;
string? githubOutput = null;
var requireTag = false;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--tag": tag = args[++index]; break;
        case "--changelog": changelog = args[++index]; break;
        case "--output": output = args[++index]; break;
        case "--github-output": githubOutput = args[++index]; break;
        case "--require-tag": requireTag = true; break;
        default: throw new ArgumentException($"Unknown option: {args[index]}");
    }
}
if (tag is null || !Regex.IsMatch(tag, @"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?$"))
    throw new ArgumentException("Pass --tag vMAJOR.MINOR.PATCH with optional SemVer prerelease identifiers.");
var version = tag[1..];
var heading = "### Libnode " + version;
var lines = await File.ReadAllLinesAsync(changelog);
var start = Array.FindIndex(lines, line => line.Trim() == heading);
if (start < 0) throw new InvalidDataException($"Missing curated CHANGELOG section: {heading}");
var dateLine = lines.Take(start).LastOrDefault(line => line.StartsWith("## ", StringComparison.Ordinal));
if (dateLine is null || !DateOnly.TryParseExact(dateLine[3..].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
    throw new InvalidDataException("Release notes must be under a dated ## YYYY-MM-DD heading.");
var body = lines.Skip(start + 1).TakeWhile(line => !line.StartsWith("### ", StringComparison.Ordinal) && !line.StartsWith("## ", StringComparison.Ordinal)).ToArray();
var entries = body.Where(line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal)).ToArray();
if (entries.Length == 0 || entries.Any(line => !Regex.IsMatch(line, @"by @[A-Za-z0-9-]+")))
    throw new InvalidDataException("Release notes must contain nonempty, attributed user-facing entries.");
if (!body.Contains("中文 | English")) throw new InvalidDataException("Release notes must include the bilingual 中文 | English sections.");
var sections = new[] { ("#### 新增功能", "New Features"), ("#### 体验优化", "Improvements"), ("#### 问题修复", "Bug Fixes"), ("#### 其他变更", "Chores") };
var categorizedEntries = 0;
foreach (var (chinese, english) in sections)
{
    var chineseCount = CountEntries(chinese);
    var englishCount = CountEntries(english);
    if (chineseCount != englishCount) throw new InvalidDataException($"Release note entries differ: {chinese} / {english}");
    categorizedEntries += chineseCount + englishCount;
}
if (categorizedEntries != entries.Length) throw new InvalidDataException("Every release note entry must belong to a paired release category.");
var commit = await Git("rev-parse", "HEAD");
if (requireTag)
{
    var tagCommit = await Git("rev-parse", "--verify", "refs/tags/" + tag + "^{commit}");
    if (tagCommit != commit) throw new InvalidDataException("Release tag must resolve to the checked-out commit.");
}
var notes = "## Libnode " + version + "\n\n" + string.Join('\n', body).Trim() + "\n";
if (output is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    await File.WriteAllTextAsync(output, notes);
}
if (githubOutput is not null)
    await File.AppendAllTextAsync(githubOutput, $"package_version={version}\nrelease_tag={tag}\ncommit={commit}\nis_prerelease={version.Contains('-').ToString().ToLowerInvariant()}\n");
Console.Write(notes);

int CountEntries(string section)
{
    var position = Array.IndexOf(body, section);
    return position < 0 ? 0 : body.Skip(position + 1)
        .TakeWhile(line => !sections.Any(pair => line == pair.Item1 || line == pair.Item2))
        .Count(line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal));
}

static async Task<string> Git(params string[] arguments)
{
    var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException($"git exited {process.ExitCode}: {await error}");
    return (await output).Trim();
}
