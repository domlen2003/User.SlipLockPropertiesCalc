using System;

namespace User.SlipLockPropertiesCalc.Balance;

/// <summary>Vehicle-model starting values for a car class (spec 4.3). NaN = use the global default.</summary>
internal sealed class ClassPresetValues
{
    public ClassPresetValues(double wheelbaseM, double k, double betaOnsetMarginDeg, double betaFullMarginDeg, double betaMarginMultiplier, bool theta0LearningMandatory)
    {
        WheelbaseM = wheelbaseM;
        K = k;
        BetaOnsetMarginDeg = betaOnsetMarginDeg;
        BetaFullMarginDeg = betaFullMarginDeg;
        BetaMarginMultiplier = betaMarginMultiplier;
        Theta0LearningMandatory = theta0LearningMandatory;
    }

    public double WheelbaseM { get; }

    public double K { get; }

    public double BetaOnsetMarginDeg { get; }

    public double BetaFullMarginDeg { get; }

    /// <summary>Multiplier applied to the (global) beta margins, e.g. 2.5 for rally.</summary>
    public double BetaMarginMultiplier { get; }

    /// <summary>
    /// Spec 4.3 "θ0 learning mandatory" (ovals). Informational: the estimator learns θ0 on straights before it
    /// accepts any G/K sample for every class (spec 5.2), and the steering-sign vote works at any lateral level, so
    /// ovals need no special handling. Known deviation for rally: the spec's handbrake tag / oversteer suppression is
    /// not implemented, because no verified handbrake telemetry path exists for the supported sims (DESIGN 1).
    /// </summary>
    public bool Theta0LearningMandatory { get; }
}

/// <summary>Class preset table and car-class auto-detection.</summary>
internal static class ClassPresets
{
    private static readonly ClassPresetValues FormulaPrototype = new ClassPresetValues(3.0, 0.0008, 1.2, 6.0, 1.0, false);
    private static readonly ClassPresetValues Gt = new ClassPresetValues(2.7, 0.0013, 1.5, 8.0, 1.0, false);
    private static readonly ClassPresetValues RoadTouring = new ClassPresetValues(2.6, 0.0020, 2.0, 10.0, 1.0, false);
    private static readonly ClassPresetValues RallyLoose = new ClassPresetValues(2.5, 0.0015, double.NaN, double.NaN, 2.5, false);
    private static readonly ClassPresetValues Oval = new ClassPresetValues(double.NaN, double.NaN, double.NaN, double.NaN, 1.0, true);

    /// <summary>Returns the values for a preset, or null for <see cref="BalanceClassPreset.None"/>.</summary>
    public static ClassPresetValues Get(BalanceClassPreset preset)
    {
        switch (preset)
        {
            case BalanceClassPreset.FormulaPrototype:
                return FormulaPrototype;
            case BalanceClassPreset.GT:
                return Gt;
            case BalanceClassPreset.RoadTouring:
                return RoadTouring;
            case BalanceClassPreset.RallyLoose:
                return RallyLoose;
            case BalanceClassPreset.Oval:
                return Oval;
            default:
                return null;
        }
    }

    /// <summary>
    /// Heuristic mapping from a sim's car class / car name to a preset (case-insensitive substring match).
    /// Allocates (ToLowerInvariant): call only on car change.
    /// </summary>
    public static BalanceClassPreset FromCarClass(string carClass, string carName)
    {
        string text = ((carClass ?? string.Empty) + " " + (carName ?? string.Empty)).ToLowerInvariant();
        if (text.Trim().Length == 0)
        {
            return BalanceClassPreset.None;
        }

        if (ContainsAny(text, "rally", "dirt", "rallycross", "wrc", " rx"))
        {
            return BalanceClassPreset.RallyLoose;
        }

        if (ContainsAny(text, "oval", "nascar", "stock car", "arca", "late model", "sprint car", "midget", "supermodified"))
        {
            return BalanceClassPreset.Oval;
        }

        if (ContainsAny(text, "mx-5", "mx5", "tcr", "touring", "road", "street", "production", "btcc", "sedan"))
        {
            return BalanceClassPreset.RoadTouring;
        }

        if (ContainsAny(text, "hyper", "lmh", "lmdh", "lmp", "gtp", "dpi", "prototype", "formula", "f1", "f2", "f3", "f4", "indy", "open wheel", "super formula"))
        {
            return BalanceClassPreset.FormulaPrototype;
        }

        if (ContainsAny(text, "gt3", "gt4", "gte", "gtd", "gtlm", "gt2", "lmgt", "gt"))
        {
            return BalanceClassPreset.GT;
        }

        return BalanceClassPreset.None;
    }

    private static bool ContainsAny(string text, params string[] tokens)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (text.IndexOf(tokens[i], StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
