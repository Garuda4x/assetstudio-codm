using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetStudio.Codm
{
    public sealed class PakEntry
    {
        public string FullPath { get; set; }
        public string FileName { get; set; }
        /// <summary>streaming / puffer / extract (see <see cref="CodmConfig.SourceLabel"/>).</summary>
        public string Source { get; set; }
        public long Size { get; set; }
        public PakName Name { get; set; }

        public override string ToString() => $"[{Source}] {Name.Decoded}";
    }

    /// <summary>
    /// Index of every .pak in the configured folders with its decoded name. Building it only
    /// lists directories and decodes file names, so it is fast enough to rebuild on every start.
    /// </summary>
    public sealed class PakIndex
    {
        public List<PakEntry> Entries { get; } = new List<PakEntry>();
        public List<string> ScannedFolders { get; } = new List<string>();
        public List<string> MissingFolders { get; } = new List<string>();

        private Dictionary<string, List<PakEntry>> byPath;

        public static PakIndex Build(CodmConfig cfg, Action<string> progress = null)
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
            index.Entries.Sort((a, b) => string.CompareOrdinal(a.Name.Decoded, b.Name.Decoded));
            return index;
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
