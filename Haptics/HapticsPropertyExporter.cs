using System;
using DivebombLogistics.Core;
using DivebombLogistics.Framework;
using DivebombLogistics.Haptics.Balance;
using DivebombLogistics.Haptics.SlipLock;

namespace DivebombLogistics.Haptics;

/// <summary>
/// Registers every haptics property (<c>DLP.SlipLock.*</c>, <c>DLP.Balance.*</c>) through the shell's
/// <see cref="IPropertyRegistry"/>. All delegates are created once in <c>Init</c> and read preallocated output fields,
/// so nothing is allocated or pushed per frame; SimHub evaluates them on demand.
/// <para>
/// COMPATIBILITY: the relative names and value semantics are the v1/v2 contract (user ShakeIT profiles and dashboards);
/// only the SimHub prefix changed from <c>SlipLockPropertiesCalc.</c> to <c>DLP.</c> in v3.
/// </para>
/// <para>
/// Threading: the delegates read the output fields without locking. SimHub runs as a 32-bit process, where an 8-byte
/// double is not guaranteed to be read atomically, so a reader on another thread (a dash or overlay) may rarely see a
/// torn value for one frame. Accepted: ShakeIT and the haptic effects evaluate the properties on the data thread,
/// which also writes them.
/// </para>
/// </summary>
internal sealed class HapticsPropertyExporter
{
    /// <summary>Number of v1 slip/lock properties (8 channels × 5 wheels/mono + 3 max-G values).</summary>
    public const int SlipLockPropertyCount = 43;

    /// <summary>Number of balance properties (outputs, tags and debug values).</summary>
    public const int BalancePropertyCount = 22;

    private const string SlipLockPrefix = "SlipLock.";
    private const string BalancePrefix = "Balance.";
    private const string MonoSuffix = "Mono";

    /// <summary>Cached export texts for <see cref="BalancePath"/> (indexed by the enum value).</summary>
    private static readonly string[] PathTexts = { "none", "model", "direct" };

    /// <summary>Cached export texts for <see cref="ParamSource"/> (indexed by the enum value).</summary>
    private static readonly string[] SourceTexts = { "default", "preset", "learned", "session", "manual" };

    private readonly IPropertyRegistry registry;

