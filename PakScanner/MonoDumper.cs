using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AssetStudio;

namespace PakScanner
{
    /// <summary>
    /// Full load via AssetsManager and list MonoScript / MonoBehaviour metadata plus
    /// printable strings left in each MonoBehaviour blob (often Wwise event names, asset paths).
    /// </summary>
    public static class MonoDumper
    {
        static readonly Regex AsciiRun = new Regex(@"[\x20-\x7E]{4,}", RegexOptions.Compiled);

        public static void DumpFile(string filePath, bool stringsOnly = false)
        {
            var mgr = new AssetsManager();
            try
            {
                mgr.LoadFiles(filePath);

                Console.WriteLine($"=== Mono dump: {System.IO.Path.GetFileName(filePath)} ===");
                Console.WriteLine($"Serialized files: {mgr.assetsFileList.Count}");
                var totalObjs = mgr.assetsFileList.Sum(f => f.Objects.Count);
                var typeCounts = mgr.assetsFileList
                    .SelectMany(f => f.Objects)
                    .GroupBy(o => o.type)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key}x{g.Count()}");
                Console.WriteLine($"Objects loaded: {totalObjs}  ({string.Join(", ", typeCounts.Take(12))})");
                Console.WriteLine();

                var scripts = new List<MonoScript>();
                var behaviours = new List<(MonoBehaviour mb, SerializedFile file)>();

                foreach (var assetsFile in mgr.assetsFileList)
                {
                    foreach (var obj in assetsFile.Objects)
                    {
                        if (obj is MonoScript ms)
                            scripts.Add(ms);
                        else if (obj is MonoBehaviour mb)
                            behaviours.Add((mb, assetsFile));
                    }
                }

                if (!stringsOnly)
                {
                    Console.WriteLine($"MonoScript ({scripts.Count}):");
                    foreach (var ms in scripts.OrderBy(s => s.m_ClassName))
                    {
                        Console.WriteLine($"  {ms.m_ClassName}  ({ms.m_Namespace})  [{ms.m_AssemblyName}]");
                    }
                    Console.WriteLine();

                    Console.WriteLine($"MonoBehaviour ({behaviours.Count}):");
                    foreach (var (mb, file) in behaviours)
                    {
                        var className = "?";
                        if (mb.m_Script.TryGet(out MonoScript script))
                            className = script.m_ClassName;
                        var goName = "";
                        if (mb.m_GameObject.TryGet(out GameObject go))
                            goName = go.m_Name;
                        Console.WriteLine($"  PathID={mb.m_PathID}  class={className}  name=\"{mb.m_Name}\"  gameObject=\"{goName}\"  file={file.fileName}");
                    }
                    Console.WriteLine();
                }

                Console.WriteLine("--- MonoBehaviour embedded strings (unique) ---");
                var allStrings = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var assetsFile in mgr.assetsFileList)
                {
                    foreach (var objectInfo in assetsFile.m_Objects)
                    {
                        if ((ClassIDType)objectInfo.classID != ClassIDType.MonoBehaviour)
                            continue;
                        var objectReader = new ObjectReader(assetsFile.reader, assetsFile, objectInfo);
                        try
                        {
                            _ = new MonoBehaviour(objectReader);
                            var n = (int)Math.Min(objectReader.Remaining, 512 * 1024);
                            if (n <= 0)
                                continue;
                            var tail = objectReader.ReadBytes(n);
                            foreach (Match m in AsciiRun.Matches(Encoding.UTF8.GetString(tail)))
                                allStrings.Add(m.Value);
                        }
                        catch
                        {
                            // skip corrupt behaviour blobs
                        }
                    }
                }

                var filtered = allStrings.Where(LooksInteresting).ToList();
                if (filtered.Count == 0 && allStrings.Count > 0)
                {
                    Console.WriteLine("  (no keyword hits — showing all runs length>=8)");
                    filtered = allStrings.Where(x => x.Length >= 8).ToList();
                }
                foreach (var s in filtered)
                    Console.WriteLine($"  {s}");

                Console.WriteLine();
                Console.WriteLine($"Total unique ASCII runs in MonoBehaviour tails: {allStrings.Count}");
            }
            finally
            {
                mgr.Clear();
            }
        }

        static bool LooksInteresting(string s)
        {
            if (s.Length >= 80)
                return true;
            var lower = s.ToLowerInvariant();
            if (lower.Contains("weapon") || lower.Contains("wpn") || lower.Contains("fx_") ||
                lower.Contains("effect") || lower.Contains("sound") || lower.Contains("wwise") ||
                lower.Contains("play_") || lower.Contains("ak") || lower.Contains("rail") ||
                lower.Contains("locus") || lower.Contains("2537") || lower.Contains("gunfire") ||
                lower.Contains("particle") || lower.Contains("assets/") || lower.Contains(".prefab"))
                return true;
            return false;
        }
    }
}
