using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AssetStudio.Codm.Wwise
{
    public sealed class BankExportResult
    {
        public string BankName { get; set; }
        public int SourcesTotal { get; set; }
        public int Extracted { get; set; }
        public int Converted { get; set; }
        public List<uint> Unresolved { get; } = new List<uint>();
        public List<string> Files { get; } = new List<string>();
    }

    /// <summary>Extract wem sources from banks and optionally convert them with vgmstream.</summary>
    public static class WemTools
    {
        /// <summary>Write one wem (embedded or loose) to <paramref name="outDir"/>/&lt;id&gt;.wem. Returns the path or null.</summary>
        public static string ExtractSource(WwiseIndex index, uint sourceId, string outDir)
        {
            var data = index.ReadSource(sourceId);
            if (data == null)
                return null;
            Directory.CreateDirectory(outDir);
            var path = Path.Combine(outDir, sourceId + ".wem");
            File.WriteAllBytes(path, data);
            return path;
        }

        /// <summary>
        /// Extract every wem a bank's Sound objects reference (resolved globally) plus any media the bank
        /// embeds that no Sound object names. Converts to wav when vgmstream is configured.
        /// </summary>
        public static BankExportResult ExportBank(WwiseIndex index, WwiseBankInfo bankInfo, string outDir, string vgmstreamPath, bool convert, Action<string> log = null)
        {
            var bank = WwiseBank.Parse(bankInfo.Path);
            var result = new BankExportResult { BankName = bank.Name };
            var ids = bank.SourceIds();
            foreach (var m in bank.Embedded)
                ids.Add(m.Id);
            result.SourcesTotal = ids.Count;
            Directory.CreateDirectory(outDir);

            foreach (var id in ids.OrderBy(x => x))
            {
                var wem = ExtractSource(index, id, outDir);
                if (wem == null)
                {
                    result.Unresolved.Add(id);
                    continue;
                }
                result.Extracted++;
                result.Files.Add(wem);
                if (convert && !string.IsNullOrEmpty(vgmstreamPath))
                {
                    var wav = ConvertWem(vgmstreamPath, wem, Path.ChangeExtension(wem, ".wav"), log);
                    if (wav != null)
                    {
                        result.Converted++;
                        result.Files.Add(wav);
                        try { File.Delete(wem); } catch { /* keep wem if delete fails */ }
                    }
                }
            }

            log?.Invoke($"[Wwise] {bank.Name}: {result.Extracted}/{result.SourcesTotal} wems extracted, {result.Converted} converted, {result.Unresolved.Count} unresolved");
            return result;
        }

        /// <summary>Run vgmstream-cli on one wem. Returns the wav path or null when conversion failed.</summary>
        public static string ConvertWem(string vgmstreamPath, string wemPath, string wavPath, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(vgmstreamPath) || !File.Exists(vgmstreamPath))
            {
                log?.Invoke($"[Wwise] vgmstream not found at '{vgmstreamPath}'");
                return null;
            }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = vgmstreamPath,
                    Arguments = $"-o \"{wavPath}\" \"{wemPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    var err = p.StandardError.ReadToEnd();
                    p.WaitForExit(60000);
                    if (p.ExitCode != 0 || !File.Exists(wavPath))
                    {
                        log?.Invoke($"[Wwise] vgmstream failed on {Path.GetFileName(wemPath)}: {err.Trim()}");
                        return null;
                    }
                }
                return wavPath;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Wwise] vgmstream error on {Path.GetFileName(wemPath)}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Look for vgmstream-cli.exe on PATH and in a few common places.</summary>
        public static string FindVgmstream()
        {
            var names = new[] { "vgmstream-cli.exe", "vgmstream.exe" };
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').ToList();
            dirs.Add(AppDomain.CurrentDomain.BaseDirectory);
            dirs.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vgmstream"));
            foreach (var d in dirs)
            {
                foreach (var n in names)
                {
                    try
                    {
                        var p = Path.Combine(d.Trim(), n);
                        if (File.Exists(p)) return p;
                    }
                    catch { /* bad PATH entry */ }
                }
            }
            return null;
        }
    }
}
