using System;
using System.Collections.Generic;
using SimHub.Plugins;
using User.SlipLockPropertiesCalc.Balance;
using User.SlipLockPropertiesCalc.Core;
using User.SlipLockPropertiesCalc.SlipLock;

namespace User.SlipLockPropertiesCalc.Integration;

/// <summary>
/// Registers every exported SimHub property as a delegate (<c>AttachDelegate</c>). All closures are created once in
/// <c>Init</c> and read preallocated output fields, so nothing is allocated or pushed per frame; SimHub evaluates the
/// delegates on demand. SimHub prefixes the names with the <b>runtime</b> plugin class name
/// (<c>SlipLockPropertiesCalc.</c>): registration goes through <see cref="PluginManager.AttachDelegate{T}(string, Type, Func{T}, string, bool, SupportStatus)"/>
/// with the plugin's <see cref="object.GetType"/>, because the <c>IPluginExtensions.AttachDelegate</c> extension takes
/// the prefix from the compile-time type of its receiver (an <c>IPlugin</c> field would publish "IPlugin.*").
/// <para>
/// Threading: the delegates read the output fields without locking. SimHub runs as a 32-bit process, where an 8-byte
/// double is not guaranteed to be read atomically, so a reader on another thread (a dash or overlay) may rarely see a
/// torn value for one frame. Accepted: ShakeIT and the haptic effects evaluate the properties on the data thread,
/// which also writes them.
/// </para>
/// </summary>
internal sealed class PropertyExporter
{
    /// <summary>Prefix SimHub adds to every property of this plugin (for logs, docs and ShakeIT formulas).</summary>
    public const string SimHubPrefix = "SlipLockPropertiesCalc.";

    private const string SlipLockPrefix = "SlipLock.";
    private const string BalancePrefix = "Balance.";
    private const string MonoSuffix = "Mono";

    /// <summary>Cached export texts for <see cref="BalancePath"/> (indexed by the enum value).</summary>
    private static readonly string[] PathTexts = { "none", "model", "direct" };

    /// <summary>Cached export texts for <see cref="ParamSource"/> (indexed by the enum value).</summary>
    private static readonly string[] SourceTexts = { "default", "preset", "learned", "session", "manual" };

    private readonly PluginManager pluginManager;
    private readonly Type pluginType;
    private readonly List<string> names = new List<string>();

    /// <param name="pluginManager">SimHub's plugin manager (from <c>Init</c>).</param>
    /// <param name="pluginType">The plugin's runtime type; its name is the property prefix.</param>
    public PropertyExporter(PluginManager pluginManager, Type pluginType)
    {
        this.pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
        this.pluginType = pluginType ?? throw new ArgumentNullException(nameof(pluginType));
    }

    /// <summary>True when SimHub will publish the properties under <see cref="SimHubPrefix"/> (plugin class not renamed).</summary>
    public bool PrefixMatchesPluginType => string.Equals(pluginType.Name + ".", SimHubPrefix, StringComparison.Ordinal);

    /// <summary>Full SimHub name of a registered property (prefix included), e.g. for the start-up self-check.</summary>
    public static string FullName(string name) => SimHubPrefix + name;

