using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lupik;
using Lupik.Core;
using Lupik.Localization;
using Lupik.Views;

static class PdfAndNewsChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Get(object o, string name) => o.GetType().GetField(name, Private | BindingFlags.Public)!.GetValue(o)!;
    static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name, Private)!.Invoke(o, args);
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

    static string Fixture(string folder)
    {
        Directory.CreateDirectory(folder);
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 5 0 R 7 0 R] /Count 3 >>" };
        foreach (string line in new[] { "FIRST PAGE sample text", "SECOND PAGE more text", "THIRD PAGE last text" })
        {
            int content = objects.Count + 2;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 9 0 R >> >> /Contents {content} 0 R >>");
            string stream = $"BT /F1 24 Tf 60 720 Td ({line}) Tj 0 -40 Td (Another line for selection.) Tj ET\n";
            if (line.StartsWith("FIRST")) stream += "BT /F1 20 Tf 320 400 Td (5) Tj 0 -30 Td (25 cm) Tj ET\nBT /F1 20 Tf 0 1 -1 0 500 300 Tm (270 cm) Tj ET\n";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        string path = Path.Combine(folder, "selection.pdf"); File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
        return path;
    }

    public static async Task Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "Lupik-pdf-news-" + Guid.NewGuid().ToString("N"));
        string fixture = Fixture(folder);
        using (var doc = PdfDocument.Open(fixture))
        {
            try { doc.ReadTextPage(0, new CancellationToken(true)); throw new Exception("Cancelled text extraction still ran"); }
            catch (OperationCanceledException) { }
            var text = doc.ReadTextPage(0);
            Assert(text.GetText(0, text.Count - 1).Contains("FIRST PAGE sample text"), "PDF text extraction failed");
            var box = text.Bounds[0];
            int hit = text.HitTest(new Point(box.X + box.Width / 2, box.Y + box.Height / 2), 0, 0, false);
            Assert(hit == 0, "PDF glyph hit-test failed");
            var word = text.WordRange(1);
            Assert(text.GetText(word.Start, word.End) == "FIRST", "Double-click did not select a word");
            var line = text.LineRange(1);
            Assert(text.GetText(line.Start, line.End) == "FIRST PAGE sample text", "Triple-click did not select a line");
            Assert(text.Rectangles(word.Start, word.End).Count > 0, "No selection rectangles");
            doc.RotatePage(0, 1);
            var rotatedText = doc.ReadTextPage(0);
            int rotatedIndex = rotatedText.GetText(0, rotatedText.Count - 1).IndexOf("FIRST", StringComparison.Ordinal);
            Assert(rotatedIndex >= 0, "Rotated text was lost");
            var rotated = rotatedText.Bounds[rotatedIndex];
            Assert(Math.Abs(rotated.X + rotated.Width / 2 - (1 - box.Y - box.Height / 2)) < 0.002 &&
                Math.Abs(rotated.Y + rotated.Height / 2 - box.X - box.Width / 2) < 0.002,
                "Text selection coordinates do not follow page rotation");
        }
        Console.WriteLine("PASS PDF text extraction, glyph hit-testing, word/line selection and rotated page coordinates");

        if (TextRecognition.IsAvailable)
        {
            string scanned = ScannedFixture(folder, fixture);
            using var document = PdfDocument.Open(scanned);
            Assert(document.ReadTextPage(0).Count == 0, "OCR fixture unexpectedly has native text");
            var read = await PdfTextOcr.ReadAsync(document, 0);
            string recognized = read.GetText(0, read.Count - 1);
            Assert(recognized.Contains("FIRST") && recognized.Contains("PAGE"), "Local OCR did not recover scanned PDF text");
            Assert(read.Bounds.Any(b => !b.IsEmpty && b.X >= 0 && b.Right <= 1), "OCR selection positions were not normalized");
            foreach (string target in new[] { "5", "270" })
            {
                int index = Enumerable.Range(0, read.Count).FirstOrDefault(i => read.GetText(i, i) == target, -1);
                Assert(index >= 0, $"OCR missed isolated/vertical text: {target}");
                var b = read.Bounds[index];
                int hit = read.HitTest(new Point(b.X + b.Width / 2, b.Y + b.Height / 2), 0, 0, false);
                Assert(hit == index, $"OCR coordinates do not match {target}");
                Assert(target != "270" || b.Height > b.Width, "Vertical OCR bounds were not rotated back");
            }
            Console.WriteLine("PASS image-only PDF OCR, isolated digit, vertical caption and selection coordinates");
        }

        var w = new MainWindow();
        await w.ShowFile(fixture);
        var pdfViewer = (PdfViewer)Get(w, "PdfViewerControl");
        var scroller = (ScrollViewer)Get(pdfViewer, "PagesScrollViewer");
        var slots = (IList)Get(pdfViewer, "_slots");
        var first = slots[0]!;
        var loading = (Task)Call(pdfViewer, "EnsureTextAsync", first, 0)!;
        await loading;
        Call(pdfViewer, "BeginTextSelection", 0, 1, 2);
        Assert(pdfViewer.SelectedText() == "FIRST", "Viewer did not preserve the selected word");
        IDataObject? oldClipboard = null;
        bool clipboardReady = false;
        for (int attempt = 0; attempt < 8 && !clipboardReady; attempt++)
        {
            try { oldClipboard = Clipboard.GetDataObject(); clipboardReady = true; }
            catch (System.Runtime.InteropServices.COMException) { await Task.Delay(40); }
        }
        if (clipboardReady)
        {
            try
            {
                pdfViewer.CopySelection();
                Assert(Clipboard.GetText() == "FIRST", "Ctrl+C copy content is incorrect");
            }
            finally { if (oldClipboard != null) Clipboard.SetDataObject(oldClipboard, true); else Clipboard.Clear(); }
        }
        else Console.WriteLine("SKIP clipboard integration: another app is holding the system clipboard");
        Assert(await pdfViewer.SearchAsync("PAGE", CancellationToken.None) == 3 && pdfViewer.SelectedText() == "FIRST",
            "Search destroyed text selection");
        pdfViewer.ClearSearch();
        Call(pdfViewer, "SetZoom", 3.0);
        await Task.Delay(200); w.UpdateLayout();
        Assert(pdfViewer.SelectedText() == "FIRST" && scroller.ScrollableWidth > 0, "Zoom lost selection or horizontal overflow");
        scroller.ScrollToHorizontalOffset(120); scroller.ScrollToVerticalOffset(180); await Task.Delay(60);
        double beforeX = scroller.HorizontalOffset, beforeY = scroller.VerticalOffset;
        Call(pdfViewer, "BeginPan", new Point(200, 200));
        Call(pdfViewer, "MovePan", new Point(150, 140)); await Task.Delay(60);
        Assert(Math.Abs(scroller.HorizontalOffset - beforeX - 50) < 1 && Math.Abs(scroller.VerticalOffset - beforeY - 60) < 1,
            "Right-button pan did not move both axes with the pointer");
        Call(pdfViewer, "StopPointerGesture");
        Assert(pdfViewer.SelectedText() == "FIRST", "Right-button pan destroyed the selected text");
        Call(pdfViewer, "SetZoom", 1.0); pdfViewer.FirstPage(); await Task.Delay(180);
        var selectionPreview = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        selectionPreview.Render(w);
        var pdfPng = new PngBitmapEncoder(); pdfPng.Frames.Add(BitmapFrame.Create(selectionPreview));
        string pdfImage = Path.Combine(folder, "pdf-selection.png");
        using (var output = File.Create(pdfImage)) pdfPng.Save(output);
        Console.WriteLine("Visual check: " + pdfImage);
        await pdfViewer.SelectAllAsync();
        string all = pdfViewer.SelectedText();
        Assert(all.Contains("FIRST PAGE") && all.Contains("SECOND PAGE") && all.Contains("THIRD PAGE"), "Select all omitted PDF pages");
        pdfViewer.ClearSelection(); Assert(!pdfViewer.HasSelection, "Clear selection failed");
        Call(pdfViewer, "BeginPan", new Point(100, 100));
        w.HideWindow();
        Assert(!(bool)Get(pdfViewer, "_panning") && !scroller.IsMouseCaptured && !pdfViewer.HasSelection, "Close retained capture or text selection");
        using (var exclusive = new FileStream(fixture, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        w.Close();
        Console.WriteLine("PASS PDF text copy, search/zoom coexistence, two-axis panning, multi-page selection and immediate close cleanup");

        var settings = (SettingsWindow)Activator.CreateInstance(typeof(SettingsWindow), true)!;
        var nav = (RadioButton)Get(settings, "NavNews");
        var entries = (StackPanel)Get(settings, "NewsEntries");
        string oldLanguage = Settings.Current.Language;
        string oldTheme = Settings.Current.Theme;
        try
        {
            nav.IsChecked = true;
            foreach (var (language, _) in Loc.Languages)
            {
                Loc.Instance.SetLanguage(language);
                Assert(entries.Children.Count == 18, "Missing release-history versions");
                foreach (var block in Descendants<TextBlock>(entries))
                    Assert(!block.Text.StartsWith("news.") && !block.Text.Contains("AutoHotkey", StringComparison.OrdinalIgnoreCase), "Missing/forbidden release-history text");
            }
            Loc.Instance.SetLanguage("pl");
            string text = string.Join("\n", Descendants<TextBlock>(entries).Select(t => t.Text));
            Assert(text.Contains("Podgląd plików w systemowych oknach „Otwórz” i „Zapisz”."), "Approved 1.7.8 wording changed");
            var v178 = (Border)entries.Children[2];
            Assert(Descendants<TextBlock>(v178).Count(t => t.Text.StartsWith("Podgląd")) == 1, "1.7.8 contains implementation-stage fixes");
            foreach (bool light in new[] { false, true })
            {
                Settings.Current.Theme = light ? "light" : "dark"; Accent.ApplySaved();
                settings.Show(); await Task.Delay(250); settings.UpdateLayout();
                var bitmap = new RenderTargetBitmap(856, 636, 96, 96, PixelFormats.Pbgra32); bitmap.Render(settings);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                string path = Path.Combine(folder, light ? "news-light.png" : "news-dark.png");
                using (var file = File.Create(path)) png.Save(file);
                Console.WriteLine("Visual check: " + path);
            }
        }
        finally
        {
            Loc.Instance.SetLanguage(oldLanguage);
            settings.Close();
            Settings.Current.Theme = oldTheme; Accent.ApplySaved();
        }
        Console.WriteLine("PASS offline release history, all eight languages, approved wording and theme rendering");
    }

    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    static string ScannedFixture(string folder, string source)
    {
        using var document = PdfDocument.Open(source);
        using var image = document.RenderPageForPrint(0, 1200);
        using var jpeg = new MemoryStream(); image.Save(jpeg, System.Drawing.Imaging.ImageFormat.Jpeg);
        using var pdf = new MemoryStream();
        void Write(string value) => pdf.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        void Obj(int number, string value) { offsets.Add(pdf.Position); Write($"{number} 0 obj\n{value}\nendobj\n"); }
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /I0 4 0 R >> >> /Contents 5 0 R >>");
        offsets.Add(pdf.Position);
        Write($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        pdf.Write(jpeg.ToArray()); Write("\nendstream\nendobj\n");
        string draw = "q 612 0 0 792 0 0 cm /I0 Do Q\n";
        Obj(5, $"<< /Length {draw.Length} >>\nstream\n{draw}endstream");
        long xref = pdf.Position;
        Write("xref\n0 6\n0000000000 65535 f \n");
        foreach (long offset in offsets) Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        string path = Path.Combine(folder, "scanned.pdf"); File.WriteAllBytes(path, pdf.ToArray()); return path;
    }

    public static async Task OcrDiagnostic(string path, string folder)
    {
        Directory.CreateDirectory(folder);
        using var document = PdfDocument.Open(path);
        using var image = document.RenderPageForPrint(0, 2400);
        image.Save(Path.Combine(folder, "page.png"), System.Drawing.Imaging.ImageFormat.Png);
        foreach (var area in new[] { new System.Drawing.Rectangle(1070,555,100,80), new System.Drawing.Rectangle(1070,555,50,45), new System.Drawing.Rectangle(1040,540,160,120) })
        {
            var lines = await TextRecognition.RecognizeAsync(image, area);
            Console.WriteLine($"Crop {area}: {TextRecognition.ToText(lines)}");
        }
        foreach (int turn in new[] { 0, 1, 3, 2 })
        {
            using var rotated = (System.Drawing.Bitmap)image.Clone();
            rotated.RotateFlip((System.Drawing.RotateFlipType)turn);
            var lines = await TextRecognition.RecognizeAsync(rotated);
            File.WriteAllText(Path.Combine(folder, $"ocr-{turn}.json"), System.Text.Json.JsonSerializer.Serialize(lines));
            var wanted = lines.SelectMany(l => l.Words).Where(w => w.Text is "5" or "S" || w.Text.Contains("270"));
            Console.WriteLine($"Turn {turn * 90}: size={rotated.Width}x{rotated.Height}, words={lines.Sum(l=>l.Words.Count)}, targets=" + string.Join("; ", wanted.Select(w=>$"{w.Text} [{w.Bounds}]")));
        }
    }
}
