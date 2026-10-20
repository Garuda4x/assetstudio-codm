using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GLTF = SharpGLTF.Schema2;
using NVector3 = System.Numerics.Vector3;
using NMatrix4x4 = System.Numerics.Matrix4x4;
using NQuaternion = System.Numerics.Quaternion;

namespace AssetStudio
{
    public static class GltfExporter
    {
        /// <summary>
        /// Export an IImported model to glTF Binary (.glb) format.
        /// Uses the Schema2 API for full control over skinning, skeleton, and materials.
        /// </summary>
        public static void ExportGlb(string path, IImported imported, float scaleFactor = 1.0f)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var rootFrame = imported.RootFrame;
            if (rootFrame == null)
            {
                Logger.Warning("[glTF] No root frame, nothing to export");
                return;
            }

            var model = GLTF.ModelRoot.CreateModel();
            var scene = model.UseScene("Default");
            model.DefaultScene = scene;

            // Collect bone + animation track paths so animated nodes aren't culled
            var bonePaths = new HashSet<string>();
            foreach (var mesh in imported.MeshList)
            {
                if (mesh.BoneList != null)
                {
                    foreach (var bone in mesh.BoneList)
                    {
                        if (bone.Path != null)
                            bonePaths.Add(bone.Path);
                    }
                }
                if (mesh.Path != null)
                    bonePaths.Add(mesh.Path);
            }

            if (imported.AnimationList != null)
            {
                foreach (var anim in imported.AnimationList)
                {
                    if (anim?.TrackList == null) continue;
                    foreach (var track in anim.TrackList)
                    {
                        if (track?.Path != null)
                            bonePaths.Add(track.Path);
                    }
                }
            }

            // Required frame paths = bones/tracks + all ancestors
            var requiredPaths = new HashSet<string>();
            requiredPaths.Add(rootFrame.Path);
            foreach (var bp in bonePaths)
            {
                var p = bp;
                while (!string.IsNullOrEmpty(p))
                {
                    requiredPaths.Add(p);
                    var lastSlash = p.LastIndexOf('/');
                    p = lastSlash >= 0 ? p.Substring(0, lastSlash) : null;
                }
            }

            var nodeMap = new Dictionary<string, GLTF.Node>();
            var rootGltfNode = CreateNodeHierarchy(scene, rootFrame, null, nodeMap, scaleFactor, requiredPaths);

            // Build texture lookup from imported textures
            var textureMap = new Dictionary<string, ImportedTexture>();
            foreach (var tex in imported.TextureList)
            {
                textureMap[tex.Name] = tex;
            }

