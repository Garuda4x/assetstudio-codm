# codm.exe command line

Built from `CodmCli` to `CodmCli\bin\Debug\net472\codm.exe`. It reads `codm.config.json` (found by
walking up from the exe directory, so the copy at the repository root is used). Global options, before
or after the command: `--config <file>` (another config, e.g. `codm --config codm.config.1957.json banks`),
`--diag` (reader diagnostics on stderr), `--legacy-anim` (decode CODM quantized animation clips the
pre-2026-10-28 way, for comparison; same as `"LegacyAnimDecode": true` in the config). Progress goes to
stderr, results to stdout.

| Command | What it does |
|---|---|
| `codm config [--write]` | show the resolved config, missing folders, vgmstream; `--write` saves the defaults |
| `codm decode <name...>` | decode obfuscated pak, table or archive entry names |
| `codm encode <name...>` | inverse |
| `codm index [--tsv file] [--top n]` | counts per top folder; `--tsv` writes every pak with source, module, decoded path and size |
| `codm search <text> [--max n]` | paks whose decoded name or raw file name contains the text |
| `codm weapons [filter] [--kind main\|melee\|throw\|assi\|special] [--base] [--no-banks]` | weapon table: paks, base, skins, fx, own banks, class banks |
| `codm weapon <id\|folder\|name>` | one weapon: paks, base chain paks, components, effects, sound banks, class banks |
| `codm export <id\|folder> [--out dir] [--no-models] [--no-textures] [--no-materials] [--no-sounds] [--no-wav] [--flat-sounds] [--class-banks] [--bank-variants] [--fx] [--no-base] [--components] [--no-deps] [--max-deps n] [--all-copies]` | export to `<out>/<weapon folder>/`; dependencies are resolved unless `--no-deps`; sounds grouped per event unless `--flat-sounds`; only the weapon's own banks unless `--class-banks`, only `<id>` and `<id>p3` unless `--bank-variants`; `--components` adds the attachment paks, of which only the attachments linked to the weapon (by container path) are exported; models carry their material and colour map (`textures/`) unless `--no-materials` |
| `codm anims <weapon> [--no-deps]` | per Animator prefab: its controller, how many clips resolve and in which bundle the missing ones live |
| `codm cabindex [--rebuild]` | build or refresh the CAB -> pak index (`cache/cab-index.json`) |
| `codm deps <pak...>` | external bundles a pak references and which paks provide them |
| `codm wwnames [--rebuild]` | recover Wwise event names for every weapon bank and record which weapon each bank belongs to |
| `codm components [weapon] [--rebuild]` | index the attachment bundles by their asset paths and link them to weapons; with a weapon, list its attachments and the paths that matched |
| `codm events <weapon> [--all] [--no-deps]` | animation events (notetracks) of the weapon's clips with any Wwise ids they carry |
| `codm mbscan <weapon\|pak> [--deps] [--hex]` | scan MonoBehaviour blobs for object references and Wwise ids (research aid) |
| `codm classes [--unknown]` | sound class of every weapon family (map, seed, kind or unknown) |
| `codm class <weapon> [<class>\|-]` | show or set the family's sound class in `weapon-classes.json` |
| `codm characters [filter]` | character skins under `cod_models/avatar` (pvebot and vfw sub folders grouped) |
| `codm character <name>` | the paks of one character skin |
| `codm exportchar <name> [--out dir] [--no-deps] [--all-copies]` | export a character: one Cast per prefab (body, head, hair, first-person arms) plus textures; no sounds |
| `codm banks [filter]` | distinct bank names with copy count, embedded media count, size, languages |
| `codm bank <name\|path>` | one bank: objects, events with their actions, sounds with codec and where the wem lives |
| `codm wems <bank> [--out dir] [--wav]` | extract every wem the bank references (resolved across banks), convert with vgmstream when `--wav` |
| `codm scan <pak\|dir> [-v] [--csv file]` | bundle metadata without full deserialization: CABs, object types, names, container paths |
| `codm mono <pak> [--strings]` | MonoScript / MonoBehaviour listing and printable strings in the behaviour blobs |
| `codm typetree <pak> [--max n]` | whether the bundle carries type trees (CoD Mobile: no) |
| `codm vfs` | 1.9.57 containers found under the game root: packs, entries, chunks, offset cache state |
| `codm vfs-index [--rebuild]` | walk the base pack once and cache where each entry starts (about 7 minutes) |
| `codm vfs-check [--source s] [--bundles] [--max n]` | read and decompress every container entry in storage order and list the ones that fail |
| `codm vfs-check --unpacked` | compare every file in the unpack folder with a fresh read of its container entry (stale after a game patch?) |
| `codm unpack <text...> [--list] [--source s] [--raw] [--chunks] [--max n] [--out d]` | extract container entries whose decoded name contains the text as loose paks, format 18 rewritten to stock; see [codm-containers.md](codm-containers.md) |

## Examples

```
> codm weapons locus
folder                                               paks base                   skins  fx banks class
mainweapon_104_locus                                   12 *                         10   2     0     1
mainweapon_2537_locusrailgun                            6 locus                      0   2     0     1
...

> codm weapon 249
mainweapon_249_vectork10redgundouble  (MainWeapon, id 249, sound class smg [seed])
  base chain  : mainweapon_138_vectork10redgun -> mainweapon_013_vectork10
  paks (3): ...
  sound banks (7): 249, 249d, 249e, 249f, 249g, 249p3, Music_RedGun_Vector
  class banks (4, media borrowed by the family's skin banks): wpn_smg, wpn_pistol, wpn_ar, vox_global

> codm export 2537 --out D:\exports --no-textures
[Deps] round 1: 34 external file(s) referenced, loading 31 pak(s)
[Export] MainWeapon_2537_LocusRailgun_1P: no clips of its own, using the 34 clip(s) of MainWeapon_104_Locus_1P
[Export] model MainWeapon_2537_LocusRailgun_1P: 1 mesh(es), 34 clip(s) found, 34 written
[Wwise] wpn_sniper: 337/337 wems extracted, 0 converted, 0 unresolved
models=6 (+6 duplicate copies skipped) textures=0 deps=60 banks=1 wems=337 (wav 0) warnings=0

> codm bank 249
249  version=135 id=2155168453 size=1.6 MB sections=BKHD,DIDX,DATA,HIRC
objects: Sound=89, Action=52, Event=18, RandomSeq=32, Switch=10, ActorMixer=14, Layer=8, FxShareSet=1
events (18):
  423327215   Play->472613036  Play->873221156  Stop->208988382 ...
sounds (89):
  sound 1050645003  wem 11181725    Vorbis  stream=0 mem=   8.5 KB  wpn_ar.bnk
  sound 234457997   wem 47386018    Vorbis  stream=0 mem=  58.0 KB  this bank
```

## Export folder

```
<out>/<weapon folder>/
  manifest.json          paks loaded, models, textures, banks, warnings
  models/<prefab>/       <prefab>.cast + anims/*.cast   (one per Animator; "<name> (module)" when two bundles ship the same prefab)
  textures/*.png         every texture of the weapon's paks; the casts reference the colour maps here
  models/<prefab>/anims/notetracks.txt   animation events per clip: time, function, Wwise ids, guessed bank events
  sounds/<bank>/<event>/*.wem   (+ .wav when vgmstream is configured; events.txt maps events to wems)
  sounds/_from_animation_events/<bank>/<event>/   wems of Wwise events the clips reference by id
  fx/<effect pak>/       textures/ + objects.txt        (with --fx; textures the effect's materials reference)
```
