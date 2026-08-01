using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using TextTemplateManager.Data;
using TextTemplateManager.Helpers;
using TextTemplateManager.Services.System;
using WinRT.Interop;


namespace TextTemplateManager
{
    public partial class App : Application
    {
        public static Window MainWindow { get; private set; } = null!;
        private static bool _isClosingFromTray = false;

        // Keep references to prevent Garbage Collection
        private HotkeyListener _hotkeyListener = null!;   // set in OnLaunched
        private PasteWindow? _pasteWindow;
        private TrayIconService _trayService = null!;     // set in OnLaunched
        private DispatcherQueue _uiDispatcher = null!;    // set in OnLaunched
        private BrowserConnector? _connector;
        private AppConnectorData? _connectorData;
        private SingleInstance _singleInstance = null!;   // set in OnLaunched

        // Pipe message a bare second launch sends to just surface the running window (no file).
        private const string ActivateSignal = "__activate__";

        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // Single-instance guard. A .ttmdata opened from Explorer starts another ttm.exe with the
            // file as an argument; hand it to the running instance and exit rather than run twice.
            _uiDispatcher = DispatcherQueue.GetForCurrentThread();
            _singleInstance = new SingleInstance();
            string? fileArg = GetTtmDataArg();
            if (!_singleInstance.TryAcquire())
            {
                // A redundant hidden autostart (no file) shouldn't surface the running window — just exit.
                if (fileArg != null)
                    SingleInstance.SendToRunningInstance(fileArg);
                else if (!IsHiddenLaunch())
                    SingleInstance.SendToRunningInstance(ActivateSignal);
                Environment.Exit(0);
                return;
            }
            _singleInstance.MessageReceived += OnOtherInstanceMessage;
            _singleInstance.StartServer();

            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            await DataNode.Instance.InitializeAsync();

            _hotkeyListener = new HotkeyListener();
            _hotkeyListener.HotkeyPressed += OnGlobalHotkeyPressed;
            _hotkeyListener.Register(DataNode.Instance.CurrentSettings.PasteWindowHotkey);

            MainWindow = new Window();
            MainWindow.Content = new MainPage();
            MainWindow.Title = "Text Template Manager";

            IntPtr hwnd = WindowNative.GetWindowHandle(MainWindow);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.SetIcon("Assets/AppIcon.ico");

            WindowHelper.SetWindowMinSize(hwnd, 720, 560);   // don't clip the panes


            _trayService = new TrayIconService(MainWindow);

            ApplyBrowserConnectorSettings();   // start the loopback connector if enabled



            // "X" hides instead of closing.
            appWindow.Closing += (s, e) =>
            {
                if (!_isClosingFromTray)
                {
                    e.Cancel = true;
                    MainWindow.Hide();
                    (MainWindow.Content as MainPage)?.ClearSearch();   // reopen with a fresh search
                }
            };

            // A hidden autostart (Windows login with --hidden) stays in the tray: skip Activate so the
            // window is never shown. A .ttmdata to open overrides this and surfaces the window below.
            bool startHidden = IsHiddenLaunch() && fileArg == null;
            if (!startHidden)
                MainWindow.Activate();

            // A .ttmdata passed to this (first) launch: add it as a sync source once the UI is up.
            if (fileArg != null)
                (MainWindow.Content as MainPage)?.HandleOpenTtmDataFile(fileArg);

