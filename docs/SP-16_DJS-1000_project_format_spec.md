# Pioneer DJ SP-16 / DJS-1000 project format: working spec

Everything here was checked against real files: the loader page's `one.tpkg`, projects saved by a DJS-1000 through Project Creator (02 to 08), and projects saved on a Toraiz SP-16 (`arr0` to `arr13`, `route1`, `route2`, `route3-mono-8ch`). A generated test package (`rev1.tpkg`) was loaded and played on an SP-16.

Evidence labels used below:

- **Confirmed (file):** read from a unit-saved file.
- **Confirmed (SP-16):** a generated file was loaded and behaved as described on an SP-16.
- **Reported:** observed on the unit by the user, not checked in a file.
- **Unverified:** assumption or guess.

The DJS-1000 and SP-16 share the container, grammar and the scene, track and pattern structures. The DJS-1000 has no arrangement mode and no separate outputs, so those parts are SP-16 only.

## 1. Project file (`.prj`)

| Offset | Content |
| --- | --- |
| 0-9 | Magic `76 6c 74 72 67 7a 69 70 78 01` (`vltrgzipx` + `0x01`) |
| 10 to end-4 | Raw DEFLATE stream (no zlib header) |
| last 4 bytes | Adler-32 of the inflated payload, big-endian |

The unit accepted a file whose outer DEFLATE was written at maximum compression, so any valid DEFLATE writer works. (Confirmed, SP-16.)

## 2. Payload grammar

The payload is a tagged tree. Strings are NUL-terminated Latin-1.

**Variable-width number (`vi`)**: one byte `k`, then `k` bytes little-endian. `k = 0` means the value is 0 or absent. A writer using the minimal `k` reproduces every sample file byte for byte.

```
struct  = name\0  vi(nScalars)  scalar*  vi(nChildren)  struct*
scalar  = name\0  00                       (null value)
        | name\0  vi(len)  type  data      (len counts the type byte plus data)
```

| Type | Meaning | Data |
| --- | --- | --- |
| 1 | int32 | 4 bytes LE |
| 2 | true | none |
| 3 | false | none |
| 4 | float64 | 8 bytes LE |
| 5 | string | bytes + trailing `00` |
| 7 | array | `00` (empty), or `vi(count)` then count items, each `vi(len) type data` |
| 8 | blob | zlib stream (seen in `Steps` and `Sections`) |

- A length above 255 is not special. `Steps` is stored as `02 b3 02 08 ...`, a 2-byte length (691) followed by type 8.
- **Keep key order.** The writer emits scalars and children in the order read, and the unit writes them in a fixed order (see section 4 for the orders seen).
- Blobs recompressed with zlib level 6 match the unit's own output byte for byte. Other compressors should still be valid.
- **Round-trip result:** all 8 DJS-1000 files and all 17 SP-16 saves parse with every byte consumed, and re-emitting the parsed tree reproduces the inflated payload byte for byte.

## 3. Tree

```
ProjectData
├─ SceneData0 ... SceneData15            16 scenes
│   ├─ TrackData0 ... TrackData15        16 pads
│   │   ├─ PlaybackData, AmpEnvelopeData, SequencerData, SliceData
│   │   ├─ ScaleSetting, ScaleSettingMidi, InsertFx0, InsertFx1
│   │   └─ SendData, LfoData, MidiTrackData
│   ├─ PatternData<N>                    one per non-empty pattern; N = 0..15
│   ├─ SendFx, MasterFx
│   └─ RoutingData  (TrackRoutingData0..15, each with assignableOutCh)
├─ ArrangementData0                      SP-16 only
├─ RibbonData
└─ MidiSettingData
```

**Child order inside a scene (Confirmed, SP-16):** 16 × `TrackData`, then any `PatternData<N>`, then `SendFx`, `MasterFx`, `RoutingData`. With several patterns in one scene, ascending N is assumed (Unverified).

## 4. Scalars

