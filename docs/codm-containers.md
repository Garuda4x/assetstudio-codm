# CoD Mobile 1.9.57 containers (QtsVFS)

Since 1.9.57 the game ships no loose `.pak` bundles. Everything sits in three packed stores of
Tencent's QtsVFS system (class names in the exe: `QtsfSys`, `QtsfPacker`, `QtsfPatcher`, `QtsVFSManager`,
`QtskDB`). `AssetStudio.Codm/Vfs/` reads them; `codm vfs`, `codm vfs-index` and `codm unpack` are the
command line front end. All offsets are little endian unless stated.

| Store | Folder | Reader | Content |
|---|---|---|---|
| base pack | `CODM_Data\StreamingAssets\AssetBundle\base\` | `QtsfPack` | the former StreamingAssets bundles (10 988 named entries) + 137 604 shared chunks, 42 GB in 22 parts |
| download pack | `PersistentData\PufferQts\` | `QtsPack` | the former Puffer module bundles: weapons, roles, effects (11 122 named entries) + 77 342 chunks, 96 parts |
| patch pack | `PersistentData\ExtractQts\<n>\` | `QtsPack` | 2 849 chunks of the current patch, 3 parts |

`CodmVfs` opens whatever it finds under `GameRoot` (or `VfsFolders` in the config), lists every entry
with the file name a loose pak would have had, and resolves chunks across the three stores.

## Qts pack (download and patch packs)

**Index** `AssetBundle.pak`, magic `ED FF 34 01`. Header (u32): magic, build stamp, page size (4096),
pages per part (128000), offset of the record section, -, 0x34 (directory offset), table count, part
count, part descriptor offset, descriptor bytes, total size, 8, then the directory: `(offset, size)`
pairs, one **block table** per entry. Part descriptors are 16 bytes each (`98 3E 34 01`, stamp, 4096,
128000) in part file order; the stamps equal the part files' header stamps.

The record section starts with `01 00 00 01 01 02 03 04`; u32 at +12 is its length, u32 at +16 the
offset of the first record. Records are nodes: `u32 self offset (relative to the section), u32
0xFFFFFFFF, u32 node size, u64 hash`, then `u32 name length, u32 20, u32 uncompressed size, u32 ordinal,
u32 timestamp, u32 0x01001389, u32 x, name`. The ordinal indexes the block table directory. Names are
stored lower case: `dynamic/module/<module>!<ver>!<tag>!<ciphered path>.pak` for module bundles (the tag
decodes to `forcedownload`), `dynamic/split/<md5>.cpak` or `split/<md5>.cpak` for chunks.

**Block table**: `u16 block count, u16 last block raw size, u32 0, u32 end offsets of blocks 0..n-2
(relative to the entry's first data byte), u32 x, u32 w, u32 Q`, optionally a run table (below), and a
16-byte hash at the end (not the md5 of the decoded entry). `Q` is the entry's global logical page; the low
16 bits of `x` are the number of pages the compressed entry occupies (holds for all 91 313 entries of both
packs; the high 16 bits are the record's `x` & 0xFFF); the low 16 bits of `w` are 1 when a run table follows.

**Parts** `AssetBundle_<k>.pak`: 32-byte header (`u32 28, u32 1001, u32 20201112, u32 stamp, u32 4096,
u32 128000, u32 32, u32 128000`), 128 000 flag bytes, a page map of 128 000 u32 (logical page ->
physical data page), then 128 000 data pages of 4 KB.

**Location rule**: page `p` of an entry is logical page `Q + p`: `part = (Q + p) / 128000`, `logical =
(Q + p) % 128000`, `physical = pageMap[logical]` of that part, data at `32 + 128000 + 512000 + physical *
4096`. Physical pages of consecutive logical pages are usually consecutive but need not be, and an entry
can run past the last page of a part into the next part (95 entries do). Part files end right after the
last used byte, so the final page may be short. An entry is its LZ4 blocks back to back (16 KB raw each,
the last one shorter); the block table gives the end offsets of all but the last block, which is decoded
until the raw size is reached (`Lz4Block`). Every other block must end exactly at its table offset, which
`QtsPack` checks: data read from the wrong place fails that test.

**Run table** (entries with `w & 0xFFFF == 1`, 586 of 91 313): `u32 run count`, one byte per page giving
the page's run (0 = the entry's own pages), padded to 4 bytes, then one u32 per run above 0. Example
`split/ff22a107...cpak`, 26 pages: runs `0×7, 1×2, 2×13, 3×1, 0×3`, values 1803, 2138, 2158; the run
lengths (10, 2, 13, 1) equal the runs of consecutive physical pages in its page map. For most of these
entries the pages are simply contiguous in the part file from the first page's physical location (the
page map sends the later logical pages elsewhere), and that is how `QtsPack` reads them, falling back to
the page-map walk when the block ends do not fit. A few (3 in the patch pack, 171 in the download pack,
see `codm vfs-check`) fit neither layout: the trailing values are not ordinals of the same pack, so the
run table is not understood yet. The record timestamp and the high half of `w` are not needed.

