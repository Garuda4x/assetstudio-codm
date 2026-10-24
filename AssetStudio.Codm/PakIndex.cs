using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio.Codm.Vfs;

namespace AssetStudio.Codm
{
    public sealed class PakEntry
    {
        public string FullPath { get; set; }
        public string FileName { get; set; }
        /// <summary>streaming / puffer / extract (see <see cref="CodmConfig.SourceLabel"/>).</summary>
        public string Source { get; set; }
        /// <summary>File size; for an entry still in the containers, the container's (pre-normalization) size.</summary>
        public long Size { get; set; }
        public PakName Name { get; set; }

        /// <summary>
        /// 1.9.57: the container entry this pak comes from. <see cref="FullPath"/> is where it is unpacked
        /// (&lt;UnpackRoot&gt;/&lt;source&gt;/&lt;file&gt;); the file is written on first use by <see cref="EnsureOnDisk(Action{string})"/>.
        /// Null for a loose pak.
        /// </summary>
        public VfsEntry Container { get; set; }
        public CodmVfs Vfs { get; set; }
        public bool IsVirtual => Container != null;
        /// <summary>A container entry whose file has not been written yet.</summary>
        public bool IsPacked => Container != null && !File.Exists(FullPath);

        private readonly object materializeGate = new object();

        /// <summary>
        /// Make sure the pak exists at <see cref="FullPath"/>: a container entry is read, normalized and
        /// written there the first time. Returns <see cref="FullPath"/>.
        /// </summary>
        public string EnsureOnDisk(Action<string> log = null)
        {
            if (Container == null || File.Exists(FullPath))
                return FullPath;
            lock (materializeGate)
            {
                if (File.Exists(FullPath))
                    return FullPath;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var stats = new BundleNormalizer.Stats();
                long written;
                try
                {
                    written = Vfs.Materialize(Container, FullPath, stats);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"unpacking {Source} {Name.Decoded} ({Container.RawName}) failed: {ex.Message}", ex);
                }
                log?.Invoke($"[Unpack] {Source} {Name.Decoded}: {written / (1024.0 * 1024.0):F1} MB in {sw.Elapsed.TotalSeconds:F1} s" +
                            (stats.RewrittenFiles > 0 || stats.StreamChunks.Count > 0 ? $" ({stats.ExternalObjects} objects from {stats.Chunks.Count} chunk(s), {stats.StreamChunks.Count} texture stream(s))" : ""));
                return FullPath;
            }
        }

        /// <summary>
        /// <see cref="EnsureOnDisk(Action{string})"/> for each pak; the paths to load, in order. A pak that
        /// cannot be unpacked is logged and left out, like an unreadable file, instead of failing the whole set.
        /// </summary>
        public static string[] EnsureOnDisk(IEnumerable<PakEntry> paks, Action<string> log = null)
        {
            var list = paks.ToList();
            var packed = list.Where(p => p.IsPacked).ToList();
            var failed = new HashSet<PakEntry>();
            if (packed.Count > 0)
            {
                log?.Invoke($"[Unpack] {packed.Count} pak(s) from the game containers ({packed.Sum(p => p.Size) / (1024.0 * 1024.0):F1} MB packed) -> {Path.GetDirectoryName(Path.GetDirectoryName(packed[0].FullPath))}");
                var vfs = packed[0].Vfs;
                foreach (var p in vfs != null ? vfs.InStorageOrder(packed, x => x.Container) : packed)
                {
                    try
                    {
                        p.EnsureOnDisk(log);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(p);
                        log?.Invoke($"[Unpack] FAILED, skipped: {ex.Message}");
                    }
                }
            }
            return list.Where(p => !failed.Contains(p)).Select(p => p.FullPath).ToArray();
        }