### ProjectData (order as saved by the SP-16)

`masterVolume`, `headphoneVolume`, `sendTrackVolume`, `monitorVolume` (int32); `bDaveSmithFilter` (bool); `daveSmithRouteMode`, `daveSmithSendMode` (string); `daveSmithSendTrack` (int32); `bSoftLimitter`, `bPlayArrangement` (bool); `swingRate` (int32); `projectBpm` (float64); `patternQuantizeUnit`, `padQuantizeUnit` (int32, packed, not decoded); `padSequenceStart` (bool); `sceneIndex`, `patternIndex` (int32, 0-based, the current scene and pattern); `projectFilePath` (string); `versionNo` (3), `minorVersionNo` (1 from the unit, 0 from the loader).

- The SP-16 writes `projectFilePath` as `/mnt/emmc/PIONEER DJ SAMPLER/Projects/<name>.prj` and updates it on every save. A generated file may carry any consistent value; the unit tolerated one it did not write itself.
- `bPlayArrangement` is true when the arranger is switched on.

### SceneData

`colourIndex` (int32, -1 default), `name` (string), `bpm` (float64, 0 default). Whether the scene `bpm` is used during arrangement playback is **Unverified**.

### TrackData (a pad)

`trackMode` ("Sample"), `triggerMode` ("OneShot"), `chokeGroup`, `volume` (100), `pan`, `soloSetting`, `muteSetting`, `colourIndex`, `instrument` ("Off").

