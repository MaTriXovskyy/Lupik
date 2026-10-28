using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Lupik;

static class PerformanceChecks
{
    public static async Task Run(string[] paths)
    {
        var window = new MainWindow();
        foreach (var path in paths)
        {
            var watch = Stopwatch.StartNew();
            var render = new TaskCompletionSource<long>();
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                if (!window.IsVisible) return;
                CompositionTarget.Rendering -= handler;
                render.TrySetResult(watch.ElapsedMilliseconds);
            };
            CompositionTarget.Rendering += handler;
            await window.ShowFile(path);
            long prepared = watch.ElapsedMilliseconds;
            var ready = await Task.WhenAny(render.Task, Task.Delay(3000));
            Console.WriteLine($"PERF {System.IO.Path.GetFileName(path)}: prepared={prepared} ms, first frame={(ready == render.Task ? await render.Task : -1)} ms");
            CompositionTarget.Rendering -= handler;
            window.HideWindow();
            await Task.Delay(100);
        }
        window.Close();
    }
}
