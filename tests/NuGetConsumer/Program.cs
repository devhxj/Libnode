using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.JavaScript.NodeApi;
using Microsoft.JavaScript.NodeApi.Runtime;

if (args.Length != 2) throw new ArgumentException("Usage: NuGetConsumer <expected-library-sha256> <identity-report.json>");
// Resolve through NodeApi's NuGet/runtime resolver and check the exact loaded
// binary. The platform then uses its normal default resolution path.
var handle = NativeLibrary.Load("libnode", typeof(NodeEmbeddingPlatform).Assembly, null);
var mapping = OperatingSystem.IsWindows()
    ? Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(module => string.Equals(module.ModuleName, "libnode.dll", StringComparison.OrdinalIgnoreCase)).FileName
    : File.ReadLines("/proc/self/maps")
    .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last())
    .First(path => Path.GetFileName(path) == "libnode.so");
var libraryPath = Path.GetFullPath(mapping);
await using var input = File.OpenRead(libraryPath);
var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
if (hash != args[0]) throw new InvalidOperationException($"Unexpected loaded library: {libraryPath} ({hash})");

using var platform = new NodeEmbeddingPlatform(new NodeEmbeddingPlatformSettings());
string? runtimeIdentity = null;
string? node26Report = null;
var suitePath = Path.Combine(AppContext.BaseDirectory, "node26-tests.cjs");
var suiteBytes = await File.ReadAllBytesAsync(suitePath);
var suiteHash = Convert.ToHexStringLower(SHA256.HashData(suiteBytes));
for (var iteration = 0; iteration < 2; iteration++)
{
    using var runtime = platform.CreateThreadRuntime(AppContext.BaseDirectory, new NodeEmbeddingRuntimeSettings
    {
        MainScript = "globalThis.require = require('node:module').createRequire(process.execPath);"
    });
    runtime.Run(() =>
    {
        runtimeIdentity = (string)JSValue.RunScript("""
            JSON.stringify({node: process.version, v8: process.versions.v8,
                napi: process.versions.napi, modules: process.versions.modules,
                pid: process.pid, temporal: typeof Temporal})
            """);
        var result = (int)JSValue.RunScript("21 * 2");
        if (result != 42) throw new InvalidOperationException($"Unexpected JS result: {result}");
        var pathModule = runtime.Import("node:path");
        if ((string)pathModule.CallMethod("join", "a", "b") != Path.Combine("a", "b"))
            throw new InvalidOperationException("Built-in import returned an unexpected result.");
    });
    await runtime.RunAsync(async () =>
    {
        var module = await runtime.ImportAsync("node:path", esModule: true);
        if ((string)module.CallMethod("join", "a", "b") != Path.Combine("a", "b"))
            throw new InvalidOperationException("Async ESM import returned an unexpected result.");
    });
    if (iteration == 0)
    {
        await runtime.RunAsync(async () =>
        {
            var promise = (JSPromise)runtime.Import(suitePath);
            node26Report = (string)await promise.AsTask();
        }).WaitAsync(TimeSpan.FromMinutes(2));
    }
    var propagated = false;
    try { runtime.Run(() => JSValue.RunScript("throw new Error('candidate-error')")); }
    catch (JSException error) when (error.Message.Contains("candidate-error", StringComparison.Ordinal)) { propagated = true; }
    if (!propagated) throw new InvalidOperationException("JS error did not propagate through the NuGet API.");
    runtime.Dispose();
    if (runtime.ExitCode != 0) throw new InvalidOperationException($"Runtime exited {runtime.ExitCode}");
}
using var identity = JsonDocument.Parse(runtimeIdentity!);
using var node26 = JsonDocument.Parse(node26Report!);
var expectedArchitecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
if (node26.RootElement.GetProperty("arch").GetString() != expectedArchitecture ||
    node26.RootElement.GetProperty("total").GetInt32() != 30 || node26.RootElement.GetProperty("passed").GetInt32() != 30)
    throw new InvalidOperationException("Node 26 runtime feature suite failed: " + node26Report);
if (identity.RootElement.GetProperty("pid").GetInt32() != Environment.ProcessId)
    throw new InvalidOperationException("JS did not execute in the managed process.");
if (identity.RootElement.GetProperty("node").GetString() != "v26.13.1")
    throw new InvalidOperationException("Unexpected Node version.");
var report = new
{
    libraryPath, librarySha256 = hash,
    managedAssembly = typeof(NodeEmbeddingPlatform).Assembly.Location,
    runtime = identity.RootElement.Clone(),
    node26SuiteSha256 = suiteHash,
    node26 = node26.RootElement.Clone(),
    rid = RuntimeInformation.RuntimeIdentifier,
    processId = Environment.ProcessId,
    sequentialEnvironments = 2,
    checkedApis = new[] { "Script evaluation", "CommonJS builtin import", "Async ESM import", "JS error propagation", "Environment recreation" }
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(args[1], json);
Console.WriteLine(json);
GC.KeepAlive(handle);
