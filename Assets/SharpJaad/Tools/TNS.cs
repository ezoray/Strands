using SharpJaad.AAC.Syntax;

namespace SharpJaad.AAC.Tools
{
    public class TNS
    {
        private static int TNS_MAX_ORDER = 20;
        private const int TNS_MAX_ORDER_SHORT = 7;
        private readonly float[] _lpc = new float[TNS_MAX_ORDER];
        private static int[] SHORT_BITS = { 1, 4, 3 }, LONG_BITS = { 2, 6, 5 };
        //bitstream
        private int[] _nFilt;
        private int[,] _length, _order;
        private bool[,] _direction;
        private float[,,] _coef;

        public TNS()
        {
            _nFilt = new int[8];
            _length = new int[8, 4];
            _direction = new bool[8, 4];
            _order = new int[8, 4];
            _coef = new float[8, 4, TNS_MAX_ORDER];
        }

        public void Decode(BitStream input, ICSInfo info)
        {
            int windowCount = info.GetWindowCount();
            int[] bits = info.IsEightShortFrame() ? SHORT_BITS : LONG_BITS;

            int w, i, filt, coefLen, coefRes, coefCompress, tmp;
            for (w = 0; w < windowCount; w++)
            {
                if ((_nFilt[w] = input.ReadBits(bits[0])) != 0)
                {
                    coefRes = input.ReadBit();

                    for (filt = 0; filt < _nFilt[w]; filt++)
                    {
                        _length[w, filt] = input.ReadBits(bits[1]);

                        if ((_order[w, filt] = input.ReadBits(bits[2])) > 20) throw new AACException("TNS filter out of range: " + _order[w, filt]);
                        else if (_order[w, filt] != 0)
                        {
                            _direction[w, filt] = input.ReadBool();
                            coefCompress = input.ReadBit();
                            coefLen = coefRes + 3 - coefCompress;
                            tmp = 2 * coefCompress + coefRes;

                            for (i = 0; i < _order[w, filt]; i++)
                            {
                                _coef[w, filt, i] = TNSTables.TNS_TABLES[tmp][input.ReadBits(coefLen)];
                            }
                        }
                    }
                }
            }
        }

        // Strands: this method was an empty stub ("//TODO...") in SharpJaad 0.1.1, as it is in JAAD,
        // so a frame coded with TNS came out without its filter and was wrong wherever TNS mattered
        // (hits on drums most of all). What follows is the decoder's side of ISO/IEC 14496-3,
        // 4.6.9.3: each filter's coefficients are turned from the reflection coefficients read
        // from the file into those of an all-pole filter, which is then run along its stretch of
        // the spectrum, upwards or downwards.
        // The tables in TNSTables hold the reflection coefficients with their sign turned round
        // (they are the ones FFmpeg uses), which is why each is negated here.
        // When `decode` is true the caller is long-term prediction, which wants the filter the
        // encoder used. That is still not written: nothing here is coded with that profile.
        public void Process(ICStream ics, float[] spec, SampleFrequency sf, bool decode)
        {
            if (decode)
                return;

            ICSInfo info = ics.GetInfo();
            bool isShort = info.IsEightShortFrame();
            int windowCount = info.GetWindowCount();
            int[] offsets = info.GetSWBOffsets();
            // A filter never reaches above the bands the frame has, nor above the limit for the sample rate.
            int maxBands = System.Math.Min(sf.GetMaximalTNS_SFB(isShort), info.GetMaxSFB());
            int maxOrder = isShort ? TNS_MAX_ORDER_SHORT : TNS_MAX_ORDER;
            if (maxBands == 0)
                return;

            for (int w = 0; w < windowCount; w++)
            {
                // The first filter covers the top of the spectrum and each one after it the bands below.
                int bottom = info.GetSWBCount();
                for (int filt = 0; filt < _nFilt[w]; filt++)
                {
                    int top = bottom;
                    bottom = System.Math.Max(0, top - _length[w, filt]);
                    int order = System.Math.Min(_order[w, filt], maxOrder);
                    if (order == 0)
                        continue;

                    for (int i = 0; i < order; i++)
                    {
                        float reflection = -_coef[w, filt, i];
                        _lpc[i] = reflection;
                        for (int j = 0; j < (i + 1) >> 1; j++)
                        {
                            float front = _lpc[j], back = _lpc[i - 1 - j];
                            _lpc[j] = front + reflection * back;
                            _lpc[i - 1 - j] = back + reflection * front;
                        }
                    }

                    int start = offsets[System.Math.Min(bottom, maxBands)];
                    int end = offsets[System.Math.Min(top, maxBands)];
                    int size = end - start;
                    if (size <= 0)
                        continue;
                    int step = 1;
                    if (_direction[w, filt])
                    {
                        step = -1;
                        start = end - 1;
                    }
                    // Each short window has 128 lines of its own, one window after another.
                    start += w * 128;

                    for (int m = 0; m < size; m++, start += step)
                    {
                        int taps = System.Math.Min(m, order);
                        float value = spec[start];
                        for (int i = 1; i <= taps; i++)
                            value -= spec[start - i * step] * _lpc[i - 1];
                        spec[start] = value;
                    }
                }
            }
        }
    }
}
