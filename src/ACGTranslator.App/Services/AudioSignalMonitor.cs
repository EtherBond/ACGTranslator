namespace ACGTranslator.Services;

public sealed record AudioSignalState(long ReceivedSamples, long AudibleSamples, double Peak, double RecentRms, double? LastSignalAt)
{
    public bool HasSignal => AudibleSamples > 0;
    // WAV output is PCM16. Sub-LSB values cannot establish a usable recording.
    public bool StartupSilent(double elapsedSeconds) => !HasSignal && elapsedSeconds >= 30;
}

public sealed class AudioSignalMonitor
{
    private readonly object _gate = new();
    private long _samples, _audible;
    private double _peak, _rms;
    private double? _last;
    public void Observe(ReadOnlySpan<float> samples, double at)
    {
        long audible = 0; double energy = 0, peak = 0;
        foreach (float value in samples)
        {
            if (!float.IsFinite(value)) continue;
            double level = Math.Abs(value); peak = Math.Max(peak, level); energy += value * (double)value;
            if (level >= 1d / 32768) audible++;
        }
        lock (_gate)
        {
            _samples += samples.Length; _audible += audible; _peak = Math.Max(_peak, peak);
            _rms = samples.Length == 0 ? 0 : Math.Sqrt(energy / samples.Length);
            if (audible > 0) _last = at;
        }
    }
    public AudioSignalState Snapshot() { lock (_gate) return new(_samples, _audible, _peak, _rms, _last); }
}
