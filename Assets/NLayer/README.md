# NLayer

A fully managed MP3 decoder by Mark Heath, Andrew Ward and contributors, under the MIT licence (see `LICENSE.txt`).

Source: https://github.com/naudio/NLayer, the `NLayer` folder at commit `046c7ce422970f8f0f0bc205d6c6341bcb7debf1` (27 August 2026, version 3.0.0).

`NLayer.asmdef` was added so that Unity builds it as its own assembly with no engine references. Only `Strands.Audio` uses it, through `Mp3Reader`, which finds the frames itself and hands them one at a time to `MpegFrameDecoder`; `MpegFile` and the stream reader are not used.

## Changed from the original

Two files. Each place is marked `Strands`.

**`Decoder/BitReservoir.cs`** (three places):

The reservoir is a ring of 8192 bytes, and `_end` is set to -1 when the last byte written was the last byte of the ring. `AddBits` also read `_end == -1` as "nothing has been added yet", and in that state it refuses any frame that borrows bytes from the frame before. So whenever a frame happened to end exactly on the last byte of the ring, about once in 8192 frames, the next frame was dropped: 1152 samples (26 ms) missing, and everything after it early by that much. Found on 2026-10-10 on three variable-bitrate files, which lost 1, 3 and 9 frames against Apple's decoder. A separate `_empty` flag now records whether anything has been added.

**`Decoder/LayerIIIDecoder.cs`** (one place, in `PrepTables`):

The table that says which of the three short windows a spectral line belongs to was filled for 12 scale factor bands. There are 13: the last one, which is the top of the spectrum (from about 11.5 kHz at 44.1 kHz), has no scale factor but its lines still belong to three windows. Left unfilled, all of it counted as window 0 and was given that window's subblock gain. It shows only when the three gains differ and there is sound in the top band, which LAME's variable-bitrate mode produces often and its constant-bitrate mode did not in the files tried. Found on 2026-10-10: stretches of about 10 ms differing from both LAME's and Apple's decoders by up to 1.3% of full scale, 76 of them in a 76-second file at `-V 2`. With the loop at 13, the same files match LAME's decoder to within one step of 16 bits.

Neither has been reported to the NLayer project.
