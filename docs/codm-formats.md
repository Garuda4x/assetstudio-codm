# CoD Mobile engine format notes

Everything below differs from stock Unity 5.6.4 and is handled in the core reader. The verbose
per-field logs that were used to find these layouts are still in the code behind
`CodmDiagnostics.Enabled` (`"Diagnostics": true` in `codm.config.json`, or CODM menu > Reader diagnostics).

## Serialized file header

- The Unity version string contains control characters (`5.6.4p4` plus junk bytes). `SerializedFile.SetVersion`
  strips `[\x00-\x1F\x7F]` and caps the parsed version at 4 parts.
- `AssetsManager.ReadAssets` wraps `AnimationClip`, `AnimatorController` and `SkinnedMeshRenderer` in
  try/catch; a failed object becomes a bare `Object` stub (the GUI shows `[PARSE FAILED]`).

## Serialized file format 18 (1.9.57)

The 1.9.57 bundles write `SerializedFile` version 18 (1.9.55 wrote 17) with three changes, all
confirmed on a rebuilt base-pack bundle:

- after the type-tree flag: `u32 count` and that many NUL-terminated strings `qtc:/split/<md5>.cpak`
  (the chunk files holding this file's external object payloads), then the usual type list;
- object entries are variable length: `i64 pathID, u32 flag`; `flag == 0`: `u32 byteStart, u32 byteSize,
  i32 typeID` (payload in this file's data section, as before); `flag == 1`: `u32 refIndex, u32 offset,
  u32 byteSize, i32 typeID` (payload at `offset` inside chunk `refIndex` of the string list). In the
  probe 4 270 of 10 221 objects (GameObjects, Transforms, MonoBehaviours) were external, spread over
  three chunks whose sizes equal the highest `offset + byteSize` referencing them;
- script types and externals keep the stock layout.

The chunks are pack entries named `<md5>.cpak` (base pack), `dynamic/split/<md5>.cpak` (PufferQts) or
`split/<md5>.cpak` (patch pack). Reading a 1.9.57 bundle therefore means: decompress the bundle,
fetch its chunks, and either feed the reader a chunk resolver or rewrite the file into a stock
version-17 layout with the external payloads appended to the data section (the planned
"normalizer", which also lets `codm unpack` produce bundles the unchanged pipeline reads).
`SerializedFile` only skips the string list (`codmSplitRefs`); `BundleNormalizer` in `AssetStudio.Codm/Vfs`
does the rewrite, see [codm-containers.md](codm-containers.md).

## Texture2D

Two differences after the `m_IsReadable` booleans:

1. no 4-byte alignment;
2. two extra `int32` fields before `m_ImageCount` (one is constantly 9, the other a per-texture hash).

`Texture2D` peeks at the candidate `m_ImageCount` / `m_TextureDimension` pair at the stock position,
after skipping 8 bytes, and after "no align + 8", and takes the first plausible one
(`1 <= imageCount <= 6`, `-1 <= dimension <= 6`). Texture data is streamed from the bundle's `.resS`.
DXT1, DXT5 and BC7 are confirmed.

Alpha: 1.9.55-era textures are DXT1 (opaque) or DXT5 where alpha carries data (glass, `_M_trans`,
camo masks; exported 0-255, colour under alpha 0 kept). The 1.9.57 DL Q33 Black Noir set is BC7 and
opaque in the game data (254-255 from BC7 rounding): an independent BC7 decode (Pillow) of the raw data
matches the exported PNGs bit for bit, alpha included. PNG export is lossless RGBA; `"TextureFormat":
"tga"` writes 32-bit uncompressed TGA instead.

## Renderer / SkinnedMeshRenderer

- `Renderer`: `m_SortingLayer` (SInt16, stock 5.6+) is **absent**.
- `SkinnedMeshRenderer`: `m_Quality` is absent (read only for versions below 5.4).
- `m_Materials` never resolves: the slots are empty or point at path id 0 (or `1 << 32`) of an unrelated
  bundle (an animation pack). The game assigns skin and camo materials at runtime (the base DL Q33
  bundle holds one material per camo). A first-person prefab's SkinnedMeshRenderer can leave `m_Mesh`
  empty too; its `AssetRef` MonoBehaviour names the mesh, and the imported `.fbx` copy next to the
  prefab carries it. Some skins strip it in every copy (DL Q33 Lotus Flames: both `Wea01_..._1P`
  renderers have `m_Mesh` = 0 and no `m_Bones`; the UI's `Enter01_Wea` points at `1 << 40`): the mesh
  is still in the bundle under the renderer's GameObject name, skinned through its `m_BoneNameHashes`,
  and the export takes it by that name when the name is unique in the file.
- Materials follow part + skin names: mesh `Wea01_DLQ33BlackNoirBLP_1P` -> `Wea01_DLQ33BlackNoirBLP_mtl`,
  `DLQ33Black_1P` -> `wea_DLQ33Black_mtl`, `Wea01_DL_Q33_UI_lod0` -> `wea_DLQ33_mtl`; variants for further
  submeshes (`..._mtl_SightPupil`). `_MainTex` is the colour map (`_A`), `_BumpMapPakced` a packed normal
  map, `_MetallicRoughnessMap` (`_M`), `_EmissionMap` (`_E`). Attachments without a material of their
  own share a sheet of the skin, packed into a free region of it: the Black Noir magazine is on the
  `Rai01` sheet, barrel, muzzle and grip on `Wea01`. No MonoBehaviour names the sheet (component
  bundles only carry `AssetRef`), so the export finds it by UV layout (`MaterialMatcher`).
- Channel layout of the 1.9.57 weapon skin sheets (DL Q33 Black Noir, all BC7, alpha unused): `_M` R =
  ambient occlusion, G = metallic mask (hard 0/255 per UV island; glass is 255 with a dark albedo, a
  mirror-glass trick), B = camo/paint mask (0 on glass and decals); `_N` RG = normal XY, B = gloss
  (smoothness: glass averages 183, grain and scratches). Evidence: M.R correlates positively with gloss
  (+0.60) and albedo brightness (+0.58), as AO does; a roughness map would anti-correlate with gloss.
  Material floats name no channels (`_AO_Intensity`, `_GlossMapScale`, `_SmoothnessTextureChannel` 0,
  four-colour camo coefficients `_4ColCoef*`). Older character textures put metallic in `_M` R and AO in G.

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
layout and soft-fails instead of allocating garbage. `codm anims <weapon> --clips` lists each override
pair (base clip -> skin clip).

The controller's state, transition and parameter names can still be read from the raw object: its
table of strings (`m_TOS`) holds entries `uint crc32(name), int length, bytes, align 4`, so a scan that
keeps only strings whose CRC32 matches the preceding uint lists them without false hits. Weapon 1P
controllers (CAB `e4a1bcd10ef44bfdb630fd4109881e7d`):

- `Basic_1P` (pathID 12): older weapons, e.g. the DL Q33 and its skins. Its equip state is
  `GetWeapon` (entry `Inactive -> GetWeapon`), with clips `Empty_MainWeapon_M_Equip`, `Equip_W` and
  `Equip_B` and parameters `QuickEquip`, `IsQuickHand` and `SpecialGetWeapon`. It has **no first-raise
  state**. The DL Q33 Black Noir's override maps five equip states (`Empty_MainWeapon_M_Equip`, the
  M4A1, Electro and drone-tablet equips) to its `Equip` (0.80 s) and `Equip_W` to its `Equip_W`
  (0.60 s); it doesn't override `Equip_B`.
- `Basic_1P_Advanced` (pathID 1226): newer weapons (Bal-27, Static-HV, Cronen Squall). It adds
  `Equip.Equip_First` (entered from `Entry`, parameters `FirstGetWeapon` and `FirstGetWeaponType`),
  with clips `Empty_MainWeapon_M_First_Equip`, `_02` and `_03`, plus `FastEquip`. The weapon's
  `..._1P_M_First_Equip` clip (Cronen Squall: 1.20 s) carries `PlayFirstRaiseSound`.

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

### Weapon attach points (attachments)

The 1P weapon rig carries the attach points under `Bone_RightHand`: `Bar_point` (and
`Bar_pointqiangtou_root`, same place), `Bip_point` (= `For_point`), `Grip_point`, `Mag_point` (under
`Bone_DanJia`, the magazine bone, so it follows the reload), `Rai_point`, `Sight_point` (and `Sight`
with `Center`), `Sto_point`, `Tra_point`, `Pendant_point` (charm), `ShellDropSocket`. Animated weapon
bones (DL Q33 Black Noir): `Bip01`, `Bone_RightHand`, `Bone_shuan`/`Bone_Bolt` (bolt), `Bone_DanJia`,
the rail blades `daopian_*`; the points themselves only carry constant keys. Not in every skin: the
DL Q33 Lotus Flames animates its points (the barrel, rail, grip, stock and bipod fly 15 to 27 cm apart
in the inspection, the sight moves for aiming), has extra ones (`ExBar_point`, `ExSra_point`,
`Sra_point_R`) and spinning ornament bones (`Bone008..035` under `W_root`), and its bolt bones never move.

Clips key bone scale too, for effects: the Lotus Flames hides the magazine with scale 0 while it is off
the gun in the reloads, hides scope parts while aiming, pulses an ornament on firing (up to 1.34), and
builds mirrored halves from bones that hold scale -1 at rest and in every clip (that cancels in
skinning). Formats without a scale channel (IW4 XANIM) lose the first three.

Attachment prefabs come in two kinds, both already in the weapon's frame (cast space, `Bone_RightHand`
at the origin in bind pose):
- skinned (magazine, grip, rail): a copy of the weapon's bone chain (`Bip01/Bone_RightHand/Bone_DanJia`,
  `Rai_point/daopian_*`), the mesh weighted to bones the main rig has; the game maps them by name. The
  copy's rest pose can differ from the main rig's (the magazine's `Bone_RightHand` is zeroed, 0.374
  away); only the main rig's rest matters once rebound.
- static (barrel, muzzle, stock, sight, bipod): a MeshRenderer under a bare root whose position is
  exactly the attach point (barrel root = `Bar_point`); the muzzle chains on the barrel's own
  `Muzzle_point`, its `MuzzleFlashSocket` is the flash. Cast exports bind such meshes rigidly to their
  own node bone.

`tools/blender/codm_assemble_weapon.py` rebinds the skinned parts by name, binds each static part to
the main-rig bone at its root position and grafts the parts' sockets onto the rig.

### Third-person models and LODs

Every part ships `_1P`, `_3P` and `_UI` prefabs. The 3P mesh is the 1P one scaled up, by a factor that
depends on the skin: 10/7 for the DL Q33 Black Noir (barrel, muzzle, grip: size ratio 1.0000 after the
scale), 8/7 for the DL Q33 Lotus Flames (rail 1.0000, stock and magazine within 1 %). Barrel along +X
instead of -Y; the Black Noir 3P root sits 0.019 1P units above the 1P `Bone_RightHand`. All 3P parts are static and already placed: the attach points hang
on the main mesh's node (`Grip_point`, `Mag_point`, `Rai_point`, `Sight_point`, `Sto_point`,
`ShellDropSocket`...), `Bar_point` and `Bip_point` on the rail, the muzzle on the barrel's `Muzzle_point`.
No bolt, magazine or blade bones.

LODs are child nodes `<part>_3P_lod1` / `_lod2` with their own meshes, and only five parts have them:
body (`Wea01`), rail, magazine, sight, bipod. The body's and rail's LOD meshes absorb the parts without
LODs, one sheet per submesh (DL Q33 Black Noir): body `_lod<n>_0` on `Rai01` (stock frame and the body's
`Rai01` pieces), `_1` on `Wea01` (receiver, grip), `_2` on `Sto01`; rail `_0` on `Rai01`, `_1` on `Wea01`
(barrel and muzzle). A world model is therefore LOD0 = every part's base meshes, LOD<n> = the `_lod<n>`
meshes alone: LOD1 lies within 1 cm (IW4 viewmodel scale) of every LOD0 part, LOD2 within 5 cm. Black
Noir triangles: 15.4k / 3.1k / 0.6k.

Sheets of the Black Noir 3P submeshes, matched by position and UV against the user's hand-fixed
viewmodel: body `_1`, the stock's `_0` and bipod `_1` are on `Rai01`, bipod `_0` on `Sto01`; the sight's
3P-only scope mount sits in a free area of `Wea01`, its lens caps take `Glass`. The export's name match
gets these wrong (`_mtl_SightPupil`, `Sto01`, `Wea01` fallback).

Lotus Flames 3P: body `_0` `Wea`, `_1` `Ball`, `_2` `HW01Glass`; its LODs add the stock on `Sto`
(`_lod1_3`, `_lod2_2`); the sight's small submeshes are lens discs and tube on `Glass` (none in 1P); the
overlay's LOD2 carries a piece LOD0 and LOD1 lack (a flame arc under the barrel).

### Scope reticles

A sniper skin's in-scope reticle is not in the weapon's paks but in the shared bundle
`cod_models/weapons/component/sight/crosshair` (148 reticles, one folder per version:
`37.2/texture/material_dlq33blacknoirblp_ssm_a.tga`, material `Material_<skin>_SSM`, shader parameters
`_SightRadius`, `_SightBlur`... for the eyepiece vignette). The sight's `_SightPupil` prefab draws the
scope view; the skin's `Glass_*_A` is only the lens seen from outside (opaque, reticle painted on).

The reticle texture (DL Q33 Black Noir: 1024, BC7) is an atlas whose shapes are in alpha (RGB white,
except black corner marks on the centre cross): one quarter of the ring, centred near the sheet's
bottom-right corner and mirrored about the middle of its last 12 o'clock bar (x = 1009.5) and the sheet's
bottom edge, plus sprites for the 9 o'clock chevron, the centre cross and a light strip. The prefab
`Material_<skin>_SSM_A` has a 25-vertex mesh placing only the centre: the cross (+-0.000642 units) and four
fine lines from the strip (0.000795 to 0.00508 units, bright end inward). The ring and chevrons are laid
out elsewhere (not found); the lens texture shows the finished layout.

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

The prefab's `CameraAnimation` component plays the camera clips, so an override controller never
lists them; a skin's camera clips usually sit in its base weapon's bundle (the DL Q33 Black Noir uses
`MainWeapon_200_DL_Q33_1P_M_ChangeClip_Camera`, path ids 9 and 10 in the component, and the base's
`FireBolt_Camera`, `AimingBolt_Camera`...). The weapon export adds to each rig's clips the camera clip
of the weapon's own bundles (skin and base chain, the rig's own bundle first) whose short name matches
one of its actions; the merger pairs them by short name (`FireBolt_Camera` -> `FireBolt`).

The `Camera` bone rests at the camera's neutral orientation, the first rotation key of the merged clips:
CODM camera clips start 180 deg about Y from identity. Clips that key no camera rotation (the DL Q33's
reload cameras are 6 constant curves, no rotation) stay at that neutral instead of snapping to identity,
and a retarget sync captured at rest lines up. Exports before 2026-10-24 rest the bone at identity.

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

**How the semantic triggers resolve** (worked out on the DL Q33 Black Noir, `mainweapon_72022`, by
hashing candidate names against every event id of the install). Each trigger plays one Wwise event,
picked by action name: the skin's own event when its bank has one, else the class bank's. The trigger
and action names differ (`BoltOpen` -> `boltpull`), so the mapping lives in the encrypted sound config
and has to be read off per weapon family:

| trigger | event | bank |
|---|---|---|
| `PlayRaiseSound` | `play_dlq33blacknoir_raise_1p` | skin `200g` |
| `PlayFirstRaiseSound` (`First_Equip`, newer weapons only) | `play_<skin>_raisefirst_1p` | skin |
| `PlayPlantOKSound` (PutDown) | `play_weapon_sniper_down_1p` (likely) | class `wpn_sniper` |
| `PlayAdsUpSound` / `PlayAdsDownSound` | `play_weapon_sniper_aim_up_1p` / `aim_down_1p` | class |
| `PlayBoltOpenSound` / `PlayBoltCloseSound` | `play_dlq33blacknoir_boltpull_1p` / `boltrelease_1p` | skin |
| `PlayReloadStartSound` | `play_dlq33blacknoir_reloadstart_1p` | skin |
| `PlayClipOutSound` / `PlayClipInSound` | `play_dlq33blacknoir_clipout_1p` / `clipin_1p` | skin |
| `PlayClipOutSound_E` / `PlayClipInSound_E` | `play_dlq33blacknoir_clipout_e_1p` / `clipin_e_1p` | skin |
| `PlayReloadEndSound` | `play_weapon_sniper_reload_end_1p` | class |
| `PlayInspectionSound1` | `play_dlq33blacknoir_inspection_1p` (one 14.9 s track; `stop_` twin) | skin |
| firing (no trigger) | `play_dlq33blacknoir_fire_1p`, `fire_first_1p`, `fire_sup_1p`, `fire_sup_first_1p` | skin |

- Names: `play_<skin sound name>_<action>_1p` / `_3p` in the skin banks (the skin's name without
  its `BLP` suffix), `play_weapon_<class>_<action>_1p` / `_3p` / `_3p_br` in the class banks,
  `play_wpn_<weapon>_fire_1p` in the base weapon's bank (`200.bnk`: the default skin's fire, shell
  drops). Also in `200g`: `raisefirst_1p`, `armsettle_1p`, `kill_sfx`, and four events not named yet
  (two pairs that play byte-identical wavs: 0.74 s and 0.76 s double metal clicks).
- First raise: the weapon sound table has a `FirstRaiseSound` field next to `RaiseSound` (field names in
  `CODM.exe`). Of the weapons and skins with recovered names, 39 ship `raisefirst_1p` / `_3p` and
  `armsettle_1p` / `_3p` events (no `_br` variant): newer weapons (Bal-27, Grav, Static-HV...) and skins
  of older ones (DL Q33 Black Noir, AK117, HVK-30...). Only `First_Equip` clips trigger the first raise
  (see AnimatorController above). On the
  DL Q33 (`Basic_1P`, no first-raise clip) the 2.13 s `raisefirst_1p` is never played; it isn't a BR
  or 3P sound. No trigger plays `armsettle`.
- Fire is layered: the event plays three containers at once (DL Q33 Black Noir: a 4.3 s body, one of
  6; a 2.0 s / 5.6 s tail, one of 12; a 0.2 s mechanism click, one of 12). The `_first` variants have
  their own body and no click; `_sup` is the suppressed set.
- `clipout_1p` also plays a shared event (in 254 banks, 40 wems) switched by surface (switch values
  `glass`, `grass`, `metal`, `sand`, `snow`, `stone`, `wood`, `ice`, `rubber`, `plastic`, `cloth`,
  `gravel` and unnamed others): the magazine landing on the ground. `200.bnk` has the same kind of
  event for the shell drop.
- FNV-1 tells a name's 3P twin without the name: `_1p` and `_3p` differ in one byte before the last
  multiply, so `twin = ((((h ^ 0x70) * P^-1) ^ 0x02) * P) ^ 0x70` (P = 16777619). A 1P id whose twin
  is in the `p3` bank ends in `_1p`; that cuts a search to the part before the suffix.

## Export

- `.cast` (dtzxporter Cast) is the primary model and animation format: Z-up, model-space vertices,
  skeleton, materials with diffuse texture reference, one `.cast` per animation under `anims/`.
  CODM models get their materials from `MaterialMatcher` (see Renderer above); the colour map is written
  once to `textures/` and referenced as `../../textures/<name>.png`.
  `CastExporter.SkipEmptyAnimations` drops clips that bind nothing on the chosen skeleton.
- GLB (SharpGLTF, skinned, PBR, pruned skeleton) and FBX (stock ModelExporter) are opt-in in Export Options.
- `CodmExportService` exports one Cast model per `Animator` with the clips its override controller
  references (fallback: every clip in the same file), one per renderer-only root, every `Texture2D`,
  and the wems of the linked banks.
