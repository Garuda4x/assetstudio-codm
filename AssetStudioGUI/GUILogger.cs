using AssetStudio;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace AssetStudioGUI
{
    class GUILogger : ILogger
    {
        public bool ShowErrorMessage = true;
        private Action<string> action;
        private readonly string logFilePath;

        public GUILogger(Action<string> action)
        {
            this.action = action;
            logFilePath = Path.Combine(Application.StartupPath, "assetstudio.log");
        }

        public void Log(LoggerEvent loggerEvent, string message)
        {
            // Always write to VS Output window when debugging
            Debug.WriteLine($"[AssetStudio][{loggerEvent}] {message}");

            if (loggerEvent == LoggerEvent.Warning || loggerEvent == LoggerEvent.Error)
            {
                try
                {
                    File.AppendAllText(logFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{loggerEvent}] {message}{Environment.NewLine}");
                }
                catch
                {
                    // ignore log I/O failures
                }
            }

            switch (loggerEvent)
            {
                case LoggerEvent.Error:
                    if (ShowErrorMessage)
                    {
                        MessageBox.Show(message);
                    }
                    break;
                default:
                    action(message);
                    break;
            }

        }
    }
}