        /// <summary>
        /// Size and time stamp the index caches compare to decide whether a pak changed: the file's for a
        /// loose pak, the container record's (size, content hash) for a container entry, unpacked or not.
        /// </summary>
        public (long size, long stamp) CacheKey()
        {
            if (Container != null)
                return (Container.Size, Container.Stamp);
            var fi = new FileInfo(FullPath);
            return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc.Ticks) : (-1, -1);
        }

        public override string ToString() => $"[{Source}] {Name.Decoded}";
    }

    /// <summary>
    /// Index of every .pak in the configured folders with its decoded name. Building it only
    /// lists directories and decodes file names, so it is fast enough to rebuild on every start.
    /// On a 1.9.57 install it also lists every bundle inside the containers as a virtual entry
    /// (<see cref="PakEntry.Container"/>), unpacked on first use.
    /// </summary>
    public sealed class PakIndex
    {
        public List<PakEntry> Entries { get; } = new List<PakEntry>();
        public List<string> ScannedFolders { get; } = new List<string>();
        public List<string> MissingFolders { get; } = new List<string>();
        /// <summary>The containers the virtual entries come from; null for a loose-pak install.</summary>
        public CodmVfs Vfs { get; private set; }

        private Dictionary<string, List<PakEntry>> byPath;
        private Dictionary<string, PakEntry> byFullPath;

        /// <summary>
        /// List the paks of the configured folders, plus, when <paramref name="vfs"/> is given, every bundle
        /// in the containers: those already unpacked are matched to their file, the others become virtual
        /// entries whose path is where they will be unpacked.
        /// </summary>
        public static PakIndex Build(CodmConfig cfg, Action<string> progress = null, CodmVfs vfs = null)
        {
            var index = new PakIndex();
            foreach (var folder in cfg.PakFolderPaths)
            {
                if (!Directory.Exists(folder))
                {
                    index.MissingFolders.Add(folder);
                    progress?.Invoke($"Pak folder not found: {folder}");
                    continue;
                }
                var label = CodmConfig.SourceLabel(folder);
                var count = 0;
                foreach (var fi in new DirectoryInfo(folder).EnumerateFiles("*.pak", SearchOption.AllDirectories))
                {
                    index.Entries.Add(new PakEntry
                    {
                        FullPath = fi.FullName,
                        FileName = fi.Name,
                        Source = label,
                        Size = fi.Length,
                        Name = PakName.Parse(fi.Name),
                    });
                    count++;
                }
                index.ScannedFolders.Add(folder);
                progress?.Invoke($"{label}: {count} paks in {folder}");
            }
            if (vfs != null)
                index.AddContainerEntries(cfg, vfs, progress);
            index.Entries.Sort((a, b) => string.CompareOrdinal(a.Name.Decoded, b.Name.Decoded));
            return index;
        }

        private void AddContainerEntries(CodmConfig cfg, CodmVfs vfs, Action<string> progress)
        {
            Vfs = vfs;
            var onDisk = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Entries)
                onDisk[e.FullPath] = e;
            var added = 0;
            var unpacked = 0;
            foreach (var v in vfs.Entries)
            {
                if (!v.IsBundle)
                    continue;
                var path = CodmVfs.UnpackPath(cfg, v);
                if (onDisk.TryGetValue(path, out var existing))
                {
                    existing.Container = v;
                    existing.Vfs = vfs;
                    unpacked++;
                    continue;
                }
                Entries.Add(new PakEntry
                {
                    FullPath = path,
                    FileName = v.FileName,
                    Source = v.SourceLabel,
                    Size = v.Size,
                    Name = v.Name,
                    Container = v,
                    Vfs = vfs,
                });
                added++;
            }
            progress?.Invoke($"containers: {added + unpacked} bundles, {unpacked} of them unpacked in {cfg.UnpackRootPath}, the others are unpacked on first use");
        }

        /// <summary>The entry whose <see cref="PakEntry.FullPath"/> is <paramref name="fullPath"/>, or null.</summary>
        public PakEntry ByFullPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return null;
            if (byFullPath == null)
            {
                var d = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in Entries)
                    d[e.FullPath] = e;
                byFullPath = d;
            }
            return byFullPath.TryGetValue(fullPath, out var found) ? found : null;
        }

        /// <summary>All entries whose decoded path starts with <paramref name="prefix"/> (case-insensitive).</summary>
        public IEnumerable<PakEntry> UnderPath(string prefix)
        {
            return Entries.Where(e => e.Name.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Entries whose decoded name or raw file name contains <paramref name="text"/>.</summary>
        public IEnumerable<PakEntry> Search(string text)
        {
            return Entries.Where(e =>
                e.Name.Decoded.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ||
                e.FileName.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>All copies of the same decoded bundle path (streaming + every download module).</summary>
        public IReadOnlyList<PakEntry> CopiesOf(string decodedPath)
        {
            if (byPath == null)
            {
                byPath = new Dictionary<string, List<PakEntry>>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in Entries)
                {
                    var key = PathKey(e.Name);
                    if (!byPath.TryGetValue(key, out var list))
                        byPath[key] = list = new List<PakEntry>();
                    list.Add(e);
                }
            }
            return byPath.TryGetValue(decodedPath, out var found) ? found : (IReadOnlyList<PakEntry>)Array.Empty<PakEntry>();
        }

        public static string PathKey(PakName n)
        {
            return n.Path + (n.Category != null ? "#" + n.Category : "") + (n.Shard >= 0 ? "@" + n.Shard : "");
        }

        /// <summary>
        /// Order entries so that download modules (newer content) come before the streaming copy and drop
        /// exact duplicates. Module copies of one bundle path are different bundles (each module ships its
        /// own slice), so they are all kept; AssetsManager skips CABs it has already loaded by name.
        /// </summary>
        public static List<PakEntry> OrderForLoading(IEnumerable<PakEntry> entries, CodmConfig cfg)
        {
            return entries
                .Distinct()
                .OrderBy(e => cfg.SourceRank(e.Source))
                .ThenByDescending(e => e.Name.Module ?? "")
                .ThenBy(e => e.Name.Decoded, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public Dictionary<string, int> CountByTopFolder(int depth = 2)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Entries)
            {
                var segs = e.Name.Segments;
                var key = string.Join("/", segs.Take(depth));
                d[key] = d.TryGetValue(key, out var n) ? n + 1 : 1;
            }
            return d;
        }

        public void WriteTsv(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("source\tfile\tmodule\tversion\tpath\tcategory\tshard\tsize");
            foreach (var e in Entries)
            {
                sb.Append(e.Source).Append('\t')
                  .Append(e.FileName).Append('\t')
                  .Append(e.Name.Module ?? "").Append('\t')
                  .Append(e.Name.ModuleVersion ?? "").Append('\t')
                  .Append(e.Name.Path).Append('\t')
                  .Append(e.Name.Category ?? "").Append('\t')
                  .Append(e.Name.Shard >= 0 ? e.Name.Shard.ToString() : "").Append('\t')
                  .Append(e.Size).AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
    }
}
