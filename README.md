# Lupik

Quick Look for Windows. Select a file in Explorer, on the desktop or in a standard Windows Open/Save dialog,
press Space, and you get a preview.
Press Space again and it's gone. That's the whole idea, and the rest of the app is there to keep it fast.

It's written in C# / WPF on .NET 10, so it talks to Windows directly: Explorer's selection, shell thumbnails,
the system preview handlers Office installs, the Recycle Bin.

## Installing

Grab `Lupik-win-Setup.exe` from the [latest release](https://github.com/MaTriXovskyy/Lupik/releases/latest) and run it.
It installs for your user only (no admin prompt), adds a Start menu entry and starts Lupik in the tray.
Windows may warn about an unknown publisher the first time, because the installer isn't code-signed.

Lupik checks GitHub for new versions and asks before installing one. You can turn that off in Settings.

## What it opens

- **Images**: PNG, JPG, GIF, BMP, WebP, TIFF, ICO, HEIC/HEIF, AVIF, PSD (flattened)
- **Vectors**: SVG, EPS/PS and Illustrator `.ai` files saved with PDF compatibility (the default)
- **Documents**: PDF; Word (`.docx`) and Excel (`.xlsx`, a tab per sheet) drawn by Lupik itself, no Office needed;
  plus anything else Windows has a preview handler for (old `.doc`/`.xls`, PowerPoint, fonts, Outlook `.msg`...)
- **Code and text**: syntax highlighting for around 40 languages and config formats, Markdown included
- **Tables**: CSV and TSV
- **Comparing**: select two pictures, or two text/code files (a diff: removed lines red, added green, changed words
  marked; ↑/↓ jump between changes), and press the preview key
- **Archives**: ZIP, RAR, 7z, TAR, GZ, BZ2, XZ. Browse the contents as a tree, hold the mouse on a file to peek at it,
  drag a file out to Explorer or extract just that one, or extract the whole archive like WinRAR does
  ("Extract here" or "Extract to "Name"")
- **Audio and video**: MP4, MKV, MOV, WebM, AVI, WMV, MP3, FLAC, WAV, OGG, Opus... whatever codecs Windows has
- **Fonts**: TTF, OTF, TTC: the alphabet, a sample at growing sizes and every character (click one to copy it)
- **Folders**: a grid of what's inside

Pictures have a filmstrip of the folder's images underneath (S).
In Windows file pickers, click an item in the file list and press the preview key. Closing the preview returns
focus to the picker without accepting it. Filename/search fields and dialog buttons keep their normal keys.
While a large image loads, the accent line under the title bar shows its progress, including while a quick
thumbnail is visible. It fades out once decoding finishes. Decoders without measurable progress show a moving line.

**Edit mode (E)** is for quick fixes, not for replacing real editors:
- text, code and config files: edit in place (the file keeps its encoding and line endings)
- CSV: edit cells like in Excel (double-click, Enter or just type; Delete clears)
- PDF: rotate or delete pages, extract pages to a new PDF, append another PDF
- pictures (JPG, PNG, WebP, BMP, TIFF): blur a part, draw an arrow or a frame, add text, make it smaller

Ctrl+S saves: the original goes to the Recycle Bin, so nothing is lost. Esc leaves edit mode (and asks about
unsaved changes). In edit mode Lupik takes the keyboard focus, which the preview otherwise never does.
Ctrl+F searches in code and text, tables, Word documents, PDFs and diffs (Enter / Shift+Enter: next / previous).

PDF and `.ai` files are rendered by [PDFium](https://pdfium.googlesource.com/pdfium/), the PDF engine inside Chrome.
Win+Shift+S opens Lupik's own screenshot overlay: choose a region, a window, one monitor or all monitors.
Captures go to the clipboard, with a thumbnail notification in the bottom-right corner. Click it to open Lupik;
the tray menu also offers the last capture. Esc or right-click cancels. Captures use a private temporary cache;
use Save as to keep one permanently. Pin a capture to keep it next to another preview. To restore Windows
snipping while Lupik is running, turn off Screenshots in Settings → Keys.

Drag with the left mouse button to select PDF text, and use Ctrl+C to copy it. Double-click selects a word,
triple-click a line, and Ctrl+A selects the document. Hold the right mouse button to pan the PDF view.
Image-only PDF pages use Windows' local OCR in the background to make their text selectable.

Settings includes an offline **What's new** page with the history of published versions.

EPS files are drawn by a small PostScript interpreter that lives in this repo (`Core/PostScript`).
No Ghostscript to install. It handles what Illustrator, CorelDRAW and Photoshop put in their EPS files,
embedded Type 1 fonts included.

`test_samples/` has one file of every format above if you want to see them all.

## Keys

| Key | |
|---|---|
| Space | open / close the preview |
| F1 or ? in the footer | the shortcuts that work for the file on screen |
| ← → | previous / next file |
| Ctrl+Shift+C | copy the file's path |
| Enter | open with… (the apps Windows suggests for the file) |
| Delete | move to the Recycle Bin (asks first) |
| F | full screen |
| + − 0 | zoom in, zoom out, fit |
| R / Shift+R | rotate |
| K | crop (Shift keeps proportions, Alt resizes from the center) |
| I | image details |
| S | filmstrip of the folder's images |
| B | background behind pictures: theme, checkerboard, black, white |
| T | text in a picture (and QR codes / barcodes: copy or open them): drag over some to copy it, click one to copy it, Enter copies all |
| Ctrl (held over a picture) | pipette: a loupe with the colour under the cursor; Ctrl+click copies it (HEX; with Shift rgb(), with Alt hsl()) |
| M | Markdown as a document or as its source |
| P | pin: the file moves to a window of its own and no longer follows Explorer; P or the layers button toggles "always on top" (remembered for new pinned windows) |
| Shift + mouse wheel | scroll horizontally; pan zoomed images sideways |
| C | compare the two selected images / text files, or back to one |
| Ctrl+F, F3 | search (Enter / Shift+Enter: next / previous, Esc closes) |
| E | edit mode (Ctrl+S saves, Esc leaves) |
| F2 | rename |
| K J L M | play/pause, back 5 s, forward 5 s, mute (audio and video) |
| Ctrl+S / Ctrl+P / Ctrl+C | save a copy in another format (images), print, copy |

Space can be swapped for any other key or shortcut in Settings.

Opening a preview activates Lupik so its shortcuts work like in a normal window. Closing it returns focus
to the Explorer window or file picker it came from. The global keyboard hook handles only the preview key;
other keys stay with the active application.

## Building

You need the .NET 10 SDK.

```
dotnet build -c Release
```

The exe ends up in `bin/Release/net10.0-windows10.0.19041.0/Lupik.exe`. Run it and it sits in the tray.
Right-click the tray icon for Settings: language (Lupik speaks Polish, English, German, Spanish, French, Italian,
Russian and Ukrainian, and follows Windows by default), the preview key, start with Windows, closing the preview
when you click another app, window size (remembered separately for each kind of file once you move or resize
the window), media autoplay, updates, and the look: a dark, light or as-Windows theme, a Mica or acrylic backdrop
for the preview (Windows 11), and Lupik's color. The gold accent can be swapped for any RGB color, one of the
ready-made ones or Windows' own accent, and every golden detail follows it.

The app button with the arrow in the title bar lists the apps Windows suggests for the file, like Explorer's
"Open with" menu.

Texts live in `Localization/translations.py`; run it after editing to regenerate `Localization/Strings/*.json`.
The app icon is drawn by `tools/make-icon.cs` (`dotnet run --file tools/make-icon.cs -- app.ico`).

## Releasing

Bump nothing by hand: push a tag and GitHub Actions does the rest.

```
git tag v1.0.1
git push origin v1.0.1
```

The workflow in `.github/workflows/release.yml` publishes the app, packs it with [Velopack](https://velopack.io)
(installer, full and delta update packages) and creates the GitHub release that installed copies update from.

## Logs

Lupik keeps a log in `%LocalAppData%\Lupik\logs\Lupik.log`. If something feels slow, that's the first place to look:
every preview logs how long it took to show and to draw its first frame.

## Licenses

Third-party libraries and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md);
the full texts for PDFium and what it bundles are in `licenses/pdfium/`.
