using System;
using System.Collections.Generic;

namespace AssetStudio
{
    /// <summary>
    /// CoD Mobile stores quantized dense animation samples after the stock Clip
    /// (Streamed/Dense/Constant) and before ClipMuscleConstant trailing fields /
    /// ClipBindingConstant. Samples are uint16 grids in range blocks:
    ///   uint numChannels, float min, float range, uint flag(=3), uint packed4BitSize,
    ///   byte[packed4BitSize * 4]  // uint16 samples; size is 4× the 4-bit-equivalent length
    /// value = min + range * q / 65535. The channels are the last ones of the binding curve
    /// space (offset = binding dims - channels). Each clip is checked: the blocks must end on
    /// binding boundaries and every rotation binding in the window must decode to unit
    /// quaternions. A clip that fails the check, or every clip with <see cref="LegacyDecode"/>,
    /// takes the older reading: the second float as the block's max, a scored window, half-span
    /// 3-channel blocks and the idle bake at export.
    /// </summary>
    public static class CodmQuantizedDense
    {
        private const uint BlockFlag = 3;
        private static int _blobEdgeLogCount;

        /// <summary>
        /// Decode every quantized clip the pre-2026-10-28 way (second header float as max, scored
        /// window, idle bake). Set from the config key LegacyAnimDecode or codm --legacy-anim.
        /// </summary>
        public static bool LegacyDecode = false;

        /// <summary>Largest mean |length - 1| of a rotation binding's quaternions that passes the check.</summary>
        private const double UnitQuatTolerance = 0.01;

        /// <summary>
        /// [INVENTED/diagnostic] When true, blocks with exactly 3 channels decode uint16 samples
        /// as IEEE half-floats instead of linear min..max dequant. Inspect Bone01.P is a lone
        /// 3ch block whose rest codes are ~0 as half but ~0.24 as linear — test whether P
        /// was stored as half while the larger R block stays linear.
        /// Default false (stock linear dequant).
        /// </summary>
        public static bool ThreeChannelAsHalf = false;

        /// <summary>
        /// Legacy decode only. When true (default), 3-channel quant blocks dequant with half the header span:
        ///   value = min + 0.5*(max-min)*(q/65535)
        /// Bone01.P is always a lone 3ch block; Blender A/B (b01sweep 06_delta_span_0.5) matched
        /// held knife travel. Stock full-span deltas are ~2× too large. Header min/max are
        /// bind-derived floats, not a proven true value range — half-span is the working fix
        /// until the engine's exact scale field is identified. R/arm blocks are not 3ch.
        /// </summary>
        public static bool ThreeChannelHalfSpan = true;

