using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AssetStudio.Codm
{
    /// <summary>
    /// Sound class per base weapon (ar, smg, lmg, sniper, shotgun, pistol, launcher, melee), keyed by
    /// the normalized base weapon name. The class picks the shared Wwise bank (wpn_&lt;class&gt;).
    /// Values are inferred from skin banks where possible and otherwise set by hand; stored next to
    /// codm.config.json as weapon-classes.json.
    /// </summary>
    public sealed class WeaponClassMap
    {
        public const string FileName = "weapon-classes.json";
        public static readonly string[] KnownClasses = { "ar", "smg", "lmg", "sniper", "shotgun", "pistol", "launcher", "melee" };

        private readonly SortedDictionary<string, string> map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Path { get; private set; } = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);

        public static WeaponClassMap Load(CodmConfig cfg)
        {
            var m = new WeaponClassMap { Path = cfg.ResolveLocalPath(FileName) };
            if (File.Exists(m.Path))
            {
                try
                {
                    var d = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(m.Path));
                    if (d != null)
                        foreach (var kv in d)
                            m.map[kv.Key] = kv.Value;
                }
                catch (Exception)
                {
                    // a broken file behaves like an empty one; Save() rewrites it
                }
            }
            return m;
        }

        public void Save()
        {
            File.WriteAllText(Path, JsonConvert.SerializeObject(map, Formatting.Indented));
        }

        /// <summary>
        /// Built-in classes for the well-known CoD Mobile weapons, matched by longest prefix of the
        /// normalized base name (so "dlq33zealot" and "m4black" resolve too). The user file wins.
        /// </summary>
        private static readonly Dictionary<string, string> Seed = BuildSeed();

        private static Dictionary<string, string> BuildSeed()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(string cls, params string[] names) { foreach (var n in names) d[n] = cls; }
            Add("ar", "ak117", "ak47", "akilo47", "an94", "asm10", "asval", "bal27", "bk57", "cr56", "dingo", "drh", "em2", "f2000", "ffar", "fr556",
                "g36c", "grau556", "groza", "hbr", "hvk30", "icr", "kilo141", "kn44", "krig6", "lachmann556", "lk24", "m13", "m16", "m27", "m4",
                "maddox", "manowar", "oden", "peacekeeper", "ram7", "swordfish", "type19", "type25", "xm4");
            Add("smg", "agr556", "aks74u", "ak74u", "cbr4", "chicom", "cordite", "cx9", "fennec", "gks", "hg40", "iso", "ksp45", "lapa", "mac10", "miniuzi",
                "mp5", "mp7", "msmc", "mx9", "ots9", "p90", "pdw57", "pharo", "pp19", "pp9", "ppsh41", "qq9", "qxr", "razorback", "rus79u", "smg5",
                "tec9", "ump", "uss9", "uzi", "vargo", "vectork10", "vmp");
            Add("lmg", "bruen", "chopper", "dp27", "hades", "holger26", "m249", "m4lmg", "m60", "mg42", "mg82", "mk48", "pkm", "raal", "rpd", "s36", "ul736");
            Add("sniper", "amr", "arctic50", "crossbow", "dlq33", "hdr", "kar98k", "kiloboltaction", "koshka", "locus", "lw3", "m1garand", "m21ebr",
                "m82a1", "mk2", "na45", "outlaw", "rytec", "sks", "so14", "spr208", "svd", "svu", "xpr50", "zrg20");
            Add("shotgun", "725", "argus", "by15", "echo", "hs0405", "hs2126", "jak12", "krm262", "m1014", "m1216", "m1887", "r90", "sg725", "spars12", "striker");
            Add("pistol", "50gs", "annihilator", "j358", "lcar9", "machinepistol", "mw11", "p1911", "p226", "renetti", "sykov", "usp45");
            Add("launcher", "d13sector", "fhj", "rpg", "smrs", "thumper", "warmachine");
            return d;
        }

        /// <summary>Class from the user file (exact normalized name), else the longest seed prefix; "" source tells which.</summary>
        public string Get(string baseName, out string source)
        {
            var key = WeaponCatalog.NormalizeName(baseName);
            if (map.TryGetValue(key, out var c))
            {
                source = "map";
                return c;
            }
            string best = null;
            var bestLen = 0;
            foreach (var kv in Seed)
            {
                if (kv.Key.Length > bestLen && key.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    best = kv.Value;
                    bestLen = kv.Key.Length;
                }
            }
            source = best != null ? "seed" : null;
            return best;
        }

        public string Get(string baseName)
        {
            return Get(baseName, out _);
        }

        public void Set(string baseName, string weaponClass)
        {
            var key = WeaponCatalog.NormalizeName(baseName);
            if (string.IsNullOrEmpty(weaponClass))
                map.Remove(key);
            else
                map[key] = weaponClass.ToLowerInvariant();
        }

        public IReadOnlyDictionary<string, string> All => map;
        public int Count => map.Count;
    }
}
