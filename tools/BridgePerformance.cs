using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

public static class HarnessPerformance
{
    public static void Run(string loader, string output, string label, bool verifyBehavior)
    {
        CoreWebView2Environment.SetLoaderDllFolderPath(loader);
        var profile = Path.Combine(output, "profile-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        CoreWebView2Controller controller = null;
        using var form = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(1280, 800) };
        using var watchdog = new Timer { Interval = 55000 };
        watchdog.Tick += (_, _) => { failure = new TimeoutException("Performance fixture timeout"); controller?.Close(); form.Close(); };
        form.Shown += async (_, _) => {
            try
            {
                watchdog.Start();
                var environment = await CoreWebView2Environment.CreateAsync(null, profile);
                controller = await environment.CreateCoreWebView2ControllerAsync(form.Handle);
                controller.Bounds = form.ClientRectangle;
                var core = controller.CoreWebView2;
                var bridge = File.ReadAllText(Path.Combine(output, label + "-bridge.js"));
                var benchmark = File.ReadAllText(Path.Combine(output, "measure.js"));
                using var writer = new StreamWriter(Path.Combine(output, label + "-results.jsonl"), false);
                foreach(var count in new[] { 100, 1000 })
                foreach(var mode in new[] { "modern", "legacy" })
                {
                    var ready = new TaskCompletionSource<bool>();
                    EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = (_, args) => ready.TrySetResult(args.IsSuccess);
                    core.NavigationCompleted += handler;
                    core.NavigateToString("<html><head><style>body{font:14px sans-serif}button{padding:5px}.message{margin:5px;padding:5px}</style></head><body></body></html>");
                    await ready.Task;
                    core.NavigationCompleted -= handler;
                    await core.ExecuteScriptAsync("window.scenario="+JsonSerializer.Serialize(new { count, mode, verifyBehavior })+";");
                    await core.ExecuteScriptAsync(benchmark);
                    await core.ExecuteScriptAsync(bridge);
                    var response = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = "window.runMeasurements()", awaitPromise = true, returnByValue = true }));
                    using var json = JsonDocument.Parse(response);
                    if(json.RootElement.TryGetProperty("exceptionDetails", out var exception)) throw new InvalidOperationException(exception.GetRawText());
                    var result = json.RootElement.GetProperty("result").GetProperty("value").GetRawText();
                    writer.WriteLine(result); writer.Flush(); Console.WriteLine(result);
                    using var behavior = JsonDocument.Parse(result);
                    if(behavior.RootElement.TryGetProperty("behavior", out var checks) && checks.EnumerateObject().Any(check => !check.Value.GetBoolean())) throw new InvalidOperationException("Browser behavior check failed");
                }
                Console.WriteLine("Runtime="+environment.BrowserVersionString);
            }
            catch(Exception ex) { failure=ex; }
            finally { watchdog.Stop(); controller?.Close(); form.Close(); }
        };
        Application.Run(form);
        if(failure != null) throw failure;
    }
}
