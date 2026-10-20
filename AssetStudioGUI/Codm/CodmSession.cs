using System;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Codm;
using AssetStudio.Codm.Wwise;

namespace AssetStudioGUI
{
    /// <summary>
    /// Process-wide CODM state for the GUI: the config and the (lazily built) pak index,
    /// Wwise index and weapon catalog. Built once in the background; reopening the browser reuses it.
    /// </summary>
    internal static class CodmSession
    {
        public static CodmConfig Config { get; private set; }
        public static PakIndex Index { get; private set; }
        public static WwiseIndex Wwise { get; private set; }
        public static WeaponCatalog Catalog { get; private set; }
        public static CabIndex Cabs { get; private set; }
        public static bool IsLoaded => Catalog != null;

        private static Task loadTask;
        private static readonly object gate = new object();

        public static CodmConfig LoadConfig()
        {
            Config = CodmConfig.Load();
            CodmDiagnostics.Enabled = Config.Diagnostics;
            if (string.IsNullOrEmpty(Config.VgmstreamPath))
                Config.VgmstreamPath = WemTools.FindVgmstream() ?? "";
            return Config;
        }

        public static CodmConfig EnsureConfig()
        {
            return Config ?? LoadConfig();
        }

        public static Task EnsureLoadedAsync(Action<string> progress, bool force = false)
        {
            lock (gate)
            {
                if (force || (loadTask != null && loadTask.IsFaulted))
                {
                    Index = null;
                    Wwise = null;
                    Catalog = null;
                    Cabs = null;
                    loadTask = null;
                }
                if (loadTask == null)
                {
                    loadTask = Task.Run(() =>
                    {
                        LoadConfig();
                        progress?.Invoke("CODM: indexing paks...");
                        var index = PakIndex.Build(Config, progress);
                        progress?.Invoke("CODM: indexing sound banks...");
                        var wwise = WwiseIndex.Build(Config, progress);
                        progress?.Invoke("CODM: building weapon catalog...");
                        var catalog = WeaponCatalog.Build(index, wwise, WeaponClassMap.Load(Config));
                        Index = index;
                        Wwise = wwise;
                        Catalog = catalog;
                        progress?.Invoke($"CODM index ready: {index.Entries.Count} paks, {catalog.Weapons.Count} weapons, {wwise.Banks.Count} bank files");
                    });
                }
                return loadTask;
            }
        }

        /// <summary>CAB -> pak index for dependency loading; built on first use (a minute the first time, cached after).</summary>
        public static CabIndex EnsureCabIndex(Action<string> progress = null)
        {
            lock (gate)
            {
                if (Cabs == null)
                    Cabs = CabIndex.Build(Index, Config, progress);
                return Cabs;
            }
        }

        public static CodmExportService CreateExportService(Action<string> progress = null)
        {
            return new CodmExportService(Config, Index, Wwise, () => EnsureCabIndex(progress));
        }
    }
}
