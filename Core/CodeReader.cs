using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using ZXing;
using ZXing.Common;

namespace Lupik.Core;

/// <summary>
/// QR codes and barcodes in a picture (ZXing): read alongside the text in text mode (T), so a link in a QR code is
/// one click away.
/// </summary>
public static class CodeReader
{
    public sealed record Code(string Text, string Kind)
    {
        /// <summary>A web address (or mailto:) Windows can open.</summary>
        public bool IsLink => Uri.TryCreate(Text.Trim(), UriKind.Absolute, out var uri)
                              && uri.Scheme is "http" or "https" or "mailto";
    }

    public static IReadOnlyList<Code> Read(Bitmap bitmap)
    {
        try
        {
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions { TryHarder = true, TryInverted = true },
            };
            var results = reader.DecodeMultiple(Luminance(bitmap));
            if (results == null) return Array.Empty<Code>();
            return results.Where(r => !string.IsNullOrWhiteSpace(r.Text))
                          .Select(r => new Code(r.Text, KindName(r.BarcodeFormat)))
                          .DistinctBy(c => c.Text)
                          .ToList();
        }
        catch (Exception ex)
        {
            App.Log($"[CodeReader] {ex.Message}");
            return Array.Empty<Code>();
        }
    }

    private static string KindName(BarcodeFormat format) => format switch
    {
        BarcodeFormat.QR_CODE => "QR",
        BarcodeFormat.DATA_MATRIX => "Data Matrix",
        BarcodeFormat.AZTEC => "Aztec",
        BarcodeFormat.PDF_417 => "PDF417",
        BarcodeFormat.EAN_13 => "EAN-13",
        BarcodeFormat.EAN_8 => "EAN-8",
        BarcodeFormat.UPC_A => "UPC-A",
        BarcodeFormat.UPC_E => "UPC-E",
        BarcodeFormat.CODE_128 => "Code 128",
        BarcodeFormat.CODE_39 => "Code 39",
        _ => format.ToString().Replace('_', ' '),
    };

    private static RGBLuminanceSource Luminance(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            // Stride may be padded: 32 bpp rows never are, so width * 4 == stride
            return new RGBLuminanceSource(bytes, bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
        }
        finally { bitmap.UnlockBits(data); }
    }
}
