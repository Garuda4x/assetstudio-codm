using System;
using System.Linq;

namespace AssetStudio.Codm
{
    /// <summary>
    /// A decoded CoD Mobile pak file name.
    ///
    /// Raw layout (before decoding):
    ///   [module!ver!ver!]path[#category][@shard].pak
    ///
    /// The module prefix only exists for downloaded ("puffer") paks, e.g. LUEEUV!8!1! = common.8.1.
    /// '$' in the raw name is '/', '!' is '.', '#category' marks a split (tex / other / prefabs),
    /// '@n' is a shard index for bundles split into several files.
    /// </summary>
    public sealed class PakName
    {
        /// <summary>File name without the .pak extension, as found on disk.</summary>
        public string Raw { get; private set; }
        /// <summary>Download module such as "common" or "normal_weapon_cn24.9"; null for streaming paks.</summary>
        public string Module { get; private set; }
        /// <summary>Module version, e.g. "8.1"; null for streaming paks.</summary>
        public string ModuleVersion { get; private set; }
        /// <summary>Decoded bundle path, e.g. cod_models/weapons/mainweapon/mainweapon_1648_locusrailgun.</summary>
        public string Path { get; private set; }
        /// <summary>Split category after '#': tex, other, prefabs, fallback... null if none.</summary>
        public string Category { get; private set; }
        /// <summary>Shard index after '@', or -1.</summary>
        public int Shard { get; private set; } = -1;
        /// <summary>Human readable full form: module.version.path#category@shard.</summary>
        public string Decoded { get; private set; }

        public string[] Segments => Path.Split('/');
        public string LastSegment => Segments[Segments.Length - 1];
        public bool HasModule => Module != null;

        public static PakName Parse(string fileName)
        {
            var raw = fileName;
            var slash = raw.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0)
                raw = raw.Substring(slash + 1);
            if (raw.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(0, raw.Length - 4);

            var pn = new PakName { Raw = raw };
            var body = raw;

            // module!ver!ver!rest  -- the module itself may contain a literal '.', so split the raw string on '!'
            var parts = raw.Split('!');
            if (parts.Length >= 4)
            {
                var i = 1;
                while (i < parts.Length - 1 && IsDigits(parts[i]))
                    i++;
                if (i >= 3)
                {
                    pn.Module = CodmNameCipher.Decode(parts[0]);
                    pn.ModuleVersion = string.Join(".", parts, 1, i - 1);
                    body = string.Join("!", parts, i, parts.Length - i);
                }
            }

            var at = body.LastIndexOf('@');
            if (at >= 0 && int.TryParse(body.Substring(at + 1), out var shard))
            {
                pn.Shard = shard;
                body = body.Substring(0, at);
            }

            var hash = body.IndexOf('#');
            if (hash >= 0)
            {
                pn.Category = CodmNameCipher.Decode(body.Substring(hash + 1));
                body = body.Substring(0, hash);
            }

            pn.Path = CodmNameCipher.Decode(body);
            pn.Decoded = (pn.Module != null ? pn.Module + "." + pn.ModuleVersion + "." : "")
                         + pn.Path
                         + (pn.Category != null ? "#" + pn.Category : "")
                         + (pn.Shard >= 0 ? "@" + pn.Shard : "");
            return pn;
        }

        private static bool IsDigits(string s)
        {
            return s.Length > 0 && s.All(char.IsDigit);
        }

        public override string ToString() => Decoded;
    }
}
