using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Persistence;
using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.Framework;
using DivebombLogistics.Haptics;
using DivebombLogistics.Haptics.Balance;
using DivebombLogistics.Haptics.Settings;
using DivebombLogistics.Haptics.UI;
using DivebombLogistics.Tests.Fakes;

namespace DivebombLogistics.Tests;

/// <summary>
/// <see cref="HapticsModule"/> running inside the real <see cref="ModuleHost"/> with fake SimHub services: the
/// exported property contract, the data folder layout, the LMU placeholder-key handling and the UI host contract.
/// </summary>
internal sealed class HapticsModuleTests
{
    /// <summary>
    /// The 65 relative property names of v1/v2 (<c>SlipLockPropertiesCalc.*</c>), in v2 registration order, written out
    /// independently of the exporter: v3 publishes exactly these names under the <c>DLP.</c> prefix.
    /// </summary>
    internal static List<string> V2PropertyNames()
    {
        var names = new List<string>();
        string[] wheels = { "FrontLeft", "FrontRight", "RearLeft", "RearRight" };
        string[] channels = { "Slip", "Lock", "ABS", "TC", "SlipBlend", "LockBlend", "SlipTC", "LockABS" };
        foreach (string wheel in wheels)
        {
            foreach (string channel in channels)
            {
                names.Add("SlipLock." + channel + "." + wheel);
            }
        }

        foreach (string channel in channels)
        {
            names.Add("SlipLock." + channel + ".Mono");
        }

        names.AddRange(new[] { "SlipLock.MaxSway", "SlipLock.MaxSurge", "SlipLock.MaxDecel" });
        foreach (string balance in new[]
        {
            "Understeer", "Oversteer", "PowerOversteer", "LiftOrBrakeOversteer", "EntryUndersteer", "ExitUndersteer",
            "Countersteer", "Spin", "Active", "Confidence", "YawRate", "YawRef", "YawRatio", "BodySlipDeg", "G", "K",
            "Theta0", "TauYaw", "SamplesG", "SamplesK", "ParamSource", "Path",
        })
        {
            names.Add("Balance." + balance);
        }

        return names;
    }

    [Test]
    public void Init_RegistersExactlyTheV2PropertyNames()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var rig = new ModuleTestRig(dir.Path);
        var module = new HapticsModule();
        ModuleSlot slot = rig.Add(module);

        Assert.True(slot.Active, "initialized: " + slot.InitError);
        List<string> expected = V2PropertyNames();
        Assert.Equal(65, expected.Count, "v2 contract size");
        Assert.Equal(HapticsPropertyExporter.SlipLockPropertyCount + HapticsPropertyExporter.BalancePropertyCount, expected.Count, "exporter constants");
        Assert.Equal(string.Join("\n", expected), string.Join("\n", rig.Properties.Names), "names and order");
        Assert.Equal(65, module.PropertyCount, "count reported by the module");
        Assert.Equal("DLP.", DlpNames.PropertyPrefix, "SimHub prefix");
        Assert.Equal(DlpNames.PropertyPrefix, Haptics.Profiles.ShakeItProfileGenerator.PropertyPrefix, "ShakeIT formulas use the same prefix");

