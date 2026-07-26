using System.Diagnostics;

namespace SnapZap.Core.Platform;

/// <summary>
/// macOS dev implementations. NOT shipped — the Windows equivalents replace these at
/// publish time. They exist so the export engine and delete flow are testable on the Mac.
/// </summary>
public sealed class MacOsTrashService : ITrashService
{
    public async Task<string?> SendToTrashAsync(string path, CancellationToken ct = default)
    {
        // Ask Finder to trash the item and report back the POSIX path it landed at in ~/.Trash,
        // so the undo log can restore it later. Reversible; respects the real Trash.
        var full = System.IO.Path.GetFullPath(path);
        var script =
            "tell application \"Finder\"\n" +
            $"set t to delete (POSIX file \"{full.Replace("\"", "\\\"")}\" as alias)\n" +
            "return POSIX path of (t as alias)\n" +
            "end tell";
        var (code, stdout, stderr) = await RunOsa(script, ct);
        if (code != 0)
            throw new IOException($"Trash failed for {path}: {stderr}");
        var loc = stdout.Trim();
        return string.IsNullOrEmpty(loc) ? null : loc;
    }

    public async Task<bool> RestoreAsync(string originalPath, string? trashedLocation, CancellationToken ct = default)
    {
        // Prefer the exact trashed location; fall back to matching the basename in ~/.Trash.
        var src = trashedLocation;
        if (string.IsNullOrEmpty(src) || !File.Exists(src))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var guess = System.IO.Path.Combine(home, ".Trash", System.IO.Path.GetFileName(originalPath));
            src = File.Exists(guess) ? guess : null;
        }
        if (src is null || !File.Exists(src)) return false;

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(originalPath)!);
        File.Move(src, originalPath, overwrite: false);
        await Task.CompletedTask;
        return File.Exists(originalPath);
    }

    static async Task<(int code, string stdout, string stderr)> RunOsa(string script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("osascript")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        // Pass the script via stdin to avoid shell-quoting hazards.
        psi.RedirectStandardInput = true;
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync(script);
        p.StandardInput.Close();
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, stdout, stderr);
    }
}

public sealed class MacOsLinkService : ILinkService
{
    public bool SameVolume(string a, string b)
    {
        var da = GetDeviceId(a);
        var db = GetDeviceId(DirectoryOf(b));
        return da is not null && da == db;
    }

    public void CreateHardLink(string linkPath, string targetPath)
    {
        var rc = link(targetPath, linkPath);
        if (rc != 0)
            throw new IOException($"hard link {linkPath} -> {targetPath} failed (errno via link())");
    }

    static string DirectoryOf(string p) =>
        System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(p)) ?? "/";

    static int? GetDeviceId(string path)
    {
        // stat(2) st_dev identifies the volume. Walk up until an existing path is found,
        // since the destination file itself may not exist yet.
        var probe = System.IO.Path.GetFullPath(path);
        while (!File.Exists(probe) && !Directory.Exists(probe))
        {
            var parent = System.IO.Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(parent) || parent == probe) return null;
            probe = parent;
        }
        return stat(probe, out var st) == 0 ? st.st_dev : null;
    }

    // --- libc interop (Darwin) ---
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    static extern int link(string oldpath, string newpath);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    static extern int stat(string path, out StatDarwin buf);

    // Layout of Darwin `struct stat` (st_dev is the first field, an int32 dev_t).
    // Only st_dev is read; the remaining fields exist to size the struct correctly.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct StatDarwin
    {
        public int st_dev;
        public ushort st_mode;
        public ushort st_nlink;
        public ulong st_ino;
        public uint st_uid;
        public uint st_gid;
        public int st_rdev;
        public long st_atime, st_atime_nsec;
        public long st_mtime, st_mtime_nsec;
        public long st_ctime, st_ctime_nsec;
        public long st_birthtime, st_birthtime_nsec;
        public long st_size, st_blocks;
        public int st_blksize;
        public uint st_flags, st_gen, st_lspare;
        public long st_qspare0, st_qspare1;
    }
}