            // Build materials
            var gltfMaterialMap = new Dictionary<string, GLTF.Material>();
            foreach (var mat in imported.MaterialList)
            {
                var gltfMat = model.CreateMaterial(mat.Name);
                gltfMat.DoubleSided = true;
                gltfMat.InitializePBRMetallicRoughness();

                var diffuse = mat.Diffuse;
                var baseColorChannel = gltfMat.FindChannel("BaseColor");
                if (baseColorChannel.HasValue)
                {
                    var ch = baseColorChannel.Value;
                    ch.Parameter = new System.Numerics.Vector4(diffuse.R, diffuse.G, diffuse.B, diffuse.A);

                    // Embed diffuse/base color texture if available
                    if (mat.Textures != null)
                    {
                        var diffuseTex = mat.Textures.FirstOrDefault(t => t.Dest == 0);
                        if (diffuseTex != null && textureMap.TryGetValue(diffuseTex.Name, out var texData) && texData.Data != null && texData.Data.Length > 0)
                        {
                            try
                            {
                                var img = model.UseImage(new SharpGLTF.Memory.MemoryImage(texData.Data));
                                var gltfTex = model.UseTexture(img, null);
                                ch.SetTexture(0, gltfTex);
                            }
                            catch (Exception ex)
                            {
                                Logger.Warning($"[glTF] Failed to embed diffuse texture '{diffuseTex.Name}': {ex.Message}");
                            }
                        }
                    }
                }

                // Embed normal map if available
                if (mat.Textures != null)
                {
                    var normalTex = mat.Textures.FirstOrDefault(t => t.Dest == 1 || t.Dest == 3);
                    if (normalTex != null && textureMap.TryGetValue(normalTex.Name, out var normalData) && normalData.Data != null && normalData.Data.Length > 0)
                    {
                        var normalChannel = gltfMat.FindChannel("Normal");
                        if (normalChannel.HasValue)
                        {
                            try
                            {
                                var img = model.UseImage(new SharpGLTF.Memory.MemoryImage(normalData.Data));
                                var gltfTex = model.UseTexture(img, null);
                                normalChannel.Value.SetTexture(0, gltfTex);
                            }
                            catch (Exception ex)
                            {
                                Logger.Warning($"[glTF] Failed to embed normal texture '{normalTex.Name}': {ex.Message}");
                            }
                        }
                    }
                }

                // Set metallic/roughness defaults
                var mrChannel = gltfMat.FindChannel("MetallicRoughness");
                if (mrChannel.HasValue)
                {
                    var ch = mrChannel.Value;
                    ch.Parameter = new System.Numerics.Vector4(0, 1, 0, 0); // metallic=0, roughness=1
                }

                gltfMaterialMap[mat.Name] = gltfMat;
            }

            // Export each mesh
            var totalBoneCount = 0;
            var hasAnyAnimTracks = imported.AnimationList != null &&
                imported.AnimationList.Any(a => a?.TrackList != null && a.TrackList.Count > 0);

            // Collect every skin joint so animation channels only target armature bones.
            // Channels on non-joint nodes make Blender invent a second Action per clip
            // (same glTF anim name + different object suffix).
            var skinJointNodes = new HashSet<GLTF.Node>();

