# codm.exe command line

Built from `CodmCli` to `CodmCli\bin\Debug\net472\codm.exe`. It reads `codm.config.json` (found by
walking up from the exe directory, so the copy at the repository root is used). Global options:
`--config <file>`, `--diag` (reader diagnostics on stderr). Progress goes to stderr, results to stdout.

| Command | What it does |
|---|---|
| `codm config [--write]` | show the resolved config, missing folders, vgmstream; `--write` saves the defaults |
| `codm decode <name...>` | decode obfuscated pak, table or archive entry names |
| `codm encode <name...>` | inverse |
| `codm index [--tsv file] [--top n]` | counts per top folder; `--tsv` writes every pak with source, module, decoded path and size |
| `codm search <text> [--max n]` | paks whose decoded name or raw file name contains the text |
| `codm weapons [filter] [--kind main\|melee\|throw\|assi\|special] [--base] [--no-banks]` | weapon table: paks, base, skins, fx, own banks, class banks |
| `codm weapon <id\|folder\|name>` | one weapon: paks, base chain paks, components, effects, sound banks, class banks |
| `codm export <id\|folder> [--out dir] [--no-models] [--no-textures] [--no-sounds] [--no-wav] [--fx] [--no-base] [--components]` | export to `<out>/<weapon folder>/` |
| `codm banks [filter]` | distinct bank names with copy count, embedded media count, size, languages |
| `codm bank <name\|path>` | one bank: objects, events with their actions, sounds with codec and where the wem lives |
| `codm wems <bank> [--out dir] [--wav]` | extract every wem the bank references (resolved across banks), convert with vgmstream when `--wav` |
| `codm scan <pak\|dir> [-v] [--csv file]` | bundle metadata without full deserialization: CABs, object types, names, container paths |
| `codm mono <pak> [--strings]` | MonoScript / MonoBehaviour listing and printable strings in the behaviour blobs |
| `codm typetree <pak> [--max n]` | whether the bundle carries type trees (CoD Mobile: no) |

## Examples

```
> codm weapons locus
folder                                               paks base                   skins  fx banks class
mainweapon_232_locusboomscrawl                          2 locus                      0   2     0     0
mainweapon_293_locus                                   12 *                         10   2     0     0
mainweapon_1648_locusrailgun                            6 locus                      0   2     0     0
...

> codm weapon 130
mainweapon_130_vectork29redgundouble  (MainWeapon, id 130)
  base chain  : mainweapon_138_vectork29redgun -> mainweapon_924_vectork29
  paks (3): ...
  sound banks (7): 130, 130d, 130e, 130f, 130g, 130p3, Music_RedGun_Vector
  class banks (1, media borrowed by the family's skin banks): wpn_ar

> codm export meleeweapon_992_knife_iw5 --out D:\exports --no-sounds
[Export] model MeleeWeapon_001_Knife_iw6_1P: 1 mesh(es), 119 clip(s)
...
models=8 textures=18 banks=0 wems=0 (wav 0) warnings=0

> codm bank 130
130  version=135 id=2155168453 size=1.6 MB sections=BKHD,DIDX,DATA,HIRC
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
  textures/*.png
  sounds/<bank>/*.wem    (+ .wav when vgmstream is configured)
  fx/<effect pak>/       textures/ + objects.txt        (with --fx)
```
