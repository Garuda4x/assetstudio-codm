# CoD Mobile engine format notes

Everything below differs from stock Unity 5.6.4 and is handled in the core reader. The verbose
per-field logs that were used to find these layouts are still in the code behind
`CodmDiagnostics.Enabled` (`"Diagnostics": true` in `codm.config.json`, or CODM menu > Reader diagnostics).

## Serialized file header

- The Unity version string contains control characters (`5.6.4p4` plus junk bytes). `SerializedFile.SetVersion`
  strips `[\x00-\x1F\x7F]` and caps the parsed version at 4 parts.
- `AssetsManager.ReadAssets` wraps `AnimationClip`, `AnimatorController` and `SkinnedMeshRenderer` in
  try/catch; a failed object becomes a bare `Object` stub (the GUI shows `[PARSE FAILED]`).

## Texture2D

Two differences after the `m_IsReadable` booleans:

1. no 4-byte alignment;
2. two extra `int32` fields before `m_ImageCount` (one is constantly 9, the other a per-texture hash).

`Texture2D` peeks at the candidate `m_ImageCount` / `m_TextureDimension` pair at the stock position,
after skipping 8 bytes, and after "no align + 8", and takes the first plausible one
(`1 <= imageCount <= 6`, `-1 <= dimension <= 6`). Texture data is streamed from the bundle's `.resS`.
DXT1, DXT5 and BC7 are confirmed.

## Renderer / SkinnedMeshRenderer

- `Renderer`: `m_SortingLayer` (SInt16, stock 5.6+) is **absent**.
- `SkinnedMeshRenderer`: `m_Quality` is absent (read only for versions below 5.4).

## Mesh

`Mesh.m_CompressedMesh` is public so the exporters can inspect it. Bind poses come from
`Mesh.m_BindPose`, weights from `Mesh.m_Skin`, the skeleton from the `Avatar`
(`m_AvatarSkeleton`, `m_DefaultPose`, bone path CRC32 lookup).

Effect meshes use vertex channel formats from the Unity 2017 enum (`UNorm16 = 5` and up) although the
file says 5.6; `MeshHelper.ToVertexFormat` falls through to the 2017 mapping for values the 5.x enum
does not have.

## ParticleSystemRenderer

Stock AssetStudio does not parse it. A minimal `ParticleSystemRenderer : Renderer` reads only the
renderer base so the materials (and through them the textures) of an effect can be followed.

## AnimatorController / AnimatorOverrideController

Weapon bundles ship `AnimatorOverrideController`s whose `m_Clips[].m_OverrideClip` point at the
clips of one variant (1P, 3P, UI). The shared `AnimatorController`s live in
`cod_models/weapons/animationcontrollers`. `AnimatorController` parsing is tolerant of the CODM
layout and soft-fails instead of allocating garbage.

## AnimationClip

### ClipMuscleConstant

Stock Unity reads a full `HumanPose` (with look-at and goal arrays) followed by foot xforms. CoD
Mobile writes **two `xform`s** (delta root pose and start pose), then `m_AverageSpeed`, then the
`Clip`. Reading the stock layout here allocates gigabytes and dies (`IsCodmMuscleLayout`).

### Layout after the stock Clip

```
Clip (StreamedClip, DenseClip, ConstantClip)      stock
prefix            ~0xC00 bytes, zeros and 1.0f runs
quantized blocks  repeated:
                    u32 numChannels
                    f32 min, f32 max
                    u32 flag (= 3)
                    u32 nb
                    byte[nb * 4]        uint16 samples, dequantised min..max, frames x channels
trailer           ~2.6 KB (0xFF runs, a 0xA1 marker)
ClipMuscleConstant trailing fields
AnimationClipBindingConstant               found by scanning for a plausible binding list
AnimationEvents
```

- The sum of `numChannels` over the blocks equals the `DenseClip` "extra" field; the bindings' curve
  index space is `streamed + dense + constant`.
- `CodmQuantizedDense` decodes the blocks into `DenseClip.m_SampleArray`, keeps the raw samples
  (`m_CodmRawSampleArray`, block mins/maxs/sizes) and chooses where the dense window sits inside the
  binding index space by scoring candidate offsets against the binding dimensions
  (`posRot` / `scale` / `dist` in the log). `TrySeekToAnimationClipBindingConstant` prefers a
  non-empty binding list; empty lists are only accepted when the muscle clip has no curve data.
- The 3-channel block is typically `Bone01.P` (weapon position), the large block the rotations of the
  whole arm chain. `m_Codm3chSpanFactor` and `CodmBone01PosDecode` are A/B switches for alternative
  position decodes; stock linear dequantisation is the default.

### Idle-relative bake (`CodmDenseIdleRelative`)

Hollow Equip / Inspect clips store deltas, not absolute poses, and the absolute idle pose is not in the
blob. At export the dense samples are rebaked relative to an idle reference:

