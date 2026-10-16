# Claude Code Kickoff: Stems-to-SP-16, Milestones 1 and 2

*Read this first, then the two attached documents. This file says what to build now and how to know it is done. The other two say what the app is and how the file format works.*

- `Stems-to-SP-16_App__Project_Brief.md`: authority on the app, its design and its open risks.
- `SP-16_DJS-1000_project_format_spec.md`: authority on the file format.
- If this file disagrees with either, stop and ask rather than guess.

## 1. Scope of this kickoff

The app takes 1 to 4 aligned audio tracks (typically four stems), analyses them, and writes a Pioneer DJ Toraiz SP-16 `.tpkg` that plays the whole song from the arranger. Only the first two milestones are in scope here:

- **Milestone 1:** a plain C# format library that reads, patches and writes SP-16 project files, proven by byte-exact round trips of real unit-saved files.
- **Milestone 2:** a command-line tool that uses that library to generate `.tpkg` packages: a real-stem export, and a set of hardware test packages whose results will decide later design.

No Unity, no UI, no audio analysis, no ffmpeg in these milestones. Unity comes later and will consume the library as a plain assembly, so keep it free of Unity dependencies.

## 2. Ground rules

1. **Evidence labels are binding.** Anything the spec marks Unverified or Reported is not a fact. Do not build behaviour that depends on it without isolating it behind a named setting and a comment pointing at the open question.
2. **Do not invent format details.** If a byte or field is not described in the spec, preserve it from the template rather than interpreting it.
3. **Start from a unit-saved template** (spec section 11). Never build a project from scratch.
4. **Keep key order** when emitting the tree. The unit writes scalars and children in a fixed order.
5. **No Python is available.** The decoder is reimplemented in C# from the spec and the fixtures. Where the spec is ambiguous, the fixtures win; log the discrepancy in `docs/spec-notes.md`.
6. **Ask before deciding** anything that changes the format library's public API after milestone 1 is signed off.
7. Prefer small, tested commits. Every behaviour in the spec gets a test that names the spec section.

## 3. Technical decisions

| Decision | Choice | Reason |
| --- | --- | --- |
| Library target | `netstandard2.1` | Loadable by Unity later |
| Test and tool target | `net8.0` | Current, runs on the M4 Max Mac natively |
| Test framework | xUnit | Standard |
| Dependencies in the library | None beyond the BCL | Keeps Unity import simple |
| Compression | `System.IO.Compression.DeflateStream`, with hand-written zlib wrapper (2-byte header, Adler-32) | `ZLibStream` is not in netstandard2.1 |
| Container | Stored `.tpkg` zip written with `System.IO.Compression.ZipArchive` using `CompressionLevel.NoCompression`, entry dates 1980-01-01 | Matches spec section 9 |

**Compression caveat.** The spec says blobs recompressed with Python's zlib level 6 reproduce the unit's bytes. .NET's deflate may not produce identical output. So the round-trip tests must be defined as follows, and this is not a failure to be "fixed":

- **Tree round trip:** parse, re-emit with blobs kept as their original compressed bytes, and compare the inflated payload byte for byte. This is the primary test.
- **Blob content round trip:** inflate each blob, compare the contents to the re-parsed content. Re-compressing is allowed to differ in compressed bytes as long as it inflates to identical contents.
- **Outer container:** the `.prj` wrapper (magic, raw DEFLATE, Adler-32) is validated by checksum and by inflating, not by compressed-byte equality.

## 4. Repository layout

```
/
├─ README.md
├─ docs/
│   ├─ Stems-to-SP-16_App__Project_Brief.md
│   ├─ SP-16_DJS-1000_project_format_spec.md
│   ├─ spec-notes.md              (discrepancies and decisions, kept up to date)
│   └─ hardware-tests.md          (what each test package is and what to listen for)
├─ src/
│   ├─ Sp16.Format/               (netstandard2.1, the library)
│   │   ├─ Container/             (.prj wrapper, zlib helpers, Adler-32, .tpkg writer)
│   │   ├─ Tree/                  (vi numbers, struct and scalar reader/writer)
│   │   ├─ Model/                 (typed views: Project, Scene, Pad, Pattern, Arrangement, Routing)
│   │   ├─ Blobs/                 (StepsBlob, SectionsBlob)
│   │   ├─ Adjunct/               (.wav.dat reader/writer)
│   │   └─ Templates/             (embedded arr0.prj)
│   ├─ Sp16.Audio/                (netstandard2.1, WAV read/write only for now)
│   └─ Sp16.Tool/                 (net8.0 console, the CLI)
├─ tests/
│   ├─ Sp16.Format.Tests/
│   └─ fixtures/                  (unit-saved files; see section 5)
└─ out/                           (generated packages, git-ignored)
```