        public static bool TryApply(byte[] region, ClipMuscleConstant muscle, AnimationClipBindingConstant bindings, string clipLabel = null)
        {
            if (region == null || region.Length < 64 || muscle?.m_Clip == null)
                return false;

            var clip = muscle.m_Clip;
            var streamed = clip.m_StreamedClip;
            var dense = clip.m_DenseClip;
            var constant = clip.m_ConstantClip;

            // Only fill clips that have no real streamed/dense sample payload.
            if (streamed != null && streamed.curveCount > 0 && streamed.data != null && streamed.data.Length > 2)
                return false;
            if (dense != null && dense.m_CurveCount > 0 && dense.m_SampleArray != null && dense.m_SampleArray.Length > 0)
                return false;
            if (dense == null || dense.m_FrameCount <= 0 || dense.m_SampleRate <= 0f)
                return false;
            if (constant?.data == null || constant.data.Length == 0)
                return false;

            int frames = dense.m_FrameCount;
            float rate = dense.m_SampleRate;

            if (!TryFindPayload(region, frames, rate, out int payloadOff, out uint headerChannels))
                return false;

            bool legacy = LegacyDecode;
            if (!TryParseBlocks(region, payloadOff, frames, legacy, out var blocks, out int endOff))
                return false;

            // Prefix / trailer are still undecoded — store trailer + sample logs.
            dense.m_CodmPrefixLength = payloadOff;
            dense.m_CodmPayloadOffset = payloadOff;
            dense.m_CodmBlocksEnd = endOff;
            if (endOff < region.Length)
            {
                int trailLen = region.Length - endOff;
                dense.m_CodmTrailer = new byte[trailLen];
                Array.Copy(region, endOff, dense.m_CodmTrailer, 0, trailLen);
            }
            else
            {
                dense.m_CodmTrailer = null;
            }

            if (System.Threading.Interlocked.Increment(ref _blobEdgeLogCount) <= 3)
            {
                if (payloadOff > 0)
                {
                    int pref = Math.Min(payloadOff, 64);
                    CodmDiagnostics.Warn($"[AnimClip] CODM blob prefix 0x0..0x{payloadOff:X} (first {pref}B): {Hex(region, 0, pref)}");
                }
                if (dense.m_CodmTrailer != null)
                {
                    int show = Math.Min(dense.m_CodmTrailer.Length, 64);
                    CodmDiagnostics.Warn($"[AnimClip] CODM blob trailer 0x{endOff:X}..0x{region.Length:X} ({dense.m_CodmTrailer.Length}B, first {show}B): {Hex(dense.m_CodmTrailer, 0, show)}");
                }
            }

            int totalChannels = 0;
            foreach (var b in blocks)
                totalChannels += b.Channels;
            if (totalChannels <= 0)
                return false;

            // Prefer header channel count when it matches (the Dense-style "extra" field).
            if (headerChannels > 0 && headerChannels != (uint)totalChannels)
            {
                CodmDiagnostics.Log(
                    $"[AnimClip] CODM quant dense: headerChannels={headerChannels} blockSum={totalChannels}; using blockSum");
            }

            var boundaries = GetBindingCurveBoundaries(bindings);
            int bindDims = boundaries.Count > 0 ? boundaries[boundaries.Count - 1] : 0;
            int constLen = constant.data.Length;
            int ideal = bindDims - constLen - totalChannels;

            int tailOffset = -1;
            if (!legacy && !CheckTailWindow(blocks, boundaries, bindings, frames, out tailOffset, out var rejectReason))
            {
                Logger.Warning($"[AnimClip] CODM quantized clip {clipLabel ?? "?"}: {rejectReason}; " +
                    "using the legacy decode (min/max, scored window, idle bake)");
                legacy = true;
                if (!TryParseBlocks(region, payloadOff, frames, true, out blocks, out endOff))
                    return false;
            }

            var samples = new float[frames * totalChannels];
            int col = 0;
            foreach (var b in blocks)
            {
                for (int f = 0; f < frames; f++)
                {
                    for (int c = 0; c < b.Channels; c++)
                    {
                        samples[f * totalChannels + col + c] = b.Values[f * b.Channels + c];
                    }
                }
                col += b.Channels;
            }

            // Keep raw samples so export can rewindow with Avatar path names.
            var raw = new float[samples.Length];
            Array.Copy(samples, raw, samples.Length);
            dense.m_CodmRawSampleArray = raw;

            // Block layout (shared min/max per block) — needed to diagnose Bone01.P crush.
            dense.m_CodmBlockChannelCounts = new int[blocks.Count];
            dense.m_CodmBlockMins = new float[blocks.Count];
            dense.m_CodmBlockMaxs = new float[blocks.Count];
            dense.m_CodmBlockPackedSizes = new uint[blocks.Count];
            dense.m_CodmBlockFlags = new uint[blocks.Count];
            for (int i = 0; i < blocks.Count; i++)
            {
                dense.m_CodmBlockChannelCounts[i] = blocks[i].Channels;
                dense.m_CodmBlockMins[i] = blocks[i].Min;
                dense.m_CodmBlockMaxs[i] = blocks[i].Max;
                dense.m_CodmBlockPackedSizes[i] = blocks[i].PackedSize;
                dense.m_CodmBlockFlags[i] = blocks[i].Flag;
            }
            // Record 3ch span factor (Bone01.P) for later halfspan= toggle without reload.
            dense.m_Codm3chSpanFactor = 1f;
            foreach (var b in blocks)
            {
                if (b.Channels == 3)
                {
                    dense.m_Codm3chSpanFactor = b.SpanFactor;
                    break;
                }
            }
            if (blocks.Count > 0 && blocks.Count <= 8)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < blocks.Count; i++)
                {
                    if (i > 0) sb.Append(" | ");
                    sb.Append(blocks[i].Channels);
                    sb.Append("ch[");
                    sb.Append(blocks[i].Min.ToString("G4"));
                    sb.Append("..");
                    sb.Append((legacy ? blocks[i].Max : blocks[i].Min + blocks[i].Max).ToString("G4"));
                    sb.Append(']');
                }
                CodmDiagnostics.Warn($"[AnimClip] CODM quant blocks: {blocks.Count} ({sb}) sum={totalChannels}");
            }

            int streamedOffset;
            string placeNote;
            if (!legacy)
            {
                streamedOffset = tailOffset;
                placeNote = "min/range, tail window, unit quaternions";
            }
            // Legacy: never trust "exact" Unity pack alone — Equip ideal=63 parks on scale/torso;
            // arm TRS live later in the binding list.
            else if (TryFindBestDenseWindow(boundaries, bindings, totalChannels, ideal, samples, frames, null, null,
                out streamedOffset, out var scoreNote))
            {
                placeNote = $"best({scoreNote},ideal={ideal})";
                CodmDiagnostics.Warn(
                    $"[AnimClip] CODM quant dense window chosen: idealOffset={ideal} → {streamedOffset}, " +
                    $"dense={totalChannels}, const={constLen}, bindDims={bindDims}, {scoreNote}");
            }
            else
            {
                CodmDiagnostics.Warn(
                    $"[AnimClip] CODM quant dense skipped (no binding window of width {totalChannels}): " +
                    $"idealOffset={ideal}, const={constLen}, bindDims={bindDims}");
                return false;
            }