`AssetBundle_Merge.pak` is a copy of the index;
`QtcLocalFile.pak`, `DynamicBaseDB.pak` and `puffer_res.eifs` are other databases of the same family
(`xx xx 34 01`) listing audio and video paths in plain text.

## Base pack (QTSF)

**Index** `AssetBundle_base.pak`, header `01 00 00 01 01 02 03 04`, page size 4096, a `QTSF_PACKAGE`
marker, then one node per entry: node header `[self offset][-1][node size][u64 hash][8][payload
size][0x20000]`, payload `QTSF`, 8-byte entry id, 8-byte kind hash (one value for chunks, one for
named files), u32 name length, name (4-byte aligned), u32 uncompressed size, u32 part, 20-byte hash,
entry id again. Names keep the old cipher (`LUJ_EUJMCF$...pak`) or are `<md5>.cpak`.

`AssetBundle_base_packorder.bin` (`QPKO`, u32 1, u32 count, u64 ids) lists the entries in storage
order. The index has no offsets.

**Parts** `AssetBundle_base_<k>.pak` (2 000 000 000 bytes each): 4 KB header, a 375-entry u32 table at
0x1000 (meaning unknown), nodes from 0x1a000. Node: `[u32 self offset][u32 link][u32 node size][u64
hash]`, block header `[u32 16][u32 compressed size][u32 method (2 = zstd, 0 = stored)][u32 raw size]`,
the zstd frame (single segment) plus 8 trailing bytes; nodes are 8-byte aligned and 4 KB index pages
(sorted u32 lists) may sit between them. The link field is `0xFFFFFFFF` for most nodes and an offset for
nodes of repeated content (runs of identical 16 KB blocks compress to 27 bytes and point at each
other), so it is not a validity criterion. Blocks are 16 KB raw, occasionally shorter in the middle of an
entry, so an entry ends where the accumulated raw size reaches its index size.

**Offset cache**: `codm vfs-index` walks every part once (about 7 minutes at 100 MB/s), assigns each
entry of the pack order its first node and writes `<CacheDir>/qtsf-AssetBundle_base-offsets.bin`
(`QOF1`, part count, part file lengths, then `u64 id, i32 part, i64 offset, i64 stored size` per entry).
The cache is invalid when a part file changes length. Reading walks the nodes from the cached offset and
decompresses each frame with ZstdSharp (`ZstdFrame` computes the exact frame length, since the 8 bytes
after it are not part of the frame).

## Serialized file format 18 and the normalizer

The bundles inside the containers are UnityFS 6 / 5.6.4p4 with the data block stored uncompressed and
the serialized file in **format 18**:

- after the type-tree flag: `u32 count` and that many NUL-terminated strings `qtc:/split/<md5>.cpak`;
- object entries: `i64 pathID, u32 flag`; `flag 0`: `u32 byteStart, u32 byteSize, i32 typeID` (payload in
  this file); `flag 1`: `u32 refIndex, u32 offset, u32 byteSize, i32 typeID` (payload at `offset` in the
  chunk named by the string list). The chunk's size equals the highest `offset + byteSize` using it;
