# Writes the stem files for Fixtures/audio from the reference WAVs already there. A stem file is
# an MP4 laid out as Native Instruments' Stems format has it and as Traktor writes it: the full
# mix as the first audio track, each stem as a track after it, and a list of the stems' names
# as JSON in moov/udta/stem.
#
# So that a test can tell from the samples which track it was handed, the first track is silence
# and the four stems are the reference sound as it is, with its sides exchanged, backwards, and
# backwards with its sides exchanged.
#
# The tracks are encoded by afconvert, so this needs macOS. afconvert writes one track to a
# file; the tracks are put together here, each with an edit list saying where its real audio
# starts and ends, which is where Traktor's files keep it too. An AAC track also gets the
# "roll" sample group Traktor's have: without it macOS takes the start from the edit list but
# plays on through the padding at the end.
# Usage: python3 make_stem_fixtures.py <folder>
import json, os, struct, subprocess, sys, tempfile, wave

NAMES = ['As it is', 'Sides exchanged', 'Backwards', 'Backwards and exchanged']
COLOURS = ['#FD6C38', '#D232F4', '#00FFAC', '#45DAFD']


def variants(path):
    """The five tracks as raw frames: silence, then the four ways round of the reference."""
    with wave.open(path, 'rb') as w:
        width, rate, count = w.getsampwidth(), w.getframerate(), w.getnframes()
        raw = w.readframes(count)
    size = 2 * width
    frames = [raw[i * size:(i + 1) * size] for i in range(count)]
    exchanged = [f[width:] + f[:width] for f in frames]
    tracks = [[bytes(size)] * count, frames, exchanged, frames[::-1], exchanged[::-1]]
    return width, rate, [b''.join(t) for t in tracks]


def boxes(data, start, end):
    """(type, start, end of contents, start of box) for each box between start and end."""
    found = []
    while start + 8 <= end:
        size, kind = struct.unpack('>I4s', data[start:start + 8])
        assert size >= 8, 'a box longer than four bytes can say is not expected here'
        found.append((kind, start + 8, start + size, start))
        start += size
    return found


def child(data, parent, kind):
    for box in boxes(data, parent[1], parent[2]):
        if box[0] == kind:
            return box
    raise ValueError('no %r box' % kind)


def encode(raw, width, rate, codec, folder, index):
    """One track through afconvert: its trak box, its audio, and where the real audio lies in it."""
    source = os.path.join(folder, 'track%d.wav' % index)
    encoded = os.path.join(folder, 'track%d.m4a' % index)
    with wave.open(source, 'wb') as w:
        w.setnchannels(2); w.setsampwidth(width); w.setframerate(rate)
        w.writeframes(raw)
    options = ['-d', 'aac', '-b', '256000'] if codec == 'aac' else ['-d', 'alac']
    subprocess.run(['afconvert', '-f', 'm4af'] + options + [source, encoded], check=True)
    data = open(encoded, 'rb').read()
    top = {box[0]: box for box in boxes(data, 0, len(data))}
    trak = child(data, top[b'moov'], b'trak')
    # afconvert keeps the encoder's delay and padding in an iTunes tag, as hexadecimal words:
    # a zero, the priming, the remainder, the real length.
    words = data[data.index(b'iTunSMPB'):].split(b'data', 1)[1][8:].split()
    priming, valid = int(words[1], 16), int(words[3], 16)
    assert valid == len(raw) // (2 * width)
    return data, trak, top[b'mdat'], priming, valid


