using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio;

namespace PakScanner
{
    /// <summary>
    /// Lightweight scanner that reads Unity bundle (.pak) headers and asset metadata
    /// without fully deserializing all objects. Shows bundle contents, asset types,
    /// asset names, and container paths to help identify obfuscated pak files.
    /// </summary>
    public class PakInfo
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public string Signature { get; set; }
        public uint BundleVersion { get; set; }
        public string UnityVersion { get; set; }
        public string UnityRevision { get; set; }
        public List<string> InternalFiles { get; set; } = new List<string>();
        public List<string> ContainerPaths { get; set; } = new List<string>();
        public Dictionary<string, int> AssetTypeCounts { get; set; } = new Dictionary<string, int>();
        public List<string> AssetNames { get; set; } = new List<string>();
        public string Error { get; set; }

        /// <summary>
        /// Returns a short summary string identifying what this pak likely contains.
        /// </summary>
        public string GetSummary()
        {
            if (Error != null)
                return $"[ERROR] {Error}";

            // Try to derive a human-readable name from container paths
            var paths = ContainerPaths.Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0)
            {
                // Find common prefix
                var commonPrefix = GetCommonPrefix(paths);
                if (!string.IsNullOrEmpty(commonPrefix))
                {
                    // Extract the most meaningful part
                    var parts = commonPrefix.Split('/').Where(p => p.Length > 0).ToArray();
                    if (parts.Length >= 3)
                        return string.Join("/", parts.Take(4));
                    if (parts.Length > 0)
                        return string.Join("/", parts);
                }

                // Just show first container path
                return paths[0].Length > 80 ? paths[0].Substring(0, 80) + "..." : paths[0];
            }

            // Fall back to asset names
            if (AssetNames.Count > 0)
            {
                var meaningful = AssetNames.Where(n => n.Length > 3 && !n.StartsWith("CAB-")).Take(5).ToList();
                if (meaningful.Count > 0)
                    return string.Join(", ", meaningful);
            }

            // Fall back to type summary
            if (AssetTypeCounts.Count > 0)
            {
                return string.Join(", ", AssetTypeCounts.Select(kv => $"{kv.Value}x {kv.Key}"));
            }

            return "(empty or unreadable)";
        }

        private static string GetCommonPrefix(List<string> strings)
        {
            if (strings.Count == 0) return "";
            var prefix = strings[0];
            foreach (var s in strings.Skip(1))
            {
                while (!s.StartsWith(prefix) && prefix.Length > 0)
                {
                    var lastSlash = prefix.LastIndexOf('/');
                    prefix = lastSlash >= 0 ? prefix.Substring(0, lastSlash) : "";
                }
            }
            return prefix;
        }
    }

    public static class Scanner
    {
        /// <summary>
        /// Scan a single .pak file and extract metadata without full deserialization.
        /// </summary>
        public static PakInfo ScanFile(string filePath)
        {
            var info = new PakInfo
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                FileSize = new FileInfo(filePath).Length
            };

            try
            {
                using (var reader = new FileReader(filePath))
                {
                    if (reader.FileType != FileType.BundleFile)
                    {
                        info.Error = $"Not a Unity bundle (detected: {reader.FileType})";
                        return info;
                    }

                    // Parse the bundle to get internal file list
                    var bundle = new BundleFile(reader);

                    info.Signature = bundle.m_Header.signature;
                    info.BundleVersion = bundle.m_Header.version;
                    info.UnityVersion = bundle.m_Header.unityVersion;
                    info.UnityRevision = bundle.m_Header.unityRevision;

                    foreach (var file in bundle.fileList)
                    {
                        info.InternalFiles.Add($"{file.fileName} ({FormatSize(file.stream.Length)})");
                    }

                    // Now try to read the serialized assets to get names and containers
                    foreach (var file in bundle.fileList)
                    {
                        var dummyPath = Path.Combine(Path.GetDirectoryName(filePath), file.fileName);
                        var subReader = new FileReader(dummyPath, file.stream);

                        if (subReader.FileType == FileType.AssetsFile)
                        {
                            ScanSerializedFile(subReader, info, bundle.m_Header.unityRevision);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                info.Error = $"{ex.GetType().Name}: {ex.Message}";
            }

            return info;
        }

        private static void ScanSerializedFile(FileReader reader, PakInfo info, string unityRevision)
        {
            try
            {
                var mgr = new AssetsManager();
                var assetsFile = new SerializedFile(reader, mgr);
                if (!string.IsNullOrEmpty(unityRevision) &&
                    assetsFile.header.m_Version < SerializedFileFormatVersion.Unknown_7)
                {
                    assetsFile.SetVersion(unityRevision);
                }

                // Count asset types from object info (lightweight — no deserialization)
                foreach (var objInfo in assetsFile.m_Objects)
                {
                    var typeName = ((ClassIDType)objInfo.classID).ToString();
                    if (!info.AssetTypeCounts.ContainsKey(typeName))
                        info.AssetTypeCounts[typeName] = 0;
                    info.AssetTypeCounts[typeName]++;
                }

                // Now selectively deserialize only NamedObjects and AssetBundle to get names/containers
                foreach (var objInfo in assetsFile.m_Objects)
                {
                    try
                    {
                        var objectReader = new ObjectReader(reader, assetsFile, objInfo);
                        switch ((ClassIDType)objInfo.classID)
                        {
                            case ClassIDType.AssetBundle:
                                var ab = new AssetBundle(objectReader);
                                foreach (var container in ab.m_Container)
                                {
                                    if (!string.IsNullOrEmpty(container.Key))
                                        info.ContainerPaths.Add(container.Key);
                                }
                                break;

                            case ClassIDType.Texture2D:
                            case ClassIDType.Mesh:
                            case ClassIDType.AnimationClip:
                            case ClassIDType.Animator:
                            case ClassIDType.Avatar:
                            case ClassIDType.Material:
                            case ClassIDType.Shader:
                            case ClassIDType.TextAsset:
                            case ClassIDType.AudioClip:
                            case ClassIDType.Font:
                            case ClassIDType.Sprite:
                            case ClassIDType.VideoClip:
                                // Just read the name from NamedObject base (4-byte length + string)
                                objectReader.Reset();
                                var nameLen = objectReader.ReadInt32();
                                if (nameLen > 0 && nameLen < 1024)
                                {
                                    var nameBytes = objectReader.ReadBytes(nameLen);
                                    var name = Encoding.UTF8.GetString(nameBytes);
                                    if (!string.IsNullOrWhiteSpace(name))
                                        info.AssetNames.Add(name);
                                }
                                break;

                            case ClassIDType.GameObject:
                                // Read past component array to get name
                                // Too complex — skip for lightweight scan
                                break;
                        }
                    }
                    catch
                    {
                        // Silently skip objects that fail to read
                    }
                }
            }
            catch (Exception ex)
            {
                // If serialized file can't be parsed, just note it
                if (info.Error == null)
                    info.Error = $"SerializedFile parse: {ex.Message}";
            }
        }

        /// <summary>
        /// Scan all .pak files in a directory and return results sorted by container path.
        /// </summary>
        public static List<PakInfo> ScanDirectory(string directoryPath, string searchPattern = "*.pak")
        {
            var results = new List<PakInfo>();
            var files = Directory.GetFiles(directoryPath, searchPattern, SearchOption.TopDirectoryOnly);

            for (int i = 0; i < files.Length; i++)
            {
                Console.Write($"\rScanning {i + 1}/{files.Length}...");
                results.Add(ScanFile(files[i]));
            }
            Console.WriteLine();

            return results.OrderBy(r => r.GetSummary()).ToList();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }
    }
}
