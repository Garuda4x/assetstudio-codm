using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AssetStudio.Codm
{
    /// <summary>
    /// One character skin folder under cod_models/avatar/ (c_f_alias, seal6_005, pvebot/xxx...).
    /// The first-person arms ship inside the same bundle as their own prefab.
    /// </summary>
    public sealed class CharacterEntry
    {
        public string Name { get; set; }
        /// <summary>Sub folder such as pvebot or vfw; null for the plain avatar folder.</summary>
        public string Group { get; set; }
        /// <summary>cod_models/avatar[/group]/name</summary>
        public string Folder { get; set; }
        public List<PakEntry> Paks { get; } = new List<PakEntry>();

        public string DisplayName => Group != null ? Group + "/" + Name : Name;
        public override string ToString() => Folder;
    }

    public sealed class CharacterCatalog
    {
        private static readonly Regex AvatarPath = new Regex(@"^cod_models/avatar/([a-z0-9_]+)(?:/([a-z0-9_.]+))?(/.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        /// <summary>Folders under avatar/ that hold further character folders instead of being a character.</summary>
        private static readonly HashSet<string> GroupFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pvebot", "vfw", "bot", "npc", "pet" };

        public List<CharacterEntry> Characters { get; } = new List<CharacterEntry>();
        public Dictionary<string, CharacterEntry> ByFolder { get; } = new Dictionary<string, CharacterEntry>(StringComparer.OrdinalIgnoreCase);

        public static CharacterCatalog Build(PakIndex index)
        {
            var cat = new CharacterCatalog();
            foreach (var e in index.Entries)
            {
                var m = AvatarPath.Match(e.Name.Path);
                if (!m.Success) continue;
                var first = m.Groups[1].Value.ToLowerInvariant();
                string group = null, name = first;
                if (GroupFolders.Contains(first) && m.Groups[2].Success)
                {
                    group = first;
                    name = m.Groups[2].Value.ToLowerInvariant();
                    var dot = name.IndexOf('.');
                    if (dot > 0) name = name.Substring(0, dot); // version bucket like 13.0 is not a character
                    if (name.All(c => char.IsDigit(c) || c == '.')) continue;
                }
                var folder = "cod_models/avatar/" + (group != null ? group + "/" : "") + name;
                if (!cat.ByFolder.TryGetValue(folder, out var c))
                {
                    c = new CharacterEntry { Name = name, Group = group, Folder = folder };
                    cat.ByFolder[folder] = c;
                    cat.Characters.Add(c);
                }
                c.Paks.Add(e);
            }
            cat.Characters.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return cat;
        }

        /// <summary>Resolve a folder, a name or a substring.</summary>
        public List<CharacterEntry> Find(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<CharacterEntry>();
            query = query.Trim();
            if (ByFolder.TryGetValue(query, out var exact))
                return new List<CharacterEntry> { exact };
            if (ByFolder.TryGetValue("cod_models/avatar/" + query, out exact))
                return new List<CharacterEntry> { exact };
            var byName = Characters.Where(c => string.Equals(c.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count > 0)
                return byName;
            return Characters.Where(c => c.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }
    }
}
