namespace DivebombLogistics.Framework;

/// <summary>
/// Frame statistics of the plugin shell, shown in the modules' diagnostics. Written by the shell at the end of every
/// <c>DataUpdate</c>; read it on the data thread only (a double read from another thread can be torn in SimHub's
/// 32-bit process), e.g. while filling a UI snapshot.
/// </summary>
internal sealed class ShellDiagnostics
{
    /// <summary>Smoothing factor of the displayed <c>DataUpdate</c> duration (exponential moving average).</summary>
    public const double TimingSmoothing = 0.05;

    /// <summary>Frames processed since start-up.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Smoothed duration of a whole <c>DataUpdate</c> in milliseconds (all modules).</summary>
    public double DataUpdateMs { get; private set; }

    /// <summary>Counts one frame that took <paramref name="elapsedMs"/>.</summary>
    public void RecordFrame(double elapsedMs)
    {
        FrameCount++;
        DataUpdateMs += (elapsedMs - DataUpdateMs) * TimingSmoothing;
    }
}
