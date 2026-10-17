# Strands release test plan

For the first release, on macOS and Windows. Linux is built but is not in this plan.

Each test has a number, what to do, and what should happen. Mark each Pass or Fail; for a Fail, note the file used and what happened instead, and keep the log.

## Before testing starts

| | Who | What |
|---|---|---|
| 0.1 | Done | Identity set on 2026-10-13: company `ezoray`, product `Strands`, identifier `com.ezoray.strands`. |
| 0.2 | Claude | Run part A. Nothing is built until it passes. |
| 0.3 | Claude | Build macOS and Windows from the same commit, and note the commit here: `096f9e6` (2026-10-16; macOS universal, Windows 64-bit, and Linux 64-bit, which is untested and outside this plan), the release candidate: since `0f1ae92`, a package that does not fit stays on Source, the refusals are reworded (A6, A7), and the audition no longer clicks when the playhead jumps, a loop turns, or play starts and stops. Before it, `ea83627`, without the last of those; before that, `0f1ae92` (2026-10-16), for the second run of part C. Before it, `f206197` (2026-10-15), on which B and the first run of C were done. Part A was run on `7712071`; since then only the tempo warnings and the app's screens changed, and the 389 tests pass on `f206197` |
| 0.4 | User | Copy `Builds/Windows` (the whole folder) and the `Release` folder of test files to the Windows PC. |

### Test files

All in one folder of copies, `Release` (237 MB, outside the repository). Copy the whole folder to the Windows PC.

| Name in this plan | Folder | Holds |
|---|---|---|
| **Stems** | `1 Stems` | `Drums.wav`, `Bass.wav`, `Other.wav`, `Vocals.wav` |
| **Song** | `2 Song` | `Mix.wav`, the same song as one file |
| **Stem file** | `3 Stem file` | the same song as a `.stem.mp4` |
| **Formats** | `4 Formats` | 19 short generated tones, one of every kind of file the app reads |
| **Separated** | `5 Separated` | the four FLAC stems of a commercial song, made by a stem separator |
| **Long song** | (add your own) | a recording of 7 minutes or more |

Logs: macOS `~/Library/Logs/ezoray/Strands/Player.log`; Windows `%USERPROFILE%\AppData\LocalLow\ezoray\Strands\Player.log`.

## A. Run by Claude on the Mac, before any build goes out

| | Do | Expect | Result |
|---|---|---|---|
| A1 | Edit Mode tests | All pass (387 at the time of writing) | pass, 388 of 388 (2026-10-15)
| A2 | Export Song and two of the user's recordings, with the core's options and the app's, twice | Same bytes both times. This is the new baseline for the full-mix check | pass: Song and two recordings, each the same bytes both times both ways
| A3 | Read back a package of Song, of Stems, and of Stems with two on one strip summed | Each layer rebuilt from its chunks is within one step of 16 bits of its source | pass: Song, Stems, Drums and Bass summed, and Bass mono on 3; every layer within one step
| A4 | Export Stems from the app and from the command line with the same settings | Same bytes | pass: same bytes
| A5 | Export Separated; list which samples are left out | Drums and Bass of pattern 2 and Vocals of the last pattern left out; 27 patterns | pass: 27 patterns; Drums and Bass of pattern 2, Vocals of the last; also Drums and Bass of the lead-in, which are silent there
| A6 | Load 17 files' worth of tracks (more than 16 separate tracks) | Analyse off; the foot says why in red | pass: 18 tracks, foot reads "18 tracks, but a scene has 16 pads. Sum or remove some." in red (now worded "… Sum stems or remove tracks.", with "Sum stems" only when a strip holds separate tracks; not yet committed)
| A7 | Load stems that pass 256 MB, Analyse | Memory figure red; after the analysis the app stays on Source with the real figure in red at the foot, and Analyse and 2 Analysis are off until a strip changes; then Analyse keeps the grid and goes on to Analysis | pass: Separated twice, 373.5 of 256 MB in red; warnings line red, Export off. Changed since (not yet committed): the app now stays on Source. Checked in play mode by script: "Samples need 357.4 MB…" in red (since reworded to "Calculated size of 357.4 MB is too large to fit. Sum stems, use mono, or remove tracks."), both off; summed, Analyse went on in seconds at 180.4 MB; separate again and 2 Analysis stayed on Source. Re-check by hand
| A8 | Analyse a song too long for 256 bars of patterns | Warnings line red; nothing can be exported | pass: the 17-minute recording on four strips, "Audio needs 103 patterns ... SP-16 holds 64" in red, Export off (over memory as well)
| A9 | macOS build: `-open` Stems `-analyse -picture`, at 1280 x 720, 1920 x 1080 and 2560 x 1440 | Screens as in the Editor; text sharp; 114.000 BPM from Drums; nothing but Unity's start-up lines in the log | pass at all three sizes, 59 frames a second, nothing in the log
| A10 | macOS build: the same with Stem file, Song, and `tone48.wav` | Each loads and analyses | pass for Stem file and Song. `tone48.wav` loads and converts, but at 0.2 s is too short to analyse ("At least 4 seconds of audio are needed"), as meant
| A11 | macOS build left idle, read with `top` | About 60 frames a second, about 15% of one core | about 23% of one core at 1886 x 1094, 59 frames a second: more than the 15% expected, not a fault

