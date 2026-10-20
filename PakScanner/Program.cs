using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PakScanner
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length == 0)
            {
                PrintUsage();
                return;
            }

            var path = args[0];
            var outputFile = args.Length > 1 ? args[1] : null;
            var verbose = args.Any(a => a == "-v" || a == "--verbose");
            var monoDump = args.Any(a => a == "--mono" || a == "-m");

            if (File.Exists(path))
            {
                if (monoDump)
                {
                    MonoDumper.DumpFile(path);
                    return;
                }

                // Single file
                var info = Scanner.ScanFile(path);
                PrintPakInfo(info, verbose);
            }
            else if (Directory.Exists(path))
            {
                // Directory scan
                var pattern = args.Length > 1 && !args[1].StartsWith("-") && outputFile == null ? args[1] : "*";
                if (args.Length > 1 && !args[1].StartsWith("-"))
                {
                    pattern = args[1];
                    outputFile = args.Length > 2 ? args[2] : null;
                }

                Console.WriteLine($"Scanning directory: {path}");
                Console.WriteLine($"Pattern: {pattern}");
                Console.WriteLine();

                var results = Scanner.ScanDirectory(path, pattern);

                // Print table
                var sb = new StringBuilder();
                sb.AppendLine("=== PAK SCANNER RESULTS ===");
                sb.AppendLine();

                // Summary table
                sb.AppendLine(new string('=', 140));
                sb.AppendLine($"{"Obfuscated Name",-60} | {"Contents",-50} | {"Size",-10} | {"Types"}");
                sb.AppendLine(new string('-', 140));

                foreach (var info in results)
                {
                    var name = info.FileName.Length > 58 ? info.FileName.Substring(0, 55) + "..." : info.FileName;
                    var summary = info.GetSummary();
                    if (summary.Length > 48) summary = summary.Substring(0, 45) + "...";
                    var size = FormatSize(info.FileSize);
                    var types = info.AssetTypeCounts.Count > 0
                        ? string.Join(", ", info.AssetTypeCounts.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Value}x{kv.Key}"))
                        : "-";
                    if (types.Length > 30) types = types.Substring(0, 27) + "...";

                    sb.AppendLine($"{name,-60} | {summary,-50} | {size,-10} | {types}");
                }
                sb.AppendLine(new string('=', 140));
                sb.AppendLine($"Total: {results.Count} files scanned");

                var tableText = sb.ToString();
                Console.WriteLine(tableText);

                // Verbose: print all container paths per file
                if (verbose)
                {
                    Console.WriteLine();
                    Console.WriteLine("=== DETAILED CONTAINER PATHS ===");
                    foreach (var info in results.Where(r => r.ContainerPaths.Count > 0))
                    {
                        Console.WriteLine();
                        Console.WriteLine($"--- {info.FileName} ---");
                        foreach (var container in info.ContainerPaths.OrderBy(c => c))
                        {
                            Console.WriteLine($"  {container}");
                        }
                        if (info.AssetNames.Count > 0)
                        {
                            Console.WriteLine($"  Assets: {string.Join(", ", info.AssetNames.Distinct().Take(20))}");
                        }
                    }
                }

                // Save to file
                if (outputFile != null)
                {
                    // Save as CSV for easy filtering
                    var csvSb = new StringBuilder();
                    csvSb.AppendLine("FileName,FileSize,Summary,UnityVersion,InternalFiles,AssetTypes,ContainerPaths,AssetNames");
                    foreach (var info in results)
                    {
                        csvSb.AppendLine(string.Join(",",
                            CsvEscape(info.FileName),
                            info.FileSize,
                            CsvEscape(info.GetSummary()),
                            CsvEscape(info.UnityRevision ?? ""),
                            CsvEscape(string.Join("; ", info.InternalFiles)),
                            CsvEscape(string.Join("; ", info.AssetTypeCounts.Select(kv => $"{kv.Value}x {kv.Key}"))),
                            CsvEscape(string.Join("; ", info.ContainerPaths.Take(20))),
                            CsvEscape(string.Join("; ", info.AssetNames.Distinct().Take(30)))
                        ));
                    }

                    File.WriteAllText(outputFile, csvSb.ToString(), Encoding.UTF8);
                    Console.WriteLine($"\nResults saved to: {outputFile}");
                }
            }
            else
            {
                Console.Error.WriteLine($"Path not found: {path}");
            }
        }

        static void PrintPakInfo(PakInfo info, bool verbose)
        {
            Console.WriteLine($"File:      {info.FileName}");
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

            Console.WriteLine($"\nAsset Types:");
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
                    Console.WriteLine($"  ... and more (use -v to see all)");
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("PakScanner — Identify obfuscated Unity .pak files");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  PakScanner <file.pak>              Scan a single file");
            Console.WriteLine("  PakScanner <file.pak> -v           Scan with verbose output");
            Console.WriteLine("  PakScanner <file.pak> --mono       Full load; dump MonoScript/MonoBehaviour + strings");
            Console.WriteLine("  PakScanner <directory>             Scan all *.pak files in directory");
            Console.WriteLine("  PakScanner <directory> *.pak       Scan with custom pattern");
            Console.WriteLine("  PakScanner <directory> *.pak out.csv  Scan and export to CSV");
            Console.WriteLine("  PakScanner <directory> -v          Scan with detailed container paths");
            Console.WriteLine();
            Console.WriteLine("The scanner reads bundle headers and asset metadata to identify");
            Console.WriteLine("pak contents without fully loading all assets.");
        }

        static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024.0 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        static string CsvEscape(string value)
        {
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}
