# CoD Mobile install layout

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
swapped and the case is flipped; digits stay; `$` stands for `/` and `!` for `.`.

```
a-p  c-l  e-m  d-j  i-z  n-v  o-u  r-t  s-f  w-x  g-h  b-k  q-y
```

`CodmNameCipher.Decode` / `codm decode`:

```
LUJ_EUJMCF$XMPAUVF$EPZVXMPAUV$EPZVXMPAUV_1648_CULOFTPZCHOV.pak
  -> cod_models/weapons/mainweapon/mainweapon_1648_locusrailgun
LUEEUV!8!1!LUJ_EUJMCF$XMPAUVF$EMCMMXMPAUV$EMCMMXMPAUV_992_BVZSM_ZX5.pak
  -> common.8.1.cod_models/weapons/meleeweapon/meleeweapon_992_knife_iw5
FLMVMF$EPA$LCQ$...$LCQ_FVPBM_AUTR#URGMT@1.pak
  -> scenes/map/cly/.../cly_snake_port#other@1
aTUAMTRQeUJZSQjMFL_.bytes  -> PropertyModifyDesc_.bytes
```

Pak name grammar (raw): `[module!ver!ver!]path[#category][@shard].pak`

- `module` only exists for Puffer downloads: `common`, `normal_weapon_cn24.9`, `normal_baseweapon_cn5.9`,
  `normal_role_cn24.9`, `mtx_meleeweapon_others_92`... The literal `.` inside a module name is a real
  dot; `!` are the encoded ones. The version is always `8.1` in this install.
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

### Weapons: base, skins, modules

A weapon folder such as `mainweapon_1648_locusrailgun` is one skin. The mesh lives in the **base**
weapon folder (`mainweapon_293_locus`), shipped by `normal_baseweapon_*` modules. Skins start with the
base name, so the catalog links `locusrailgun -> locus` by longest prefix (cosmetic prefixes
`legendary`, `mythic`, `baseversion`, `epic` and version suffixes `v72` are ignored for matching).
Chains exist: `vectork29redgundouble -> vectork29redgun -> vectork29`.

Every module copy of a path is a different bundle (different CAB) holding a slice of the weapon:
a 2 KB `normal_weapon_cn0.9` pak may only carry an `AssetBundle` object, `common` a texture, the
streaming copy the prefabs and clips. An export loads all copies; `AssetsManager` skips CABs it has
already seen by name.

## Config tables (encrypted)

`PersistentData\Extract\55\DataTableBytes\*.bytes`, names decode to among others:

```
WeaponAsset  WeaponConf  WeaponSkin  WeaponCamos  WeaponSoundConfigs  WeaponPartAssetGroup
WeaponPartCompose  WeaponBlueprintConf  WeaponSocketConfig  LegendaryWeaponEffect
MythicWeaponLevelConf  ItemResource  ItemInventory  PropertyModifyDesc  SpecialItemResource
```

The contents are encrypted (high entropy; a 4-byte periodic pattern suggests a short XOR key over an
int32 table, but the key and framing are unknown). Decrypting them would need static analysis of
`CODM.exe`. They are the authoritative weapon -> model / sound / FX mapping; until then the tooling
derives relations from names.

## Wwise audio

- Bank version 135, no `STID` section, so bank files carry no names other than their file name.
- 2 187 distinct bank names in 3 819 files (copies per language and quality tree).
- **Numeric banks** `130.bnk`, `130d.bnk`, `130p3.bnk`, `12888a.bnk`: custom sound sets of a weapon
  skin, numbered by the skin id; letters are variants, `p3` is a third-person set.
- **Class banks** `wpn_ar`, `wpn_smg`, `wpn_sniper`, `wpn_lmg`, `wpn_shotgun`, `wpn_pistol`,
  `wpn_melee`, `wpn_launcher`: base weapon sounds. Skin banks reference media embedded in these.
- Others: `Music_RedGun_<weapon>`, `vox_*` (1 100+ voice banks), `Amb_MP_*`, `BR_*`, `CLY_*`, `UI_*`.
- Sound objects in a bank reference wem ids that may be embedded in **another** bank. A global
  media index over every bank's `DIDX` plus the loose `<id>.wem` files resolves 99.95 % of them.
- Event and action ids are FNV-1 hashes of their Wwise names; the names are not shipped.

## Scripts and type information

- Bundles have **no type trees** (`codm typetree <pak>` reports 0), and the engine is IL2CPP with the
  metadata inside the exe, so `MonoBehaviour` payloads can only be read with hand-written layouts.
- `MonoScript` class names are present. Weapon bundles use `WeaponControllerAsset`, `AssetRef`,
  `AnimationEventActionMap`, `CameraAnimation`; the scriptable-object bundles use the `GAS`
  namespace (`*TaskConfig`), `IndividuationMap`, `MeshParticleLODSetting`, `BotVoiceConfig`...
- The `MonoBehaviour` blobs in weapon bundles contain no readable strings (references are ids and
  PPtrs), so sounds and FX cannot be read from them without the class layouts.
