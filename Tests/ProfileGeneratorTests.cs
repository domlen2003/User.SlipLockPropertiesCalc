using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using User.SlipLockPropertiesCalc.Profiles;

namespace User.SlipLockPropertiesCalc.Tests;

/// <summary>
/// Tests of <see cref="ShakeItProfileGenerator"/>. The v1 profiles are checked against a verbatim copy of the v1
/// string-building code (<see cref="V1Oracle"/>) with deterministic ids.
/// </summary>
internal sealed class ProfileGeneratorTests
{
    private const string NormalizedId = "<id>";
    private static readonly string[] IdMembers = { "ContainerId", "ProfileId" };

    [Test]
    public void DataExportProfile_MatchesV1()
    {
        JObject expected = ParseV1(V1Oracle.DataExportProfile(DeterministicIds()));
        JObject actual = ShakeItProfileGenerator.BuildDataExportProfile(DeterministicIds());

        AssertSameJson(expected, actual);
    }

    [Test]
    public void HapticPedalProfile_MatchesV1_ExceptFixedChannelMapOfDisabledEffects()
    {
        JObject expected = ParseV1(V1Oracle.HapticPedalProfile(DeterministicIds()));
        JObject actual = ShakeItProfileGenerator.BuildHapticPedalProfile(DeterministicIds());

        // v1 disabled the alternative effects with a text replace of "IsEnabled":true, which also switched off
        // their pedal channel. Document that quirk, then compare against the intended mapping.
        var effects = (JArray)expected["EffectsContainers"];
        for (int i = 2; i < 4; i++)
        {
            JObject channels = ChannelsOf(effects[i]);
            Assert.False((bool)channels["0"]["IsEnabled"] || (bool)channels["1"]["IsEnabled"] || (bool)channels["2"]["IsEnabled"], "v1 quirk present in the oracle");
        }

        ChannelsOf(effects[2])["0"]["IsEnabled"] = true; // Slip*Throttle -> throttle motor
        ChannelsOf(effects[3])["1"]["IsEnabled"] = true; // Lock*Brake -> brake motor

        AssertSameJson(expected, actual);
    }

    [Test]
    public void HapticPedalProfile_RoutesEachEffectToOnePedal()
    {
        JObject profile = ShakeItProfileGenerator.BuildHapticPedalProfile(DeterministicIds());
        var effects = (JArray)profile["EffectsContainers"];
        bool[] expectedThrottle = { true, false, true, false };
        bool[] expectedEnabled = { true, true, false, false };

        for (int i = 0; i < effects.Count; i++)
        {
            JObject channels = ChannelsOf(effects[i]);
            Assert.Equal(expectedThrottle[i], (bool)channels["0"]["IsEnabled"], "throttle channel of effect " + i);
            Assert.Equal(!expectedThrottle[i], (bool)channels["1"]["IsEnabled"], "brake channel of effect " + i);
            Assert.False((bool)channels["2"]["IsEnabled"], "clutch channel of effect " + i);
            Assert.Equal(expectedEnabled[i], (bool)effects[i]["IsEnabled"], "effect enabled " + i);
        }

        Assert.Equal("[SlipLockPropertiesCalc.SlipLock.SlipTC.Mono]", (string)effects[0]["FrontLeftFormula"]["Expression"], "formula uses the SimHub property prefix");
    }

    [Test]
    public void BalanceProfile_HasFrontUndersteerAndRearOversteer()
    {
        JObject profile = ShakeItProfileGenerator.BuildBalanceProfile(DeterministicIds());
        JObject haptic = ShakeItProfileGenerator.BuildHapticPedalProfile(DeterministicIds());

        // Same profile-level structure as the haptic pedal profile (Motors tab).
        var profileKeys = new List<string>();
        foreach (JProperty property in profile.Properties())
        {
            profileKeys.Add(property.Name);
        }

        var hapticKeys = new List<string>();
        foreach (JProperty property in haptic.Properties())
        {
            hapticKeys.Add(property.Name);
        }

        Assert.Equal(string.Join(",", hapticKeys), string.Join(",", profileKeys), "profile members");
        Assert.Equal(3, (int)profile["OutputMode"], "motors output mode");
        Assert.Equal("SlipLock Balance", (string)profile["Name"]);

        var effects = (JArray)profile["EffectsContainers"];
        Assert.Equal(2, effects.Count, "two effects");

        const string Understeer = "[SlipLockPropertiesCalc.Balance.Understeer] * 100";
        const string Oversteer = "[SlipLockPropertiesCalc.Balance.Oversteer] * 100";
        AssertEffect(effects[0], "Understeer (front)", Understeer, Understeer, string.Empty, string.Empty, 40);
        AssertEffect(effects[1], "Oversteer (rear)", string.Empty, string.Empty, Oversteer, Oversteer, 35);
    }

