using System;

namespace User.SlipLockPropertiesCalc.Tests;

internal static class Program
{
    /// <summary>
    /// Usage:
    ///   User.SlipLockPropertiesCalc.Tests.exe                 run all tests
    ///   User.SlipLockPropertiesCalc.Tests.exe --filter Balance run tests whose "Class.Method" contains the text
    ///   User.SlipLockPropertiesCalc.Tests.exe --replay rec.csv run the balance estimator offline over a recording
    /// Exit code = number of failed tests (or replay error code).
    /// </summary>
    private static int Main(string[] args)
    {
        string filter = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--replay" && i + 1 < args.Length)
            {
                return ReplayTool.Run(args[i + 1], Console.Out);
            }

            if (args[i] == "--filter" && i + 1 < args.Length)
            {
                filter = args[++i];
            }
        }

        return TestRunner.RunAll(filter);
    }
}
