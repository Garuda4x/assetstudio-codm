using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace AssetStudio.Codm.Wwise
{
    /// <summary>
    /// Wwise object ids are FNV-1 (32-bit) hashes of lower-case names. CoD Mobile does not ship the
    /// names, but the weapon events follow patterns (play_wpn_locus_fire_1p, play_weapon_sniper_raise_3p,
    /// stop_219_reload_1), so candidate names can be generated per bank and checked against the ids.
    /// Resolved names are remembered in cache/wwise-names.json.
    /// </summary>
    public sealed class WwiseNames
    {
        public static uint Fnv1(string name)
        {
            var h = 2166136261u;
            foreach (var b in Encoding.UTF8.GetBytes(name.ToLowerInvariant()))
            {
                h = unchecked(h * 16777619u);
                h ^= b;
            }
            return h;
        }

        public static readonly string[] Prefixes = { "play_wpn_", "play_weapon_", "play_", "stop_wpn_", "stop_weapon_", "stop_", "wpn_", "weapon_" };
        public static readonly string[] Actions =
        {
            "fire", "fire_loop", "fire_tail", "fire_start", "fire_end", "fire_stop", "fireloop", "tail", "shoot", "shot",
            "reload", "reload_empty", "reload_1", "reload_2", "reload_3", "reload_4", "reload_a", "reload_b", "reload_c", "reload_d", "reload_fast", "reload_quick",
            "equip", "raise", "lower", "draw", "holster", "putaway", "switch", "pickup", "drop", "unequip",
            "ads", "ads_in", "ads_out", "adsin", "adsout", "aim", "aim_in", "aim_out", "scope", "scope_in", "scope_out", "zoom",
            "bolt", "pump", "lever", "slide", "cock", "rechamber", "chamber", "charge", "cycle", "trigger", "mode", "select",
            "melee", "empty", "dry", "dryfire", "dry_fire", "click", "inspect", "inspection", "foley", "handle",
            "sprint", "sprint_in", "sprint_out", "run", "walk", "jump", "land", "land_out", "idle", "crouch", "prone", "slide_in", "slide_out",
            "mag_in", "mag_out", "magin", "magout", "clip_in", "clip_out", "shell", "shell_drop", "casing", "bullet", "bulletshell",
            "hit", "hitflesh", "hitwall", "hit_flesh", "hit_wall", "impact", "whizby", "flyby", "crack", "snap", "ricochet",
            "loop", "stop", "start", "end", "burst", "single", "auto", "semi", "full",
            "swing", "stab", "attack", "attack_1", "attack_2", "attack_3", "combo", "throw", "pin", "pin_out", "bounce", "detonate", "explode", "explosion", "open", "close",
            "overheat", "cooldown", "energy", "charge_up", "charge_loop", "release", "spin", "spin_up", "spin_down", "beam", "laser", "rail",
            "firstattack", "first_attack", "secondattack", "second_attack", "thirdattack", "third_attack", "attack_a", "attack_b", "attack_c",
            "putdown", "put_down", "inspection_1", "inspection_2", "inspection1", "inspection2", "clipin", "clipout", "clip_in_1", "clip_out_1",
            "boltback", "bolt_back", "boltbackandrelease", "bolt_back_and_release", "firing", "fs", "footstep", "footsteps", "step",
        };
        public static readonly string[] Suffixes =
        {
            "", "_1p", "_3p", "_fp", "_tp", "_player", "_npc", "_plr", "_near", "_far", "_mid", "_close", "_dist", "_lr", "_in", "_out", "_int", "_ext",
            "_br", "_mp", "_sp", "_pve", "_a", "_b", "_c", "_1", "_2", "_3", "_01", "_02", "_loop", "_start", "_end", "_stop", "_1p_a", "_1p_b", "_3p_a", "_3p_b",
        };
        public static readonly string[] Classes = { "ar", "smg", "lmg", "sniper", "shotgun", "pistol", "launcher", "melee" };

        private readonly Dictionary<uint, string> known = new Dictionary<uint, string>();
        public string Path { get; private set; }
        public int Count => known.Count;

        public static WwiseNames Load(CodmConfig cfg)
        {
            var n = new WwiseNames { Path = cfg.CachePath("wwise-names.json") };
            if (File.Exists(n.Path))
            {
                try
                {
                    var d = JsonConvert.DeserializeObject<Dictionary<uint, string>>(File.ReadAllText(n.Path));
                    if (d != null) foreach (var kv in d) n.known[kv.Key] = kv.Value;
                }
                catch (Exception) { /* rebuilt by guessing */ }
            }
            foreach (var s in new[] { "default", "none", "normal", "common", "ads", "loop", "water", "wpn_ads" })
                n.known[Fnv1(s)] = s;
            return n;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                File.WriteAllText(Path, JsonConvert.SerializeObject(known.OrderBy(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value), Formatting.Indented));
            }
            catch (Exception) { /* cache only */ }
        }

        public string Get(uint id) => known.TryGetValue(id, out var n) ? n : null;
        public string GetOrId(uint id) => known.TryGetValue(id, out var n) ? n : id.ToString();

        private static readonly string[] QuickActions = { "fire", "reload", "equip", "raise", "fire_loop", "fire_tail", "hit", "swing", "attack", "melee", "throw", "explode", "idle", "ads" };
        private static readonly string[] QuickSuffixes = { "", "_1p", "_3p", "_a", "_1", "_player", "_lr" };

        /// <summary>Which of the tokens appear in this bank's event names (cheap pass over a few actions). Hits are remembered as names.</summary>
        public HashSet<string> IdentifyTokens(WwiseBank bank, IEnumerable<string> tokens)
        {
            var targets = new HashSet<uint>(bank.Events.Select(e => e.Id));
            var hits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (targets.Count == 0) return hits;
            foreach (var tok in tokens)
            {
                foreach (var t in new[] { tok, tok.Replace("_", "") })
                {
                    var found = false;
                    foreach (var pre in new[] { "play_wpn_", "play_weapon_", "play_", "stop_wpn_", "stop_weapon_", "stop_" })
                    {
                        foreach (var act in QuickActions)
                        {
                            foreach (var suf in QuickSuffixes)
                            {
                                var name = pre + t + "_" + act + suf;
                                var h = Fnv1(name);
                                if (targets.Contains(h))
                                {
                                    known[h] = name;
                                    found = true;
                                }
                            }
                        }
                    }
                    if (found) hits.Add(tok);
                }
            }
            return hits;
        }

        /// <summary>
        /// Try generated names against every event, action and switch id of a bank. Tokens are the
        /// weapon names and ids that could appear in this bank's event names.
        /// </summary>
        public int Guess(WwiseBank bank, IEnumerable<string> tokens, bool includeClasses = true)
        {
            var targets = new HashSet<uint>(bank.Events.Select(e => e.Id));
            foreach (var c in bank.Containers.Values)
            {
                targets.Add(c.SwitchGroupId);
                foreach (var k in c.SwitchMap.Keys) targets.Add(k);
            }
            foreach (var a in bank.Actions) targets.Add(a.Id);
            targets.RemoveWhere(t => known.ContainsKey(t));
            if (targets.Count == 0)
                return 0;

            var toks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tokens)
            {
                if (string.IsNullOrEmpty(t)) continue;
                toks.Add(t);
                toks.Add(t.Replace("_", ""));
            }
            if (includeClasses)
                foreach (var c in Classes) toks.Add(c);

            var found = 0;
            void Try(string name)
            {
                var h = Fnv1(name);
                if (targets.Contains(h) && !known.ContainsKey(h))
                {
                    known[h] = name;
                    found++;
                }
            }
            foreach (var pre in Prefixes)
            {
                foreach (var tok in toks)
                {
                    foreach (var act in Actions)
                        foreach (var suf in Suffixes)
                            Try(pre + tok + "_" + act + suf);
                    foreach (var suf in Suffixes)
                        Try(pre + tok + suf);
                }
                foreach (var act in Actions)
                    foreach (var suf in Suffixes)
                        Try(pre + act + suf);
            }
            foreach (var tok in toks)
                foreach (var suf in Suffixes)
                    Try(tok + suf);
            return found;
        }
    }
}