    /// <param name="registry">The shell's property registry (publishes <c>DLP.&lt;name&gt;</c>).</param>
    public HapticsPropertyExporter(IPropertyRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>Registers the 43 v1 properties (names and value semantics unchanged).</summary>
    public void RegisterSlipLock(SlipLockOutputs outputs, MaxGTracker maxG)
    {
        if (outputs == null)
        {
            throw new ArgumentNullException(nameof(outputs));
        }

        if (maxG == null)
        {
            throw new ArgumentNullException(nameof(maxG));
        }

        // v1 registration order: per wheel all eight channels, then the monos.
        for (int wheel = 0; wheel < Wheels.Count; wheel++)
        {
            string name = Wheels.Names[wheel];
            int w = wheel; // captured once per wheel (closures are created here, never per frame)
            registry.Attach(SlipLockPrefix + "Slip." + name, () => outputs.Slip[w]);
            registry.Attach(SlipLockPrefix + "Lock." + name, () => outputs.Lock[w]);
            registry.Attach(SlipLockPrefix + "ABS." + name, () => outputs.Abs[w]);
            registry.Attach(SlipLockPrefix + "TC." + name, () => outputs.Tc[w]);
            registry.Attach(SlipLockPrefix + "SlipBlend." + name, () => outputs.SlipBlend[w]);
            registry.Attach(SlipLockPrefix + "LockBlend." + name, () => outputs.LockBlend[w]);
            registry.Attach(SlipLockPrefix + "SlipTC." + name, () => outputs.SlipTc[w]);
            registry.Attach(SlipLockPrefix + "LockABS." + name, () => outputs.LockAbs[w]);
        }

        registry.Attach(SlipLockPrefix + "Slip." + MonoSuffix, () => outputs.SlipMono);
        registry.Attach(SlipLockPrefix + "Lock." + MonoSuffix, () => outputs.LockMono);
        registry.Attach(SlipLockPrefix + "ABS." + MonoSuffix, () => outputs.AbsMono);
        registry.Attach(SlipLockPrefix + "TC." + MonoSuffix, () => outputs.TcMono);
        registry.Attach(SlipLockPrefix + "SlipBlend." + MonoSuffix, () => outputs.SlipBlendMono);
        registry.Attach(SlipLockPrefix + "LockBlend." + MonoSuffix, () => outputs.LockBlendMono);
        registry.Attach(SlipLockPrefix + "SlipTC." + MonoSuffix, () => outputs.SlipTcMono);
        registry.Attach(SlipLockPrefix + "LockABS." + MonoSuffix, () => outputs.LockAbsMono);

        registry.Attach(SlipLockPrefix + "MaxSway", () => maxG.MaxSway);
        registry.Attach(SlipLockPrefix + "MaxSurge", () => maxG.MaxSurge);
        registry.Attach(SlipLockPrefix + "MaxDecel", () => maxG.MaxDecel);
    }

    /// <summary>Registers the understeer/oversteer channels, tags and debug values (NaN/∞ exported as 0).</summary>
    public void RegisterBalance(BalanceOutputs b)
    {
        if (b == null)
        {
            throw new ArgumentNullException(nameof(b));
        }

        // Outputs (0..1) and tags.
        registry.Attach(BalancePrefix + "Understeer", () => Finite(b.Understeer));
        registry.Attach(BalancePrefix + "Oversteer", () => Finite(b.Oversteer));
        registry.Attach(BalancePrefix + "PowerOversteer", () => b.PowerOversteer);
        registry.Attach(BalancePrefix + "LiftOrBrakeOversteer", () => b.LiftOrBrakeOversteer);
        registry.Attach(BalancePrefix + "EntryUndersteer", () => b.EntryUndersteer);
        registry.Attach(BalancePrefix + "ExitUndersteer", () => b.ExitUndersteer);
        registry.Attach(BalancePrefix + "Countersteer", () => b.Countersteer);
        registry.Attach(BalancePrefix + "Spin", () => b.Spin);
        registry.Attach(BalancePrefix + "Active", () => b.Active);
        registry.Attach(BalancePrefix + "Confidence", () => Finite(b.Confidence));

        // Debug signals and effective vehicle-model parameters.
        registry.Attach(BalancePrefix + "YawRate", () => Finite(b.YawRate));
        registry.Attach(BalancePrefix + "YawRef", () => Finite(b.YawRef));
        registry.Attach(BalancePrefix + "YawRatio", () => Finite(b.YawRatio));
        registry.Attach(BalancePrefix + "BodySlipDeg", () => Finite(b.BodySlipDeg));
        registry.Attach(BalancePrefix + "G", () => Finite(b.G));
        registry.Attach(BalancePrefix + "K", () => Finite(b.K));
        registry.Attach(BalancePrefix + "Theta0", () => Finite(b.Theta0Deg));
        registry.Attach(BalancePrefix + "TauYaw", () => Finite(b.TauYaw));
        registry.Attach(BalancePrefix + "SamplesG", () => b.SamplesG);
        registry.Attach(BalancePrefix + "SamplesK", () => b.SamplesK);
        registry.Attach(BalancePrefix + "ParamSource", () => Text(SourceTexts, (int)b.GSource));
        registry.Attach(BalancePrefix + "Path", () => Text(PathTexts, (int)b.Path));
    }

    private static double Finite(double value) => MathUtil.IsFinite(value) ? value : 0.0;

    private static string Text(string[] texts, int index) =>
        index >= 0 && index < texts.Length ? texts[index] : texts[0];
}
