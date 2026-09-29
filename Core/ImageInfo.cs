using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ImageMagick;

namespace Lupik.Core;

/// <summary>One label/value cell of the image info panel.</summary>
public sealed record InfoItem(string Label, string Value, bool Accent = false);

/// <summary>
/// Image details for the info panel: resolution, print size in cm, colour, camera EXIF, file dates.
/// Uses Magick's Ping — headers and metadata only, no pixel decoding — so it's quick even for big PSDs.
/// </summary>
public static class ImageInfo
{
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    public static List<InfoItem> Read(string path)
    {
        var items = new List<InfoItem>();
        var file = new FileInfo(path);

        try
        {
            using var image = new MagickImage();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                image.Ping(stream);

            uint w = image.Width, h = image.Height;
            items.Add(new InfoItem("Wymiary", $"{w} × {h} px  ({w * (double)h / 1e6:0.#} MP)"));

            // DPI: files store either pixels per inch or per centimetre (or nothing at all)
            double dpiX = image.Density.X, dpiY = image.Density.Y;
            if (image.Density.Units == DensityUnit.PixelsPerCentimeter) { dpiX *= 2.54; dpiY *= 2.54; }
            bool hasDpi = image.Density.Units != DensityUnit.Undefined && dpiX >= 1 && dpiY >= 1;

            if (hasDpi)
            {
                items.Add(new InfoItem("Rozdzielczość", Math.Abs(dpiX - dpiY) < 0.5 ? $"{dpiX:0.#} DPI" : $"{dpiX:0.#} × {dpiY:0.#} DPI"));
                items.Add(new InfoItem("Rozmiar wydruku", $"{Cm(w, dpiX)} × {Cm(h, dpiY)} cm", Accent: true));
            }
            else
            {
                items.Add(new InfoItem("Rozdzielczość", "brak zapisanego DPI"));
                items.Add(new InfoItem("Wydruk przy 300 DPI", $"{Cm(w, 300)} × {Cm(h, 300)} cm", Accent: true));
            }

            string color = $"{ColorSpaceName(image.ColorSpace)}, {image.Depth} bit" + (image.HasAlpha ? ", przezroczystość" : "");
            items.Add(new InfoItem("Kolor", color));
            string? profile = image.GetColorProfile()?.Description;
            if (!string.IsNullOrWhiteSpace(profile)) items.Add(new InfoItem("Profil ICC", profile.Trim()));

            AddExif(image.GetExifProfile(), items);
        }
        catch (Exception ex)
        {
            App.Log($"[ImageInfo] Ping failed for '{path}': {ex.Message}");
        }

        items.Add(new InfoItem("Utworzono", file.CreationTime.ToString("yyyy-MM-dd HH:mm")));
        items.Add(new InfoItem("Zmodyfikowano", file.LastWriteTime.ToString("yyyy-MM-dd HH:mm")));
        return items;
    }

    private static void AddExif(IExifProfile? exif, List<InfoItem> items)
    {
        if (exif == null) return;

        string? make = exif.GetValue(ExifTag.Make)?.Value?.Trim();
        string? model = exif.GetValue(ExifTag.Model)?.Value?.Trim();
        if (!string.IsNullOrEmpty(model))
        {
            // "Canon Canon EOS 80D" → "Canon EOS 80D"
            string camera = !string.IsNullOrEmpty(make) && !model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? $"{make} {model}" : model;
            items.Add(new InfoItem("Aparat", camera));
        }

        string? lens = exif.GetValue(ExifTag.LensModel)?.Value?.Trim();
        if (!string.IsNullOrEmpty(lens)) items.Add(new InfoItem("Obiektyw", lens));

        var settings = new List<string>();
        if (exif.GetValue(ExifTag.FNumber)?.Value is { } f && f.Denominator != 0) settings.Add($"f/{f.ToDouble():0.#}");
        if (exif.GetValue(ExifTag.ExposureTime)?.Value is { } t && t.Denominator != 0)
        {
            double s = t.ToDouble();
            settings.Add(s >= 1 || s <= 0 ? $"{s:0.#} s" : $"1/{Math.Round(1 / s)} s");
        }
        if (exif.GetValue(ExifTag.ISOSpeedRatings)?.Value is { Length: > 0 } iso) settings.Add($"ISO {iso[0]}");
        if (exif.GetValue(ExifTag.FocalLength)?.Value is { } fl && fl.Denominator != 0) settings.Add($"{fl.ToDouble():0.#} mm");
        if (settings.Count > 0) items.Add(new InfoItem("Ustawienia", string.Join("  ·  ", settings)));

        string? taken = exif.GetValue(ExifTag.DateTimeOriginal)?.Value;
        if (!string.IsNullOrEmpty(taken) &&
            DateTime.TryParseExact(taken.Trim('\0', ' '), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
            items.Add(new InfoItem("Data wykonania", when.ToString("yyyy-MM-dd HH:mm")));
    }

    private static string Cm(uint pixels, double dpi) => (pixels / dpi * 2.54).ToString("0.#", Pl);

    private static string ColorSpaceName(ColorSpace space) => space switch
    {
        ColorSpace.sRGB or ColorSpace.RGB => "RGB",
        ColorSpace.CMYK => "CMYK",
        ColorSpace.Gray or ColorSpace.LinearGray => "Skala szarości",
        ColorSpace.Lab => "Lab",
        _ => space.ToString(),
    };

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
        return $"{len:0.##} {sizes[order]}";
    }
}
