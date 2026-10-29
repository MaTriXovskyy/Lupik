using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;

namespace Lupik.Core;

internal static class ScreenshotText
{
    internal static Task<string> ReadAsync(BitmapSource bitmap) => Task.Run(async () =>
    {
        if(!TextRecognition.IsAvailable) return "";
        using var stream=new MemoryStream(ScreenshotFreeform.Png(bitmap));
        using var picture=new Bitmap(stream);
        return TextRecognition.ToText(await TextRecognition.RecognizeAsync(picture).ConfigureAwait(false));
    });
}