**`colourIndex`:** every unit-saved project gives `TrackData<N>` the value N, in every scene, and the unit numbers its 16 track colours from 1 on screen, so track 5 has colour 05 (Confirmed, file and SP-16). A generator may want another: Strands gives every pad of a stem one colour, so that the stem is one colour in every pattern, with 0, 4, 8 and 12 for the stems of the four output pairs (the colours the unit calls 01, 05, 09 and 13; the user's choice, 2026-10-12). That the unit shows the colour a file gives, and takes one colour on several tracks of a scene, is **Unverified**: first written on 2026-10-12, in `Quiet112.tpkg`.

**`chokeGroup` (Confirmed, hardware):** `-1` = Off, `0` to `7` = groups 1 to 8. The loader's default of `0` means group 1, so every pad shares a choke group and only one plays at a time. Set `-1` for independent pads. The generated SP-16 test used `-1`, but its triggers were staggered, so simultaneous triggering has not been heard on the SP-16 (Unverified there).

### PlaybackData (the fields a generator writes)

Order as saved: `startSample`, `loopStartSample`, `lengthSample` (int32 frames), `bLoop`, `bReverse`, `pitchCent`, `stretchMode`, `audioSourceUrl` (string), `bBypass`.

- `stretchMode`: the loader's template uses `MT`; the unit saves `Off`. Use `Off` for stems so nothing is time-stretched.
- **`stretchMode` values (Confirmed, file, 2026-10-12):** `Off`, `MT` (shown as M.TMP: the length follows the tempo, the pitch stays) and `Resample` (shown as RESMPL: played faster or slower with the tempo). From a project saved on the SP-16 with one track set to each; the setting changes no other field. Both work from the `bpm` in the sample's `.dat`.
- `bLoop` false gives one-shot playback.
- `SliceData` (Confirmed, file; from four SP-16 saves of one 81415-frame sample, see `Fixtures/slice-*.prj`):
  - `nGrids` (int32) is the number of slices. Changing the count on the unit from 16 to 8 changed this field and nothing else. An empty pad holds 1.
  - `grids` is empty (`07 00`) until a slice point is moved by hand. Until then the unit divides the sample into `nGrids` equal slices.
  - Once a point is moved, `grids` is an array of `nGrids + 1` int32 frame positions: the boundaries, from 0 to `lengthSample`, both included. The end of one slice is the start of the next, so there is one value per boundary. Points that were not moved hold `round(k * lengthSample / nGrids)`.
  - Each item is `01 05 01` and four bytes little-endian (`vi(5)`, type int32, value).
  - `windowOffset` (0) was not changed by any of this.
  - Nothing in the track says that a pad is sliced: `trackMode` stays `Sample` and `triggerMode` stays `OneShot`. Slice play is a mode of the pads on the unit and leaves the other pads alone (Reported, user).
  - The loader page writes `grids` in this form for its "native slice" (0 and the sample length included, at most 17 values) and leaves `nGrids` at its template's 16 whatever the number of slices (Reported, DJS-1000).
  - The SP-16 loads `grids` written by a generator and plays them in slice mode, with counts below 16 as well (Confirmed, SP-16, 2026-10-10).
  - Unverified: what the unit does when `nGrids` and the length of `grids` disagree, and how a step in a pattern records which slice it plays.

## 5. Patterns

### Storage

- Patterns are **per scene**: each scene has 16 patterns (1 to 16 on screen).
- A pattern is stored as `PatternData<N>` with N the zero-based pattern number. **An empty pattern has no struct.** The arranger plays an empty pattern as silence. (Confirmed, file and SP-16.)
- A pattern need not trigger every pad that holds a sample in its scene, and a pad of the scene may hold none: Strands leaves a pad empty, and out of its pattern, where a stem is silent for the whole of that pattern, as taking a track out of a pattern on the unit does (Reported, user). In generated packages so far the empty pads were the ones after the last pattern of a scene (Confirmed, SP-16, `Stems112.tpkg`); empty pads between pads in use were first written on 2026-10-12, in `Quiet112.tpkg` (**Unverified**).
- A pattern that has a struct and a `Length` but no trigger at all is **Unverified**: whether the arranger plays it for its length. Strands does not write one: a pattern in which every stem is silent keeps one sample.
- Scalars, in the order the unit writes them: `Steps` (blob), `Length` (int32), `TrackMutes` (array of 16 bool), `TrackSolos` (array of 16 bool). A generated file that wrote `Length`, `TrackMutes`, `TrackSolos`, `Steps` loaded fine, but the unit's order is the safe choice.
- Copying a scene copies its patterns.

### Length

- On screen a pattern is 1 to 4 bars, and the number of beats in the last bar is 1 to 16.
- **`Length` is the total step count:** `16 × (bars − 1) + beatsInLastBar`. Maximum 64. (Confirmed, file: 2 bars with 5 beats in the second stored 21.)
- **Each pattern has its own length.** (Reported; the file stores `Length` inside each `PatternData<N>`.) Only one pattern plays at a time, so no conflict arises.
- When the bar count is increased on the unit, it duplicates the existing steps into the new bars, and steps past `Length` stay in the file. A generator should write only the steps it wants and clear any beyond `Length`.

### Steps blob

Inflates to 155,648 bytes: 1,024 records of 152 bytes. Record index = **pad × 64 + step**, pad and step counted from 0. Pad 5, step 9 on screen is record 4 × 64 + 8 = 264. (Confirmed, SP-16.)

**Record (152 bytes):**

| Offset | Size | Meaning |
| --- | --- | --- |
| 0 | int32 | pad number (0-based); `-127` for an unused record |
| 4 | int32 | step number (0-based); `-126` for an unused record |
| 8 | int32 | trigger kind: 15 Full, 11 Half, 8 Param (15 in an unused record) |
| 12 | 35 × 4 bytes | table of parameter entries |

- Default entries are `(id, 0x00, 0x80, 0x3f)`: ids 0 to 22, then ids 5 to 10, then ids 5 to 10 again. Building an unused record from this description reproduces the unit's default byte for byte.
- To place a trigger, write the pad and step into the first two ints of the record at pad × 64 + step. Every other byte stays at its default. (Confirmed, SP-16.)
- Entry id 13 holds the step volume. Setting 50 changed that entry to `(13, 50, 0, 0)`.
- **Trigger kind** (Confirmed, file: `steps-1.prj`, saved on the DJS-1000): Full restarts the sample, its envelope and the step's settings; Half the envelope and settings only; Param the settings only. Half and Param alone make no sound (Confirmed, DJS-1000 render).

**Step settings** (Confirmed, file: `steps-1.prj` and `steps-2.prj`, one setting per step with the screen values noted; Confirmed, DJS-1000: a package whose settings Strands wrote showed and played as meant).

An entry is its id in byte 0 and a **22-bit signed value** in bytes 1 to 3 (−500 is `0c fe 3f`). `00 80 3f` in bytes 1 to 3 means the step leaves the setting to the track. Among the first 23 entries the id is the entry's place:

| Id | Setting | Value |
| --- | --- | --- |
| 0 | PITCH | cents (+7 is 700) |
| 1 | START | frames |
| 2 | LOOP START | frames |
| 3 | LENGTH | frames |
| 4 | VELOCITY | 0 to 127 |
| 5, 6, 7 | ATTACK, HOLD, RELEASE | knob |
| 8 | LFO SPEED | knob |
| 10 | OFFSET | 1/48 of a step, ±48 |
| 11 | RETRIGGER CNT | triggers in all, up to 64 (Reported, DJS-1000) |
| 12 | RETRIGGER SPD | place in the list 1/64, 1/32T, 1/32, 1/16T, 1/16, 1/8T, 1/8, 1/4T, 1/4 |
| 13 | step volume | 0 to 127 |

- A **knob** of 0 to 127 is stored as `floor(knob × 2^21 / 127)`. With the envelope's SYNC on, HOLD is on another scale, not mapped.
- The last twelve entries are the six settings of FX1 and of FX2. A set one carries another id: FX1's first setting (the multi-mode filter's cutoff) was written as id 41, its value as the fraction of 2^21.
- **Choosing a slice** for a step writes START, LOOP START and LENGTH as the slice's boundaries. The step keeps them when the slice is moved afterwards (`steps-2.prj`). A START set by its knob writes START alone.
- Ids 9 and 14 to 22 are not known.

