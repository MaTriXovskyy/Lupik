using System.Drawing;
using System.Windows.Media.Imaging;
using Lupik.Views;

namespace Lupik.Core;

internal sealed class ScrollCaptureSession(Rectangle area,ScreenshotWindow? target,Func<Rectangle,BitmapSource> capture) : IDisposable
{
    private readonly TaskCompletionSource<bool> _stop=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ScrollCaptureControl? _control;
    private bool _cancelled;
    internal void Complete()=>_stop.TrySetResult(true);
    internal void Cancel() {_cancelled=true;_stop.TrySetResult(false);}

    internal async Task<BitmapSource?> RunAsync()
    {
        try
        {
            var first=await Task.Run(()=>capture(area));
            if(_cancelled) return null;
            var stitcher=new ScrollStitcher(first);
            _control=new ScrollCaptureControl(area);
            _control.Finished+=Complete; _control.Cancelled+=Cancel;
            _control.SetStatus("capture.scrollDown",stitcher.Width,stitcher.Height);
            _control.Show();
            if(!_control.SafeToCapture) throw new InvalidOperationException("Capture controls cannot be excluded; select a smaller viewport.");
            while(!_stop.Task.IsCompleted)
            {
                if(await Task.WhenAny(Task.Delay(280),_stop.Task)==_stop.Task) break;
                if(target!=null && (!ScreenshotWindows.StillExists(target) || !ScreenshotWindows.InSamePosition(target)))
                {_control.SetStatus("capture.scrollMoved",stitcher.Width,stitcher.Height);continue;}
                if(target!=null && NativeMethods.GetForegroundWindow()!=target.Handle)
                {_control.SetStatus("capture.scrollFocus",stitcher.Width,stitcher.Height);continue;}
                var frame=await Task.Run(()=>capture(area));
                if(_cancelled) return null;
                var appended=await Task.Run(()=>stitcher.Append(frame));
                if(_cancelled) return null;
                string key=appended switch
                {
                    ScrollAppendResult.NoOverlap=>"capture.scrollOverlap",
                    ScrollAppendResult.Backwards=>"capture.scrollDown",
                    _=>"capture.scrollDown"
                };
                _control.SetStatus(key,stitcher.Width,stitcher.Height);
                if(appended==ScrollAppendResult.Limit) Complete();
            }
            if(_cancelled || !await _stop.Task) return null;
            _control.Building();
            var result=await Task.Run(stitcher.Build);
            return _cancelled ? null : result;
        }
        finally {_control?.Dismiss();_control=null;}
    }
    public void Dispose() {Cancel();_control?.Dismiss();}
}
