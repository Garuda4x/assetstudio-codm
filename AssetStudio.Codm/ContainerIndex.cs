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
            System.Threading.Tasks.Parallel.For(0, list.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                var pak = list[i];
                var fi = new FileInfo(pak.FullPath);
                var mtime = fi.LastWriteTimeUtc.Ticks;
                if (!rebuild && cached.TryGetValue(pak.FullPath, out var ce) && ce.Size == fi.Length && ce.Mtime == mtime)
                {
                    System.Threading.Interlocked.Increment(ref reused);
                }
                else
                {
                    ce = new CacheEntry { Path = pak.FullPath, Size = fi.Length, Mtime = mtime };
                    try
                    {
                        ce.Containers = ReadContainers(pak.FullPath);
                    }
                    catch (Exception)
                    {
                        // unreadable bundle: stays empty
                    }
                    var n = System.Threading.Interlocked.Increment(ref scanned);
                    if (n % 200 == 0)
                        progress?.Invoke($"container index: {n + reused}/{list.Count} paks");
                }
                entries[i] = ce;
            });
            foreach (var e in entries)
                result.byPak[e.Path] = e.Containers;

            if (scanned > 0)
            {
                try
                {
                    // keep entries of paks that were not part of this build
                    foreach (var kv in cached)
                        if (!result.byPak.ContainsKey(kv.Key))
                            entries = entries.Concat(new[] { kv.Value }).ToArray();
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cachePath));
                    File.WriteAllText(cachePath, JsonConvert.SerializeObject(entries.ToList()));
                }
                catch (Exception ex)
                {
                    progress?.Invoke($"container cache not written: {ex.Message}");
                }
            }
            progress?.Invoke($"container index: {result.PakCount} paks ({scanned} scanned, {reused} from cache)");
            return result;
        }

        /// <summary>Container paths of one bundle: only the AssetBundle object is deserialized.</summary>
        public static List<string> ReadContainers(string pakPath)
        {
            var result = new List<string>();
            using (var reader = new FileReader(pakPath))
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
