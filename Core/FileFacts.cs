using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Lupik.Core;

/// <summary>
/// What can be told about a file Lupik can't show: where it was downloaded from, its digital signature, what program
/// it is (version info, MSI properties), its type and default app, and where a shortcut leads. Everything is read
/// without running or opening the file in its app; each part fails quietly on its own.
/// </summary>
public static class FileFacts
{
    public enum SignatureState { None, Valid, Invalid }

    public sealed record Signature(SignatureState State, string? Signer);

    /// <summary>Program details (from an .exe/.dll's version info or an .msi's properties).</summary>
    public sealed record ProgramInfo(string? Product, string? Version, string? Company, string? Description, string? Copyright);

    public sealed record Origin(string? Host, string? Url, bool FromInternet);

    public sealed record Shortcut(string Target, string? Arguments, bool IsUrl);

    private static readonly HashSet<string> Signable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".dll", ".sys", ".msix", ".appx", ".msixbundle", ".cab", ".ps1", ".ocx", ".cat", ".msp", ".scr",
    };

    public static bool CanBeSigned(string path) => Signable.Contains(Path.GetExtension(path));

    // --- Type and default app

    /// <summary>"Windows Installer Package" and the app that opens it, as Windows names them.</summary>
    public static (string? TypeName, string? OpensWith) TypeAndApp(string path)
    {
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return (null, null);
        return (Assoc(ext, ASSOCSTR_FRIENDLYDOCNAME), Assoc(ext, ASSOCSTR_FRIENDLYAPPNAME));
    }

    private static string? Assoc(string ext, int what)
    {
        uint size = 512;
        var sb = new StringBuilder((int)size);
        return AssocQueryString(ASSOCF_INIT_IGNOREUNKNOWN, what, ext, null, sb, ref size) == 0 && sb.Length > 0 ? sb.ToString() : null;
    }

    // --- Origin (the "Mark of the Web" Windows keeps next to downloaded files)

    public static Origin? ReadOrigin(string path)
    {
        try
        {
            // An alternate data stream of the file: [ZoneTransfer] ZoneId=3, HostUrl=..., ReferrerUrl=...
            using var stream = new FileStream(path + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            int zone = -1;
            string? host = null, referrer = null;
            while (reader.ReadLine() is string line)
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
                if (key.Equals("ZoneId", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out zone);
                else if (key.Equals("HostUrl", StringComparison.OrdinalIgnoreCase)) host = value;
                else if (key.Equals("ReferrerUrl", StringComparison.OrdinalIgnoreCase)) referrer = value;
            }
            // HostUrl is the file's own address (often a CDN); the page it came from says more when it's there
            string? url = IsWeb(referrer) ? referrer : IsWeb(host) ? host : null;
            string? name = url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
            if (name?.StartsWith("www.", StringComparison.OrdinalIgnoreCase) == true) name = name[4..];
            return new Origin(name, url, zone >= 3);
        }
        catch (FileNotFoundException) { return null; }
        catch (Exception ex)
        {
            App.Log($"[FileFacts] Zone.Identifier of '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
    }

    private static bool IsWeb(string? url) =>
        url != null && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    // --- Digital signature

    /// <summary>Checks the signature like Windows does before running the file (no revocation check: no network).</summary>
    public static Signature CheckSignature(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = WTD_UI_NONE, fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE, pFile = pFile, dwStateAction = WTD_STATEACTION_VERIFY, dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
            };
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);

            if ((uint)result is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN)
                return new Signature(SignatureState.None, null);
            return new Signature(result == 0 ? SignatureState.Valid : SignatureState.Invalid, SignerName(path));
        }
        catch (Exception ex)
        {
            App.Log($"[FileFacts] Signature of '{Path.GetFileName(path)}': {ex.Message}");
            return new Signature(SignatureState.None, null);
        }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    private static string? SignerName(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // the signer of an Authenticode signature, not a certificate file
            using var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var cert2 = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert);
            return cert2.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
        }
        catch { return null; }
    }

    // --- Program details

    public static ProgramInfo? ReadProgram(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext.Equals(".msi", StringComparison.OrdinalIgnoreCase)) return ReadMsi(path);
        if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".sys", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".scr", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            // "1.3.0+dd1853ab…": the part after + is build metadata (a commit hash), not something to show
            string? version = (Clean(v.ProductVersion) ?? Clean(v.FileVersion))?.Split('+')[0];
            var info = new ProgramInfo(Clean(v.ProductName), version, Clean(v.CompanyName),
                Clean(v.FileDescription), Clean(v.LegalCopyright));
            return info is { Product: null, Version: null, Company: null, Description: null } ? null : info;
        }
        catch { return null; }
    }

    /// <summary>An installer's name, version and maker, from its Property table (read only, nothing installed).</summary>
    private static ProgramInfo? ReadMsi(string path)
    {
        if (MsiOpenDatabase(path, IntPtr.Zero, out IntPtr db) != 0) return null;
        try
        {
            string? Property(string name)
            {
                if (MsiDatabaseOpenView(db, $"SELECT `Value` FROM `Property` WHERE `Property`='{name}'", out IntPtr view) != 0) return null;
                try
                {
                    if (MsiViewExecute(view, IntPtr.Zero) != 0 || MsiViewFetch(view, out IntPtr record) != 0) return null;
                    try
                    {
                        uint size = 1024;
                        var sb = new StringBuilder((int)size);
                        return MsiRecordGetString(record, 1, sb, ref size) == 0 && sb.Length > 0 ? sb.ToString() : null;
                    }
                    finally { MsiCloseHandle(record); }
                }
                finally { MsiCloseHandle(view); }
            }

            string? comments = null;
            if (MsiGetSummaryInformation(db, null, 0, out IntPtr summary) == 0)
            {
                try
                {
                    uint size = 1024;
                    var sb = new StringBuilder((int)size);
                    if (MsiSummaryInfoGetProperty(summary, PID_COMMENTS, out _, out _, IntPtr.Zero, sb, ref size) == 0 && sb.Length > 0)
                        comments = sb.ToString();
                }
                finally { MsiCloseHandle(summary); }
            }
            var info = new ProgramInfo(Property("ProductName"), Property("ProductVersion"), Property("Manufacturer"), comments, null);
            return info is { Product: null, Version: null, Company: null } ? null : info;
        }
        catch (Exception ex)
        {
            App.Log($"[FileFacts] MSI '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
        finally { MsiCloseHandle(db); }
    }

    // --- Shortcuts

    public static Shortcut? ReadShortcut(string path)
    {
        string ext = Path.GetExtension(path);
        try
        {
            if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string line in File.ReadLines(path))
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return new Shortcut(line[4..].Trim(), null, true);
                return null;
            }
            if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return null;

            var link = (IShellLinkW)new ShellLink();
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var args = new StringBuilder(1024);
            link.GetArguments(args, args.Capacity);
            Marshal.ReleaseComObject(link);
            return target.Length == 0 ? null : new Shortcut(target.ToString(), args.Length > 0 ? args.ToString() : null, false);
        }
        catch (Exception ex)
        {
            App.Log($"[FileFacts] Shortcut '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
    }

    // --- Interop

    private const int ASSOCF_INIT_IGNOREUNKNOWN = 0x400, ASSOCSTR_FRIENDLYDOCNAME = 3, ASSOCSTR_FRIENDLYAPPNAME = 4;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string pszAssoc, string? pszExtra, StringBuilder pszOut, ref uint pcchOut);

    private static Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1, WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2,
        WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
    private const uint TRUST_E_NOSIGNATURE = 0x800B0100, TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003, TRUST_E_PROVIDER_UNKNOWN = 0x800B0001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    private const uint PID_COMMENTS = 6;

    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiOpenDatabase(string path, IntPtr persist, out IntPtr handle);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiDatabaseOpenView(IntPtr db, string query, out IntPtr view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(IntPtr view, IntPtr record);
    [DllImport("msi.dll")] private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordGetString(IntPtr record, uint field, StringBuilder value, ref uint size);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiGetSummaryInformation(IntPtr db, string? path, uint updateCount, out IntPtr summary);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiSummaryInfoGetProperty(IntPtr summary, uint property, out uint dataType, out int intValue, IntPtr fileTime, StringBuilder value, ref uint size);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(IntPtr handle);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
    }
}
