namespace Plainspoken.Core.Audio;

/// <summary>
/// Converts a mono float stream at any input rate to a mono stream at the output rate
/// (16 kHz for Plainspoken) using windowed-sinc interpolation with a built-in low-pass filter,
/// so downsampling from 44.1/48 kHz does not alias. Feed chunks as they arrive.
/// Not thread-safe: call from one thread at a time.
/// </summary>
public sealed class StreamingResampler
{
    private const int HalfTaps = 16; // filter half-length, in input samples (scaled when downsampling)

    private readonly double _step;      // input samples per output sample
    private readonly double _cutoff;    // normalised to input Nyquist (0..1]
    private readonly int _halfWidth;    // half-window in input samples
    private readonly float[][] _table;  // precomputed, normalised filter weights per fractional phase
    private readonly List<float> _buffer = new();
    private double _position;           // next output position, in _buffer coordinates

    public StreamingResampler(int inputRate, int outputRate = WavEncoder.TargetSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);
        InputRate = inputRate;
        OutputRate = outputRate;
        _step = (double)inputRate / outputRate;
        // Low-pass a little below the output Nyquist when downsampling.
        _cutoff = Math.Min(1.0, 1.0 / _step) * 0.92;
        _halfWidth = (int)Math.Ceiling(HalfTaps / Math.Min(1.0, _cutoff));
        // Start with the window centred on sample 0 by padding history with silence.
        _buffer.AddRange(new float[_halfWidth]);
        _position = _halfWidth;
        _table = BuildTable();
    }

    private const int Phases = 256;

    public int InputRate { get; }

    public int OutputRate { get; }

    /// <summary>Adds input samples and appends every output sample that can now be computed.</summary>
    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (InputRate == OutputRate)
        {
            foreach (var x in input)
            {
                output.Add(x);
            }

            return;
        }

        foreach (var x in input)
        {
            _buffer.Add(x);
        }

        while (_position + _halfWidth < _buffer.Count)
        {
            output.Add(Interpolate(_position));
            _position += _step;
        }

        // Drop history that no future output needs.
        var drop = (int)Math.Floor(_position) - _halfWidth;
        if (drop > 4096)
        {
            _buffer.RemoveRange(0, drop);
            _position -= drop;
        }
    }

    /// <summary>Pads with silence so the last real input samples are emitted.</summary>
    public void Flush(List<float> output)
    {
        if (InputRate == OutputRate)
        {
            return;
        }

        Process(new float[_halfWidth + 1], output);
    }

    private float Interpolate(double t)
    {
        var centre = (int)Math.Floor(t);
        var phase = (int)Math.Round((t - centre) * Phases);
        var weights = _table[phase];
        var first = centre - _halfWidth + 1;
        float sum = 0;
        for (var k = 0; k < weights.Length; k++)
        {
            sum += _buffer[first + k] * weights[k];
        }

        return sum;
    }

    // Weights for input samples centre-halfWidth+1 .. centre+halfWidth, for each fractional offset.
    // Normalising each row keeps DC gain at exactly 1.
    private float[][] BuildTable()
    {
        var table = new float[Phases + 1][];
        for (var p = 0; p <= Phases; p++)
        {
            var frac = (double)p / Phases;
            var row = new double[2 * _halfWidth];
            double total = 0;
            for (var k = 0; k < row.Length; k++)
            {
                var d = frac + _halfWidth - 1 - k; // distance from the interpolation point
                row[k] = Sinc(d * _cutoff) * Blackman(d / (_halfWidth + 1));
                total += row[k];
            }

            table[p] = row.Select(w => (float)(w / total)).ToArray();
        }

        return table;
    }

    private static double Sinc(double x) => Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    // Blackman window over [-1, 1].
    private static double Blackman(double x) =>
        Math.Abs(x) >= 1 ? 0 : 0.42 + (0.5 * Math.Cos(Math.PI * x)) + (0.08 * Math.Cos(2 * Math.PI * x));
}