- script types and externals keep the stock layout.

Chunks of download-pack bundles live in the download pack, chunks of base-pack bundles in the base
pack (checked on the Locus bundles and 30 random module bundles). Chunk names are stable ids, not
content hashes: the patch pack ships newer versions of base-pack chunks under the same name with a
different size, so a bundle takes each chunk from its own pack first and falls back to the other packs
only when its own has none.

Streamed data moved as well: most `Texture2D.m_StreamData` paths are `qtc:/stream/<md5>.cpak` (a
chunk holding the pixel data) instead of `archive:/CAB-.../CAB-....resS`; a few textures still use the
bundle's `.resS`.

`BundleNormalizer` turns such a bundle into a stock one: it decompresses the UnityFS blocks, rewrites
every format-18 file as format 17 (string list dropped, external payloads appended to the data section
8-byte aligned, object table in the stock 20-byte layout, metadata size and data offset recomputed),
embeds every chunk referenced from the data section (`qtc:/.../<md5>.cpak`) as a resource node named
`<md5>.cpak` (the reader resolves stream paths by file name among the bundle's resource nodes) and
re-emits an uncompressed UnityFS. `.resS` nodes pass through. The unchanged AssetStudio reader then
loads the result; `SerializedFile` itself only learned to skip the string list.

## On-demand loading

When the config's game root holds containers, `PakIndex.Build` lists every bundle of the three stores
(22 666 on 1.9.57) as a `PakEntry` whose `Container` is the `VfsEntry` and whose `FullPath` is where it
is unpacked: `<UnpackRoot>/<puffer|extract|streaming>/<file name>`. Files already there (from `codm
unpack` or an earlier load) are matched to their entry. Catalog, browser and export see one index as
with loose paks; `PakEntry.EnsureOnDisk` writes a missing file (normalized, through a temporary file)
right before it is loaded, so only what gets loaded or exported is ever unpacked. A pak that cannot be
unpacked is logged and skipped, like an unreadable file. Several paks are unpacked in storage order.

The indexes read the containers directly instead of unpacking: the CAB index decodes only the first
16 KB block of each bundle (the directory sits right after the header), all 22 552 in one sweep in
storage order (pack, part, offset), 2.5 minutes from the hard disk, cached in `cab-index.json` with the
entry's container size and hash stamp as its identity. The attachment index normalizes the 2 466
component bundles in memory without their texture streams. Neither writes paks.

An unpacked file is trusted as long as it exists. After a game patch, `codm vfs-check --unpacked`
compares every unpacked file with a fresh read of its entry; delete the ones that differ (or the whole
unpack folder) and they are unpacked again on next use.

## Command line

```
codm --config codm.config.1957.json vfs                    packs, entry counts, offset cache state
codm --config codm.config.1957.json vfs-index [--rebuild]  build the base pack offset cache
codm --config codm.config.1957.json vfs-check [--source s] [--bundles] [--max n]   read every entry, report failures
codm --config codm.config.1957.json vfs-check --unpacked   compare the unpack folder with the containers
codm --config codm.config.1957.json unpack <text...> [--list] [--source puffer|extract|streaming] [--raw] [--chunks] [--max n] [--out dir]
```

`unpack` stays the batch path (and the only one for `--raw` and chunks); everything else unpacks on
demand.

`unpack` matches the text against decoded names (`mainweapon_2537_locusrailgun`, `avatar/c_f_alias`,
`weapons/animations/1p`) and writes `<UnpackRoot>/<source>/<FILENAME>.PAK`, the file name being the
ciphered name a loose pak had, so `PakName` decodes it and the pak index, catalog and export work on
the output unchanged. `codm.config.1957.json` lists those three folders as its `PakFolders`. Without
`--raw` bundles are normalized; `--chunks` also extracts raw `.cpak` chunks into `chunks/`.