            foreach (var importedMesh in imported.MeshList)
            {
                var hasRealSkin = importedMesh.BoneList != null && importedMesh.BoneList.Count > 0 &&
                              importedMesh.VertexList.Any(v => v.BoneIndices != null && v.Weights != null);
                // Unskinned weapon hierarchies: without a Skin, Blender creates one Action per
                // empty. Force a dummy skin so it imports as one Armature Action per clip.
                var forceArmature = !hasRealSkin && hasAnyAnimTracks;
                var hasSkin = hasRealSkin || forceArmature;

                // Find the frame node for this mesh
                GLTF.Node meshGltfNode;
                if (importedMesh.Path == null || !nodeMap.TryGetValue(importedMesh.Path, out meshGltfNode))
                {
                    meshGltfNode = rootGltfNode;
                }

                var vertCount = importedMesh.VertexList.Count;

                // Build vertex data arrays
                var positions = new byte[vertCount * 12];
                var normals = importedMesh.hasNormal ? new byte[vertCount * 12] : null;
                var hasUV0 = importedMesh.hasUV != null && importedMesh.hasUV[0];
                var uvs = hasUV0 ? new byte[vertCount * 8] : null;
                var jointsBuf = hasSkin ? new byte[vertCount * 8] : null; // VEC4 UNSIGNED_SHORT
                var weightsBuf = hasSkin ? new byte[vertCount * 16] : null; // VEC4 FLOAT

                for (int j = 0; j < vertCount; j++)
                {
                    var v = importedMesh.VertexList[j];

                    // Position
                    WriteFloat(positions, j * 12, v.Vertex.X * scaleFactor);
                    WriteFloat(positions, j * 12 + 4, v.Vertex.Y * scaleFactor);
                    WriteFloat(positions, j * 12 + 8, v.Vertex.Z * scaleFactor);

                    // Normal
                    if (normals != null)
                    {
                        WriteFloat(normals, j * 12, v.Normal.X);
                        WriteFloat(normals, j * 12 + 4, v.Normal.Y);
                        WriteFloat(normals, j * 12 + 8, v.Normal.Z);
                    }

                    // UV0 (flip V for glTF)
                    if (uvs != null && v.UV != null && v.UV[0] != null)
                    {
                        WriteFloat(uvs, j * 8, v.UV[0][0]);
                        WriteFloat(uvs, j * 8 + 4, 1.0f - v.UV[0][1]);
                    }

                    // Skinning
                    if (hasRealSkin && v.BoneIndices != null && v.Weights != null)
                    {
                        WriteUShort(jointsBuf, j * 8, (ushort)v.BoneIndices[0]);
                        WriteUShort(jointsBuf, j * 8 + 2, (ushort)v.BoneIndices[1]);
                        WriteUShort(jointsBuf, j * 8 + 4, (ushort)v.BoneIndices[2]);
                        WriteUShort(jointsBuf, j * 8 + 6, (ushort)v.BoneIndices[3]);

                        float totalW = v.Weights[0] + v.Weights[1] + v.Weights[2] + v.Weights[3];
                        if (totalW > 0)
                        {
                            WriteFloat(weightsBuf, j * 16, v.Weights[0] / totalW);
                            WriteFloat(weightsBuf, j * 16 + 4, v.Weights[1] / totalW);
                            WriteFloat(weightsBuf, j * 16 + 8, v.Weights[2] / totalW);
                            WriteFloat(weightsBuf, j * 16 + 12, v.Weights[3] / totalW);
                        }
                        else
                        {
                            WriteFloat(weightsBuf, j * 16, 1.0f);
                        }
                    }
                    else if (hasSkin)
                    {
                        // Real skin missing weights, or forced armature: bind fully to joint 0
                        WriteFloat(weightsBuf, j * 16, 1.0f);
                    }
                }

                // Create buffer views
                var posView = model.UseBufferView(positions, 0, null, 0, GLTF.BufferMode.ARRAY_BUFFER);
                var posAccessor = model.CreateAccessor();
                posAccessor.SetVertexData(posView, 0, vertCount, GLTF.DimensionType.VEC3, GLTF.EncodingType.FLOAT, false);

                GLTF.Accessor normalAccessor = null;
                if (normals != null)
                {
                    var normalView = model.UseBufferView(normals, 0, null, 0, GLTF.BufferMode.ARRAY_BUFFER);
                    normalAccessor = model.CreateAccessor();
                    normalAccessor.SetVertexData(normalView, 0, vertCount, GLTF.DimensionType.VEC3, GLTF.EncodingType.FLOAT, false);
                }

                GLTF.Accessor uvAccessor = null;
                if (uvs != null)
                {
                    var uvView = model.UseBufferView(uvs, 0, null, 0, GLTF.BufferMode.ARRAY_BUFFER);
                    uvAccessor = model.CreateAccessor();
                    uvAccessor.SetVertexData(uvView, 0, vertCount, GLTF.DimensionType.VEC2, GLTF.EncodingType.FLOAT, false);
                }

                GLTF.Accessor jointsAccessor = null;
                GLTF.Accessor weightsAccessor = null;
                if (hasSkin)
                {
                    var jointsView = model.UseBufferView(jointsBuf, 0, null, 0, GLTF.BufferMode.ARRAY_BUFFER);
                    jointsAccessor = model.CreateAccessor();
                    jointsAccessor.SetVertexData(jointsView, 0, vertCount, GLTF.DimensionType.VEC4, GLTF.EncodingType.UNSIGNED_SHORT, false);

                    var weightsView = model.UseBufferView(weightsBuf, 0, null, 0, GLTF.BufferMode.ARRAY_BUFFER);
                    weightsAccessor = model.CreateAccessor();
                    weightsAccessor.SetVertexData(weightsView, 0, vertCount, GLTF.DimensionType.VEC4, GLTF.EncodingType.FLOAT, false);
                }

                // Create mesh with primitives per submesh
                var gltfMesh = model.CreateMesh(importedMesh.Path ?? "Mesh");

                foreach (var submesh in importedMesh.SubmeshList)
                {
                    // Build index buffer
                    var indices = new List<uint>();
                    foreach (var face in submesh.FaceList)
                    {
                        indices.Add((uint)(face.VertexIndices[0] + submesh.BaseVertex));
                        indices.Add((uint)(face.VertexIndices[1] + submesh.BaseVertex));
                        indices.Add((uint)(face.VertexIndices[2] + submesh.BaseVertex));
                    }

                    var idxBytes = new byte[indices.Count * 4];
                    for (int j = 0; j < indices.Count; j++)
                    {
                        WriteUInt(idxBytes, j * 4, indices[j]);
                    }

                    var idxView = model.UseBufferView(idxBytes, 0, null, 0, GLTF.BufferMode.ELEMENT_ARRAY_BUFFER);
                    var idxAccessor = model.CreateAccessor();
                    idxAccessor.SetIndexData(idxView, 0, indices.Count, GLTF.IndexEncodingType.UNSIGNED_INT);

                    var primitive = gltfMesh.CreatePrimitive();
                    primitive.DrawPrimitiveType = GLTF.PrimitiveType.TRIANGLES;

                    primitive.SetVertexAccessor("POSITION", posAccessor);
                    if (normalAccessor != null) primitive.SetVertexAccessor("NORMAL", normalAccessor);
                    if (uvAccessor != null) primitive.SetVertexAccessor("TEXCOORD_0", uvAccessor);
                    if (jointsAccessor != null) primitive.SetVertexAccessor("JOINTS_0", jointsAccessor);
                    if (weightsAccessor != null) primitive.SetVertexAccessor("WEIGHTS_0", weightsAccessor);
                    primitive.SetIndexAccessor(idxAccessor);

                    // Assign material
                    if (submesh.Material != null && gltfMaterialMap.TryGetValue(submesh.Material, out var gltfMat))
                    {
                        primitive.Material = gltfMat;
                    }
                }

                // glTF spec: nodes with a skin must not have non-identity transforms.
                // If the mesh node has a transform, create a child node for the mesh+skin.
                if (hasSkin)
                {
                    // Leaf name only — full Path here made Blender show huge object names.
                    var skinLeaf = "Mesh";
                    if (!string.IsNullOrEmpty(importedMesh.Path))
                    {
                        var slash = importedMesh.Path.LastIndexOf('/');
                        skinLeaf = (slash >= 0 ? importedMesh.Path.Substring(slash + 1) : importedMesh.Path) + "_Mesh";
                    }
                    var skinNode = meshGltfNode.CreateNode(skinLeaf);
                    skinNode.Mesh = gltfMesh;
                    meshGltfNode = skinNode; // reassign so skin gets attached to identity-transform node
                }
                else
                {
                    meshGltfNode.Mesh = gltfMesh;
                }

                // Create skin
                if (hasSkin)
                {
                    var boneList = hasRealSkin
                        ? importedMesh.BoneList
                        : new List<ImportedBone>
                        {
                            // Bind rigid mesh to its attach node; animated empties become extra joints
                            // so Blender builds one Armature Action per clip.
                            new ImportedBone
                            {
                                Path = importedMesh.Path ?? rootFrame.Path,
                                Matrix = Matrix4x4.Scale(new Vector3(1, 1, 1))
                            }
                        };

                    var skin = model.CreateSkin();
                    skin.Skeleton = rootGltfNode;

                    // Weighted bones first — vertex JOINTS_0 indices must keep this order.
                    // Then append every animated / ancestor node so Blender treats them as
                    // armature bones (one Action per clip) instead of separate Objects.
                    var jointBindings = new List<(GLTF.Node node, NMatrix4x4 ibm)>(boneList.Count + 64);
                    var jointPaths = new HashSet<string>();

                    for (int i = 0; i < boneList.Count; i++)
                    {
                        GLTF.Node jointNode = null;
                        var bonePath = boneList[i].Path;
                        if (bonePath != null)
                            nodeMap.TryGetValue(bonePath, out jointNode);
                        if (jointNode == null)
                            jointNode = rootGltfNode.CreateNode($"Bone_{i}");

                        jointBindings.Add((jointNode, ConvertBindMatrix(boneList[i].Matrix)));
                        if (bonePath != null)
                            jointPaths.Add(bonePath);
                    }

                    var extraPaths = CollectExtraSkinJointPaths(
                        imported, boneList, jointPaths, rootFrame.Path, importedMesh.Path);
                    foreach (var jointPath in extraPaths
                        .OrderBy(p => p.Count(c => c == '/'))
                        .ThenBy(p => p, StringComparer.Ordinal))
                    {
                        if (!nodeMap.TryGetValue(jointPath, out var jointNode))
                            continue;

                        var world = jointNode.WorldMatrix;
                        if (!NMatrix4x4.Invert(world, out var ibm))
                            ibm = NMatrix4x4.Identity;

                        jointBindings.Add((jointNode, ibm));
                        jointPaths.Add(jointPath);
                    }

                    totalBoneCount = jointBindings.Count;
                    skin.BindJoints(jointBindings.ToArray());
                    meshGltfNode.Skin = skin;
                    foreach (var (jn, _) in jointBindings)
                        skinJointNodes.Add(jn);
                    if (skin.Skeleton != null)
                        skinJointNodes.Add(skin.Skeleton);

                    if (forceArmature)
                        CodmDiagnostics.Log($"[glTF] Forced armature skin for unskinned mesh: {jointBindings.Count} joints");
                    else
                        CodmDiagnostics.Log($"[glTF] Skin joints: {importedMesh.BoneList.Count} weighted + " +
                            $"{jointBindings.Count - importedMesh.BoneList.Count} animated/hierarchy extras");
                }
            }

