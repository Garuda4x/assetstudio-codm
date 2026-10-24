using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetStudio.Codm.Vfs
{
    public enum VfsSource { Puffer, Patch, Streaming }

    /// <summary>A file inside one of the 1.9.57 containers, with the same naming the loose paks had.</summary>
    public sealed class VfsEntry
    {
        public VfsSource Source { get; internal set; }
        public string SourceLabel => Source == VfsSource.Puffer ? "puffer" : Source == VfsSource.Patch ? "extract" : "streaming";
        /// <summary>Name as stored in the container.</summary>
        public string RawName { get; internal set; }
        /// <summary>File name a loose pak would have had (ciphered, upper case) or "&lt;md5&gt;.cpak" for chunks.</summary>
        public string FileName { get; internal set; }
        public PakName Name { get; internal set; }
        public long Size { get; internal set; }
        public bool IsChunk { get; internal set; }
        public string ChunkId { get; internal set; }
        /// <summary>First 8 bytes of the content hash the container stores for the entry; changes when a patch replaces it.</summary>
        public long Stamp { get; internal set; }
        internal QtsEntry Qts { get; set; }
        internal QtsfEntry Qtsf { get; set; }

        public string Decoded => IsChunk ? RawName : (Name?.Decoded ?? FileName);
        public bool IsBundle => !IsChunk && FileName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);
        public override string ToString() => $"{SourceLabel}: {Decoded} ({Size:N0} B)";
    }

    /// <summary>
    /// All containers of a 1.9.57 install: the base pack (former StreamingAssets), the PufferQts download
    /// pack (modules: weapons, roles...) and the ExtractQts patch pack. Finds entries by decoded name,
    /// reads them, resolves the chunks a format-18 bundle references and hands bundles to the normalizer.
    /// </summary>
    public sealed class CodmVfs : IDisposable
    {
        public List<QtsPack> QtsPacks { get; } = new List<QtsPack>();
        public QtsfPack BasePack { get; private set; }
        public List<VfsEntry> Entries { get; } = new List<VfsEntry>();
        // chunk names are stable ids, not content hashes: a patch ships a new version under the same name,
        // so every pack holding a chunk is kept and a bundle takes the copy from its own pack first
        private readonly Dictionary<string, List<VfsEntry>> chunks = new Dictionary<string, List<VfsEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> chunkCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public bool HasBaseOffsets => BasePack != null && BasePack.HasOffsets;
        /// <summary>The config this set was opened with (cache paths for the base pack offsets).</summary>
        public CodmConfig Config { get; private set; }
        private Action<string> log;

        /// <summary>Default container folders under the game root.</summary>
        public static IEnumerable<string> DefaultFolders(CodmConfig cfg)
        {
            yield return cfg.ResolveGamePath(@"CODM_Data\StreamingAssets\AssetBundle\base");
            yield return cfg.ResolveGamePath(@"PersistentData\PufferQts");
            var extract = cfg.ResolveGamePath(@"PersistentData\ExtractQts");
            if (Directory.Exists(extract))
            {
                var numbered = Directory.GetDirectories(extract)
                    .Select(d => (dir: d, n: int.TryParse(Path.GetFileName(d), out var v) ? v : -1))
                    .Where(x => x.n >= 0).OrderByDescending(x => x.n).FirstOrDefault();
                if (numbered.dir != null)
                    yield return numbered.dir;
            }
        }

        /// <summary>The configured container folders (<see cref="CodmConfig.VfsFolders"/>) or the default ones under the game root.</summary>
        public static IEnumerable<string> Folders(CodmConfig cfg)
        {
            return cfg.VfsFolders != null && cfg.VfsFolders.Count > 0 ? cfg.VfsFolders.Select(cfg.ResolveGamePath) : DefaultFolders(cfg);
        }

        /// <summary>True for a 1.9.57 install: at least one container folder exists.</summary>
        public static bool HasContainers(CodmConfig cfg) => Folders(cfg).Any(Directory.Exists);

        /// <summary>Where <c>codm unpack</c> and on-demand loading write an entry: &lt;UnpackRoot&gt;/&lt;source&gt;/&lt;file name&gt;.</summary>
        public static string UnpackPath(CodmConfig cfg, VfsEntry e)
        {
            return Path.Combine(cfg.UnpackRootPath, e.IsChunk ? "chunks" : e.SourceLabel, e.FileName);
        }

        public static CodmVfs Open(CodmConfig cfg, Action<string> log = null)
        {
            var vfs = new CodmVfs { Config = cfg, log = log };
            foreach (var folder in Folders(cfg))
            {
                if (!Directory.Exists(folder))
                {
                    log?.Invoke($"container folder not found: {folder}");
                    continue;
                }
                var order = Directory.GetFiles(folder, "*_packorder.bin").FirstOrDefault();
                if (order != null)
                {
                    var prefix = Path.GetFileName(order).Replace("_packorder.bin", "");
                    if (QtsfPack.LooksLikeIndex(Path.Combine(folder, prefix + ".pak")))
                    {
                        vfs.BasePack = QtsfPack.Open(folder, prefix + ".pak", "streaming", log);
                        continue;
                    }
                }
                var index = Path.Combine(folder, "AssetBundle.pak");
                if (QtsPack.LooksLikeIndex(index))
                {
                    var name = folder.Replace('/', '\\');
                    var label = name.IndexOf("Puffer", StringComparison.OrdinalIgnoreCase) >= 0 ? "puffer"
                        : name.IndexOf("Extract", StringComparison.OrdinalIgnoreCase) >= 0 ? "extract"
                        : Path.GetFileName(folder.TrimEnd('\\'));
                    vfs.QtsPacks.Add(QtsPack.Open(folder, label, log));
                    continue;
                }
                log?.Invoke($"no container index in {folder}");
            }
            vfs.BuildEntryList();
            return vfs;
        }

        private void BuildEntryList()
        {
            Entries.Clear();
            chunks.Clear();
            foreach (var pack in QtsPacks)
            {
                var source = pack.Label == "puffer" ? VfsSource.Puffer : VfsSource.Patch;
                foreach (var q in pack.Entries)
                {
                    var e = new VfsEntry { Source = source, RawName = q.Name, Size = q.Size, Qts = q, IsChunk = q.IsChunk, ChunkId = q.ChunkId, Stamp = BitConverter.ToInt64(q.Hash, 0) };
                    if (q.IsChunk)
                    {
                        e.FileName = q.ChunkId + ".cpak";
                        AddChunk(e);
                    }
                    else
                    {
                        e.FileName = q.FileName.ToUpperInvariant();
                        e.Name = PakName.Parse(e.FileName);
                    }
                    Entries.Add(e);
                }
            }
            if (BasePack != null)
            {
                foreach (var b in BasePack.Entries)
                {
                    var e = new VfsEntry { Source = VfsSource.Streaming, RawName = b.Name, Size = b.Size, Qtsf = b, IsChunk = b.IsChunk, ChunkId = b.ChunkId, Stamp = BitConverter.ToInt64(b.Hash20, 0) };
                    if (b.IsChunk)
                    {
                        e.FileName = b.ChunkId + ".cpak";
                        AddChunk(e);
                    }
                    else
                    {
                        e.FileName = b.Name;
                        e.Name = PakName.Parse(b.Name);
                    }
                    Entries.Add(e);
                }
            }
        }

        private void AddChunk(VfsEntry e)
        {
            if (!chunks.TryGetValue(e.ChunkId, out var list))
                chunks[e.ChunkId] = list = new List<VfsEntry>(1);
            list.Add(e);
        }

        public int ChunkCount => chunks.Count;

        private readonly object offsetGate = new object();
        private volatile bool offsetsReady;

        /// <summary>Load or build the base pack offset cache (a walk over all parts, minutes the first time).</summary>
        public void EnsureBaseOffsets(CodmConfig cfg, Action<string> progress = null, bool rebuild = false)
        {
            if (BasePack == null || (offsetsReady && !rebuild)) return;
            lock (offsetGate)
            {
                if (offsetsReady && !rebuild) return;
                var cache = cfg.CachePath($"qtsf-{BasePack.Prefix}-offsets.bin");
                if (!rebuild && BasePack.EntriesWithOffsets > 0)
                {
                    offsetsReady = true;
                    return;
                }
                if (!rebuild && BasePack.LoadOffsets(cache))
                {
                    progress?.Invoke($"{BasePack.Label}: offsets loaded from {cache} ({BasePack.EntriesWithOffsets}/{BasePack.Entries.Count} entries)");
                    offsetsReady = true;
                    return;
                }
                progress?.Invoke($"{BasePack.Label}: building the entry offset cache (one walk over {BasePack.PartCount} parts, minutes)...");
                BasePack.BuildOffsets(cache, progress);
                offsetsReady = true;
            }
        }

        public string BaseOffsetCachePath(CodmConfig cfg) => BasePack == null ? null : cfg.CachePath($"qtsf-{BasePack.Prefix}-offsets.bin");

        /// <summary>Entries whose decoded or raw name contains <paramref name="query"/> (case-insensitive).</summary>
        public IEnumerable<VfsEntry> Find(string query, bool includeChunks = false)
        {
            foreach (var e in Entries)
            {
                if (e.IsChunk && !includeChunks) continue;
                if (e.Decoded.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || e.RawName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    yield return e;
            }
        }

        public byte[] ReadRaw(VfsEntry e) => ReadPrefix(e, long.MaxValue);

        /// <summary>The first bytes of an entry (whole 16 KB blocks, at least <paramref name="bytes"/>), or all of it when it is not longer.</summary>
        public byte[] ReadPrefix(VfsEntry e, long bytes)
        {
            if (e.Qts != null) return e.Qts.Pack.Read(e.Qts, bytes);
            if (e.Qtsf != null)
            {
                if (e.Qtsf.Offset < 0 && Config != null)
                    EnsureBaseOffsets(Config, log);
                return BasePack.Read(e.Qtsf, bytes);
            }
            throw new InvalidOperationException("entry without a pack");
        }

        /// <summary>
        /// Bundle header and directory (serialized file names) of an entry, read from its first block when
        /// the directory sits there (the usual layout), else from the whole entry.
        /// </summary>
        public BundleHeaderReader.Info ReadBundleHeader(VfsEntry e)
        {
            var prefix = ReadPrefix(e, QtsPack.BlockSize);
            using (var ms = new MemoryStream(prefix))
            {
                var info = BundleHeaderReader.Read(ms, e.Size);
                if (info != null) return info;
            }
            using (var ms = new MemoryStream(ReadRaw(e)))
                return BundleHeaderReader.Read(ms);
        }

        /// <summary>
        /// Order entries the way they lie on disk (pack, part, offset), so reading many of them is one forward
        /// sweep per part instead of random seeks; the containers usually sit on a hard disk.
        /// </summary>
        public List<T> InStorageOrder<T>(IEnumerable<T> items, Func<T, VfsEntry> entryOf)
        {
            var list = items.ToList();
            if (list.Any(x => entryOf(x).Qtsf != null) && Config != null)
                EnsureBaseOffsets(Config, log);
            var keys = new Dictionary<VfsEntry, (int pack, int part, long offset)>();
            foreach (var x in list)
            {
                var e = entryOf(x);
                if (keys.ContainsKey(e)) continue;
                try
                {
                    keys[e] = e.Qts != null
                        ? (QtsPacks.IndexOf(e.Qts.Pack), e.Qts.Part, e.Qts.Pack.DataOffset(e.Qts))
                        : (QtsPacks.Count, e.Qtsf.Part, e.Qtsf.Offset);
                }
                catch (Exception)
                {
                    keys[e] = (int.MaxValue, 0, 0); // part missing: read last, the read reports the error
                }
            }
            return list.OrderBy(x => keys[entryOf(x)].pack).ThenBy(x => keys[entryOf(x)].part).ThenBy(x => keys[entryOf(x)].offset).ToList();
        }

        /// <summary>The chunk's bytes: the copy in <paramref name="preferred"/> when that pack has one, else the first pack holding it.</summary>
        public byte[] ReadChunk(string md5, VfsSource? preferred = null)
        {
            var e = ChunkEntry(md5, preferred);
            if (e == null)
                return null;
            var key = e.SourceLabel + ":" + md5;
            lock (chunkCache)
            {
                if (chunkCache.TryGetValue(key, out var cached)) return cached;
            }
            var data = ReadRaw(e);
            if (data.Length != e.Size)
                throw new InvalidDataException($"chunk {md5} from {e.SourceLabel}: read {data.Length} bytes, index says {e.Size}");
            lock (chunkCache)
            {
                if (chunkCache.Count > 64) chunkCache.Clear();
                chunkCache[key] = data;
            }
            return data;
        }

        public bool HasChunk(string md5) => chunks.ContainsKey(md5);

        public VfsEntry ChunkEntry(string md5, VfsSource? preferred = null)
        {
            if (!chunks.TryGetValue(md5, out var list)) return null;
            if (preferred.HasValue)
                foreach (var c in list)
                    if (c.Source == preferred.Value) return c;
            return list[0];
        }

        /// <summary>
        /// Read an entry; bundles are rewritten into stock Unity 5.6 bundles unless <paramref name="normalize"/> is false.
        /// <paramref name="embedStreams"/> false leaves streamed texture data out (enough for reading metadata).
        /// </summary>
        public byte[] ReadBundle(VfsEntry e, bool normalize, BundleNormalizer.Stats stats = null, Action<string> log = null, bool embedStreams = true)
        {
            var raw = ReadRaw(e);
            if (!normalize || !BundleNormalizer.IsUnityFS(raw))
                return raw;
            return BundleNormalizer.Normalize(raw, md5 => ReadChunk(md5, e.Source), stats, log, embedStreams) ?? raw;
        }

        /// <summary>
        /// Write an entry (normalized when it is a bundle) to <paramref name="path"/> through a temporary file,
        /// so an interrupted write never leaves a truncated pak behind. Returns the bytes written.
        /// </summary>
        public long Materialize(VfsEntry e, string path, BundleNormalizer.Stats stats = null, Action<string> log = null)
        {
            var data = e.IsBundle ? ReadBundle(e, true, stats, log) : ReadRaw(e);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, data);
                File.Move(tmp, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                TryDelete(tmp); // written meanwhile by another reader or a codm unpack
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
            return data.Length;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (Exception) { }
        }

        public void Dispose()
        {
            foreach (var p in QtsPacks) p.Dispose();
            BasePack?.Dispose();
        }
    }
}
