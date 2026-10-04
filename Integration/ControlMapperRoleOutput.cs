using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using DivebombLogistics.Core;
using DivebombLogistics.Framework;
using SimHub.Plugins;

namespace DivebombLogistics.Integration;

/// <summary>
/// <see cref="IRoleOutput"/> backed by SimHub's Control Mapper (<see cref="PluginManager.GetControlMapperInterface"/>).
/// A single background worker executes <c>StartStopRole(role, durationMs)</c> one press after the other, because it
/// is unknown whether that call blocks for the press duration: the data thread only enqueues (allocation-free, never
/// blocks). <see cref="PluginManager.GetControlMapperInterface"/> never returns null (it only wraps a lookup of the
/// plugin that runs on every call), so availability is taken from the plugin itself: the worker lists the defined
/// button roles (<c>GetAvailableButtonRoles</c>) at start, every <see cref="LookupIntervalMs"/> and right after a
/// rejected press, and publishes them as an immutable set. <see cref="IsAvailable"/> and the role check in
/// <see cref="Press"/> only read that snapshot (no Control Mapper call on the data thread), so a deactivated Control
/// Mapper or an unknown role is refused at once instead of being found out by a confirmation timeout. Failures are
/// logged at most every <see cref="LogIntervalMs"/>. Shared by all modules; disposed in the shell's <c>End</c>.
/// </summary>
internal sealed class ControlMapperRoleOutput : IRoleOutput, IDisposable
{
    /// <summary>The worker refreshes the Control Mapper's role list this often (it may initialize after DLP, and roles can be added or removed).</summary>
    public const int LookupIntervalMs = 5000;

    /// <summary>Presses waiting beyond this are rejected (a dial never needs more; protects against runaway loops).</summary>
    public const int QueueCapacity = 64;

    /// <summary>Failures are logged at most this often.</summary>
    public const int LogIntervalMs = 10000;

    /// <summary>End(): how long to wait for the worker to finish the queued presses.</summary>
    private const int ShutdownTimeoutMs = 2000;

    private readonly PluginManager pluginManager;
    private readonly ILog log;
    private readonly BlockingCollection<RolePress> queue = new BlockingCollection<RolePress>(new ConcurrentQueue<RolePress>(), QueueCapacity);
    private readonly Stopwatch logClock = Stopwatch.StartNew();
    private readonly Thread worker;
    private volatile ControlMapperInterface controlMapper;

    /// <summary>Button roles the Control Mapper defined at the last refresh; empty = unavailable. Replaced, never mutated.</summary>
    private volatile HashSet<string> knownRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="logClock"/> time of the last role refresh (worker thread only).</summary>
    private long lastRefreshMs = long.MinValue / 2;
    private long nextLogMs;
    private int suppressedFailures;
    private int disposed;

