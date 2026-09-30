using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lupik.Core;

/// <summary>
/// The apps Windows offers for a file type (the list in Explorer's "Open with" submenu): name, icon, whether it's the
/// default, and a way to open a file with it. Uses the shell's own association handlers, so desktop and Store apps
/// both show up, exactly as in Explorer.
/// </summary>
public static class OpenWithApps
{
    public sealed class App
    {
        internal IAssocHandler Handler = null!;
        public string Name { get; init; } = "";
        public ImageSource? Icon { get; init; }
        public bool IsDefault { get; init; }
    }

    /// <summary>Recommended apps for the file's extension, the default one first. Empty if there are none.</summary>
    public static List<App> For(string path)
    {
        var apps = new List<App>();
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return apps;
        string? defaultName = DefaultAppName(ext);
        try
        {
            if (SHAssocEnumHandlers(ext, ASSOC_FILTER_RECOMMENDED, out var list) != 0 || list == null) return apps;
            var one = new IAssocHandler[1];
            while (list.Next(1, one, out uint fetched) == 0 && fetched == 1)
            {
                var handler = one[0];
                string name = "";
                try
                {
                    handler.GetUIName(out var ui);
                    name = ui;
                }
                catch { }
                if (string.IsNullOrWhiteSpace(name)) continue;
                ImageSource? icon = null;
                try
                {
                    handler.GetIconLocation(out var iconPath, out int index);
                    icon = LoadIcon(iconPath, index);
                }
                catch { }
                apps.Add(new App
                {
                    Handler = handler,
                    Name = name,
                    Icon = icon,
                    IsDefault = defaultName != null && string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        catch (Exception ex)
        {
            Lupik.App.Log($"[OpenWith] Listing apps for {ext} failed: {ex.Message}");
        }
        // The default first, the rest alphabetically; each name once
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        apps.RemoveAll(a => !seen.Add(a.Name));
        apps.Sort((a, b) => a.IsDefault != b.IsDefault ? (a.IsDefault ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return apps;
    }

    /// <summary>Opens <paramref name="path"/> with <paramref name="app"/>. False if Windows refused.</summary>
    public static bool Open(App app, string path)
    {
        try
        {
            var iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item) != 0 || item == null) return false;
            var bhid = BHID_DataObject;
            var dataIid = new Guid("0000010e-0000-0000-C000-000000000046"); // IDataObject
            item.BindToHandler(IntPtr.Zero, ref bhid, ref dataIid, out var dataObject);
            int hr = app.Handler.Invoke(dataObject);
            Marshal.ReleaseComObject(dataObject);
            return hr == 0;
        }
        catch (Exception ex)
        {
            Lupik.App.Log($"[OpenWith] Opening with {app.Name} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Display name of the app that opens <paramref name="ext"/> by default.</summary>
    private static string? DefaultAppName(string ext)
    {
        uint size = 512;
        var sb = new StringBuilder((int)size);
        return AssocQueryString(ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_FRIENDLYAPPNAME, ext, null, sb, ref size) == 0 ? sb.ToString() : null;
    }

    /// <summary>Icon from "file,index", or a Store app's "@{package?ms-resource://…}" logo.</summary>
    private static ImageSource? LoadIcon(string location, int index)
    {
        if (string.IsNullOrEmpty(location)) return null;
        if (location.StartsWith("@{", StringComparison.Ordinal))
        {
            var sb = new StringBuilder(1024);
            if (SHLoadIndirectString(location, sb, sb.Capacity, IntPtr.Zero) != 0) return null;
            string file = sb.ToString();
            if (!File.Exists(file)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(file);
            bmp.DecodePixelWidth = 32;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        string expanded = Environment.ExpandEnvironmentVariables(location);
        var large = new IntPtr[1];
        if (ExtractIconEx(expanded, index, large, null, 1) == 0 || large[0] == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DestroyIcon(large[0]); }
    }

    // --- Shell interop

    private const int ASSOC_FILTER_RECOMMENDED = 1;
    private const int ASSOCF_INIT_IGNOREUNKNOWN = 0x400;
    private const int ASSOCSTR_FRIENDLYAPPNAME = 4;
    private static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHAssocEnumHandlers(string pszExtra, int afFilter, out IEnumAssocHandlers ppEnumHandler);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IShellItem ppv);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string pszAssoc, string? pszExtra, StringBuilder pszOut, ref uint pcchOut);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

    [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumAssocHandlers
    {
        [PreserveSig] int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IAssocHandler[] rgelt, out uint pceltFetched);
    }

    [ComImport, Guid("F04061AC-1659-4a3f-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAssocHandler
    {
        void GetName([MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
        void GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string ppszPath, out int pIndex);
        [PreserveSig] int IsRecommended();
        [PreserveSig] int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
        [PreserveSig] int Invoke([MarshalAs(UnmanagedType.Interface)] object pdo);
        [PreserveSig] int CreateInvoker([MarshalAs(UnmanagedType.Interface)] object pdo, out IntPtr ppInvoker);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
