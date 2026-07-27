using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Photino.NET;
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
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("PC_NO_WINDOW") is null)
            return new PhotinoAppHost(new BrowserAppHost(lifetime), logs.CreateLogger<PhotinoAppHost>());

        return new BrowserAppHost(lifetime);
    }
}

/// <summary>
/// An embedded WebView2 window (Photino). This is what "SnapZap" is to the person using it:
/// one window in the taskbar that they close when they are done, no browser tab and no console.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PhotinoAppHost(IAppHost fallback, ILogger<PhotinoAppHost> log) : IAppHost
{
    public void Run(string url)
    {
        // From here on the window is the app, so the console SnapZap was given on double-click
        // is just a black rectangle the user is invited to close mid-export. Hidden only if we
        // own it — started from a terminal, it belongs to whoever is reading the log.
        var hidConsole = ConsoleWindow.HideIfOwned();

        // The window is created on a dedicated STA thread rather than on whichever thread the
        // startup continuation landed on. WebView2 requires an STA apartment, and top-level
        // statements cannot carry [STAThread] — so rather than depend on the main thread's
        // apartment, own one. It also keeps the native message loop off a thread-pool thread.
        Exception? failure = null;
        var ui = new Thread(() =>
        {
            try
            {
                var (width, height) = DefaultWindowSize();

                var window = new PhotinoWindow()
                    .SetTitle("SnapZap")
                    .SetUseOsDefaultSize(false)
                    .SetSize(width, height)
                    .SetUseOsDefaultLocation(false)
                    .Center()
                    .SetResizable(true)
                    .SetContextMenuEnabled(false)   // a browser context menu in a desktop app reads as a bug
                    .SetLogVerbosity(0);

                var icon = Path.Combine(AppContext.BaseDirectory, "wwwroot", "favicon.ico");
                if (File.Exists(icon)) window.SetIconFile(icon);

                window.Load(new Uri(url)).WaitForClose();
            }
            catch (Exception ex)
            {
                // Almost always a missing WebView2 runtime. Recorded, not thrown: the session
                // continues in a browser rather than dying at the point the window would have
                // appeared, which from the outside is indistinguishable from a crash.
                failure = ex;
            }
        })
        { Name = "SnapZap window", IsBackground = false };

        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();

        if (failure is null) return;


        // No window means the console is the only handle the user has on this process — it is
        // now the one place Ctrl+C can end a session that would otherwise have to be killed.
        if (hidConsole) ConsoleWindow.Show();

        log.LogWarning(failure, "Native window unavailable; falling back to the default browser");
        NativeDialog.Warn(
            "SnapZap could not open its own window because the Microsoft Edge WebView2 runtime " +
            "is missing.\n\nSnapZap will open in your browser instead. It stops when you close " +
            "this message... so leave it open while you work, or install WebView2 and restart.");
        fallback.Run(url);
    }

    /// <summary>
    /// A first-run window size taken from the monitor rather than hard-coded.
    ///
    /// Photino sizes in physical pixels, and the window is DPI-aware, so a fixed 1440×920 is a
    /// different window on every machine: reasonable at 1080p, and a small box in the middle of
    /// a 4K panel at 150% — where it works out to 960×613 of usable layout. This app is a wall
    /// of thumbnails; it wants the space. Clamped at both ends so a very large monitor doesn't
    /// produce a window nobody asked for, and a small one still gets a usable grid.
    /// </summary>
    static (int Width, int Height) DefaultWindowSize()
    {
        var (areaW, areaH) = WorkArea();
        return (
            Math.Clamp((int)(areaW * 0.82), 1100, 2400),
            Math.Clamp((int)(areaH * 0.86), 720, 1500));
    }

    /// <summary>
    /// The primary display's true resolution, in the same physical pixels
    /// <c>PhotinoWindow.SetSize</c> works in.
    ///
    /// EnumDisplaySettings, not SystemParametersInfo(SPI_GETWORKAREA), and the difference is
    /// not cosmetic: this runs before Photino creates the window, so the thread is still
    /// DPI-unaware and the SPI call comes back in virtualised 96-DPI coordinates — 2560×1440 on
    /// a 3840×2160 panel at 150%. Feeding those to a SetSize that means physical pixels shrank
    /// the window by the scale factor again, on exactly the high-DPI machines that had the most
    /// room to give. The display mode is reported in real pixels whatever the caller's
    /// awareness, so there is nothing to correct for.
    /// </summary>
    static (int Width, int Height) WorkArea()
    {
        try
        {
            var mode = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref mode) &&
                mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0)
            {
                // Room for the taskbar, whichever edge it is on and however tall it is. The
                // fractions applied by the caller are well under 1, so this only has to be
                // approximately right.
                return ((int)mode.dmPelsWidth, (int)(mode.dmPelsHeight * 0.94));
            }
        }
        catch { /* fall through to a size that is reasonable on a 1080p panel */ }

        return (1440, 920);
    }

    const int ENUM_CURRENT_SETTINGS = -1;

    // Only the two dm* fields below are read; the rest exists to size the struct correctly, as
    // EnumDisplaySettings validates dmSize against the real DEVMODEW layout.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DevMode devMode);
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