    /// <param name="pluginManager">SimHub's plugin manager.</param>
    /// <param name="log">Receives rate-limited failures.</param>
    public ControlMapperRoleOutput(PluginManager pluginManager, ILog log)
    {
        this.pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
        this.log = log ?? NullLog.Instance;
        worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "DLP role output",
        };
        worker.Start();
    }

    /// <summary>True when the Control Mapper answered the last role refresh with at least one button role.</summary>
    public bool IsAvailable => knownRoles.Count > 0;

    /// <summary>
    /// Queues the press when the Control Mapper is available and defines <paramref name="role"/> (as of the last
    /// refresh, case-insensitive); false otherwise, so the caller reports a missing binding instead of waiting for a
    /// change that cannot come. Allocation-free apart from the queue node.
    /// </summary>
    public bool Press(string role, int durationMs)
    {
        if (string.IsNullOrEmpty(role) || Volatile.Read(ref disposed) != 0)
        {
            return false;
        }

        HashSet<string> roles = knownRoles;
        if (roles.Count == 0 || !roles.Contains(role))
        {
            return false;
        }

        try
        {
            if (queue.TryAdd(new RolePress(role, Math.Max(0, durationMs))))
            {
                return true;
            }

            LogFailure("role press queue is full, dropped " + role, null);
            return false;
        }
        catch (InvalidOperationException)
        {
            return false; // CompleteAdding was called: shutting down
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetButtonRoles()
    {
        ControlMapperInterface mapper = controlMapper ?? TryLookup();
        if (mapper == null)
        {
            return new string[0];
        }

        try
        {
            List<string> roles = mapper.GetAvailableButtonRoles();
            PublishRoles(roles);
            return roles == null ? new string[0] : roles.ToArray();
        }
        catch (Exception ex)
        {
            LogFailure("could not list the Control Mapper roles", ex);
            return new string[0];
        }
    }

    /// <summary>Stops accepting presses and waits briefly for the queued ones.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        queue.CompleteAdding();
        if (!worker.Join(ShutdownTimeoutMs))
        {
            log.Warn("Role output worker did not finish within " + ShutdownTimeoutMs.ToString(CultureInfo.InvariantCulture) + " ms.");
        }
    }

    /// <summary>Worker body. Never throws (an unhandled exception would terminate SimHub).</summary>
    private void Run()
    {
        try
        {
            RefreshRoles();
            while (!queue.IsCompleted)
            {
                if (queue.TryTake(out RolePress press, LookupIntervalMs))
                {
                    Execute(press);
                }

                if (logClock.ElapsedMilliseconds - lastRefreshMs >= LookupIntervalMs)
                {
                    RefreshRoles();
                }
            }
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding raced with TryTake: normal shutdown.
        }
        catch (Exception ex)
        {
            LogFailure("role output worker stopped", ex);
        }
    }

    private void Execute(RolePress press)
    {
        ControlMapperInterface mapper = controlMapper ?? TryLookup();
        if (mapper == null)
        {
            LogFailure("Control Mapper unavailable, dropped " + press.Role, null);
            return;
        }

        try
        {
            if (!mapper.StartStopRole(press.Role, press.DurationMs))
            {
                LogFailure("Control Mapper rejected role '" + press.Role + "' (unknown role or output not mapped)", null);
                RefreshRoles(); // the role may be gone or the plugin deactivated: later presses are refused at once
            }
        }
        catch (Exception ex)
        {
            LogFailure("pressing role '" + press.Role + "' failed", ex);
        }
    }

    /// <summary>Lists the Control Mapper's button roles (worker thread) and publishes them; null, empty or a failure means unavailable.</summary>
    private void RefreshRoles()
    {
        lastRefreshMs = logClock.ElapsedMilliseconds;
        ControlMapperInterface mapper = controlMapper ?? TryLookup();
        if (mapper == null)
        {
            PublishRoles(null);
            return;
        }

        try
        {
            PublishRoles(mapper.GetAvailableButtonRoles());
        }
        catch (Exception ex)
        {
            LogFailure("could not list the Control Mapper roles", ex);
            PublishRoles(null);
        }
    }

    /// <summary>Replaces <see cref="knownRoles"/> (any thread; the reference swap is atomic); logs when availability changes.</summary>
    private void PublishRoles(List<string> roles)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (roles != null)
        {
            foreach (string role in roles)
            {
                if (!string.IsNullOrEmpty(role))
                {
                    set.Add(role);
                }
            }
        }

        bool wasAvailable = knownRoles.Count > 0;
        knownRoles = set;
        if (set.Count > 0 && !wasAvailable)
        {
            log.Info("Control Mapper available: " + set.Count.ToString(CultureInfo.InvariantCulture) + " button roles.");
        }
        else if (set.Count == 0 && wasAvailable)
        {
            log.Warn("Control Mapper not available (no button roles): role presses are refused.");
        }
    }

    private ControlMapperInterface TryLookup()
    {
        try
        {
            ControlMapperInterface mapper = pluginManager.GetControlMapperInterface();
            controlMapper = mapper;
            return mapper;
        }
        catch (Exception ex)
        {
            LogFailure("Control Mapper lookup failed", ex);
            return null;
        }
    }

    private void LogFailure(string message, Exception ex)
    {
        long nowMs = logClock.ElapsedMilliseconds;
        if (nowMs < Interlocked.Read(ref nextLogMs))
        {
            Interlocked.Increment(ref suppressedFailures);
            return;
        }

        Interlocked.Exchange(ref nextLogMs, nowMs + LogIntervalMs);
        int suppressed = Interlocked.Exchange(ref suppressedFailures, 0);
        log.Warn("Role output: " + message + (ex == null ? string.Empty : ": " + ex.Message)
            + (suppressed > 0 ? " (" + suppressed.ToString(CultureInfo.InvariantCulture) + " further failures suppressed)" : string.Empty));
    }

    /// <summary>One queued press.</summary>
    private readonly struct RolePress
    {
        public RolePress(string role, int durationMs)
        {
            Role = role;
            DurationMs = durationMs;
        }

        public string Role { get; }

        public int DurationMs { get; }
    }
}
