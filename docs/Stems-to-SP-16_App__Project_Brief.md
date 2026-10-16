# Design Doc: Stems-to-SP-16 Arrangement App

*Hand-off document for a new conversation or for Claude Code. It describes the idea, the hardware, what has been verified, the design, and what is still open. Attach `SP-16_DJS-1000_project_format_spec.md` alongside it; that file is the authority on the file format, and this one is the authority on the app.*

Evidence labels: **Confirmed** (tested on the SP-16 or read from a unit-saved file), **Reported** (observed on the unit by the user, not file-tested), **Unverified** (assumption).

## 1. The idea

An app that takes a commercial track already split into four stems, analyses its structure, and generates a Pioneer DJ Toraiz SP-16 project so the whole song plays back by pressing Start. The four stems come out of the SP-16's four stereo output pairs (8 outs) into separate mixer channels, so each stem can be EQ'd, filtered and effected live.

## 2. The performance setup

- **Sampler:** Pioneer DJ Toraiz SP-16. The DJS-1000 shares the project file format but has no arrangement mode and no separate outputs.
- **Mixer:** DJM-2000 Nexus. The SP-16's 8 outputs go to its 4 channels, one stereo pair per channel.
- **Intent:** play full songs from the sampler while keeping per-stem control on the mixer, which a normal laptop DJ setup doesn't give.
- **Test hardware:** the user owns both an SP-16 and a DJS-1000. Everything involving arrangement and output routing has to be tested on the SP-16.
- **Platform:** Unity (C#), standalone desktop build. The coding is done with Claude Code.

## 3. Workflow

1. **Separate:** run the track through **NUO-STEMS 4** in **DAW mode**, which writes WAV stems (drums, bass, other, vocals). Enable "Sum of Stems = Original". The demo only processes 20 seconds, so full tracks need the paid licence. NUO-STEMS is closed software; the app only reads its files.
2. **Import:** drop the four WAVs into the app. Validate identical length and sample rate.
3. **Analyse:** detect the beat grid (BPM and first downbeat), the song sections, and repeated material.
4. **Arrange:** the app proposes a layout of scenes, patterns and arranger sections. The user corrects it in an editing step.
5. **Export:** write an SP-16 `.tpkg` (project, WAVs and `.dat` files) with each stem routed to its output pair.
6. **Perform:** load on the SP-16 and press Start.

## 4. What the SP-16 gives us (the target model)

| Thing | Capacity |
| --- | --- |
| Scenes | 16, each with its own 16 pads (samples) and its own routing |
| Patterns | 16 per scene, each 1 to 4 bars (up to 64 steps), each with its own length |
| Arranger | up to 999 sections in the file; each section plays one pattern of one scene, repeated 1 to 32 times, with an optional tempo override |
| Sample | at most 64 s, 16 or 24-bit, 44.1 kHz tested |
| Sample memory | 250 MB total (Reported); per-project usage is not shown on the unit |
| Outputs | 4 stereo pairs in 8ch mode |

**How playback behaves (Reported unless stated):**

- Samples are one-shot and are **not cut when a pattern or section ends**. Audio runs on past the boundary, so a sample must be cut so that it ends where the next trigger of the same material begins, or it will overlap.
- The tempo override in an arranger section is **sticky**: it carries on into later sections and later plays and does not revert to the project tempo. Write an explicit tempo into every section.
- Fractional tempo is assumed to be honoured, since the unit syncs with Pro Link. The file stores a float32, and 133.3 was stored exactly as such. Not heard against a click yet.
- Scene changes inside the arranger caused no problems with the short test samples (Confirmed, SP-16). Behaviour with long samples running across a scene change is untested.
- An arranger section that points at an empty pattern plays silence (Confirmed, SP-16).

## 5. Established facts

- The `.prj` format is decoded well enough to read, patch and generate. All 25 sample projects (8 from the DJS-1000, 17 from the SP-16) parse with every byte consumed, and re-emitting the parsed tree reproduces each payload byte for byte.
- **Generated projects load and play on the SP-16.** The test package `rev1.tpkg` had 2 scenes × 4 sine samples, routed to 4 output pairs, one pattern per scene, and an arrangement of three sections with a tempo override. The arranger showed those sections, the playback order and output routing were correct, the sample names were correct, and there was no problem at the boundaries.
- **Arranger section layout** (8 bytes): scene, pattern, repeat, reserved, float32 tempo. Insert, delete and clear behaviour is known.
- **Patterns** are stored per scene as `PatternData<N>`, and an empty pattern has no struct. `Length` is total steps: 16 × (bars − 1) + beats in the last bar. A trigger is a write of pad and step into the record at pad × 64 + step.
- **Routing** is stored per scene. 8ch mode is `assignMode` `4Assign` with `headphoneOutCh` `Cue12`, and the four pairs are `AssignTo12`, `AssignTo34`, `AssignTo56`, `AssignTo78`.
- **Choke group:** `-1` is Off. The loader's default of `0` puts every pad in group 1, so only one stem plays at a time.
- The user's tracks have **constant tempo** (timing is solid in Traktor), so grid detection is a fitting problem rather than a tracking problem.

## 6. Design

### 6.1 Grid detection

- Run onset detection on the **drums stem** (spectral flux), fit a straight line of beat number against time for a precise BPM and first-beat position, and apply it to all four stems.
- Alternatively import BPM and first beat from Traktor's collection (field names unverified; check against a real file). Offer both.
- **Downbeat:** auto-guess, then let the user confirm or nudge by ear with the waveform view.
- Keep the BPM as a float and write it with full precision into every arranger section. At 120 BPM, an error of 0.05 BPM drifts a 60 s chunk by about 25 ms, which is why the fit needs to be precise and why an explicit tempo is written everywhere.

### 6.2 Structure analysis

- Split the song into sections (intro, verse, chorus, drop, breakdown, outro) on bar boundaries, using change in energy and in the non-drum stems' spectral content, with bar-level features compared across the song.
- Section detection is imperfect, so the proposal is always editable: move boundaries, merge, split, rename, and mark sections as repeats of each other.
- Sections are measured in whole bars. A song with a non-whole-bar intro or outro gets a trimmed or padded edge, shown explicitly in the UI.

### 6.3 Mapping the song onto the SP-16

The hardware offers three layers, and the app should use each for what it is good at:

1. **Scene = a bank of 16 samples** (one musical section, or a group of sections that share material). Routing is set per scene.
2. **Pattern = a trigger layout** over those pads, up to 4 bars, with its own length. Patterns are cheap: 16 per scene and no sample memory.
3. **Arranger section = pattern + repeat + tempo**, in play order. Scenes and patterns can be reused as often as needed, at no memory cost.

Two ways to play a stem through a section:

- **Loop-style:** a short sample (at most 4 bars, cut on the bar line) is triggered by a pattern that repeats. Good for drum and bass loops. The sample must end at or before the pattern's end, or the retrigger overlaps.
- **Long-chunk:** one sample of up to 64 s is triggered once by a pattern with repeat 1. The following sections use empty patterns with a repeat count that covers the remaining bars, so nothing retriggers. Good for material that does not repeat, such as vocals. Chunks are cut on bar lines.

A stem's bar count is covered by one or the other, or by a mix. The arranger sections are driven by the union of all four stems' trigger points, so a section boundary is needed wherever any stem has to start a new sample.

### 6.4 Memory budget and reuse of repeated material

The budget is 250 MB. 16-bit stereo at 44.1 kHz is 176.4 kB/s, which gives:

| Material | Size |
| --- | --- |
| 64 s sample (the longest allowed) | 11.3 MB |
| One stem of a 4-minute track | 42.3 MB |
| Four stems of a 4-minute track | 169 MB |
| Four stems at the 250 MB limit | about 5.9 minutes |
| Four stems of a 4-minute track at 24-bit | about 254 MB (over budget) |

So a plain conversion of a typical track fits, but long tracks, or any use of 24-bit samples, do not. 16-bit is the default; 24-bit is not worth the memory unless there's a reason.

Reuse is the way to fit longer tracks:

- **Detect repeats per stem.** Drums and bass in dance music often repeat bar for bar. Compare bar-aligned audio between sections and cluster the near-identical ones. Separated stems are rarely bit-identical, so the user decides whether a near-repeat may be replaced by one representative copy, with an audition button and a similarity score.
- **Load each unique piece once** and trigger it from several patterns. Inside a scene, one pad can be triggered by any number of patterns.
- **Reuse scenes through the arranger** (A, B, A) rather than copying samples into another scene. Identical samples in different scenes are assumed to cost memory twice (Unverified), so shared material belongs in one scene.
- **Show a running memory estimate** (unique sample bytes against 250 MB) and warn above about 85%. Offer fixes: more repeat substitution, shorter chunks, or a stem summed to mono (whether memory use follows file size is Unverified).

Vocals rarely repeat exactly, so they are the stem that fills the memory; drums and bass shrink the most.

### 6.5 Tempo strategy

- Write the fitted tempo into **every** arranger section, with the project tempo set to the same value. If the song has tempo changes, the per-section override handles them.
- Samples are never time-stretched (`stretchMode` `Off`), so the sequencer tempo only decides when the triggers happen. A chunk plays at its recorded speed, so chunk length in bars must match the fitted tempo.
- Leave a few milliseconds of fade at the end of a sample so a late tempo error does not click.

### 6.6 Export

- **Start from a unit-saved template** and patch it. Embed a clean SP-16-saved empty project (`arr0.prj`) in the app.
- Follow the recipe in section 11 of the format spec: pads, playback fields, `chokeGroup` -1, patterns built from the default step record, per-scene routing, arrangement records.
- Sample files: 16-bit stereo 44.1 kHz WAV by default, with a `.dat` for each (`bar` 2.0, `bpm` 0.0, `sampleNum` the frame count, the values the loader uses and the SP-16 accepted).
- Keep sample names short and unique within the package, as a convention rather than a tested limit. The test names looked like `rev1_A1.wav`.
- Validate before writing: every sample at most 64 s, memory estimate under budget, every scene's routing set, arranger length at most 999, repeat at most 32, pattern length at most 64.

### 6.7 Unity architecture

- **Format library** as a plain C# assembly with no Unity dependency, so it can be tested outside the editor: container, grammar, step records, arrangement records, `.dat`, `.tpkg`. Test against the fixtures in section 12 of the format spec, by re-emitting every unit-saved file byte for byte.
- **Analysis library**, also plain C#: WAV import and validation, onset detection, grid fitting, section proposal, repeat detection.
- **Unity layer:** waveform and timeline editing, audition playback of stems, the section and repeat editor, the memory meter, and the export button.
- Analysis should run off the main thread and report progress.
- Arrangement data is the app's central model: a list of sections (scene, pattern, repeat, tempo) plus the scene banks and pattern definitions. The editing UI edits that model, and the exporter serialises it.

## 7. Staged build

1. **Format library and fixtures.** Port the codec, verify byte for byte.
2. **Stage 1 export:** four stems, one trigger each, one pattern, correct routing. Done as a prototype with sine tones (confirmed on the SP-16); redo with real stems.
3. **Sections and scenes:** several scenes, patterns per section, the arranger written from a hand-made section list. Done as a prototype with sine tones; redo with real stems.
4. **Grid detection** and downbeat editing.
5. **Structure and repeat analysis**, with the editing step.
6. **Memory packer** with the running estimate and reuse.
7. **Full song:** analyse, edit, export, load, play. Compare against the original track by ear.

## 8. Open problems and risks (most important first)

1. **Sample memory behaviour near the limit.** The 250 MB figure is reported; what happens on load when it is exceeded (refusal, partial load, crash) is unknown, and so is whether the same file in two scenes counts twice.
2. **Long samples across boundaries.** Audio running past a pattern end is reported, but a long sample across a scene change, and whether a pad's new-scene trigger cuts the old one, is untested. The packer's chunk-end rule depends on it.
3. **Timing precision of tempo.** A float32 is stored and the unit accepts one decimal on screen. Whether more digits are honoured, and how a long chunk lines up with a trigger 60 s later, is untested.
4. **Section detection** (verse, chorus, drop) is imperfect, so a manual editing step is required.
5. **Stem quality.** Separation artifacts may be audible when a single stem is soloed through the mixer. Test a few tracks before investing heavily.
6. **Repeat substitution quality.** Replacing near-repeats with one copy may sound different from the original; it must be an audible, optional choice.
7. **Sample format from NUO-STEMS.** The DAW-mode sample rate, bit depth and length alignment aren't confirmed. 44.1 kHz is the only rate tested on the unit.
8. **Choke group:** `-1` was used in the SP-16 test, but the test triggers were staggered, so simultaneous playback of all four stems has not been heard on the SP-16. *(Settled on 2026-10-10: a package with four stems triggered together in every pattern played correctly on the SP-16.)*
9. **Small format unknowns:** byte 3 of the section record, the `.dat` `bar` field, step parameters other than volume, the quantize fields, and how scene changes are quantized.
10. **Licensing.** Generated projects contain commercial audio, so sharing them is a separate matter from personal use.

## 9. Next hardware tests

Each is a generated package, so the app's own writer can produce them.

1. **Memory limit:** load about 240 MB of distinct samples across two scenes (16-bit, for example 21 × 11.3 MB), then step up or down to find where it refuses. Note what the unit says.
2. **Memory sharing:** one 11 MB file referenced by every pad of two scenes. If it loads when the combined size is over 250 MB, copies are shared.
3. **Long sample across boundaries:** a 20 s tone triggered in scene 1, with sections that run into scene 2 and back. Does it keep playing? Is it cut at the scene change? Does it overlap with its own retrigger?
4. **Simultaneous stems:** four 20 s tones triggered together on step 1 with choke Off, each on its own output pair.
5. **Fractional tempo:** an arranger tempo of 133.28 against a click or Pro Link, and a 60 s chunk against a trigger 60 s later.
6. **Pattern lengths:** three patterns in one scene of 1, 2 and 4 bars with different lengths, to confirm each is stored and plays at its own length.
7. **NUO-STEMS output:** check the sample rate, bit depth and length alignment of real files.

## 10. Notes for the next session

- Attach the format spec and this document.
- Say which stage is current and what has been tested on the unit.
- The Python used for decoding is not available; the format is implemented again from the spec in C#, using the fixtures.
- Treat anything marked Unverified or Reported as unconfirmed until it has been tested.
