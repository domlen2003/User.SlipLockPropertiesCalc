using System;
using System.Collections.Generic;
using System.IO;
using DivebombLogistics.Haptics.Profiles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DivebombLogistics.Tests;

/// <summary>
/// Tests of <see cref="ShakeItProfileGenerator"/>. The v1 profiles are checked against a verbatim copy of the v1
/// string-building code (<see cref="V1Oracle"/>) with deterministic ids.
/// </summary>
internal sealed class ProfileGeneratorTests
{
    private const string NormalizedId = "<id>";
    private static readonly string[] IdMembers = { "ContainerId", "ProfileId" };

    /// <summary>
    /// The gear-shift effect exactly as the user exported it from ShakeIT
    /// (Documents\SimHub\Any Game - SlipLock Haptic Pedals old.siprofile). Do not edit: the generator must match it.
    /// </summary>
    private const string UserExportGearShiftJson = @"{
 ""ContainerType"": ""GearEffectContainer"",
 ""IsEnabled"": true,
 ""Gain"": 37.04761904761914,
 ""ModulateGainUsingRpms"": false,
 ""MaxFeedbackRpmPercent"": 90,
 ""MinFeedbackRpmPercent"": 50,
 ""GearMode"": 2,
 ""AlwaysIgnoreNeutral"": false,
 ""IgnoreNeutral"": true,
 ""NeutralDebouningTime"": 200.0,
 ""EngagingDebouningTime"": 1000.0,
 ""SettingsStore"": { ""Settings"": [ { ""Channels"": { ""All"": { ""Channels"": {
   ""0"": { ""IsEnabled"": false }, ""1"": { ""IsEnabled"": true }, ""2"": { ""IsEnabled"": true } } } },
   ""TypeName"": ""DeviceChannelActivationSettings"" } ] },
 ""ContainerId"": ""e068c7a2-4072-45bc-b40f-79db58b623b7"",
 ""Filter"": { ""Duration"": 90, ""FilterType"": ""PulseFilter"" },
 ""Output"": { ""UsePrehemptiveMode"": true, ""Frequency"": 15, ""OutputType"": ""SingleToneOutput"" }
}";

    [Test]
    public void DataExportProfile_MatchesV1()
    {
        JObject expected = WithDlpNames(ParseV1(V1Oracle.DataExportProfile(DeterministicIds())), "DLP Data Export", expectedFormulas: 0);
        JObject actual = ShakeItProfileGenerator.BuildDataExportProfile(DeterministicIds());

        AssertSameJson(expected, actual);
    }

    [Test]
    public void HapticPedalProfile_MatchesV1_ExceptChannelLayoutAndGearShift()
    {
        JObject expected = WithDlpNames(ParseV1(V1Oracle.HapticPedalProfile(DeterministicIds())), "DLP Haptic Pedals", expectedFormulas: 4);
        JObject actual = ShakeItProfileGenerator.BuildHapticPedalProfile(DeterministicIds());

        // v1 disabled the alternative effects with a text replace of "IsEnabled":true, which also switched off
        // their pedal channel. Document that quirk, then compare against the intended mapping.
        var effects = (JArray)expected["EffectsContainers"];
        for (int i = 2; i < 4; i++)
        {
            JObject channels = ChannelsOf(effects[i]);
            Assert.False((bool)channels["0"]["IsEnabled"] || (bool)channels["1"]["IsEnabled"] || (bool)channels["2"]["IsEnabled"], "v1 quirk present in the oracle");
        }

        // v1 put the throttle motor on channel 0; the user's pedal set has it on channel 2 (brake stays on 1).
        Assert.True((bool)ChannelsOf(effects[0])["0"]["IsEnabled"], "v1 throttle channel 0 in the oracle");
        ChannelsOf(effects[0])["0"]["IsEnabled"] = false;
        ChannelsOf(effects[0])["2"]["IsEnabled"] = true;  // SlipTC -> throttle motor
        ChannelsOf(effects[2])["2"]["IsEnabled"] = true;  // Slip*Throttle -> throttle motor
        ChannelsOf(effects[3])["1"]["IsEnabled"] = true;  // Lock*Brake -> brake motor

        // New effect appended after the v1 effects: the gear shift from the user's exported profile.
        effects.Add(ParseV1(UserExportGearShiftJson));

        AssertSameJson(expected, actual);
    }

    [Test]
    public void HapticPedalProfile_RoutesEachEffectToItsPedals()
    {
        JObject profile = ShakeItProfileGenerator.BuildHapticPedalProfile(DeterministicIds());
        var effects = (JArray)profile["EffectsContainers"];
        Assert.Equal(5, effects.Count, "four pedal effects plus the gear shift");

        // Channels: 0 clutch, 1 brake, 2 throttle.
        bool[] expectedBrake = { false, true, false, true, true };
        bool[] expectedThrottle = { true, false, true, false, true };
        bool[] expectedEnabled = { true, true, false, false, true };

        for (int i = 0; i < effects.Count; i++)
        {
            JObject channels = ChannelsOf(effects[i]);
            Assert.False((bool)channels["0"]["IsEnabled"], "clutch channel of effect " + i);
            Assert.Equal(expectedBrake[i], (bool)channels["1"]["IsEnabled"], "brake channel of effect " + i);
            Assert.Equal(expectedThrottle[i], (bool)channels["2"]["IsEnabled"], "throttle channel of effect " + i);
            Assert.Equal(expectedEnabled[i], (bool)effects[i]["IsEnabled"], "effect enabled " + i);
        }

        Assert.Equal("[DLP.SlipLock.SlipTC.Mono]", (string)effects[0]["FrontLeftFormula"]["Expression"], "formula uses the SimHub property prefix");
    }

    [Test]
    public void HapticPedalProfile_GearShiftMatchesUserExport()
    {
        JObject profile = ShakeItProfileGenerator.BuildHapticPedalProfile(DeterministicIds());
        var actual = (JObject)profile["EffectsContainers"][4];

        AssertSameJson(ParseV1(UserExportGearShiftJson), actual);
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
        Assert.Equal("DLP Balance", (string)profile["Name"]);

        var effects = (JArray)profile["EffectsContainers"];
        Assert.Equal(2, effects.Count, "two effects");

        const string Understeer = "[DLP.Balance.Understeer] * 100";
        const string Oversteer = "[DLP.Balance.Oversteer] * 100";
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

        AssertWritten(data, Path.Combine(target, "DLP_DataExport.siprofile"), "Saved! Import in ShakeIT and restart.");
        AssertWritten(pedals, Path.Combine(target, "DLP_HapticPedals.siprofile"), "Haptic pedal profile saved! Import in ShakeIT Motors tab.");
        AssertWritten(balance, Path.Combine(target, "DLP_Balance.siprofile"), ShakeItProfileGenerator.BalanceSavedMessage);

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

        Assert.Equal(4 + 6 + 3, idCount, "id count (3+1, 5+1, 2+1)");
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

    /// <summary>
    /// The only intended v3 (DLP) differences to the v1 oracle: the profile name and the property prefix in formulas
    /// (<c>[SlipLockPropertiesCalc.x]</c> became <c>[DLP.x]</c>). Applies them to <paramref name="v1"/> and checks how
    /// many formulas were rewritten, so nothing else can differ unnoticed.
    /// </summary>
    private static JObject WithDlpNames(JObject v1, string profileName, int expectedFormulas)
    {
        const string V1Prefix = "[SlipLockPropertiesCalc.";
        string dlpPrefix = "[" + ShakeItProfileGenerator.PropertyPrefix;
        int rewritten = 0;
        foreach (JToken token in v1.DescendantsAndSelf())
        {
            if (token is JValue value && value.Type == JTokenType.String && ((string)value).IndexOf(V1Prefix, StringComparison.Ordinal) >= 0)
            {
                value.Value = ((string)value).Replace(V1Prefix, dlpPrefix);
                rewritten++;
            }
        }

        Assert.Equal(expectedFormulas, rewritten, "formulas with the v1 prefix");
        v1["Name"] = profileName;
        return v1;
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