## B. macOS build, by hand

Start the app from Finder, not from the Editor.

### Source screen

| | Do | Expect | Result |
|---|---|---|---|
| B1 | Start the app for the first time | Window about two thirds of the screen's height; "SP-16" over "DJS" at the foot; version in the top bar | pass
| B2 | Add files, choose the four Stems | One on each strip by name, each in its strip's colour; project name suggested | pass
| B3 | Open Add files and press Cancel; do it three times | The dialog closes and does not open again by itself | pass
| B4 | Clear. Add folder, choose `1 Stems` | The four files load, one on each strip | pass
| B5 | Clear. Add the Song alone to the Vocals strip | Multi-coloured waveform, grey badge , "full song, on master" | pass
| B6 | Clear. Add the Stem file | Four stems on the four strips, in the file's order | pass
| B7 | With Stems loaded, drag the Bass card on to the Drums strip | Card dims, name follows the pointer, target strip lights; Bass joins Drums as a separate track | pass
| B8 | Press → on that card | It moves one strip along | pass
| B9 | Put two files on one strip; switch "Separate tracks" to "Sum tracks" and back | Track count and memory change each time | pass
| B10 | Switch a strip to "3 and 4 mono" | Two outputs shown; memory for that strip halves | pass
| B11 | Switch "Master | 1 to 8" to Master | Every badge reads Master; stereo/mono switch hidden | pass
| B12 | Hold the pointer over a card | Box with the file's whole name and folder | pass
| B13 | Load each file in Formats, one at a time | Each loads. `tone48` files say "from 48 kHz" on the card | pass
| B14 | Cmd+O | Add files dialog opens | pass

### Analysis screen (Stems loaded)

| | Do | Expect | Result |
|---|---|---|---|
| B15 | Press Analyse | Dots move in the status bar; button reads "Analysing…"; then 114.000 BPM, "from Drums", 10 patterns in 3 scenes | pass
| B16 | Space | Plays from the first pattern; Play and Pause the same width | pass
| B17 | C, then listen | Click on every beat, on the kicks | pass
| B18 | Play from the very start of the lead-in | Silence first, then the song comes in on the beat the screen names | pass
| B19 | Keys 1 to 4, Shift+2, 5 | Strips go off and on, drawn and heard; Shift+2 is Bass alone; 5 is all | pass
| B20 | L on a pattern | That pattern loops with no gap at the turn | pass
| B21 | Click the waveform while stopped, then while playing | Stopped: exactly where clicked. Playing: from the start of that beat | pass
| B22 | Wheel to zoom, drag to pan, F | F returns to the scene | pass
| B23 | Click and drag in the long waveform | The large one follows | pass
| B24 | Drag grid on, drag the lines, let go | Lines stay where they were let go | pass
| B25 | Move bar line: each of the six arrows | 1 beat changes which line is the bar; ½ and ¼ move every line | pass
| B26 | Fine tuning −10, −1, +1, +10; comma and full stop | Total in ms changes to match | pass
| B27 | Tap (T) eight times with the music | Tempo near 114 shown in the panel's head | pass
| B28 | Type a tempo, ÷2, ×2, Refit | Grid follows each; playhead stays where it is | pass
| B29 | Start song at: → three times, ← three times | Patterns drop from the front and come back; warnings line says what is dropped; nothing above it moves | pass
| B30 | Adjust lead-in start: → twice, ← twice | Reads as bar and beat; view stays where it was | pass
| B31 | Start pattern at playhead, with the playhead on a kick mid-song | A pattern starts there; flag on the lane | pass
| B32 | Select a pattern | Selected shows its name, Bars, Length, Time, Hits, Samples | pass
| B33 | Reset analysis | Everything as first analysed | pass
| B34 | Press "1 Source", change an output, press "2 Analysis" | Grid kept. "Laying out stems" shows briefly with the moving dots; that is not an analysis | pass
| B35 | Resize the window small, large, and full screen | Nothing overlaps or is cut off; text stays sharp; Export page pads stay 4 x 4 | Pass

