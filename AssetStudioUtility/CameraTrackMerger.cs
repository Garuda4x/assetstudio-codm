using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetStudio
{
    /// <summary>
    /// CoD Mobile ships the first-person camera motion as a separate clip per animation
    /// (MeleeWeapon_001_Knife_iw6_1P_M_FireLeft_Camera next to ..._M_FireLeft). Those clips animate the
    /// Animator root (path hash 0), which has no bone in the rig, so on their own they export as empty
    /// animations. This merges each "_Camera" clip into its partner animation as a track on a
    /// synthetic <c>Camera</c> bone parented to the rig root, so one Cast file carries the weapon,
    /// the arms and the camera together. Camera clips without a partner keep their own animation,
    /// also on the Camera bone.
    /// </summary>
    public static class CameraTrackMerger
    {
        public const string CameraBoneName = "Camera";
        private const string Suffix = "_Camera";

        public static bool IsCameraClipName(string name)
        {
            return !string.IsNullOrEmpty(name) && name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);
        }

        public static string PartnerName(string cameraClipName)
        {
            return IsCameraClipName(cameraClipName) ? cameraClipName.Substring(0, cameraClipName.Length - Suffix.Length) : cameraClipName;
        }

        /// <summary>
        /// Merge camera clips into their partner animations. Returns the number merged. With
        /// <paramref name="keepStandalone"/> camera clips without an animated partner are kept as
        /// their own animation on the Camera bone instead of being dropped.
        /// </summary>
        public static int Merge(IImported imported, Action<string> log = null, bool keepStandalone = false)
        {
            if (imported?.RootFrame == null || imported.AnimationList == null)
                return 0;
            var cameras = imported.AnimationList.Where(a => IsCameraClipName(a.Name)).ToList();
            if (cameras.Count == 0)
                return 0;

            var root = imported.RootFrame;
            var camFrame = FindChild(root, CameraBoneName);
            if (camFrame == null)
            {
                camFrame = new ImportedFrame
                {
                    Name = CameraBoneName,
                    LocalPosition = Vector3.Zero,
                    LocalRotation = Vector3.Zero,
                    LocalScale = Vector3.One,
                };
                root.AddChild(camFrame);
            }
            var camPath = camFrame.Path;

            var merged = 0;
            var standalone = 0;
            var empty = 0;
            var dropped = 0;
            foreach (var cam in cameras)
            {
                var rootTrack = cam.TrackList?.FirstOrDefault(t => IsRootPath(t.Path, root));
                if (rootTrack == null || (rootTrack.Rotations.Count == 0 && rootTrack.Translations.Count == 0))
                {
                    empty++;
                    log?.Invoke($"[Camera] '{cam.Name}' carries no root curves (hollow CODM clip not decoded); left out");
                    imported.AnimationList.Remove(cam);
                    continue;
                }

                var track = new ImportedAnimationKeyframedTrack { Path = camPath };
                track.Translations.AddRange(rootTrack.Translations);
                track.Rotations.AddRange(rootTrack.Rotations);
                track.Scalings.AddRange(rootTrack.Scalings);

                var partnerName = PartnerName(cam.Name);
                var partner = imported.AnimationList.FirstOrDefault(a => a != cam && string.Equals(a.Name, partnerName, StringComparison.OrdinalIgnoreCase));
                // only rigs that the partner animation actually drives (the first-person arms) get the camera;
                // on third-person / UI rigs the partner has no keyed bones and the camera track is meaningless
                if (partner != null && HasKeyedBoneTrack(partner, root))
                {
                    partner.TrackList.RemoveAll(t => t.Path == camPath);
                    partner.TrackList.Add(track);
                    imported.AnimationList.Remove(cam);
                    merged++;
                }
                else if (keepStandalone)
                {
                    cam.TrackList.Remove(rootTrack);
                    cam.TrackList.RemoveAll(t => t.Path == camPath);
                    cam.TrackList.Add(track);
                    standalone++;
                }
                else
                {
                    imported.AnimationList.Remove(cam);
                    dropped++;
                }
            }
            if (merged == 0 && standalone == 0 && camFrame.Count == 0)
                root.Remove(camFrame); // nothing landed on it: keep the rig unchanged
            log?.Invoke($"[Camera] {merged} camera clip(s) merged into their animations on bone '{CameraBoneName}', {standalone} standalone, {dropped} without an animated partner on this rig, {empty} empty");
            return merged;
        }

        /// <summary>True when the animation keys at least one real bone of this rig (not the root, not the camera).</summary>
        private static bool HasKeyedBoneTrack(ImportedKeyframedAnimation anim, ImportedFrame root)
        {
            if (anim.TrackList == null) return false;
            foreach (var t in anim.TrackList)
            {
                if (IsRootPath(t.Path, root)) continue;
                if (t.Path != null && t.Path.EndsWith("/" + CameraBoneName, StringComparison.Ordinal)) continue;
                if (t.Rotations.Count > 0 || t.Translations.Count > 0 || t.Scalings.Count > 0)
                    return true;
            }
            return false;
        }

        private static bool IsRootPath(string path, ImportedFrame root)
        {
            return string.IsNullOrEmpty(path) || path == root.Path || path == root.Name;
        }

        private static ImportedFrame FindChild(ImportedFrame frame, string name)
        {
            for (var i = 0; i < frame.Count; i++)
                if (string.Equals(frame[i].Name, name, StringComparison.Ordinal))
                    return frame[i];
            return null;
        }
    }
}