**Playback of steps** (Confirmed, DJS-1000, measured in its renders, which are frame-exact): a trigger fades the sound it cuts off in a straight line over the 44 frames (1 ms) before it, and starts the new sound at full level at once, unless ATTACK is set (its shortest, 1, is 6 ms). Retrigger repeats replay the step's own range, each landing within a frame of its step time, and ATTACK applies again on each (`P03Slicer`, test stems). HOLD and RELEASE are 6.25 ms a unit at 5 to 20; HOLD 40 is more than 500 ms. Velocity 32, 64, 100 and 127 play at −15.3, −6.9, −2.3 and 0 dB.

## 6. Arrangement (SP-16 only)

`ArrangementData0` has two scalars: `length` (int32, the number of used sections) and `Sections` (blob, zlib).

`Sections` inflates to 7,992 bytes: **999 records of 8 bytes**, played in order from record 0. Unused records hold the default `00 00 01 00 00 00 00 00`.

| Byte | Field | Notes |
| --- | --- | --- |
| 0 | scene | 0-based, 0 to 15 (screen 1 to 16) |
| 1 | pattern | 0-based, 0 to 15 (screen 1 to 16) |
| 2 | repeat | stored as the count itself, 1 to 32 |
| 3 | reserved | 0 in every file so far |
| 4-7 | bpm override | float32 little-endian; 0 means no override; maximum on screen is 220 |

Evidence (all Confirmed, file; the whole layout Confirmed, SP-16):

- Repeat 2 gave `02` and repeat 32 gave `20` in byte 2. Scene 2 gave `01` in byte 0 and pattern 2 gave `01` in byte 1.
- Tempo 133 gave `00 00 05 43` and 133.3 gave `cd 4c 05 43`, exactly float32 133.0 and 133.3. The unit stores one decimal place on screen; whether more is accepted is untested.
- The record count (999) is the file's capacity. The unit's own limit on sections is untested.

