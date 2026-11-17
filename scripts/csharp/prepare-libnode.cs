#:property PublishAot=false

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

var root = Directory.GetCurrentDirectory();
var downloadNode = false;
var offline = false;
foreach (var argument in args)
{
    switch (argument)
    {
        case "--download-node": downloadNode = true; break;
        case "--offline": offline = true; break;
        default: throw new ArgumentException($"Unknown argument: {argument}");
    }
}
var lockPath = Path.Combine(root, "native/libnode/source.lock.json");
var lockBytes = await File.ReadAllBytesAsync(lockPath);
using var source = JsonDocument.Parse(lockBytes);
var manifest = source.RootElement;
foreach (var patch in manifest.GetProperty("patches").EnumerateArray())
    await CheckHash(Path.Combine(root, patch.GetProperty("path").GetString()!), patch.GetProperty("sha256").GetString()!);
var testAdaptation = manifest.GetProperty("testAdaptation");
await CheckHash(Path.Combine(root, testAdaptation.GetProperty("path").GetString()!), testAdaptation.GetProperty("sha256").GetString()!);
var output = Path.Combine(root, "artifacts/inputs");
Directory.CreateDirectory(output);
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };

foreach (var file in manifest.GetProperty("files").EnumerateArray())
    await Fetch(file.GetProperty("url").GetString()!, file.GetProperty("sha256").GetString()!, file.GetProperty("path").GetString()!);
var embedding = manifest.GetProperty("embedding");
await Fetch(embedding.GetProperty("diff").GetString()!, embedding.GetProperty("diffSha256").GetString()!, "embedding/upstream.diff");
var nodeApi = manifest.GetProperty("nodeApi");
var props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
if (props.Descendants("NodeApiVersion").Single().Value != nodeApi.GetProperty("version").GetString())
    throw new InvalidDataException("NodeApiVersion differs from the locked managed package.");
await Fetch(nodeApi.GetProperty("archive").GetString()!, nodeApi.GetProperty("sha256").GetString()!, "node-api-dotnet.tar.gz");
if (downloadNode)
{
    var node = manifest.GetProperty("node");
    await Fetch(node.GetProperty("archive").GetString()!, node.GetProperty("sha256").GetString()!, $"node-{node.GetProperty("tag").GetString()}.tar.xz");
}

var managed = await File.ReadAllTextAsync(Path.Combine(output, "managed/NodejsRuntime.Embedding.cs"));
var header = await File.ReadAllTextAsync(Path.Combine(output, "embedding/src/node_embedding_api.h"));
var imports = Regex.Matches(managed, @"private\s+delegate\*\s+unmanaged\[Cdecl\]<([^;]+)>\s+(node_embedding_\w+)\s*;")
    .Select(match => new { name = match.Groups[2].Value, signature = Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim() })
    .OrderBy(import => import.name, StringComparer.Ordinal).ToArray();
if (imports.Length == 0) throw new InvalidDataException("No managed embedding imports discovered.");
foreach (var import in imports)
    if (!Regex.IsMatch(header, $@"\b{Regex.Escape(import.name)}\s*\("))
        throw new InvalidDataException($"Native header does not declare {import.name}.");
if (!Regex.IsMatch(header, $@"#define\s+NODE_EMBEDDING_VERSION\s+{embedding.GetProperty("apiVersion").GetInt32()}\b"))
    throw new InvalidDataException("Unexpected embedding API version.");

var report = new
{
    sourceLockSha256 = Convert.ToHexStringLower(SHA256.HashData(lockBytes)),
    nodeTag = manifest.GetProperty("node").GetProperty("tag").GetString(),
    nodeApiVersion = nodeApi.GetProperty("version").GetString(),
    imports,
    sourceDeclarationsPresent = true,
    nativeCompatibilityVerified = false,
    pending = new[] { "Native port/build", "ABI widths, enums, callbacks and ownership review", "Reused C and managed tests", "Actual NuGet consumer", "RID/platform migration gates" }
};
await File.WriteAllTextAsync(Path.Combine(output, "abi-inventory.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Verified locked inputs and {imports.Length} managed import declarations. Inventory: {output}/abi-inventory.json");

async Task Fetch(string url, string expectedHash, string relativePath)
{
    var destination = Path.Combine(output, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    if (File.Exists(destination))
    {
        await CheckHash(destination, expectedHash);
        Console.WriteLine($"Verified {relativePath}");
        return;
    }
    if (offline) throw new FileNotFoundException($"Offline input missing: {relativePath}", destination);
    var temporary = destination + ".download";
    try
    {
        Console.WriteLine($"Downloading {relativePath}");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using (var stream = File.Create(temporary)) await response.Content.CopyToAsync(stream);
        await CheckHash(temporary, expectedHash);
        File.Move(temporary, destination);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
}

static async Task CheckHash(string path, string expected)
{
    await using var input = File.OpenRead(path);
    var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
        throw new InvalidDataException($"SHA256 mismatch for {path}: expected {expected}, got {actual}.");
}
