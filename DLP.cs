using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.Framework;
using DivebombLogistics.Haptics;
using DivebombLogistics.Haptics.UI;
using DivebombLogistics.Integration;
using DivebombLogistics.SpeedDial;
using DivebombLogistics.SpeedDial.UI;
using DivebombLogistics.UI;
using GameReaderCommon;
using SimHub.Plugins;

namespace DivebombLogistics;

/// <summary>
/// DLP - Divebomb Logistics Plugin: the SimHub plugin shell. It owns what is not a feature: the SimHub lifecycle, the
/// per-frame <see cref="FrameContext"/>, the SimHub services handed to the modules (properties, actions, Control Mapper
/// roles, logging), the legacy data migration and the settings page. Module orchestration (game/car/session changes,
/// the LMU car identity retry window, fault isolation) is in <see cref="ModuleHost"/>; features are modules
/// (<see cref="IDlpModule"/>) listed in <see cref="CreateModules"/>.
/// <para>
/// Threading: <see cref="DataUpdate"/> runs on SimHub's data thread, which owns every module's processing state. UI
/// and SimHub action callbacks reach a module only through its <see cref="DataThreadDispatcher"/>, drained at the start
/// of each frame. Hot path: allocation-free in steady state.
/// </para>
/// COMPATIBILITY: the class name is the SimHub property prefix (<c>DLP.</c>) and SimHub records plugin activation by the
/// full class name (<c>DivebombLogistics.DLP</c>); never rename either.
/// </summary>
[PluginDescription("DLP: haptic slip/lock and understeer/oversteer channels, speed-dial setup presets")]
[PluginAuthor("Dominik Lenz")]
[PluginName("Divebomb Logistics Plugin")]
public sealed class DLP : IPlugin, IDataPlugin, IWPFSettingsV2
{
    /// <summary>Start-up self-check probe: always registered by the haptics module and never null (starts at 5).</summary>
    private const string SelfCheckProperty = "SlipLock.MaxSway";

    private const string PluginsDataFolder = "PluginsData";
    private const string LogsFolder = "Logs";

    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly FrameContext ctx = new FrameContext();
    private SimHubLog log;
    private ModuleHost host;
    private ModuleRegistration[] registrations = new ModuleRegistration[0];
    private SimHubPropertyRegistry properties;
    private ControlMapperRoleOutput roles;

    /// <summary>Set by SimHub before <see cref="Init"/>.</summary>
    public PluginManager PluginManager { get; set; }

    /// <summary>Left menu icon (24x24).</summary>
    public ImageSource PictureIcon => this.ToIcon(Properties.Resources.sdkmenuicon);

    /// <summary>Short title for SimHub's left menu.</summary>
    public string LeftMenuTitle => "DLP";

    /// <summary>
    /// The modules in processing and tab order (Haptics first: its tab and its <c>SlipLock.MaxSway</c> self-check probe).
    /// Adding a module is one line here.
    /// </summary>
    private static ModuleRegistration[] CreateModules() => new[]
    {
        ModuleRegistration.Create(new HapticsModule(), m => new HapticsView(new HapticsViewModel(m))),
        ModuleRegistration.Create(new SpeedDialModule(), m => new SpeedDialView(new SpeedDialViewModel(m))),
    };

    // =====================================================================================================
    // SimHub lifecycle
    // =====================================================================================================

    /// <summary>Called once after plugin start-up: migrates old data, builds the modules and registers their properties.</summary>
    public void Init(PluginManager pluginManager)
    {
        log = new SimHubLog();
        log.Info("Starting plugin");
        PluginManager = pluginManager;

        string simHubDirectory = Path.GetDirectoryName(typeof(PluginManager).Assembly.Location) ?? ".";
        string pluginsData = Path.Combine(simHubDirectory, PluginsDataFolder);
        string dataRoot = Path.Combine(pluginsData, DlpNames.DataFolderName);
        string logDirectory = Path.Combine(simHubDirectory, LogsFolder);

        // Before any module reads its files.
        LegacyMigration.Run(pluginsData, log);

        var reader = new PluginManagerTelemetryReader(pluginManager);
        host = new ModuleHost(ctx, reader, log);
        properties = new SimHubPropertyRegistry(pluginManager, GetType());
        roles = new ControlMapperRoleOutput(pluginManager, log);
        var actionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Func<double> seconds = () => clock.Elapsed.TotalSeconds;
        Func<IList<string>> propertyNames = () => pluginManager.GetAllPropertiesNames() ?? new List<string>();

        registrations = CreateModules();
        foreach (ModuleRegistration registration in registrations)
        {
            IDlpModule module = registration.Module;
            SimHubLog moduleLog = SimHubLog.ForModule(module.Id);
            ModuleSlot slot = host.Add(module, moduleLog);
            host.Initialize(slot, new ModuleContext
            {
                Id = module.Id,
                Log = moduleLog,
                Reader = reader,
                Frame = ctx,
                Car = host.Car,
                Properties = properties,
                Actions = new SimHubActionRegistry(pluginManager, GetType(), slot.Dispatcher, actionNames),
                Roles = roles,
                Dispatcher = slot.Dispatcher,
                Errors = slot.Errors,
                Diagnostics = host.Diagnostics,
                DataDirectory = Path.Combine(dataRoot, module.Id),
                LogDirectory = logDirectory,
                Clock = seconds,
                ListPropertyNames = propertyNames,
            });
        }

        VerifyPropertyRegistration();
        log.Info("Plugin initialized: " + properties.Names.Count.ToString(CultureInfo.InvariantCulture) + " properties, "
            + actionNames.Count.ToString(CultureInfo.InvariantCulture) + " actions, data folder " + dataRoot);
    }

