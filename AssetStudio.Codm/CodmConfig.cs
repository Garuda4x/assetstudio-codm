using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace AssetStudio.Codm
{
    /// <summary>
    /// User configuration, stored as codm.config.json. The file is looked up by walking up from the
    /// executable directory (so Debug/Release builds inside the repo share the copy at the repo root),
    /// then the current directory. Relative game paths resolve against <see cref="GameRoot"/>,
    /// relative local paths (exports, cache) against the config file's directory.
    /// </summary>
    public sealed class CodmConfig
    {
        public const string FileName = "codm.config.json";

        [JsonProperty(Order = 0)]
        public string GameRoot { get; set; } = @"Q:\CODMGameApps\CN\emulator\Extract\CODM";

        [JsonProperty(Order = 1)]
        public List<string> PakFolders { get; set; } = new List<string>
        {
            @"CODM_Data\StreamingAssets\AssetBundle",
            @"PersistentData\Puffer\AssetBundle\dynamic\module",
            @"PersistentData\Extract\55\AssetBundle",
        };

        [JsonProperty(Order = 2)]
        public List<string> SoundBankFolders { get; set; } = new List<string>
        {
            @"CODM_Data\StreamingAssets\Audio\GeneratedSoundBanks\Windows",
            @"PersistentData\Puffer\Audio\GeneratedSoundBanks\Windows",
            @"PersistentData\Puffer\Audio\GeneratedSoundBanks\Windows_HQ",
        };

        /// <summary>Full path to vgmstream-cli.exe. Empty: wem files are exported as-is.</summary>
        [JsonProperty(Order = 3)]
        public string VgmstreamPath { get; set; } = "";

        /// <summary>Default export folder (relative to the config file).</summary>
        [JsonProperty(Order = 4)]
        public string ExportRoot { get; set; } = "exports";

        /// <summary>Preferred Wwise language folder when a wem exists in several.</summary>
        [JsonProperty(Order = 5)]
        public string PreferredLanguage { get; set; } = "English(US)";

        /// <summary>Texture export format: png, tga, jpeg, bmp.</summary>
        [JsonProperty(Order = 6)]
        public string TextureFormat { get; set; } = "png";

        /// <summary>Scale applied to exported models and animations.</summary>
        [JsonProperty(Order = 7)]
        public float ScaleFactor { get; set; } = 1.0f;

        /// <summary>Order in which pak sources win when the same decoded path exists in several folders (first wins).</summary>
        [JsonProperty(Order = 8)]
        public List<string> SourcePriority { get; set; } = new List<string> { "puffer", "extract", "streaming" };

        /// <summary>Verbose per-field reader diagnostics in the log.</summary>
        [JsonProperty(Order = 9)]
        public bool Diagnostics { get; set; } = false;

        [JsonIgnore]
        public string ConfigPath { get; private set; }

        [JsonIgnore]
        public string ConfigDir => ConfigPath != null ? Path.GetDirectoryName(ConfigPath) : AppDomain.CurrentDomain.BaseDirectory;

        public string ResolveGamePath(string rel) => Path.IsPathRooted(rel) ? rel : Path.Combine(GameRoot, rel);
        public string ResolveLocalPath(string rel) => Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(ConfigDir, rel));

        [JsonIgnore]
        public IEnumerable<string> PakFolderPaths => PakFolders.Select(ResolveGamePath);
        [JsonIgnore]
        public IEnumerable<string> SoundBankFolderPaths => SoundBankFolders.Select(ResolveGamePath);
        [JsonIgnore]
        public string ExportRootPath => ResolveLocalPath(ExportRoot);

        public ImageFormat ImageFormat
        {
            get
            {
                return Enum.TryParse<ImageFormat>(TextureFormat, true, out var f) ? f : ImageFormat.Png;
            }
        }

        /// <summary>Find codm.config.json by walking up from <paramref name="startDir"/> (default: exe dir), then the current directory.</summary>
        public static string Locate(string startDir = null)
        {
            var dirs = new List<string>();
            var dir = startDir ?? AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                dirs.Add(dir);
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            dirs.Add(Directory.GetCurrentDirectory());
            foreach (var d in dirs)
            {
                var candidate = Path.Combine(d, FileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        /// <summary>Load the config at <paramref name="path"/> or the located one; defaults when none exists.</summary>
        public static CodmConfig Load(string path = null)
        {
            path = path ?? Locate();
            CodmConfig cfg;
            if (path != null && File.Exists(path))
            {
                cfg = JsonConvert.DeserializeObject<CodmConfig>(File.ReadAllText(path), new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace }) ?? new CodmConfig();
                cfg.ConfigPath = Path.GetFullPath(path);
            }
            else
            {
                cfg = new CodmConfig
                {
                    ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName)
                };
            }
            return cfg;
        }

        public void Save(string path = null)
        {
            path = path ?? ConfigPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
            ConfigPath = Path.GetFullPath(path);
        }

        /// <summary>Short label for a pak folder: "streaming", "puffer", "extract" or the folder name.</summary>
        public static string SourceLabel(string folder)
        {
            var f = folder.Replace('/', '\\');
            if (f.IndexOf("StreamingAssets", StringComparison.OrdinalIgnoreCase) >= 0) return "streaming";
            if (f.IndexOf("Puffer", StringComparison.OrdinalIgnoreCase) >= 0) return "puffer";
            if (f.IndexOf("\\Extract\\", StringComparison.OrdinalIgnoreCase) >= 0) return "extract";
            return Path.GetFileName(f.TrimEnd('\\'));
        }

        public int SourceRank(string sourceLabel)
        {
            var i = SourcePriority.FindIndex(s => string.Equals(s, sourceLabel, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? SourcePriority.Count : i;
        }
    }
}
