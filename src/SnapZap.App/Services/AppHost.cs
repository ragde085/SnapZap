using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SnapZap.Core.Platform;

namespace SnapZap.App.Services;

/// <summary>
/// The <see cref="IAppHost"/> implementations (DESIGN §9, the fourth platform seam). The web
/// host is already listening by the time <c>Run</c> is called; <c>Run</c> owns the foreground
/// for the rest of the session and returns when the user is finished, at which point the
/// process shuts the server down and exits.
///
/// That contract is the whole point of the seam. Before it existed the app launched a browser
/// tab and then blocked forever: closing the tab left a server running with no window, no
/// console entry the user would recognise and no way back to it short of Task Manager.
/// </summary>
public static class AppHostFactory
{
    /// <summary>
    /// Picks the host for this machine. A native window wherever one can be had, a browser tab
    /// otherwise. <c>PC_NO_WINDOW=1</c> forces the browser path (useful when debugging the UI
    /// with real devtools, which the embedded WebView does not offer).
    /// </summary>
    public static IAppHost Create(IHostApplicationLifetime lifetime, ILoggerFactory logs)
    {
        // PhotinoAppHost (Services/PhotinoAppHost.cs) lives in its own file specifically so it —
        // and the Photino.NET package it needs — can be left out of a future net10.0-android
        // build entirely (AC-1.4, SnapZap.App.csproj). #if !ANDROID keeps this factory itself
        // compiling either way: with the symbol undefined (every build today), this branch is
        // live and behaviour is unchanged; the day something defines ANDROID, the branch
        // disappears along with the type it would have referenced, and the plain BrowserAppHost
        // below is what Create returns instead.
#if !ANDROID
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("PC_NO_WINDOW") is null)
            return new PhotinoAppHost(new BrowserAppHost(lifetime), logs.CreateLogger<PhotinoAppHost>());
#endif

        return new BrowserAppHost(lifetime);
    }
}

/// <summary>
/// The fallback, and the only host on the dev Mac: open the default browser at the app's URL
/// and hold the foreground until the process is asked to stop.
/// </summary>
public sealed class BrowserAppHost(IHostApplicationLifetime lifetime) : IAppHost
{
    public void Run(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Headless, or no registered default browser. The URL is on the console either way.
        }

        // There is no "user closed the tab" signal to wait on, so the session ends when the
        // process is asked to: Ctrl+C, a console close, or a service stop.
        lifetime.ApplicationStopping.WaitHandle.WaitOne();
    }
}

/// <summary>
/// The console window SnapZap is handed when it is double-clicked.
///
/// The app cannot simply be built as a GUI-subsystem binary to avoid it: OutputType=WinExe
/// makes the SDK drop Blazor's own scripts from the output (the note in SnapZap.App.csproj has
/// the details), which is a far worse bug than a stray window. So the console is disposed of
/// here instead — and only when it is ours to dispose of.
/// </summary>
static class ConsoleWindow
{
    const int SW_HIDE = 0;
    const int SW_SHOW = 5;

    /// <summary>
    /// Hides the console if this process is the only one attached to it, which is true exactly
    /// when Windows created it for us — i.e. a double-click. Launched from an existing terminal
    /// the shell is attached too, and that console belongs to the developer reading the log.
    /// Returns whether anything was hidden.
    /// </summary>
    public static bool HideIfOwned()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var console = GetConsoleWindow();
            if (console == IntPtr.Zero) return false;      // already detached, or no console at all

            // The count is what's wanted, not the list; a one-element buffer is enough to ask.
            var buffer = new uint[1];
            if (GetConsoleProcessList(buffer, 1) != 1) return false;

            return ShowWindow(console, SW_HIDE);
        }
        catch
        {
            return false;
        }
    }

    public static void Show()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var console = GetConsoleWindow();
            if (console != IntPtr.Zero) ShowWindow(console, SW_SHOW);
        }
        catch { /* best effort */ }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    static extern uint GetConsoleProcessList(uint[] lpdwProcessList, uint dwProcessCount);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

/// <summary>
/// A message box, for the two moments a windowed app has to say something before the UI it
/// would have said it in exists. Everything else belongs in the app.
/// </summary>
public static class NativeDialog
{
    const uint MB_OK = 0x0;
    const uint MB_ICONWARNING = 0x30;
    const uint MB_ICONERROR = 0x10;
    const uint MB_TOPMOST = 0x40000;

    public static void Warn(string message) => Show(message, MB_ICONWARNING);

    public static void Error(string message) => Show(message, MB_ICONERROR);

    static void Show(string message, uint icon)
    {
        // A WinExe has nowhere to print. On anything else the console is the better channel,
        // and there may be no window server at all.
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(message);
            return;
        }

        try { MessageBoxW(IntPtr.Zero, message, "SnapZap", MB_OK | icon | MB_TOPMOST); }
        catch { /* a diagnostic must never become the failure it is reporting */ }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
