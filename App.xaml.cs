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

        public App()
        {
            InitializeComponent();
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
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


            _uiDispatcher = DispatcherQueue.GetForCurrentThread();
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

            MainWindow.Activate();
        }

        private void OnGlobalHotkeyPressed()
        {
            WindowHelper.CaptureTargetContext();

            MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                if (_pasteWindow == null)
                {
                    _pasteWindow = new PasteWindow();
                    _pasteWindow.Closed += (s, e) => _pasteWindow = null;
                }

                IntPtr hWnd = WindowNative.GetWindowHandle(_pasteWindow);
                WindowHelper.SetForegroundWindow(hWnd);   // input recipient before Activate
                _pasteWindow.Activate();
            });
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

        /// <summary>Quit: remove the tray icon, then hard-exit. Uses Environment.Exit because
        /// Application.Current.Exit()'s teardown throws a stowed exception (0xc000027b) here.
        /// Persist pending state before calling.</summary>
        public void Shutdown()
        {
            _isClosingFromTray = true;
            try { _connector?.Stop(); } catch { }
            try { _trayService?.Dispose(); } catch { }
            Environment.Exit(0);
        }
    }
}