        // Value semantics at start-up: max G starts at 5, everything else 0 / "none".
        Assert.Equal(5.0, (double)rig.Properties.Read("SlipLock.MaxSway"), "MaxSway initial (self-check probe)");
        Assert.Equal(0.0, (double)rig.Properties.Read("SlipLock.SlipTC.Mono"), "mono output");
        Assert.Equal(0.0, (double)rig.Properties.Read("Balance.Understeer"), "balance output");
        Assert.Equal("none", (string)rig.Properties.Read("Balance.Path"), "path text");
        Assert.Equal("default", (string)rig.Properties.Read("Balance.ParamSource"), "source text");
    }

    [Test]
    public void Settings_LiveInTheModuleFolderAndAreSavedOnEnd()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var rig = new ModuleTestRig(dir.Path);
        var module = new HapticsModule();
        rig.Add(module);
        string settingsPath = Path.Combine(dir.Path, HapticsModule.ModuleId, HapticsSettingsStore.FileName);

        IHapticsHost host = module;
        host.EditSettings(s => s.SlipThreshold = 7.5);
        Assert.Equal(5.0, module.Settings.SlipThreshold, "queued, not applied on the UI thread");
        rig.RunFrame();
        Assert.Equal(7.5, module.Settings.SlipThreshold, "applied on the data thread");

        // Saved 2 s after the last change (asynchronously).
        rig.RunFrame(SaveScheduler.SettingsDebounceSeconds + 0.1);
        Assert.True(WaitForFile(settingsPath), "settings written after the debounce");

        module.Settings.ShowDebugView = true;
        host.NotifySettingsChanged();
        rig.Host.End(rig.Now);
        HapticsSettingsStore.LoadResult saved = HapticsSettingsStore.Load(Path.GetDirectoryName(settingsPath), NullLog.Instance);
        Assert.Equal(7.5, saved.Settings.SlipThreshold, "numeric edit saved");
        Assert.True(saved.Settings.ShowDebugView, "synchronous save in End");

        // A second start reads them back.
        var restarted = new ModuleTestRig(dir.Path);
        var second = new HapticsModule();
        restarted.Add(second);
        Assert.Equal(7.5, second.Settings.SlipThreshold, "loaded at Init");
    }

    [Test]
    public void LmuPlaceholderKey_IsNeverSavedAndCarriesOverToTheNativeKey()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var rig = new ModuleTestRig(dir.Path);
        var module = new HapticsModule();
        rig.Add(module);
        IHapticsHost host = module;
        string carsRoot = Path.Combine(dir.Path, HapticsModule.ModuleId, CarFileNaming.CarsFolderName);

        rig.Reader.Set(CarIdentityResolver.LmuVehicleModelPath, new byte[30]); // LMU's first frames: no model name yet
        rig.SetGame("LMU", "GT3_Iron Lynx 2026_61", "296GT3 Custom Team 2025", "GT3");
        rig.RunFrame();
        var snapshot = new HapticsSnapshot();
        host.CopySnapshot(new HapticsSnapshot()); // before the first snapshot refresh: must not throw
        rig.RunFrame(0.1);
        host.CopySnapshot(snapshot);
        Assert.Equal("296GT3 Custom Team 2025", snapshot.CarKey, "placeholder key");
        Assert.True(snapshot.CarKeyProvisional, "shown as provisional");

        host.SetSensitivity(SensitivityKind.Slip, 150);
        rig.RunFrame();
        rig.RunFrame(SaveScheduler.SettingsDebounceSeconds + 0.1);
        Assert.False(Directory.Exists(carsRoot), "no file for the placeholder key");

        // The native model name appears: the shell retries once per second inside the window.
        rig.Reader.Set(CarIdentityResolver.LmuVehicleModelPath, Encoding.ASCII.GetBytes("Ferrari 296 GT3\0"));
        rig.RunFrame(CarIdentityTracker.RetryIntervalSeconds);
        rig.RunFrame(0.1);
        host.CopySnapshot(snapshot);
        Assert.Equal("Ferrari 296 GT3", snapshot.CarKey, "native key");
        Assert.False(snapshot.CarKeyProvisional, "final");
        Assert.Equal(150.0, snapshot.SlipSensitivity, "edit carried over");

        rig.RunFrame(SaveScheduler.SettingsDebounceSeconds + 0.1);
        string nativePath = CarFileNaming.GetCarFilePath(Path.Combine(dir.Path, HapticsModule.ModuleId), "LMU", "Ferrari 296 GT3");
        Assert.True(WaitForFile(nativePath), "native profile saved");
        rig.Host.End(rig.Now);
        Assert.Equal(1, Directory.GetFiles(carsRoot, "*.json", SearchOption.AllDirectories).Length, "only the native profile exists");
        Assert.Equal(150.0, new CarProfileStore(Path.Combine(dir.Path, HapticsModule.ModuleId), NullLog.Instance)
            .Load("LMU", "Ferrari 296 GT3", null, null).SlipSensitivity, "saved value");
    }

    [Test]
    public void RFactor2CarChange_SavesThePreviousCarsPendingEdit()
    {
        // rF2 never reports a native model name, so every key "should retry" until its window is over. The previous
        // car must be judged by its own window, not by the one the shell has just started for the next car.
        using var dir = new PersistenceTests.TempDirectory();
        var rig = new ModuleTestRig(dir.Path);
        var module = new HapticsModule();
        rig.Add(module);
        IHapticsHost host = module;
        string root = Path.Combine(dir.Path, HapticsModule.ModuleId);

        rig.SetGame("RFactor2", "car_a", "Car A");
        rig.RunFrame();
        rig.RunFrames((int)CarIdentityTracker.RetryWindowSeconds + 2, 1.0); // the key is final now
        host.SetSensitivity(SensitivityKind.Slip, 250);
        rig.RunFrame();
        rig.SetGame("RFactor2", "car_b", "Car B"); // well inside the 2 s save debounce
        rig.RunFrame();

        string pathA = CarFileNaming.GetCarFilePath(root, "RFactor2", "Car A");
        Assert.True(WaitForFile(pathA), "car A saved on the car change");
        Assert.Equal(250.0, new CarProfileStore(root, NullLog.Instance).Load("RFactor2", "Car A", null, null).SlipSensitivity, "edit kept");
        rig.Host.End(rig.Now);
    }

    [Test]
    public void LmuFinalLiveryKey_IsNotCarriedOverToAnotherCar()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var rig = new ModuleTestRig(dir.Path);
        var module = new HapticsModule();
        rig.Add(module);
        IHapticsHost host = module;
        string root = Path.Combine(dir.Path, HapticsModule.ModuleId);

        // Car A never reports its model name: after the window its livery key is final.
        rig.Reader.Set(CarIdentityResolver.LmuVehicleModelPath, new byte[30]);
        rig.SetGame("LMU", "GT3_Iron Lynx 2026_61", "296GT3 Custom Team 2025", "GT3");
        rig.RunFrame();
        rig.RunFrames((int)CarIdentityTracker.RetryWindowSeconds + 2, 1.0);
        host.SetSensitivity(SensitivityKind.Slip, 300);
        rig.RunFrame();

        // Another car (different CarId) that reports its native model name.
        rig.Reader.Set(CarIdentityResolver.LmuVehicleModelPath, Encoding.ASCII.GetBytes("Porsche 963\0"));
        rig.SetGame("LMU", "HY_Penske 2026_5", "963 Penske 2026", "Hypercar");
        rig.RunFrame();
        rig.RunFrame(0.1);
        var snapshot = new HapticsSnapshot();
        host.CopySnapshot(snapshot);
        Assert.Equal("Porsche 963", snapshot.CarKey, "native key of car B");
        Assert.Equal(100.0, snapshot.SlipSensitivity, "car B starts fresh");

        string pathA = CarFileNaming.GetCarFilePath(root, "LMU", "296GT3 Custom Team 2025");
        Assert.True(WaitForFile(pathA), "car A saved under its final livery key");
        Assert.Equal(300.0, new CarProfileStore(root, NullLog.Instance).Load("LMU", "296GT3 Custom Team 2025", null, null).SlipSensitivity);
        rig.Host.End(rig.Now);
    }

    [Test]
    public void SnapshotCarriesShellDiagnosticsAndErrors()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var rig = new ModuleTestRig(dir.Path);
        var module = new HapticsModule();
        rig.Add(module);
        rig.SetGame("IRacing", "id", "Mazda MX-5 Cup");
        rig.RunFrames(3, 0.1);

        var snapshot = new HapticsSnapshot();
        ((IHapticsHost)module).CopySnapshot(snapshot);
        Assert.True(snapshot.GameRunning && snapshot.HasCar, "game and car");
        Assert.Equal("IRacing", snapshot.GameName);
        Assert.True(snapshot.FrameCount >= 2, "frame count from the shell");
        Assert.Equal(string.Empty, snapshot.LastError, "no error");

        rig.RunFailedFrame(new InvalidOperationException("frame broken"));
        ((IHapticsHost)module).CopySnapshot(snapshot);
        Assert.True(snapshot.LastError.Contains("frame broken"), "shell errors reach the haptics diagnostics: " + snapshot.LastError);
        Assert.Equal(0.0, (double)rig.Properties.Read("SlipLock.Slip.Mono"), "outputs zeroed by OnFault");
    }

    private static bool WaitForFile(string path)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }

        return File.Exists(path);
    }
}
