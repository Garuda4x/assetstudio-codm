using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace AssetStudio.Codm
{
    /// <summary>
    /// Map from serialized file name inside a bundle (CAB-...) to the pak(s) that contain it. Bundles
    /// reference each other by these names (SerializedFile.m_Externals), so this is what turns an
    /// unresolved PPtr into a pak to load. Built from bundle headers only and cached as JSON; entries
    /// are reused while a pak's size and modification time are unchanged.
    /// </summary>
    public sealed class CabIndex
    {
        public sealed class CacheEntry
        {
            public string Path { get; set; }
            public long Size { get; set; }
            public long Mtime { get; set; }
            public List<string> Cabs { get; set; } = new List<string>();
        }

        private readonly Dictionary<string, List<PakEntry>> byCab = new Dictionary<string, List<PakEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> cabsByPak = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public int PakCount => cabsByPak.Count;
        public int CabCount => byCab.Count;
        public int Failed { get; private set; }


        public static string DefaultCachePath(CodmConfig cfg) => cfg.ResolveLocalPath(Path.Combine("cache", "cab-index.json"));

        /// <summary>Build (or refresh) the index for every pak in <paramref name="index"/>, reusing the cache where possible.</summary>
        public static CabIndex Build(PakIndex index, CodmConfig cfg, Action<string> progress = null, bool rebuild = false)
        {
            var cachePath = DefaultCachePath(cfg);
            var cached = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            if (!rebuild && File.Exists(cachePath))
            {
                try
                {
                    foreach (var e in JsonConvert.DeserializeObject<List<CacheEntry>>(File.ReadAllText(cachePath)) ?? new List<CacheEntry>())
                        cached[e.Path] = e;
                }
                catch (Exception ex)
                {
                    progress?.Invoke($"CAB cache unreadable, rebuilding: {ex.Message}");
                }
            }

            var result = new CabIndex();
            var entries = new CacheEntry[index.Entries.Count];
            var scanned = 0;
            var reused = 0;
            var failed = 0;
            var total = index.Entries.Count;
            System.Threading.Tasks.Parallel.For(0, index.Entries.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                var pak = index.Entries[i];
                var fi = new FileInfo(pak.FullPath);
                var mtime = fi.LastWriteTimeUtc.Ticks;
                if (cached.TryGetValue(pak.FullPath, out var ce) && ce.Size == fi.Length && ce.Mtime == mtime)
                {
                    System.Threading.Interlocked.Increment(ref reused);
                }
                else
                {
                    ce = new CacheEntry { Path = pak.FullPath, Size = fi.Length, Mtime = mtime };
                    try
                    {
                        ce.Cabs = BundleHeaderReader.Read(pak.FullPath).Entries;
                    }
                    catch (Exception)
                    {
                        System.Threading.Interlocked.Increment(ref failed);
                    }
                    var n = System.Threading.Interlocked.Increment(ref scanned);
                    if (n % 1000 == 0)
                        progress?.Invoke($"CAB index: {n + reused}/{total} paks");
                }
                entries[i] = ce;
            });
            result.Failed = failed;
            for (var i = 0; i < entries.Length; i++)
                result.Add(index.Entries[i], entries[i].Cabs);

            if (scanned > 0)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                    File.WriteAllText(cachePath, JsonConvert.SerializeObject(entries.ToList()));
                }
                catch (Exception ex)
                {
                    progress?.Invoke($"CAB cache not written: {ex.Message}");
                }
            }
            progress?.Invoke($"CAB index: {result.CabCount} serialized files in {result.PakCount} paks ({scanned} scanned, {reused} from cache, {result.Failed} unreadable)");
            return result;
        }

        private void Add(PakEntry pak, List<string> cabs)
        {
            cabsByPak[pak.FullPath] = cabs;
            foreach (var cab in cabs)
            {
                if (cab.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) || cab.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!byCab.TryGetValue(cab, out var list))
                    byCab[cab] = list = new List<PakEntry>();
                list.Add(pak);
            }
        }

        /// <summary>Paks containing a serialized file of this name (several when copies exist across sources).</summary>
        public IReadOnlyList<PakEntry> PaksFor(string cabName)
        {
            return byCab.TryGetValue(cabName, out var list) ? list : (IReadOnlyList<PakEntry>)Array.Empty<PakEntry>();
        }

        public IReadOnlyList<string> CabsOf(string pakPath)
        {
            return cabsByPak.TryGetValue(pakPath, out var list) ? list : (IReadOnlyList<string>)Array.Empty<string>();
        }
    }
}
