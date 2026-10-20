using System;

namespace AssetStudio
{
    /// <summary>
    /// Experimental Bone01.P (leading 3ch quant block) re-decodes for fast Blender A/B.
    /// Mutates <see cref="DenseClip.m_CodmRawSampleArray"/> cols 0..2 only; R/arms untouched.
    /// Call before rewindow/bake. Restore from a pristine raw clone between trials.
    /// </summary>
    public static class CodmBone01PosDecode
    {
        /// <summary>Active mode name for export tagging (null/stock = no rewrite).</summary>
        public static string ActiveMode;

        /// <summary>Scale argument for modes that take one (e.g. delta_u).</summary>
        public static float ActiveScale = 1f;

        public static float[] CloneRaw(AnimationClip clip)
        {
            var raw = clip?.m_MuscleClip?.m_Clip?.m_DenseClip?.m_CodmRawSampleArray;
            if (raw == null) return null;
            var copy = new float[raw.Length];
            Array.Copy(raw, copy, raw.Length);
            return copy;
        }

        public static void RestoreRaw(AnimationClip clip, float[] saved)
        {
            var dense = clip?.m_MuscleClip?.m_Clip?.m_DenseClip;
            if (dense?.m_CodmRawSampleArray == null || saved == null) return;
            if (dense.m_CodmRawSampleArray.Length != saved.Length) return;
            Array.Copy(saved, dense.m_CodmRawSampleArray, saved.Length);
        }

        /// <summary>
        /// Rewrite leading 3ch in raw from recovered uint16 codes + mode.
        /// Modes (grounded in prior diagnostics):
        ///   stock       — no change (linear min..max)
        ///   delta_u     — end + (u-uEnd)/65535 * scale   (ignore header span; scale=|BRH.X| matched wpos clue)
        ///   delta_span  — end + (u-uEnd)/65535 * span * scale  (stock deltas * scale; scale=0.5 = clue look)
        ///   u01         — replace with u in [0,1] (absolute; bake still subtracts end)
        ///   half        — IEEE half of code bits (previously rejected; kept for A/B)
        ///   flipx/y/z   — stock values with one axis negated
        ///   absx_rest   — end + (raw-end) * (|scale| / |end_x|) on all axes (uniform)
        /// </summary>
        public static bool Apply(AnimationClip clip, string mode, float scale = 1f)
        {
            if (string.IsNullOrEmpty(mode) || mode.Equals("stock", StringComparison.OrdinalIgnoreCase))
                return false;

            var dense = clip?.m_MuscleClip?.m_Clip?.m_DenseClip;
            if (dense?.m_CodmRawSampleArray == null) return false;
            if (dense.m_CodmBlockChannelCounts == null || dense.m_CodmBlockChannelCounts.Length < 1)
                return false;
            if (dense.m_CodmBlockChannelCounts[0] != 3) return false;
            if (dense.m_CodmBlockMins == null || dense.m_CodmBlockMaxs == null) return false;

            float min = dense.m_CodmBlockMins[0];
            float max = dense.m_CodmBlockMaxs[0];
            float span = max - min;
            if (Math.Abs(span) < 1e-12f) return false;

            int frames = dense.m_FrameCount;
            int ch = (int)dense.m_CurveCount;
            var raw = dense.m_CodmRawSampleArray;
            if (raw.Length < frames * ch || ch < 3) return false;

            // Recover codes from stock linear decode.
            var codes = new ushort[frames * 3];
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < 3; c++)
                {
                    float v = raw[f * ch + c];
                    int q = (int)Math.Round((v - min) / span * 65535.0);
                    if (q < 0) q = 0;
                    if (q > 65535) q = 65535;
                    codes[f * 3 + c] = (ushort)q;
                }
            }

            int e = (frames - 1) * 3;
            ushort qex = codes[e], qey = codes[e + 1], qez = codes[e + 2];
            float ex = raw[(frames - 1) * ch];
            float ey = raw[(frames - 1) * ch + 1];
            float ez = raw[(frames - 1) * ch + 2];
            const float inv = 1f / 65535f;
            string m = mode.Trim().ToLowerInvariant();

            for (int f = 0; f < frames; f++)
            {
                int ci = f * 3;
                int oi = f * ch;
                ushort qx = codes[ci], qy = codes[ci + 1], qz = codes[ci + 2];
                float ux = qx * inv, uy = qy * inv, uz = qz * inv;
                float sx, sy, sz;

                switch (m)
                {
                    case "delta_u":
                        // Header span ignored; Δcode → float with explicit scale.
                        sx = ex + (qx - qex) * inv * scale;
                        sy = ey + (qy - qey) * inv * scale;
                        sz = ez + (qz - qez) * inv * scale;
                        break;
                    case "delta_span":
                        // Stock linear deltas * scale (0.5 ≈ wpos clue look at decode layer).
                        sx = ex + (qx - qex) * inv * span * scale;
                        sy = ey + (qy - qey) * inv * span * scale;
                        sz = ez + (qz - qez) * inv * span * scale;
                        break;
                    case "u01":
                        sx = ux; sy = uy; sz = uz;
                        break;
                    case "half":
                        sx = CodmQuantizedDense.HalfToFloat(qx);
                        sy = CodmQuantizedDense.HalfToFloat(qy);
                        sz = CodmQuantizedDense.HalfToFloat(qz);
                        break;
                    case "flipx":
                        sx = -raw[oi]; sy = raw[oi + 1]; sz = raw[oi + 2];
                        break;
                    case "flipy":
                        sx = raw[oi]; sy = -raw[oi + 1]; sz = raw[oi + 2];
                        break;
                    case "flipz":
                        sx = raw[oi]; sy = raw[oi + 1]; sz = -raw[oi + 2];
                        break;
                    case "absx_rest":
                    {
                        // Uniform scale so rest |X| maps toward |scale| (e.g. knife |BRH.X|).
                        float denom = Math.Abs(ex);
                        float g = denom > 1e-8f ? Math.Abs(scale) / denom : 1f;
                        sx = ex + (raw[oi] - ex) * g;
                        sy = ey + (raw[oi + 1] - ey) * g;
                        sz = ez + (raw[oi + 2] - ez) * g;
                        break;
                    }
                    default:
                        return false;
                }

                raw[oi] = sx;
                raw[oi + 1] = sy;
                raw[oi + 2] = sz;
            }

            // Keep sampleArray in sync if present (rewindow will recopy from raw anyway).
            if (dense.m_SampleArray != null && dense.m_SampleArray.Length == raw.Length)
                Array.Copy(raw, dense.m_SampleArray, raw.Length);

            CodmDiagnostics.Log($"[AnimClip] Bone01.P decode '{mode}' scale={scale:G4} on '{clip.m_Name}'");
            return true;
        }

        public static void ResetActive()
        {
            ActiveMode = null;
            ActiveScale = 1f;
        }
    }
}