### Export page

| | Do | Expect | Result |
|---|---|---|---|
| B36 | Press Export (or Cmd+E) | Sixteen pads, top row first, each with name, colour, track number and waveform | pass
| B37 | ← → and the arrows | Scenes change | pass
| B38 | Click a pad; click it again; click its square | Plays from the start; restarts; stops. No click in the sound | pass
| B39 | Play four pads of one pattern together | The song, in time | pass but no way to accurately play 4 pads together with a mouse?
| B40 | Set a stem to a choke group; play two of its pads in turn | The second cuts off the first, without a click | pass
| B41 | Set Loop on a pad and play it | Goes round with no click at the turn | pass
| B42 | Stop in the Pads head; Space | Everything stops | pass
| B43 | Options: each row's Choke and Loop, then All | Pads follow; a pad set by hand afterwards keeps its own setting | pass
| B44 | Time stretch, Arrangement, Add tempo to arrangement: each choice | Switch shows the choice; Package panel clears any "Written to" | pass
| B45 | Export package, save to the desktop | Package panel "Written to …", status bar "Wrote …"; Show in folder opens Finder on it | pass - Wrote, not, Written to
| B46 | Export again over the same file | Written without complaint | pass
| B49 | On a pad that is playing, press its Choke, then its Loop, then Loop again | It goes on sounding throughout; with Loop on it goes round, with Loop off it ends at its end | pass

### Long song

| | Do | Expect | Result |
|---|---|---|---|
| B47 | Load Long song, analyse | No long freeze; job can be cancelled; memory bar and patterns as expected | pass
| B48 | Scroll and play across a scene boundary | Smooth; no stutter in the sound | pass

### Added on 2026-10-14

| | Do | Expect | Result |
|---|---|---|---|
| B50 | Loop pattern on, playhead in the middle of a pattern. Move bar line (each step), Fine tuning, a new tempo, ÷2, ×2, Start song at → and ←, Adjust lead-in start → and ← | The patterns move; the playhead stays where it is. Only when it is in what Start song at or Adjust lead-in start leaves out does it go to where the song now starts | pass
| B51 | As B50 with Loop pattern off, and while playing | The same | pass
| B52 | Click a pattern, press ← →, with Loop pattern off and on | The playhead goes to that pattern, as before | pass
| B53 | Switch to Master, quit, start the app again | Master is still on | pass
| B54 | Turn Click off on Analysis, quit, start again, analyse | Click is still off | pass
| B55 | Select a pattern in the middle of the song; the ← under its name twice (or `[` twice) | It is 2 bars; every pattern after it starts 2 bars earlier on the lane and the long waveform; the grid and playhead do not move | pass
| B56 | → twice (or `]`) on the same pattern | Back to 4 bars, every pattern where it was | pass
| B57 | Shorten a pattern, then Move bar line a beat, change tempo and back, Reset analysis | The short pattern stays on the same music until Reset, which puts every pattern back to 4 bars | pass
| B58 | The lead-in and the last pattern | The lead-in shows its length with no arrows, and nothing below it moves; on the last pattern ← and → are off and "Pattern length" and the length are greyed; between 1 bar and 4 bars the → does not move | pass
| B59 | A song whose phrases move two bars against the patterns part way in: shorten the pattern before the section that moved | Build-ups after it start on a pattern again | pass

### Added on 2026-10-16