| `CodmBakeMode` | Output |
|---|---|
| `Absolute` | quantized dense used as is |
| `IdleRelativeEnd` (default) | `idle + (dense[f] - dense[last])`, clip ends on idle |
| `DeltaFromIdle` | `idle + dense[f]` |
| `IdleRelativeStart` | `idle + (dense[f] - dense[0])` |
| `Static` | idle on every frame (control) |

Idle sources, in order: Animator/Avatar local TRS (Unity WriteDefaults, `PreferWriteDefaultsIdle`),
a `Fire*` streamed clip from the same selection (`FindIdleReference`), and the `ConstantClip` as last
resort. `ModelConverter` picks this automatically ("idle=auto" in the log).

### ConstantClip layout

For CODM quantized clips the `ConstantClip` is a rest-pose float array laid out from curve index 0
(it matches the FireJump idle), not after streamed+dense as in stock Unity. `ModelConverter` wires it
accordingly and never writes constant keys over dense channels (Equip must start off-idle).

### Weapon attach bones

- Hollow clips bind `Bip01/Bone_RightHand/Bone01/Bone02` (CRC 0x4714F6A0) but the shipped 1P prefab
  has no `Bone02` transform; `ModelConverter` injects an animation-only `Bone02` under `Bone01`.
- Karambit-style skins keep `Bone_RightHand` static at a 90 degree Z rest while the clips are authored
  in knife space; `CodmBone01BrhCompensate` can rewrite `Bone01` by the inverse rest rotation.
- `CodmWeaponHandFollow` (off by default) re-anchors `Bone01` to `b_RightHand` when quantized clips
  under-drive the weapon.

### Camera clips

Every first-person animation has a sibling `<name>_Camera` clip (`..._1P_M_FireLeft_Camera`) that
animates the Animator root (path hash 0, three Transform bindings): the camera motion. The rig has no
bone for the root, so on its own such a clip exports as an empty animation.

`CameraTrackMerger` runs before every Cast/FBX/GLB write (weapon export, Animator export, GameObject
export): it adds a `Camera` bone under the rig root and moves each camera clip's root curves onto it
inside the partner animation, so one `FireLeft.cast` carries arms, weapon and camera. Only rigs the
partner animation actually drives (the first-person arms) get the bone; on third-person and UI
prefabs the camera clips are dropped. Camera clips whose motion sits only
in the undecoded CODM dense blob (`streamed curves = 0`, e.g. the knife's `Fire_Camera`) have no
curves and are dropped with a log line; `FireLeft_Camera` / `FireRight_Camera` / `ProneCrawlLT_Camera`
carry streamed curves and merge.

The older `OrphanAnimationClipConverter` path (Export > "EXCEPTION: Orphan/Camera clips") still
exists for inspecting a camera clip alone.

### Animation events (notetracks)

The clips carry Unity `AnimationEvent`s and CoD Mobile keys its weapon audio on them. Two kinds:

- semantic triggers with an empty data string: `PlayFirstAttackSound`, `PlaySecondAttackSound`,
  `PlayStabSound`, `PlayRaiseSound`, `PlayPutDownSound`, `PlayInspectionSound1/2`, `PlayClipInSound`,
  `PlayClipOutSound`, `PlayBoltBackAndReleaseSound`, `FiringEvent`. The game resolves them to the
  weapon's Wwise events through its (encrypted) sound config;
- `PlayEventSound` / `PlayEventSoundAnyCase` with ids in `data`: a plain number is a Wwise event id
  (`347825172` is an event in `MP_GEN.bnk`); the 64-bit pairs `562950372539117~1125900325960429|8`
  are config-table ids (tag in the high bits, the same low 32 bits in both halves), not Wwise hashes,
  so they cannot be followed into a bank without the tables.

`ModelConverter` copies the events and `CastExporter` writes them as Cast notification tracks, so the
`.cast` animations already carry the triggers with their frame. `AnimationSoundMap` additionally writes
`anims/notetracks.txt` per prefab (time, function, decoded Wwise ids with bank, and a "guessed" column
listing the events in the weapon's banks whose recovered name fits the function), and the wems of
directly referenced Wwise events land in `sounds/_from_animation_events/`.

## Export

- `.cast` (dtzxporter Cast) is the primary model and animation format: Z-up, model-space vertices,
  skeleton, materials with diffuse texture reference, one `.cast` per animation under `anims/`.
  `CastExporter.SkipEmptyAnimations` drops clips that bind nothing on the chosen skeleton.
- GLB (SharpGLTF, skinned, PBR, pruned skeleton) and FBX (stock ModelExporter) are opt-in in Export Options.
- `CodmExportService` exports one Cast model per `Animator` with the clips its override controller
  references (fallback: every clip in the same file), one per renderer-only root, every `Texture2D`,
  and the wems of the linked banks.