`Sp16.Format` has no reference to `Sp16.Audio`. The tool joins them.

## 5. Fixtures

The user will place these in `tests/fixtures/` before work starts. Check they are all present and list any that are missing before writing code.

`one.tpkg`, `02` to `08` (DJS-1000 projects), `arr0.prj`, `arr0-one-section.prj` to `arr6-bpm-decimal.prj`, `arr7-pattern2-step.prj`, `arr8-length.prj`, `arr9` to `arr13`, `route1.prj`, `route2.prj`, `rev1.tpkg`.

Exact filenames may differ from the spec's shorthand. Test discovery should enumerate the directory rather than hard-code names, so a renamed file is still tested.

`arr0.prj` is also embedded in the library as the export template (spec section 11 step 1). Keep one copy in fixtures and one embedded resource, and add a test that asserts they are identical.

## 6. Milestone 1: format library

### 6.1 Deliverables, in order

1. **Variable-width numbers and the tree grammar** (spec section 2). Reader and writer for `struct`, `scalar`, every type 1 to 8, the null scalar, and lengths above 255. Preserve scalar and child order.
2. **`.prj` container** (spec section 1): magic, raw DEFLATE, Adler-32 big-endian. Validate the checksum on read; compute it on write.
3. **Blob handling** for `Steps` and `Sections` (spec sections 5 and 6). Keep the original compressed bytes on read; decompress lazily; recompress only if the blob was modified.
4. **Steps blob model:** 1,024 records of 152 bytes, record index = pad × 64 + step, the default unused record built from the description in spec section 5. Test that a default-built blob equals the unit's default byte for byte.
5. **Sections blob model:** 999 records of 8 bytes (scene, pattern, repeat, reserved, float32 tempo). Preserve byte 3. Implement insert, delete and clear as described in spec section 6, as operations on the model.
6. **Typed model** over the tree: `Project`, `Scene`, `Pad`, `Pattern`, `Routing`, `Arrangement`. Typed properties for the fields the spec lists; everything else stays reachable and preserved through the raw tree. A parse then a model edit then an emit must not disturb untouched fields.
7. **`.wav.dat` adjunct** reader and writer (spec section 8).
8. **`.tpkg` reader and writer** (spec section 9), flat layout, stored entries.

### 6.2 Tests

- **Round trip over every fixture:** every `.prj` parses with every byte consumed, and re-emitting gives the identical inflated payload. Every `.tpkg` unpacks, and its `.prj` passes the same test.
- **Field facts from the spec,** each as a named test with the spec section in the name: repeat 2 stores `02`, repeat 32 stores `20`; scene 2 stores `01`; tempo 133 stores `00 00 05 43`; 133.3 stores `cd 4c 05 43`; `Length` 21 for 2 bars with 5 beats in the second; pad 5 step 9 is record 264; step volume 50 sets entry id 13 to `(13, 50, 0, 0)`.
- **Editing behaviour:** insert, delete and clear sections against the `arr9` to `arr13` fixtures.
- **Routing:** `route1` and `route2` strings parse and re-emit; building 8ch routing (`4Assign`, `Cue12`, four pairs) produces the expected strings.
- **Patching test:** load `arr0.prj`, set one pad's `audioSourceUrl` and `lengthSample`, emit, re-parse, and confirm that only the intended fields changed (diff the trees).
- **Generation test:** reproduce `rev1.tpkg`'s structure from code using the recipe in spec section 11, then compare its parsed tree with the fixture's. Differences must be explainable (such as `projectFilePath`); list them in `docs/spec-notes.md`.

### 6.3 Definition of done

- All fixtures round-trip.
- All the spec fact tests pass.
- The generation test reproduces `rev1`'s structure, with any differences documented.
- `Sp16.Format` builds with no warnings for `netstandard2.1` and has no dependencies beyond the BCL.
- `docs/spec-notes.md` lists every discrepancy found between the spec and the fixtures, or states that there were none.
- A short report of anything the spec left ambiguous.

**Stop here and report before starting milestone 2.**

## 7. Milestone 2: the command-line tool

### 7.1 Commands

```
sp16 inspect <file.prj|file.tpkg>
sp16 export-stems --stems <a.wav> <b.wav> <c.wav> <d.wav> --bpm <float> --first-beat <seconds> --out <name>
sp16 hwtest <test-id> --out <dir>
sp16 hwtest --list
```

- **`inspect`:** prints scenes, pad sources, patterns (with `Length`), routing and the arranger list. This is also how fixtures and generated files are checked by eye.
- **`export-stems`:** the real-stem prototype (section 7.2).
- **`hwtest`:** generates the hardware test packages (section 7.3).

### 7.2 `export-stems` (spec stage 2, with real stems)