Editing behaviour (Confirmed, file):

- A new section is the default record (scene 1, pattern 1, repeat 1, no override).
- **Slots are stored in play order.** A repeated scene (A, B, A) is just a repeated record, with no back-references.
- **Insert** shifts the following records one slot later. **Delete** shifts them earlier and resets the vacated last slot to the default.
- **Clear** resets every record and sets `length` to 0. In the saved clear file `bPlayArrangement` was also false; whether Clear did that or the arranger was switched off by hand is not known.

**Tempo override is sticky (Reported).** After a section with a bpm override plays, the later sections and later plays keep that tempo, and it does not revert on its own. A generator should write an explicit bpm into every section, including the first.

## 7. Routing (per scene)

`RoutingData` is a child of each scene (Confirmed, file: changing one scene left the other 15 at their defaults, so routing is set per scene).

Scalars, in order: `assignMode`, `headphoneOutCh`, `effectMode` ("EffectModeSend"), `bDaveSmithFilter`, `daveSmithRouteMode`, `daveSmithSendMode`, `daveSmithSendTrack`. Children `TrackRoutingData0..15`, one per pad in pad order, each with a single scalar `assignableOutCh`.

| `assignMode` | On screen | `headphoneOutCh` |
| --- | --- | --- |
| `Master` | M+6ch (default) | `CueMaster` |
| `4Assign` | 8ch | `Cue12` |

The unit changes the headphone cue automatically with the mode, so write the two together.

`assignableOutCh` values (Confirmed, file; the 8ch pairs Confirmed, SP-16 on the mixer):

| Screen | Value | Mode |
| --- | --- | --- |
| dashed (master) | `AssignToNull` | M+6ch only |
| 1-2 | `AssignTo12` | 8ch |
| 3-4 | `AssignTo34` | both |
| 5-6 | `AssignTo56` | both |
| 7-8 | `AssignTo78` | both |
| mono 5, mono 6 | `AssignTo5`, `AssignTo6` | M+6ch (3 to 8 available) |
| 1 to 8, one output each | `AssignTo1` to `AssignTo8` | 8ch |

