using System.Text;

namespace AssetStudio.Codm
{
    /// <summary>
    /// Decoder for CoD Mobile's obfuscated asset names (pak files, DataTableBytes, 0db.bytes entries).
    ///
    /// The scheme is a fixed 13-pair letter swap with the case flipped, digits untouched,
    /// '$' standing in for '/' and '!' for '.'. It is an involution: decoding twice gives the input back.
    ///
    ///   a-p  c-l  e-m  d-j  i-z  n-v  o-u  r-t  s-f  w-x  g-h  b-k  q-y
    ///
    /// Example: LUJ_EUJMCF$XMPAUVF$EPZVXMPAUV$EPZVXMPAUV_1648_CULOFTPZCHOV
    ///       -> cod_models/weapons/mainweapon/mainweapon_1648_locusrailgun
    /// </summary>
    public static class CodmNameCipher
    {
        private static readonly string[] Pairs = { "ap", "cl", "em", "dj", "iz", "nv", "ou", "rt", "sf", "wx", "gh", "bk", "qy" };
        private static readonly char[] Partner = new char[128];

        static CodmNameCipher()
        {
            for (var i = 0; i < Partner.Length; i++)
                Partner[i] = (char)i;
            foreach (var p in Pairs)
            {
                Partner[p[0]] = p[1];
                Partner[p[1]] = p[0];
                Partner[char.ToUpperInvariant(p[0])] = char.ToUpperInvariant(p[1]);
                Partner[char.ToUpperInvariant(p[1])] = char.ToUpperInvariant(p[0]);
            }
        }

        /// <summary>Swap every ASCII letter with its partner and flip its case. Everything else passes through.</summary>
        public static string SwapLetters(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (ch < 128 && char.IsLetter(ch))
                {
                    var p = Partner[ch];
                    sb.Append(char.IsUpper(ch) ? char.ToLowerInvariant(p) : char.ToUpperInvariant(p));
                }
                else
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }

        /// <summary>Decode a raw obfuscated name: letters swapped and case flipped, '$' to '/', '!' to '.'.</summary>
        public static string Decode(string raw)
        {
            return SwapLetters(raw).Replace('$', '/').Replace('!', '.');
        }

        /// <summary>Inverse of <see cref="Decode"/>.</summary>
        public static string Encode(string plain)
        {
            return SwapLetters(plain.Replace('/', '$').Replace('.', '!'));
        }

        /// <summary>True when the string looks like it went through the cipher (uppercase letters with '$' or '!' separators).</summary>
        public static bool LooksEncoded(string s)
        {
            if (string.IsNullOrEmpty(s))
                return false;
            var upper = 0;
            var lower = 0;
            foreach (var ch in s)
            {
                if (ch == '$' || ch == '!') return true;
                if (char.IsUpper(ch)) upper++;
                else if (char.IsLower(ch)) lower++;
            }
            return upper > 0 && lower == 0;
        }
    }
}
