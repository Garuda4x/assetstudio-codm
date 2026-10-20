using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetStudio.Codm.Wwise
{
    public sealed class WwiseMedia
    {
        public uint Id { get; set; }
        /// <summary>Absolute offset of the wem data inside the bank file.</summary>
        public long Offset { get; set; }
        public uint Size { get; set; }
        public string BankPath { get; set; }
    }

    public sealed class WwiseSound
    {
        public uint Id { get; set; }
        public uint PluginId { get; set; }
        /// <summary>0 = data in bank, 1 = prefetch + stream, 2 = stream.</summary>
        public byte StreamType { get; set; }
        public uint SourceId { get; set; }
        public uint InMemorySize { get; set; }

        public string Codec
        {
            get
            {
                switch (PluginId >> 16)
                {
                    case 1: return "PCM";
                    case 2: return "ADPCM";
                    case 3: return "XMA";
                    case 4: return "Vorbis";
                    case 5: return "WiiADPCM";
                    case 7: return "PCMEX";
                    case 8: return "External";
                    case 0xB: return "XWMA";
                    case 0xC: return "AAC";
                    case 0xD: return "FileSource";
                    case 0xE: return "ATRAC9";
                    case 0xF: return "Opus";
                    case 0x10: return "OpusNX";
                    case 0x13: return "OpusWEM";
                    default: return "0x" + PluginId.ToString("X8");
                }
            }
        }
    }

    public sealed class WwiseEvent
    {
        public uint Id { get; set; }
        public uint[] ActionIds { get; set; }
    }

    public sealed class WwiseAction
    {
        public uint Id { get; set; }
        public ushort ActionType { get; set; }
        public uint TargetId { get; set; }

        public bool IsPlay => (ActionType & 0xFF00) == 0x0400 || (ActionType & 0xFF00) == 0x0500 || (ActionType & 0xFF00) == 0x2100;

        public string ActionTypeName
        {
            get
            {
                switch (ActionType & 0xFF00)
                {
                    case 0x0100: return "Stop";
                    case 0x0200: return "Pause";
                    case 0x0300: return "Resume";
                    case 0x0400: return "Play";
                    case 0x0500: return "PlayAndContinue";
                    case 0x0600: return "Mute";
                    case 0x0700: return "UnMute";
                    case 0x0800: return "SetPitch";
                    case 0x0900: return "ResetPitch";
                    case 0x0A00: return "SetVolume";
                    case 0x0B00: return "ResetVolume";
                    case 0x0C00: return "SetBusVolume";
                    case 0x0D00: return "ResetBusVolume";
                    case 0x0E00: return "SetLPF";
                    case 0x0F00: return "ResetLPF";
                    case 0x1000: return "UseState";
                    case 0x1100: return "UnuseState";
                    case 0x1200: return "SetState";
                    case 0x1300: return "SetGameParameter";
                    case 0x1400: return "ResetGameParameter";
                    case 0x1900: return "SetSwitch";
                    case 0x1A00: return "BypassFX";
                    case 0x1B00: return "ResetBypassFX";
                    case 0x1C00: return "Break";
                    case 0x1D00: return "Trigger";
                    case 0x1E00: return "Seek";
                    case 0x1F00: return "Release";
                    case 0x2000: return "SetHPF";
                    case 0x2100: return "PlayEvent";
                    case 0x2200: return "ResetPlaylist";
                    case 0x2300: return "PlayEventUnknown";
                    case 0x3000: return "ResetHPF";
                    default: return "0x" + ActionType.ToString("X4");
                }
            }
        }
    }

    /// <summary>Random/sequence, switch, layer and actor-mixer containers: the objects that sit between an action and its sounds.</summary>
    public sealed class WwiseContainer
    {
        public uint Id { get; set; }
        public byte Type { get; set; }
        public uint[] Children { get; set; } = Array.Empty<uint>();
        /// <summary>Switch containers: the switch group that selects a child.</summary>
        public uint SwitchGroupId { get; set; }
        public uint DefaultSwitch { get; set; }
        /// <summary>Switch containers: switch value id -> the children it selects.</summary>
        public Dictionary<uint, uint[]> SwitchMap { get; } = new Dictionary<uint, uint[]>();
    }

    /// <summary>
    /// Minimal Wwise SoundBank (.bnk) reader for bank version 135 (CoD Mobile). Reads the header,
    /// the embedded media index (DIDX), and from HIRC: Sounds (which wem they use), Events, Actions and
    /// the child lists of containers, so an event can be followed down to its wems.
    /// </summary>
    public sealed class WwiseBank
    {
        public string Path { get; private set; }
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
        public uint Version { get; private set; }
        public uint BankId { get; private set; }
        public long DataOffset { get; private set; } = -1;
        public long FileSize { get; private set; }
        public List<WwiseMedia> Embedded { get; } = new List<WwiseMedia>();
        public List<WwiseSound> Sounds { get; } = new List<WwiseSound>();
        public List<WwiseEvent> Events { get; } = new List<WwiseEvent>();
        public List<WwiseAction> Actions { get; } = new List<WwiseAction>();
        public Dictionary<uint, WwiseContainer> Containers { get; } = new Dictionary<uint, WwiseContainer>();
        public Dictionary<byte, int> ObjectTypeCounts { get; } = new Dictionary<byte, int>();
        /// <summary>Every HIRC object id in this bank with its type byte.</summary>
        public Dictionary<uint, byte> ObjectIds { get; } = new Dictionary<uint, byte>();
        public List<string> Sections { get; } = new List<string>();
        public bool HasHirc { get; private set; }

        private Dictionary<uint, WwiseSound> soundsById;
        private Dictionary<uint, WwiseAction> actionsById;

        public static WwiseBank Parse(string path, bool parseHirc = true)
        {
            var bank = new WwiseBank { Path = path };
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                bank.FileSize = fs.Length;
                var didxRaw = new List<(uint id, uint off, uint size)>();
                while (fs.Position + 8 <= fs.Length)
                {
                    var tagBytes = br.ReadBytes(4);
                    var len = br.ReadUInt32();
                    var start = fs.Position;
                    var tag = Encoding.ASCII.GetString(tagBytes);
                    if (!IsTag(tagBytes) || start + len > fs.Length)
                        break;
                    bank.Sections.Add(tag);
                    switch (tag)
                    {
                        case "BKHD":
                            bank.Version = br.ReadUInt32();
                            bank.BankId = br.ReadUInt32();
                            break;
                        case "DIDX":
                            for (var i = 0; i + 12 <= len; i += 12)
                                didxRaw.Add((br.ReadUInt32(), br.ReadUInt32(), br.ReadUInt32()));
                            break;
                        case "DATA":
                            bank.DataOffset = start;
                            break;
                        case "HIRC":
                            bank.HasHirc = true;
                            if (parseHirc)
                                bank.ParseHirc(br.ReadBytes((int)len));
                            break;
                    }
                    fs.Position = start + len;
                }
                foreach (var (id, off, size) in didxRaw)
                {
                    bank.Embedded.Add(new WwiseMedia
                    {
                        Id = id,
                        Offset = bank.DataOffset >= 0 ? bank.DataOffset + off : -1,
                        Size = size,
                        BankPath = path,
                    });
                }
            }
            return bank;
        }

        private static bool IsTag(byte[] b)
        {
            foreach (var c in b)
                if (c < 'A' || c > 'Z') return false;
            return true;
        }

        private void ParseHirc(byte[] hirc)
        {
            if (hirc.Length < 4)
                return;
            var count = BitConverter.ToUInt32(hirc, 0);
            var p = 4;
            var objects = new List<(byte type, uint id, int bodyOff, int bodyLen)>();
            for (var i = 0; i < count && p + 9 <= hirc.Length; i++)
            {
                var type = hirc[p];
                var size = BitConverter.ToUInt32(hirc, p + 1);
                var id = BitConverter.ToUInt32(hirc, p + 5);
                var bodyOff = p + 9;
                var bodyLen = (int)Math.Min(size - 4, (uint)(hirc.Length - bodyOff));
                if (bodyLen < 0) break;
                objects.Add((type, id, bodyOff, bodyLen));
                ObjectTypeCounts[type] = ObjectTypeCounts.TryGetValue(type, out var n) ? n + 1 : 1;
                ObjectIds[id] = type;
                p = bodyOff + (int)(size - 4);
            }

            foreach (var (type, id, off, len) in objects)
            {
                try
                {
                    switch (type)
                    {
                        case 2: // Sound
                            if (len >= 13)
                                Sounds.Add(new WwiseSound
                                {
                                    Id = id,
                                    PluginId = BitConverter.ToUInt32(hirc, off),
                                    StreamType = hirc[off + 4],
                                    SourceId = BitConverter.ToUInt32(hirc, off + 5),
                                    InMemorySize = BitConverter.ToUInt32(hirc, off + 9),
                                });
                            break;
                        case 3: // Action
                            if (len >= 6)
                                Actions.Add(new WwiseAction
                                {
                                    Id = id,
                                    ActionType = BitConverter.ToUInt16(hirc, off),
                                    TargetId = BitConverter.ToUInt32(hirc, off + 2),
                                });
                            break;
                        case 4: // Event
                        {
                            var q = off;
                            var actionCount = ReadVarUInt(hirc, ref q);
                            if (actionCount > 4096) actionCount = 0;
                            var ids = new uint[actionCount];
                            for (var k = 0; k < actionCount && q + 4 <= off + len; k++, q += 4)
                                ids[k] = BitConverter.ToUInt32(hirc, q);
                            Events.Add(new WwiseEvent { Id = id, ActionIds = ids });
                            break;
                        }
                        case 5: // RandomSequence
                        case 6: // Switch
                        case 7: // ActorMixer
                        case 9: // Layer
                        {
                            var c = new WwiseContainer { Id = id, Type = type };
                            var childOff = FindChildren(hirc, off, len, out var children);
                            if (childOff >= 0)
                            {
                                c.Children = children;
                                if (type == 6)
                                    ParseSwitch(hirc, off, len, childOff, c);
                            }
                            Containers[id] = c;
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    // malformed object: keep what was parsed so far
                }
            }
        }

        /// <summary>
        /// Child lists are "u32 count, count ids". NodeBaseParams in front of them is long and
        /// version-specific, so the list is located by scanning for a count followed by ids that all
        /// exist in this bank. Returns the offset of the count or -1.
        /// </summary>
        private int FindChildren(byte[] hirc, int off, int len, out uint[] children)
        {
            for (var q = off; q + 4 <= off + len; q++)
            {
                var n = BitConverter.ToUInt32(hirc, q);
                if (n < 1 || n > 512 || q + 4 + 4 * n > off + len)
                    continue;
                var ok = true;
                var ids = new uint[n];
                for (var k = 0; k < n; k++)
                {
                    ids[k] = BitConverter.ToUInt32(hirc, q + 4 + 4 * k);
                    if (!ObjectIds.ContainsKey(ids[k])) { ok = false; break; }
                }
                if (ok)
                {
                    children = ids;
                    return q;
                }
            }
            children = Array.Empty<uint>();
            return -1;
        }

        /// <summary>Switch container: u8 groupType, u32 groupId, u32 default, u8 continuous, children, then the switch map.</summary>
        private static void ParseSwitch(byte[] hirc, int off, int len, int childOff, WwiseContainer c)
        {
            if (childOff - 10 >= off)
            {
                c.SwitchGroupId = BitConverter.ToUInt32(hirc, childOff - 9);
                c.DefaultSwitch = BitConverter.ToUInt32(hirc, childOff - 5);
            }
            var q = childOff + 4 + 4 * c.Children.Length;
            if (q + 4 > off + len) return;
            var groups = BitConverter.ToUInt32(hirc, q);
            q += 4;
            for (var g = 0; g < groups && q + 8 <= off + len; g++)
            {
                var switchId = BitConverter.ToUInt32(hirc, q);
                var items = BitConverter.ToUInt32(hirc, q + 4);
                q += 8;
                if (items > 512 || q + 4 * items > off + len) break;
                var ids = new uint[items];
                for (var k = 0; k < items; k++)
                    ids[k] = BitConverter.ToUInt32(hirc, q + 4 * k);
                q += 4 * (int)items;
                c.SwitchMap[switchId] = ids;
            }
        }

        private static uint ReadVarUInt(byte[] data, ref int p)
        {
            uint result = 0;
            var shift = 0;
            while (shift < 35 && p < data.Length)
            {
                var b = data[p++];
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    break;
                shift += 7;
            }
            return result;
        }

        public WwiseSound Sound(uint id)
        {
            if (soundsById == null) soundsById = Sounds.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
            return soundsById.TryGetValue(id, out var s) ? s : null;
        }

        public WwiseAction Action(uint id)
        {
            if (actionsById == null) actionsById = Actions.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
            return actionsById.TryGetValue(id, out var a) ? a : null;
        }

        /// <summary>
        /// Follow an event through its Play actions and every container down to the sounds.
        /// <paramref name="switchValue"/> restricts switch containers to one value (null = all children).
        /// Targets that are not in this bank are returned in <paramref name="external"/>.
        /// </summary>
        public HashSet<uint> EventSources(uint eventId, out HashSet<uint> external, uint? switchValue = null)
        {
            var sources = new HashSet<uint>();
            external = new HashSet<uint>();
            var ev = Events.FirstOrDefault(e => e.Id == eventId);
            if (ev == null) return sources;
            var visited = new HashSet<uint>();
            foreach (var aid in ev.ActionIds)
            {
                var a = Action(aid);
                if (a == null || !a.IsPlay) continue;
                Walk(a.TargetId, sources, external, visited, switchValue, 0);
            }
            return sources;
        }

        private void Walk(uint id, HashSet<uint> sources, HashSet<uint> external, HashSet<uint> visited, uint? switchValue, int depth)
        {
            if (depth > 24 || !visited.Add(id)) return;
            var snd = Sound(id);
            if (snd != null) { sources.Add(snd.SourceId); return; }
            if (Containers.TryGetValue(id, out var c))
            {
                IEnumerable<uint> kids = c.Children;
                if (c.Type == 6 && switchValue.HasValue && c.SwitchMap.Count > 0)
                    kids = c.SwitchMap.TryGetValue(switchValue.Value, out var sel) ? sel : (c.SwitchMap.TryGetValue(c.DefaultSwitch, out var def) ? def : Array.Empty<uint>());
                foreach (var k in kids)
                    Walk(k, sources, external, visited, switchValue, depth + 1);
                return;
            }
            if (Events.Any(e => e.Id == id))
            {
                var nested = EventSources(id, out var ext2, switchValue);
                foreach (var s in nested) sources.Add(s);
                foreach (var x in ext2) external.Add(x);
                return;
            }
            if (!ObjectIds.ContainsKey(id))
                external.Add(id);
        }

        /// <summary>Read an embedded media blob from this bank.</summary>
        public static byte[] ReadMedia(WwiseMedia media)
        {
            if (media.Offset < 0)
                return null;
            using (var fs = new FileStream(media.BankPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Position = media.Offset;
                var buf = new byte[media.Size];
                var read = 0;
                while (read < buf.Length)
                {
                    var n = fs.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return read == buf.Length ? buf : null;
            }
        }

        /// <summary>Distinct wem source ids referenced by this bank's Sound objects.</summary>
        public HashSet<uint> SourceIds()
        {
            var set = new HashSet<uint>();
            foreach (var s in Sounds)
                set.Add(s.SourceId);
            return set;
        }
    }
}
