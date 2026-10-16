# SharpJaad

A fully managed AAC decoder by Lukas Volf, under the MIT licence (see `LICENSE.txt`). It is a port to C# of JAAD, a Java AAC decoder that is in the public domain.

Source: https://github.com/jimm98y/SharpJaad, the `src/SharpJaad.AAC` folder at commit `3b2014c177d76ca7ec01d0be56e35f1874e21f4b` (12 February 2026, version 0.1.1). Only the decoder was taken. The project's MP4 reader (`src/SharpJaad`, 160 more files) was not: the app finds a track's packets itself, in `Mp4` in `Strands.Audio`.

`SharpJaad.asmdef` was added so that Unity builds it as its own assembly with no engine references. Only `Strands.Audio` uses it, through `AacReader`, which hands it the packets of one track one at a time. That is how the stems of a Traktor stem file are decoded, on every system.

## Changed from the original

One file. The place is marked `Strands`.

**`Tools/TNS.cs`** (the method `Process`, and two fields for it):

`Process` was an empty stub, `//TODO...`, as it is in JAAD. TNS (temporal noise shaping) is a filter an encoder runs along the spectrum of a frame, mostly where there is a sharp hit, and the decoder has to run the opposite filter. The decoder read the filter from the file and then did nothing with it, so every frame coded with TNS came out wrong. Found on 2026-10-11 on a Traktor stem file: 4% of the samples of the drums were 5 steps of 16 bits or more from what macOS decodes, the worst by 14,696 steps. Every packet that came out wrong had TNS in it or in the packet before it, and no packet without TNS was wrong.

The method is now the decoder's side of ISO/IEC 14496-3, 4.6.9.3. With it, all five tracks of that file are within one step of 16 bits of macOS's decoder at every sample, and 97.8 to 99.5% of the samples are the same.

It has not been reported to the SharpJaad project. `docs/sharpjaad-tns.patch` is the same change as a patch against that commit.

## Never run by the app

These are in the folder as they were downloaded, and none of the app's tests reach them: HE-AAC (`Sbr`, `Ps`), the error-resilient profiles (`Error`), gain control (`Gain`), the ADIF and ADTS headers (`Transport`), and long-term prediction, whose own use of TNS is still not written.

Noise substitution draws its noise from one generator that every decoder in the program shares (`ICStream.randomState`). A file that uses it would not decode to the same samples twice running, nor to what any other decoder gives. Traktor's files do not use it.
