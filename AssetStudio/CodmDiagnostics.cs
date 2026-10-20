namespace AssetStudio
{
    /// <summary>
    /// Switch for the verbose per-field reader diagnostics that were used to reverse engineer the
    /// CoD Mobile engine changes. Off by default; the GUI and CLI set it from codm.config.json.
    /// </summary>
    public static class CodmDiagnostics
    {
        public static bool Enabled { get; set; }

        /// <summary>Log at Info level only when diagnostics are enabled.</summary>
        public static void Log(string message)
        {
            if (Enabled)
                Logger.Info(message);
        }

        /// <summary>Log at Warning level only when diagnostics are enabled.</summary>
        public static void Warn(string message)
        {
            if (Enabled)
                Logger.Warning(message);
        }
    }
}
