using System;
using System.IO;
using DivebombLogistics.Framework;
using DivebombLogistics.Haptics;
using DivebombLogistics.Haptics.Balance;
using DivebombLogistics.Haptics.SlipLock;
using DivebombLogistics.Tests.Fakes;

namespace DivebombLogistics.Tests;

internal static class Program
{
    /// <summary>
    /// Usage:
    ///   DivebombLogistics.Tests.exe                      run all tests
    ///   DivebombLogistics.Tests.exe --filter Balance      run tests whose "Class.Method" contains the text
    ///   DivebombLogistics.Tests.exe --replay rec.csv      run the balance estimator offline over a recording
    ///   DivebombLogistics.Tests.exe --properties out.txt  write the exported property names (v2 name -> DLP name)
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

            if (args[i] == "--properties" && i + 1 < args.Length)
            {
                return WritePropertyNames(args[i + 1]);
            }

            if (args[i] == "--filter" && i + 1 < args.Length)
            {
                filter = args[++i];
            }
        }

        return TestRunner.RunAll(filter);
    }

    /// <summary>Lists every haptics property as registered by the exporter, with its v1/v2 and its DLP full name.</summary>
    private static int WritePropertyNames(string path)
    {
        const string V2Prefix = "SlipLockPropertiesCalc.";
        var registry = new RecordingPropertyRegistry();
        var exporter = new HapticsPropertyExporter(registry);
        exporter.RegisterSlipLock(new SlipLockOutputs(), new MaxGTracker());
        exporter.RegisterBalance(new BalanceOutputs());
        using (var writer = new StreamWriter(path))
        {
            writer.WriteLine("# " + registry.Names.Count + " exported properties: v1/v2 full name -> v3 (DLP) full name");
            foreach (string name in registry.Names)
            {
                writer.WriteLine(V2Prefix + name + " -> " + DlpNames.FullName(name));
            }
        }

        Console.WriteLine(registry.Names.Count + " property names written to " + path);
        return 0;
    }
}