    /// <summary>
    /// Called once per SimHub data frame (60+ Hz) on the data thread. Allocation-free in steady state; never throws.
    /// </summary>
    public void DataUpdate(PluginManager pluginManager, ref GameData data)
    {
        if (host == null)
        {
            return; // Init did not complete (already logged by SimHub)
        }

        long startTicks = clock.ElapsedTicks;
        double now = clock.Elapsed.TotalSeconds;
        host.BeginFrame(now);

        bool running;
        try
        {
            FrameContextBuilder.Fill(ctx, data, now);
            running = host.PrepareFrame(now);
        }
        catch (Exception ex)
        {
            host.FrameFailed(ex, now);
            running = false;
        }

        host.EndFrame(now, running);
        host.Diagnostics.RecordFrame((clock.ElapsedTicks - startTicks) * 1000.0 / Stopwatch.Frequency);
    }

    /// <summary>Called at plugin manager stop: applies queued UI edits, then every module saves synchronously.</summary>
    public void End(PluginManager pluginManager)
    {
        host?.End(clock.Elapsed.TotalSeconds);
        try
        {
            roles?.Dispose();
        }
        catch (Exception ex)
        {
            log?.Error("Stopping the role output failed: " + ex.Message);
        }

        log?.Info("Plugin stopped");
    }

    /// <summary>Returns the settings page: one tab per module.</summary>
    public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
    {
        var view = new MainView();
        IReadOnlyList<ModuleSlot> slots = host?.Slots ?? new ModuleSlot[0];
        for (int i = 0; i < slots.Count && i < registrations.Length; i++)
        {
            view.AddTab(slots[i].Module.DisplayName, CreateTabContent(slots[i], registrations[i]));
        }

        return view;
    }

    /// <summary>The module's view, or an error text when the module is disabled or its view fails to load.</summary>
    private UIElement CreateTabContent(ModuleSlot slot, ModuleRegistration registration)
    {
        if (!slot.Active)
        {
            return MainView.CreateErrorContent(slot.Module.DisplayName + " could not start: " + slot.InitError
                + "\nSee SimHub's log for details.");
        }

        try
        {
            return registration.CreateView();
        }
        catch (Exception ex)
        {
            log?.Error("Settings page of module " + slot.Module.Id + " failed to load: " + ex);
            return MainView.CreateErrorContent(slot.Module.DisplayName + " page failed to load: " + ex.Message);
        }
    }

    /// <summary>
    /// Start-up self-check: every ShakeIT/dash formula and generated profile references the properties as
    /// <c>DLP.*</c>. A registration under another prefix would silently turn all effects off.
    /// </summary>
    private void VerifyPropertyRegistration()
    {
        try
        {
            if (!properties.PrefixMatchesPluginType)
            {
                log.Error("Plugin class " + GetType().Name + " does not match the property prefix " + DlpNames.PropertyPrefix
                    + ": ShakeIT profiles and dashboards will not find the properties.");
                return;
            }

            IReadOnlyList<string> names = properties.Names;
            string probe = Contains(names, SelfCheckProperty) ? SelfCheckProperty : (names.Count > 0 ? names[0] : null);
            if (probe != null && !properties.IsReadable(probe))
            {
                log.Error("Self-check failed: property " + DlpNames.FullName(probe) + " is not readable after registration.");
            }
        }
        catch (Exception ex)
        {
            log.Warn("Property self-check could not run: " + ex.Message);
        }
    }

    private static bool Contains(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
