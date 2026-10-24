using System;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Codm;
using AssetStudio.Codm.Vfs;
using AssetStudio.Codm.Wwise;

namespace AssetStudioGUI
{
    /// <summary>
    /// Process-wide CODM state for the GUI: the config and the (lazily built) pak index,
    /// Wwise index and weapon catalog. Built once in the background; reopening the browser reuses it.
    /// On a 1.9.57 install the containers are opened next to the pak index, which then lists every
    /// bundle; those not unpacked yet are written to the unpack folder when loaded or exported.
    /// </summary>
    internal static class CodmSession
    {
        public static CodmConfig Config { get; private set; }
        public static PakIndex Index { get; private set; }
        public static WwiseIndex Wwise { get; private set; }
        public static WeaponCatalog Catalog { get; private set; }
        public static CabIndex Cabs { get; private set; }
        public static CharacterCatalog Characters { get; private set; }
        /// <summary>The 1.9.57 containers of the current config; null for a loose-pak install.</summary>
        public static CodmVfs Vfs { get; private set; }
        public static bool IsLoaded => Catalog != null;

        private static Task loadTask;
        private static readonly object gate = new object();
        // one lock per index: building the attachment index (minutes) must not hold up dependency loading
        private static readonly object cabGate = new object();
        private static readonly object containerGate = new object();
        private static PakIndex cabsBuiltFor, containersBuiltFor;

        public static CodmConfig LoadConfig()
        {
            var chosen = Properties.Settings.Default.codmConfigPath;
            var path = !string.IsNullOrEmpty(chosen) && System.IO.File.Exists(chosen) ? chosen : null;
            Config = CodmConfig.Load(path);
            CodmDiagnostics.Enabled = Config.Diagnostics;
            if (string.IsNullOrEmpty(Config.VgmstreamPath))
                Config.VgmstreamPath = WemTools.FindVgmstream() ?? "";
            return Config;
        }

        public static CodmConfig EnsureConfig()
        {
            return Config ?? LoadConfig();
        }

        /// <summary>Use another config file from now on (remembered in the user settings); null returns to the default lookup.</summary>
        public static CodmConfig SwitchConfig(string path)
        {
            Properties.Settings.Default.codmConfigPath = path ?? "";
            Properties.Settings.Default.Save();
            return LoadConfig();
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
                    Containers = null;
                    loadTask = null;
                }
                if (loadTask == null)
                {
                    loadTask = Task.Run(() =>
                    {
                        LoadConfig();
                        CodmVfs vfs = null;
                        if (CodmVfs.HasContainers(Config))
                        {
                            progress?.Invoke("CODM: opening the game containers (1.9.57)...");
                            vfs = CodmVfs.Open(Config, progress);
                        }
                        progress?.Invoke("CODM: indexing paks...");
                        var index = PakIndex.Build(Config, progress, vfs);
                        progress?.Invoke("CODM: indexing sound banks...");
                        var wwise = WwiseIndex.Build(Config, progress);
                        progress?.Invoke("CODM: resolving Wwise names...");
                        wwise.LoadNames(Config, WeaponCatalog.WeaponTokens(index), progress);
                        progress?.Invoke("CODM: building weapon catalog...");
                        var catalog = WeaponCatalog.Build(index, wwise, WeaponClassMap.Load(Config));
                        Characters = CharacterCatalog.Build(index);
                        var previous = Vfs;
                        Vfs = vfs;
                        Index = index;
                        Wwise = wwise;
                        Catalog = catalog;
                        if (previous != null && previous != vfs)
                            previous.Dispose();
                        progress?.Invoke($"CODM index ready: {index.Entries.Count} paks, {catalog.Weapons.Count} weapons, {wwise.Banks.Count} bank files");
                    });
                }
                return loadTask;
            }
        }

        public static ContainerIndex Containers { get; private set; }

        /// <summary>Container-path index of the attachment bundles; links magazines, barrels, stocks... to weapons.</summary>
        public static ContainerIndex EnsureContainerIndex(Action<string> progress = null, bool rebuild = false)
        {
            lock (containerGate)
            {
                var index = Index;
                var catalog = Catalog;
                if (Containers == null || containersBuiltFor != index || rebuild)
                {
                    var containers = ContainerIndex.Build(catalog.ComponentPaks, Config, progress, rebuild);
                    var linked = catalog.LinkComponentsByContainers(containers);
                    progress?.Invoke($"components: {linked} attachment links from container paths");
                    Containers = containers;
                    containersBuiltFor = index;
                }
                return Containers;
            }
        }

        /// <summary>CAB -> pak index for dependency loading; built on first use (minutes the first time, cached after).</summary>
        public static CabIndex EnsureCabIndex(Action<string> progress = null)
        {
            var cabs = Cabs;
            if (cabs != null && cabsBuiltFor == Index)
                return cabs;
            lock (cabGate)
            {
                var index = Index;
                if (Cabs == null || cabsBuiltFor != index)
                {
                    Cabs = CabIndex.Build(index, Config, progress);
                    cabsBuiltFor = index;
                }
                return Cabs;
            }
        }

        public static CodmExportService CreateExportService(Action<string> progress = null)
        {
            return new CodmExportService(Config, Index, Wwise, () => EnsureCabIndex(progress));
        }
    }
}
