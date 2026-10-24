using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace AssetStudio.Codm
{
    /// <summary>
    /// Container paths (the asset paths recorded in each bundle's AssetBundle object) for a set of
    /// paks. Pak file names only carry a version bucket for attachments (component/mag/13.0), but the
    /// container paths inside name the weapon or skin they belong to (mag01_svdpurplesoul). Reading
    /// them means decompressing the bundle, so the result is cached as JSON and reused while a pak's
    /// size and modification time are unchanged.
    /// </summary>
    public sealed class ContainerIndex
    {
        public sealed class CacheEntry
        {
            public string Path { get; set; }
            public long Size { get; set; }
            public long Mtime { get; set; }
            public List<string> Containers { get; set; } = new List<string>();
        }

        private readonly Dictionary<string, List<string>> byPak = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public int PakCount => byPak.Count;

        public static string DefaultCachePath(CodmConfig cfg) => cfg.CachePath("container-index.json");

        public IReadOnlyList<string> ContainersOf(string pakPath)
        {
            return byPak.TryGetValue(pakPath, out var list) ? list : (IReadOnlyList<string>)Array.Empty<string>();
        }

        /// <summary>Build (or refresh) the index for the given paks, reusing the cache where possible.</summary>
        public static ContainerIndex Build(IEnumerable<PakEntry> paks, CodmConfig cfg, Action<string> progress = null, bool rebuild = false)
        {
            var cachePath = DefaultCachePath(cfg);
            var cached = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(cachePath))
            {
                try
                {
                    foreach (var e in JsonConvert.DeserializeObject<List<CacheEntry>>(File.ReadAllText(cachePath)) ?? new List<CacheEntry>())
                        cached[e.Path] = e;
                }
                catch (Exception ex)
                {
                    progress?.Invoke($"container cache unreadable, rebuilding: {ex.Message}");
                }
            }

            var list = paks.ToList();
            var result = new ContainerIndex();
            var entries = new CacheEntry[list.Count];
            var scanned = 0;
            var reused = 0;
            var onDisk = new List<int>();
            var packed = new List<int>();
            for (var i = 0; i < list.Count; i++)
            {
                var pak = list[i];
                var (size, stamp) = pak.CacheKey();
                if (!rebuild && cached.TryGetValue(pak.FullPath, out var ce) && ce.Size == size && ce.Mtime == stamp)
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
                var pak = list[i];
                try
                {
                    // a bundle still in the containers is normalized in memory (no texture streams needed), not unpacked
                    entries[i].Containers = pak.IsPacked
                        ? ReadContainers(pak.FullPath, new MemoryStream(pak.Vfs.ReadBundle(pak.Container, true, embedStreams: false)))
                        : ReadContainers(pak.FullPath);
                }
                catch (Exception)
                {
                    // unreadable: stays empty; a file is retried on the next build, a container entry when its stamp changes
                    if (!pak.IsVirtual)
                        entries[i].Size = -1;
                }
                var n = System.Threading.Interlocked.Increment(ref scanned);
                if (n % 200 == 0)
                    progress?.Invoke($"container index: {n + reused}/{list.Count} paks");
            }

            System.Threading.Tasks.Parallel.ForEach(onDisk, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, Scan);
            if (packed.Count > 0)
            {
                progress?.Invoke($"container index: reading {packed.Count} attachment bundles from the game containers (once, cached after)...");
                foreach (var i in list[packed[0]].Vfs.InStorageOrder(packed, k => list[k].Container))
                    Scan(i);
            }
            foreach (var e in entries)
                result.byPak[e.Path] = e.Containers;

            if (scanned > 0)
            {
                try
                {
                    // keep entries of paks that were not part of this build
                    var all = entries.ToList();
                    foreach (var kv in cached)
                        if (!result.byPak.ContainsKey(kv.Key))
                            all.Add(kv.Value);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cachePath));
                    File.WriteAllText(cachePath, JsonConvert.SerializeObject(all));
                }
                catch (Exception ex)
                {
                    progress?.Invoke($"container cache not written: {ex.Message}");
                }
            }
            progress?.Invoke($"container index: {result.PakCount} paks ({scanned} scanned, {reused} from cache)");
            return result;
        }

        /// <summary>
        /// Container paths of one bundle: only the AssetBundle object is deserialized. <paramref name="data"/>
        /// supplies the bundle bytes when it is not (yet) a file at <paramref name="pakPath"/>.
        /// </summary>
        public static List<string> ReadContainers(string pakPath, Stream data = null)
        {
            var result = new List<string>();
            using (var reader = data != null ? new FileReader(pakPath, data) : new FileReader(pakPath))
            {
                if (reader.FileType != FileType.BundleFile)
                    return result;
                var bundle = new BundleFile(reader);
                foreach (var file in bundle.fileList)
                {
                    var dummyPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(pakPath), file.fileName);
                    var subReader = new FileReader(dummyPath, file.stream);
                    if (subReader.FileType != FileType.AssetsFile)
                        continue;
                    var mgr = new AssetsManager();
                    var assetsFile = new SerializedFile(subReader, mgr);
                    if (!string.IsNullOrEmpty(bundle.m_Header.unityRevision) && assetsFile.header.m_Version < SerializedFileFormatVersion.Unknown_7)
                        assetsFile.SetVersion(bundle.m_Header.unityRevision);
                    foreach (var objInfo in assetsFile.m_Objects)
                    {
                        if ((ClassIDType)objInfo.classID != ClassIDType.AssetBundle)
                            continue;
                        try
                        {
                            var ab = new AssetBundle(new ObjectReader(subReader, assetsFile, objInfo));
                            foreach (var c in ab.m_Container)
                                if (!string.IsNullOrEmpty(c.Key) && !result.Contains(c.Key))
                                    result.Add(c.Key);
                        }
                        catch (Exception)
                        {
                            // skip unreadable AssetBundle objects
                        }
                    }
                }
            }
            return result;
        }
    }
}
