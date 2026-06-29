using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace AudioMixerWin
{
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            InitializeComponent();

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log("AppDomain", e.ExceptionObject as Exception);
            this.UnhandledException += (_, e) =>
            {
                Log("XamlUnhandled", e.Exception);
                e.Handled = true;
            };
        }

        private static void Log(string source, Exception? ex)
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "audiomixer_crash.log");
                File.AppendAllText(path,
                    $"[{DateTime.Now:O}] {source}\n{ex}\n\n");
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                _window = new MainWindow();
                _window.Activate();
            }
            catch (Exception ex)
            {
                Log("OnLaunched", ex);
                throw;
            }
        }
    }
}
