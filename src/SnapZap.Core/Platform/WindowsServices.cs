using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SnapZap.Core.Resources;

namespace SnapZap.Core.Platform;

/// <summary>
/// Windows platform implementations (the shipping target). These compile cross-platform but
/// only run on Windows; they are registered in place of the macOS dev implementations at
/// runtime. ⚠ RUNTIME-VERIFY ON WINDOWS: authored on macOS and cross-compiled, so the P/Invoke
/// and Shell interop below must be exercised on real Windows hardware before release
/// (DESIGN §9).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrashService : ITrashService
{
    public Task<string?> SendToTrashAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = full + '\0' + '\0',           // double-null-terminated list
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        int rc = SHFileOperation(ref op);
        if (rc != 0) throw new IOException(CoreStrings.Get("RecycleFailed", path, rc));
        // The Recycle Bin does not expose a stable post-move path; restore is by original path.
        return Task.FromResult<string?>(null);
    }

    public Task<bool> RestoreAsync(string originalPath, string? trashedLocation, CancellationToken ct = default)
    {
        // Restore via the Shell namespace: find the Recycle Bin item whose original location
        // matches, then invoke its "Restore" verb. Uses late-bound COM so it compiles anywhere.
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return Task.FromResult(false);
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic recycler = shell.NameSpace(10); // ssfBITBUCKET
            foreach (dynamic item in recycler.Items())
            {
                string name = item.Name;
                // Column 1 is the original location (folder); combine with the item name.
                string folder = recycler.GetDetailsOf(item, 1);
                var candidate = Path.Combine(folder, name);
                if (string.Equals(candidate, originalPath, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (dynamic verb in item.Verbs())
                    {
                        string vn = verb.Name;
                        if (vn.Replace("&", "").Equals("Restore", StringComparison.OrdinalIgnoreCase))
                        {
                            verb.DoIt();
                            return Task.FromResult(File.Exists(originalPath));
                        }
                    }
                }
            }
        }
        catch { /* fall through to false */ }
        return Task.FromResult(false);
    }

    const uint FO_DELETE = 0x0003;
    const ushort FOF_SILENT = 0x0004;
    const ushort FOF_NOCONFIRMATION = 0x0010;
    const ushort FOF_ALLOWUNDO = 0x0040;
    const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsLinkService : ILinkService
{
    public bool SameVolume(string a, string b)
    {
        // Hard links require the same NTFS volume. Comparing path roots (drive letters) is the
        // practical proxy; distinct mount points on one drive are rare in this app's usage.
        var ra = Path.GetPathRoot(Path.GetFullPath(a));
        var rb = Path.GetPathRoot(Path.GetFullPath(NearestExisting(b)));
        return !string.IsNullOrEmpty(ra) && string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
    }

    public void CreateHardLink(string linkPath, string targetPath)
    {
        if (!CreateHardLinkW(linkPath, targetPath, IntPtr.Zero))
            throw new IOException(CoreStrings.Get("CreateHardLinkFailed", linkPath, targetPath, Marshal.GetLastWin32Error()));
    }

    static string NearestExisting(string path)
    {
        var p = Path.GetFullPath(path);
        while (!File.Exists(p) && !Directory.Exists(p))
        {
            var parent = Path.GetDirectoryName(p);
            if (string.IsNullOrEmpty(parent) || parent == p) break;
            p = parent;
        }
        return p;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
