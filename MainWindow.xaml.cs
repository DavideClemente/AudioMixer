using System;
using AudioMixerWin.Core.ViewModels;
using AudioMixerWin.Core.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace AudioMixerWin
{
    public sealed partial class MainWindow : Window
    {
        private const double MinPaneWidth = 200;
        private const double MaxPaneWidth = 400;

        public MainViewModel ViewModel { get; } = new();

        private readonly MainPage _mainPage;
        private readonly SettingsPage _settingsPage;

        private bool _isDraggingSplitter;
        private double _dragStartX;
        private double _dragStartWidth;

        public MainWindow()
        {
            InitializeComponent();

            var hwnd = WindowNative.GetWindowHandle(this);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                Win32Interop.GetWindowIdFromWindow(hwnd));
            appWindow.SetIcon("Assets\\AudioMixer.ico");

            _mainPage = new MainPage(ViewModel);
            _settingsPage = new SettingsPage(ViewModel);

            ContentFrame.Content = _mainPage;

            NavView.OpenPaneLength = ViewModel.NavPaneWidth;
            PositionSplitter(ViewModel.NavPaneWidth);
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
