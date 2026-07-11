using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Threading.Tasks;
using TextTemplateManager.Common;
using TextTemplateManager.Data;
using TextTemplateManager.Helpers;
using TextTemplateManager.Services.Pasting.Strategies;
using TextTemplateManager.Services.System;
using WinRT.Interop;


namespace TextTemplateManager
{
    public partial class App : Application
    {
        public static Window MainWindow { get; private set; } = null!;
        private static bool _isClosingFromTray = false;

        // Keep references to prevent Garbage Collection
        private HotkeyListener _hotkeyListener;
        private PasteWindow? _pasteWindow;
        private TrayIconService _trayService;
        private DispatcherQueue _uiDispatcher;

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



            // "X" hides instead of closing.
            appWindow.Closing += (s, e) =>
            {
                if (!_isClosingFromTray)
                {
                    e.Cancel = true;
                    MainWindow.Hide();
                }
            };

            MainWindow.Activate();
        }

        private void OnGlobalHotkeyPressed()
        {
            WindowHelper.CaptureTargetContext();

            MainWindow.DispatcherQueue.TryEnqueue(() => {
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

        /// <summary>Quit: remove the tray icon, then hard-exit. Uses Environment.Exit because
        /// Application.Current.Exit()'s teardown throws a stowed exception (0xc000027b) here.
        /// Persist pending state before calling.</summary>
        public void Shutdown()
        {
            _isClosingFromTray = true;
            try { _trayService?.Dispose(); } catch { }
            Environment.Exit(0);
        }
    }
}