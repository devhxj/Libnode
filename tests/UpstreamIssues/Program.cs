using System.Diagnostics;
using System.Text.Json;
using Microsoft.JavaScript.NodeApi;
using Microsoft.JavaScript.NodeApi.Runtime;
using static Microsoft.JavaScript.NodeApi.Runtime.NodejsRuntime;

if (args.Length < 2) throw new ArgumentException("Usage: UpstreamIssues <case> <libnode.so> [addon.node]");
var scenario = args[0];
using var platform = new NodeEmbeddingPlatform(new NodeEmbeddingPlatformSettings
{
    LibNodePath = Path.GetFullPath(args[1])
});
const string mainScript = "globalThis.require = require('node:module').createRequire(process.execPath);";
NodeEmbeddingRuntimeSettings Settings(bool concurrent = false) => new()
{
    MainScript = mainScript,
    RuntimeFlags = concurrent
        ? NodeEmbeddingRuntimeFlags.DefaultFlags | NodeEmbeddingRuntimeFlags.NoCreateInspector | NodeEmbeddingRuntimeFlags.NoStartDebugSignalHandler
        : NodeEmbeddingRuntimeFlags.DefaultFlags
};

switch (scenario)
{
    case "concurrent-default":
    case "concurrent-configured":
    {
        var settings = Settings(scenario == "concurrent-configured");
        using var first = platform.CreateThreadRuntime(AppContext.BaseDirectory, settings);
        using var second = platform.CreateThreadRuntime(AppContext.BaseDirectory, settings);
        await Task.WhenAll(first.RunAsync(async () =>
        {
            await Task.Yield();
            if ((int)JSValue.RunScript("21 * 2") != 42) throw new InvalidOperationException();
        }), second.RunAsync(async () =>
        {
            await Task.Yield();
            if ((int)JSValue.RunScript("21 * 2") != 42) throw new InvalidOperationException();
        }));
        break;
    }
    case "timer-dispose":
    case "timer-clear":
    {
        using var runtime = platform.CreateThreadRuntime(AppContext.BaseDirectory, Settings());
        runtime.Run(() => JSValue.RunScript("globalThis.timer = setInterval(() => {}, 1000)"));
        if (scenario == "timer-clear") runtime.Run(() => JSValue.RunScript("clearInterval(globalThis.timer)"));
        break;
    }
    case "reference":
    {
        using var runtime = platform.CreateThreadRuntime(AppContext.BaseDirectory, Settings());
        JSReference reference = null!;
        runtime.Run(() => reference = new JSReference(JSValue.RunScript("({answer: () => 42})")));
        runtime.Run(() =>
        {
            using (reference)
                if ((int)reference.GetValue().CallMethod("answer") != 42) throw new InvalidOperationException();
        });
        break;
    }
    case "linked-module":
    case "linked-main":
    {
        var settings = Settings();
        settings.Modules = [new NodeEmbeddingModuleInfo
        {
            Name = "issue464", NodeApiVersion = NodeEmbedding.NodeApiVersion,
            OnInitialize = (_, _, exports) =>
            {
                exports["add"] = JSValue.CreateFunction("add", a => (int)a[0] + (int)a[1]);
                return exports;
            }
        }];
        if (scenario == "linked-main") settings.MainScript += "globalThis.answer = process._linkedBinding('issue464').add(21,21);";
        using var runtime = platform.CreateThreadRuntime(AppContext.BaseDirectory, settings);
        runtime.Run(() =>
        {
            var script = scenario == "linked-main" ? "globalThis.answer" : "process._linkedBinding('issue464').add(21,21)";
            if ((int)JSValue.RunScript(script) != 42) throw new InvalidOperationException();
        });
        break;
    }
    case "typed-error":
    {
        using var runtime = platform.CreateThreadRuntime(AppContext.BaseDirectory, Settings());
        runtime.Run(() =>
        {
            JSValue.Global.SetProperty("typed", JSValue.CreateFunction("typed", _ =>
            {
                JSError.ThrowTypeError("test");
                return JSValue.Undefined;
            }));
            var name = (string)JSValue.RunScript("(() => {try {typed()} catch(e) {return e.name}})()");
            Console.WriteLine(JsonSerializer.Serialize(new { observed = name, expected = "TypeError", layer = "managed" }));
            if (name != "TypeError") Environment.ExitCode = 2;
        });
        break;
    }
    case "addon":
    case "addon-typed-error":
    case "rust-addon":
    {
        using var runtime = platform.CreateThreadRuntime(AppContext.BaseDirectory, Settings());
        runtime.Run(() =>
        {
            var addon = runtime.Import(Path.GetFullPath(args[2]));
            if (scenario == "rust-addon")
            {
                JSValue.Global.SetProperty("rustAddon", addon);
                var result = (string)JSValue.RunScript("""
                    (() => {
                        const result = rustAddon.minifySync('probe.js', 'globalThis.result = 21 * 2;', {});
                        if (result.errors.length) throw new Error(JSON.stringify(result.errors));
                        (0, eval)(result.code);
                        if (globalThis.result !== 42) throw new Error('minified result');
                        return result.code;
                    })()
                    """);
                Console.WriteLine(result);
                return;
            }
            if ((int)addon["answer"] != 42) throw new InvalidOperationException();
            JSValue.Global.SetProperty("issueAddon", addon);
            if (scenario == "addon-typed-error" && !(bool)JSValue.RunScript("""
                (() => {try {issueAddon.throwTypeError()} catch(e) {
                    return e instanceof TypeError && e.name === 'TypeError' && e.code === 'ISSUE509';
                }})()
                """)) throw new InvalidOperationException("Native typed error lost its type.");
        });
        break;
    }
    case "no-wait":
    {
        using var runtime = NodeEmbeddingRuntime.Create(platform, Settings());
        using var scope = new NodeEmbeddingNodeApiScope(runtime);
        JSValue.RunScript("globalThis.timer = setTimeout(() => {}, 3000)");
        var timer = Stopwatch.StartNew();
        runtime.RunEventLoopNoWait();
        timer.Stop();
        JSValue.RunScript("clearTimeout(globalThis.timer)");
        Console.WriteLine(JsonSerializer.Serialize(new { elapsedMs = timer.ElapsedMilliseconds }));
        if (timer.ElapsedMilliseconds >= 1000) throw new InvalidOperationException("NoWait blocked for the pending timer.");
        break;
    }
    case "timer-terminate":
    {
        using var runtime = NodeEmbeddingRuntime.Create(platform, Settings());
        using var scope = new NodeEmbeddingNodeApiScope(runtime);
        JSValue.RunScript("setInterval(() => {}, 1000)");
        runtime.TerminateEventLoop();
        break;
    }
    case "workers":
    {
        using var runtime = platform.CreateThreadRuntime(AppContext.BaseDirectory, Settings());
        await runtime.RunAsync(async () =>
        {
            var promise = (JSPromise)JSValue.RunScript("""
                (async () => {
                    const {Worker} = require('node:worker_threads');
                    for (let i = 0; i < 20; i++) {
                        const worker = new Worker("require('node:worker_threads').parentPort.postMessage(42); setInterval(() => {}, 1000)", {eval: true});
                        const answer = await new Promise((resolve, reject) => {
                            worker.once('message', resolve); worker.once('error', reject);
                        });
                        if (answer !== 42) throw new Error('worker result');
                        await worker.terminate();
                    }
                    return 42;
                })()
                """);
            if ((int)await promise.AsTask() != 42) throw new InvalidOperationException();
        });
        break;
    }
    default: throw new ArgumentException($"Unknown case {scenario}");
}
Console.WriteLine($"Completed {scenario}");
