using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using TextTemplateManager.Helpers;
using WinRT.Interop;

namespace TextTemplateManager.Services.System
{
    public sealed class TrayIconService : IDisposable
    {
        private readonly TaskbarIcon _trayIcon;
        private readonly DispatcherQueue _dispatcher;
        private readonly Window _mainWindow;

        public IRelayCommand OpenCommand { get; }
        public IRelayCommand ExitCommand { get; }


        public TrayIconService(Window mainWindow)
        {
            _mainWindow = mainWindow;
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            OpenCommand = new RelayCommand(OpenWindow);
            ExitCommand = new RelayCommand(ExitApp);



            _trayIcon = new TaskbarIcon
            {
                IconSource = new BitmapImage(
                    new Uri("ms-appx:///Assets/AppIcon.ico")
                ),
                ToolTipText = "Text Template Manager",
                DoubleClickCommand = OpenCommand,   // double-click the tray icon to open the window
            };


            var menu = new MenuFlyout();

            menu.Items.Add(new MenuFlyoutItem
            {
                Text = "Open",
                Command = OpenCommand
            });

            menu.Items.Add(new MenuFlyoutItem
            {
                Text = "Exit App",
                Command = ExitCommand
            });

            _trayIcon.ContextFlyout = menu;
            _trayIcon.ForceCreate();
        }

        private void OpenWindow()
        {
            _dispatcher.TryEnqueue(() =>
            {
                _mainWindow.Show();

                IntPtr hWnd = WindowNative.GetWindowHandle(_mainWindow);
                WindowHelper.ShowWindow(hWnd, 9);
                WindowHelper.SetForegroundWindow(hWnd);

                _mainWindow.Activate();
            });
        }

        private void ExitApp()
        {
            _dispatcher.TryEnqueue(async () =>
            {
                try { await TextTemplateManager.Data.DataNode.Instance.SaveDataAsync(); } catch { }
                (Application.Current as TextTemplateManager.App)?.Shutdown();
            });
        }

        /// <summary>Balloon fallback for when Windows App SDK toasts aren't available. It goes through
        /// Shell_NotifyIcon, which needs no notification COM registration, so it still reaches the user
        /// when <see cref="UpdateNotifier"/> failed to register.</summary>
        public void ShowBalloon(string title, string message)
        {
            _dispatcher.TryEnqueue(() =>
            {
                try { _trayIcon.ShowNotification(title, message, H.NotifyIcon.Core.NotificationIcon.Info); }
                catch { /* notifications are never worth breaking the app over */ }
            });
        }

        public void Dispose()
        {
            _trayIcon?.Dispose();
        }
    }
}
