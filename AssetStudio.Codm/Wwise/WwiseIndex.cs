using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AssetStudio.Codm.Wwise
{
    public sealed class WwiseBankInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        /// <summary>Language sub folder (English(US), Chinese(PRC)) or null.</summary>
        public string Language { get; set; }
        /// <summary>True for the Windows_HQ tree.</summary>
        public bool HighQuality { get; set; }
        public long Size { get; set; }
        public uint BankId { get; set; }
        public int EmbeddedCount { get; set; }
        public bool HasHirc { get; set; }

        public bool IsNumeric => NumericBase >= 0;
        /// <summary>For banks named like 130, 130a, 130p3: the leading number; otherwise -1.</summary>
        public int NumericBase { get; set; } = -1;

        public override string ToString() => Name + (Language != null ? $" [{Language}]" : "") + (HighQuality ? " [HQ]" : "");
    }

    /// <summary>
    /// Index of every SoundBank and loose .wem under the configured bank folders, plus a global
    /// media map (wem id -> the bank that embeds it). CoD Mobile banks reference media embedded in
    /// other banks (a skin bank pulls base weapon sounds from wpn_ar.bnk etc.), so resolution must
    /// be global.
    /// </summary>
    public sealed class WwiseIndex
    {
        private static readonly Regex NumericBank = new Regex(@"^(\d+)[a-z]*(p\d)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public List<WwiseBankInfo> Banks { get; } = new List<WwiseBankInfo>();
        /// <summary>wem id -> first bank that embeds it.</summary>
        public Dictionary<uint, WwiseMedia> Media { get; } = new Dictionary<uint, WwiseMedia>();
        /// <summary>wem id -> loose .wem files on disk.</summary>
        public Dictionary<uint, List<string>> LooseWems { get; } = new Dictionary<uint, List<string>>();
        public List<string> ScannedFolders { get; } = new List<string>();
        public int TotalBankFiles { get; private set; }

        private readonly string preferredLanguage;

        private WwiseIndex(string preferredLanguage)
        {
            this.preferredLanguage = preferredLanguage;
        }

        public static WwiseIndex Build(CodmConfig cfg, Action<string> progress = null)
        {
            var idx = new WwiseIndex(cfg.PreferredLanguage);
            foreach (var folder in cfg.SoundBankFolderPaths)
            {
                if (!Directory.Exists(folder))
                {
                    progress?.Invoke($"Sound bank folder not found: {folder}");
                    continue;
                }
                var hq = folder.IndexOf("_HQ", StringComparison.OrdinalIgnoreCase) >= 0;
                var banks = 0;
                var wems = 0;
                foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
                    {
                        idx.AddBank(file, folder, hq);
                        banks++;
                    }
                    else if (file.EndsWith(".wem", StringComparison.OrdinalIgnoreCase))
                    {
                        var stem = Path.GetFileNameWithoutExtension(file);
                        if (uint.TryParse(stem, out var id))
                        {
                            if (!idx.LooseWems.TryGetValue(id, out var list))
                                idx.LooseWems[id] = list = new List<string>();
                            list.Add(file);
                            wems++;
                        }
                    }
                }
                idx.ScannedFolders.Add(folder);
                progress?.Invoke($"{banks} banks, {wems} loose wems in {folder}");
            }
            idx.TotalBankFiles = idx.Banks.Count;
            idx.Banks.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return idx;
        }

        private void AddBank(string file, string root, bool hq)
        {
            WwiseBank bank;
            try
            {
                bank = WwiseBank.Parse(file, parseHirc: false);
            }
            catch (Exception)
            {
                return;
            }
            var rel = file.Substring(root.Length).TrimStart('\\', '/');
            var relDir = Path.GetDirectoryName(rel) ?? "";
            string lang = null;
            foreach (var seg in relDir.Split('\\', '/'))
            {
                if (seg.Contains("(") && seg.Contains(")"))
                    lang = seg;
            }
            var name = Path.GetFileNameWithoutExtension(file);
            var info = new WwiseBankInfo
            {
                Name = name,
                Path = file,
                Language = lang,
                HighQuality = hq,
                Size = bank.FileSize,
                BankId = bank.BankId,
                EmbeddedCount = bank.Embedded.Count,
                HasHirc = bank.HasHirc,
            };
            var m = NumericBank.Match(name);
            if (m.Success)
                info.NumericBase = int.Parse(m.Groups[1].Value);
            Banks.Add(info);

            foreach (var media in bank.Embedded)
            {
                if (!Media.ContainsKey(media.Id))
                    Media[media.Id] = media;
            }
        }

        public IEnumerable<WwiseBankInfo> FindBanks(string name)
        {
            return Banks.Where(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Banks named &lt;id&gt;, &lt;id&gt;a.., &lt;id&gt;p3: the custom sound set of a weapon skin.</summary>
        public IEnumerable<WwiseBankInfo> BanksForNumericId(int id)
        {
            return Banks.Where(b => b.NumericBase == id);
        }

        /// <summary>Prefer the first copy in the preferred language, then non-HQ, then anything.</summary>
        public WwiseBankInfo PreferredCopy(string name)
        {
            var copies = FindBanks(name).ToList();
            if (copies.Count == 0)
                return null;
            return copies
                .OrderBy(b => b.Language == null ? 0 : (string.Equals(b.Language, preferredLanguage, StringComparison.OrdinalIgnoreCase) ? 1 : 2))
                .ThenBy(b => b.HighQuality ? 1 : 0)
                .First();
        }

        public bool IsNumericBankName(string name)
        {
            return NumericBank.IsMatch(name);
        }

        /// <summary>Banks that embed the media this bank's Sound objects reference (itself included), with how many sounds each one provides.</summary>
        public Dictionary<string, int> ReferencedBankNames(WwiseBankInfo info)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            WwiseBank bank;
            try
            {
                bank = WwiseBank.Parse(info.Path);
            }
            catch (Exception)
            {
                return result;
            }
            foreach (var id in bank.SourceIds())
            {
                if (Media.TryGetValue(id, out var media))
                {
                    var name = Path.GetFileNameWithoutExtension(media.BankPath);
                    result[name] = result.TryGetValue(name, out var n) ? n + 1 : 1;
                }
            }
            return result;
        }

        /// <summary>Find where a wem lives: embedded in some bank, or as a loose file.</summary>
        public bool TryResolve(uint sourceId, out WwiseMedia media, out string loosePath)
        {
            media = null;
            loosePath = null;
            if (Media.TryGetValue(sourceId, out media))
                return true;
            if (LooseWems.TryGetValue(sourceId, out var paths) && paths.Count > 0)
            {
                loosePath = paths.FirstOrDefault(p => p.IndexOf(preferredLanguage, StringComparison.OrdinalIgnoreCase) >= 0) ?? paths[0];
                return true;
            }
            return false;
        }

        public byte[] ReadSource(uint sourceId)
        {
            if (!TryResolve(sourceId, out var media, out var loose))
                return null;
            return media != null ? WwiseBank.ReadMedia(media) : File.ReadAllBytes(loose);
        }
    }
}
