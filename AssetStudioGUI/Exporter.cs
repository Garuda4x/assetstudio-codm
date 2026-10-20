using AssetStudio;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetStudioGUI
{
    internal static class Exporter
    {
        public static bool ExportTexture2D(AssetItem item, string exportPath)
        {
            var m_Texture2D = (Texture2D)item.Asset;
            CodmDiagnostics.Log($"[TexExport] '{m_Texture2D.m_Name}' {m_Texture2D.m_Width}x{m_Texture2D.m_Height} fmt={m_Texture2D.m_TextureFormat} imageDataSize={m_Texture2D.image_data.Size} convertTexture={Properties.Settings.Default.convertTexture}");
            if (Properties.Settings.Default.convertTexture)
            {
                var type = Properties.Settings.Default.convertType;
                if (!TryExportFile(exportPath, item, "." + type.ToString().ToLower(), out var exportFullPath))
                    return false;
                var image = m_Texture2D.ConvertToImage(true);
                if (image == null)
                {
                    Logger.Warning($"[TexExport] ConvertToImage returned null for '{m_Texture2D.m_Name}' (format={m_Texture2D.m_TextureFormat}, dataSize={m_Texture2D.image_data.Size})");
                    return false;
                }
                using (image)
                {
                    using (var file = File.OpenWrite(exportFullPath))
                    {
                        image.WriteToStream(file, type);
                    }
                    return true;
                }
            }
            else
            {
                if (!TryExportFile(exportPath, item, ".tex", out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_Texture2D.image_data.GetData());
                return true;
            }
        }

        public static bool ExportAudioClip(AssetItem item, string exportPath)
        {
            var m_AudioClip = (AudioClip)item.Asset;
            var m_AudioData = m_AudioClip.m_AudioData.GetData();
            if (m_AudioData == null || m_AudioData.Length == 0)
                return false;
            var converter = new AudioClipConverter(m_AudioClip);
            if (Properties.Settings.Default.convertAudio && converter.IsSupport)
            {
                if (!TryExportFile(exportPath, item, ".wav", out var exportFullPath))
                    return false;
                var buffer = converter.ConvertToWav();
                if (buffer == null)
                    return false;
                File.WriteAllBytes(exportFullPath, buffer);
            }
            else
            {
                if (!TryExportFile(exportPath, item, converter.GetExtensionName(), out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_AudioData);
            }
            return true;
        }

        public static bool ExportShader(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".shader", out var exportFullPath))
                return false;
            var m_Shader = (Shader)item.Asset;
            var str = m_Shader.Convert();
            File.WriteAllText(exportFullPath, str);
            return true;
        }

        public static bool ExportTextAsset(AssetItem item, string exportPath)
        {
            var m_TextAsset = (TextAsset)(item.Asset);
            var extension = ".txt";
            if (Properties.Settings.Default.restoreExtensionName)
            {
                if (!string.IsNullOrEmpty(item.Container))
                {
                    extension = Path.GetExtension(item.Container);
                }
            }
            if (!TryExportFile(exportPath, item, extension, out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, m_TextAsset.m_Script);
            return true;
        }

        public static bool ExportMonoBehaviour(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".json", out var exportFullPath))
                return false;
            var m_MonoBehaviour = (MonoBehaviour)item.Asset;
            var type = m_MonoBehaviour.ToType();
            if (type == null)
            {
                var m_Type = Studio.MonoBehaviourToTypeTree(m_MonoBehaviour);
                type = m_MonoBehaviour.ToType(m_Type);
            }
            var str = JsonConvert.SerializeObject(type, Formatting.Indented);
            File.WriteAllText(exportFullPath, str);
            return true;
        }

        public static bool ExportFont(AssetItem item, string exportPath)
        {
            var m_Font = (Font)item.Asset;
            if (m_Font.m_FontData != null)
            {
                var extension = ".ttf";
                if (m_Font.m_FontData[0] == 79 && m_Font.m_FontData[1] == 84 && m_Font.m_FontData[2] == 84 && m_Font.m_FontData[3] == 79)
                {
                    extension = ".otf";
                }
                if (!TryExportFile(exportPath, item, extension, out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_Font.m_FontData);
                return true;
            }
            return false;
        }

        public static bool ExportMesh(AssetItem item, string exportPath)
        {
            var m_Mesh = (Mesh)item.Asset;
            if (m_Mesh.m_VertexCount <= 0)
                return false;
            if (!TryExportFile(exportPath, item, ".obj", out var exportFullPath))
                return false;

            // Export skin weights as companion file if available
            if (m_Mesh.m_Skin?.Length > 0 && m_Mesh.m_BoneNameHashes?.Length > 0)
            {
                // Find an Avatar to resolve bone name hashes
                Avatar matchedAvatar = null;
                foreach (var asset in Studio.assetsManager.assetsFileList.SelectMany(f => f.Objects))
                {
                    if (asset is Avatar av)
                    {
                        // Check if this avatar contains at least one of our bone hashes
                        var tosDict = av.m_TOS.ToDictionary(p => p.Key, p => p.Value);
                        if (m_Mesh.m_BoneNameHashes.Any(h => tosDict.ContainsKey(h)))
                        {
                            matchedAvatar = av;
                            break;
                        }
                    }
                }

                // Build bone name lookup
                var boneNames = new string[m_Mesh.m_BoneNameHashes.Length];
                if (matchedAvatar != null)
                {
                    var tosDict = matchedAvatar.m_TOS.ToDictionary(p => p.Key, p => p.Value);
                    for (int b = 0; b < m_Mesh.m_BoneNameHashes.Length; b++)
                    {
                        if (tosDict.TryGetValue(m_Mesh.m_BoneNameHashes[b], out var path))
                        {
                            // Use the last segment of the path as bone name
                            boneNames[b] = path.Contains("/") ? path.Substring(path.LastIndexOf('/') + 1) : path;
                        }
                        else
                        {
                            boneNames[b] = $"Bone_{b}_Hash{m_Mesh.m_BoneNameHashes[b]}";
                        }
                    }
                    CodmDiagnostics.Log($"[SkinExport] Resolved bone names using Avatar '{matchedAvatar.m_Name}'");
                }
                else
                {
                    for (int b = 0; b < m_Mesh.m_BoneNameHashes.Length; b++)
                        boneNames[b] = $"Bone_{b}_Hash{m_Mesh.m_BoneNameHashes[b]}";
                    Logger.Warning($"[SkinExport] No matching Avatar found, using hash-based bone names");
                }

                // Export consolidated skin data file
                var skinPath = Path.ChangeExtension(exportFullPath, ".skin.csv");
                var skinSb = new StringBuilder();
                skinSb.AppendLine("VertexIndex,BoneIndex0,BoneName0,Weight0,BoneIndex1,BoneName1,Weight1,BoneIndex2,BoneName2,Weight2,BoneIndex3,BoneName3,Weight3");
                for (int v = 0; v < m_Mesh.m_Skin.Length; v++)
                {
                    var skin = m_Mesh.m_Skin[v];
                    skinSb.AppendLine($"{v},{skin.boneIndex[0]},{GetBoneName(boneNames, skin.boneIndex[0])},{skin.weight[0]},{skin.boneIndex[1]},{GetBoneName(boneNames, skin.boneIndex[1])},{skin.weight[1]},{skin.boneIndex[2]},{GetBoneName(boneNames, skin.boneIndex[2])},{skin.weight[2]},{skin.boneIndex[3]},{GetBoneName(boneNames, skin.boneIndex[3])},{skin.weight[3]}");
                }
                File.WriteAllText(skinPath, skinSb.ToString());
                CodmDiagnostics.Log($"[SkinExport] Exported {m_Mesh.m_Skin.Length} vertex weights to {Path.GetFileName(skinPath)}");

                // Export bone info
                var bonePath = Path.ChangeExtension(exportFullPath, ".bones.csv");
                var boneSb = new StringBuilder();
                boneSb.AppendLine("BoneIndex,BoneName,BoneNameHash");
                for (int b = 0; b < m_Mesh.m_BoneNameHashes.Length; b++)
                {
                    boneSb.AppendLine($"{b},{boneNames[b]},{m_Mesh.m_BoneNameHashes[b]}");
                }
                File.WriteAllText(bonePath, boneSb.ToString());

                // Export bind poses if available
                if (m_Mesh.m_BindPose?.Length > 0)
                {
                    var bindPath = Path.ChangeExtension(exportFullPath, ".bindpose.csv");
                    var bindSb = new StringBuilder();
                    bindSb.AppendLine("BoneIndex,BoneName,M00,M01,M02,M03,M10,M11,M12,M13,M20,M21,M22,M23,M30,M31,M32,M33");
                    for (int b = 0; b < m_Mesh.m_BindPose.Length; b++)
                    {
                        var m = m_Mesh.m_BindPose[b];
                        var name = b < boneNames.Length ? boneNames[b] : $"Bone_{b}";
                        bindSb.AppendLine($"{b},{name},{m.M00},{m.M01},{m.M02},{m.M03},{m.M10},{m.M11},{m.M12},{m.M13},{m.M20},{m.M21},{m.M22},{m.M23},{m.M30},{m.M31},{m.M32},{m.M33}");
                    }
                    File.WriteAllText(bindPath, bindSb.ToString());
                    CodmDiagnostics.Log($"[SkinExport] Exported {m_Mesh.m_BindPose.Length} bind poses to {Path.GetFileName(bindPath)}");
                }
            }

            CodmDiagnostics.Log($"[MeshDebug] Mesh '{m_Mesh.m_Name}': vertices={m_Mesh.m_VertexCount}, skin={m_Mesh.m_Skin?.Length ?? 0}, bindPose={m_Mesh.m_BindPose?.Length ?? 0}, boneNameHashes={m_Mesh.m_BoneNameHashes?.Length ?? 0}");
            if (m_Mesh.m_Skin?.Length > 0)
            {
                var uniqueBones = new HashSet<int>();
                var nonDefaultCount = 0;
                for (int v = 0; v < m_Mesh.m_Skin.Length; v++)
                {
                    var s = m_Mesh.m_Skin[v];
                    for (int j = 0; j < 4; j++)
                    {
                        if (s.weight[j] > 0) uniqueBones.Add(s.boneIndex[j]);
                    }
                    if (s.boneIndex[0] != 0 || s.weight[0] != 1.0f || s.weight[1] != 0 || s.weight[2] != 0 || s.weight[3] != 0)
                        nonDefaultCount++;
                }
                CodmDiagnostics.Log($"[MeshDebug]   Unique bones referenced: {uniqueBones.Count} ({string.Join(",", uniqueBones)}), non-default weights: {nonDefaultCount}/{m_Mesh.m_Skin.Length}");
            }

            var sb = new StringBuilder();
            sb.AppendLine("g " + m_Mesh.m_Name);
            #region Vertices
            if (m_Mesh.m_Vertices == null || m_Mesh.m_Vertices.Length == 0)
            {
                return false;
            }
            int c = 3;
            if (m_Mesh.m_Vertices.Length == m_Mesh.m_VertexCount * 4)
            {
                c = 4;
            }
            for (int v = 0; v < m_Mesh.m_VertexCount; v++)
            {
                sb.AppendFormat("v {0} {1} {2}\r\n", -m_Mesh.m_Vertices[v * c], m_Mesh.m_Vertices[v * c + 1], m_Mesh.m_Vertices[v * c + 2]);
            }
            #endregion

            #region UV
            if (m_Mesh.m_UV0?.Length > 0)
            {
                c = 4;
                if (m_Mesh.m_UV0.Length == m_Mesh.m_VertexCount * 2)
                {
                    c = 2;
                }
                else if (m_Mesh.m_UV0.Length == m_Mesh.m_VertexCount * 3)
                {
                    c = 3;
                }
                for (int v = 0; v < m_Mesh.m_VertexCount; v++)
                {
                    sb.AppendFormat("vt {0} {1}\r\n", m_Mesh.m_UV0[v * c], m_Mesh.m_UV0[v * c + 1]);
                }
            }
            #endregion

            #region Normals
            if (m_Mesh.m_Normals?.Length > 0)
            {
                if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 3)
                {
                    c = 3;
                }
                else if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 4)
                {
                    c = 4;
                }
                for (int v = 0; v < m_Mesh.m_VertexCount; v++)
                {
                    sb.AppendFormat("vn {0} {1} {2}\r\n", -m_Mesh.m_Normals[v * c], m_Mesh.m_Normals[v * c + 1], m_Mesh.m_Normals[v * c + 2]);
                }
            }
            #endregion

            #region Face
            int sum = 0;
            for (var i = 0; i < m_Mesh.m_SubMeshes.Length; i++)
            {
                sb.AppendLine($"g {m_Mesh.m_Name}_{i}");
                int indexCount = (int)m_Mesh.m_SubMeshes[i].indexCount;
                var end = sum + indexCount / 3;
                for (int f = sum; f < end; f++)
                {
                    sb.AppendFormat("f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}\r\n", m_Mesh.m_Indices[f * 3 + 2] + 1, m_Mesh.m_Indices[f * 3 + 1] + 1, m_Mesh.m_Indices[f * 3] + 1);
                }
                sum = end;
            }
            #endregion

            sb.Replace("NaN", "0");
            File.WriteAllText(exportFullPath, sb.ToString());
            return true;
        }

        public static bool ExportVideoClip(AssetItem item, string exportPath)
        {
            var m_VideoClip = (VideoClip)item.Asset;
            if (m_VideoClip.m_ExternalResources.m_Size > 0)
            {
                if (!TryExportFile(exportPath, item, Path.GetExtension(m_VideoClip.m_OriginalPath), out var exportFullPath))
                    return false;
                m_VideoClip.m_VideoData.WriteData(exportFullPath);
                return true;
            }
            return false;
        }

        public static bool ExportSkinnedMeshFbx(AssetItem item, string exportPath)
        {
            var m_Mesh = (Mesh)item.Asset;
            if (m_Mesh.m_VertexCount <= 0)
                return false;
            if (m_Mesh.m_Skin == null || m_Mesh.m_Skin.Length == 0)
                return false;

            if (!TryExportFile(exportPath, item, ".fbx", out var exportFullPath))
                return false;

            var matchedAvatar = FindAvatarForMesh(m_Mesh);

            CodmDiagnostics.Log($"[SkinnedFBX] Exporting '{m_Mesh.m_Name}' with {m_Mesh.m_Skin.Length} vertices, {m_Mesh.m_BindPose?.Length ?? 0} bones, Avatar={matchedAvatar?.m_Name ?? "none"}");

            // Find materials by locating SkinnedMeshRenderers that reference this mesh
            var materials = FindMaterialsForMesh(m_Mesh);

            // Export FBX (works in Maya, may not work in Blender)
            var convert = new ModelConverter(m_Mesh, matchedAvatar, Properties.Settings.Default.convertType, materials.Count > 0 ? materials.ToArray() : null);
            ExportFbxDirect(convert, exportFullPath);
            ExportCompanionFormats(convert, exportFullPath);

            // Also generate a Blender Python script for reliable import
            GenerateBlenderScript(m_Mesh, matchedAvatar, exportFullPath, materials);

            return true;
        }

        public static bool ExportSkinnedMeshGlb(AssetItem item, string exportPath)
        {
            var m_Mesh = (Mesh)item.Asset;
            if (m_Mesh.m_VertexCount <= 0)
                return false;
            if (m_Mesh.m_Skin == null || m_Mesh.m_Skin.Length == 0)
                return false;

            if (!TryExportFile(exportPath, item, ".glb", out var exportFullPath))
                return false;

            var matchedAvatar = FindAvatarForMesh(m_Mesh);
            var materials = FindMaterialsForMesh(m_Mesh);

            CodmDiagnostics.Log($"[glTF] Exporting skinned mesh '{m_Mesh.m_Name}' with {m_Mesh.m_Skin.Length} vertices, {m_Mesh.m_BindPose?.Length ?? 0} bones, Avatar={matchedAvatar?.m_Name ?? "none"}, materials={materials.Count}");

            var convert = new ModelConverter(m_Mesh, matchedAvatar, Properties.Settings.Default.convertType, materials.Count > 0 ? materials.ToArray() : null);
            var scaleFactor = (float)Properties.Settings.Default.scaleFactor;
            GltfExporter.ExportGlb(exportFullPath, convert, scaleFactor);

            return true;
        }

        private static Avatar FindAvatarForMesh(Mesh m_Mesh)
        {
            Avatar matchedAvatar = null;
            if (m_Mesh.m_BoneNameHashes?.Length > 0)
            {
                foreach (var asset in Studio.assetsManager.assetsFileList.SelectMany(f => f.Objects))
                {
                    if (asset is Avatar av)
                    {
                        var tosDict = av.m_TOS.ToDictionary(p => p.Key, p => p.Value);
                        if (m_Mesh.m_BoneNameHashes.Any(h => tosDict.ContainsKey(h)))
                        {
                            matchedAvatar = av;
                            break;
                        }
                    }
                }
            }
            return matchedAvatar;
        }

        private static List<Material> FindMaterialsForMesh(Mesh mesh)
        {
            var materials = new List<Material>();
            var meshPathID = mesh.m_PathID;
            var meshFile = mesh.assetsFile;

            // Strategy 1: Find SMR in same file where m_Mesh PPtr resolves to our mesh
            foreach (var obj in meshFile.Objects)
            {
                if (obj is SkinnedMeshRenderer smr && smr.m_Materials.Length > 0)
                {
                    if (smr.m_Mesh.m_PathID == meshPathID ||
                        (smr.m_Mesh.TryGet(out var smrMesh) && smrMesh == mesh))
                    {
                        foreach (var matPPtr in smr.m_Materials)
                        {
                            if (matPPtr.TryGet(out var mat))
                                materials.Add(mat);
                            else
                                materials.Add(null);
                        }
                        CodmDiagnostics.Log($"[Materials] Found {materials.Count} materials for '{mesh.m_Name}' from SMR (PathID={smr.m_PathID})");
                        return materials;
                    }
                }
            }

            // Strategy 2: Search all files for SMRs
            foreach (var file in Studio.assetsManager.assetsFileList)
            {
                foreach (var obj in file.Objects)
                {
                    if (obj is SkinnedMeshRenderer smr && smr.m_Materials.Length > 0)
                    {
                        if (smr.m_Mesh.m_PathID == meshPathID ||
                            (smr.m_Mesh.TryGet(out var smrMesh) && smrMesh.m_Name == mesh.m_Name))
                        {
                            foreach (var matPPtr in smr.m_Materials)
                            {
                                if (matPPtr.TryGet(out var mat))
                                    materials.Add(mat);
                                else
                                    materials.Add(null);
                            }
                            CodmDiagnostics.Log($"[Materials] Found {materials.Count} materials for '{mesh.m_Name}' from SMR in {file.fileName}");
                            return materials;
                        }
                    }
                }
            }

            // Strategy 3: Search for Material objects by name convention
            // e.g. mesh "C_M_Bulldozer_Dark_Star_Body_LOD0" -> material containing "Bulldozer_Dark_Star_Body"
            var meshBaseName = mesh.m_Name;
            // Strip LOD suffix
            var lodIdx = meshBaseName.LastIndexOf("_LOD");
            var nameForMatch = lodIdx >= 0 ? meshBaseName.Substring(0, lodIdx) : meshBaseName;

            foreach (var file in Studio.assetsManager.assetsFileList)
            {
                foreach (var obj in file.Objects)
                {
                    if (obj is Material mat && mat.m_Name.IndexOf(nameForMatch, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        materials.Add(mat);
                    }
                }
            }
            if (materials.Count > 0)
            {
                CodmDiagnostics.Log($"[Materials] Found {materials.Count} materials for '{mesh.m_Name}' by name match (pattern='{nameForMatch}')");
                return materials;
            }

            // Strategy 4: Search for any Material in the same asset file as the mesh
            foreach (var obj in meshFile.Objects)
            {
                if (obj is Material mat)
                {
                    materials.Add(mat);
                }
            }
            if (materials.Count > 0)
            {
                CodmDiagnostics.Log($"[Materials] Found {materials.Count} materials for '{mesh.m_Name}' from same asset file");
                return materials;
            }

            Logger.Warning($"[Materials] No materials found for '{mesh.m_Name}'");
            return materials;
        }

        private static void GenerateBlenderScript(Mesh mesh, Avatar avatar, string fbxPath, List<Material> materials)
        {
            var objPath = Path.ChangeExtension(fbxPath, ".obj").Replace("\\", "/");
            var scriptPath = Path.ChangeExtension(fbxPath, ".py");
            var meshName = mesh.m_Name;

            // Build bone data
            var boneNames = new string[mesh.m_BoneNameHashes?.Length ?? 0];
            var bonePaths = new string[boneNames.Length];
            var boneParents = new int[boneNames.Length]; // parent index, -1 for root

            if (avatar != null && mesh.m_BoneNameHashes?.Length > 0)
            {
                var tosDict = avatar.m_TOS.ToDictionary(p => p.Key, p => p.Value);
                var skeleton = avatar.m_Avatar.m_AvatarSkeleton;
                var skeletonPose = avatar.m_Avatar.m_DefaultPose ?? avatar.m_Avatar.m_AvatarSkeletonPose;

                // Build skeleton path list for parent lookup
                var skelPaths = new List<string>();
                foreach (var id in skeleton.m_ID)
                    skelPaths.Add(avatar.FindBonePath(id) ?? "");

                for (int b = 0; b < boneNames.Length; b++)
                {
                    if (tosDict.TryGetValue(mesh.m_BoneNameHashes[b], out var path))
                    {
                        bonePaths[b] = path;
                        boneNames[b] = path.Contains("/") ? path.Substring(path.LastIndexOf('/') + 1) : path;
                    }
                    else
                    {
                        bonePaths[b] = $"Bone_{b}";
                        boneNames[b] = $"Bone_{b}";
                    }
                    boneParents[b] = -1;
                }

                // Find parent bones within the mesh's bone set
                for (int b = 0; b < boneNames.Length; b++)
                {
                    var path = bonePaths[b];
                    if (string.IsNullOrEmpty(path) || !path.Contains("/")) continue;
                    var parentPath = path.Substring(0, path.LastIndexOf('/'));
                    // Walk up the parent chain to find a parent that's also in our bone set
                    while (!string.IsNullOrEmpty(parentPath))
                    {
                        for (int p = 0; p < boneNames.Length; p++)
                        {
                            if (p != b && bonePaths[p] == parentPath)
                            {
                                boneParents[b] = p;
                                goto foundParent;
                            }
                        }
                        var lastSlash = parentPath.LastIndexOf('/');
                        parentPath = lastSlash >= 0 ? parentPath.Substring(0, lastSlash) : "";
                    }
                    foundParent:;
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("import bpy");
            sb.AppendLine("import os");
            sb.AppendLine("from mathutils import Matrix, Vector");
            sb.AppendLine("");
            sb.AppendLine("# Auto-generated by AssetStudio - Skinned Mesh Import Script");
            sb.AppendLine($"# Mesh: {meshName}");
            sb.AppendLine($"# Vertices: {mesh.m_VertexCount}, Bones: {boneNames.Length}");
            sb.AppendLine("");
            sb.AppendLine("# Clear scene");
            sb.AppendLine("bpy.ops.object.select_all(action='SELECT')");
            sb.AppendLine("bpy.ops.object.delete(use_global=False)");
            sb.AppendLine("");

            // Import OBJ
            sb.AppendLine("# Import OBJ mesh");
            sb.AppendLine($"obj_path = r\"{Path.ChangeExtension(fbxPath, ".obj")}\"");
            sb.AppendLine("if os.path.exists(obj_path):");
            sb.AppendLine("    bpy.ops.wm.obj_import(filepath=obj_path)");
            sb.AppendLine("    mesh_obj = bpy.context.selected_objects[0]");
            sb.AppendLine("    mesh_obj.name = '" + meshName + "'");
            sb.AppendLine("else:");
            sb.AppendLine("    print(f'OBJ not found: {obj_path}')");
            sb.AppendLine("    mesh_obj = None");
            sb.AppendLine("");

            if (boneNames.Length > 0 && mesh.m_BindPose?.Length > 0)
            {
                // Create armature
                sb.AppendLine("# Create armature");
                sb.AppendLine($"arm_data = bpy.data.armatures.new('{meshName}_Armature')");
                sb.AppendLine($"arm_obj = bpy.data.objects.new('{meshName}_Armature', arm_data)");
                sb.AppendLine("bpy.context.collection.objects.link(arm_obj)");
                sb.AppendLine("bpy.context.view_layer.objects.active = arm_obj");
                sb.AppendLine("bpy.ops.object.mode_set(mode='EDIT')");
                sb.AppendLine("");

                // Bone names array
                sb.AppendLine("bone_names = [");
                for (int b = 0; b < boneNames.Length; b++)
                    sb.AppendLine($"    '{boneNames[b]}',");
                sb.AppendLine("]");
                sb.AppendLine("");

                // Parent indices array
                sb.AppendLine("bone_parents = [");
                for (int b = 0; b < boneParents.Length; b++)
                    sb.AppendLine($"    {boneParents[b]},");
                sb.AppendLine("]");
                sb.AppendLine("");

                // Bind pose matrices (inverse bind matrices from Unity)
                sb.AppendLine("# Bind pose matrices (inverse bind matrices from Unity, row-major)");
                sb.AppendLine("# These need coordinate conversion: Unity is left-handed Y-up, Blender is right-handed Z-up");
                sb.AppendLine("bind_poses_raw = [");
                for (int b = 0; b < mesh.m_BindPose.Length && b < boneNames.Length; b++)
                {
                    var m = mesh.m_BindPose[b];
                    sb.AppendLine($"    Matrix((({m.M00},{m.M01},{m.M02},{m.M03}),({m.M10},{m.M11},{m.M12},{m.M13}),({m.M20},{m.M21},{m.M22},{m.M23}),({m.M30},{m.M31},{m.M32},{m.M33}))),");
                }
                sb.AppendLine("]");
                sb.AppendLine("");
                sb.AppendLine("# Coordinate conversion: Unity (left-handed Y-up) -> Blender (right-handed Z-up)");
                sb.AppendLine("# Flip X axis (like FBX exporter does) and swap Y/Z");
                sb.AppendLine("correction = Matrix(((-1,0,0,0),(0,0,1,0),(0,1,0,0),(0,0,0,1)))");
                sb.AppendLine("bind_poses = []");
                sb.AppendLine("for m in bind_poses_raw:");
                sb.AppendLine("    bind_poses.append(correction @ m @ correction.inverted())");
                sb.AppendLine("");

                // Create bones in edit mode
                sb.AppendLine("# Create bones");
                sb.AppendLine("edit_bones = {}");
                sb.AppendLine("bone_heads = {}");
                sb.AppendLine("for i, name in enumerate(bone_names):");
                sb.AppendLine("    bone = arm_data.edit_bones.new(name)");
                sb.AppendLine("    bone.length = 0.05");
                sb.AppendLine("    if i < len(bind_poses):");
                sb.AppendLine("        inv_bind = bind_poses[i]");
                sb.AppendLine("        try:");
                sb.AppendLine("            world_mat = inv_bind.inverted()");
                sb.AppendLine("            bone.head = world_mat.translation");
                sb.AppendLine("            bone_heads[name] = bone.head.copy()");
                sb.AppendLine("        except:");
                sb.AppendLine("            bone.head = Vector((0, 0, 0))");
                sb.AppendLine("            bone_heads[name] = bone.head.copy()");
                sb.AppendLine("    bone.tail = bone.head + Vector((0, 0.05, 0))");
                sb.AppendLine("    edit_bones[name] = bone");
                sb.AppendLine("");
                sb.AppendLine("# Set bone parents");
                sb.AppendLine("for i, parent_idx in enumerate(bone_parents):");
                sb.AppendLine("    if parent_idx >= 0:");
                sb.AppendLine("        edit_bones[bone_names[i]].parent = edit_bones[bone_names[parent_idx]]");
                sb.AppendLine("");
                sb.AppendLine("# Point bone tails toward first child (or away from parent)");
                sb.AppendLine("for i, name in enumerate(bone_names):");
                sb.AppendLine("    bone = edit_bones[name]");
                sb.AppendLine("    children = [c for c, p in enumerate(bone_parents) if p == i]");
                sb.AppendLine("    if children:");
                sb.AppendLine("        child_head = bone_heads[bone_names[children[0]]]");
                sb.AppendLine("        direction = child_head - bone.head");
                sb.AppendLine("        if direction.length > 0.001:");
                sb.AppendLine("            bone.tail = child_head");
                sb.AppendLine("    elif bone.parent:");
                sb.AppendLine("        direction = bone.head - bone.parent.head");
                sb.AppendLine("        if direction.length > 0.001:");
                sb.AppendLine("            bone.tail = bone.head + direction.normalized * 0.03");
                sb.AppendLine("");
                sb.AppendLine("bpy.ops.object.mode_set(mode='OBJECT')");
                sb.AppendLine("");

                // Apply vertex groups
                sb.AppendLine("# Apply vertex groups / skin weights");
                sb.AppendLine("if mesh_obj:");
                sb.AppendLine("    # Create vertex groups");
                sb.AppendLine("    for name in bone_names:");
                sb.AppendLine("        mesh_obj.vertex_groups.new(name=name)");
                sb.AppendLine("");

                // Write weights inline
                sb.AppendLine("    # Assign weights");
                sb.AppendLine("    weights = [");
                for (int v = 0; v < mesh.m_Skin.Length; v++)
                {
                    var skin = mesh.m_Skin[v];
                    sb.Append($"        ({skin.boneIndex[0]},{skin.weight[0]},{skin.boneIndex[1]},{skin.weight[1]},{skin.boneIndex[2]},{skin.weight[2]},{skin.boneIndex[3]},{skin.weight[3]}),");
                    if (v % 10 == 9) sb.AppendLine();
                }
                sb.AppendLine("");
                sb.AppendLine("    ]");
                sb.AppendLine("    for vi, w in enumerate(weights):");
                sb.AppendLine("        for j in range(4):");
                sb.AppendLine("            bi = w[j*2]");
                sb.AppendLine("            bw = w[j*2+1]");
                sb.AppendLine("            if bw > 0 and bi < len(bone_names):");
                sb.AppendLine("                mesh_obj.vertex_groups[bone_names[bi]].add([vi], bw, 'REPLACE')");
                sb.AppendLine("");

                // Parent mesh to armature
                sb.AppendLine("    # Parent mesh to armature with armature modifier");
                sb.AppendLine("    mesh_obj.parent = arm_obj");
                sb.AppendLine("    mod = mesh_obj.modifiers.new(name='Armature', type='ARMATURE')");
                sb.AppendLine("    mod.object = arm_obj");
                sb.AppendLine("");
            }

            sb.AppendLine("print('Import complete!')");

            // Build material section
            var materialSection = new StringBuilder();
            if (materials.Count > 0)
            {
                materialSection.AppendLine("# Setup materials and assign to faces per submesh");
                materialSection.AppendLine("if mesh_obj:");

                // Create materials
                for (int mi = 0; mi < materials.Count; mi++)
                {
                    var mat = materials[mi];
                    var matName = mat?.m_Name ?? $"Material_{mi}";
                    materialSection.AppendLine($"    mat = bpy.data.materials.new(name='{FixFileName(matName)}')");
                    materialSection.AppendLine($"    mat.use_nodes = True");

                    // Set diffuse color from material if available
                    if (mat?.m_SavedProperties?.m_Colors != null)
                    {
                        foreach (var color in mat.m_SavedProperties.m_Colors)
                        {
                            if (color.Key == "_Color")
                            {
                                materialSection.AppendLine($"    bsdf = mat.node_tree.nodes.get('Principled BSDF')");
                                materialSection.AppendLine($"    if bsdf: bsdf.inputs['Base Color'].default_value = ({color.Value.R}, {color.Value.G}, {color.Value.B}, {color.Value.A})");
                                break;
                            }
                        }
                    }

                    materialSection.AppendLine($"    mesh_obj.data.materials.append(mat)");
                }
                materialSection.AppendLine("");

                // Assign material indices to faces per submesh
                // OBJ importer creates face groups matching submesh order
                materialSection.AppendLine("    # Assign material indices to polygons per submesh");
                materialSection.AppendLine($"    submesh_face_counts = [");
                for (int si = 0; si < mesh.m_SubMeshes.Length; si++)
                {
                    materialSection.AppendLine($"        {mesh.m_SubMeshes[si].indexCount / 3},");
                }
                materialSection.AppendLine("    ]");
                materialSection.AppendLine("    face_idx = 0");
                materialSection.AppendLine("    for si, count in enumerate(submesh_face_counts):");
                materialSection.AppendLine("        mat_idx = min(si, len(mesh_obj.data.materials) - 1)");
                materialSection.AppendLine("        for fi in range(face_idx, face_idx + count):");
                materialSection.AppendLine("            if fi < len(mesh_obj.data.polygons):");
                materialSection.AppendLine("                mesh_obj.data.polygons[fi].material_index = mat_idx");
                materialSection.AppendLine("        face_idx += count");
                materialSection.AppendLine("");
            }

            // Insert material section before the final print
            var scriptText = sb.ToString();
            scriptText = scriptText.Replace("print('Import complete!')", materialSection.ToString() + "print('Import complete!')");

            File.WriteAllText(scriptPath, scriptText);
            CodmDiagnostics.Log($"[SkinnedFBX] Generated Blender script: {Path.GetFileName(scriptPath)}");
        }

        public static bool ExportMovieTexture(AssetItem item, string exportPath)
        {
            var m_MovieTexture = (MovieTexture)item.Asset;
            if (!TryExportFile(exportPath, item, ".ogv", out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, m_MovieTexture.m_MovieData);
            return true;
        }

        public static bool ExportSprite(AssetItem item, string exportPath)
        {
            var type = Properties.Settings.Default.convertType;
            if (!TryExportFile(exportPath, item, "." + type.ToString().ToLower(), out var exportFullPath))
                return false;
            var image = ((Sprite)item.Asset).GetImage();
            if (image != null)
            {
                using (image)
                {
                    using (var file = File.OpenWrite(exportFullPath))
                    {
                        image.WriteToStream(file, type);
                    }
                    return true;
                }
            }
            return false;
        }

        public static bool ExportRawFile(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".dat", out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, item.Asset.GetRawData());
            return true;
        }

        private static bool TryExportFile(string dir, AssetItem item, string extension, out string fullPath)
        {
            var fileName = FixFileName(item.Text);
            fullPath = Path.Combine(dir, fileName + extension);
            if (!File.Exists(fullPath))
            {
                Directory.CreateDirectory(dir);
                return true;
            }
            fullPath = Path.Combine(dir, fileName + item.UniqueID + extension);
            if (!File.Exists(fullPath))
            {
                Directory.CreateDirectory(dir);
                return true;
            }
            return false;
        }

        private static AnimationClip[] GetParsedAnimationClips(List<AssetItem> animationList)
        {
            if (animationList == null)
                return null;

            var clips = new List<AnimationClip>();
            var stubCount = 0;
            foreach (var item in animationList)
            {
                if (item.Asset is AnimationClip clip)
                {
                    clips.Add(clip);
                }
                else
                {
                    stubCount++;
                    Logger.Warning($"Selected AnimationClip '{item.Text}' PathID={item.Asset?.m_PathID} is a parse stub ({item.Asset?.GetType().Name}), not a real AnimationClip");
                }
            }

            if (stubCount > 0)
            {
                Logger.Warning($"{stubCount}/{animationList.Count} selected AnimationClip(s) failed to parse at load time (see 'Skipping AnimationClip' in log)");
            }

            if (clips.Count == 0 && animationList.Count > 0)
            {
                throw new InvalidOperationException(
                    $"None of the {animationList.Count} selected AnimationClip(s) parsed successfully. " +
                    "They are soft-fail stubs from AssetsManager (wrong Unity version / clip layout). " +
                    "Reload and check assetstudio.log for Skipping AnimationClip errors.");
            }

            return clips.ToArray();
        }

        public static bool ExportAnimator(AssetItem item, string exportPath, List<AssetItem> animationList = null)
        {
            var exportFullPath = Path.Combine(exportPath, item.Text, item.Text + ".fbx");
            if (File.Exists(exportFullPath))
            {
                exportFullPath = Path.Combine(exportPath, item.Text + item.UniqueID, item.Text + ".fbx");
            }
            var m_Animator = (Animator)item.Asset;
            var clips = GetParsedAnimationClips(animationList);
            var convert = clips != null
                ? new ModelConverter(m_Animator, Properties.Settings.Default.convertType, clips)
                : new ModelConverter(m_Animator, Properties.Settings.Default.convertType);
            ExportFbx(convert, exportFullPath);
            ExportCompanionFormats(convert, exportFullPath);
            return true;
        }

        /// <summary>
        /// EXCEPTION-ONLY export: selected AnimationClips → synthetic <c>Camera</c> bone at origin.
        /// Completely independent of <see cref="ExportAnimator"/> / <see cref="ModelConverter"/>.
        /// Only clips that animate Animator root (path hash 0) are accepted.
        /// </summary>
        public static bool ExportOrphanCameraAnimationClips(List<AssetItem> animationList, string exportPath)
        {
            var clips = GetParsedAnimationClips(animationList);
            if (clips == null || clips.Length == 0)
                throw new InvalidOperationException("No parsed AnimationClips to export.");

            var accepted = new List<AnimationClip>();
            var rejected = new List<string>();
            foreach (var clip in clips)
            {
                if (OrphanAnimationClipConverter.TryValidateRootOnly(clip, out var reason))
                    accepted.Add(clip);
                else
                    rejected.Add($"{clip.m_Name}: {reason}");
            }

            if (accepted.Count == 0)
            {
                throw new InvalidOperationException(
                    "No clips eligible for orphan/camera export (root path 0 only).\n\n" +
                    string.Join("\n", rejected));
            }

            if (rejected.Count > 0)
            {
                Logger.Warning($"[OrphanCamera] Skipped {rejected.Count} ineligible clip(s):\n" + string.Join("\n", rejected));
            }

            Logger.Warning(
                "[OrphanCamera] EXCEPTION EXPORT — inventing a synthetic 'Camera' bone at origin. " +
                "This is not a real skeleton. Do not use for normal weapon/character clips.");

            var convert = new OrphanAnimationClipConverter(accepted);

            // Flat short paths: deep CODM pack folders + long clip names easily exceed MAX_PATH (260).
            // Nesting ClipName/ClipName.fbx made a 290-char path and left an empty folder after FBX failed.
            Directory.CreateDirectory(exportPath);
            const string baseName = "Camera";
            var exportFullPath = Path.Combine(exportPath, baseName + ".fbx");
            if (exportFullPath.Length >= 240)
            {
                Logger.Warning(
                    $"[OrphanCamera] Export path is very long ({exportFullPath.Length} chars). " +
                    "If writes fail, pick a shorter folder (e.g. C:\\exports\\cam).");
            }

            var errors = new List<string>();
            var wrote = new List<string>();

            // Cast + GLB first — these are what you actually use; don't depend on FBX succeeding.
            try
            {
                var scaleFactor = (float)Properties.Settings.Default.scaleFactor;
                var glbPath = Path.Combine(exportPath, baseName + ".glb");
                GltfExporter.ExportGlb(glbPath, convert, scaleFactor);
                if (File.Exists(glbPath))
                    wrote.Add(Path.GetFileName(glbPath));
            }
            catch (Exception ex)
            {
                errors.Add("GLB: " + ex.Message);
                Logger.Warning($"[OrphanCamera] GLB failed: {ex.Message}");
            }

            try
            {
                var scaleFactor = (float)Properties.Settings.Default.scaleFactor;
                CastExporter.Export(exportPath, baseName, convert, scaleFactor);
                var castPath = Path.Combine(exportPath, baseName + ".cast");
                if (File.Exists(castPath))
                    wrote.Add(Path.GetFileName(castPath));
                var animDir = Path.Combine(exportPath, "anims");
                if (Directory.Exists(animDir))
                {
                    foreach (var f in Directory.GetFiles(animDir, "*.cast"))
                        wrote.Add("anims/" + Path.GetFileName(f));
                }
            }
            catch (Exception ex)
            {
                errors.Add("Cast: " + ex.Message);
                Logger.Warning($"[OrphanCamera] Cast failed: {ex.Message}");
            }

            try
            {
                ExportFbxOrphanCamera(convert, exportFullPath);
                if (File.Exists(exportFullPath))
                    wrote.Add(Path.GetFileName(exportFullPath));
            }
            catch (Exception ex)
            {
                errors.Add("FBX: " + ex.Message);
                Logger.Warning($"[OrphanCamera] FBX failed (Cast/GLB may still be OK): {ex.Message}");
            }

            if (wrote.Count == 0)
            {
                throw new InvalidOperationException(
                    "Orphan/Camera export wrote no files.\n\n" +
                    string.Join("\n", errors) +
                    (exportFullPath.Length >= 240
                        ? "\n\nHint: export path may be too long — choose a shorter folder."
                        : ""));
            }

            Logger.Info("[OrphanCamera] Wrote: " + string.Join(", ", wrote));
            if (errors.Count > 0)
                Logger.Warning("[OrphanCamera] Partial failures:\n" + string.Join("\n", errors));

            return true;
        }

        /// <summary>FBX for orphan/camera: force bone + animations so the synthetic Camera is usable.</summary>
        private static void ExportFbxOrphanCamera(IImported convert, string exportPath)
        {
            var eulerFilter = Properties.Settings.Default.eulerFilter;
            var filterPrecision = (float)Properties.Settings.Default.filterPrecision;
            var exportBlendShape = Properties.Settings.Default.exportBlendShape;
            var boneSize = (int)Properties.Settings.Default.boneSize;
            var exportAllUvsAsDiffuseMaps = Properties.Settings.Default.exportAllUvsAsDiffuseMaps;
            var scaleFactor = (float)Properties.Settings.Default.scaleFactor;
            var fbxVersion = Properties.Settings.Default.fbxVersion;
            var fbxFormat = Properties.Settings.Default.fbxFormat;
            ModelExporter.ExportFbx(exportPath, convert, eulerFilter, filterPrecision,
                true, true, true, exportBlendShape, true, boneSize, exportAllUvsAsDiffuseMaps, scaleFactor, fbxVersion, fbxFormat == 1);
        }

        public static void ExportGameObject(GameObject gameObject, string exportPath, List<AssetItem> animationList = null)
        {
            var clips = GetParsedAnimationClips(animationList);
            var convert = clips != null
                ? new ModelConverter(gameObject, Properties.Settings.Default.convertType, clips)
                : new ModelConverter(gameObject, Properties.Settings.Default.convertType);
            exportPath = exportPath + FixFileName(gameObject.m_Name) + ".fbx";
            ExportFbx(convert, exportPath);
            ExportCompanionFormats(convert, exportPath);
        }

        public static void ExportGameObjectMerge(List<GameObject> gameObject, string exportPath, List<AssetItem> animationList = null)
        {
            var rootName = Path.GetFileNameWithoutExtension(exportPath);
            var clips = GetParsedAnimationClips(animationList);
            var convert = clips != null
                ? new ModelConverter(rootName, gameObject, Properties.Settings.Default.convertType, clips)
                : new ModelConverter(rootName, gameObject, Properties.Settings.Default.convertType);
            ExportFbx(convert, exportPath);
            ExportCompanionFormats(convert, exportPath);
        }

        /// <summary>
        /// Optional GLB / Cast sidecars next to the FBX. Toggled in Export Options.
        /// </summary>
        private static void ExportCompanionFormats(IImported convert, string fbxPath)
        {
            var scaleFactor = (float)Properties.Settings.Default.scaleFactor;
            var directory = Path.GetDirectoryName(fbxPath);
            var baseName = Path.GetFileNameWithoutExtension(fbxPath);

            if (Properties.Settings.Default.exportGlb)
            {
                var glbPath = Path.ChangeExtension(fbxPath, ".glb");
                try
                {
                    GltfExporter.ExportGlb(glbPath, convert, scaleFactor);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[glTF] Failed to export glTF alongside FBX: {ex.Message}");
                }
            }

            if (Properties.Settings.Default.exportCast)
            {
                try
                {
                    CastExporter.Export(directory, baseName, convert, scaleFactor);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[Cast] Failed to export Cast alongside FBX: {ex.Message}");
                }
            }
        }

        private static void ExportFbx(IImported convert, string exportPath)
        {
            var eulerFilter = Properties.Settings.Default.eulerFilter;
            var filterPrecision = (float)Properties.Settings.Default.filterPrecision;
            var exportAllNodes = Properties.Settings.Default.exportAllNodes;
            var exportSkins = Properties.Settings.Default.exportSkins;
            var exportAnimations = Properties.Settings.Default.exportAnimations;
            var exportBlendShape = Properties.Settings.Default.exportBlendShape;
            var castToBone = Properties.Settings.Default.castToBone;
            var boneSize = (int)Properties.Settings.Default.boneSize;
            var exportAllUvsAsDiffuseMaps = Properties.Settings.Default.exportAllUvsAsDiffuseMaps;
            var scaleFactor = (float)Properties.Settings.Default.scaleFactor;
            var fbxVersion = Properties.Settings.Default.fbxVersion;
            var fbxFormat = Properties.Settings.Default.fbxFormat;
            ModelExporter.ExportFbx(exportPath, convert, eulerFilter, filterPrecision,
                exportAllNodes, exportSkins, exportAnimations, exportBlendShape, castToBone, boneSize, exportAllUvsAsDiffuseMaps, scaleFactor, fbxVersion, fbxFormat == 1);
        }

        private static void ExportFbxDirect(IImported convert, string exportPath)
        {
            var eulerFilter = Properties.Settings.Default.eulerFilter;
            var filterPrecision = (float)Properties.Settings.Default.filterPrecision;
            var exportSkins = true;
            var exportAnimations = false;
            var exportBlendShape = Properties.Settings.Default.exportBlendShape;
            var castToBone = true;
            var boneSize = (int)Properties.Settings.Default.boneSize;
            var exportAllUvsAsDiffuseMaps = Properties.Settings.Default.exportAllUvsAsDiffuseMaps;
            var scaleFactor = (float)Properties.Settings.Default.scaleFactor;
            // Force FBX 7.4 binary (versionIndex=5 = FBX_2014_00, isAscii=false)
            // This is the most compatible format for Blender
            ModelExporter.ExportFbx(exportPath, convert, eulerFilter, filterPrecision,
                true, exportSkins, exportAnimations, exportBlendShape, castToBone, boneSize, exportAllUvsAsDiffuseMaps, scaleFactor, 5, false);
        }

        public static bool ExportDumpFile(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".txt", out var exportFullPath))
                return false;
            var str = item.Asset.Dump();
            if (str == null && item.Asset is MonoBehaviour m_MonoBehaviour)
            {
                var m_Type = Studio.MonoBehaviourToTypeTree(m_MonoBehaviour);
                str = m_MonoBehaviour.Dump(m_Type);
            }
            if (str != null)
            {
                File.WriteAllText(exportFullPath, str);
                return true;
            }
            return false;
        }

        public static bool ExportConvertFile(AssetItem item, string exportPath)
        {
            switch (item.Type)
            {
                case ClassIDType.Texture2D:
                    return ExportTexture2D(item, exportPath);
                case ClassIDType.AudioClip:
                    return ExportAudioClip(item, exportPath);
                case ClassIDType.Shader:
                    return ExportShader(item, exportPath);
                case ClassIDType.TextAsset:
                    return ExportTextAsset(item, exportPath);
                case ClassIDType.MonoBehaviour:
                    return ExportMonoBehaviour(item, exportPath);
                case ClassIDType.Font:
                    return ExportFont(item, exportPath);
                case ClassIDType.Mesh:
                    var meshAsset = (Mesh)item.Asset;
                    if (meshAsset.m_Skin?.Length > 0 && meshAsset.m_BindPose?.Length > 0)
                    {
                        // Export single-mesh glTF as primary format (works in Blender, Maya, etc.)
                        ExportSkinnedMeshGlb(item, exportPath);
                        // Also export OBJ for reference
                        ExportMesh(item, exportPath);
                        // Also export FBX + Blender script as fallback
                        return ExportSkinnedMeshFbx(item, exportPath);
                    }
                    return ExportMesh(item, exportPath);
                case ClassIDType.VideoClip:
                    return ExportVideoClip(item, exportPath);
                case ClassIDType.MovieTexture:
                    return ExportMovieTexture(item, exportPath);
                case ClassIDType.Sprite:
                    return ExportSprite(item, exportPath);
                case ClassIDType.Animator:
                    return ExportAnimator(item, exportPath);
                case ClassIDType.AnimationClip:
                    return false;
                default:
                    return ExportRawFile(item, exportPath);
            }
        }

        private static string GetBoneName(string[] boneNames, int index)
        {
            if (index >= 0 && index < boneNames.Length)
                return boneNames[index];
            return $"Unknown_{index}";
        }

        public static string FixFileName(string str)
        {
            if (str.Length >= 260) return Path.GetRandomFileName();
            return Path.GetInvalidFileNameChars().Aggregate(str, (current, c) => current.Replace(c, '_'));
        }
    }
}
