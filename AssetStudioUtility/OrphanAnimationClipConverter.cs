using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetStudio
{
    /// <summary>
    /// EXCEPTION path: builds a tiny synthetic hierarchy for AnimationClips that only
    /// animate the Animator root (Unity path hash 0 / empty legacy path).
    ///
    /// Intentionally separate from <see cref="ModelConverter"/> so the normal
    /// Animator + AnimationClip export workflow is never affected.
    /// </summary>
    public sealed class OrphanAnimationClipConverter : IImported
    {
        public const string SyntheticBoneName = "Camera";

        public ImportedFrame RootFrame { get; }
        // Empty list (not null): Gltf/Cast iterate MeshList; FBX still castToBone-marks the root.
        public List<ImportedMesh> MeshList { get; } = new List<ImportedMesh>();
        public List<ImportedMaterial> MaterialList { get; } = new List<ImportedMaterial>();
        public List<ImportedTexture> TextureList { get; } = new List<ImportedTexture>();
        public List<ImportedKeyframedAnimation> AnimationList { get; } = new List<ImportedKeyframedAnimation>();
        public List<ImportedMorph> MorphList { get; } = new List<ImportedMorph>();

        public OrphanAnimationClipConverter(IEnumerable<AnimationClip> clips)
        {
            if (clips == null)
                throw new ArgumentNullException(nameof(clips));

            var list = clips.Where(c => c != null).ToList();
            if (list.Count == 0)
                throw new ArgumentException("No AnimationClips provided.", nameof(clips));

            foreach (var clip in list)
            {
                if (!TryValidateRootOnly(clip, out var reason))
                    throw new InvalidOperationException($"Clip '{clip.m_Name}' is not eligible for orphan/camera export: {reason}");
            }

            RootFrame = new ImportedFrame
            {
                Name = SyntheticBoneName,
                LocalPosition = Vector3.Zero,
                LocalRotation = Vector3.Zero,
                LocalScale = Vector3.One
            };

            foreach (var clip in list)
                ConvertClip(clip);
        }

        /// <summary>
        /// True only when every Transform binding targets Animator root (path 0 / empty path).
        /// Clips with child bone paths are refused so we never emit a flat pile of origin bones.
        /// </summary>
        public static bool TryValidateRootOnly(AnimationClip clip, out string rejectReason)
        {
            rejectReason = null;
            if (clip == null)
            {
                rejectReason = "null clip";
                return false;
            }

            if (clip.m_Legacy)
                return TryValidateLegacyRootOnly(clip, out rejectReason);

            var bindings = GetBindings(clip);
            if (bindings == null || bindings.Length == 0)
            {
                rejectReason = "no curve bindings";
                return false;
            }

            var transformBindings = bindings.Where(b => b.typeID == ClassIDType.Transform).ToList();
            if (transformBindings.Count == 0)
            {
                rejectReason = "no Transform bindings (nothing to put on a synthetic bone)";
                return false;
            }

            var nonRoot = transformBindings.Where(b => b.path != 0).Select(b => b.path).Distinct().ToList();
            if (nonRoot.Count > 0)
            {
                rejectReason =
                    $"has Transform path hash(es) other than root (0): {string.Join(", ", nonRoot)}. " +
                    "Refusing — would invent unnamed bones at origin. Use Animator + clips with a real skeleton instead.";
                return false;
            }

            return true;
        }

        private static bool TryValidateLegacyRootOnly(AnimationClip clip, out string rejectReason)
        {
            rejectReason = null;
            var paths = new HashSet<string>(StringComparer.Ordinal);

            void Collect(string path)
            {
                if (path != null)
                    paths.Add(path);
            }

            if (clip.m_CompressedRotationCurves != null)
                foreach (var c in clip.m_CompressedRotationCurves) Collect(c.m_Path);
            if (clip.m_RotationCurves != null)
                foreach (var c in clip.m_RotationCurves) Collect(c.path);
            if (clip.m_PositionCurves != null)
                foreach (var c in clip.m_PositionCurves) Collect(c.path);
            if (clip.m_ScaleCurves != null)
                foreach (var c in clip.m_ScaleCurves) Collect(c.path);
            if (clip.m_EulerCurves != null)
                foreach (var c in clip.m_EulerCurves) Collect(c.path);

            if (paths.Count == 0)
            {
                rejectReason = "legacy clip has no TRS curves";
                return false;
            }

            var nonRoot = paths.Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (nonRoot.Count > 0)
            {
                rejectReason =
                    $"legacy clip animates non-root path(s): {string.Join(", ", nonRoot)}. " +
                    "Refusing — would invent bones at origin.";
                return false;
            }

            return true;
        }

        private static GenericBinding[] GetBindings(AnimationClip clip)
        {
            if (clip.m_ClipBindingConstant?.genericBindings != null &&
                clip.m_ClipBindingConstant.genericBindings.Length > 0)
                return clip.m_ClipBindingConstant.genericBindings;

            var muscle = clip.m_MuscleClip?.m_Clip;
            if (muscle?.m_Binding != null)
                return muscle.ConvertValueArrayToGenericBinding()?.genericBindings;

            return null;
        }

        private void ConvertClip(AnimationClip animationClip)
        {
            var iAnim = new ImportedKeyframedAnimation
            {
                Name = UniqueAnimName(animationClip.m_Name),
                SampleRate = animationClip.m_SampleRate,
                TrackList = new List<ImportedAnimationKeyframedTrack>()
            };
            AnimationList.Add(iAnim);

            if (animationClip.m_Legacy)
                ConvertLegacy(animationClip, iAnim);
            else
                ConvertMuscle(animationClip, iAnim);

            CopyAnimationEvents(animationClip, iAnim);
        }

        private string UniqueAnimName(string name)
        {
            if (string.IsNullOrEmpty(name))
                name = "AnimationClip";
            if (!AnimationList.Exists(x => x.Name == name))
                return name;
            for (int i = 1; ; i++)
            {
                var fix = name + $"_{i}";
                if (!AnimationList.Exists(x => x.Name == fix))
                    return fix;
            }
        }

        private static void ConvertLegacy(AnimationClip animationClip, ImportedKeyframedAnimation iAnim)
        {
            var track = iAnim.FindTrack(SyntheticBoneName);

            if (animationClip.m_CompressedRotationCurves != null)
            {
                foreach (var m_CompressedRotationCurve in animationClip.m_CompressedRotationCurves)
                {
                    if (!string.IsNullOrEmpty(m_CompressedRotationCurve.m_Path))
                        continue;
                    var numKeys = m_CompressedRotationCurve.m_Times.m_NumItems;
                    var data = m_CompressedRotationCurve.m_Times.UnpackInts();
                    var times = new float[numKeys];
                    int t = 0;
                    for (int i = 0; i < numKeys; i++)
                    {
                        t += data[i];
                        times[i] = t * 0.01f;
                    }
                    var quats = m_CompressedRotationCurve.m_Values.UnpackQuats();
                    for (int i = 0; i < numKeys; i++)
                    {
                        var quat = quats[i];
                        var value = Fbx.QuaternionToEuler(new Quaternion(quat.X, -quat.Y, -quat.Z, quat.W));
                        track.Rotations.Add(new ImportedKeyframe<Vector3>(times[i], value));
                    }
                }
            }

            if (animationClip.m_RotationCurves != null)
            {
                foreach (var m_RotationCurve in animationClip.m_RotationCurves)
                {
                    if (!string.IsNullOrEmpty(m_RotationCurve.path)) continue;
                    foreach (var m_Curve in m_RotationCurve.curve.m_Curve)
                    {
                        var value = Fbx.QuaternionToEuler(new Quaternion(m_Curve.value.X, -m_Curve.value.Y, -m_Curve.value.Z, m_Curve.value.W));
                        track.Rotations.Add(new ImportedKeyframe<Vector3>(m_Curve.time, value));
                    }
                }
            }

            if (animationClip.m_PositionCurves != null)
            {
                foreach (var m_PositionCurve in animationClip.m_PositionCurves)
                {
                    if (!string.IsNullOrEmpty(m_PositionCurve.path)) continue;
                    foreach (var m_Curve in m_PositionCurve.curve.m_Curve)
                    {
                        track.Translations.Add(new ImportedKeyframe<Vector3>(m_Curve.time,
                            new Vector3(-m_Curve.value.X, m_Curve.value.Y, m_Curve.value.Z)));
                    }
                }
            }

            if (animationClip.m_ScaleCurves != null)
            {
                foreach (var m_ScaleCurve in animationClip.m_ScaleCurves)
                {
                    if (!string.IsNullOrEmpty(m_ScaleCurve.path)) continue;
                    foreach (var m_Curve in m_ScaleCurve.curve.m_Curve)
                    {
                        track.Scalings.Add(new ImportedKeyframe<Vector3>(m_Curve.time,
                            new Vector3(m_Curve.value.X, m_Curve.value.Y, m_Curve.value.Z)));
                    }
                }
            }

            if (animationClip.m_EulerCurves != null)
            {
                foreach (var m_EulerCurve in animationClip.m_EulerCurves)
                {
                    if (!string.IsNullOrEmpty(m_EulerCurve.path)) continue;
                    foreach (var m_Curve in m_EulerCurve.curve.m_Curve)
                    {
                        track.Rotations.Add(new ImportedKeyframe<Vector3>(m_Curve.time,
                            new Vector3(m_Curve.value.X, -m_Curve.value.Y, -m_Curve.value.Z)));
                    }
                }
            }
        }

        private static void ConvertMuscle(AnimationClip animationClip, ImportedKeyframedAnimation iAnim)
        {
            if (animationClip.m_MuscleClip?.m_Clip == null)
                throw new InvalidOperationException($"Clip '{animationClip.m_Name}' has no muscle clip data.");

            var m_Clip = animationClip.m_MuscleClip.m_Clip;
            var streamedFrames = m_Clip.m_StreamedClip.ReadData();
            var m_ClipBindingConstant = animationClip.m_ClipBindingConstant;
            if (m_ClipBindingConstant?.genericBindings == null || m_ClipBindingConstant.genericBindings.Length == 0)
            {
                var rebuilt = m_Clip.ConvertValueArrayToGenericBinding();
                if (rebuilt?.genericBindings != null && rebuilt.genericBindings.Length > 0)
                    m_ClipBindingConstant = rebuilt;
                else
                    m_ClipBindingConstant = m_ClipBindingConstant ?? new AnimationClipBindingConstant
                    {
                        genericBindings = Array.Empty<GenericBinding>(),
                        pptrCurveMapping = Array.Empty<PPtr<Object>>()
                    };
            }

            for (int frameIndex = 1; frameIndex < streamedFrames.Count - 1; frameIndex++)
            {
                var frame = streamedFrames[frameIndex];
                var streamedValues = frame.keyList.Select(x => x.value).ToArray();
                for (int curveIndex = 0; curveIndex < frame.keyList.Length;)
                {
                    ReadCurveData(iAnim, m_ClipBindingConstant, frame.keyList[curveIndex].index, frame.time, streamedValues, 0, ref curveIndex);
                }
            }

            var m_DenseClip = m_Clip.m_DenseClip;
            var streamCount = m_Clip.m_StreamedClip.curveCount;
            for (int frameIndex = 0; frameIndex < m_DenseClip.m_FrameCount; frameIndex++)
            {
                var time = m_DenseClip.m_BeginTime + frameIndex / m_DenseClip.m_SampleRate;
                var frameOffset = frameIndex * m_DenseClip.m_CurveCount;
                for (int curveIndex = 0; curveIndex < m_DenseClip.m_CurveCount;)
                {
                    var index = streamCount + curveIndex;
                    ReadCurveData(iAnim, m_ClipBindingConstant, (int)index, time, m_DenseClip.m_SampleArray, (int)frameOffset, ref curveIndex);
                }
            }

            if (m_Clip.m_ConstantClip != null)
            {
                var m_ConstantClip = m_Clip.m_ConstantClip;
                var denseCount = m_Clip.m_DenseClip.m_CurveCount;
                var time2 = 0.0f;
                for (int i = 0; i < 2; i++)
                {
                    for (int curveIndex = 0; curveIndex < m_ConstantClip.data.Length;)
                    {
                        var index = streamCount + denseCount + curveIndex;
                        ReadCurveData(iAnim, m_ClipBindingConstant, (int)index, time2, m_ConstantClip.data, 0, ref curveIndex);
                    }
                    time2 = animationClip.m_MuscleClip.m_StopTime;
                }
            }
        }

        private static void ReadCurveData(ImportedKeyframedAnimation iAnim, AnimationClipBindingConstant m_ClipBindingConstant,
            int index, float time, float[] data, int offset, ref int curveIndex)
        {
            var binding = m_ClipBindingConstant?.FindBinding(index);
            if (binding == null)
            {
                curveIndex++;
                return;
            }
            if (binding.typeID != ClassIDType.Transform || binding.path != 0)
            {
                // Skip non-root / non-transform (attribute width unknown for arbitrary types — advance 1 like ModelConverter default)
                if (binding.typeID == ClassIDType.Transform)
                {
                    switch (binding.attribute)
                    {
                        case 1: case 3: case 4: curveIndex += 3; break;
                        case 2: curveIndex += 4; break;
                        default: curveIndex++; break;
                    }
                }
                else
                {
                    curveIndex++;
                }
                return;
            }

            var track = iAnim.FindTrack(SyntheticBoneName);
            switch (binding.attribute)
            {
                case 1:
                    track.Translations.Add(new ImportedKeyframe<Vector3>(time, new Vector3(
                        -data[curveIndex++ + offset],
                        data[curveIndex++ + offset],
                        data[curveIndex++ + offset])));
                    break;
                case 2:
                    var value = Fbx.QuaternionToEuler(new Quaternion(
                        data[curveIndex++ + offset],
                        -data[curveIndex++ + offset],
                        -data[curveIndex++ + offset],
                        data[curveIndex++ + offset]));
                    track.Rotations.Add(new ImportedKeyframe<Vector3>(time, value));
                    break;
                case 3:
                    track.Scalings.Add(new ImportedKeyframe<Vector3>(time, new Vector3(
                        data[curveIndex++ + offset],
                        data[curveIndex++ + offset],
                        data[curveIndex++ + offset])));
                    break;
                case 4:
                    track.Rotations.Add(new ImportedKeyframe<Vector3>(time, new Vector3(
                        data[curveIndex++ + offset],
                        -data[curveIndex++ + offset],
                        -data[curveIndex++ + offset])));
                    break;
                default:
                    curveIndex++;
                    break;
            }
        }

        private static void CopyAnimationEvents(AnimationClip clip, ImportedKeyframedAnimation iAnim)
        {
            if (clip?.m_Events == null || clip.m_Events.Length == 0)
                return;

            iAnim.Events = new List<ImportedAnimationEvent>(clip.m_Events.Length);
            foreach (var e in clip.m_Events)
            {
                var name = e.functionName ?? "";
                if (!string.IsNullOrEmpty(e.data))
                    name = string.IsNullOrEmpty(name) ? e.data : name + ":" + e.data;
                if (string.IsNullOrEmpty(name))
                    name = "event";
                iAnim.Events.Add(new ImportedAnimationEvent { time = e.time, Name = name });
            }
        }
    }
}
