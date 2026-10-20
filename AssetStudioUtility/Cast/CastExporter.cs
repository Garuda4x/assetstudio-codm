using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio.Cast;
using NMatrix4x4 = System.Numerics.Matrix4x4;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace AssetStudio
{
    /// <summary>
    /// Exports <see cref="IImported"/> models and animations to Cast (.cast).
    /// Opt in via GUI setting <c>exportCast</c>; independent of FBX/GLB.
    /// Layout: {name}.cast (model) + anims/{clip}.cast (one file per clip).
    ///
    /// Space notes:
    /// - IImported data is Y-up (Unity / FBX-style after ModelConverter flips).
    /// - Blender's Cast importer does NOT convert up-axis (unlike glTF), so we bake Z-up.
    /// - Cast meshes are not parented under hierarchy nodes; verts must be in model/armature space.
    /// </summary>
    public static class CastExporter
    {
        // +90° around X: (x, y, z) -> (x, -z, y)  — Y-up to Z-up
        private static readonly NQuaternion YupToZupRotation =
            NQuaternion.CreateFromAxisAngle(NVector3.UnitX, (float)(Math.PI / 2.0));

        public static void Export(string directory, string baseName, IImported imported, float scaleFactor = 1.0f)
        {
            if (imported?.RootFrame == null)
            {
                Logger.Warning("[Cast] No root frame, nothing to export");
                return;
            }

            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var safeBase = SanitizeFileName(baseName);
            var bones = BuildBoneIndex(imported.RootFrame, scaleFactor);
            ConvertSkeletonToZUp(bones);

            var worldByPath = BuildWorldMatrixLookup(imported.RootFrame, scaleFactor);

            var modelPath = Path.Combine(directory, safeBase + ".cast");
            WriteModel(modelPath, safeBase, imported, bones, worldByPath, scaleFactor);
            Logger.Info($"[Cast] Model -> {Path.GetFileName(modelPath)} ({bones.Count} bones, {imported.MeshList?.Count ?? 0} meshes)");

            if (imported.AnimationList == null || imported.AnimationList.Count == 0)
                return;

            var animDir = Path.Combine(directory, "anims");
            Directory.CreateDirectory(animDir);

            var rootBoneNames = new HashSet<string>(
                bones.Where(b => b.ParentIndex < 0).Select(b => b.Name),
                StringComparer.Ordinal);

            var written = 0;
            foreach (var anim in imported.AnimationList)
            {
                if (anim == null) continue;
                var animName = SanitizeFileName(string.IsNullOrEmpty(anim.Name) ? $"anim_{written}" : anim.Name);
                var animPath = Path.Combine(animDir, animName + ".cast");
                WriteAnimation(animPath, anim, bones, rootBoneNames, scaleFactor);
                written++;
            }

            Logger.Info($"[Cast] Animations -> {written} file(s) in anims/");
        }

        private static void WriteModel(string path, string name, IImported imported,
            List<BoneInfo> bones, Dictionary<string, NMatrix4x4> worldByPath, float scaleFactor)
        {
            var root = new CastNode(CastIds.Root);
            var meta = root.AddChild(CastIds.Metadata);
            meta.SetString("s", "AssetStudio");
            meta.SetString("up", "z");

            var model = root.AddChild(CastIds.Model);
            model.SetString("n", name);

            AddSkeleton(model, bones);

            var materialHashes = new Dictionary<string, ulong>(StringComparer.Ordinal);
            if (imported.MaterialList != null)
            {
                foreach (var mat in imported.MaterialList)
                {
                    if (mat == null || string.IsNullOrEmpty(mat.Name)) continue;
                    var matNode = model.AddChild(CastIds.Material);
                    matNode.SetString("n", mat.Name);
                    matNode.SetString("t", "pbr");
                    materialHashes[mat.Name] = matNode.Hash;
                    TryAttachDiffuseTexture(matNode, mat, imported, Path.GetDirectoryName(path));
                }
            }

            if (imported.MeshList != null)
            {
                foreach (var mesh in imported.MeshList)
                    AddMesh(model, mesh, bones, materialHashes, worldByPath, scaleFactor);
            }

            CastFile.Save(path, root);
        }

        private static void WriteAnimation(string path, ImportedKeyframedAnimation anim,
            List<BoneInfo> bones, HashSet<string> rootBoneNames, float scaleFactor)
        {
            var root = new CastNode(CastIds.Root);
            var meta = root.AddChild(CastIds.Metadata);
            meta.SetString("s", "AssetStudio");
            meta.SetString("up", "z");

            var animation = root.AddChild(CastIds.Animation);
            animation.SetString("n", anim.Name ?? "Animation");
            var fps = anim.SampleRate > 0.01f ? anim.SampleRate : 30f;
            animation.SetFloat("fr", fps);

            AddSkeleton(animation, bones);

            var curvesWritten = 0;
            var skippedNoBone = 0;
            var skippedNullPath = 0;
            if (anim.TrackList != null)
            {
                foreach (var track in anim.TrackList)
                {
                    if (track == null) continue;
                    var boneName = BoneLeafName(track.Path);
                    if (string.IsNullOrEmpty(boneName))
                    {
                        skippedNullPath++;
                        continue;
                    }
                    if (!bones.Any(b => string.Equals(b.Name, boneName, StringComparison.Ordinal)))
                        skippedNoBone++;

                    var isRoot = rootBoneNames.Contains(boneName);
                    var before = animation.Children.Count;
                    WriteTranslationCurves(animation, boneName, track.Translations, fps, scaleFactor, isRoot);
                    WriteRotationCurve(animation, boneName, track.Rotations, fps, isRoot);
                    WriteScaleCurves(animation, boneName, track.Scalings, fps);
                    curvesWritten += animation.Children.Count - before;
                }
            }

            WriteNotifications(animation, anim, fps);
            CastFile.Save(path, root);

            if (curvesWritten == 0)
            {
                Logger.Warning(
                    $"[Cast] '{anim.Name}' wrote 0 curves (skeleton-only ~dud). " +
                    $"tracks={anim.TrackList?.Count ?? 0}, nullPath={skippedNullPath}, " +
                    $"leafNotInSkeleton={skippedNoBone}, bones={bones.Count}");
            }
        }

        private static void AddSkeleton(CastNode parent, List<BoneInfo> bones)
        {
            var skeleton = parent.AddChild(CastIds.Skeleton);
            foreach (var bone in bones)
            {
                var node = skeleton.AddChild(CastIds.Bone);
                node.SetString("n", bone.Name);
                var parentIndex = bone.ParentIndex < 0 ? unchecked((int)0xFFFFFFFFu) : bone.ParentIndex;
                node.SetInt32("p", parentIndex);

                node.SetVec3("lp", bone.LocalPosition.X, bone.LocalPosition.Y, bone.LocalPosition.Z);
                node.SetVec4("lr", bone.LocalRotation.X, bone.LocalRotation.Y, bone.LocalRotation.Z, bone.LocalRotation.W);
                node.SetVec3("s", bone.LocalScale.X, bone.LocalScale.Y, bone.LocalScale.Z);

                node.SetVec3("wp", bone.WorldPosition.X, bone.WorldPosition.Y, bone.WorldPosition.Z);
                node.SetVec4("wr", bone.WorldRotation.X, bone.WorldRotation.Y, bone.WorldRotation.Z, bone.WorldRotation.W);
            }
        }

        private static void AddMesh(CastNode model, ImportedMesh mesh, List<BoneInfo> bones,
            Dictionary<string, ulong> materialHashes, Dictionary<string, NMatrix4x4> worldByPath,
            float scaleFactor)
        {
            if (mesh?.VertexList == null || mesh.VertexList.Count == 0)
                return;

            // Bake mesh node transform into verts — Cast has no per-mesh hierarchy parent.
            // worldByPath is still Y-up; we convert positions to Z-up after transform.
            var meshWorldY = NMatrix4x4.Identity;
            if (!string.IsNullOrEmpty(mesh.Path) && worldByPath.TryGetValue(mesh.Path, out var mw))
                meshWorldY = mw;

            var submeshes = mesh.SubmeshList;
            if (submeshes == null || submeshes.Count == 0)
            {
                submeshes = new List<ImportedSubmesh>
                {
                    new ImportedSubmesh { FaceList = new List<ImportedFace>(), BaseVertex = 0 }
                };
            }

            var pathToBone = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < bones.Count; i++)
            {
                if (!string.IsNullOrEmpty(bones[i].Path))
                    pathToBone[bones[i].Path] = i;
            }

            int[] skinToSkeleton = null;
            if (mesh.BoneList != null && mesh.BoneList.Count > 0)
            {
                skinToSkeleton = new int[mesh.BoneList.Count];
                for (int i = 0; i < mesh.BoneList.Count; i++)
                {
                    var bp = mesh.BoneList[i].Path;
                    skinToSkeleton[i] = bp != null && pathToBone.TryGetValue(bp, out var si) ? si : 0;
                }
            }

            for (int s = 0; s < submeshes.Count; s++)
            {
                var sub = submeshes[s];
                var meshNode = model.AddChild(CastIds.Mesh);
                var meshName = BoneLeafName(mesh.Path);
                if (submeshes.Count > 1)
                    meshName = $"{meshName}_{s}";
                meshNode.SetString("n", string.IsNullOrEmpty(meshName) ? $"mesh_{s}" : meshName);

                if (sub.Material != null && materialHashes.TryGetValue(sub.Material, out var matHash))
                    meshNode.SetInt64("m", matHash);

                var used = new HashSet<int>();
                if (sub.FaceList != null)
                {
                    foreach (var face in sub.FaceList)
                    {
                        if (face?.VertexIndices == null || face.VertexIndices.Length < 3) continue;
                        used.Add(face.VertexIndices[0] + sub.BaseVertex);
                        used.Add(face.VertexIndices[1] + sub.BaseVertex);
                        used.Add(face.VertexIndices[2] + sub.BaseVertex);
                    }
                }

                if (used.Count == 0)
                {
                    for (int i = 0; i < mesh.VertexList.Count; i++)
                        used.Add(i);
                }

                var oldToNew = new Dictionary<int, int>();
                var positions = new List<float>();
                var normals = new List<float>();
                var uvs = new List<float>();
                var weightBones = new List<uint>();
                var weightValues = new List<float>();
                var hasUv = mesh.hasUV != null && mesh.hasUV.Length > 0 && mesh.hasUV[0];
                var hasSkin = skinToSkeleton != null;

                foreach (var vi in used.OrderBy(x => x))
                {
                    if (vi < 0 || vi >= mesh.VertexList.Count) continue;
                    oldToNew[vi] = oldToNew.Count;
                    var v = mesh.VertexList[vi];

                    var local = new NVector3(v.Vertex.X * scaleFactor, v.Vertex.Y * scaleFactor, v.Vertex.Z * scaleFactor);
                    var worldY = NVector3.Transform(local, meshWorldY);
                    var worldZ = YupToZup(worldY);
                    positions.Add(worldZ.X);
                    positions.Add(worldZ.Y);
                    positions.Add(worldZ.Z);

                    if (mesh.hasNormal)
                    {
                        var nLocal = new NVector3(v.Normal.X, v.Normal.Y, v.Normal.Z);
                        var nWorldY = NVector3.TransformNormal(nLocal, meshWorldY);
                        var nWorldZ = NVector3.Normalize(YupToZup(nWorldY));
                        normals.Add(nWorldZ.X);
                        normals.Add(nWorldZ.Y);
                        normals.Add(nWorldZ.Z);
                    }

                    if (hasUv && v.UV != null && v.UV.Length > 0 && v.UV[0] != null)
                    {
                        // Match Blender Cast export (V flipped in file; importer flips again)
                        uvs.Add(v.UV[0][0]);
                        uvs.Add(1f - v.UV[0][1]);
                    }
                    else if (hasUv)
                    {
                        uvs.Add(0f);
                        uvs.Add(0f);
                    }

                    if (hasSkin)
                    {
                        for (int w = 0; w < 4; w++)
                        {
                            var bi = (v.BoneIndices != null && w < v.BoneIndices.Length) ? v.BoneIndices[w] : 0;
                            var bw = (v.Weights != null && w < v.Weights.Length) ? v.Weights[w] : (w == 0 ? 1f : 0f);
                            if (bi < 0 || bi >= skinToSkeleton.Length) bi = 0;
                            weightBones.Add((uint)skinToSkeleton[bi]);
                            weightValues.Add(bw);
                        }
                    }
                }

                meshNode.SetVec3Array("vp", positions);
                if (normals.Count == positions.Count)
                    meshNode.SetVec3Array("vn", normals);
                if (hasUv && uvs.Count > 0)
                {
                    meshNode.SetByte("ul", 1);
                    meshNode.SetVec2Array("u0", uvs);
                }

                if (hasSkin && weightBones.Count > 0)
                {
                    meshNode.SetByte("mi", 4);
                    meshNode.SetInt32Array("wb", weightBones);
                    meshNode.SetFloatArray("wv", weightValues);
                }

                var faces = new List<uint>();
                if (sub.FaceList != null)
                {
                    foreach (var face in sub.FaceList)
                    {
                        if (face?.VertexIndices == null || face.VertexIndices.Length < 3) continue;
                        var a = face.VertexIndices[0] + sub.BaseVertex;
                        var b = face.VertexIndices[1] + sub.BaseVertex;
                        var c = face.VertexIndices[2] + sub.BaseVertex;
                        if (!oldToNew.TryGetValue(a, out var na) ||
                            !oldToNew.TryGetValue(b, out var nb) ||
                            !oldToNew.TryGetValue(c, out var nc))
                            continue;
                        faces.Add((uint)na);
                        faces.Add((uint)nb);
                        faces.Add((uint)nc);
                    }
                }

                if (faces.Count > 0)
                    meshNode.SetInt32Array("f", faces);
            }
        }

        private static void TryAttachDiffuseTexture(CastNode matNode, ImportedMaterial mat,
            IImported imported, string directory)
        {
            if (mat.Textures == null || imported.TextureList == null || string.IsNullOrEmpty(directory))
                return;

            var diffuse = mat.Textures.FirstOrDefault(t => t.Dest == 0);
            if (diffuse == null) return;

            var tex = imported.TextureList.FirstOrDefault(t => t.Name == diffuse.Name);
            if (tex?.Data == null || tex.Data.Length == 0) return;

            try
            {
                var texDir = Path.Combine(directory, "_cast_textures");
                Directory.CreateDirectory(texDir);
                var fileName = SanitizeFileName(tex.Name);
                if (!Path.HasExtension(fileName))
                    fileName += ".png";
                File.WriteAllBytes(Path.Combine(texDir, fileName), tex.Data);

                var fileNode = matNode.AddChild(CastIds.File);
                fileNode.SetString("p", Path.Combine("_cast_textures", fileName).Replace('\\', '/'));
                matNode.SetInt64("diffuse", fileNode.Hash);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[Cast] Failed to write texture '{tex.Name}': {ex.Message}");
            }
        }

        private static void WriteTranslationCurves(CastNode animation, string boneName,
            List<ImportedKeyframe<Vector3>> keys, float fps, float scaleFactor, bool isRoot)
        {
            if (keys == null || keys.Count == 0) return;

            Func<Vector3, float> pickX = v =>
            {
                var t = new NVector3(v.X * scaleFactor, v.Y * scaleFactor, v.Z * scaleFactor);
                if (isRoot) t = YupToZup(t);
                return t.X;
            };
            Func<Vector3, float> pickY = v =>
            {
                var t = new NVector3(v.X * scaleFactor, v.Y * scaleFactor, v.Z * scaleFactor);
                if (isRoot) t = YupToZup(t);
                return t.Y;
            };
            Func<Vector3, float> pickZ = v =>
            {
                var t = new NVector3(v.X * scaleFactor, v.Y * scaleFactor, v.Z * scaleFactor);
                if (isRoot) t = YupToZup(t);
                return t.Z;
            };

            WriteFloatCurve(animation, boneName, "tx", keys, fps, pickX);
            WriteFloatCurve(animation, boneName, "ty", keys, fps, pickY);
            WriteFloatCurve(animation, boneName, "tz", keys, fps, pickZ);
        }

        private static void WriteScaleCurves(CastNode animation, string boneName,
            List<ImportedKeyframe<Vector3>> keys, float fps)
        {
            if (keys == null || keys.Count == 0) return;
            WriteFloatCurve(animation, boneName, "sx", keys, fps, v => v.X);
            WriteFloatCurve(animation, boneName, "sy", keys, fps, v => v.Y);
            WriteFloatCurve(animation, boneName, "sz", keys, fps, v => v.Z);
        }

        private static void WriteFloatCurve(CastNode animation, string boneName, string prop,
            List<ImportedKeyframe<Vector3>> keys, float fps, Func<Vector3, float> pick)
        {
            var frames = new List<uint>();
            var values = new List<float>();
            var seen = new HashSet<uint>();
            foreach (var kf in keys.OrderBy(k => k.time))
            {
                var frame = (uint)Math.Max(0, Math.Round(kf.time * fps));
                if (!seen.Add(frame))
                {
                    values[values.Count - 1] = pick(kf.value);
                    continue;
                }
                frames.Add(frame);
                values.Add(pick(kf.value));
            }

            if (frames.Count == 0) return;
            var curve = animation.AddChild(CastIds.Curve);
            curve.SetString("nn", boneName);
            curve.SetString("kp", prop);
            curve.SetInt32Array("kb", frames);
            curve.SetFloatArray("kv", values);
            curve.SetString("m", "absolute");
        }

        private static void WriteRotationCurve(CastNode animation, string boneName,
            List<ImportedKeyframe<Vector3>> keys, float fps, bool isRoot)
        {
            if (keys == null || keys.Count == 0) return;

            var frames = new List<uint>();
            var values = new List<float>();
            var seen = new HashSet<uint>();
            foreach (var kf in keys.OrderBy(k => k.time))
            {
                var frame = (uint)Math.Max(0, Math.Round(kf.time * fps));
                var q = EulerToQuaternion(kf.value.X, kf.value.Y, kf.value.Z);
                if (isRoot)
                    q = NQuaternion.Normalize(YupToZupRotation * q);

                if (!seen.Add(frame))
                {
                    var i = values.Count - 4;
                    values[i] = q.X;
                    values[i + 1] = q.Y;
                    values[i + 2] = q.Z;
                    values[i + 3] = q.W;
                    continue;
                }
                frames.Add(frame);
                values.Add(q.X);
                values.Add(q.Y);
                values.Add(q.Z);
                values.Add(q.W);
            }

            if (frames.Count == 0) return;
            var curve = animation.AddChild(CastIds.Curve);
            curve.SetString("nn", boneName);
            curve.SetString("kp", "rq");
            curve.SetInt32Array("kb", frames);
            curve.SetVec4Array("kv", values);
            curve.SetString("m", "absolute");
        }

        private static void WriteNotifications(CastNode animation, ImportedKeyframedAnimation anim, float fps)
        {
            if (anim.Events == null || anim.Events.Count == 0)
                return;

            var groups = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
            foreach (var ev in anim.Events)
            {
                var name = string.IsNullOrEmpty(ev.Name) ? "event" : ev.Name;
                if (!groups.TryGetValue(name, out var frames))
                {
                    frames = new List<uint>();
                    groups[name] = frames;
                }
                frames.Add((uint)Math.Max(0, Math.Round(ev.time * fps)));
            }

            foreach (var kv in groups)
            {
                var track = animation.AddChild(CastIds.NotificationTrack);
                track.SetString("n", kv.Key);
                track.SetInt32Array("kb", kv.Value.Distinct().OrderBy(f => f).ToList());
            }
        }

        private static List<BoneInfo> BuildBoneIndex(ImportedFrame root, float scaleFactor)
        {
            var list = new List<BoneInfo>();
            void Walk(ImportedFrame frame, int parentIndex)
            {
                if (frame == null) return;
                var index = list.Count;
                var q = EulerToQuaternion(frame.LocalRotation.X, frame.LocalRotation.Y, frame.LocalRotation.Z);
                list.Add(new BoneInfo
                {
                    Name = frame.Name ?? $"bone_{index}",
                    Path = frame.Path,
                    ParentIndex = parentIndex,
                    LocalPosition = new NVector3(
                        frame.LocalPosition.X * scaleFactor,
                        frame.LocalPosition.Y * scaleFactor,
                        frame.LocalPosition.Z * scaleFactor),
                    LocalRotation = q,
                    LocalScale = new NVector3(frame.LocalScale.X, frame.LocalScale.Y, frame.LocalScale.Z)
                });
                for (int i = 0; i < frame.Count; i++)
                    Walk(frame[i], index);
            }
            Walk(root, -1);
            return list;
        }

        /// <summary>
        /// Convert hierarchy from Y-up to Z-up by rewriting root locals and recomputing world/local.
        /// Child local anims stay valid; only root TRS keys need the same conversion.
        /// </summary>
        private static void ConvertSkeletonToZUp(List<BoneInfo> bones)
        {
            if (bones.Count == 0) return;

            // First pass: apply scale to locals, compute Y-up worlds
            for (int i = 0; i < bones.Count; i++)
            {
                var b = bones[i];
                NMatrix4x4 parentWorld = NMatrix4x4.Identity;
                if (b.ParentIndex >= 0)
                    parentWorld = bones[b.ParentIndex].WorldMatrixY;

                var local = NMatrix4x4.CreateScale(b.LocalScale)
                            * NMatrix4x4.CreateFromQuaternion(b.LocalRotation)
                            * NMatrix4x4.CreateTranslation(b.LocalPosition);
                b.WorldMatrixY = local * parentWorld;
                bones[i] = b;
            }

            // Convert root local into Z-up; keep child locals (relative) unchanged
            for (int i = 0; i < bones.Count; i++)
            {
                var b = bones[i];
                if (b.ParentIndex < 0)
                {
                    b.LocalPosition = YupToZup(b.LocalPosition);
                    b.LocalRotation = NQuaternion.Normalize(YupToZupRotation * b.LocalRotation);
                    bones[i] = b;
                }
            }

            // Recompute Z-up worlds and write wp/wr; also bake scale into lp for Cast
            for (int i = 0; i < bones.Count; i++)
            {
                var b = bones[i];
                NMatrix4x4 parentWorld = NMatrix4x4.Identity;
                if (b.ParentIndex >= 0)
                    parentWorld = bones[b.ParentIndex].WorldMatrixZ;

                var local = NMatrix4x4.CreateScale(b.LocalScale)
                            * NMatrix4x4.CreateFromQuaternion(b.LocalRotation)
                            * NMatrix4x4.CreateTranslation(b.LocalPosition);
                b.WorldMatrixZ = local * parentWorld;

                NMatrix4x4.Decompose(b.WorldMatrixZ, out _, out var worldRot, out var worldPos);
                b.WorldPosition = worldPos;
                b.WorldRotation = worldRot;
                bones[i] = b;
            }
        }

        private static Dictionary<string, NMatrix4x4> BuildWorldMatrixLookup(ImportedFrame root, float scaleFactor)
        {
            var map = new Dictionary<string, NMatrix4x4>(StringComparer.Ordinal);
            void Walk(ImportedFrame frame, NMatrix4x4 parentWorld)
            {
                if (frame == null) return;
                var q = EulerToQuaternion(frame.LocalRotation.X, frame.LocalRotation.Y, frame.LocalRotation.Z);
                var lp = new NVector3(
                    frame.LocalPosition.X * scaleFactor,
                    frame.LocalPosition.Y * scaleFactor,
                    frame.LocalPosition.Z * scaleFactor);
                var ls = new NVector3(frame.LocalScale.X, frame.LocalScale.Y, frame.LocalScale.Z);
                var local = NMatrix4x4.CreateScale(ls)
                            * NMatrix4x4.CreateFromQuaternion(q)
                            * NMatrix4x4.CreateTranslation(lp);
                var world = local * parentWorld;
                if (!string.IsNullOrEmpty(frame.Path))
                    map[frame.Path] = world;
                for (int i = 0; i < frame.Count; i++)
                    Walk(frame[i], world);
            }
            Walk(root, NMatrix4x4.Identity);
            return map;
        }

        private static NVector3 YupToZup(NVector3 v) => new NVector3(v.X, -v.Z, v.Y);

        private static string BoneLeafName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var i = path.LastIndexOf('/');
            return i >= 0 ? path.Substring(i + 1) : path;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        private static NQuaternion EulerToQuaternion(float xDeg, float yDeg, float zDeg)
        {
            const float Deg2Rad = (float)(Math.PI / 180.0);
            var qx = NQuaternion.CreateFromAxisAngle(NVector3.UnitX, xDeg * Deg2Rad);
            var qy = NQuaternion.CreateFromAxisAngle(NVector3.UnitY, yDeg * Deg2Rad);
            var qz = NQuaternion.CreateFromAxisAngle(NVector3.UnitZ, zDeg * Deg2Rad);
            return NQuaternion.Normalize(qz * qy * qx);
        }

        private struct BoneInfo
        {
            public string Name;
            public string Path;
            public int ParentIndex;
            public NVector3 LocalPosition;
            public NQuaternion LocalRotation;
            public NVector3 LocalScale;
            public NVector3 WorldPosition;
            public NQuaternion WorldRotation;
            public NMatrix4x4 WorldMatrixY;
            public NMatrix4x4 WorldMatrixZ;
        }
    }
}