    [Test]
    public void WriteProfiles_WriteFilesWithFreshIdsAndV1Messages()
    {
        using var dir = new PersistenceTests.TempDirectory();
        string target = Path.Combine(dir.Path, "Documents", "SimHub");

        ProfileWriteResult data = ShakeItProfileGenerator.WriteDataExportProfile(target);
        ProfileWriteResult pedals = ShakeItProfileGenerator.WriteHapticPedalProfile(target);
        ProfileWriteResult balance = ShakeItProfileGenerator.WriteBalanceProfile(target);

        AssertWritten(data, Path.Combine(target, "SlipLock_DataExport.siprofile"), "Saved! Import in ShakeIT and restart.");
        AssertWritten(pedals, Path.Combine(target, "SlipLock_HapticPedals.siprofile"), "Haptic pedal profile saved! Import in ShakeIT Motors tab.");
        AssertWritten(balance, Path.Combine(target, "SlipLock_Balance.siprofile"), ShakeItProfileGenerator.BalanceSavedMessage);

        // Ids are real, distinct GUIDs within and across files.
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int idCount = 0;
        foreach (ProfileWriteResult result in new[] { data, pedals, balance })
        {
            foreach (JToken id in CollectIds(ParseV1(File.ReadAllText(result.Path))))
            {
                idCount++;
                Assert.True(Guid.TryParse((string)id, out _), "GUID: " + id);
                ids.Add((string)id);
            }
        }

        Assert.Equal(4 + 5 + 3, idCount, "id count (3+1, 4+1, 2+1)");
        Assert.Equal(idCount, ids.Count, "all ids distinct");

        // A second write regenerates ids and overwrites the file.
        string before = File.ReadAllText(data.Path);
        ShakeItProfileGenerator.WriteDataExportProfile(target);
        Assert.False(before == File.ReadAllText(data.Path), "fresh ids on every write");
    }

