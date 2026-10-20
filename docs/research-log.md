# Research log: CoD Mobile animation clip reverse engineering

This is the distilled record of the probe scripts that lived in `_temp/` while the CODM
`AnimationClip` extension format was being worked out (July to September 2026). The scripts
themselves were archived outside the repository; the knowledge they produced is in
`AssetStudio/Classes/AnimationClip.cs`, `CodmQuantizedDense.cs`, `CodmDenseIdleRelative.cs`,
`CodmBone01PosDecode.cs`, `CodmBone01BrhCompensate.cs` and `AssetStudioUtility/CodmWeaponHandFollow.cs`,
and is summarised in [codm-formats.md](codm-formats.md).

## Test material

- Knife: `cod_models/weapons/meleeweapon/meleeweapon_992_knife_iw5` (streaming), prefab `MeleeWeapon_001_Knife_iw6_1P`, 119 clips, 5 animators.
- Karambit: `cod_models/weapons/meleeweapon/meleeweapon_993_karambit`.
- Animator controllers: `cod_models/weapons/animationcontrollers`.
- Clips dumped to disk for offline probing: Equip (PathID 14), Idle (54), Fire (60), FireJump (117).

## C# probes (ClipDiag project)

| Probe | What it tested |
|---|---|
| `AbsRHunt.cs` | Hunt raw absolute Bone01.R (= Fire* f0 / bind) in prefix, trailer, ConstantClip, and as a transform of dense-end R. No bake. |
| `AbsStoreHunt.cs` | Find where absolute Bone01.R (= Fire* f0) actually lives when bind ≠ Fire* (Avatar pose, prefab, muscle StartX/StopX, ValueArrayReferencePose, any object bytes). |
| `AlignCheck.cs` | (no header comment) |
| `ArmDump.cs` | (no header comment) |
| `ArmMotion.cs` | After rewindow+bake, report arm/body motion spans for key clips. |
| `ArmProbe.cs` | Diagnose right-arm bone path resolution for FireLeft/FireJump vs hierarchy. |
| `BakeSweep.cs` | Score several CODM bake formulas on Equip without Blender. Restores dense samples between trials. |
| `BindFireDelta.cs` | Fire* streamed idle vs Animator bind for weapon leaves — angular deltas. |
| `BindLayout.cs` | Dump Transform binding curve indices for arm/torso/weapon bones. |
| `BindVsIdle.cs` | Compare prefab Transform locals vs Avatar skeleton pose vs Fire* idle — explains which "rest" the GLB bind pose is actually using. |
| `BlobEdges.cs` | Dump unknown bytes before the dense header and after the last quant block. Reconstruction target: channel table / trailer meaning. |
| `BlobPrefix.cs` | Dump bytes before the dense-style header and compare Bone01 P/R motion FireLeft (streamed) vs Inspection (dense, pre/post bake). |
| `BlockHeaderOrigin.cs` | Prove 3ch "min/max" headers are bind-pose floats, not Bone01.P value ranges. |
| `Bone01Couple.cs` | Data probe: Bone01 / b_RightHand coupling in FireLeft (truth) vs dense Inspect/Equip raw. Labels inventeds separately from measured deltas. |
| `Bone01Focus.cs` | Knife-only probe: Bone01 dense/bake vs FireLeft, and knife_melee_1p bind local. Arms assumed OK per Blender — focus weapon chain + mesh attach. |
| `Bone01Pos.cs` | Bone01.P only — R stays IdleRelativeEnd. Figure out why position is wrong. |
| `Bone01PosSweep.cs` | Fast Blender A/B: rewrite Bone01.P leading-3ch decode, export labeled GLBs. Usage: ClipDiag.exe [pak...] b01sweep &lt;outdir&gt; [go=Knife_iw6|Karambit] [clips...] |
| `Bone01Quant.cs` | Inspect how Bone01.P lands in quant blocks (min/max/codes) vs arm R channels. |
| `Bone02Check.cs` | (no header comment) |
| `CabFind.cs` | Find which .pak files contain a given CAB name string (header/directory scan). |
| `ChannelShift.cs` | Within the current dense window, try binding-aligned column shifts and score Bone01 / arm end pose vs FireLeft — tests whether blob columns are offset relative to binding order. |
| `CodmAcDump.cs` | Partial CODM Basic_Melee_1P reader: adaptive layers + TOS + clip list. |
| `CodmAcProbe.cs` | Field-walk Basic_Melee_1P ControllerConstant to find where stock Unity 5.6 layout diverges. |
| `ConstVsFire.cs` | Compare ConstantClip (static) channels vs Fire* idle after rewindow — these are NOT touched by IdleRelativeEnd dense bake. |
| `CrossMelee.cs` | Compare karambit (993) hollow Equip/Inspect to knife (992). Karambit pak only has those two clips; Fire* may override to knife. |
| `CtrlNames.cs` | AssetInfo has preloadIndex, preloadSize, asset PPtr |
| `DenseBone01Dump.cs` | What's in the hydrated dense window for Bone01 / Bone_RightHand (pre-bake). |
| `DumpBrh.cs` | (no header comment) |
| `EmptyDump.cs` | (no header comment) |
| `EndFrameDump.cs` | After hspan + IdleRelativeEnd: how Bone01 / hand sit vs FireLeft at start/mid/end, and how fully Bone01.R uses the shared quat block range. |
| `EngineWire.cs` | In-engine wiring: Animator → Override → base controller + external CAB refs. Hunt missing assets instead of inventing bake. |
| `ExportCli.cs` | Headless Animator+clips → GLB export for fast Blender A/B without the GUI. |
| `FingerMag.cs` | (no header comment) |
| `GainSweep.cs` | Export a small gain × hand-follow matrix for Blender A/B. |
| `GripErrorSweep.cs` | Score how stable the hand→knife relative pose stays vs FireLeft (ground truth). Sweeps 3ch span factors and optional Bone01.R modes; prints RMSE table. |
| `GripShift.cs` | Uniform reversible theory: dense columns are a cyclic shift of binding order. Roll by whole binding dims; same shift for all bones; score grip on Inspection. |
| `GripShiftCross.cs` | Cross-check candidate uniform column shifts on Idle+Inspect+Equip. Must help hold clips generally — not Inspection-only coincidence. |
| `GripStability.cs` | FireLeft t=0 defines grip: relative transform hand→knife. Idle/Inspect/Equip should keep that grip (small drift). Same bake formula for all dense bones — drift means formula/window error, not a Bone01 special case. |
| `GripSweep.cs` | Uniform reversible A/B: same bake mode for every dense bone. Score = grip stability vs FireLeft t=0 on hold clips (Idle/Inspect). Equip scored at end (should return to grip); start may be off-grip. |
| `GripWindow.cs` | Uniform theory: binding window chosen by post-bake grip stability (hold clips), not only idle-end cost. Same window rule for all bones. |
| `HashHunt.cs` | (no header comment) |
| `HashHunt2.cs` | (no header comment) |
| `HeaderReinterpret.cs` | Dump raw 3ch block bytes + try alternate header meanings, scored vs wpos=0.5 clue target. |
| `HollowChannels.cs` | (no header comment) |
| `HydrateCheck.cs` | (no header comment) |
| `IdleBones.cs` | Per-bone local TRS at t≈0: FireLeft vs baked Inspection/Equip/Idle. Isolates which chain bones diverge (cause of world-hand mismatch). |
| `IdleMatch.cs` | Diagnose whether idle-ref matching hits arm/weapon bones after rewindow. |
| `KarambitGrip.cs` | Karambit Equip spin / index-through-hole: compare Bone01 R vs right index, and Bone01/mesh rest between knife and karambit. |
| `KnifeMeshProbe.csx` | (no header comment) |
| `KnifeOffset.cs` | Bone01 local TRS: hierarchy bind vs FireLeft vs baked Inspection/Equip/Idle. |
| `LayerVar.cs` | (no header comment) |
| `LeftHandLayer.cs` | (no header comment) |
| `LoadBase.cs` | (no header comment) |
| `MaskPoseDump.cs` | Dump Basic_Melee_1P skeleton masks → bone names, resolve layer bindings, and confirm ForPose / Offset clips are empty placeholders. |
| `MeleeCtrlProbe.cs` | Explore Basic_Melee_1P.controller despite soft-fail parse: dump name, trailing TOS+clips, layer-ish floats, resolve clip PPtrs. |
| `MuscleProbe.cs` | (no header comment) |
| `OffsetSample.cs` | (no header comment) |
| `OverrideDump.cs` | (no header comment) |
| `PathCompare.cs` | (no header comment) |
| `PosDoubleCount.cs` | Test: if Bone01.P were frozen at idle, does Bone01.R alone already track the hand? If yes, dense P is additive double-count → looks exaggerated but same direction/timing. |
| `PosExaggeration.cs` | Quantify Bone01.P exaggeration: raw span, bake deltas, vs hand motion / FireLeft. |
| `PosSpace.cs` | Probe alternate interpretations of the 3-channel Bone01.P quant block. |
| `PosTarget.cs` | Is dense local[0..2] really Bone01.P, or should those samples target another binding? Also dump trailer table slots at Bone01.P curve indices. |
| `PoseMatch.cs` | Score Equip/Idle dense endpoints against FireJump/FireLeft t≈0 pose, matched by (path hash, attribute) — not raw curve index. |
| `Prefix116.cs` | Prefix after const pose: test 4*116 float frames vs dense R block. |
| `PrefixHunt.cs` | Dump CODM blob prefix for Inspection — hunt for alternate Bone01.P payload. |
| `PrefixParse.cs` | Parse Inspection CODM blob prefix as bookend float poses / streamed-style keys. Goal: find a trustworthy Bone01.P source outside the 3ch quant block. |
| `Program.cs` | (no header comment) |
| `RawIdlePath.cs` | Hunt a non-bake path from raw CODM data → Fire* f0 for Bone01 (and optionally arms). Compares FireLeft@0 vs: hierarchy, ConstantClip slots, dense raw end (full/half span), and simple absolute reinterpretations of end codes. |
| `RelPose.cs` | Compare Bone01 local motion vs b_RightHand in FireLeft (truth) vs Inspection/Equip after idle-end bake — measures detach without inventing constraints. |
| `RestPose.cs` | Probe whether idle/rest used for IdleRelativeEnd bake is accurate. Wrong rest → constant offset on every dense clip after bake. |
| `SchemaCount.cs` | (no header comment) |
| `TrailerDecode.cs` | Decode CODM quant blob trailer after the last range block. Observed: pad, float, zeros, uint count(=0xA1?), then 0xFF bitmask run. |
| `WeaponPose.cs` | Compare FireLeft/FireJump Bone01 + arm rest vs post-bake Idle/Equip end pose. |
| `WindowProbe.cs` | List which bones fall inside each CODM dense window and their motion energy. |
| `WindowScore.cs` | Score alternate dense binding windows by how well baked end-pose / mid motion resembles FireLeft on arm+weapon bones — data-driven, not hand-follow. |
| `WireSweep.cs` | Methodical wiring sweep: score how ConstantClip + dense should combine for CODM clips. |
| `WorldAttach.cs` | World-space knife origin (Bip01→Bone_RightHand→Bone01) vs skinned hand (Bip01→…→b_RightHand). Measures real detach in export space. |
| `WposClue.cs` | Use wpos=0.5 baked P as a TARGET clue — find a real decode/space transform whose output lands near that target (without baking in a literal 0.5 gain). |
| `_dumpbrh.csx` | (no header comment) |
| `cmp_glb_rest.py` | (no header comment) |