            // One glTF animation per Unity AnimationClip → Blender imports each as an Action
            var animCount = ExportAnimations(model, imported, nodeMap, scaleFactor, skinJointNodes);

            model.MergeBuffers();
            model.SaveGLB(path);

            CodmDiagnostics.Log($"[glTF] Exported to {Path.GetFileName(path)}: " +
                $"{imported.MeshList.Count} meshes, " +
                $"{imported.MaterialList.Count} materials, " +
                $"{totalBoneCount} bones, " +
                $"{animCount} animations");
        }

        /// <summary>
        /// Writes <see cref="IImported.AnimationList"/> as named glTF animations.
        /// With animated nodes registered as skin joints, Blender maps these to one
        /// Armature Action per clip (pose.bones) instead of per-object Actions.
        /// </summary>
        private static int ExportAnimations(GLTF.ModelRoot model, IImported imported,
            Dictionary<string, GLTF.Node> nodeMap, float scaleFactor, HashSet<GLTF.Node> skinJointNodes)
        {
            if (imported.AnimationList == null || imported.AnimationList.Count == 0)
                return 0;

            var written = 0;
            var skippedNonJoint = 0;
            foreach (var iAnim in imported.AnimationList)
            {
                if (iAnim?.TrackList == null || iAnim.TrackList.Count == 0)
                    continue;

                var animName = string.IsNullOrEmpty(iAnim.Name) ? $"Animation_{written}" : iAnim.Name;
                GLTF.Animation animation = null;
                var channelCount = 0;

                foreach (var track in iAnim.TrackList)
                {
                    if (track?.Path == null || !nodeMap.TryGetValue(track.Path, out var node))
                        continue;

                    // Prefer armature-only channels when a skin exists (avoids duplicate Blender Actions).
                    if (skinJointNodes != null && skinJointNodes.Count > 0 && !skinJointNodes.Contains(node))
                    {
                        skippedNonJoint++;
                        continue;
                    }

                    if (track.Translations != null && track.Translations.Count > 0)
                    {
                        var keys = BuildVector3Keys(track.Translations, scaleFactor);
                        if (keys.Count > 0)
                        {
                            if (animation == null)
                                animation = model.CreateAnimation(animName);
                            animation.CreateTranslationChannel(node, keys, true);
                            channelCount++;
                        }
                    }

                    if (track.Rotations != null && track.Rotations.Count > 0)
                    {
                        var keys = BuildRotationKeys(track.Rotations);
                        if (keys.Count > 0)
                        {
                            if (animation == null)
                                animation = model.CreateAnimation(animName);
                            animation.CreateRotationChannel(node, keys, true);
                            channelCount++;
                        }
                    }

                    if (track.Scalings != null && track.Scalings.Count > 0)
                    {
                        var keys = BuildVector3Keys(track.Scalings, 1.0f);
                        if (keys.Count > 0)
                        {
                            if (animation == null)
                                animation = model.CreateAnimation(animName);
                            animation.CreateScaleChannel(node, keys, true);
                            channelCount++;
                        }
                    }
                }

                if (channelCount > 0)
                    written++;
                else
                    Logger.Warning($"[glTF] Animation '{animName}' had no channels matching exported nodes");
            }

            if (skippedNonJoint > 0)
                CodmDiagnostics.Log($"[glTF] Skipped {skippedNonJoint} track(s) on non-skin nodes (avoids duplicate Blender Actions)");

            return written;
        }

