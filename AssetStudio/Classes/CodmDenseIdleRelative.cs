using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetStudio
{
    public enum CodmBakeMode
    {
        /// <summary>Leave quantized dense as absolute samples.</summary>
        Off = 0,
        /// <summary>out[f] = idle + (dense[f] - dense[last]) — clip ends on idle.</summary>
        IdleRelativeEnd = 1,
        /// <summary>out[f] = idle + dense[f] — treat dense as delta from idle (end = idle only if dense[last]≈0).</summary>
        IdlePlusDense = 2,
        /// <summary>out[f] = idle + (dense[f] - dense[0]) — clip starts on idle.</summary>
        IdleRelativeStart = 3,
        /// <summary>out[f] = idle for all frames (static control).</summary>
        IdleStatic = 4,
    }

    /// <summary>
    /// Rebake CODM quantized dense samples relative to an idle pose.
    /// Absolute idle for hollow Equip/Inspect is NOT in the CODM blob (prefix/trailer/ConstantClip
    /// /muscle StartX / ValueArrayReferencePose). Engine-side sources:
    /// <list type="bullet">
    /// <item><see cref="PreferWriteDefaultsIdle"/> — Animator/Avatar local TRS (Unity WriteDefaults). Structural.</item>
    /// <item><see cref="FindIdleReference"/> Fire* StreamedClip — pragmatic held pose; knife Bone01≈bind,
    ///   and melee overrides often remap Idle/Fire to knife clips (state continuity).</item>
    /// </list>
    /// ConstantClip is last-resort fallback only.
    /// </summary>
    public static class CodmDenseIdleRelative
    {
        /// <summary>Active bake mode for ModelConverter / Bake(). Default IdleRelativeEnd.</summary>
        public static CodmBakeMode Mode = CodmBakeMode.IdleRelativeEnd;

        public static bool IsCodmHydratedDense(AnimationClip clip)
        {
            var c = clip?.m_MuscleClip?.m_Clip;
            if (c?.m_DenseClip == null) return false;
            var streamed = c.m_StreamedClip;
            var dense = c.m_DenseClip;
            if (dense.m_CurveCount == 0 || dense.m_SampleArray == null || dense.m_SampleArray.Length == 0)
                return false;
            return streamed == null || streamed.data == null || streamed.data.Length <= 2;
        }

        public static AnimationClip FindIdleReference(IEnumerable<AnimationClip> clips)
        {
            if (clips == null) return null;
            var list = clips.Where(c => c?.m_Name != null).ToList();
            AnimationClip Pick(Func<AnimationClip, bool> pred) => list.FirstOrDefault(pred);

            return Pick(c => c.m_Name == "MeleeWeapon_001_Knife_iw6_1P_M_FireJump")
                ?? Pick(c => c.m_Name == "MeleeWeapon_001_Knife_iw6_1P_M_FireLeft")
                ?? Pick(c => c.m_Name.EndsWith("FireJump", StringComparison.OrdinalIgnoreCase))
                ?? Pick(c => c.m_Name.IndexOf("FireLeft", StringComparison.OrdinalIgnoreCase) >= 0
                             && c.m_Name.IndexOf("End", StringComparison.OrdinalIgnoreCase) < 0
                             && c.m_Name.IndexOf("Star", StringComparison.OrdinalIgnoreCase) < 0
                             && c.m_Name.IndexOf("Camera", StringComparison.OrdinalIgnoreCase) < 0);
        }

        /// <summary>
        /// Snapshot dense samples so a clip can be restored between bake-mode trials.
        /// </summary>
        public static float[] CloneDenseSamples(AnimationClip clip)
        {
            var s = clip?.m_MuscleClip?.m_Clip?.m_DenseClip?.m_SampleArray;
            if (s == null) return null;
            var copy = new float[s.Length];
            Array.Copy(s, copy, s.Length);
            return copy;
        }

        public static void RestoreDenseSamples(AnimationClip clip, float[] saved)
        {
            var s = clip?.m_MuscleClip?.m_Clip?.m_DenseClip?.m_SampleArray;
            if (s == null || saved == null || s.Length != saved.Length) return;
            Array.Copy(saved, s, saved.Length);
        }

        /// <summary>Scale relative arm motion after idle bake. 1 = as decoded (no invent).</summary>
        public static float ArmMotionGain = 1f;
        /// <summary>Scale relative weapon rotation after idle bake. 1 = as decoded.</summary>
        public static float WeaponRotGain = 1f;
        /// <summary>Scale relative weapon translation after idle bake. 1 = as decoded.</summary>
        public static float WeaponPosGain = 1f;

        /// <summary>
        /// [INVENTED] When true, Bone01 / Bone_*Hand position curves stay at idle (no dense P motion).
        /// Default false — we keep dense P and fix its space instead of freezing it.
        /// </summary>
        public static bool SkipDenseWeaponPos = false;

        /// <summary>
        /// [INVENTED/diagnostic] When true, Bone01 / Bone_*Hand rotations stay at idle (no dense R motion).
        /// Tests whether knife error is in Bone01.R vs .P. Default false.
        /// </summary>
        public static bool SkipDenseWeaponRot = false;

        /// <summary>Deprecated alias — sets both weapon rot/pos gains.</summary>
        public static float WeaponMotionGain
        {
            get => WeaponRotGain;
            set { WeaponRotGain = value; WeaponPosGain = value; }
        }
        /// <summary>
        /// [REJECTED] SE(3) position bake: P_out = P_idle + R_idle*conj(R_ref)*(P_raw-P_ref).
        /// Blender: significantly worse than component-wise hspan baseline. Default false.
        /// </summary>
        public static bool UseSe3PositionBake = false;

        /// <summary>
        /// [INVENTED/diagnostic] After component-wise IdleRelative P bake on weapon leaves,
        /// rotate the position delta by that bone's idle (rest) rotation:
        /// P_out = P_idle + R_idle * (P_raw - P_ref).
        /// Distinct from SE3 (which uses R_idle*conj(R_ref)). Default false.
        /// </summary>
        public static bool WeaponPosMulRestR = false;

        /// <summary>
        /// [INVENTED/diagnostic] Like <see cref="WeaponPosMulRestR"/> but with conj(R_idle).
        /// Default false.
        /// </summary>
        public static bool WeaponPosMulInvRestR = false;

        /// <summary>
        /// [EXPERIMENTAL] Rescale weapon P deltas using hierarchy, not wpos gain.
        /// After IdleRelative, replace (raw-end) with (q-qRest)/qRest * |Bone_RightHand.X|
        /// which is (raw-end) * |BRH.X| / ((max-min) * uRest). Matches wpos≈0.5 clue
        /// (RMSE~0.001) because Inspect/Equip 3ch headers are bind floats and over-wide.
        /// Requires <see cref="BindBoneRightHandAbsX"/> &gt; 0. Default false.
        /// </summary>
        public static bool WeaponPosRestRelBindX = false;

        /// <summary>|Bone_RightHand.localPosition.X| from prefab; set by exporter when using bind-X scale.</summary>
        public static float BindBoneRightHandAbsX = 0f;

        /// <summary>
        /// [REJECTED] Weapon-leaf right-mul quat bake (qRaw*conj(qRef)*qIdle).
        /// Blender: kar_qr net worse than hspan control (maybe slight P side-effect).
        /// Arms stay left-mul either way; default false.
        /// </summary>
        public static bool QuatRightMul = false;

        /// <summary>
        /// [DIAGNOSTIC] After IdleRelative bake, lock Bone01 + Bone_RightHand dense
        /// channels to idle for all frames. Tests whether knife error is Bone01 motion
        /// vs static attach. Default false — not for production.
        /// </summary>
        public static bool FreezeDenseWeaponToIdle = false;

        /// <summary>
        /// Engine-accurate idle: every Transform local P/R from this Animator (WriteDefaults /
        /// prefab bind). When true, hierarchy idle wins over Fire* for all matching bindings.
        /// Prefer <see cref="IdleSourceAuto"/> for production (Fire* if present, else WD).
        /// </summary>
        public static bool PreferWriteDefaultsIdle = false;

        /// <summary>
        /// Production default: if a FireJump/FireLeft clip is in the export set, use it as idle
        /// (matches melee OverrideController continuity — karambit Idle/Fire remap to knife).
        /// If none, fall back to <see cref="PreferWriteDefaultsIdle"/> (prefab bind).
        /// Set false when the caller forces <c>idle=fire</c> or <c>idle=wd</c>.
        /// </summary>
        public static bool IdleSourceAuto = true;

        /// <summary>
        /// [PARTIAL] Rebase weapon-leaf idle (Bone01 / Bone_*Hand) onto hierarchy bind only.
        /// Prefer <see cref="PreferWriteDefaultsIdle"/> for full-skeleton WD.
        /// </summary>
        public static bool PreferHierarchyWeaponIdle = false;

        /// <summary>
        /// [PARTIAL] Rebase non-weapon (arm/finger) idle onto hierarchy bind only.
        /// Prefer <see cref="PreferWriteDefaultsIdle"/> for full-skeleton WD.
        /// </summary>
        public static bool PreferHierarchyArmIdle = false;

        /// <summary>
        /// Unity-space P/R from the target Animator hierarchy (full skeleton and/or weapon/arm).
        /// Keyed like clip bindings (path hash + attribute). Set by ModelConverter before Bake.
        /// </summary>
        public static Dictionary<(uint path, uint attr), float[]> HierarchyWeaponIdle;

        /// <summary>Optional bone-path resolver for motion-gain classification (set by ModelConverter).</summary>
        public static Func<uint, string> PathResolver;

        /// <summary>
        /// Mutates target dense samples in place using <see cref="Mode"/>. idleRef may be null.
        /// </summary>
        public static bool Bake(AnimationClip target, AnimationClip idleRef)
        {
            if (Mode == CodmBakeMode.Off)
                return false;
            if (!IsCodmHydratedDense(target))
                return false;

            var clip = target.m_MuscleClip.m_Clip;
            var dense = clip.m_DenseClip;
            var binds = target.m_ClipBindingConstant?.genericBindings;
            if (binds == null || binds.Length == 0)
                return false;

            var idle = idleRef != null
                ? BuildStreamedPoseNearTime(idleRef, 0f)
                : new Dictionary<(uint path, uint attr), float[]>();
            if (idleRef != null && idle.Count == 0)
                CodmDiagnostics.Warn($"[AnimClip] CODM bake: no pose from '{idleRef.m_Name}', using const-prefix only");

            int frames = dense.m_FrameCount;
            int ch = (int)dense.m_CurveCount;
            var samples = dense.m_SampleArray;
            if (samples.Length < frames * ch)
                return false;

            int stream = (int)(clip.m_StreamedClip?.curveCount ?? 0);
            var cdata = clip.m_ConstantClip?.data;

            var lastFrame = new float[ch];
            var firstFrame = new float[ch];
            Array.Copy(samples, (frames - 1) * ch, lastFrame, 0, ch);
            Array.Copy(samples, 0, firstFrame, 0, ch);

            // Dense-window rotation locals per path (for SE(3) position bake).
            var rotLocalInWindow = new Dictionary<uint, int>();
            var rotIdleVals = new Dictionary<uint, float[]>();
            {
                int cscan = 0;
                foreach (var b in binds)
                {
                    int dim = BindingDim(b);
                    int start = cscan;
                    cscan += dim;
                    if (b.typeID != ClassIDType.Transform || b.attribute != 2 || dim != 4)
                        continue;
                    if (start + dim <= stream || start >= stream + ch)
                        continue;
                    int local = start - stream;
                    if (local < 0 || local + 4 > ch)
                        continue;
                    rotLocalInWindow[b.path] = local;
                    if (idle.TryGetValue((b.path, 2u), out var rv) && rv != null && rv.Length == 4)
                        rotIdleVals[b.path] = rv;
                    else if (cdata != null && start + 4 <= cdata.Length)
                        rotIdleVals[b.path] = new[] { cdata[start], cdata[start + 1], cdata[start + 2], cdata[start + 3] };
                    if (PreferWriteDefaultsIdle
                        && HierarchyWeaponIdle != null
                        && HierarchyWeaponIdle.TryGetValue((b.path, 2u), out var hrWd)
                        && hrWd != null && hrWd.Length == 4)
                        rotIdleVals[b.path] = hrWd;
                    else if (PreferHierarchyWeaponIdle
                        && HierarchyWeaponIdle != null
                        && HierarchyWeaponIdle.TryGetValue((b.path, 2u), out var hr)
                        && hr != null && hr.Length == 4)
                        rotIdleVals[b.path] = hr;
                    else if (PreferHierarchyArmIdle
                        && HierarchyWeaponIdle != null
                        && !IsWeaponLeaf(b.path)
                        && HierarchyWeaponIdle.TryGetValue((b.path, 2u), out var hrArm)
                        && hrArm != null && hrArm.Length == 4)
                        rotIdleVals[b.path] = hrArm;
                }
            }

            int matched = 0, fromConst = 0, fromHier = 0, deltaOnly = 0;
            int se3Pos = 0;
            int curve = 0;
            foreach (var b in binds)
            {
                int dim = BindingDim(b);
                int start = curve;
                curve += dim;

                if (start + dim <= stream || start >= stream + ch)
                    continue;

                int local = start - stream;
                if (local < 0 || local + dim > ch)
                    continue;

                var key = (b.path, b.attribute);
                bool hasIdle = idle.TryGetValue(key, out var idleVals) && idleVals != null && idleVals.Length == dim;
                int idleSource = hasIdle ? 1 : 0;

                if (!hasIdle && cdata != null && start + dim <= cdata.Length)
                {
                    idleVals = new float[dim];
                    for (int d = 0; d < dim; d++)
                        idleVals[d] = cdata[start + d];
                    hasIdle = true;
                    idleSource = 2;
                }

                // WriteDefaults / full hierarchy bind (engine-accurate idle source).
                if (PreferWriteDefaultsIdle
                    && b.typeID == ClassIDType.Transform
                    && (b.attribute == 1 || b.attribute == 2)
                    && HierarchyWeaponIdle != null
                    && HierarchyWeaponIdle.TryGetValue(key, out var hierWd)
                    && hierWd != null && hierWd.Length == dim)
                {
                    idleVals = hierWd;
                    hasIdle = true;
                    idleSource = 3;
                }
                // Weapon leaves only → hierarchy bind.
                else if (PreferHierarchyWeaponIdle
                    && b.typeID == ClassIDType.Transform
                    && (b.attribute == 1 || b.attribute == 2)
                    && HierarchyWeaponIdle != null
                    && HierarchyWeaponIdle.TryGetValue(key, out var hierIdle)
                    && hierIdle != null && hierIdle.Length == dim
                    && IsWeaponLeaf(b.path))
                {
                    idleVals = hierIdle;
                    hasIdle = true;
                    idleSource = 3;
                }
                // Arms/fingers → this Animator hierarchy.
                else if (PreferHierarchyArmIdle
                    && b.typeID == ClassIDType.Transform
                    && (b.attribute == 1 || b.attribute == 2)
                    && HierarchyWeaponIdle != null
                    && HierarchyWeaponIdle.TryGetValue(key, out var hierArm)
                    && hierArm != null && hierArm.Length == dim
                    && !IsWeaponLeaf(b.path))
                {
                    idleVals = hierArm;
                    hasIdle = true;
                    idleSource = 3;
                }

                if (idleSource == 1) matched += dim;
                else if (idleSource == 2) fromConst += dim;
                else if (idleSource == 3) fromHier += dim;

                float rotGain = MotionGainForPath(b.path, isPosition: false);
                float posGain = MotionGainForPath(b.path, isPosition: true);

                // [INVENTED/diagnostic] optional: keep weapon attach position at idle (skip dense P).
                if (SkipDenseWeaponPos
                    && b.typeID == ClassIDType.Transform && b.attribute == 1 && dim == 3
                    && IsWeaponLeaf(b.path) && hasIdle)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        int o = f * ch + local;
                        samples[o] = idleVals[0];
                        samples[o + 1] = idleVals[1];
                        samples[o + 2] = idleVals[2];
                    }
                    continue;
                }

                // [INVENTED/diagnostic] optional: keep weapon rotation at idle (skip dense R).
                if (SkipDenseWeaponRot
                    && b.typeID == ClassIDType.Transform && b.attribute == 2 && dim == 4
                    && IsWeaponLeaf(b.path) && hasIdle)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        int o = f * ch + local;
                        samples[o] = idleVals[0];
                        samples[o + 1] = idleVals[1];
                        samples[o + 2] = idleVals[2];
                        samples[o + 3] = idleVals[3];
                    }
                    continue;
                }

                // Quaternions: SO(3) relative bake. Component-wise idle+(raw-end) then
                // normalize exaggerates curls (esp. fingers) once angles get large.
                // Default: qOut = qIdle * conj(qRef) * qRaw (left-multiply, local composition).
                // QuatRightMul: qOut = qRaw * conj(qRef) * qIdle (alternate association).
                if (b.typeID == ClassIDType.Transform && b.attribute == 2 && dim == 4
                    && Mode != CodmBakeMode.Off)
                {
                    if (!hasIdle) deltaOnly += dim;
                    var qIdle = hasIdle
                        ? NormalizeQuat(new Quaternion(idleVals[0], idleVals[1], idleVals[2], idleVals[3]))
                        : new Quaternion(0, 0, 0, 1);
                    var qEnd = NormalizeQuat(new Quaternion(
                        lastFrame[local], lastFrame[local + 1], lastFrame[local + 2], lastFrame[local + 3]));
                    var qStart = NormalizeQuat(new Quaternion(
                        firstFrame[local], firstFrame[local + 1], firstFrame[local + 2], firstFrame[local + 3]));
                    var qIdentity = new Quaternion(0, 0, 0, 1);
                    float gain = rotGain;
                    // Right-mul only on weapon leaves — arms keep left-mul (Fire*-compatible).
                    bool rightMul = QuatRightMul && IsWeaponLeaf(b.path);

                    for (int f = 0; f < frames; f++)
                    {
                        int o = f * ch + local;
                        var qRaw = NormalizeQuat(new Quaternion(
                            samples[o], samples[o + 1], samples[o + 2], samples[o + 3]));
                        Quaternion qOut;
                        switch (Mode)
                        {
                            case CodmBakeMode.IdleRelativeEnd:
                            {
                                var rawA = AlignHemisphere(qEnd, qRaw);
                                if (rightMul)
                                {
                                    var qDeltaR = MulQuat(rawA, ConjugateQuat(qEnd));
                                    if (Math.Abs(gain - 1f) > 1e-4f)
                                        qDeltaR = SlerpQuat(qIdentity, qDeltaR, gain);
                                    qOut = MulQuat(qDeltaR, qIdle);
                                }
                                else
                                {
                                    var qDelta = MulQuat(ConjugateQuat(qEnd), rawA);
                                    if (Math.Abs(gain - 1f) > 1e-4f)
                                        qDelta = SlerpQuat(qIdentity, qDelta, gain);
                                    qOut = MulQuat(qIdle, qDelta);
                                }
                                break;
                            }
                            case CodmBakeMode.IdleRelativeStart:
                            {
                                var rawA = AlignHemisphere(qStart, qRaw);
                                if (rightMul)
                                {
                                    var qDeltaR = MulQuat(rawA, ConjugateQuat(qStart));
                                    if (Math.Abs(gain - 1f) > 1e-4f)
                                        qDeltaR = SlerpQuat(qIdentity, qDeltaR, gain);
                                    qOut = MulQuat(qDeltaR, qIdle);
                                }
                                else
                                {
                                    var qDelta = MulQuat(ConjugateQuat(qStart), rawA);
                                    if (Math.Abs(gain - 1f) > 1e-4f)
                                        qDelta = SlerpQuat(qIdentity, qDelta, gain);
                                    qOut = MulQuat(qIdle, qDelta);
                                }
                                break;
                            }
                            case CodmBakeMode.IdlePlusDense:
                                qOut = rightMul
                                    ? MulQuat(AlignHemisphere(qIdle, qRaw), qIdle)
                                    : MulQuat(qIdle, AlignHemisphere(qIdle, qRaw));
                                break;
                            case CodmBakeMode.IdleStatic:
                                qOut = qIdle;
                                break;
                            default:
                                qOut = qRaw;
                                break;
                        }
                        qOut = NormalizeQuat(qOut);
                        samples[o] = qOut.X;
                        samples[o + 1] = qOut.Y;
                        samples[o + 2] = qOut.Z;
                        samples[o + 3] = qOut.W;
                    }
                    continue;
                }

                // Position: SE(3) when same path has dense R, else component-wise.
                int rLocal = -1;
                bool se3 = UseSe3PositionBake
                    && b.typeID == ClassIDType.Transform && b.attribute == 1 && dim == 3
                    && (Mode == CodmBakeMode.IdleRelativeEnd || Mode == CodmBakeMode.IdleRelativeStart)
                    && rotLocalInWindow.TryGetValue(b.path, out rLocal);

                if (se3)
                {
                    se3Pos += 3;
                    if (!hasIdle) deltaOnly += dim;
                    var qIdleR = rotIdleVals.TryGetValue(b.path, out var riv) && riv.Length == 4
                        ? NormalizeQuat(new Quaternion(riv[0], riv[1], riv[2], riv[3]))
                        : new Quaternion(0, 0, 0, 1);
                    Quaternion qRefR = Mode == CodmBakeMode.IdleRelativeStart
                        ? NormalizeQuat(new Quaternion(
                            firstFrame[rLocal], firstFrame[rLocal + 1], firstFrame[rLocal + 2], firstFrame[rLocal + 3]))
                        : NormalizeQuat(new Quaternion(
                            lastFrame[rLocal], lastFrame[rLocal + 1], lastFrame[rLocal + 2], lastFrame[rLocal + 3]));
                    // R_idle * conj(R_ref) rotates dense position deltas into idle space.
                    var qDeltaSpace = MulQuat(qIdleR, ConjugateQuat(qRefR));
                    float g = posGain;
                    for (int f = 0; f < frames; f++)
                    {
                        int o = f * ch + local;
                        float dx = samples[o] - (Mode == CodmBakeMode.IdleRelativeStart
                            ? firstFrame[local] : lastFrame[local]);
                        float dy = samples[o + 1] - (Mode == CodmBakeMode.IdleRelativeStart
                            ? firstFrame[local + 1] : lastFrame[local + 1]);
                        float dz = samples[o + 2] - (Mode == CodmBakeMode.IdleRelativeStart
                            ? firstFrame[local + 2] : lastFrame[local + 2]);
                        RotateVector(qDeltaSpace, dx * g, dy * g, dz * g, out float rx, out float ry, out float rz);
                        float ix = hasIdle ? idleVals[0] : 0f;
                        float iy = hasIdle ? idleVals[1] : 0f;
                        float iz = hasIdle ? idleVals[2] : 0f;
                        samples[o] = ix + rx;
                        samples[o + 1] = iy + ry;
                        samples[o + 2] = iz + rz;
                    }
                    continue;
                }

                float gainPos = posGain;
                // Experimental: replace uniform WeaponPosGain with bind-derived scale for weapon P.
                if (WeaponPosRestRelBindX && BindBoneRightHandAbsX > 1e-8f
                    && b.typeID == ClassIDType.Transform && b.attribute == 1 && dim == 3
                    && IsWeaponLeaf(b.path)
                    && dense.m_CodmBlockMins != null && dense.m_CodmBlockMins.Length > 0
                    && dense.m_CodmBlockChannelCounts != null && dense.m_CodmBlockChannelCounts[0] == 3)
                {
                    float bmin = dense.m_CodmBlockMins[0];
                    float bmax = dense.m_CodmBlockMaxs[0];
                    float span = bmax - bmin;
                    if (span > 1e-8f)
                    {
                        // Per-axis: factor_d = |BRH.X| / (span * uRest_d), uRest from end (or start) sample.
                        bool useStart = Mode == CodmBakeMode.IdleRelativeStart;
                        for (int d = 0; d < 3; d++)
                        {
                            float refVal = useStart ? firstFrame[local + d] : lastFrame[local + d];
                            float uRest = (refVal - bmin) / span;
                            if (uRest < 1e-4f) uRest = 0.5f; // fallback if degenerate
                            float factor = BindBoneRightHandAbsX / (span * uRest);
                            float idleVal = hasIdle ? idleVals[d] : 0f;
                            for (int f = 0; f < frames; f++)
                            {
                                int o = f * ch + local + d;
                                float raw = samples[o];
                                samples[o] = idleVal + factor * (raw - refVal);
                            }
                        }
                        continue;
                    }
                }

                float[] wRiv = null;
                bool weaponRestR = (WeaponPosMulRestR || WeaponPosMulInvRestR)
                    && b.typeID == ClassIDType.Transform && b.attribute == 1 && dim == 3
                    && IsWeaponLeaf(b.path)
                    && (Mode == CodmBakeMode.IdleRelativeEnd || Mode == CodmBakeMode.IdleRelativeStart)
                    && rotIdleVals.TryGetValue(b.path, out wRiv) && wRiv != null && wRiv.Length == 4;

                if (weaponRestR)
                {
                    var qRest = NormalizeQuat(new Quaternion(wRiv[0], wRiv[1], wRiv[2], wRiv[3]));
                    if (WeaponPosMulInvRestR)
                        qRest = ConjugateQuat(qRest);
                    if (!hasIdle) deltaOnly += dim;
                    float ix = hasIdle ? idleVals[0] : 0f;
                    float iy = hasIdle ? idleVals[1] : 0f;
                    float iz = hasIdle ? idleVals[2] : 0f;
                    for (int f = 0; f < frames; f++)
                    {
                        int o = f * ch + local;
                        float dx = samples[o] - (Mode == CodmBakeMode.IdleRelativeStart
                            ? firstFrame[local] : lastFrame[local]);
                        float dy = samples[o + 1] - (Mode == CodmBakeMode.IdleRelativeStart
                            ? firstFrame[local + 1] : lastFrame[local + 1]);
                        float dz = samples[o + 2] - (Mode == CodmBakeMode.IdleRelativeStart
                            ? firstFrame[local + 2] : lastFrame[local + 2]);
                        RotateVector(qRest, dx * gainPos, dy * gainPos, dz * gainPos,
                            out float rx, out float ry, out float rz);
                        samples[o] = ix + rx;
                        samples[o + 1] = iy + ry;
                        samples[o + 2] = iz + rz;
                    }
                    continue;
                }

                for (int d = 0; d < dim; d++)
                {
                    float idleVal = hasIdle ? idleVals[d] : 0f;
                    if (!hasIdle) deltaOnly++;
                    float endVal = lastFrame[local + d];
                    float startVal = firstFrame[local + d];

                    for (int f = 0; f < frames; f++)
                    {
                        int o = f * ch + local + d;
                        float raw = samples[o];
                        switch (Mode)
                        {
                            case CodmBakeMode.IdleRelativeEnd:
                                samples[o] = idleVal + gainPos * (raw - endVal);
                                break;
                            case CodmBakeMode.IdlePlusDense:
                                samples[o] = idleVal + raw;
                                break;
                            case CodmBakeMode.IdleRelativeStart:
                                samples[o] = idleVal + gainPos * (raw - startVal);
                                break;
                            case CodmBakeMode.IdleStatic:
                                samples[o] = idleVal;
                                break;
                            default:
                                break;
                        }
                    }
                }

                if (b.typeID == ClassIDType.Transform && b.attribute == 3)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        int o = f * ch + local;
                        if (hasIdle)
                        {
                            samples[o] = idleVals[0];
                            samples[o + 1] = idleVals[1];
                            samples[o + 2] = idleVals[2];
                        }
                        else
                        {
                            samples[o] = 1f;
                            samples[o + 1] = 1f;
                            samples[o + 2] = 1f;
                        }
                    }
                }
            }

            // [DIAGNOSTIC] lock weapon dense channels to idle (knife attach test).
            int froze = 0;
            if (FreezeDenseWeaponToIdle)
            {
                int c2 = 0;
                foreach (var b in binds)
                {
                    int dim = BindingDim(b);
                    int start = c2;
                    c2 += dim;
                    if (b.typeID != ClassIDType.Transform || (b.attribute != 1 && b.attribute != 2))
                        continue;
                    if (!IsWeaponLeaf(b.path))
                        continue;
                    if (start + dim <= stream || start >= stream + ch)
                        continue;
                    int local = start - stream;
                    if (local < 0 || local + dim > ch)
                        continue;
                    var key = (b.path, b.attribute);
                    if (!idle.TryGetValue(key, out var idleVals) || idleVals == null || idleVals.Length != dim)
                    {
                        if (cdata == null || start + dim > cdata.Length)
                            continue;
                        idleVals = new float[dim];
                        for (int d = 0; d < dim; d++)
                            idleVals[d] = cdata[start + d];
                    }
                    for (int f = 0; f < frames; f++)
                    {
                        int o = f * ch + local;
                        for (int d = 0; d < dim; d++)
                            samples[o + d] = idleVals[d];
                    }
                    froze += dim;
                }
            }

            CodmDiagnostics.Warn(
                $"[AnimClip] CODM bake mode={Mode} on '{target.m_Name}': " +
                $"ref='{(idleRef != null ? idleRef.m_Name : "(none)")}', " +
                $"idleKeys={idle.Count}, fromRef={matched}, fromConstPrefix={fromConst}, fromHierWeapon={fromHier}, deltaOnly={deltaOnly}, " +
                $"hierWeapon={PreferHierarchyWeaponIdle} hierArm={PreferHierarchyArmIdle} writeDefaults={PreferWriteDefaultsIdle}/{(HierarchyWeaponIdle?.Count ?? 0)}, " +
                $"armGain={ArmMotionGain}, weaponRotGain={WeaponRotGain}, weaponPosGain={WeaponPosGain}, " +
                $"bindX={WeaponPosRestRelBindX}/{BindBoneRightHandAbsX:G4}, " +
                $"skipWeaponPos={SkipDenseWeaponPos}, skipWeaponRot={SkipDenseWeaponRot}, " +
                $"se3Pos={UseSe3PositionBake}/{se3Pos}, " +
                $"quatRight={QuatRightMul}, freezeWpn={FreezeDenseWeaponToIdle}/{froze}, " +
                $"frames={frames}, denseCurves={ch}");
            return true;
        }

        private static float MotionGainForPath(uint path, bool isPosition)
        {
            string leaf = LeafOf(path);
            if (string.IsNullOrEmpty(leaf))
                return 1f;

            if (IsWeaponLeafName(leaf))
                return isPosition ? WeaponPosGain : WeaponRotGain;

            if (leaf.IndexOf("Thumb", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("Index", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("Middle", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("Ring", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("Pinky", StringComparison.OrdinalIgnoreCase) >= 0)
                return 1f;

            if (leaf.IndexOf("Arm", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("Clav", StringComparison.OrdinalIgnoreCase) >= 0)
                return ArmMotionGain;

            return 1f;
        }

        private static bool IsWeaponLeaf(uint path) => IsWeaponLeafName(LeafOf(path));

        private static bool IsWeaponLeafName(string leaf) =>
            !string.IsNullOrEmpty(leaf) && (
                leaf.Equals("Bone01", StringComparison.OrdinalIgnoreCase)
                || leaf.Equals("Bone_RightHand", StringComparison.OrdinalIgnoreCase)
                || leaf.Equals("Bone_LeftHand", StringComparison.OrdinalIgnoreCase));

        private static string LeafOf(uint path)
        {
            if (PathResolver == null) return null;
            var p = PathResolver(path);
            if (string.IsNullOrEmpty(p)) return null;
            int i = p.LastIndexOf('/');
            return i >= 0 ? p.Substring(i + 1) : p;
        }

        private static Quaternion NormalizeQuat(Quaternion q)
        {
            float lenSq = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
            if (lenSq < 1e-8f)
                return new Quaternion(0, 0, 0, 1);
            float inv = 1f / (float)Math.Sqrt(lenSq);
            return new Quaternion(q.X * inv, q.Y * inv, q.Z * inv, q.W * inv);
        }

        private static Quaternion ConjugateQuat(Quaternion q) =>
            new Quaternion(-q.X, -q.Y, -q.Z, q.W);

        private static Quaternion AlignHemisphere(Quaternion reference, Quaternion q) =>
            Quaternion.Dot(reference, q) < 0f
                ? new Quaternion(-q.X, -q.Y, -q.Z, -q.W)
                : q;

        private static Quaternion MulQuat(Quaternion a, Quaternion b) =>
            new Quaternion(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        /// <summary>Rotate vector by quaternion (Unity-style: q * v * conj(q)).</summary>
        private static void RotateVector(Quaternion q, float x, float y, float z,
            out float ox, out float oy, out float oz)
        {
            // t = 2 * cross(q.xyz, v)
            float tx = 2f * (q.Y * z - q.Z * y);
            float ty = 2f * (q.Z * x - q.X * z);
            float tz = 2f * (q.X * y - q.Y * x);
            // v + q.w * t + cross(q.xyz, t)
            ox = x + q.W * tx + (q.Y * tz - q.Z * ty);
            oy = y + q.W * ty + (q.Z * tx - q.X * tz);
            oz = z + q.W * tz + (q.X * ty - q.Y * tx);
        }

        public static Dictionary<(uint path, uint attr), float[]> SampleIdlePose(AnimationClip idleRef, float time = 0f)
            => BuildStreamedPoseNearTime(idleRef, time);

        private static Dictionary<(uint path, uint attr), float[]> BuildStreamedPoseNearTime(AnimationClip anim, float time)
        {
            var result = new Dictionary<(uint, uint), float[]>();
            var clip = anim.m_MuscleClip?.m_Clip;
            var binds = anim.m_ClipBindingConstant?.genericBindings;
            if (clip?.m_StreamedClip == null || binds == null)
                return result;

            List<StreamedClip.StreamedFrame> frames;
            try { frames = clip.m_StreamedClip.ReadData(); }
            catch { return result; }
            if (frames == null || frames.Count == 0)
                return result;

            // Precompute binding curve ranges — sample each binding as an atomic TRS block.
            // Per-float "first key" stitching mixes quat components from different poses and
            // offsets the knife (Bone01) even when Fire* control clips look fine.
            var bindRanges = new List<(uint path, uint attr, int start, int dim)>();
            int curve = 0;
            foreach (var b in binds)
            {
                int dim = BindingDim(b);
                bindRanges.Add((b.path, b.attribute, curve, dim));
                curve += dim;
            }

            var ordered = frames.OrderBy(f => f.time).ToList();
            var curveVals = new Dictionary<int, float>();

            // 1) First frame that contains ALL components of a binding → initial pose for it
            var filled = new HashSet<int>(); // binding start indices filled
            foreach (var fr in ordered)
            {
                if (fr.keyList == null || fr.keyList.Length == 0) continue;
                var present = new HashSet<int>();
                var byIndex = new Dictionary<int, float>();
                foreach (var k in fr.keyList)
                {
                    present.Add(k.index);
                    byIndex[k.index] = k.value;
                }
                for (int bi = 0; bi < bindRanges.Count; bi++)
                {
                    var br = bindRanges[bi];
                    if (filled.Contains(br.start)) continue;
                    bool all = true;
                    for (int i = 0; i < br.dim; i++)
                    {
                        if (!present.Contains(br.start + i)) { all = false; break; }
                    }
                    if (!all) continue;
                    for (int i = 0; i < br.dim; i++)
                        curveVals[br.start + i] = byIndex[br.start + i];
                    filled.Add(br.start);
                }
            }

            // 2) Advance pose with complete binding updates for frames at time <= target
            foreach (var fr in ordered)
            {
                if (fr.time > time + 1e-4f) break;
                if (fr.keyList == null || fr.keyList.Length == 0) continue;
                var present = new HashSet<int>();
                var byIndex = new Dictionary<int, float>();
                foreach (var k in fr.keyList)
                {
                    present.Add(k.index);
                    byIndex[k.index] = k.value;
                }
                foreach (var br in bindRanges)
                {
                    bool all = true;
                    for (int i = 0; i < br.dim; i++)
                    {
                        if (!present.Contains(br.start + i)) { all = false; break; }
                    }
                    if (!all) continue;
                    for (int i = 0; i < br.dim; i++)
                        curveVals[br.start + i] = byIndex[br.start + i];
                }
            }

            // 3) ConstantClip tail fill for curves never streamed
            var cdata = clip.m_ConstantClip?.data;
            if (cdata != null)
            {
                int baseIdx = (int)clip.m_StreamedClip.curveCount + (int)(clip.m_DenseClip?.m_CurveCount ?? 0);
                for (int i = 0; i < cdata.Length; i++)
                {
                    int idx = baseIdx + i;
                    if (!curveVals.ContainsKey(idx))
                        curveVals[idx] = cdata[i];
                }
            }

            foreach (var br in bindRanges)
            {
                var vals = new float[br.dim];
                bool any = false, all = true;
                for (int i = 0; i < br.dim; i++)
                {
                    if (curveVals.TryGetValue(br.start + i, out var v))
                    {
                        vals[i] = v;
                        any = true;
                    }
                    else all = false;
                }
                // Prefer complete bindings only (avoid partial quats)
                if (all && any)
                    result[(br.path, br.attr)] = vals;
            }
            return result;
        }

        private static int BindingDim(GenericBinding b)
        {
            if (b.typeID != ClassIDType.Transform) return 1;
            switch (b.attribute)
            {
                case 1:
                case 3:
                case 4:
                    return 3;
                case 2:
                    return 4;
                default:
                    return 1;
            }
        }

        private static Quaternion SlerpQuat(Quaternion a, Quaternion b, float t)
        {
            // Allow t outside [0,1] so ArmMotionGain > 1 can amplify deltas.
            if (Math.Abs(t) < 1e-8f) return a;
            float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            if (dot < 0f)
            {
                b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);
                dot = -dot;
            }
            if (dot > 0.9995f)
            {
                return NormalizeQuat(new Quaternion(
                    a.X + (b.X - a.X) * t,
                    a.Y + (b.Y - a.Y) * t,
                    a.Z + (b.Z - a.Z) * t,
                    a.W + (b.W - a.W) * t));
            }
            dot = Math.Min(1f, dot);
            float theta = (float)Math.Acos(dot);
            float s = (float)Math.Sin(theta);
            if (Math.Abs(s) < 1e-8f) return a;
            float w1 = (float)Math.Sin((1f - t) * theta) / s;
            float w2 = (float)Math.Sin(t * theta) / s;
            return NormalizeQuat(new Quaternion(
                a.X * w1 + b.X * w2,
                a.Y * w1 + b.Y * w2,
                a.Z * w1 + b.Z * w2,
                a.W * w1 + b.W * w2));
        }
    }
}
