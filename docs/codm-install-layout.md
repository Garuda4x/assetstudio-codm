# CoD Mobile install layout

> **Version note.** This page describes the **1.9.55** install (patch 55), which the tooling reads.
> On 2026-10-24 that install was archived to `Q:\CODMGameApps_Archive` and `Q:\CODMGameApps` was
> updated to 1.9.57, which packs all bundles into QtsVFS containers and renames `Puffer` /
> `Extract\55` to `PufferQts` / `ExtractQts\57`. The tooling does not read 1.9.57 yet; the findings
> are in [progress.md](progress.md) (2026-10-24 entry).

Install root: `Q:\CODMGameApps\CN\emulator\Extract\CODM` (CN build, PC emulator package, patch 55,
Unity `5.6.4p4` modified, IL2CPP). Read only. Never run, patch or update the game.

## Folders

| Path (under the root) | Contents |
|---|---|
| `CODM.exe` (290 MB), `CODMBase.dll` | the IL2CPP game binary; no `GameAssembly.dll`, no `global-metadata.dat` on disk |
| `CODM_Data\StreamingAssets\AssetBundle` | 13 826 `.pak` (UnityFS bundles) shipped with the install |
| `PersistentData\Puffer\AssetBundle\dynamic\module` | 3 615 `.pak` downloaded by the Puffer patcher, grouped in modules |
| `PersistentData\Extract\55\AssetBundle` | 233 `.pak` from the patch 55 extract |
| `PersistentData\Extract\55\DataTableBytes`, `DataAssetBytes`, `DataPropertyBytes` | encrypted game config tables (106 tables) |
| `CODM_Data\StreamingAssets\Audio\GeneratedSoundBanks\Windows` | Wwise banks and wems shipped with the install |
| `PersistentData\Puffer\Audio\GeneratedSoundBanks\Windows[_HQ]` | downloaded banks and wems, with `Chinese(PRC)` and `English(US)` language folders |
| `CODM_Data\StreamingAssets\0db.bytes` | 197 MB custom archive; entry names use the same cipher (PVS data, more tables) |
| `CODM_Data\resources.assets`, `globalgamemanagers`, `level*` | regular Unity player files, loadable in AssetStudio |
| `cache\Cache\Log\*.ldl`, `CrashSightLog` | game logs, nothing useful for asset mapping |

## The name cipher

Pak files, table files and `0db.bytes` entries use a reversible substitution: 13 letter pairs are
swapped and the case is flipped, 5 digit pairs are swapped, `$` stands for `/` and `!` for `.`.

```
a-p  c-l  e-m  d-j  i-z  n-v  o-u  r-t  s-f  w-x  g-h  b-k  q-y
0-9  1-2  3-4  5-6  7-8
```

The digit swap matters: the weapon ids in decoded pak names are the real game ids, the ones used by
the prefab names inside the bundles and by the Wwise skin banks (`2537.bnk` belongs to
`mainweapon_2537_locusrailgun`). Module names and version buckets decode too (`cn13.0`, `7.2`).

`CodmNameCipher.Decode` / `codm decode`:

```
LUJ_EUJMCF$XMPAUVF$EPZVXMPAUV$EPZVXMPAUV_1648_CULOFTPZCHOV.pak
  -> cod_models/weapons/mainweapon/mainweapon_2537_locusrailgun
LUEEUV!8!1!LUJ_EUJMCF$XMPAUVF$EMCMMXMPAUV$EMCMMXMPAUV_992_BVZSM_ZX5.pak
  -> common.7.2.cod_models/weapons/meleeweapon/meleeweapon_001_knife_iw6
FLMVMF$EPA$LCQ$...$LCQ_FVPBM_AUTR#URGMT@1.pak
  -> scenes/map/cly/.../cly_snake_port#other@1
aTUAMTRQeUJZSQjMFL_.bytes  -> PropertyModifyDesc_.bytes
```

Pak name grammar (raw): `[module!ver!ver!]path[#category][@shard].pak`

- `module` only exists for Puffer downloads: `common`, `normal_weapon_cn13.0`, `normal_baseweapon_cn6.0`,
  `normal_role_cn13.0`, `mtx_meleeweapon_others_01`... The literal `.` inside a module name is a real
  dot; `!` are the encoded ones. The version is always `7.2` in this install.
- `#category`: `tex`, `other`, `prefabs`, `fallback` for bundles split by content type.
- `@n`: shard index for bundles split into several files.