def write(path, source, codec):
    width, rate, tracks = variants(source)
    ftyp = struct.pack('>I4s4sI4s4s4s', 28, b'ftyp', b'mp42', 1, b'isom', b'mp41', b'mp42')
    audio, traks, mvhd = b'', b'', None
    with tempfile.TemporaryDirectory() as folder:
        for index, raw in enumerate(tracks):
            data, trak, mdat, priming, valid = encode(raw, width, rate, codec, folder, index)
            if mvhd is None:
                moov = [box for box in boxes(data, 0, len(data)) if box[0] == b'moov'][0]
                box = child(data, moov, b'mvhd')
                mvhd = bytearray(data[box[3]:box[2]])
                assert mvhd[8] == 0, 'the fields below are those of version 0'
                # Time is counted in samples throughout, and the next track would be the sixth.
                struct.pack_into('>II', mvhd, 20, rate, valid)
                struct.pack_into('>I', mvhd, len(mvhd) - 4, len(tracks) + 1)

            # The audio will sit at a new place in the file, so every chunk offset moves with it.
            moved = bytearray(data[trak[3]:trak[2]])
            shift = len(ftyp) + 8 + len(audio) - mdat[1]
            around = []
            for kind in (b'mdia', b'minf', b'stbl'):
                around.append(child(data, around[-1] if around else trak, kind))
            path_to_tables = around[-1]
            at = child(data, path_to_tables, b'stco')[1] - trak[3]
            for entry in range(struct.unpack_from('>I', moved, at + 4)[0]):
                old = struct.unpack_from('>I', moved, at + 8 + 4 * entry)[0]
                struct.pack_into('>I', moved, at + 8 + 4 * entry, old + shift)
            audio += data[mdat[1]:mdat[2]]
            if codec == 'aac':
                # The sample tables are the last thing in the track, so the group goes on the end
                # and every box around it grows by its length.
                packets = struct.unpack_from('>I', data, child(data, path_to_tables, b'stsz')[1] + 8)[0]
                roll = struct.pack('>I4sI4sIIh', 26, b'sgpd', 1 << 24, b'roll', 2, 1, -1)
                roll += struct.pack('>I4sI4sIII', 28, b'sbgp', 0, b'roll', 1, packets, 1)
                for box in around:
                    assert box[2] == trak[2], 'expected the sample tables at the end of the track'
                    struct.pack_into('>I', moved, box[3] - trak[3], box[2] - box[3] + len(roll))
                moved += roll

            tkhd = child(data, trak, b'tkhd')
            head = bytearray(data[tkhd[3]:tkhd[2]])
            assert head[8] == 0
            struct.pack_into('>I', head, 20, index + 1)
            struct.pack_into('>I', head, 28, valid)
            # One edit: play this many samples, starting after the encoder's priming.
            elst = struct.pack('>I4sIIIiI', 28, b'elst', 0, 1, valid, priming, 0x10000)
            edts = struct.pack('>I4s', 8 + len(elst), b'edts') + elst
            rest = bytes(moved[tkhd[2] - trak[3]:])
            body = bytes(head) + edts + rest
            traks += struct.pack('>I4s', 8 + len(body), b'trak') + body

    stems = {
        'mastering_dsp': {
            'compressor': {'attack': 0.003, 'dry_wet': 50, 'enabled': False, 'hp_cutoff': 300, 'input_gain': 6,
                           'output_gain': 0, 'ratio': 3, 'release': 0.3, 'threshold': 0},
            'limiter': {'ceiling': 0, 'enabled': False, 'release': 0.001, 'threshold': 0}},
        'stems': [{'color': colour, 'name': name} for colour, name in zip(COLOURS, NAMES)],
        'version': 1}
    listed = json.dumps(stems, separators=(',', ':')).encode('utf-8')
    stem = struct.pack('>I4s', 8 + len(listed), b'stem') + listed
    udta = struct.pack('>I4s', 8 + len(stem), b'udta') + stem
    moov = bytes(mvhd) + traks + udta
    with open(path, 'wb') as out:
        out.write(ftyp)
        out.write(struct.pack('>I4s', 8 + len(audio), b'mdat') + audio)
        out.write(struct.pack('>I4s', 8 + len(moov), b'moov') + moov)


folder = sys.argv[1]
write(folder + '/tone16.stem.mp4', folder + '/tone16.wav', 'aac')
write(folder + '/tone16.alac.stem.mp4', folder + '/tone16.wav', 'alac')
write(folder + '/tone24.alac.stem.mp4', folder + '/tone24.wav', 'alac')
write(folder + '/tone48.alac.stem.mp4', folder + '/tone48.wav', 'alac')
write(folder + '/tone48.stem.mp4', folder + '/tone48.wav', 'aac')
