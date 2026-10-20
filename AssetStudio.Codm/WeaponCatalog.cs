using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AssetStudio.Codm.Wwise;

namespace AssetStudio.Codm
{
    public enum WeaponKind
    {
        MainWeapon,
        MeleeWeapon,
        ThrowWeapon,
        AssiWeapon,
        SpecialWeapon,
    }

    /// <summary>
    /// One weapon folder under cod_models/weapons/&lt;kind&gt;/, e.g. mainweapon_1648_locusrailgun.
    /// A "base" weapon is one shipped in a normal_baseweapon module (it carries the mesh);
    /// skins are matched to their base by name prefix.
    /// </summary>
    public sealed class WeaponEntry
    {
        public WeaponKind Kind { get; set; }
        public string KindFolder { get; set; }
        public int Id { get; set; }
        public string Name { get; set; }
        /// <summary>Folder name: mainweapon_1648_locusrailgun.</summary>
        public string Folder { get; set; }
        /// <summary>Every pak whose path lives under this weapon folder (all sources, sub-versions and #categories).</summary>
        public List<PakEntry> Paks { get; } = new List<PakEntry>();
        public bool IsBase { get; set; }
        public WeaponEntry Base { get; set; }
        public List<WeaponEntry> Skins { get; } = new List<WeaponEntry>();
        /// <summary>Component paks matched by name (sights are per base weapon).</summary>
        public List<PakEntry> Components { get; } = new List<PakEntry>();
        /// <summary>cod_effects paks whose name mentions this weapon or its base.</summary>
        public List<PakEntry> Effects { get; } = new List<PakEntry>();
        /// <summary>Wwise banks: numeric banks matching the id plus named banks mentioning the weapon.</summary>
        public List<WwiseBankInfo> Banks { get; } = new List<WwiseBankInfo>();
        /// <summary>Class banks (wpn_ar, wpn_sniper, ...) that the family's skin banks borrow media from.</summary>
        public List<WwiseBankInfo> SharedBanks { get; } = new List<WwiseBankInfo>();

        public string BaseName => Base?.Name ?? Name;
        public override string ToString() => Folder;
    }

