using System;
using System.Runtime.InteropServices;
using AudioMixerWin.Core.ViewModels;
using AudioMixerWin.Core.Views;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT.Interop;

namespace AudioMixerWin
{
    public sealed partial class MainWindow : Window
    {
        private const int WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint IMAGE_ICON = 1;

        private const int GCLP_HICON = -14;
        private const int GCLP_HICONSM = -34;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [DllImport("user32.dll")]
        private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private const double MinPaneWidth = 200;
        private const double MaxPaneWidth = 400;

        public MainViewModel ViewModel { get; } = new();

        private readonly MainPage _mainPage;
        private readonly SettingsPage _settingsPage;
        private readonly AppWindow _appWindow;
        private readonly TaskbarIcon _trayIcon;
        private readonly string _iconPath;
        private readonly IntPtr _hwnd;

        private bool _isDraggingSplitter;
        private double _dragStartX;
        private double _dragStartWidth;

        public MainWindow()
        {
            InitializeComponent();

            var hwnd = WindowNative.GetWindowHandle(this);
            _iconPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()!.Location)!,
                "Assets", "AudioMixer.ico");
            _hwnd = hwnd;
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.SetIcon(_iconPath);
            _appWindow.Closing += OnWindowClosing;
            _appWindow.Changed += OnAppWindowChanged;
            ConfigureTitleBar();

            this.Activated += OnFirstActivated;

            _trayIcon = new TaskbarIcon
            {
                ToolTipText = "Audio Mixer",
                Icon = new System.Drawing.Icon(_iconPath),
                LeftClickCommand = new RelayCommand(RestoreWindow),
                DoubleClickCommand = new RelayCommand(RestoreWindow),
            };
            _trayIcon.ForceCreate(enablesEfficiencyMode: false);

            _mainPage = new MainPage(ViewModel);
            _settingsPage = new SettingsPage(ViewModel);

            ContentFrame.Content = _mainPage;

            NavView.OpenPaneLength = ViewModel.NavPaneWidth;
            PositionSplitter(ViewModel.NavPaneWidth);
        }

        private void ConfigureTitleBar()
        {
            var titleBar = _appWindow.TitleBar;
            var bg = Color.FromArgb(255, 10, 10, 10);
            var fg = Color.FromArgb(255, 230, 230, 230);
            var fgMuted = Color.FromArgb(255, 80, 80, 80);
            var hover = Color.FromArgb(255, 28, 28, 28);
            var pressed = Color.FromArgb(255, 18, 18, 18);
            titleBar.BackgroundColor = bg;
            titleBar.ForegroundColor = fg;
            titleBar.InactiveBackgroundColor = bg;
            titleBar.InactiveForegroundColor = fgMuted;
            titleBar.ButtonBackgroundColor = bg;
            titleBar.ButtonForegroundColor = fg;
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonHoverForegroundColor = fg;
            titleBar.ButtonPressedBackgroundColor = pressed;
            titleBar.ButtonPressedForegroundColor = fgMuted;
            titleBar.ButtonInactiveBackgroundColor = bg;
            titleBar.ButtonInactiveForegroundColor = fgMuted;
        }

        private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            args.Cancel = true;

            var dialog = new ContentDialog
            {
                Title = "Close Audio Mixer",
                Content = "Do you want to close the app or minimize it to the system tray?",
                PrimaryButtonText = "Close",
                SecondaryButtonText = "Minimize to Tray",
                CloseButtonText = "Cancel",
                XamlRoot = Content.XamlRoot,
                DefaultButton = ContentDialogButton.Secondary,
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
                ExitApp();
            else if (result == ContentDialogResult.Secondary)
                MinimizeToTray();
        }

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidPresenterChange || !args.DidSizeChange && !args.DidPositionChange)
            {
                if (_appWindow.Presenter is OverlappedPresenter p &&
                    p.State == OverlappedPresenterState.Minimized)
                {
                    MinimizeToTray();
                }
            }
        }

        private void MinimizeToTray() => WindowExtensions.Hide(this);

        private void RestoreWindow() => WindowExtensions.Show(this);

        private void ExitApp()
        {
            _trayIcon.Dispose();
            Application.Current.Exit();
        }

        private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
        {
            this.Activated -= OnFirstActivated;
            var hIconBig = LoadImage(IntPtr.Zero, _iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
            var hIconSmall = LoadImage(IntPtr.Zero, _iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
            SendMessage(_hwnd, WM_SETICON, ICON_BIG, hIconBig);
            SendMessage(_hwnd, WM_SETICON, ICON_SMALL, hIconSmall);
            SetClassLongPtr(_hwnd, GCLP_HICON, hIconBig);
            SetClassLongPtr(_hwnd, GCLP_HICONSM, hIconSmall);
        }

        private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            ContentFrame.Content = args.IsSettingsSelected ? _settingsPage : _mainPage;
        }

        private void PositionSplitter(double paneWidth) =>
            PaneSplitter.Margin = new Thickness(paneWidth - PaneSplitter.Width / 2, 0, 0, 0);

        private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e) =>
            PaneSplitter.Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.3 };

        private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingSplitter)
                PaneSplitter.Background = new SolidColorBrush(Colors.Transparent);
        }

        private void OnSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isDraggingSplitter = true;
            _dragStartX = e.GetCurrentPoint(Content).Position.X;
            _dragStartWidth = NavView.OpenPaneLength;
            PaneSplitter.CapturePointer(e.Pointer);
        }

        private void OnSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingSplitter)
                return;

            var currentX = e.GetCurrentPoint(Content).Position.X;
            var newWidth = Math.Clamp(_dragStartWidth + (currentX - _dragStartX), MinPaneWidth, MaxPaneWidth);
            NavView.OpenPaneLength = newWidth;
            PositionSplitter(newWidth);
        }

        private void OnSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            PaneSplitter.ReleasePointerCapture(e.Pointer);
            EndSplitterDrag();
        }

        private void OnSplitterPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            // PointerCaptureLost means capture is already gone; do not call
            // ReleasePointerCapture here as it would be redundant/could throw.
            EndSplitterDrag();
        }

        private void EndSplitterDrag()
        {
            if (!_isDraggingSplitter)
                return;

            _isDraggingSplitter = false;
            PaneSplitter.Background = new SolidColorBrush(Colors.Transparent);
            ViewModel.NavPaneWidth = NavView.OpenPaneLength;
        }
    }
}
