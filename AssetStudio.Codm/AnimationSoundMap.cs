using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio.Codm.Wwise;

namespace AssetStudio.Codm
{
    /// <summary>One animation event (notetrack key) with everything that could be tied to it.</summary>
    public sealed class ClipSoundEvent
    {
        public string Clip { get; set; }
        public float Time { get; set; }
        public string Function { get; set; }
        public string Data { get; set; }
        /// <summary>Wwise event ids found in the data string (direct ids or the low 32 bits of the 64-bit tagged ids).</summary>
        public List<uint> WwiseEvents { get; } = new List<uint>();
        /// <summary>Resolved names / banks for <see cref="WwiseEvents"/>.</summary>
        public List<string> WwiseLabels { get; } = new List<string>();
        /// <summary>Events in the weapon's own banks whose recovered name matches the function (PlayStabSound -> ..._stab_1p).</summary>
        public List<string> GuessedEvents { get; } = new List<string>();
    }

    /// <summary>
    /// CoD Mobile keys its weapon sounds on the animation clips: Unity AnimationEvents with function
    /// names such as PlayFirstAttackSound, PlayRaiseSound, PlayClipInSound (the game maps those to the
    /// weapon's Wwise events through its config) or PlayEventSound with Wwise ids in the data string.
    /// The Cast exporter already writes them as notification tracks; this class turns them into a
    /// readable per-animation sound sheet and links them to banks and wems where the ids allow it.
    /// </summary>
    public static class AnimationSoundMap
    {
        /// <summary>Function name fragments -> the words the matching Wwise event names tend to contain.</summary>
        private static readonly (string function, string[] words)[] FunctionWords =
        {
            ("firstattack", new[] { "attack_1", "attack1", "attack_a", "fire", "swing_1", "swing1", "melee_1", "attack" }),
            ("secondattack", new[] { "attack_2", "attack2", "attack_b", "swing_2", "swing2", "melee_2" }),
            ("thirdattack", new[] { "attack_3", "attack3", "attack_c", "swing_3" }),
            ("stab", new[] { "stab", "attack_3", "attack3" }),
            ("raise", new[] { "raise", "equip", "draw" }),
            ("putdown", new[] { "putaway", "put_down", "putdown", "lower", "holster" }),
            ("inspection", new[] { "inspect", "inspection" }),
            ("clipin", new[] { "clip_in", "clipin", "mag_in", "magin", "reload_in" }),
            ("clipout", new[] { "clip_out", "clipout", "mag_out", "magout", "reload_out" }),
            ("boltback", new[] { "bolt", "bolt_back", "boltback", "charge", "release", "rechamber" }),
            ("fire", new[] { "fire", "shoot", "shot" }),
            ("reload", new[] { "reload" }),
            ("empty", new[] { "empty", "dry" }),
            ("pump", new[] { "pump" }),
            ("sprint", new[] { "sprint" }),
            ("jump", new[] { "jump" }),
            ("land", new[] { "land" }),
            ("foley", new[] { "foley" }),
        };

        /// <summary>Collect the events of the given clips and resolve what can be resolved.</summary>
        public static List<ClipSoundEvent> Collect(IEnumerable<AnimationClip> clips, WwiseIndex wwise, IEnumerable<WwiseBankInfo> weaponBanks)
        {
            var result = new List<ClipSoundEvent>();
            var hirc = wwise?.HircObjects();
            var names = wwise?.Names;

            // event names resolved in this weapon's banks, for the function-name guess
            var bankEvents = new List<(string bank, uint id, string name)>();
            if (wwise != null && weaponBanks != null)
            {
                foreach (var b in weaponBanks.Distinct())
                {
                    WwiseBank bank;
                    try { bank = WwiseBank.Parse(b.Path); } catch (Exception) { continue; }
                    foreach (var ev in bank.Events)
                    {
                        var n = names?.Get(ev.Id);
                        if (n != null) bankEvents.Add((b.Name, ev.Id, n));
                    }
                }
            }

            foreach (var clip in clips)
            {
                if (clip?.m_Events == null) continue;
                foreach (var ev in clip.m_Events.OrderBy(e => e.time))
                {
                    var entry = new ClipSoundEvent { Clip = clip.m_Name, Time = ev.time, Function = ev.functionName ?? "", Data = ev.data ?? "" };
                    foreach (var id in IdsInData(ev.data))
                    {
                        if (hirc != null && hirc.TryGetValue(id, out var r) && r.Type == 4)
                        {
                            entry.WwiseEvents.Add(id);
                            entry.WwiseLabels.Add($"{names?.GetOrId(id) ?? id.ToString()} in {r.Bank}.bnk");
                        }
                    }
                    if (ev.intParameter != 0 && hirc != null && hirc.TryGetValue((uint)ev.intParameter, out var ri) && ri.Type == 4)
                    {
                        entry.WwiseEvents.Add((uint)ev.intParameter);
                        entry.WwiseLabels.Add($"{names?.GetOrId((uint)ev.intParameter) ?? ev.intParameter.ToString()} in {ri.Bank}.bnk");
                    }
                    foreach (var word in WordsFor(entry.Function))
                    {
                        foreach (var (bank, id, name) in bankEvents)
                        {
                            if (name.IndexOf("_" + word, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var label = $"{name} in {bank}.bnk";
                            if (!entry.GuessedEvents.Contains(label)) entry.GuessedEvents.Add(label);
                        }
                        if (entry.GuessedEvents.Count > 0) break; // most specific word first
                    }
                    result.Add(entry);
                }
            }
            return result;
        }

        /// <summary>
        /// Wwise ids hidden in an event's data string: "347825172", or "562950372539117~1125900325960429|8"
        /// where each 64-bit value carries the 32-bit Wwise id in its low half.
        /// </summary>
        public static IEnumerable<uint> IdsInData(string data)
        {
            if (string.IsNullOrEmpty(data)) yield break;
            foreach (var tok in data.Split('~', '|', ',', ';', ' '))
            {
                if (ulong.TryParse(tok.Trim(), out var v))
                {
                    if (v <= uint.MaxValue) { if (v > 1000) yield return (uint)v; }
                    else yield return (uint)(v & 0xFFFFFFFF);
                }
            }
        }

        private static IEnumerable<string[]> WordsFor(string function)
        {
            var f = (function ?? "").ToLowerInvariant().Replace("play", "").Replace("sound", "").Replace("event", "").Replace("anycase", "");
            foreach (var (key, words) in FunctionWords)
                if (f.Contains(key)) yield return words;
        }

        /// <summary>Write the sheet next to the exported animations.</summary>
        public static void WriteSheet(string path, List<ClipSoundEvent> events)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# animation events (notetracks). time in seconds; function = what the game plays; Wwise = ids found in the event data;");
            sb.AppendLine("# guessed = events in this weapon's banks whose recovered name fits the function name");
            foreach (var g in events.GroupBy(e => e.Clip))
            {
                sb.AppendLine(g.Key);
                foreach (var e in g.OrderBy(e => e.Time))
                {
                    sb.Append($"  {e.Time,7:F3}s  {e.Function}");
                    if (!string.IsNullOrEmpty(e.Data)) sb.Append($"  data={e.Data}");
                    if (e.WwiseLabels.Count > 0) sb.Append($"  Wwise: {string.Join(", ", e.WwiseLabels)}");
                    if (e.GuessedEvents.Count > 0) sb.Append($"  guessed: {string.Join(", ", e.GuessedEvents)}");
                    sb.AppendLine();
                }
            }
            File.WriteAllText(path, sb.ToString());
        }
    }
}