    [Test]
    public void WriteProfile_InvalidDirectory_ReturnsErrorMessage()
    {
        using var dir = new PersistenceTests.TempDirectory();
        string blocker = Path.Combine(dir.Path, "not-a-folder");
        File.WriteAllText(blocker, "x");

        ProfileWriteResult result = ShakeItProfileGenerator.WriteBalanceProfile(blocker);

        Assert.False(result.Success, "fails");
        Assert.True(result.Message.StartsWith("Error: ", StringComparison.Ordinal), result.Message);
        Assert.True(result.Message.Length > "Error: ".Length, "carries the exception message");

        ProfileWriteResult nullDir = ShakeItProfileGenerator.WriteHapticPedalProfile(null);
        Assert.False(nullDir.Success, "null directory fails gracefully");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Id factory producing a fixed GUID sequence (same for oracle and generator).</summary>
    private static Func<string> DeterministicIds()
    {
        int next = 0;
        return () =>
        {
            next++;
            return new Guid(next, 0, 0, new byte[8]).ToString();
        };
    }

    /// <summary>Parses like ShakeIT would see the text; date-like strings stay strings.</summary>
    private static JObject ParseV1(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return (JObject)JToken.ReadFrom(reader);
    }

    /// <summary>Semantic equality (after id normalization) plus identical member order and token types.</summary>
    private static void AssertSameJson(JObject expected, JObject actual)
    {
        NormalizeIds(expected);
        NormalizeIds(actual);
        Assert.True(JToken.DeepEquals(expected, actual), "DeepEquals");

        // DeepEquals treats 100 and 100.0 as equal; the compact text also pins types (Integer vs Float) and order.
        Assert.Equal(expected.ToString(Formatting.None), actual.ToString(Formatting.None), "compact JSON");
    }

    private static void NormalizeIds(JContainer token)
    {
        foreach (JToken id in CollectIds(token))
        {
            ((JValue)id).Value = NormalizedId;
        }
    }

    private static List<JToken> CollectIds(JContainer token)
    {
        var result = new List<JToken>();
        foreach (JToken descendant in token.DescendantsAndSelf())
        {
            if (descendant is JProperty property && Array.IndexOf(IdMembers, property.Name) >= 0 && property.Value.Type == JTokenType.String)
            {
                result.Add(property.Value);
            }
        }

        return result;
    }

    private static JObject ChannelsOf(JToken effect) =>
        (JObject)effect["SettingsStore"]["Settings"][0]["Channels"]["All"]["Channels"];

    private static void AssertEffect(JToken effect, string description, string fl, string fr, string rl, string rr, int frequency)
    {
        Assert.Equal("CustomEffectContainer", (string)effect["ContainerType"], description);
        Assert.True((bool)effect["IsEnabled"], description + " enabled");
        Assert.Equal(description, (string)effect["Description"]);
        Assert.Equal(fl, (string)effect["FrontLeftFormula"]["Expression"], description + " FL");
        Assert.Equal(fr, (string)effect["FrontRightFormula"]["Expression"], description + " FR");
        Assert.Equal(rl, (string)effect["RearLeftFormula"]["Expression"], description + " RL");
        Assert.Equal(rr, (string)effect["RearRightFormula"]["Expression"], description + " RR");
        Assert.Equal("Corners", (string)effect["AggregationMode"], description + " aggregation");
        Assert.True(effect["SettingsStore"] == null, description + " has no channel map");
        Assert.Equal(frequency, (int)effect["Output"]["Frequency"], description + " frequency");
        Assert.Equal("ToneOutput", (string)effect["Output"]["OutputType"], description + " output");
        Assert.Equal("GammaFilter", (string)effect["Filter"]["FilterType"], description + " filter");
    }

    private static void AssertWritten(ProfileWriteResult result, string expectedPath, string expectedMessage)
    {
        Assert.True(result.Success, "success: " + result.Message);
        Assert.Equal(expectedPath, result.Path, "path");
        Assert.Equal(expectedMessage, result.Message, "message");
        Assert.True(File.Exists(expectedPath), "file exists");
        Assert.False(File.Exists(expectedPath + ".tmp"), "no temp file");
        Assert.True(ParseV1(File.ReadAllText(expectedPath))["EffectsContainers"] is JArray, "valid profile JSON");
    }

    /// <summary>
    /// Verbatim copy of the v1 string builders (legacy SlipLockPropertiesCalc.cs, GenerateShakeITProfile and
    /// GenerateHapticPedalProfile), with only the GUID source and file IO replaced. Do not "fix" this code.
    /// </summary>
    private static class V1Oracle
    {
        public static string DataExportProfile(Func<string> newId)
        {
            string g(int _) => newId();
            string j = $@"{{""CarChoices"":[],""IncludeOutputSettingsInProfile"":false,""UnmuteEffectsAfterSimhubRestart"":true,""EffectsContainers"":[{{""ContainerType"":""GroupContainer"",""IsEnabled"":true,""Gain"":100.0,""Description"":""SlipLock Data Export"",""EffectsContainers"":[{{""ContainerType"":""WheelsSlipContainer"",""IsEnabled"":true,""Gain"":50.0,""BrakeFilter"":10,""MuteWhenLockEffectIsActive"":false,""ThrottleFilter"":10,""UseBrakeFilter"":false,""UseThrottleFilter"":false,""UseLegacyIracingAlgorythm"":false,""ContainerId"":""{g(0)}"",""AggregationMode"":""Corners"",""Filter"":{{""GammaValue"":1.0,""InputGain"":100.0,""MinimumForce"":0,""Threshold"":0,""FilterType"":""GammaFilter""}},""Output"":{{""UseHighFrequency"":false,""HighFrequency"":50,""WhiteNoise"":10,""UseWhiteNoise"":false,""FrequencyBasedOnPreFilter"":false,""UsePrehemptiveMode"":false,""Frequency"":50,""PropertyName"":""WheelSlip"",""ExportProperty"":true,""DisableOutput"":true,""OutputType"":""ToneOutput""}}}},{{""ContainerType"":""WheelsLockContainer"",""IsEnabled"":true,""Gain"":50.0,""IsLock"":true,""UseLegacyIracingAlgorythm"":false,""LockSensibility"":50.0,""BrakeFilter"":20,""ContainerId"":""{g(1)}"",""AggregationMode"":""Corners"",""Filter"":{{""GammaValue"":1.0,""InputGain"":100.0,""MinimumForce"":0,""Threshold"":0,""FilterType"":""GammaFilter""}},""Output"":{{""UseHighFrequency"":false,""HighFrequency"":50,""WhiteNoise"":10,""UseWhiteNoise"":false,""FrequencyBasedOnPreFilter"":false,""UsePrehemptiveMode"":false,""Frequency"":50,""PropertyName"":""WheelLock"",""ExportProperty"":true,""DisableOutput"":true,""OutputType"":""ToneOutput""}}}}],""ContainerId"":""{g(2)}"",""Filter"":null,""Output"":null}}],""AutoCalibrationRatio2"":100,""OutputMode"":1,""GlobalGain"":50.0,""UseProfileGain"":false,""Name"":""SlipLock Data Export"",""ProfileId"":""{g(3)}"",""GameCode"":null,""CarChoice"":null}}";
            return j;
        }

        public static string HapticPedalProfile(Func<string> newId)
        {
            string G() => newId();

            string ChMap(bool ch0, bool ch1, bool ch2)
            {
                string c0 = ch0 ? "true" : "false", c1 = ch1 ? "true" : "false", c2 = ch2 ? "true" : "false";
                return "\"SettingsStore\":{\"Settings\":[{\"Channels\":{\"All\":{\"Channels\":{\"0\":{\"IsEnabled\":" + c0 + "},\"1\":{\"IsEnabled\":" + c1 + "},\"2\":{\"IsEnabled\":" + c2 + "}}}},\"TypeName\":\"DeviceChannelActivationSettings\"}]}";
            }

            string pfx = "SlipLockPropertiesCalc.";

            string MotorEffect(string desc, string prop, bool throttleCh, bool brakeCh, int freq) =>
$@"{{
  ""ContainerType"":""CustomEffectContainer"",
  ""IsEnabled"":true,
  ""Gain"":100.0,
  ""Description"":""{desc}"",
  ""FrontLeftFormula"":{{""Expression"":""[{pfx}{prop}.Mono]""}},
  ""FrontRightFormula"":{{""Expression"":""""}},
  ""RearLeftFormula"":{{""Expression"":""""}},
  ""RearRightFormula"":{{""Expression"":""""}},
  ""ForceFrequencies"":false,
  ""FrontLeftFrequencyFormula"":{{""Expression"":""""}},
  ""FrontRightFrequencyFormula"":{{""Expression"":""""}},
  ""RearLeftFrequencyFormula"":{{""Expression"":""""}},
  ""RearRightFrequencyFormula"":{{""Expression"":""""}},
  ""AlwaysExecute"":false,
  {ChMap(throttleCh, brakeCh, false)},
  ""ContainerId"":""{G()}"",
  ""AggregationMode"":""Mono"",
  ""Filter"":{{""GammaValue"":1.0,""InputGain"":100.0,""MinimumForce"":0,""Threshold"":0,""FilterType"":""GammaFilter""}},
  ""Output"":{{""UseHighFrequency"":false,""HighFrequency"":50,""WhiteNoise"":10,""UseWhiteNoise"":false,""FrequencyBasedOnPreFilter"":false,""UsePrehemptiveMode"":false,""Frequency"":{freq},""OutputType"":""ToneOutput""}}
}}";

            string slipTC = MotorEffect("SlipTC Aggregate (throttle)", "SlipLock.SlipTC", true, false, 30);
            string lockABS = MotorEffect("LockABS Aggregate (brake)", "SlipLock.LockABS", false, true, 25);
            string slipBlend = MotorEffect("Slip*Throttle (throttle)", "SlipLock.SlipBlend", true, false, 30)
                .Replace("\"IsEnabled\":true", "\"IsEnabled\":false");
            string lockBlend = MotorEffect("Lock*Brake (brake)", "SlipLock.LockBlend", false, true, 25)
                .Replace("\"IsEnabled\":true", "\"IsEnabled\":false");

            string json = $@"{{
  ""CarChoices"":[],
  ""IncludeOutputSettingsInProfile"":false,
  ""UnmuteEffectsAfterSimhubRestart"":true,
  ""EffectsContainers"":[
    {slipTC},
    {lockABS},
    {slipBlend},
    {lockBlend}
  ],
  ""AutoCalibrationRatio2"":100,
  ""OutputMode"":3,
  ""GlobalGain"":50.0,
  ""UseProfileGain"":false,
  ""LastLoaded"":""0001-01-01T00:00:00"",
  ""Name"":""SlipLock Haptic Pedals"",
  ""ProfileId"":""{G()}"",
  ""GameCode"":null,
  ""CarChoice"":null
}}";
            return json;
        }
    }
}
