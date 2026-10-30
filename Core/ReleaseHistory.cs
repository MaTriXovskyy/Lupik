namespace Lupik.Core;

/// <summary>Curated user-facing changes between published versions, available offline.</summary>
internal static class ReleaseHistory
{
    internal sealed record Entry(string Version, params string[] Items);
    internal static readonly Entry[] Entries =
    {
        new("1.9.0", "news.mediaEdit", "news.screenshotEdit", "news.cropKey", "news.captureBar"),
        new("1.8.1", "news.freeform", "news.contrastCursor", "news.textScreenshot", "news.scrollScreenshot"),
        new("1.8.0", "news.screenshots"),
        new("1.7.9", "news.settings", "news.pdfText", "news.pdfPan"),
        new("1.7.8", "news.dialogs"),
        new("1.7.7", "news.loading", "news.onTop", "news.horizontal"),
        new("1.7.6", "news.windowButtons", "news.keyboard", "news.orientation"),
        new("1.7.5", "news.windows", "news.releaseFiles", "news.unsaved"),
        new("1.7.2", "news.pdfUnlocked"),
        new("1.7.1", "news.extract"),
        new("1.7.0", "news.switchApps"),
        new("1.6.0", "news.qr", "news.fonts", "news.openWithKeys", "news.copyPath", "news.colorFormats"),
        new("1.5.0", "news.ocr", "news.pipette", "news.markdown", "news.shortcuts"),
        new("1.4.1", "news.installer"),
        new("1.4.0", "news.archiveSelection", "news.archiveDrag", "news.archivePeek", "news.fileInfo"),
        new("1.3.0", "news.themes", "news.accent", "news.backdrops", "news.pin", "news.openWith", "news.bounds"),
        new("1.2.0", "news.edit", "news.rename"),
        new("1.1.0", "news.office", "news.search", "news.filmstrip", "news.textCompare"),
        new("1.0.1", "news.fasterPreviews"),
        new("1.0.0", "news.first", "news.formats", "news.imageCompare", "news.imageTools", "news.updatesLanguages"),
    };
}
