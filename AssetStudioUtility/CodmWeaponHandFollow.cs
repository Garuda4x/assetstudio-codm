using System;
using System.Collections.Generic;
using System.Linq;
using NMatrix4x4 = System.Numerics.Matrix4x4;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace AssetStudio
{
    /// <summary>
    /// CODM 1P knife: mesh hangs under Bip01/Bone_RightHand/Bone01 while the skinned
    /// hand is b_RightHand on the arm chain. Fire* authored Bone01 so the blade tracks
    /// the hand; quantized clips under-drive Bone01 so the knife drifts. After bake,
    /// re-anchor Bone01 so World(Bone01,t) = World(b_RightHand,t) * restOffset
    /// where restOffset = inv(World(hand,0)) * World(Bone01,0).
    /// </summary>
    public static class CodmWeaponHandFollow
    {
        /// <summary>
        /// Post-process constraint (NOT in clip bytes). Off by default — only enable for experiments.
        /// When on, rewrites Bone01 to track b_RightHand using bind-relative offset.
        /// </summary>
        public static bool Enabled = false;

        /// <summary>
        /// [INVENTED] When <see cref="Enabled"/>, only rewrite Bone01 translation so the bone
        /// origin tracks hand*restOffset; keep dense Bone01 rotation untouched.
        /// Prior full follow overwrote R and was rejected — R is already good.
        /// Default false.
        /// </summary>
        public static bool PositionOnly = false;

        public static void Apply(ImportedFrame root, ImportedKeyframedAnimation anim)
        {
            if (!Enabled || root == null || anim?.TrackList == null)
                return;
            if (anim.Name != null &&
                (anim.Name.IndexOf("FireLeft", StringComparison.OrdinalIgnoreCase) >= 0
                 || anim.Name.IndexOf("FireRight", StringComparison.OrdinalIgnoreCase) >= 0
                 || anim.Name.IndexOf("FireJump", StringComparison.OrdinalIgnoreCase) >= 0
                 || anim.Name.IndexOf("Fire_", StringComparison.OrdinalIgnoreCase) >= 0))
                return;

            var handTrack = FindByLeaf(anim, "b_RightHand");
            var bone01Track = FindByLeaf(anim, "Bone01");
            if (handTrack == null || bone01Track == null)
                return;
            // Only when Bone01 was densely keyed (CODM path)
            int boneKeys = (bone01Track.Translations?.Count ?? 0) + (bone01Track.Rotations?.Count ?? 0);
            if (boneKeys < 8)
                return;

            var handFrame = FindFrame(root, handTrack.Path) ?? FindFrameByLeaf(root, "b_RightHand");
            var bone01Frame = FindFrame(root, bone01Track.Path) ?? FindFrameByLeaf(root, "Bone01");
            if (handFrame == null || bone01Frame?.Parent == null)
                return;

            var times = new SortedSet<float>();
            AddTimes(times, handTrack);
            AddTimes(times, bone01Track);
            // Also sample arm parents so FK is complete
            foreach (var tr in anim.TrackList)
            {
                var leaf = Leaf(tr.Path);
                if (leaf.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0
                    || leaf.IndexOf("Spine", StringComparison.OrdinalIgnoreCase) >= 0
                    || leaf.IndexOf("Bip01", StringComparison.OrdinalIgnoreCase) >= 0
                    || leaf.IndexOf("Hips", StringComparison.OrdinalIgnoreCase) >= 0
                    || leaf.IndexOf("Bone", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddTimes(times, tr);
            }
            if (times.Count == 0)
                return;

            // Rest offset from clip t≈0 (IdleRelativeEnd makes hold-clip ends = Fire* rest).
            float t0 = times.Min;
            var locals0 = SampleLocals(root, anim, t0);
            if (!TryWorld(handFrame, locals0, out var wHand0) || !TryWorld(bone01Frame, locals0, out var wBone0))
                return;
            if (!NMatrix4x4.Invert(wHand0, out var invHand0))
                return;
            var restOffset = invHand0 * wBone0;

            var newPos = new List<ImportedKeyframe<Vector3>>();
            var newRot = PositionOnly ? null : new List<ImportedKeyframe<Vector3>>();
            int fixedCount = 0;
            foreach (var t in times)
            {
                var locals = SampleLocals(root, anim, t);
                if (!TryWorld(handFrame, locals, out var wHand))
                    continue;
                var wDesired = wHand * restOffset;
                if (!TryWorld(bone01Frame.Parent, locals, out var wParent))
                    continue;
                if (!NMatrix4x4.Invert(wParent, out var invParent))
                    continue;

                if (PositionOnly)
                {
                    // Keep dense R; solve P so bone origin matches desired origin.
                    // world_origin = parent_origin + parent_rot * P  =>  P = inv(parent_rot)*(desired - parent_origin)
                    if (!NMatrix4x4.Decompose(wParent, out _, out var parentQ, out var parentPos))
                        continue;
                    if (!NMatrix4x4.Decompose(wDesired, out _, out _, out var desiredPos))
                        continue;
                    var invParentQ = NQuaternion.Conjugate(NQuaternion.Normalize(parentQ));
                    var delta = desiredPos - parentPos;
                    var localP = NVector3.Transform(delta, invParentQ);
                    newPos.Add(new ImportedKeyframe<Vector3>(t, new Vector3(localP.X, localP.Y, localP.Z)));
                }
                else
                {
                    var localMat = invParent * wDesired;
                    if (!NMatrix4x4.Decompose(localMat, out _, out var q, out var p))
                        continue;
                    var euler = Fbx.QuaternionToEuler(new Quaternion(q.X, q.Y, q.Z, q.W));
                    newPos.Add(new ImportedKeyframe<Vector3>(t, new Vector3(p.X, p.Y, p.Z)));
                    newRot.Add(new ImportedKeyframe<Vector3>(t, euler));
                }
                fixedCount++;
            }

            if (fixedCount < 2)
                return;

            bone01Track.Translations = newPos;
            if (!PositionOnly)
                bone01Track.Rotations = newRot;
            CodmDiagnostics.Warn(
                $"[AnimExport] CODM weapon-hand follow on '{anim.Name}': " +
                $"{(PositionOnly ? "P-only " : "")}retargeted Bone01 to b_RightHand ({fixedCount} keys)");
        }

        private static void AddTimes(SortedSet<float> times, ImportedAnimationKeyframedTrack tr)
        {
            if (tr.Translations != null)
                foreach (var k in tr.Translations) times.Add(k.time);
            if (tr.Rotations != null)
                foreach (var k in tr.Rotations) times.Add(k.time);
        }

        private static Dictionary<ImportedFrame, (Vector3 pos, Vector3 euler, Vector3 scale)> BindLocals(ImportedFrame root)
        {
            var map = new Dictionary<ImportedFrame, (Vector3, Vector3, Vector3)>();
            void Walk(ImportedFrame f)
            {
                map[f] = (f.LocalPosition, f.LocalRotation, f.LocalScale);
                for (int i = 0; i < f.Count; i++)
                    Walk(f[i]);
            }
            Walk(root);
            return map;
        }

        private static Dictionary<ImportedFrame, (Vector3 pos, Vector3 euler, Vector3 scale)> SampleLocals(
            ImportedFrame root, ImportedKeyframedAnimation anim, float time)
        {
            var map = new Dictionary<ImportedFrame, (Vector3, Vector3, Vector3)>();
            void Walk(ImportedFrame f)
            {
                map[f] = (f.LocalPosition, f.LocalRotation, f.LocalScale);
                for (int i = 0; i < f.Count; i++)
                    Walk(f[i]);
            }
            Walk(root);

            foreach (var tr in anim.TrackList)
            {
                var frame = FindFrame(root, tr.Path);
                if (frame == null || !map.ContainsKey(frame)) continue;
                var (pos, euler, scale) = map[frame];
                if (tr.Translations != null && tr.Translations.Count > 0)
                    pos = SampleVec3(tr.Translations, time);
                if (tr.Rotations != null && tr.Rotations.Count > 0)
                    euler = SampleVec3(tr.Rotations, time);
                if (tr.Scalings != null && tr.Scalings.Count > 0)
                    scale = SampleVec3(tr.Scalings, time);
                map[frame] = (pos, euler, scale);
            }
            return map;
        }

        private static Vector3 SampleVec3(List<ImportedKeyframe<Vector3>> keys, float time)
        {
            if (keys.Count == 1) return keys[0].value;
            if (time <= keys[0].time) return keys[0].value;
            if (time >= keys[keys.Count - 1].time) return keys[keys.Count - 1].value;
            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (time >= keys[i].time && time <= keys[i + 1].time)
                {
                    float u = keys[i + 1].time - keys[i].time;
                    float a = u > 1e-8f ? (time - keys[i].time) / u : 0f;
                    var A = keys[i].value;
                    var B = keys[i + 1].value;
                    return new Vector3(
                        A.X + (B.X - A.X) * a,
                        A.Y + (B.Y - A.Y) * a,
                        A.Z + (B.Z - A.Z) * a);
                }
            }
            return keys[keys.Count - 1].value;
        }

        private static bool TryWorld(
            ImportedFrame frame,
            Dictionary<ImportedFrame, (Vector3 pos, Vector3 euler, Vector3 scale)> locals,
            out NMatrix4x4 world)
        {
            world = NMatrix4x4.Identity;
            if (frame == null) return false;
            var stack = new Stack<ImportedFrame>();
            for (var f = frame; f != null; f = f.Parent)
                stack.Push(f);
            while (stack.Count > 0)
            {
                var f = stack.Pop();
                if (!locals.TryGetValue(f, out var trs))
                    trs = (f.LocalPosition, f.LocalRotation, f.LocalScale);
                var q = Fbx.EulerToQuaternion(trs.euler);
                var nq = new NQuaternion(q.X, q.Y, q.Z, q.W);
                var s = new NVector3(
                    trs.scale.X == 0 ? 1 : trs.scale.X,
                    trs.scale.Y == 0 ? 1 : trs.scale.Y,
                    trs.scale.Z == 0 ? 1 : trs.scale.Z);
                // Column-vector TRS: world = parent * local
                var local = NMatrix4x4.CreateTranslation(new NVector3(trs.pos.X, trs.pos.Y, trs.pos.Z))
                            * NMatrix4x4.CreateFromQuaternion(nq)
                            * NMatrix4x4.CreateScale(s);
                world = world * local;
            }
            return true;
        }

        private static ImportedAnimationKeyframedTrack FindByLeaf(ImportedKeyframedAnimation anim, string leaf)
        {
            return anim.TrackList.FirstOrDefault(t => Leaf(t.Path).Equals(leaf, StringComparison.OrdinalIgnoreCase));
        }

        private static ImportedFrame FindFrame(ImportedFrame root, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return root.FindFrameByPath(path) ?? root.FindFrameByPath(path.Replace('\\', '/'));
        }

        private static ImportedFrame FindFrameByLeaf(ImportedFrame root, string leaf)
        {
            foreach (var f in Enumerate(root))
                if (f.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase))
                    return f;
            return null;
        }

        private static IEnumerable<ImportedFrame> Enumerate(ImportedFrame f)
        {
            yield return f;
            for (int i = 0; i < f.Count; i++)
                foreach (var c in Enumerate(f[i]))
                    yield return c;
        }

        private static string Leaf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int i = path.LastIndexOf('/');
            return i >= 0 ? path.Substring(i + 1) : path;
        }
    }
}
