using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>
    /// Minimal Wwise SoundBank (.bnk) reader for bank version 135 (CoD Mobile). Reads the header,
    /// the embedded media index (DIDX), and from HIRC the objects that matter for extraction:
    /// Sounds (which wem they use), Events and Actions. Containers are counted but not walked.
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
        public Dictionary<byte, int> ObjectTypeCounts { get; } = new Dictionary<byte, int>();
        public List<string> Sections { get; } = new List<string>();
        public bool HasHirc { get; private set; }

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
                                bank.ParseHirc(br, start, len);
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

        private void ParseHirc(BinaryReader br, long start, uint len)
        {
            var fs = br.BaseStream;
            var count = br.ReadUInt32();
            for (var i = 0; i < count; i++)
            {
                if (fs.Position + 9 > start + len)
                    break;
                var type = br.ReadByte();
                var size = br.ReadUInt32();
                var objStart = fs.Position;
                var id = br.ReadUInt32();
                ObjectTypeCounts[type] = ObjectTypeCounts.TryGetValue(type, out var n) ? n + 1 : 1;
                try
                {
                    switch (type)
                    {
                        case 2: // Sound
                            Sounds.Add(new WwiseSound
                            {
                                Id = id,
                                PluginId = br.ReadUInt32(),
                                StreamType = br.ReadByte(),
                                SourceId = br.ReadUInt32(),
                                InMemorySize = br.ReadUInt32(),
                            });
                            break;
                        case 3: // Action
                            Actions.Add(new WwiseAction
                            {
                                Id = id,
                                ActionType = br.ReadUInt16(),
                                TargetId = br.ReadUInt32(),
                            });
                            break;
                        case 4: // Event
                        {
                            var actionCount = ReadVarUInt(br);
                            if (actionCount > 4096) actionCount = 0;
                            var ids = new uint[actionCount];
                            for (var k = 0; k < actionCount; k++)
                                ids[k] = br.ReadUInt32();
                            Events.Add(new WwiseEvent { Id = id, ActionIds = ids });
                            break;
                        }
                    }
                }
                catch (EndOfStreamException)
                {
                    break;
                }
                fs.Position = objStart + size;
            }
        }

        private static uint ReadVarUInt(BinaryReader br)
        {
            uint result = 0;
            var shift = 0;
            while (shift < 35)
            {
                var b = br.ReadByte();
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    break;
                shift += 7;
            }
            return result;
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