Takes four WAVs of identical length and sample rate, and a BPM and first-beat offset supplied by hand (no analysis yet). Produces one scene, long-chunk playback:

1. Validate: equal length, equal sample rate, 44.1 kHz and 16-bit (convert 24-bit to 16-bit with dither off; reject other rates for now with a clear message).
2. Cut each stem into chunks of whole bars, each at most 64 s and at most one bar-aligned boundary, starting at the first beat. Apply a short fade at each chunk's end (a few milliseconds).
3. Place chunks on pads: stem k uses pads in a fixed block, in play order. A song that needs more than four chunks per stem is rejected for now with a message (multi-scene packing is a later milestone).
4. Build one pattern per chunk start that triggers the right pads, and arranger sections that sequence them, using empty patterns with repeat counts to wait. **The length of an empty pattern is unconfirmed, so the waiting design must be driven by a single constant** `EmptyPatternBars` (default 1) that hardware test T1 will confirm or correct.
5. Write an explicit tempo into every arranger section, set `projectBpm` to the same value, set 8ch routing, `chokeGroup` -1, `bPlayArrangement` true, `sceneIndex` and `patternIndex` 0.
6. Write the `.tpkg` with `.dat` files (`bar` 2.0, `bpm` 0.0, `sampleNum` = frame count, as the SP-16 accepted).
7. Print a validation summary: sample memory estimate against 250 MB, section count, longest sample, pattern lengths.

All the spec validation rules apply (samples at most 64 s, repeat at most 32, pattern length at most 64, section count at most 999).

### 7.3 Hardware test packages

Each is generated from synthetic tones so the correct result is audible and no commercial audio is involved. Every package has `README` text printed to the console and written to `docs/hardware-tests.md`: what it contains, exactly what to listen for, and what each outcome means for the design.

Tone design: one distinct pitch per output pair, with a short click at every bar line, so drift, overlap and cuts are audible and countable.

| ID | Tests | Package |
| --- | --- | --- |
| **T1** | Empty pattern length (new) | One scene with a tone triggered at section 1, then a section with an empty pattern and repeat 4, then a second tone. Listen for the gap: 4 bars implies a 1-bar empty pattern, 16 bars implies 4. |
| **T2** | Simultaneous stems, choke Off (brief test 4) | Four 20 s tones triggered together on step 1, each on its own pair. All four should sound. |
| **T3** | Long sample across boundaries (brief test 3) | A 20 s tone in scene 1 with sections running into scene 2 and back. Listen: does it continue, get cut at the scene change, or overlap its own retrigger? |
| **T4** | Chunk handoff at a bar line (new) | Two 16-bar chunks per stem, cut on bar lines, second triggered by the next section. Listen for gap, overlap or click at the join. |
| **T5** | Fractional tempo (brief test 5) | A click track at 133.28 BPM with a 60 s chunk and a trigger 60 s later, to compare against a click or Pro Link. |
| **T6** | Pattern lengths (brief test 6) | Three patterns in one scene of 1, 2 and 4 bars, each audibly different. |
| **T7** | Memory limit (brief test 1) | Parameterised: `--mb <n>` produces about n MB of distinct 16-bit samples across two scenes. Run at 200, 240, 250, 260. |
| **T8** | Memory sharing (brief test 2) | One 11 MB file referenced by every pad of two scenes, over 250 MB combined. |

T7 and T8 produce large packages; make sure `out/` stays git-ignored and that generation streams to disk rather than holding everything in memory.

### 7.4 Definition of done

- `inspect` works on every fixture and on its own generated output.
- `export-stems` produces a package from four real WAVs that `inspect` describes correctly, and the user can load it on the SP-16. (Whether it plays the whole song is exactly what T1 and T4 will settle, so a first failure there is information, not a bug.)
- All T1 to T8 packages generate, with `docs/hardware-tests.md` complete.
- Library tests still pass, and every new behaviour has a test.

## 8. What not to do yet

- No audio analysis, beat detection, section detection or repeat detection.
- No ffmpeg, multi-format import or resampling.
- No Unity project or UI.
- No scene reuse across the arranger or multi-scene packing.
- No time-stretching, ever (`stretchMode` is `Off`).
- No format guesses outside the spec.

## 9. Questions to ask the user before starting

1. Are all the fixtures in `tests/fixtures/`, and are the filenames as listed in the spec?
2. Which real stems will be used for the `export-stems` check, and what are their sample rate, bit depth and length? (Brief open problem 7 is still unconfirmed for NUO-STEMS output.)
3. Is the SP-16 available for T1 to T8 as each package is generated?

## 10. Reporting

At the end of each milestone, report:

1. What was built and what passes.
2. Spec ambiguities and fixture discrepancies found.
3. Anything marked Unverified that the code relies on, and where it is isolated.
4. Proposed next step.
