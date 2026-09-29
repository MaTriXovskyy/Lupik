# Lupik

Quick Look for Windows. Select a file in Explorer or on the desktop, press Space, and you get a preview.
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
- **Documents**: PDF, plus anything Windows has a preview handler for (Word, Excel, fonts, Outlook `.msg`...)
- **Code and text**: syntax highlighting for around 40 languages and config formats, Markdown included
- **Tables**: CSV and TSV
- **Archives**: ZIP, RAR, 7z, TAR, GZ, BZ2, XZ. Browse the contents as a tree, hold the mouse on a file to peek at it,
  drag a file out to Explorer or extract just that one, or extract the whole archive
- **Audio and video**: MP4, MKV, MOV, WebM, AVI, WMV, MP3, FLAC, WAV, OGG, Opus... whatever codecs Windows has
- **Folders**: a grid of what's inside

PDF and `.ai` files are rendered by [PDFium](https://pdfium.googlesource.com/pdfium/), the PDF engine inside Chrome.

EPS files are drawn by a small PostScript interpreter that lives in this repo (`Core/PostScript`).
No Ghostscript to install. It handles what Illustrator, CorelDRAW and Photoshop put in their EPS files,
embedded Type 1 fonts included.

`test_samples/` has one file of every format above if you want to see them all.

## Keys

| Key | |
|---|---|
| Space | open / close the preview |
| ← → | previous / next file |
| Enter | open in the default app |
| Delete | move to the Recycle Bin (asks first) |
| F | full screen |
| + − 0 | zoom in, zoom out, fit |
| R / Shift+R | rotate |
| K | crop (Shift keeps proportions, Alt resizes from the center) |
| I | image details |
| C | compare two selected images side by side |
| K J L M | play/pause, back 5 s, forward 5 s, mute (audio and video) |
| Ctrl+S / Ctrl+P / Ctrl+C | save a copy in another format (images), print, copy |

Space can be swapped for any other key or shortcut in Settings.

The preview never steals the focus from Explorer. Explorer stays the active window, and Lupik picks up its
keys through a keyboard hook while it's open. Taking the focus made Windows wait on Explorer, sometimes
for five seconds, and that's not what a preview should feel like.

## Building

You need the .NET 10 SDK.

```
dotnet build -c Release
```

The exe ends up in `bin/Release/net10.0-windows/Lupik.exe`. Run it and it sits in the tray.
Right-click the tray icon for Settings: language (Lupik speaks Polish, English, German, Spanish, French, Italian,
Russian and Ukrainian, and follows Windows by default), the preview key, start with Windows, closing the preview
when you click another app, window size, media autoplay and updates.

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