        /// <summary>
        /// Nodes that receive animation (plus their ancestors) must be skin joints so Blender
        /// imports them as armature bones and keeps one Action per glTF animation.
        /// Weighted BoneList entries are excluded — they are already joints at fixed indices.
        /// </summary>
        private static HashSet<string> CollectExtraSkinJointPaths(
            IImported imported,
            List<ImportedBone> boneList,
            HashSet<string> alreadyJointPaths,
            string rootPath,
            string meshPath)
        {
            var seeds = new HashSet<string>();

            // Ancestors of weighted bones (keeps armature hierarchy connected)
            foreach (var bone in boneList)
            {
                if (bone?.Path == null) continue;
                AddAncestorPaths(bone.Path, seeds);
            }

            // Every animated track path + ancestors
            if (imported.AnimationList != null)
            {
                foreach (var anim in imported.AnimationList)
                {
                    if (anim?.TrackList == null) continue;
                    foreach (var track in anim.TrackList)
                    {
                        if (track?.Path == null) continue;
                        seeds.Add(track.Path);
                        AddAncestorPaths(track.Path, seeds);
                    }
                }
            }

            seeds.RemoveWhere(p =>
                string.IsNullOrEmpty(p) ||
                p == rootPath ||
                (meshPath != null && p == meshPath) ||
                alreadyJointPaths.Contains(p));

            return seeds;
        }