    public sealed class WeaponCatalog
    {
        private static readonly Regex WeaponPath = new Regex(
            @"^cod_models/weapons/(mainweapon|meleeweapon|throwweapon|assiweapon|specialweapon)/([a-z]+_(\d+)_([a-z0-9_]+?))(/.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SkinPrefix = new Regex(@"^(legendary|mythic|baseversion|epic|ultra|basic|base)", RegexOptions.Compiled);
        private static readonly Regex VersionSuffix = new Regex(@"v\d+$", RegexOptions.Compiled);

        public List<WeaponEntry> Weapons { get; } = new List<WeaponEntry>();
        public Dictionary<string, WeaponEntry> ByFolder { get; } = new Dictionary<string, WeaponEntry>(StringComparer.OrdinalIgnoreCase);
        /// <summary>cod_models/weapons/animations* and animationcontrollers*: shared, not attributable to one weapon.</summary>
        public List<PakEntry> SharedAnimationPaks { get; } = new List<PakEntry>();
        /// <summary>cod_models/weapons/component*: attachments bucketed by version, only sights are per weapon.</summary>
        public List<PakEntry> ComponentPaks { get; } = new List<PakEntry>();
        public List<PakEntry> CamouflagePaks { get; } = new List<PakEntry>();
        /// <summary>cod_effects/particles/weapon/*: generic gunfire, hit, trail, shell effects.</summary>
        public List<PakEntry> GenericWeaponFxPaks { get; } = new List<PakEntry>();
        public List<PakEntry> AllEffectPaks { get; } = new List<PakEntry>();

        public static WeaponCatalog Build(PakIndex index, WwiseIndex wwise = null)
        {
            var cat = new WeaponCatalog();

            foreach (var e in index.Entries)
            {
                var path = e.Name.Path;
                var m = WeaponPath.Match(path);
                if (m.Success)
                {
                    var folder = m.Groups[2].Value.ToLowerInvariant();
                    if (!cat.ByFolder.TryGetValue(folder, out var w))
                    {
                        w = new WeaponEntry
                        {
                            KindFolder = m.Groups[1].Value.ToLowerInvariant(),
                            Kind = ParseKind(m.Groups[1].Value),
                            Id = int.Parse(m.Groups[3].Value),
                            Name = m.Groups[4].Value.ToLowerInvariant(),
                            Folder = folder,
                        };
                        cat.ByFolder[folder] = w;
                        cat.Weapons.Add(w);
                    }
                    w.Paks.Add(e);
                    if (e.Name.Module != null && e.Name.Module.StartsWith("normal_baseweapon", StringComparison.OrdinalIgnoreCase))
                        w.IsBase = true;
                    continue;
                }

                if (path.StartsWith("cod_models/weapons/animation", StringComparison.OrdinalIgnoreCase))
                    cat.SharedAnimationPaks.Add(e);
                else if (path.StartsWith("cod_models/weapons/component", StringComparison.OrdinalIgnoreCase))
                    cat.ComponentPaks.Add(e);
                else if (path.StartsWith("cod_models/weapons/camouflage", StringComparison.OrdinalIgnoreCase))
                    cat.CamouflagePaks.Add(e);

                if (path.StartsWith("cod_effects/", StringComparison.OrdinalIgnoreCase))
                {
                    cat.AllEffectPaks.Add(e);
                    if (path.StartsWith("cod_effects/particles/weapon", StringComparison.OrdinalIgnoreCase))
                        cat.GenericWeaponFxPaks.Add(e);
                }
            }

            cat.Weapons.Sort((a, b) =>
            {
                var k = a.Kind.CompareTo(b.Kind);
                return k != 0 ? k : a.Id.CompareTo(b.Id);
            });

            cat.LinkBases();
            cat.LinkComponents();
            cat.LinkEffects();
            if (wwise != null)
                cat.LinkBanks(wwise);
            return cat;
        }

        private static WeaponKind ParseKind(string folder)
        {
            switch (folder.ToLowerInvariant())
            {
                case "meleeweapon": return WeaponKind.MeleeWeapon;
                case "throwweapon": return WeaponKind.ThrowWeapon;
                case "assiweapon": return WeaponKind.AssiWeapon;
                case "specialweapon": return WeaponKind.SpecialWeapon;
                default: return WeaponKind.MainWeapon;
            }
        }

        /// <summary>Strip cosmetic prefixes/suffixes so "mythicpeacekeepermk1" and "baseversionpeacekeepermk1v72" both become "peacekeepermk1".</summary>
        public static string NormalizeName(string name)
        {
            var n = name.ToLowerInvariant();
            n = SkinPrefix.Replace(n, "");
            n = VersionSuffix.Replace(n, "");
            return n.Length > 0 ? n : name.ToLowerInvariant();
        }

        /// <summary>
        /// A skin's name starts with its base weapon's name (locus -> locusrailgun). A weapon counts as a
        /// base when it ships in a normal_baseweapon module, or when it is the longest such prefix of
        /// another weapon of the same kind. Module-flagged bases win ties with equal names
        /// (baseversionpeacekeepermk1v72 is the base of mythicpeacekeepermk1).
        /// </summary>
        private void LinkBases()
        {
            foreach (var kindGroup in Weapons.GroupBy(w => w.Kind))
            {
                var all = kindGroup.Select(w => (entry: w, norm: NormalizeName(w.Name))).ToList();
                foreach (var w in all)
                {
                    if (w.norm.Length < 2)
                        continue;
                    (WeaponEntry entry, string norm) best = (null, null);
                    foreach (var b in all)
                    {
                        if (b.entry == w.entry || b.norm.Length < 2)
                            continue;
                        var isPrefix = w.norm.Length > b.norm.Length
                            ? w.norm.StartsWith(b.norm)
                            : w.norm == b.norm && b.entry.IsBase && !w.entry.IsBase;
                        if (!isPrefix)
                            continue;
                        if (best.entry == null
                            || b.norm.Length > best.norm.Length
                            || (b.norm.Length == best.norm.Length && b.entry.IsBase && !best.entry.IsBase)
                            || (b.norm.Length == best.norm.Length && b.entry.IsBase == best.entry.IsBase && b.entry.Id < best.entry.Id))
                            best = b;
                    }
                    if (best.entry == null)
                        continue;
                    if (w.entry.IsBase && !best.entry.IsBase)
                        continue; // a flagged base is never a skin of an unflagged one
                    w.entry.Base = best.entry;
                    best.entry.Skins.Add(w.entry);
                }
            }

            foreach (var w in Weapons)
            {
                if (w.Skins.Count > 0 && w.Base == null)
                    w.IsBase = true;
                w.Skins.Sort((a, b) => a.Id.CompareTo(b.Id));
            }
        }

        private void LinkComponents()
        {
            // sights are named sig9x_<baseweapon>; everything else is a version bucket shared by many weapons
            foreach (var w in Weapons)
            {
                var names = NameTokens(w);
                foreach (var c in ComponentPaks)
                {
                    var last = c.Name.LastSegment.ToLowerInvariant();
                    var tokens = last.Split('_');
                    if (tokens.Length >= 2 && names.Contains(tokens[tokens.Length - 1]))
                        w.Components.Add(c);
                }
            }
        }

        private void LinkEffects()
        {
            foreach (var w in Weapons)
            {
                var names = NameTokens(w);
                foreach (var fx in AllEffectPaks)
                {
                    if (GenericWeaponFxPaks.Contains(fx))
                        continue;
                    var tokens = fx.Name.Path.ToLowerInvariant().Split('/', '_', '-');
                    if (tokens.Any(t => t.Length >= 3 && names.Contains(t)))
                        w.Effects.Add(fx);
                }
            }
        }

        private void LinkBanks(WwiseIndex wwise)
        {
            foreach (var w in Weapons)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in wwise.BanksForNumericId(w.Id))
                    if (seen.Add(b.Name)) w.Banks.Add(b);

                var names = NameTokens(w);
                foreach (var b in wwise.Banks)
                {
                    if (b.IsNumeric || seen.Contains(b.Name))
                        continue;
                    var tokens = b.Name.ToLowerInvariant().Split('_', '-', ' ');
                    var hit = tokens.Any(t => t.Length >= 4 && names.Any(n => n == t || (n.Length >= 4 && (n.StartsWith(t) || t.StartsWith(n)))));
                    if (hit && seen.Add(b.Name))
                        w.Banks.Add(b);
                }
            }

