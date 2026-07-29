using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Photino.NET;
using SnapZap.Core.Platform;

namespace SnapZap.App.Services;

/// <summary>
/// An embedded WebView2 window (Photino). This is what "SnapZap" is to the person using it:
/// one window in the taskbar that they close when they are done, no browser tab and no console.
///
/// Split into its own file (AC-1.4, Android port prep) so the one type that depends on
/// Photino.NET can be excluded from compilation — together with the package itself — for a
/// future net10.0-android build of this project, without touching anything else in
/// <c>AppHost.cs</c>. See the <c>Compile Remove</c>/<c>PackageReference</c> conditions in
/// <c>SnapZap.App.csproj</c> and the <c>#if !ANDROID</c> guard in
/// <see cref="AppHostFactory.Create"/>.
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
