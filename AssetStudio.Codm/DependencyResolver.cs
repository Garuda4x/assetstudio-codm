using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetStudio.Codm
{
    public sealed class DependencyReport
    {
        public List<PakEntry> Loaded { get; } = new List<PakEntry>();
        public List<string> Unresolved { get; } = new List<string>();
        public int Rounds { get; set; }
        public bool HitLimit { get; set; }
    }

    /// <summary>
    /// Loads the bundles that already loaded files reference through m_Externals (shared animation
    /// packs, animator controllers, avatars, shared materials...). Works in rounds until nothing new is
    /// referenced, a depth limit, or a pak limit is reached.
    /// </summary>
    public static class DependencyResolver
    {
        /// <summary>External names that never live in a pak (resource files, editor defaults).</summary>
        private static bool IsSkippable(string name)
        {
            return string.IsNullOrEmpty(name)
                   || name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith("unity_builtin", StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith("unity default", StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith("library/", StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith("resources/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Names referenced by the loaded files that are not loaded themselves.</summary>
        public static List<string> MissingExternals(AssetsManager am)
        {
            var loaded = new HashSet<string>(am.assetsFileList.Select(f => f.fileName), StringComparer.OrdinalIgnoreCase);
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in am.assetsFileList)
            {
                foreach (var ext in file.m_Externals)
                {
                    if (IsSkippable(ext.fileName) || loaded.Contains(ext.fileName))
                        continue;
                    missing.Add(ext.fileName);
                }
            }
            return missing.ToList();
        }

        /// <summary>Bundles that are referenced by every weapon but never contribute to a model/animation export.</summary>
        public static readonly string[] DefaultSkipPrefixes = { "_codm_shader", "cod_effects/", "scenes/", "_s_", "textures/ui", "cod_prefabs/ngui" };

        /// <summary>Lower rank loads first: shared animations and controllers before everything else.</summary>
        private static int Priority(PakEntry p)
        {
            var path = p.Name.Path;
            if (path.IndexOf("animation", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("controller", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("avatar", StringComparison.OrdinalIgnoreCase) >= 0)
                return 0;
            if (path.StartsWith("cod_models/weapons", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (path.StartsWith("_codm_", StringComparison.OrdinalIgnoreCase))
                return 3;
            return 2;
        }

        /// <summary>Load the paks that contain the given serialized file names (targeted, no rounds).</summary>
        public static List<PakEntry> LoadCabs(AssetsManager am, CabIndex cabs, CodmConfig cfg, IEnumerable<string> cabNames, Action<string> log = null)
        {
            var loaded = new HashSet<string>(am.assetsFileList.Select(f => f.fileName), StringComparer.OrdinalIgnoreCase);
            var toLoad = new List<PakEntry>();
            foreach (var cab in cabNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (IsSkippable(cab) || loaded.Contains(cab)) continue;
                var candidates = cabs.PaksFor(cab);
                if (candidates.Count == 0) continue;
                var pick = candidates.OrderBy(p => cfg.SourceRank(p.Source)).First();
                if (!toLoad.Contains(pick)) toLoad.Add(pick);
            }
            if (toLoad.Count > 0)
            {
                log?.Invoke($"[Deps] loading {toLoad.Count} pak(s) for referenced assets");
                am.LoadFiles(toLoad.Select(p => p.FullPath).ToArray());
            }
            return toLoad;
        }

        public static DependencyReport Resolve(AssetsManager am, CabIndex cabs, CodmConfig cfg, Action<string> log = null, int maxRounds = 3, int maxPaks = 60, IEnumerable<string> skipPrefixes = null, Func<PakEntry, bool> deepFilter = null)
        {
            var report = new DependencyReport();
            var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skip = (skipPrefixes ?? DefaultSkipPrefixes).ToArray();
            for (var round = 0; round < maxRounds; round++)
            {
                var missing = MissingExternals(am).Where(attempted.Add).ToList();
                if (missing.Count == 0)
                    break;

                var toLoad = new List<PakEntry>();
                foreach (var cab in missing)
                {
                    var candidates = cabs.PaksFor(cab);
                    if (candidates.Count == 0)
                    {
                        report.Unresolved.Add(cab);
                        continue;
                    }
                    var pick = candidates.OrderBy(p => cfg.SourceRank(p.Source)).First();
                    if (skip.Any(sp => pick.Name.Path.StartsWith(sp, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (!toLoad.Contains(pick) && !report.Loaded.Contains(pick))
                        toLoad.Add(pick);
                }
                if (round > 0)
                    toLoad = toLoad.Where(deepFilter ?? (p => Priority(p) == 0)).ToList(); // deeper rounds: only what the caller needs
                toLoad = toLoad.OrderBy(Priority).ThenBy(p => p.Size).ToList();
                if (toLoad.Count == 0)
                    break;
                if (report.Loaded.Count + toLoad.Count > maxPaks)
                {
                    report.HitLimit = true;
                    toLoad = toLoad.Take(Math.Max(0, maxPaks - report.Loaded.Count)).ToList();
                    log?.Invoke($"[Deps] dependency limit of {maxPaks} paks reached; some references stay unresolved");
                    if (toLoad.Count == 0)
                        break;
                }

                report.Rounds = round + 1;
                log?.Invoke($"[Deps] round {round + 1}: {missing.Count} external file(s) referenced, loading {toLoad.Count} pak(s)");
                foreach (var p in toLoad)
                    log?.Invoke($"[Deps]   + {p.Name.Decoded}");
                am.LoadFiles(toLoad.Select(p => p.FullPath).ToArray());
                report.Loaded.AddRange(toLoad);
            }
            if (report.Unresolved.Count > 0)
                log?.Invoke($"[Deps] {report.Unresolved.Count} referenced file(s) not found in any pak: {string.Join(", ", report.Unresolved.Take(5))}{(report.Unresolved.Count > 5 ? ", ..." : "")}");
            return report;
        }
    }
}