## Python probes

| Script | Purpose |
|---|---|
| `dump_trail.py` | (no header comment) |
| `find_locus_strings.py` | (no header comment) |
| `find_weapon_def_paks.py` | CODM / Unity weapon config hints (from community + user observation) |
| `find_weapon_def_paks_fast.py` | (no header comment) |
| `inspect_glb.py` | (no header comment) |
| `probe_after_prefix.py` | (no header comment) |
| `probe_after_stream.py` | (no header comment) |
| `probe_bind.py` | Idle clip - large, interesting |
| `probe_bind_align.py` | verify path64: typeID at right place path, attr, fileID, pathID64, typeID |
| `probe_bind_dims.py` | (no header comment) |
| `probe_bitgrid.py` | (no header comment) |
| `probe_bits_validate.py` | (no header comment) |
| `probe_blocks.py` | (no header comment) |
| `probe_blocks2.py` | (no header comment) |
| `probe_clip.py` | (no header comment) |
| `probe_cs_logic.py` | Offline verify CodmQuantizedDense logic against clip dumps. |
| `probe_decompress.py` | (no header comment) |
| `probe_dense2.py` | (no header comment) |
| `probe_dense_ext.py` | (no header comment) |
| `probe_ext_format.py` | IEEE half -> float |
| `probe_ext_format2.py` | (no header comment) |
| `probe_fit.py` | (no header comment) |
| `probe_fit2.py` | (no header comment) |
| `probe_fit3.py` | (no header comment) |
| `probe_framehdr.py` | (no header comment) |
| `probe_frames.py` | (no header comment) |
| `probe_fullframes.py` | (no header comment) |
| `probe_gap.py` | magic search |
| `probe_gap2.py` | (no header comment) |
| `probe_gap3.py` | (no header comment) |
| `probe_gap4.py` | (no header comment) |
| `probe_gap_streamed.py` | (no header comment) |
| `probe_layouts2.py` | (no header comment) |
| `probe_muscle.py` | (no header comment) |
| `probe_muscle2.py` | (no header comment) |
| `probe_packed.py` | (no header comment) |
| `probe_payload1.py` | (no header comment) |
| `probe_payload2.py` | (no header comment) |
| `probe_payload3.py` | (no header comment) |
| `probe_payload_hdr.py` | (no header comment) |
| `probe_pfv.py` | (no header comment) |
| `probe_quant.py` | (no header comment) |
| `probe_quant_dump.py` | (no header comment) |
| `probe_quat_ranges.py` | (no header comment) |
| `probe_records.py` | Assume header 16 bytes at 0x580, then 6-byte records try record sizes 4,6,8,10,12,16 after various header sizes |
| `probe_scan_blocks.py` | (no header comment) |
| `probe_seek.py` | (no header comment) |
| `probe_seq_blocks.py` | (no header comment) |
| `probe_seq_blocks2.py` | (no header comment) |
| `probe_streamed_hdr.py` | (no header comment) |
| `probe_trailing.py` | (no header comment) |
| `probe_trailing_layouts.py` | clip_end = offset after ValueArray (including its contents) |
| `probe_u16_final.py` | (no header comment) |
| `probe_u16recs.py` | (no header comment) |
| `probe_validate.py` | (no header comment) |
| `probe_validate2.py` | (no header comment) |
| `scan_locus_paks.py` | (no header comment) |
