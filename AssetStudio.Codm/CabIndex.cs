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
    /// are reused while a pak's size and modification time (container entries: size and content hash,
    /// see <see cref="PakEntry.CacheKey"/>) are unchanged.
    /// </summary>
    public sealed class CabIndex
    {
        public sealed class CacheEntry
        {
            public string Path { get; set; }
            public long Size { get; set; }
            /// <summary>File modification time, or the content hash stamp of a container entry.</summary>
            public long Mtime { get; set; }
            public List<string> Cabs { get; set; } = new List<string>();
        }

        private readonly Dictionary<string, List<PakEntry>> byCab = new Dictionary<string, List<PakEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> cabsByPak = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public int PakCount => cabsByPak.Count;
        public int CabCount => byCab.Count;
        public int Failed { get; private set; }


        public static string DefaultCachePath(CodmConfig cfg) => cfg.CachePath("cab-index.json");

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
            var onDisk = new List<int>();
            var packed = new List<int>();
            for (var i = 0; i < total; i++)
            {
                var pak = index.Entries[i];
                var (size, stamp) = pak.CacheKey();
                if (cached.TryGetValue(pak.FullPath, out var ce) && ce.Size == size && ce.Mtime == stamp)
                {
                    entries[i] = ce;
                    reused++;
                }
                else
                {
                    entries[i] = new CacheEntry { Path = pak.FullPath, Size = size, Mtime = stamp };
                    (pak.IsPacked ? packed : onDisk).Add(i);
                }
            }

            void Scan(int i)
            {
                var pak = index.Entries[i];
                try
                {
                    entries[i].Cabs = (pak.IsPacked ? pak.Vfs.ReadBundleHeader(pak.Container) : BundleHeaderReader.Read(pak.FullPath)).Entries;
                }
                catch (Exception)
                {
                    // a file is retried on the next build; a container entry only changes with its stamp
                    // (the patch pack holds a 32-byte placeholder .pak), so its failure stays cached
                    if (!pak.IsVirtual)
                        entries[i].Size = -1;
                    System.Threading.Interlocked.Increment(ref failed);
                }
                var n = System.Threading.Interlocked.Increment(ref scanned);
                if (n % 1000 == 0)
                    progress?.Invoke($"CAB index: {n + reused}/{total} paks");
            }

            System.Threading.Tasks.Parallel.ForEach(onDisk, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 }, Scan);
            if (packed.Count > 0)
            {
                // bundles still in the game containers: only their first block is read, in storage order
                // (one forward sweep per part; the containers usually sit on a hard disk)
                var sw = System.Diagnostics.Stopwatch.StartNew();
                progress?.Invoke($"CAB index: reading the headers of {packed.Count} bundles from the game containers (once, cached after)...");
                foreach (var i in index.Vfs.InStorageOrder(packed, k => index.Entries[k].Container))
                    Scan(i);
                progress?.Invoke($"CAB index: container headers read in {sw.Elapsed.TotalSeconds:F0} s");
            }
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
                // resource nodes: .resS/.resource, and the texture stream chunks the normalizer embeds as <md5>.cpak
                if (cab.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) || cab.EndsWith(".resource", StringComparison.OrdinalIgnoreCase) || cab.EndsWith(".cpak", StringComparison.OrdinalIgnoreCase))
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
