# Lupik

Quick Look for Windows. Select a file in Explorer or on the desktop, press Space, and you get a preview.
Press Space again and it's gone. That's the whole idea, and the rest of the app is there to keep it fast.

It's written in C# / WPF on .NET 10, so it talks to Windows directly: Explorer's selection, shell thumbnails,
the system preview handlers Office installs, the Recycle Bin.

## What it opens

- **Images**: PNG, JPG, GIF, BMP, WebP, TIFF, ICO, HEIC/HEIF, AVIF, PSD (flattened)
- **Vectors**: SVG, EPS/PS and Illustrator `.ai` files saved with PDF compatibility (the default)
- **Documents**: PDF, plus anything Windows has a preview handler for (Word, Excel, fonts, Outlook `.msg`...)
- **Code and text**: syntax highlighting for around 40 languages and config formats, Markdown included
- **Tables**: CSV and TSV
- **Archives**: ZIP, RAR, 7z, TAR, GZ, BZ2, XZ (browse the contents, extract)
- **Audio and video**: MP4, MKV, MOV, WebM, AVI, WMV, MP3, FLAC, WAV, OGG, Opus... whatever codecs Windows has
- **Folders**: a grid of what's inside

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
| Ctrl+S / Ctrl+P / Ctrl+C | save a copy, print, copy |
| Ctrl+Space | open Lupik from anywhere |

The preview never steals the focus from Explorer. Explorer stays the active window, and Lupik picks up its
keys through a keyboard hook while it's open. Taking the focus made Windows wait on Explorer, sometimes
for five seconds, and that's not what a preview should feel like.

## Building

You need the .NET 10 SDK.

```
dotnet build -c Release
```

The exe ends up in `bin/Release/net10.0-windows10.0.19041.0/Lupik.exe`. Run it and it sits in the tray.
The tray menu has the settings: start with Windows, the global shortcut, Space in Explorer on or off.

Lupik keeps a log (`Lupik.log`) next to the exe. If something feels slow, that's the first place to look:
every preview logs how long it took to show and to draw its first frame.
