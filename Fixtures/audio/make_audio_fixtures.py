# Writes the reference WAVs for Fixtures/audio: 0.2 s of generated sound, different in each channel.
# Silence, a click, a decaying tone, a sweep and a burst of noise, so that a lossless coder has to
# use more than one kind of block. tone48.wav is the same recipe at 48 kHz, for the files that
# have to be brought to 44.1 kHz. Usage: python3 make_audio_fixtures.py <folder>
import math, random, struct, sys, wave

def signal(channel, RATE, FRAMES):
    rng = random.Random(11 + channel)
    out = []
    for i in range(FRAMES):
        v = 0.0
        if i == 600 + 37 * channel:
            v = 0.95 if channel == 0 else -0.95
        if 1000 <= i < 4000:
            u = (i - 1000) / RATE
            v += 0.6 * math.exp(-u * 25) * math.sin(2 * math.pi * (220 + 110 * channel) * u)
        if 4000 <= i < 7000:
            u = (i - 4000) / RATE
            v += 0.4 * math.sin(2 * math.pi * (300 + 6000 * u * (1 + channel)) * u)
        if 7000 <= i < 8400:
            v += 0.5 * (rng.random() * 2 - 1)
        out.append(max(-1.0, min(1.0, v)))
    return out

def write(path, bits, rate=44100):
    frames = rate // 5
    left, right = signal(0, rate, frames), signal(1, rate, frames)
    full = (1 << (bits - 1)) - 1
    data = bytearray()
    for l, r in zip(left, right):
        for v in (l, r):
            data += struct.pack('<i', int(round(v * full)))[:bits // 8]
    with wave.open(path, 'wb') as w:
        w.setnchannels(2); w.setsampwidth(bits // 8); w.setframerate(rate)
        w.writeframes(bytes(data))

folder = sys.argv[1]
write(folder + '/tone16.wav', 16)
write(folder + '/tone24.wav', 24)
write(folder + '/tone48.wav', 16, 48000)
