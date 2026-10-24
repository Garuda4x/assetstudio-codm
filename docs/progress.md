# Progress log

Dated record of what was built, what was found out about the game data, what was tried and did not
work, and what is still open. The git history has the code-level detail; this is the narrative.
Older history (the animation-format reverse engineering, July to September 2026) is summarised in
[research-log.md](research-log.md).

## 2026-10-20: repository, navigation, sounds, FX

**Repository.** Upstream Perfare remote dropped, history kept; the uncommitted work of the previous
months committed in five logical commits (core reader, animation decoder, Cast/glTF export, GUI,
PakScanner). All projects pinned to `net472`. Probe scripts moved out to
`C:\Users\Dylan\repo\AssetStudio_research_archive`. Reader diagnostics gated behind one switch
(`CodmDiagnostics`, `"Diagnostics"` in the config). Config moved to `codm.config.json`.

**Finding: the pak names are a cipher.** A fixed 13-pair letter swap with the case flipped, `$` for
`/`, `!` for `.`: `LUJ_EUJMCF$XMPAUVF$...` is `cod_models/weapons/...`. Later the same day the digits
turned out to be swapped too (0-9, 1-2, 3-4, 5-6, 7-8); the ids in decoded names are the real game ids
(Locus railgun is 2537, Locus 104), which the prefab names inside the bundles confirmed.

**Finding: weapons are split across bundles.** A skin folder (`mainweapon_2537_locusrailgun`) holds
textures and overrides; the mesh sits in the base weapon folder (`mainweapon_104_locus`, shipped by
`normal_baseweapon_*` modules); skins start with the base name. Every download module copy of a path
is a different bundle holding a slice. Weapon bundles contain no animation clips: the override
controller sits in a small controller bundle and the clips in shared packs
(`cod_models/weapons/animations/1p`, `br`, `unarmed`...), referenced by CAB name.

**Built.** `AssetStudio.Codm` library (name cipher, pak index, weapon catalog with base chains,
Wwise v135 bank parser and index, export service), `codm.exe` CLI, CODM menu and weapon browser in
the GUI, CAB index (bundle headers only, cached) plus dependency resolver, weapon class map for the
`wpn_<class>` banks, per-event wem export, attachment linking through the asset paths inside the
attachment bundles (container index), FX export through the materials' texture bundles,
`ParticleSystemRenderer` parsed for its materials, CoD Mobile's 2017-style vertex formats accepted.
Cast became the default export; GLB and FBX opt-in.

**Finding: Wwise.** Bank version 135, no names. 192 000 embedded media ids across 3 819 bank files;
skin banks borrow media from other banks, a global media index resolves 99.95 % of sources. Event ids
are FNV-1 of names and the weapon events follow patterns (`play_wpn_locus_fire_1p` in `105.bnk`,
`play_weapon_sniper_raise_3p` in `wpn_sniper`); candidate hashing recovers the names that fit the
patterns and identifies which weapon a numeric bank belongs to. Bank numbers are not the pak ids of
the same kind (`105.bnk` is the Locus, `throwweapon_105_cannon_turret` is unrelated), so the name
match is the primary link and the numeric match only a fallback.

**Dead ends.**
- Bank media sharing as a class signal: skin banks borrow handling sounds from several `wpn_*`
  banks, so inferring the weapon class from them gave wrong classes (Vector -> pistol). Replaced
  by a seeded class list plus `weapon-classes.json`.
- `MonoBehaviour` blobs as a per-weapon manifest: `AssetRef` only lists the prefab's LOD meshes,
  `CameraAnimation` its camera clips, `WeaponControllerAsset` two config ids; no Wwise ids anywhere.
- The config tables (`DataTableBytes`) are not a repeating XOR: per-position byte statistics and a
  brute force over 4-byte keys found nothing; the high 16 bits of each dword are near-constant per
  file, so it is a short-key stream cipher of some kind. Decrypting them means static analysis of
  `CODM.exe`.
- `CODM.exe` plain strings: 96 000 strings in 290 MB and no Wwise names, so the IL2CPP metadata (and
  with it every C# string literal and class layout) is encrypted in the binary.
- Switch values inside the class banks (the per-weapon selector) did not match any generated name.

**Characters.** `cod_models/avatar/<skin>` (573 folders, bot and event variants under `pvebot`,
`vfw`); `cod_models/cly` and `cod_models/br` are pickups and boxes, not characters. Character
catalog, CLI and browser root added; body, UI and arms prefabs export as Cast. The `_1P` arms prefab
resolves no mesh (assigned at runtime from a shared asset, source not found yet). "Preview mesh"
button added to the browser.

**Sound export trimmed.** Weapon exports take the weapon's own banks and its base chain's; class
banks (`wpn_sniper`, 337 wems) and the lettered bank variants (`247a`, `247c`...) are opt-in.

## 2026-10-21: camera clips

**Finding.** Every first-person animation has a sibling `<name>_Camera` clip animating the Animator
root (path hash 0). The archive showed they had only ever been exported alone (an 8.8 KB skeleton-only
dud next to each weapon animation, plus the "orphan camera" exception path); combining them with the
weapon animation had been a manual Blender step.

**Built.** `CameraTrackMerger`: a `Camera` bone under the rig root receives the camera clip's root
curves inside the partner animation, for every Cast/FBX/GLB write. Only rigs the partner animation
drives (the first-person arms) get the bone; where several clips share a short name the one keying
the most bones wins. On the knife 7 of 8 camera clips merge; `ProneCrawlBwd_Camera` carries no
decoded curves.

## 2026-10-22: sound triggers on the animations

**Finding.** The clips carry Unity `AnimationEvent`s and the game keys its weapon audio on them:
semantic triggers (`PlayFirstAttackSound`, `PlayStabSound`, `PlayRaiseSound`, `PlayPutDownSound`,
`PlayInspectionSound1/2`, `PlayClipInSound`, `PlayClipOutSound`, `PlayBoltBackAndReleaseSound`,
`FiringEvent`) with empty data, and `PlayEventSound` with ids in the data. A plain number is a Wwise
event id (`347825172` in `MP_GEN.bnk`); the 64-bit pairs (`562950372539117~1125900325960429|8`) are
config-table ids (tag in the high bits, same low 32 bits in both halves), not Wwise hashes. The Cast
exporter had been writing the events as notification tracks all along.

**Built.** `anims/notetracks.txt` per prefab (time, function, decoded Wwise ids with bank, guessed
bank events by function name), wems of clip-referenced Wwise events under
`sounds/_from_animation_events/`, `codm events`. `Windows_HQ` bank copies preferred where they exist
(79 banks; the rest is 22 kHz Vorbis). The event vocabulary was widened with the function-name words;
total recovered names 90, the `wpn_melee` events still unnamed, so the melee trigger-to-wem pairing
stays by ear.

## Open items

- Name the switch values inside the class banks (per-weapon split of `wpn_*`), and widen event
  name recovery (90 of several thousand ids named).
- Source of the characters' first-person arms mesh.
- Decrypt `DataTableBytes` by static analysis of `CODM.exe` (the real manifest: `WeaponAsset`,
  `WeaponSoundConfigs`, `WeaponPartCompose`, `LegendaryWeaponEffect`). Reading or patching a copy
  of the exe is allowed; running the game is not.
- Optional: embedded textured mesh preview in the browser; standalone GUI.
