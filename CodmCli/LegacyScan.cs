using System;
using System.IO;
using System.Linq;
using System.Text;

namespace CodmCli
{
    /// <summary>Console output for the metadata scanner (the original PakScanner tool).</summary>
    internal static class LegacyScan
    {
        public static void PrintPakInfo(PakInfo info, bool verbose)
        {
            Console.WriteLine($"File:      {info.FileName}");
            Console.WriteLine($"Decoded:   {AssetStudio.Codm.PakName.Parse(info.FileName).Decoded}");
            Console.WriteLine($"Size:      {FormatSize(info.FileSize)}");
            Console.WriteLine($"Summary:   {info.GetSummary()}");
            if (info.Error != null)
            {
                Console.WriteLine($"Error:     {info.Error}");
                return;
            }
            Console.WriteLine($"Signature: {info.Signature}");
            Console.WriteLine($"Bundle V:  {info.BundleVersion}");
            Console.WriteLine($"Unity:     {info.UnityVersion} / {info.UnityRevision}");
            Console.WriteLine($"\nInternal Files ({info.InternalFiles.Count}):");
            foreach (var f in info.InternalFiles)
                Console.WriteLine($"  {f}");
            Console.WriteLine("\nAsset Types:");
            foreach (var kv in info.AssetTypeCounts.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"  {kv.Key,-30} x{kv.Value}");
            if (info.ContainerPaths.Count > 0)
            {
                Console.WriteLine($"\nContainer Paths ({info.ContainerPaths.Count}):");
                var paths = verbose ? info.ContainerPaths : info.ContainerPaths.Take(20).ToList();
                foreach (var p in paths.OrderBy(p => p))
                    Console.WriteLine($"  {p}");
                if (!verbose && info.ContainerPaths.Count > 20)
                    Console.WriteLine($"  ... and {info.ContainerPaths.Count - 20} more (use -v to see all)");
            }
            if (info.AssetNames.Count > 0)
            {
                Console.WriteLine($"\nAsset Names ({info.AssetNames.Count}):");
                var names = verbose ? info.AssetNames : info.AssetNames.Take(30).ToList();
                foreach (var n in names.Distinct().OrderBy(n => n))
                    Console.WriteLine($"  {n}");
                if (!verbose && info.AssetNames.Distinct().Count() > 30)
                    Console.WriteLine("  ... and more (use -v to see all)");
            }
        }

        public static void ScanDirectoryToConsole(string path, string pattern, bool verbose, string csvOut)
        {
            Console.WriteLine($"Scanning directory: {path}  pattern: {pattern}");
            var results = Scanner.ScanDirectory(path, pattern);
            var sb = new StringBuilder();
            sb.AppendLine($"{"Decoded name",-70} | {"Contents",-40} | {"Size",-10} | {"Types"}");
            sb.AppendLine(new string('-', 150));
            foreach (var info in results)
            {
                var name = AssetStudio.Codm.PakName.Parse(info.FileName).Decoded;
                if (name.Length > 68) name = name.Substring(0, 65) + "...";
                var summary = info.GetSummary();
                if (summary.Length > 38) summary = summary.Substring(0, 35) + "...";
                var types = info.AssetTypeCounts.Count > 0
                    ? string.Join(", ", info.AssetTypeCounts.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Value}x{kv.Key}"))
                    : "-";
                sb.AppendLine($"{name,-70} | {summary,-40} | {FormatSize(info.FileSize),-10} | {types}");
            }
            sb.AppendLine($"Total: {results.Count} files scanned");
            Console.WriteLine(sb.ToString());

            if (verbose)
            {
                foreach (var info in results.Where(r => r.ContainerPaths.Count > 0))
                {
                    Console.WriteLine($"\n--- {info.FileName} ---");
                    foreach (var container in info.ContainerPaths.OrderBy(c => c))
                        Console.WriteLine($"  {container}");
                }
            }

            if (csvOut != null)
            {
                var csv = new StringBuilder();
                csv.AppendLine("FileName,Decoded,FileSize,Summary,UnityVersion,InternalFiles,AssetTypes,ContainerPaths,AssetNames");
                foreach (var info in results)
                {
                    csv.AppendLine(string.Join(",",
                        CsvEscape(info.FileName),
                        CsvEscape(AssetStudio.Codm.PakName.Parse(info.FileName).Decoded),
                        info.FileSize,
                        CsvEscape(info.GetSummary()),
                        CsvEscape(info.UnityRevision ?? ""),
                        CsvEscape(string.Join("; ", info.InternalFiles)),
                        CsvEscape(string.Join("; ", info.AssetTypeCounts.Select(kv => $"{kv.Value}x {kv.Key}"))),
                        CsvEscape(string.Join("; ", info.ContainerPaths.Take(20))),
                        CsvEscape(string.Join("; ", info.AssetNames.Distinct().Take(30)))));
                }
                File.WriteAllText(csvOut, csv.ToString(), Encoding.UTF8);
                Console.WriteLine($"Results saved to: {csvOut}");
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024.0 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        private static string CsvEscape(string value)
        {
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}