| | Do | Expect | Result |
|---|---|---|---|
| B60 | A quiet, sparse song (a ballad, an instrumental): while playing, click about the waveform, select patterns, loop a pattern, play and pause | No click at any jump, at the loop's turn, or as play starts or stops; the metronome still on the beats |
| B61 | Export page with stems: switch Velocity on and off for a strip and for All | A third column beside Choke group and Loop, headings centred over their switches; the rows fit the panel with no overlap |
| B62 | Arrangement On, then Off, with some strips set to Loop | With Arrangement on the Loop switches and the pads' Loop buttons are greyed and pads do not loop when played; switched off, the loops set before are back |
| B63 | Set Time stretch, both arrangement switches and Choke, Loop and Velocity for each strip. Quit, reopen, load other stems and Analyse | The Export page starts as it was left, strip by strip; a pad set by itself does not carry over |
| B64 | A song in one file, Export page | No Song label; Choke group, Loop and Velocity start at the panel's left; the groups below are evenly spaced |
| B65 | Load more than fits: stems on separate tracks; then the same with strips summed and split to mono; then on Master | Analyse stays on Source with "Calculated size of … is too large to fit." in red, and the advice names only what would help: no "Sum stems" when nothing can be summed, no "use mono" on Master or when every strip is mono |
| B66 | Load the same stems twice (two files on each strip). Set Drums to Sum tracks, then Other; then split a strip to mono with two files on one output, summed | "Too loud summed, will distort" under the switch of each strip whose files clip together often enough to hear (the test song's Drums twice do), as soon as Sum is chosen, and gone with Separate tracks; none on a strip whose sum fits or only clips now and then for a moment (Other twice) |
| B67 | With B66 loaded, two strips summed and two on Separate tracks: Analyse, then the Export page | Analyse takes a few seconds, not tens of seconds; every pad with a sample shows its waveform, summed strips and separate tracks alike |
| B68 | Load the same stems four times, split every strip to mono and sum each output; Analyse, then the Export page | Under Package each note starts on a line of its own and wraps; nothing runs past the panel's foot: where the text would, it stops after the last word that fits with "…"; after Export package, "Written to …" still shows at the foot |
| B69 | A long song (4 minutes or more) as stems loaded three times, 12 tracks. Watch the foot as it loads; Analyse; split every strip to mono; then sum strips until the bars needed fit | Within a few seconds of loading, "bars needed" shows "~" and a figure, in red when it is more than the bars of room, with "Likely too large to fit with 12 tracks. Fits with about 8 or fewer."; Analyse can still be pressed. After Analyse the figure loses its "~" and the message reads "Too large to fit with 12 tracks. Fits with 8 or fewer."; mono alone keeps it, with Analyse off; once the bars needed fit the bars of room, the figure is no longer red and the foot goes back to the memory figure |
| B70 | Stems whose estimate on Source is a little over 256 MB but whose package fits once silent samples are left out (seven tracks of a 4-minute song, one strip summed): Analyse, then 1 Source; then change a strip and change it back | Back on Source the memory bar, each strip's MB and the foot show the analysed package's figures, under 256 MB, with no memory warning; after a change the estimate comes back; changed back as it was, the analysed figures return |

## C. Windows build

Run on an ordinary 64-bit Intel or AMD PC. First, the unattended run, in a command prompt in the build's folder, with the full path of the Stems folder in quotes (a folder stands for its audio files):

```
Strands.exe -open "C:\path\to\Release\1 Stems" -analyse -picture shot.png -then-quit
```

Send back `shot.png`, `shot.png.txt` and `Player.log`. The log lists what was opened, any path that was not found, and where the picture went.

| | Do | Expect | Result |
|---|---|---|---|
| C1 | The run above | A picture of the Analysis screen: 114.000 BPM from Drums, 10 patterns in 3 scenes | Fail on `f206197`: only Drums loaded, and no `shot.png`. The command named the files as if they were in the build's folder, and the picture was saved after the app had quit. Fixed in `0f1ae92`: `-open` takes a folder, paths are resolved and logged, the picture is written at once. Pass on `0f1ae92` (same laptop): four stems, 114.000 BPM from Drums, 10 patterns in 3 scenes. Pass on a second PC (1920 x 1121, 120 dpi) |
| C2 | Start by double click | Opens, at a sensible size; note any SmartScreen warning and its wording | Pass. Near full screen on a 1366 x 768 laptop. Text soft at smaller sizes: see C14. Same on `0f1ae92`. Second PC: acceptable, soft only below the size the screens were drawn for |
| C3 | Look at both screens beside the Mac's captures | Same layout, typefaces, colours and ground; arrows ← → drawn, not boxes | Pass, but two-finger scroll on the trackpad does not scroll a strip's list. Old laptop; a mouse wheel not tried. Same on `0f1ae92`, same laptop. Second PC: a mouse wheel scrolls it (no trackpad there) |
| C4 | **Add files**, choose the four Stems | Dialog opens; files load. This is the main risk on Windows | Pass. Second PC: pass |
| C5 | Add files, Cancel, three times | No dialog reopens | Pass |
| C6 | **Add folder** | Dialog opens; files load | Pass. Windows' folder dialog shows folders only, not the files in them. Second PC: pass |
| C7 | A strip's own add button | Dialog opens; file lands on that strip | Pass |
| C8 | Load each of Formats in turn | All load **except** the three `.alac.stem.mp4` and `tone16.alac.m4a`, which are refused with a message saying Apple Lossless needs a Mac | Pass |
| C9 | Load the Stem file | Four stems (our own AAC decoding) | Pass |
| C10 | A file from a path with spaces and accented letters | Loads | Pass |
| C11 | Analyse, Space, C | Sound from the default device; click on the kicks | Pass. Second PC: pass |
| C12 | Export page: play pads | Sound, in time | Pass. Second PC: pass |
| C13 | Keys: Space, arrows, L, C, T, F, 1 to 5, **Ctrl+E**, **Ctrl+O** | As on the Mac, with Ctrl for Cmd | Pass |
| C14 | Resize, maximise | As B35 | Pass, but text is soft when the window is under full screen: below 1280 x 720 the screens are drawn smaller than designed, on a 96 dpi screen. On `0f1ae92` the window was 769 x 522, so everything at 0.6 of its size; the laptop's command prompt is soft too. Second PC: acceptable |
| C15 | Export Stems with default settings as `Win112.tpkg`; copy it to the Mac | Claude compares it with the Mac's: same bytes expected | Pass. Set to choke by stem and M.TMP; byte for byte the Mac's export with the same settings. Second PC, default settings: byte for byte the Mac's |
| C16 | Export; Show in folder | Explorer opens on the file | Pass |
| C17 | Close and start again | Window comes back where and as it was | Pass |
| C18 | Load the package of C15 on the unit | Plays as the Mac's does | Pass on the SP-16: plays fine |

Whichever of B15 to B46 there is time for is worth repeating on Windows; C covers what is likeliest to differ.

First run, 2026-10-16, on an old 1366 x 768 laptop with the build from `f206197`. Second run, the same day, on `0f1ae92`: the same laptop, then a second PC, its window 1920 x 1121 at 120 dpi, which is Windows' 125% (C1, C2, C3, C4, C6, C11, C12, C14, C15).

## D. On the SP-16 and the DJS-1000

Sixteen packages, made on 2026-10-14 from `6401b75`, are in `out/Unit tests/` (883 MB), with `U17Double` added on 2026-10-15. Copy the folder to the unit once. U01 to U14 are the test song's four stems (Drums, Bass, Other, Vocals), 114 BPM, 10 patterns in 3 scenes, unless the row says otherwise.

Things true of every stems package, worth checking on U01 and then only noticing elsewhere:

- The first pattern is on the top row of scene 1 (tracks 13 to 16), the next on the row below.
- The lead-in's Bass pad (track 14) is empty: the bass is as good as silent there and is left out.
- Drums, Bass, Other and Vocals are in track colours 01, 05, 09 and 13.
- A sample's BPM reads 114 (sample info).
- The lead-in starts with about 0.3 s of silence and hands over to pattern 2 in time.

| | Package | What it is | Listen and look for | Result |
|---|---|---|---|---|
| D1 | `U01Pairs` | Every setting at its default: stems on 1-2, 3-4, 5-6, 7-8, arranger on with the song's tempo, no choke, no loop, stretch off | The whole song from the arranger, each stem from its pair; clean joins between patterns and over the scene changes; the points above | pass
| D2 | `U02Master` | The four stems as four tracks on master | The whole song from the master out, all four parts; nothing from 1 to 8 | pass
| D3 | `U03Mono` | Bass by itself on output 3, as mono samples | Bass from output 3 only; the rest on their pairs | pass
| D4 | `U04Summed` | Drums and Bass summed into one track on 1-2 | Three tracks to a pattern; drums and bass together from 1-2, at a sensible level, not distorted | pass
| D5 | `U05Separate` | Drums and Bass as two tracks on 1-2 | Four tracks to a pattern; Bass in colour 02 beside Drums in 01; both from 1-2 | pass
| D6 | `U06Choke` | Each stem in its own choke group (1 to 4) | The arrangement as U01, no clicks or gaps at the joins; playing two Drums pads in turn, the second cuts off the first | pass
| D7 | `U07LoopArr` | Every pad looped, arranger on (looped pads are choked) | The arrangement plays through without loops piling up; no clicks where patterns join; a pad played by hand goes round cleanly | pass
| D8 | `U08LoopFree` | Every pad looped, arranger off | Opens with the arranger off; any pad played goes round until stopped, no click at the turn; pads of one stem can overlap (no choke) | pass
| D9 | `U09ArrOff` | Arranger off, nothing looped | Opens with the arranger off; switching the arranger on plays the song | pass
| D10 | `U10Resmpl` | RESMPL, no tempo in the arrangement | At 114 plays as U01; change the unit's tempo: the song follows it, pitch with it, still in time at the joins | pass
| D11 | `U11Mtmp` | M.TMP, no tempo in the arrangement | As D10, but the pitch stays put | pass
| D12 | `U12ResmplTempo` | RESMPL with the song's tempo in each section | The unit goes back to 114 at each section, whatever it is set to | pass
| D13 | `U13Half` | The same stems at exactly half tempo, 57 BPM: 5 patterns of 8 real bars each | The unit shows 57; the song sounds exactly as U01, same speed, same joins | pass - but 60 bpm is SP-16 minimum so timing off
| D14 | `U14Short` | Pattern 4 of scene 1 is 2 bars (32 steps) | Plays straight through pattern 4 into scene 2 with no gap, overlap or jump | pass
| D15 | `U15Song` | The song as one file, on master | 10 patterns, all 16 pads of scene 1 used, left to right from the top row; samples named `U15Song_01` to `U15Song_10`; the template's colours | pass
| D16 | `U16Separated` | Separated's four stems, 92.9 BPM, 27 patterns in 7 scenes, 179 MB | No gaps where near-silent samples were left out (pattern 2 has only Other and Vocals); the vocals' tail is not missed at the end; no patterns of silence at the end; loads without "out of memory" | pass
| D17 | `U01Pairs` on the **DJS-1000** | | Loads and plays as on the SP-16 | pass
| D18 | `U15Song` on the **DJS-1000** | | Loads and plays as on the SP-16 | pass
| D19 | The Windows package (C15) | | As D1 | Pass, 2026-10-16: plays fine on the SP-16 |
| D20 | `U17Double` (made 2026-10-15, on the SP-16 and the DJS-1000) | The same stems at exactly twice the tempo, 228 BPM in the project and in all 19 sections: each pattern is 2 real bars, a sample 4.21 s long | Whether the unit holds 228 or plays at its 220 maximum. At 220 a pattern lasts 4.36 s, so every 4.2 s the sound stops for about 150 ms before the next pattern starts: a regular stutter. At 228 it plays straight through like U01. Note what the arranger and the tempo display show | Pass, 2026-10-16, SP-16: the expected stutter, so it plays at 220 BPM |
| D21 | `U18Velocity` (made 2026-10-16 from the app, on the SP-16) | The test stems with velocity on for every stem, every pad of every scene | Every pad's velocity reads on; the arrangement plays the whole song at its usual level, no quieter than `U01Pairs`; pads struck by hand follow how hard they are hit | pass, 2026-10-16: "works fine, volume in arrangement as before"
| D22 | `U19EmptyPads` (made 2026-10-16 from the app) | The test stems, arrangement on, every stem choked with velocity on. The bass is silent in the lead-in, so scene 1 track 14 has no sample | Plays as `U01Pairs`. Track 14 of scene 1 shows the bass's colour and is in choke group 2 with velocity on; a sample put on it there behaves as the bass's other pads |
| D23 | `U20Loops` (made 2026-10-16 from the app) | The test stems, arrangement off, every pad looping and choked by stem | Opens in pattern mode. Each pad and pattern played by hand goes round cleanly, and a stem's next pattern cuts its last off. The last pattern's samples are shorter than the pattern, so they are expected to go round early: note how it sounds |

The packages made earlier for single questions, `Quiet112`, `Master112` and `Short112`, are covered by D1, D2, D14 and D16 and need not be loaded.

## Results

| Test | Pass / Fail | Build commit | Note |
|---|---|---|---|
| | | | |