- In 8ch mode the unit fills every unassigned pad with `AssignTo12`. A generator should set all 16 explicitly.
- Single outputs in 8ch mode are stored as `AssignTo1` to `AssignTo8` (Confirmed, file: `route3-mono-8ch.prj`, saved on the SP-16 with tracks 1 to 8 sent to outputs 1 to 8, differs from the empty project only in its name, scene 1's mode and cue, and the sixteen values, the last eight filled in by the unit as `AssignTo12`). A generated project using them plays each pad on its output (Confirmed, SP-16, 2026-10-10, `SingleOutA.tpkg`: eight tones on outputs 1 to 8). What the unit does with the second side of a stereo sample on one output is not known.

**For four stems:** `assignMode` `4Assign`, `headphoneOutCh` `Cue12`, and drums, bass, other, vocals on `AssignTo12`, `AssignTo34`, `AssignTo56`, `AssignTo78`. Every scene that is used needs its own `RoutingData` set. A scene left at the default sends its pads to master.

**For stems on the master output:** nothing is written. Every scene stays in `Master` mode with every pad on `AssignToNull`, as the empty project has them (Confirmed, file: `arr0.prj`), and each stem is still a pad of its own, started with the others on step 1. A pad on master plays (Confirmed, SP-16, in every full-mix package), and pads started together all sound (Confirmed, SP-16, `Stems112.tpkg`, in 8ch mode). The two together are Unverified: `Master112.tpkg`, generated on 2026-10-11, is the same four stems as `Stems112.tpkg` with the routing left alone, and has not been on the unit. One package cannot hold both kinds: pads on master beside pads on 3-4, 5-6 or 7-8 would be `Master` mode with those assignments, which no generated project has tried, and a generator should refuse it.

**Result (`Stems112.tpkg`, Confirmed, SP-16, 2026-10-10):** three scenes in 8ch mode, each pattern triggering four pads on step 1 with choke Off, the samples about 8 s long. The four stems came out of the pairs they were assigned, with no glitching at the pattern boundaries or at the scene changes.

## 8. Sample adjunct file (`name.wav.dat`)

A tree in the same grammar: struct `SampleAdjunctData` with `bar` (float64), `bpm` (float64), `sampleNum` (int32, frame count) and no children.

- The loader writes `bar` 2.0, `bpm` 0.0 for a one-shot kick. A generated package using exactly those values loaded and played correctly on the SP-16 (0.4 s tones).
- For the DJS-1000 stem test, `bar` was set to the length in beats and `bpm` to the tempo, and the unit loaded it. What `bar` actually means is Unverified.
- **With `bpm` 0.0 the SP-16's Time Stretch and Resmpl run at the wrong speed for a sample cut from a song (Reported, user, 2026-10-12).** A generator should write the song's tempo. Strands writes `bpm` as the tempo and `bar` as the sample's length in bars, to a sixteenth (4.0 for a pattern of four bars, 3.5 for a lead-in of fourteen beats), without the tail the sample fades out over. That the unit reads `bar` as a count of bars is **Unverified**; the stem test above put a count of beats there.

## 9. Package (`.tpkg`)

A stored (uncompressed) zip with a flat layout: `<name>.prj`, `<sample>.wav`, `<sample>.wav.dat`. Entry dates are 1980-01-01. (Confirmed, SP-16: a package built this way loaded.)

- `audioSourceUrl` has the form `$Int0/%Tmp/<name>.wav`.
- When Project Creator imports a package it rewrites the paths to `$Pc/%PcISmp/[Imported]/<project>/<name>.wav`. That is fine to ignore.

## 10. Samples

- **Mono** 16-bit files load from a package and play, on a single output and on a pair (Confirmed, SP-16, 2026-10-10, `SingleOutB.tpkg`). A whole stem as mono samples of about 8 s, sliced, on a single output beside stereo samples on pairs also plays correctly (Confirmed, SP-16, 2026-10-10, `Stems112m.tpkg`). `lengthSample` and `sampleNum` are the frame count, as for stereo. A mono sample takes the memory its file holds, half that of a stereo one of the same length (Confirmed, SP-16, 2026-10-10: packages of 60 s mono samples holding 140 and 268 million bytes of audio, 27 and 51 samples, loaded in full).
- **44.1 kHz, 16-bit stereo** loads. **16-bit and 24-bit** WAVs are accepted (Reported). Other sample rates have not been tested; the loader page re-renders anything that is not 44.1 kHz/16-bit.
- **Maximum 64 s per sample.** Longer files do not load (Reported).
- **Total sample memory is between 268,000,000 and 269,000,000 bytes of 16-bit audio** (Confirmed, SP-16, 2026-10-10), taken to be 268,435,456 (256 x 2^20). Packages of distinct 60 s stereo samples holding 259, 265 and 268 million bytes of audio loaded in full; 269 and 272 million did not. The 250 MB once given here was Reported and is wrong. The unit does not show per-project memory use.
- **Over the limit** the unit shows an out-of-memory message saying a sample could not be loaded, leaves that sample's pad empty and loads the rest (Confirmed, SP-16). In both packages that were too big, 26 samples over two scenes, the one left out was the 18th (scene 2, track 2), not the last and not the shortest: samples are not loaded in pad order.
- **A file used on several tracks is held once, in one scene or across scenes** (Confirmed, SP-16, 2026-10-10). In `Mem268m`, which leaves 435,456 bytes free, the user pasted tracks holding 60 s mono samples (5.3 MB each) onto 8 more pads, some of them tracks from scenes 1 and 2 pasted into scene 4, saved and reloaded with no out-of-memory message. Held separately they would have needed 42 MB more.
- A 16-bit stereo 44.1 kHz sample is 176.4 kB/s, so 64 s is 11.3 MB and the limit is about 25.4 minutes of audio, or about 6.3 minutes of four stems.
- Audio keeps playing after a pattern or section ends. Samples are not cut at boundaries (Reported).

## 11. Generating a project (recipe, Confirmed on an SP-16)

1. Start from a unit-saved project. Any SP-16 save works; `arr0.prj` (empty) or `route1.prj` are both fine templates. Do not build a project from scratch.
2. Set `projectBpm`. Set `bPlayArrangement` true if the arranger is used. Set `sceneIndex` and `patternIndex` to 0.
3. For each scene used: set `name`; for each pad, set `PlaybackData` `audioSourceUrl`, `lengthSample` (frame count), `startSample` 0, `loopStartSample` 0, `bLoop` false, `stretchMode` `Off`, and `chokeGroup` -1.
4. Remove any `PatternData<N>` the template carries. For each non-empty pattern, build a new `PatternData<N>` with the step blob (all 1,024 records at the default, then write pad and step into each trigger's record), `Length`, and 16 false values each for `TrackMutes` and `TrackSolos`. Insert it after `TrackData15`, before `SendFx`.
5. Set each used scene's `RoutingData` (section 7).
6. Write `ArrangementData0`: start from 999 default records, write the sections from record 0 (scene, pattern, repeat, 0, float32 bpm), set `length`, and recompress.
7. Recompress `Steps` and `Sections` with zlib level 6, write the `.prj`, write one `.dat` per WAV, and zip them stored with a flat layout.

**Result (`rev1.tpkg`):** two scenes with four 0.4 s sine samples each (one tone per pad), routed to outputs 1-2, 3-4, 5-6 and 7-8, with one-bar patterns (scene 1 pattern 1, scene 2 pattern 2), and the arrangement scene 1 ×2, scene 2 ×1, scene 1 ×1 at 140 BPM. On the SP-16: the arranger showed those three sections, the playback order was correct, each pad came out of its own pair on the mixer channels, the names were correct, and there was no problem at the section boundaries.

## 12. Fixtures for tests

| File | Source | Use |
| --- | --- | --- |
| `one.tpkg`, `02` to `08` | loader page, DJS-1000 | container, grammar, steps, round trip |
| `arr0.prj` | SP-16 | empty baseline, empty arrangement, no patterns |
| `arr0-one-section.prj` to `arr6-bpm-decimal.prj` | SP-16 | one change per file, section record fields |
| `arr7-pattern2-step.prj`, `arr8-length.prj` | SP-16 | `PatternData1`, `Length` 21 |
| `arr9` to `arr13` | SP-16 | multiple sections, insert, delete, clear |
| `route1.prj`, `route2.prj` | SP-16 | routing modes and strings |
| `route3-mono-8ch.prj` | SP-16 | single outputs in 8ch mode |
| `rev1.tpkg` | generated | full package that loads and plays on the SP-16 |

Every unit-saved file should parse with all bytes consumed and re-emit the inflated payload byte for byte.

## 13. Open questions

- Whether the sequencer honours a bpm with more than one decimal place (the file stores a float32; the unit displays one decimal).
- Byte 3 of the section record, always 0 so far.
- The `bar` field in the `.dat`.
- Step entries 9 and 14 to 22, the times of ATTACK, HOLD and RELEASE above 20, HOLD with SYNC on, and the packing of `patternQuantizeUnit` and `padQuantizeUnit`.
- How scene changes are quantized during arrangement playback.
- Why loading a project over a large one that is already loaded locks the unit up, and how large either has to be (section 10). The limit itself and what happens above it are answered there.
- What the unit plays when a stereo sample is sent to a single output: one side, or both added.
- Whether the scene `bpm` field affects arrangement playback.
- The highest section count the unit allows (the file holds 999).