            SanitizeTransformSamples(samples, frames, totalChannels, streamedOffset, bindings, keepScale: !legacy);

            dense.m_CodmAbsolute = !legacy;
            dense.m_CurveCount = (uint)totalChannels;
            dense.m_SampleArray = samples;
            dense.m_BeginTime = 0f;
            if (dense.m_SampleRate <= 0f)
                dense.m_SampleRate = rate > 0 ? rate : 30f;

            if (streamed != null)
                streamed.curveCount = (uint)streamedOffset;

            if (muscle.m_StopTime <= 0f && dense.m_SampleRate > 0f)
                muscle.m_StopTime = frames / dense.m_SampleRate;

            CodmDiagnostics.Warn(
                $"[AnimClip] CODM quant dense decoded ({placeNote}): frames={frames}, denseCurves={totalChannels}, " +
                $"streamedOffset={streamedOffset}, constant={constLen}, " +
                $"blocks={blocks.Count}, payload=0x{payloadOff:X}-0x{endOff:X}");
            return true;
        }

        /// <summary>
        /// Re-pick dense→binding window using resolved bone paths.
        /// When <paramref name="idlePose"/> is provided (FireLeft/FireJump @0), prefer windows
        /// whose dense last frame is closest to that pose on arm/weapon curves — matches the
        /// IdleRelativeEnd assumption (dense end ≈ held rest in authoring space).
        /// </summary>
        public static bool TryRewindowWithPaths(
            AnimationClip clip,
            Func<uint, string> resolvePath,
            Dictionary<(uint path, uint attr), float[]> idlePose = null)
        {
            if (clip?.m_MuscleClip?.m_Clip == null || resolvePath == null)
                return false;
            var muscle = clip.m_MuscleClip;
            var c = muscle.m_Clip;
            var dense = c.m_DenseClip;
            var streamed = c.m_StreamedClip;
            var bindings = clip.m_ClipBindingConstant;
            if (dense?.m_CodmRawSampleArray == null || dense.m_CurveCount == 0 || dense.m_CodmAbsolute)
                return false;
            if (streamed != null && streamed.data != null && streamed.data.Length > 2)
                return false;

            int frames = dense.m_FrameCount;
            int width = (int)dense.m_CurveCount;
            var raw = dense.m_CodmRawSampleArray;
            if (raw.Length < frames * width)
                return false;

            var samples = new float[raw.Length];
            Array.Copy(raw, samples, raw.Length);

            var boundaries = GetBindingCurveBoundaries(bindings);
            int bindDims = boundaries.Count > 0 ? boundaries[boundaries.Count - 1] : 0;
            int constLen = c.m_ConstantClip?.data?.Length ?? 0;
            int ideal = bindDims - constLen - width;
            int prev = (int)(streamed?.curveCount ?? 0);

            if (!TryFindBestDenseWindow(boundaries, bindings, width, ideal, samples, frames, resolvePath,
                idlePose, out int streamedOffset, out var scoreNote))
                return false;

            SanitizeTransformSamples(samples, frames, width, streamedOffset, bindings, keepScale: false);
            dense.m_SampleArray = samples;
            if (streamed != null)
                streamed.curveCount = (uint)streamedOffset;

            CodmDiagnostics.Warn(
                $"[AnimClip] CODM dense rewindow '{clip.m_Name}': {prev} → {streamedOffset} ({scoreNote})");
            return streamedOffset != prev;
        }

        private static int CountScalesInWindow(AnimationClipBindingConstant bindings, int start, int width)
        {
            if (bindings?.genericBindings == null || width <= 0) return 0;
            int curve = 0, scales = 0;
            foreach (var b in bindings.genericBindings)
            {
                int dim = BindingCurveDim(b);
                int s = curve, e = curve + dim;
                curve = e;
                if (e <= start || s >= start + width) continue;
                if (b.typeID == ClassIDType.Transform && b.attribute == 3)
                    scales += dim;
            }
            return scales;
        }

        /// <summary>
        /// Pick a binding-aligned [start, start+width] window.
        /// With idle pose: minimize end-frame distance to idle on arm/weapon (data-driven).
        /// With path resolver only: arm/weapon coverage + motion.
        /// Without: maximize pos+rot, minimize scales.
        /// </summary>
        private static bool TryFindBestDenseWindow(
            List<int> boundaries, AnimationClipBindingConstant bindings,
            int width, int preferredStart, float[] samples, int frames,
            Func<uint, string> resolvePath,
            Dictionary<(uint path, uint attr), float[]> idlePose,
            out int start, out string note)
        {
            start = -1;
            note = null;
            if (width <= 0 || boundaries == null || boundaries.Count < 2 || bindings?.genericBindings == null)
                return false;

            var set = new HashSet<int>(boundaries);
            var slots = new List<(int curve, int dim, uint path, uint attr)>();
            int curve = 0;
            foreach (var b in bindings.genericBindings)
            {
                int dim = BindingCurveDim(b);
                slots.Add((curve, dim, b.path, b.typeID == ClassIDType.Transform ? b.attribute : 0u));
                curve += dim;
            }
            int attrLen = boundaries[boundaries.Count - 1];

            int best = -1;
            int bestArms = -1;
            int bestWeapon = -1;
            double bestArmMot = -1;
            double bestEndCost = double.MaxValue;
            int bestEndN = 0;
            int bestBody = -1;
            int bestPosRot = -1;
            int bestScale = int.MaxValue;
            int bestFingers = int.MaxValue;
            int bestDist = int.MaxValue;

            foreach (var s in boundaries)
            {
                int e = s + width;
                if (!set.Contains(e) || e > attrLen)
                    continue;

                int posRot = 0, scale = 0, arms = 0, body = 0, fingers = 0, weapon = 0;
                double armMot = 0;
                double endCost = 0;
                int endN = 0;
                foreach (var slot in slots)
                {
                    if (slot.curve + slot.dim <= s || slot.curve >= e)
                        continue;
                    if (slot.attr == 3) { scale += slot.dim; continue; }
                    if (slot.attr == 1 || slot.attr == 2 || slot.attr == 4) posRot += slot.dim;

                    string leaf = LeafName(resolvePath, slot.path);
                    int kind = BoneKind(leaf);
                    if (kind == 3) fingers++;
                    else if (kind == 2) arms++;
                    else if (kind == 1) body++;
                    if (IsWeaponBone(leaf)) weapon++;

                    int local = slot.curve - s;
                    if (local >= 0 && local + slot.dim <= width && samples != null && frames > 1
                        && (kind == 2 || IsWeaponBone(leaf)))
                    {
                        armMot += ChannelMotion(samples, frames, width, local, slot.dim);

                        if (idlePose != null
                            && idlePose.TryGetValue((slot.path, slot.attr), out var idle)
                            && idle != null && idle.Length == slot.dim)
                        {
                            int baseOff = (frames - 1) * width + local;
                            if (slot.attr == 2 && slot.dim == 4)
                            {
                                endCost += QuatAngleDeg(
                                    samples[baseOff], samples[baseOff + 1], samples[baseOff + 2], samples[baseOff + 3],
                                    idle[0], idle[1], idle[2], idle[3]);
                                endN++;
                            }
                            else if (slot.attr == 1 && slot.dim == 3)
                            {
                                double dx = samples[baseOff] - idle[0];
                                double dy = samples[baseOff + 1] - idle[1];
                                double dz = samples[baseOff + 2] - idle[2];
                                endCost += Math.Sqrt(dx * dx + dy * dy + dz * dz) * 50.0; // rough deg-equivalent weight
                                endN++;
                            }
                        }
                    }
                }

                double avgEnd = endN > 0 ? endCost / endN : 999.0;
                int dist = preferredStart >= 0 ? Math.Abs(s - preferredStart) : s;
                bool better;
                if (resolvePath != null && idlePose != null && idlePose.Count > 0)
                {
                    // Data-driven: dense end should look like held idle on arm/weapon.
                    // Require some arm/weapon coverage; then minimize end cost.
                    bool viable = arms + weapon >= 4 && scale < width / 2;
                    bool bestViable = bestArms + bestWeapon >= 4;
                    better = viable && (!bestViable
                        || avgEnd < bestEndCost - 0.5
                        || (Math.Abs(avgEnd - bestEndCost) <= 0.5 && arms + weapon > bestArms + bestWeapon)
                        || (Math.Abs(avgEnd - bestEndCost) <= 0.5 && arms + weapon == bestArms + bestWeapon && armMot > bestArmMot)
                        || (Math.Abs(avgEnd - bestEndCost) <= 0.5 && arms + weapon == bestArms + bestWeapon
                            && Math.Abs(armMot - bestArmMot) <= 1e-9 && dist < bestDist));
                }
                else if (resolvePath != null)
                {
                    better = weapon > bestWeapon
                        || (weapon == bestWeapon && arms > bestArms)
                        || (weapon == bestWeapon && arms == bestArms && armMot > bestArmMot + 1e-9)
                        || (weapon == bestWeapon && arms == bestArms && Math.Abs(armMot - bestArmMot) <= 1e-9 && body > bestBody)
                        || (weapon == bestWeapon && arms == bestArms && Math.Abs(armMot - bestArmMot) <= 1e-9 && body == bestBody && scale < bestScale)
                        || (weapon == bestWeapon && arms == bestArms && Math.Abs(armMot - bestArmMot) <= 1e-9 && body == bestBody && scale == bestScale && fingers < bestFingers)
                        || (weapon == bestWeapon && arms == bestArms && Math.Abs(armMot - bestArmMot) <= 1e-9 && body == bestBody && scale == bestScale && fingers == bestFingers && dist < bestDist);
                }
                else
                {
                    better = scale < bestScale
                        || (scale == bestScale && posRot > bestPosRot)
                        || (scale == bestScale && posRot == bestPosRot && dist < bestDist)
                        || (scale == bestScale && posRot == bestPosRot && dist == bestDist && s > best);
                }

                if (better)
                {
                    best = s;
                    bestArms = arms;
                    bestWeapon = weapon;
                    bestArmMot = armMot;
                    bestEndCost = avgEnd;
                    bestEndN = endN;
                    bestBody = body;
                    bestPosRot = posRot;
                    bestScale = scale;
                    bestFingers = fingers;
                    bestDist = dist;
                }
            }

            if (best < 0)
                return false;
            start = best;
            note = idlePose != null && idlePose.Count > 0
                ? $"endCost={bestEndCost:F1}(n={bestEndN}),weapon={bestWeapon},arms={bestArms},armMot={bestArmMot:F3},scale={bestScale}"
                : resolvePath != null
                    ? $"weapon={bestWeapon},arms={bestArms},armMot={bestArmMot:F3},body={bestBody},fingers={bestFingers},scale={bestScale},dist={bestDist}"
                    : $"posRot={bestPosRot},scale={bestScale},dist={bestDist}";
            return true;
        }

        private static double QuatAngleDeg(float x0, float y0, float z0, float w0, float x1, float y1, float z1, float w1)
        {
            double l0 = Math.Sqrt(x0 * x0 + y0 * y0 + z0 * z0 + w0 * w0);
            double l1 = Math.Sqrt(x1 * x1 + y1 * y1 + z1 * z1 + w1 * w1);
            if (l0 < 1e-8 || l1 < 1e-8) return 180;
            double dot = Math.Abs((x0 * x1 + y0 * y1 + z0 * z1 + w0 * w1) / (l0 * l1));
            if (dot > 1) dot = 1;
            return Math.Acos(dot) * 2 * 180 / Math.PI;
        }

        private static bool IsWeaponBone(string leaf) =>
            !string.IsNullOrEmpty(leaf) && (
                leaf.Equals("Bone01", StringComparison.OrdinalIgnoreCase)
                || leaf.Equals("Bone_RightHand", StringComparison.OrdinalIgnoreCase)
                || leaf.Equals("Bone_LeftHand", StringComparison.OrdinalIgnoreCase));

        private static double ChannelMotion(float[] samples, int frames, int width, int local, int dim)
        {
            double sum = 0; int n = 0;
            for (int f = 1; f < frames; f++)
            {
                for (int d = 0; d < dim; d++)
                {
                    float a = samples[(f - 1) * width + local + d];
                    float b = samples[f * width + local + d];
                    sum += (b - a) * (b - a);
                    n++;
                }
            }
            return n > 0 ? Math.Sqrt(sum / n) : 0;
        }

        private static string LeafName(Func<uint, string> resolvePath, uint path)
        {
            if (resolvePath == null) return null;
            var p = resolvePath(path);
            if (string.IsNullOrEmpty(p)) return null;
            int i = p.LastIndexOf('/');
            return i >= 0 ? p.Substring(i + 1) : p;
        }

        /// <summary>1=body, 2=arm-core, 3=finger, 0=other</summary>
        private static int BoneKind(string leaf)
        {
            if (string.IsNullOrEmpty(leaf)) return 0;
            if (leaf.IndexOf("Thumb", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("Index", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("Middle", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("Ring", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("Pinky", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("Pinkie", StringComparison.OrdinalIgnoreCase) >= 0)
                return 3;
            if (leaf.IndexOf("UpperArm", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("ForeArm", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.Equals("b_LeftArm", StringComparison.OrdinalIgnoreCase)
                || leaf.Equals("b_RightArm", StringComparison.OrdinalIgnoreCase)
                || leaf.Equals("b_LeftHand", StringComparison.OrdinalIgnoreCase)
                || leaf.Equals("b_RightHand", StringComparison.OrdinalIgnoreCase)
                || leaf.IndexOf("Clav", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.Equals("Bone01", StringComparison.OrdinalIgnoreCase)
                || leaf.Equals("Bone_RightHand", StringComparison.OrdinalIgnoreCase)
                || leaf.Equals("Bone_LeftHand", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (leaf.IndexOf("Bip01", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("Spine", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("Hips", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("Neck", StringComparison.OrdinalIgnoreCase) >= 0
                || leaf.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0)
                return 1;
            return 0;
        }

        /// <summary>
        /// Find binding-aligned [start, start+width] closest to preferredStart.
        /// </summary>
        private static bool TryFindNearestDenseWindow(
            List<int> boundaries, int width, int preferredStart, out int start)
        {
            start = -1;
            if (width <= 0 || boundaries == null || boundaries.Count < 2)
                return false;

            var set = new HashSet<int>(boundaries);
            int best = -1;
            int bestDist = int.MaxValue;
            foreach (var s in boundaries)
            {
                int e = s + width;
                if (!set.Contains(e))
                    continue;
                int dist = preferredStart >= 0 ? Math.Abs(s - preferredStart) : s;
                if (dist < bestDist || (dist == bestDist && (best < 0 || s < best)))
                {
                    bestDist = dist;
                    best = s;
                }
            }
            if (best < 0)
                return false;
            start = best;
            return true;
        }

        /// <summary>
        /// Dense quant values mapped onto Scale bindings are not ~1 and explode skins;
        /// rotation slots are often non-unit. Force scales to identity and normalize quats.
        /// </summary>
        private static void SanitizeTransformSamples(
            float[] samples, int frames, int denseChannels, int streamedOffset,
            AnimationClipBindingConstant bindings, bool keepScale)
        {
            if (bindings?.genericBindings == null || samples == null)
                return;

            int curve = 0;
            foreach (var b in bindings.genericBindings)
            {
                int dim = BindingCurveDim(b);
                int start = curve;
                int end = curve + dim;
                curve = end;

                // Only touch curves inside the dense window.
                if (end <= streamedOffset || start >= streamedOffset + denseChannels)
                    continue;

                int local = start - streamedOffset;
                if (local < 0 || local + dim > denseChannels)
                    continue;

                if (b.typeID != ClassIDType.Transform)
                    continue;

                if (b.attribute == 3) // scale — legacy: keep bind-pose scale
                {
                    if (keepScale)
                        continue;
                    for (int f = 0; f < frames; f++)
                    {
                        int o = f * denseChannels + local;
                        samples[o] = 1f;
                        samples[o + 1] = 1f;
                        samples[o + 2] = 1f;
                    }
                }
                else if (b.attribute == 2) // quaternion
                {
                    for (int f = 0; f < frames; f++)
                    {
                        int o = f * denseChannels + local;
                        float x = samples[o], y = samples[o + 1], z = samples[o + 2], w = samples[o + 3];
                        float lenSq = x * x + y * y + z * z + w * w;
                        if (lenSq > 1e-8f)
                        {
                            float inv = 1f / (float)Math.Sqrt(lenSq);
                            samples[o] = x * inv;
                            samples[o + 1] = y * inv;
                            samples[o + 2] = z * inv;
                            samples[o + 3] = w * inv;
                        }
                        else
                        {
                            samples[o] = 0f;
                            samples[o + 1] = 0f;
                            samples[o + 2] = 0f;
                            samples[o + 3] = 1f;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The (min, range) reading puts the channels at the end of the binding curve space. Accept it when
        /// every block starts on a binding boundary there and each rotation binding in the window decodes
        /// to unit quaternions; a window without a rotation to check is refused.
        /// </summary>
        private static bool CheckTailWindow(List<Block> blocks, List<int> boundaries, AnimationClipBindingConstant bindings,
            int frames, out int offset, out string reason)
        {
            int total = 0;
            foreach (var b in blocks)
                total += b.Channels;
            int bindDims = boundaries[boundaries.Count - 1];
            offset = bindDims - total;
            reason = null;
            if (offset < 0)
            {
                reason = $"{total} quantized channels but {bindDims} binding curves";
                return false;
            }
            var edges = new HashSet<int>(boundaries);
            int edge = offset;
            foreach (var b in blocks)
            {
                if (!edges.Contains(edge))
                {
                    reason = $"block at curve {edge} starts inside a binding";
                    return false;
                }
                edge += b.Channels;
            }

            int curve = 0, rotations = 0;
            foreach (var g in bindings.genericBindings)
            {
                int start = curve;
                curve += BindingCurveDim(g);
                if (start < offset || g.typeID != ClassIDType.Transform || g.attribute != 2)
                    continue;
                double err = MeanUnitError(blocks, frames, start - offset);
                if (!(err <= UnitQuatTolerance))
                {
                    reason = $"rotation at curve {start} is not a unit quaternion (mean |length - 1| = {err:0.###})";
                    return false;
                }
                rotations++;
            }
            if (rotations == 0)
            {
                reason = "no rotation binding in the window to check";
                return false;
            }
            return true;
        }

        /// <summary>Mean |length - 1| over the frames of the quaternion at window column <paramref name="local"/>; NaN when it crosses a block.</summary>
        private static double MeanUnitError(List<Block> blocks, int frames, int local)
        {
            foreach (var b in blocks)
            {
                if (local < b.Channels)
                {
                    if (local + 4 > b.Channels || frames <= 0)
                        return double.NaN;
                    double sum = 0;
                    for (int f = 0; f < frames; f++)
                    {
                        int o = f * b.Channels + local;
                        double x = b.Values[o], y = b.Values[o + 1], z = b.Values[o + 2], w = b.Values[o + 3];
                        sum += Math.Abs(Math.Sqrt(x * x + y * y + z * z + w * w) - 1);
                    }
                    return sum / frames;
                }
                local -= b.Channels;
            }
            return double.NaN;
        }

        private static int BindingCurveDim(GenericBinding b)
        {
            if (b.typeID == ClassIDType.Transform)
            {
                switch (b.attribute)
                {
                    case 1: // position
                    case 3: // scale
                    case 4: // euler
                        return 3;
                    case 2: // rotation
                        return 4;
                    default:
                        return 1;
                }
            }
            return 1;
        }

        private static List<int> GetBindingCurveBoundaries(AnimationClipBindingConstant bindings)
        {
            var boundaries = new List<int> { 0 };
            if (bindings?.genericBindings == null)
                return boundaries;
            int total = 0;
            foreach (var b in bindings.genericBindings)
            {
                total += BindingCurveDim(b);
                boundaries.Add(total);
            }
            return boundaries;
        }

        private static bool TryFindPayload(byte[] data, int frames, float rate, out int payloadOff, out uint headerChannels)
        {
            payloadOff = -1;
            headerChannels = 0;
            var rateBytes = BitConverter.GetBytes(rate);
            // Scan for Dense-style header ending in: frames, channels, rate, 0
            for (int i = 0; i + 48 < data.Length; i += 4)
            {
                if (data[i + 40] != rateBytes[0] || data[i + 41] != rateBytes[1] ||
                    data[i + 42] != rateBytes[2] || data[i + 43] != rateBytes[3])
                    continue;
                int hdrFrames = BitConverter.ToInt32(data, i + 32);
                uint channels = BitConverter.ToUInt32(data, i + 36);
                uint trailing = BitConverter.ToUInt32(data, i + 44);
                if (hdrFrames != frames || trailing != 0)
                    continue;
                if (channels == 0 || channels > 512)
                    continue;
                // Preceding words look like the range descriptors we observed.
                payloadOff = i + 48;
                headerChannels = channels;
            }
            return payloadOff >= 0;
        }

        private static string Hex(byte[] data, int off, int len)
        {
            if (data == null || len <= 0) return "";
            var sb = new System.Text.StringBuilder(len * 3);
            for (int i = 0; i < len && off + i < data.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[off + i].ToString("X2"));
            }
            return sb.ToString();
        }

        private struct Block
        {
            public int Channels;
            public float Min;
            public float Max;
            public uint Flag;
            public uint PackedSize;
            public float[] Values;
            public float SpanFactor;
        }

        private static bool TryParseBlocks(byte[] data, int start, int frames, bool legacy, out List<Block> blocks, out int endOff)
        {
            blocks = new List<Block>();
            endOff = start;
            int p = start;
            int guard = 0;
            while (p + 20 <= data.Length && guard++ < 32)
            {
                // Optional leading 0 before a block (padding from previous nb*4).
                if (BitConverter.ToUInt32(data, p) == 0 && p + 24 <= data.Length)
                {
                    uint peekFlag = BitConverter.ToUInt32(data, p + 16);
                    uint peekNi = BitConverter.ToUInt32(data, p + 4);
                    if (peekFlag == BlockFlag && peekNi > 0 && peekNi <= 512)
                        p += 4;
                }

                uint ni = BitConverter.ToUInt32(data, p);
                float min = BitConverter.ToSingle(data, p + 4);
                float max = BitConverter.ToSingle(data, p + 8);
                uint flag = BitConverter.ToUInt32(data, p + 12);
                uint nb = BitConverter.ToUInt32(data, p + 16);

                if (flag != BlockFlag || ni > 512 || nb > 500000)
                    break;

                if (ni == 0 || nb == 0)
                {
                    endOff = p + 20;
                    break;
                }

                int dataLen = checked((int)nb * 4);
                if (p + 20 + dataLen > data.Length)
                    break;

                int sampleCount = frames * (int)ni;
                if (sampleCount * 2 > dataLen)
                    break;

                var values = new float[sampleCount];
                // The second header float is the block's range; the legacy decode reads it as the max.
                float span = legacy ? max - min : max;
                float spanFactor = 1f;
                // Legacy: Bone01.P (lone 3ch) with the max reading overshoots ~2× (b01sweep 06).
                if (legacy && ThreeChannelHalfSpan && ni == 3)
                {
                    span *= 0.5f;
                    spanFactor = 0.5f;
                }
                const float inv = 1f / 65535f;
                int dataOff = p + 20;
                bool asHalf = legacy && ThreeChannelAsHalf && ni == 3;
                for (int i = 0; i < sampleCount; i++)
                {
                    ushort q = BitConverter.ToUInt16(data, dataOff + i * 2);
                    values[i] = asHalf ? HalfToFloat(q) : min + span * (q * inv);
                }

                blocks.Add(new Block
                {
                    Channels = (int)ni,
                    Min = min,
                    Max = max,
                    Flag = flag,
                    PackedSize = nb,
                    Values = values,
                    SpanFactor = spanFactor
                });
                p += 20 + dataLen;
                endOff = p;
            }

            return blocks.Count > 0;
        }

        /// <summary>IEEE 754 binary16 → float32 (little-endian uint16 bit pattern).</summary>
        public static float HalfToFloat(ushort u)
        {
            int s = (u >> 15) & 1;
            int e = (u >> 10) & 0x1f;
            int m = u & 0x3ff;
            float sign = s == 0 ? 1f : -1f;
            if (e == 0)
                return sign * (m / 1024f) * (1f / 16384f); // 2^-14
            if (e == 31)
                return 0f; // Inf/NaN → 0 (animation keys must be finite)
            return sign * (1f + m / 1024f) * (float)Math.Pow(2, e - 15);
        }

        /// <summary>
        /// [INVENTED/diagnostic] Reinterpret the leading 3-channel quant block's already-decoded
        /// linear samples as IEEE half by recovering uint16 codes via block min/max, then
        /// writing half→float into <see cref="DenseClip.m_CodmRawSampleArray"/> (and sampleArray).
        /// Call before rewindow/bake. No-op if block0 is not 3 channels.
        /// </summary>
        public static bool TryReinterpretLeading3chAsHalf(AnimationClip clip)
        {
            var dense = clip?.m_MuscleClip?.m_Clip?.m_DenseClip;
            if (dense?.m_CodmRawSampleArray == null || dense.m_CodmBlockChannelCounts == null
                || dense.m_CodmBlockChannelCounts.Length < 1 || dense.m_CodmBlockChannelCounts[0] != 3)
                return false;
            if (dense.m_CodmBlockMins == null || dense.m_CodmBlockMaxs == null)
                return false;

            float min = dense.m_CodmBlockMins[0];
            float max = dense.m_CodmBlockMaxs[0];
            float span = max - min;
            if (Math.Abs(span) < 1e-12f)
                return false;

            int ch = (int)dense.m_CurveCount;
            int frames = dense.m_FrameCount;
            var raw = dense.m_CodmRawSampleArray;
            if (raw.Length < frames * ch || ch < 3)
                return false;

            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int o = f * ch + c;
                    int q = (int)Math.Round((raw[o] - min) / span * 65535.0);
                    if (q < 0) q = 0;
                    if (q > 65535) q = 65535;
                    float h = HalfToFloat((ushort)q);
                    if (float.IsNaN(h) || float.IsInfinity(h))
                        h = 0f;
                    raw[o] = h;
                }
            }
            if (dense.m_SampleArray != null && dense.m_SampleArray.Length == raw.Length)
                Array.Copy(raw, dense.m_SampleArray, raw.Length);

            CodmDiagnostics.Warn($"[AnimClip] CODM [INVENTED] reinterpreted leading 3ch block as IEEE half on '{clip.m_Name}'");
            return true;
        }

        /// <summary>
        /// Switch leading 3ch (Bone01.P) between full header span and half-span without reloading.
        /// Uses stored min/max + <see cref="DenseClip.m_Codm3chSpanFactor"/>.
        /// </summary>
        public static bool Reapply3chSpanFactor(AnimationClip clip, float desiredFactor)
        {
            if (desiredFactor < 0.01f || desiredFactor > 1.01f)
                return false;
            var dense = clip?.m_MuscleClip?.m_Clip?.m_DenseClip;
            if (dense?.m_CodmRawSampleArray == null || dense.m_CodmBlockChannelCounts == null
                || dense.m_CodmBlockChannelCounts.Length < 1 || dense.m_CodmBlockChannelCounts[0] != 3)
                return false;
            if (dense.m_CodmBlockMins == null || dense.m_CodmBlockMaxs == null)
                return false;

            float cur = dense.m_Codm3chSpanFactor;
            if (cur < 0.01f) cur = 1f;
            if (Math.Abs(cur - desiredFactor) < 1e-6f)
                return false;

            float min = dense.m_CodmBlockMins[0];
            float max = dense.m_CodmBlockMaxs[0];
            float fullSpan = max - min;
            if (Math.Abs(fullSpan) < 1e-12f)
                return false;
            float curSpan = fullSpan * cur;
            float newSpan = fullSpan * desiredFactor;

            int ch = (int)dense.m_CurveCount;
            int frames = dense.m_FrameCount;
            var raw = dense.m_CodmRawSampleArray;
            if (raw.Length < frames * ch || ch < 3)
                return false;

            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int o = f * ch + c;
                    float u = (raw[o] - min) / curSpan;
                    raw[o] = min + newSpan * u;
                }
            }
            if (dense.m_SampleArray != null && dense.m_SampleArray.Length == raw.Length)
                Array.Copy(raw, dense.m_SampleArray, raw.Length);
            dense.m_Codm3chSpanFactor = desiredFactor;
            CodmDiagnostics.Log(
                $"[AnimClip] CODM 3ch span factor {cur:G3} → {desiredFactor:G3} on '{clip.m_Name}'");
            return true;
        }
    }
}
