using System.IO;
using System.Reflection;
using Lupik.Core;
using Lupik.Views;

static class ImageReleaseChecks
{
    public static async Task Run(string samples)
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new DecodeProgress { Cancellation = cancellation.Token };
        using (var stream = new DecodeReadStream(Path.Combine(samples, "sample.png"), progress))
        {
            cancellation.Cancel();
            try { _ = stream.Read(new byte[10], 0, 10); throw new Exception("Cancelled byte read succeeded"); }
            catch (OperationCanceledException) { }
            try { _ = stream.Read(new byte[10].AsSpan()); throw new Exception("Cancelled span read succeeded"); }
            catch (OperationCanceledException) { }
        }

        var viewer = new ImageViewer();
        var field = typeof(ImageViewer).GetField("_decodeSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var oldToken = ((CancellationTokenSource)field.GetValue(viewer)!).Token;
        viewer.Preload(Path.Combine(samples, "sample.png"));
        viewer.Release();
        if (!oldToken.IsCancellationRequested) throw new Exception("Closing did not cancel the decode session");
        var cache = (System.Collections.IDictionary)typeof(ImageViewer)
            .GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
        if (cache.Count != 0) throw new Exception("Closed preview retained cached images");
        if (((CancellationTokenSource)field.GetValue(viewer)!).IsCancellationRequested)
            throw new Exception("Next preview inherited cancellation");
        if (!await viewer.LoadImageAsync(Path.Combine(samples, "sample.png")))
            throw new Exception("Reopening failed");
        viewer.Release();
        var image = (System.Windows.Controls.Image)typeof(ImageViewer)
            .GetField("PreviewImage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(viewer)!;
        if (image.Source != null || viewer.LoadingProgress != null) throw new Exception("Closed preview retained its image or decode");
        Console.WriteLine("PASS cancelled decoder reads, session cancellation, reopen and image release");
    }
}