            // Prewarm the Quick Paste window while idle so the first hotkey open isn't cold (builds its
            // UI + tree and starts the WebView2 preview). It stays hidden until the hotkey is pressed.
            _uiDispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => EnsurePasteWindow().Prewarm());
        }

        // ---- File association (.ttmdata) ----

        // The path Explorer passes when opening a .ttmdata, or null for a normal launch.
        private static string? GetTtmDataArg()
        {
            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (a.EndsWith(".ttmdata", StringComparison.OrdinalIgnoreCase))
                {
                    try { if (System.IO.File.Exists(a)) return System.IO.Path.GetFullPath(a); }
                    catch { }
                }
            }
            return null;
        }

        // True when this process was launched with the hidden-start flag (the autostart entry appends it).
        public static bool IsHiddenLaunch()
        {
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, StartupManager.HiddenFlag, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // Another launch reached the running instance: surface the window and open its file (if any).
        private void OnOtherInstanceMessage(string message)
        {
            _uiDispatcher?.TryEnqueue(() =>
            {
                ShowMainWindow();
                if (message != ActivateSignal)
                    (MainWindow.Content as MainPage)?.HandleOpenTtmDataFile(message);
            });
        }

        /// <summary>True while the Quick Paste window is on screen. It is kept warm but hidden between
        /// uses, so this is "the user is mid-paste" — never a moment to close the app for an update.</summary>
        public bool PasteWindowVisible
        {
            get
            {
                try
                {
                    return _pasteWindow != null
                        && WindowHelper.IsWindowVisible(WindowNative.GetWindowHandle(_pasteWindow));
                }
                catch { return false; }
            }
        }

        /// <summary>Tray balloon — the fallback channel when toast notifications aren't registered.</summary>
        public void ShowTrayBalloon(string title, string message) => _trayService?.ShowBalloon(title, message);

        /// <summary>Bring the main window up from the tray (e.g. the user pressed a toast's "Open app").</summary>
        public static void SurfaceMainWindow() => ShowMainWindow();

        // Restore + foreground the main window (it may be hidden in the tray). Mirrors the tray's Open.
        private static void ShowMainWindow()
        {
            MainWindow.Show();
            IntPtr hWnd = WindowNative.GetWindowHandle(MainWindow);
            WindowHelper.ShowWindow(hWnd, 9);          // SW_RESTORE
            WindowHelper.SetForegroundWindow(hWnd);
            MainWindow.Activate();
        }

        private void OnGlobalHotkeyPressed()
        {
            WindowHelper.CaptureTargetContext();

            MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                // Reuse the warm instance (hidden between uses) so reopen is instant and immediately
                // ready for a single-key press — no cold rebuild of the tree / WebView preview.
                EnsurePasteWindow().ShowForPaste();
            });
        }

        // The Quick Paste window, kept warm (hidden) between uses. A paste destroys it (so it can't
        // steal the Ctrl+V) and OnPasteWindowClosed rebuilds a fresh prewarmed one; the X/Esc only hide.
        private PasteWindow EnsurePasteWindow()
        {
            if (_pasteWindow == null)
            {
                _pasteWindow = new PasteWindow();
                _pasteWindow.Closed += OnPasteWindowClosed;
            }
            return _pasteWindow;
        }

        // A paste closed (destroyed) the window: rebuild a fresh hidden, prewarmed instance for the next
        // hotkey, mirroring the launch prewarm. Low priority so it never contends with the paste's
        // foreground handoff; if the user reopens first, EnsurePasteWindow just prewarms that instance.
        private void OnPasteWindowClosed(object sender, WindowEventArgs e)
        {
            _pasteWindow = null;
            _uiDispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => EnsurePasteWindow().Prewarm());
        }

        public void UpdateGlobalHotkey(string hotkey)
        {
            _hotkeyListener?.Register(hotkey);
        }

        /// <summary>Starts/stops/restarts the browser connector to match the current settings.
        /// Safe to call at launch and whenever the connector settings change.</summary>
        public void ApplyBrowserConnectorSettings()
        {
            var s = DataNode.Instance.CurrentSettings;
            if (!s.BrowserConnectorEnabled) { _connector?.Stop(); return; }

            // Generate the shared token on first enable and persist it.
            if (string.IsNullOrEmpty(s.BrowserConnectorToken))
            {
                s.BrowserConnectorToken = Guid.NewGuid().ToString("N");
                _ = StorageService.SaveSettingsAsync(s);
            }

            if (_connectorData == null)
            {
                _connectorData = new AppConnectorData();
                DataNode.Instance.TreeChanged += RebuildConnectorSnapshot;
                DataNode.Instance.DataSaved += RebuildConnectorSnapshot;
            }
            _connectorData.Rebuild();   // on the UI thread here

            _connector ??= new BrowserConnector(_connectorData);
            try { _connector.Start(s.BrowserConnectorPort, s.BrowserConnectorToken); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Connector] start failed: {ex.Message}"); }
        }

        // TreeChanged/DataSaved can fire off the UI thread; marshal the snapshot rebuild back.
        private void RebuildConnectorSnapshot() =>
            _uiDispatcher?.TryEnqueue(() => { try { _connectorData?.Rebuild(); } catch { } });

        // Last-resort net: an unhandled exception on the UI thread would terminate the app (async-void
        // handlers make this easy to trip). Log it and keep running so the user doesn't lose their
        // session; the crash log preserves the stack for diagnosis. Handled=true stops the shutdown.
        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            LogCrash("UnhandledException", e.Exception);
            e.Handled = true;
        }

        private static void LogCrash(string source, Exception? ex)
        {
            System.Diagnostics.Debug.WriteLine($"[{source}] {ex}");
            try
            {
                System.IO.File.AppendAllText(
                    StorageService.GetCrashLogPath(),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { /* logging must never throw */ }
        }

        /// <summary>Quit: remove the tray icon, then hard-exit. Uses Environment.Exit because
        /// Application.Current.Exit()'s teardown throws a stowed exception (0xc000027b) here.
        /// Persist pending state before calling.</summary>
        public void Shutdown()
        {
            _isClosingFromTray = true;
            // Unpackaged toast registration is process-wide COM state; release it before the hard exit.
            try { (MainWindow?.Content as MainPage)?.ShutdownUpdates(); } catch { }
            try { _connector?.Stop(); } catch { }
            try { _singleInstance?.Dispose(); } catch { }
            try { _trayService?.Dispose(); } catch { }
            Environment.Exit(0);
        }
    }
}