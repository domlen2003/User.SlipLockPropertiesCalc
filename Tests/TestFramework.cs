using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace DivebombLogistics.Tests;

/// <summary>Marks a test method. Methods may be static or instance (class needs a parameterless constructor).</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class TestAttribute : Attribute
{
}

/// <summary>Thrown by <see cref="Assert"/> on failure.</summary>
internal sealed class AssertionException : Exception
{
    public AssertionException(string message)
        : base(message)
    {
    }
}

/// <summary>Minimal assertion helpers.</summary>
internal static class Assert
{
    public static void True(bool condition, string message = "expected true")
    {
        if (!condition)
        {
            throw new AssertionException(message);
        }
    }

    public static void False(bool condition, string message = "expected false") => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{message ?? "values differ"}: expected <{expected}> but was <{actual}>");
        }
    }

    /// <summary>|expected - actual| &lt;= tolerance (NaN equals NaN).</summary>
    public static void Near(double expected, double actual, double tolerance, string message = null)
    {
        bool bothNaN = double.IsNaN(expected) && double.IsNaN(actual);
        if (!bothNaN && !(Math.Abs(expected - actual) <= tolerance))
        {
            throw new AssertionException(string.Format(
                CultureInfo.InvariantCulture,
                "{0}: expected {1:R} ± {2:R} but was {3:R}",
                message ?? "values differ",
                expected,
                tolerance,
                actual));
        }
    }

    public static void InRange(double value, double min, double max, string message = null)
    {
        if (!(value >= min && value <= max))
        {
            throw new AssertionException(string.Format(
                CultureInfo.InvariantCulture,
                "{0}: {1:R} not in [{2:R}, {3:R}]",
                message ?? "value out of range",
                value,
                min,
                max));
        }
    }

    public static TException Throws<TException>(Action action, string message = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }

        throw new AssertionException(message ?? $"expected {typeof(TException).Name}");
    }

    public static void Fail(string message) => throw new AssertionException(message);
}

/// <summary>Discovers and runs [Test] methods.</summary>
internal static class TestRunner
{
    public static int RunAll(string filter)
    {
        var tests = Assembly.GetExecutingAssembly()
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .Select(m => (Type: t, Method: m)))
            .Where(x => filter == null || (x.Type.Name + "." + x.Method.Name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(x => x.Type.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Method.Name, StringComparer.Ordinal)
            .ToList();

        int failed = 0;
        var total = Stopwatch.StartNew();
        foreach (var (type, method) in tests)
        {
            string name = type.Name + "." + method.Name;
            var sw = Stopwatch.StartNew();
            try
            {
                object instance = method.IsStatic ? null : Activator.CreateInstance(type, nonPublic: true);
                method.Invoke(instance, null);
                Console.WriteLine($"PASS {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (TargetInvocationException ex)
            {
                failed++;
                Exception inner = ex.InnerException ?? ex;
                Console.WriteLine($"FAIL {name}: {inner.GetType().Name}: {inner.Message}");
                if (!(inner is AssertionException))
                {
                    Console.WriteLine(inner.StackTrace);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed, {failed} failed ({total.ElapsedMilliseconds} ms)");
        return failed;
    }
}