`PakName.Parse` returns module, version, decoded path, category and shard.

## What the paks contain

| Decoded prefix | Paks | Notes |
|---|---|---|
| `cod_models/weapons/mainweapon/mainweapon_<id>_<name>` | 1 104 | 665 ids; the same path exists in streaming and in several modules |
| `cod_models/weapons/component/{sight,mag,sto,grip,bar,for,rai,muz,sra,pendant}/...` | 2 018 | attachments; only `sight/sig9x_<base>` is per weapon, the rest are version buckets |
| `cod_models/weapons/{throwweapon,meleeweapon,assiweapon,specialweapon}` | 312 / 174 / 137 / 139 | |
| `cod_models/weapons/{camouflage,animations,animationcontrollers,fallback}` | 134 / 166 / 13 / 8 | shared |
| `cod_models/avatar`, `cod_models/br`, `cod_models/vehicles`, `cod_models/cly` | | characters, BR, vehicles, zombies |
| `cod_effects/particles/weapon/{gunfire,hit,trail,bulletshell,explosion,show,attack,other}` | 10 | generic weapon FX |
| `cod_effects/efx_<n>v<m>/...` | | per-skin FX, e.g. `efx_419v1/efx_vfw/locus_ca91` |
| `cod_prefabs/scriptableobject@0..9` | 10 | 1 100+ `MonoBehaviour` configs (GAS ability tasks, individuation maps) |
| `scenes/map/...`, `_s_br/...`, `_s_cly/...` | ~12 000 | maps and streamed map chunks |
| `textures/ui_icon`, `cod_prefabs/ngui` | | UI |

### Characters

`cod_models/avatar/<skin>` holds one folder per character skin (`c_f_alias`, `c_m_price_dead_of_night`,
`seal6_005`); `avatar/pvebot/*` and `avatar/vfw/*` hold bot and event variants. Download copies sit in
the `normal_role_cn*` modules. A skin bundle carries separate prefabs for the body (`_BR_3P`), the UI
pose and the first-person arms (`_1P`); the arms' meshes and animations come from shared bundles through
the usual dependency loading. `cod_models/cly` and `cod_models/br` are pickups, bags and boxes, not
characters.

### Attachments

`cod_models/weapons/component/<type>/<bucket>` paks (`mag/13.0`, `bar/5.0`, `sto/5.0`...) each hold
parts for several weapons. The asset folders inside name the owner (`mag01_svdpurplesoul`,
`bar01_locus`, `bip01_arctic50`), so `ContainerIndex` reads every attachment bundle's container
paths once (cached) and `WeaponCatalog.LinkComponentsByContainers` links the paks to the weapons whose
name appears. Sights are named per weapon in the pak name itself (`sight/sig01_locus`).

### Weapons: base, skins, modules

A weapon folder such as `mainweapon_2537_locusrailgun` is one skin. The mesh lives in the **base**
weapon folder (`mainweapon_104_locus`), shipped by `normal_baseweapon_*` modules. Skins start with the
base name, so the catalog links `locusrailgun -> locus` by longest prefix (cosmetic prefixes
`legendary`, `mythic`, `baseversion`, `epic`, version suffixes `v81` and underscores are ignored).
Chains exist: `vectork10redgundouble -> vectork10redgun -> vectork10`.

Every module copy of a path is a different bundle (different CAB) holding a slice of the weapon:
a 2 KB `normal_weapon_cn9.0` pak may only carry an `AssetBundle` object, `common` a texture, the
streaming copy the prefabs. An export loads all copies; `AssetsManager` skips CABs it has already
seen by name. Prefabs that exist in several copies are exported once (the copy with the most clips).

### Bundle dependencies

Weapon bundles do not contain their animations. The prefab's `AnimatorOverrideController` lives in a
small controller bundle and its clips in the shared packs (`cod_models/weapons/animations/1p`, `br`,
`unarmed`...), referenced through `SerializedFile.m_Externals` by CAB name. `CabIndex` maps every CAB
name to its pak by reading only bundle headers (`cache/cab-index.json`); `DependencyResolver` loads
the referenced paks in rounds (animations, controllers and avatars first; shaders, effects and scenes
are skipped). A few skins reference controller bundles that are not on disk; they reuse the base
weapon's clips. Third-person prefabs have no controller at all (assigned at runtime).

## Config tables (encrypted)

`PersistentData\Extract\55\DataTableBytes\*.bytes`, names decode to among others:

```
WeaponAsset  WeaponConf  WeaponSkin  WeaponCamos  WeaponSoundConfigs  WeaponPartAssetGroup
WeaponPartCompose  WeaponBlueprintConf  WeaponSocketConfig  LegendaryWeaponEffect
MythicWeaponLevelConf  ItemResource  ItemInventory  PropertyModifyDesc  SpecialItemResource
```

The contents are encrypted. It is not a repeating XOR (per-position statistics and a brute force over
4-byte keys found nothing; the high 16 bits of each dword stay near-constant per file, so a short-key
stream cipher is likely). Decrypting them needs static analysis of `CODM.exe`, whose IL2CPP metadata
is itself encrypted (no Wwise names or C# strings among the exe's plain strings). They are the
authoritative weapon -> model / sound / FX mapping; until then the tooling derives relations from
names. See [progress.md](progress.md) for what was tried.

## Wwise audio

- Bank version 135, no `STID` section, so bank files carry no names other than their file name.
- 2 187 distinct bank names in 3 819 files (copies per language and quality tree).
- **Numeric banks** `249.bnk`, `249d.bnk`, `249p3.bnk`, `80a.bnk`: custom sound sets of a weapon
  skin or base weapon, numbered by the real weapon id; letters are variants, `p3` is a third-person set.
  A letter bank belongs to one skin of that base weapon: `200g` / `200gp3` are the DL Q33 Black Noir's
  (`mainweapon_72022`, events `play_dlq33blacknoir_*`), and the plain `200` is the base DL Q33's. The
  skin's events are named after the skin without its `BLP` suffix, which the bank linking does not
  try yet, so weapon exports miss these banks (`codm wems 200g` extracts one by hand). How animation
  triggers pick events: [codm-formats.md](codm-formats.md), "Animation events".
- **Class banks** `wpn_ar`, `wpn_smg`, `wpn_sniper`, `wpn_lmg`, `wpn_shotgun`, `wpn_pistol`,
  `wpn_melee`, `wpn_launcher`: base weapon sounds. Which one a weapon uses is not derivable from the
  files (skin banks borrow handling sounds from several of them), so it comes from the weapon class
  list (`WeaponClassMap`: built-in seed plus `weapon-classes.json`). `247.bnk` is a shared bank many
  weapons borrow from.
- Others: `Music_RedGun_<weapon>`, `vox_*` (1 100+ voice banks), `Amb_MP_*`, `BR_*`, `CLY_*`, `UI_*`.
- Sound objects in a bank reference wem ids that may be embedded in **another** bank. A global
  media index over every bank's `DIDX` plus the loose `<id>.wem` files resolves 99.95 % of them.
- Event and action ids are FNV-1 hashes of their Wwise names; the names are not shipped, but the
  weapon events follow patterns that can be regenerated: `play_wpn_<weapon>_fire_1p` / `_3p` / `_3p_br`
  in the weapon's own bank, `play_weapon_<class>_raise_1p`, `play_weapon_sniper_reload_end_3p` in the
  class banks, `stop_*` counterparts. `WwiseNames` hashes candidates per bank and `wwise-bank-tokens.json`
  records which weapon a bank mentions. Bank numbers are **not** the pak ids of the same kind
  (`105.bnk` is the Locus, `mainweapon_104_locus`; `throwweapon_105_cannon_turret` is unrelated), so
  the name match is what links banks to weapons; numeric matching is only a fallback.
- Inside a bank, an event's actions target containers (random/sequence, switch, layer, actor-mixer);
  `WwiseBank.EventSources` walks them down to the wems. The weapon is selected inside class banks through
  switch groups whose value names are still unknown; weapon banks have no such ambiguity.

## Scripts and type information

- Bundles have **no type trees** (`codm typetree <pak>` reports 0), and the engine is IL2CPP with the
  metadata inside the exe, so `MonoBehaviour` payloads can only be read with hand-written layouts.
- `MonoScript` class names are present. Weapon bundles use `WeaponControllerAsset`, `AssetRef`,
  `AnimationEventActionMap`, `CameraAnimation`; the scriptable-object bundles use the `GAS`
  namespace (`*TaskConfig`), `IndividuationMap`, `MeshParticleLODSetting`, `BotVoiceConfig`...
- The `MonoBehaviour` blobs in weapon bundles contain no readable strings (references are ids and
  PPtrs), so sounds and FX cannot be read from them without the class layouts.