    /// <summary>Registered property names without the SimHub prefix, in registration order.</summary>
    public IReadOnlyList<string> Names => names;

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
            Add(SlipLockPrefix + "Slip." + name, () => outputs.Slip[w]);
            Add(SlipLockPrefix + "Lock." + name, () => outputs.Lock[w]);
            Add(SlipLockPrefix + "ABS." + name, () => outputs.Abs[w]);
            Add(SlipLockPrefix + "TC." + name, () => outputs.Tc[w]);
            Add(SlipLockPrefix + "SlipBlend." + name, () => outputs.SlipBlend[w]);
            Add(SlipLockPrefix + "LockBlend." + name, () => outputs.LockBlend[w]);
            Add(SlipLockPrefix + "SlipTC." + name, () => outputs.SlipTc[w]);
            Add(SlipLockPrefix + "LockABS." + name, () => outputs.LockAbs[w]);
        }

        Add(SlipLockPrefix + "Slip." + MonoSuffix, () => outputs.SlipMono);
        Add(SlipLockPrefix + "Lock." + MonoSuffix, () => outputs.LockMono);
        Add(SlipLockPrefix + "ABS." + MonoSuffix, () => outputs.AbsMono);
        Add(SlipLockPrefix + "TC." + MonoSuffix, () => outputs.TcMono);
        Add(SlipLockPrefix + "SlipBlend." + MonoSuffix, () => outputs.SlipBlendMono);
        Add(SlipLockPrefix + "LockBlend." + MonoSuffix, () => outputs.LockBlendMono);
        Add(SlipLockPrefix + "SlipTC." + MonoSuffix, () => outputs.SlipTcMono);
        Add(SlipLockPrefix + "LockABS." + MonoSuffix, () => outputs.LockAbsMono);

        Add(SlipLockPrefix + "MaxSway", () => maxG.MaxSway);
        Add(SlipLockPrefix + "MaxSurge", () => maxG.MaxSurge);
        Add(SlipLockPrefix + "MaxDecel", () => maxG.MaxDecel);
    }

    /// <summary>Registers the understeer/oversteer channels, tags and debug values (NaN/∞ exported as 0).</summary>
    public void RegisterBalance(BalanceOutputs b)
    {
        if (b == null)
        {
            throw new ArgumentNullException(nameof(b));
        }

        // Outputs (0..1) and tags.
        Add(BalancePrefix + "Understeer", () => Finite(b.Understeer));
        Add(BalancePrefix + "Oversteer", () => Finite(b.Oversteer));
        Add(BalancePrefix + "PowerOversteer", () => b.PowerOversteer);
        Add(BalancePrefix + "LiftOrBrakeOversteer", () => b.LiftOrBrakeOversteer);
        Add(BalancePrefix + "EntryUndersteer", () => b.EntryUndersteer);
        Add(BalancePrefix + "ExitUndersteer", () => b.ExitUndersteer);
        Add(BalancePrefix + "Countersteer", () => b.Countersteer);
        Add(BalancePrefix + "Spin", () => b.Spin);
        Add(BalancePrefix + "Active", () => b.Active);
        Add(BalancePrefix + "Confidence", () => Finite(b.Confidence));

        // Debug signals and effective vehicle-model parameters.
        Add(BalancePrefix + "YawRate", () => Finite(b.YawRate));
        Add(BalancePrefix + "YawRef", () => Finite(b.YawRef));
        Add(BalancePrefix + "YawRatio", () => Finite(b.YawRatio));
        Add(BalancePrefix + "BodySlipDeg", () => Finite(b.BodySlipDeg));
        Add(BalancePrefix + "G", () => Finite(b.G));
        Add(BalancePrefix + "K", () => Finite(b.K));
        Add(BalancePrefix + "Theta0", () => Finite(b.Theta0Deg));
        Add(BalancePrefix + "TauYaw", () => Finite(b.TauYaw));
        Add(BalancePrefix + "SamplesG", () => b.SamplesG);
        Add(BalancePrefix + "SamplesK", () => b.SamplesK);
        Add(BalancePrefix + "ParamSource", () => Text(SourceTexts, (int)b.GSource));
        Add(BalancePrefix + "Path", () => Text(PathTexts, (int)b.Path));
    }

    private void Add<T>(string name, Func<T> provider)
    {
        pluginManager.AttachDelegate(name, pluginType, provider);
        names.Add(name);
    }

    private static double Finite(double value) => MathUtil.IsFinite(value) ? value : 0.0;

    private static string Text(string[] texts, int index) =>
        index >= 0 && index < texts.Length ? texts[index] : texts[0];
}