        private static void AddAncestorPaths(string path, HashSet<string> dest)
        {
            var p = ParentPath(path);
            while (!string.IsNullOrEmpty(p))
            {
                dest.Add(p);
                p = ParentPath(p);
            }
        }

        private static string ParentPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var lastSlash = path.LastIndexOf('/');
            return lastSlash >= 0 ? path.Substring(0, lastSlash) : null;
        }

        private static Dictionary<float, NVector3> BuildVector3Keys(
            List<ImportedKeyframe<Vector3>> frames, float scaleFactor)
        {
            // Duplicate times: last key wins (glTF sampler keys must be unique)
            var keys = new Dictionary<float, NVector3>(frames.Count);
            foreach (var kf in frames)
            {
                keys[kf.time] = new NVector3(
                    kf.value.X * scaleFactor,
                    kf.value.Y * scaleFactor,
                    kf.value.Z * scaleFactor);
            }
            return keys;
        }

        private static Dictionary<float, NQuaternion> BuildRotationKeys(
            List<ImportedKeyframe<Vector3>> frames)
        {
            var keys = new Dictionary<float, NQuaternion>(frames.Count);
            foreach (var kf in frames)
            {
                keys[kf.time] = EulerToQuaternion(kf.value.X, kf.value.Y, kf.value.Z);
            }
            return keys;
        }

        private static GLTF.Node CreateNodeHierarchy(GLTF.Scene scene, ImportedFrame frame, GLTF.Node parent,
            Dictionary<string, GLTF.Node> nodeMap, float scaleFactor, HashSet<string> requiredPaths)
        {
            // Blender names Actions "{clip}_{object}". Root becomes the armature object — keep short.
            var displayName = parent == null ? ShortArmatureName(frame.Name) : frame.Name;
            GLTF.Node node;
            if (parent == null)
            {
                node = scene.CreateNode(displayName);
            }
            else
            {
                node = parent.CreateNode(displayName);
            }

            var pos = frame.LocalPosition;
            var euler = frame.LocalRotation;
            var scl = frame.LocalScale;

            var quat = EulerToQuaternion(euler.X, euler.Y, euler.Z);

            node.LocalTransform = new SharpGLTF.Transforms.AffineTransform(
                new NVector3(scl.X, scl.Y, scl.Z),
                quat,
                new NVector3(pos.X * scaleFactor, pos.Y * scaleFactor, pos.Z * scaleFactor));

            nodeMap[frame.Path] = node;

            for (int i = 0; i < frame.Count; i++)
            {
                var child = frame[i];
                // Only include children that are in the required paths set (bones or bone ancestors)
                if (requiredPaths == null || requiredPaths.Contains(child.Path))
                {
                    CreateNodeHierarchy(scene, child, node, nodeMap, scaleFactor, requiredPaths);
                }
            }

            return node;
        }

        /// <summary>
        /// Armature / root object name shown in Blender. Appended to every Action
        /// (e.g. Idle_Knife_iw6_1P). Strip verbose MeleeWeapon_001_ prefixes.
        /// </summary>
        private static string ShortArmatureName(string rootName)
        {
            if (string.IsNullOrEmpty(rootName))
                return "Armature";
            const string prefix = "MeleeWeapon_001_";
            if (rootName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return rootName.Substring(prefix.Length);
            // Generic: drop leading Type_###_ if present
            var parts = rootName.Split('_');
            if (parts.Length >= 3 && parts[1].All(char.IsDigit))
                return string.Join("_", parts.Skip(2));
            return rootName;
        }

        private static NMatrix4x4 ConvertBindMatrix(Matrix4x4 m)
        {
            return new NMatrix4x4(
                m.M00, m.M01, m.M02, m.M03,
                m.M10, m.M11, m.M12, m.M13,
                m.M20, m.M21, m.M22, m.M23,
                m.M30, m.M31, m.M32, m.M33
            );
        }

        private static NQuaternion EulerToQuaternion(float xDeg, float yDeg, float zDeg)
        {
            const float Deg2Rad = (float)(Math.PI / 180.0);
            float x = xDeg * Deg2Rad;
            float y = yDeg * Deg2Rad;
            float z = zDeg * Deg2Rad;

            var qx = NQuaternion.CreateFromAxisAngle(NVector3.UnitX, x);
            var qy = NQuaternion.CreateFromAxisAngle(NVector3.UnitY, y);
            var qz = NQuaternion.CreateFromAxisAngle(NVector3.UnitZ, z);

            return NQuaternion.Normalize(qz * qy * qx);
        }

        private static void WriteFloat(byte[] buf, int offset, float value)
        {
            BitConverter.GetBytes(value).CopyTo(buf, offset);
        }

        private static void WriteUShort(byte[] buf, int offset, ushort value)
        {
            BitConverter.GetBytes(value).CopyTo(buf, offset);
        }

        private static void WriteUInt(byte[] buf, int offset, uint value)
        {
            BitConverter.GetBytes(value).CopyTo(buf, offset);
        }
    }
}
