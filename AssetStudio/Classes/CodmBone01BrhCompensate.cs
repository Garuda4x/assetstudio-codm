using System;

namespace AssetStudio
{
    /// <summary>
    /// Karambit (and similar) skins keep Bone_RightHand static with a non-identity rest
    /// (90° Z) while Equip/Inspect Bone01 curves are knife-space (authored under BRH=I).
    /// These diagnostics rewrite Bone01 and/or BRH rest so world weapon matches knife.
    /// </summary>
    public static class CodmBone01BrhCompensate
    {
        /// <summary>
        /// After IdleRelative bake: Bone01.R' = conj(BRH.R)*R, Bone01.P' = conj(BRH.R)*P.
        /// Makes world(BRH*Bone01) match knife where BRH=I. No-op if BRH≈I.
        /// </summary>
        public static bool CompensateBone01 = false;

        /// <summary>
        /// Replace Bone_RightHand hierarchy rest with knife bind (I + knife P).
        /// Mesh was skinned for kar BRH — may look rotated; tests hole vs index parenting.
        /// </summary>
        public static bool ForceBrhToKnifeBind = false;

        public static Quaternion BrhRestR = new Quaternion(0, 0, 0, 1);
        public static Vector3 BrhRestP = Vector3.Zero;

        /// <summary>Knife Bone_RightHand bind (from MeleeWeapon_001_Knife_iw6_1P).</summary>
        public static readonly Vector3 KnifeBrhP = new Vector3(-0.336269f, -0.121643f, 0.385742f);
        public static readonly Quaternion KnifeBrhR = new Quaternion(0, 0, 0, 1);

        public static void Reset()
        {
            CompensateBone01 = false;
            ForceBrhToKnifeBind = false;
            BrhRestR = new Quaternion(0, 0, 0, 1);
            BrhRestP = Vector3.Zero;
        }

        public static bool IsBrhNearIdentity(Quaternion r, float eps = 1e-3f)
        {
            // identity or antipodal
            float d = Math.Abs(r.W);
            return d > 1f - eps && Math.Abs(r.X) < eps && Math.Abs(r.Y) < eps && Math.Abs(r.Z) < eps;
        }

        /// <summary>Mutate Bone01 dense P+R in-place (call after Bake).</summary>
        public static bool ApplyToClip(AnimationClip clip, Func<uint, string> resolvePath)
        {
            if (!CompensateBone01 || clip == null || resolvePath == null)
                return false;
            if (IsBrhNearIdentity(BrhRestR))
                return false;
            if (!CodmDenseIdleRelative.IsCodmHydratedDense(clip))
                return false;

            var dense = clip.m_MuscleClip.m_Clip.m_DenseClip;
            var binds = clip.m_ClipBindingConstant?.genericBindings;
            if (binds == null || dense?.m_SampleArray == null)
                return false;

            int stream = (int)(clip.m_MuscleClip.m_Clip.m_StreamedClip?.curveCount ?? 0);
            int ch = (int)dense.m_CurveCount;
            int frames = dense.m_FrameCount;
            var s = dense.m_SampleArray;

            int pLoc = -1, rLoc = -1;
            int curve = 0;
            foreach (var b in binds)
            {
                int dim = b.attribute == 2 ? 4 : 3;
                if (b.typeID == ClassIDType.Transform && curve >= stream && curve < stream + ch)
                {
                    string leaf = Leaf(resolvePath(b.path));
                    if (leaf == "Bone01")
                    {
                        if (b.attribute == 1) pLoc = curve - stream;
                        if (b.attribute == 2) rLoc = curve - stream;
                    }
                }
                curve += dim;
            }
            if (pLoc < 0 && rLoc < 0)
                return false;

            var invBrh = Conjugate(Normalize(BrhRestR));
            for (int f = 0; f < frames; f++)
            {
                if (rLoc >= 0)
                {
                    int o = f * ch + rLoc;
                    var q = Normalize(new Quaternion(s[o], s[o + 1], s[o + 2], s[o + 3]));
                    var qOut = Normalize(Mul(invBrh, q));
                    s[o] = qOut.X; s[o + 1] = qOut.Y; s[o + 2] = qOut.Z; s[o + 3] = qOut.W;
                }
                if (pLoc >= 0)
                {
                    int o = f * ch + pLoc;
                    Rotate(invBrh, s[o], s[o + 1], s[o + 2], out float x, out float y, out float z);
                    s[o] = x; s[o + 1] = y; s[o + 2] = z;
                }
            }

            Logger.Info(
                $"[AnimClip] Bone01 compensated by inv(BRH.R) on '{clip.m_Name}' " +
                $"(BRH.R=({BrhRestR.X:F3},{BrhRestR.Y:F3},{BrhRestR.Z:F3},{BrhRestR.W:F3}))");
            return true;
        }

        static string Leaf(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            int i = p.LastIndexOf('/');
            return i >= 0 ? p.Substring(i + 1) : p;
        }

        static Quaternion Normalize(Quaternion q)
        {
            float n = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
            if (n < 1e-12f) return new Quaternion(0, 0, 0, 1);
            float inv = 1f / (float)Math.Sqrt(n);
            return new Quaternion(q.X * inv, q.Y * inv, q.Z * inv, q.W * inv);
        }

        static Quaternion Conjugate(Quaternion q) => new Quaternion(-q.X, -q.Y, -q.Z, q.W);

        static Quaternion Mul(Quaternion a, Quaternion b) =>
            new Quaternion(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        static void Rotate(Quaternion q, float x, float y, float z, out float ox, out float oy, out float oz)
        {
            // q * (0,x,y,z) * conj(q)
            var v = new Quaternion(x, y, z, 0);
            var r = Mul(Mul(q, v), Conjugate(q));
            ox = r.X; oy = r.Y; oz = r.Z;
        }
    }
}
