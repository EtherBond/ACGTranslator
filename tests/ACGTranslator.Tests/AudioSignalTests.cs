using ACGTranslator.Services;

namespace ACGTranslator.Tests;

public class AudioSignalTests
{
    [Fact]
    public void Zero_callbacks_and_no_callbacks_both_trigger_startup_guard()
    {
        var monitor = new AudioSignalMonitor();
        Assert.False(monitor.Snapshot().StartupSilent(29.9));
        Assert.True(monitor.Snapshot().StartupSilent(30));
        monitor.Observe(new float[48000], 3);
        Assert.True(monitor.Snapshot().StartupSilent(60));
        Assert.Equal(48000, monitor.Snapshot().ReceivedSamples);
        Assert.Equal(0, monitor.Snapshot().AudibleSamples);
    }
    [Fact]
    public void Recorded_signal_survives_later_silence_and_nan_does_not_count()
    {
        var monitor = new AudioSignalMonitor();
        monitor.Observe([float.NaN, float.PositiveInfinity, 1e-9f], 1);
        Assert.False(monitor.Snapshot().HasSignal);
        monitor.Observe([.1f, -.2f, 0], 2);
        monitor.Observe(new float[16000], 60);
        var state = monitor.Snapshot();
        Assert.True(state.HasSignal); Assert.False(state.StartupSilent(60));
        Assert.Equal(2, state.AudibleSamples); Assert.Equal(2, state.LastSignalAt);
        Assert.InRange(state.Peak, .19999, .20001); Assert.Equal(0, state.RecentRms);
    }
}
