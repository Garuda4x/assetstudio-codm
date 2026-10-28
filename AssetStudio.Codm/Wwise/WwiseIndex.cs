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
        /// <summary>
        /// Melee, throw and assi weapons name their banks with a prefix: m01, m30882a (melee), t04 (throw), a21p3
        /// (assi); the numbers are those weapons' ids. The prefix and number of such a bank; null / -1 otherwise.
        /// (sp&lt;n&gt; banks are not special weapons: none of their numbers is a special weapon id.)
        /// </summary>
        public string KindPrefix { get; set; }
        public int KindBase { get; set; } = -1;

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
        private static readonly Regex KindBank = new Regex(@"^(m|t|a)(\d+)[a-z]*(p\d)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public List<WwiseBankInfo> Banks { get; } = new List<WwiseBankInfo>();
        /// <summary>wem id -> first bank that embeds it.</summary>
        public Dictionary<uint, WwiseMedia> Media { get; } = new Dictionary<uint, WwiseMedia>();
        /// <summary>wem id -> loose .wem files on disk.</summary>
        public Dictionary<uint, List<string>> LooseWems { get; } = new Dictionary<uint, List<string>>();
        public List<string> ScannedFolders { get; } = new List<string>();
        public int TotalBankFiles { get; private set; }
        /// <summary>Resolved Wwise names (events, switches); null until <see cref="LoadNames"/>.</summary>
        public WwiseNames Names { get; private set; }
        /// <summary>Bank name -> weapon name tokens found in its event names (play_wpn_locus_fire_1p -> locus).</summary>
        public Dictionary<string, HashSet<string>> BankTokens { get; } = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

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
            var k = KindBank.Match(name);
            if (k.Success)
            {
                info.KindPrefix = k.Groups[1].Value.ToLowerInvariant();
                info.KindBase = int.Parse(k.Groups[2].Value);
            }
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

        /// <summary>Banks named &lt;prefix&gt;&lt;id&gt;, with letter and p3 variants: m01, m30882a, t04.</summary>
        public IEnumerable<WwiseBankInfo> BanksForKindId(string prefix, int id)
        {
            return Banks.Where(b => b.KindBase == id && string.Equals(b.KindPrefix, prefix, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Prefer the first copy in the preferred language, then non-HQ, then anything.</summary>
        public WwiseBankInfo PreferredCopy(string name)
        {
            var copies = FindBanks(name).ToList();
            if (copies.Count == 0)
                return null;
            return copies
                .OrderBy(b => b.Language == null ? 0 : (string.Equals(b.Language, preferredLanguage, StringComparison.OrdinalIgnoreCase) ? 1 : 2))
                .ThenBy(b => b.HighQuality ? 0 : 1) // Windows_HQ copies first when the game shipped one
                .First();
        }

        public sealed class HircRef
        {
            public byte Type { get; set; }
            public string Bank { get; set; }
            public int Copies { get; set; }
        }

        private Dictionary<uint, HircRef> hircObjects;

        /// <summary>Every HIRC object id (events, actions, sounds, containers...) across all banks, first bank wins. Built on first use.</summary>
        public Dictionary<uint, HircRef> HircObjects(Action<string> progress = null)
        {
            if (hircObjects != null)
                return hircObjects;
            var map = new Dictionary<uint, HircRef>();
            var done = 0;
            foreach (var info in Banks)
            {
                if (!info.HasHirc) continue;
                WwiseBank bank;
                try { bank = WwiseBank.Parse(info.Path); }
                catch (Exception) { continue; }
                foreach (var ev in bank.Events) AddRef(map, ev.Id, 4, info.Name);
                foreach (var a in bank.Actions) AddRef(map, a.Id, 3, info.Name);
                foreach (var snd in bank.Sounds) AddRef(map, snd.Id, 2, info.Name);
                foreach (var kv in bank.ObjectIds) AddRef(map, kv.Key, kv.Value, info.Name);
                done++;
                if (done % 1000 == 0) progress?.Invoke($"Wwise objects: {done} banks");
            }
            progress?.Invoke($"Wwise objects: {map.Count} ids from {done} banks");
            hircObjects = map;
            return map;
        }

        private static void AddRef(Dictionary<uint, HircRef> map, uint id, byte type, string bank)
        {
            if (map.TryGetValue(id, out var r)) { r.Copies++; return; }
            map[id] = new HircRef { Type = type, Bank = bank, Copies = 1 };
        }

        public string TokensCachePath(CodmConfig cfg) => cfg.CachePath("wwise-bank-tokens.json");

        /// <summary>Load the name cache and the bank-token cache; when the token cache is missing, build both by guessing.</summary>
        public void LoadNames(CodmConfig cfg, IEnumerable<string> weaponTokens, Action<string> progress = null, bool rebuild = false)
        {
            Names = WwiseNames.Load(cfg);
            var tokPath = TokensCachePath(cfg);
            if (!rebuild && File.Exists(tokPath))
            {
                try
                {
                    var d = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(File.ReadAllText(tokPath));
                    if (d != null)
                    {
                        BankTokens.Clear();
                        foreach (var kv in d)
                            BankTokens[kv.Key] = new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase);
                        return;
                    }
                }
                catch (Exception) { /* rebuild below */ }
            }
            NameBanks(cfg, weaponTokens, progress);
        }

        /// <summary>
        /// Guess event / switch names in every weapon bank (numeric and wpn_*). First a quick pass finds
        /// which weapon tokens a bank mentions, then the full action/suffix expansion runs for those tokens.
        /// </summary>
        public void NameBanks(CodmConfig cfg, IEnumerable<string> weaponTokens, Action<string> progress = null)
        {
            if (Names == null) Names = WwiseNames.Load(cfg);
            var tokens = new HashSet<string>(weaponTokens.Where(t => !string.IsNullOrEmpty(t)), StringComparer.OrdinalIgnoreCase);
            var bankNames = Banks.Where(b => b.HasHirc && (b.IsNumeric || b.KindPrefix != null || b.Name.StartsWith("wpn_", StringComparison.OrdinalIgnoreCase)))
                .Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            BankTokens.Clear();
            var done = 0; var named = 0;
            foreach (var name in bankNames)
            {
                var info = PreferredCopy(name);
                WwiseBank bank;
                try { bank = WwiseBank.Parse(info.Path); } catch (Exception) { continue; }
                var meleeBank = KindBank.IsMatch(name) && name.StartsWith("m", StringComparison.OrdinalIgnoreCase);
                var hit = Names.IdentifyTokens(bank, tokens, meleeBank);
                var full = new HashSet<string>(hit, StringComparer.OrdinalIgnoreCase);
                var weaponBank = IsNumericBankName(name) || KindBank.IsMatch(name);
                if (IsNumericBankName(name))
                    full.Add(name.TrimEnd('a','b','c','d','e','f','g','h','i','j','p','3'));
                else if (!weaponBank)
                    full.UnionWith(WwiseNames.Classes);
                named += Names.Guess(bank, full, includeClasses: !weaponBank, meleeBank: meleeBank);
                BankTokens[name] = hit;
                done++;
                if (done % 100 == 0) progress?.Invoke($"Wwise names: {done}/{bankNames.Count} banks, {Names.Count} names");
            }
            Names.Save();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TokensCachePath(cfg)));
                File.WriteAllText(TokensCachePath(cfg), Newtonsoft.Json.JsonConvert.SerializeObject(BankTokens.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()), Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception) { /* cache only */ }
            progress?.Invoke($"Wwise names: {Names.Count} resolved, {BankTokens.Count(kv => kv.Value.Count > 0)} of {done} weapon banks identified by name");
        }

        /// <summary>Banks whose event names mention the token (weapon name).</summary>
        public IEnumerable<WwiseBankInfo> BanksForToken(string token)
        {
            foreach (var kv in BankTokens)
                if (kv.Value.Contains(token))
                {
                    var info = PreferredCopy(kv.Key);
                    if (info != null) yield return info;
                }
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