            // Skin banks pull their base weapon sounds from the class banks (wpn_ar, wpn_sniper, ...).
            // Follow those references once per weapon family so every member knows its class bank.
            var referencedCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var family in Weapons.GroupBy(w => w.Base ?? w))
            {
                var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var member in family.Concat(new[] { family.Key }).Distinct())
                {
                    foreach (var b in member.Banks.Where(b => b.IsNumeric))
                    {
                        if (!referencedCache.TryGetValue(b.Name, out var refs))
                            referencedCache[b.Name] = refs = wwise.ReferencedBankNames(b);
                        foreach (var r in refs)
                            if (!r.Equals(b.Name, StringComparison.OrdinalIgnoreCase) && !wwise.IsNumericBankName(r))
                                shared.Add(r);
                    }
                }
                if (shared.Count == 0)
                    continue;
                foreach (var member in family.Concat(new[] { family.Key }).Distinct())
                {
                    foreach (var name in shared)
                    {
                        if (member.Banks.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var info = wwise.PreferredCopy(name);
                        if (info != null && !member.SharedBanks.Contains(info))
                            member.SharedBanks.Add(info);
                    }
                }
            }
        }

        /// <summary>Lower-case name tokens that identify the weapon: its own name, normalized name and base name.</summary>
        private static HashSet<string> NameTokens(WeaponEntry w)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { w.Name, NormalizeName(w.Name) };
            if (w.Base != null)
            {
                set.Add(w.Base.Name);
                set.Add(NormalizeName(w.Base.Name));
            }
            return set;
        }

        /// <summary>Resolve "1648", "mainweapon_1648_locusrailgun" or "locusrailgun" to entries.</summary>
        public List<WeaponEntry> Find(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<WeaponEntry>();
            query = query.Trim();
            if (ByFolder.TryGetValue(query, out var exact))
                return new List<WeaponEntry> { exact };
            if (int.TryParse(query, out var id))
                return Weapons.Where(w => w.Id == id).ToList();
            var byName = Weapons.Where(w => string.Equals(w.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count > 0)
                return byName;
            return Weapons.Where(w => w.Folder.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }
    }
}
