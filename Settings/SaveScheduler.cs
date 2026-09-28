using System;
using User.SlipLockPropertiesCalc.Core;

namespace User.SlipLockPropertiesCalc.Settings;

/// <summary>
/// Decides when settings and the current car profile are written, so the data thread never saves more than
/// needed (pure logic: time is injected, saving is done by callbacks).
/// <list type="bullet">
/// <item>Settings: saved <see cref="SettingsDebounceSeconds"/> after the last change (a dragged slider saves once).</item>
/// <item>Car profile, user edits (sensitivity, overrides): debounced the same way.</item>
/// <item>Car profile, background changes (learner progress): saved at most every
/// <see cref="ProfileIntervalSeconds"/>, counted from the first change after the previous save.</item>
/// <item><see cref="FlushAll"/> saves everything dirty immediately (SimHub shutdown, car change).</item>
/// </list>
/// Tick is allocation-free and cheap enough for every frame. Not thread-safe: use from the data thread only.
/// </summary>
internal sealed class SaveScheduler
{
    /// <summary>Quiet time after the last settings change or profile edit before saving.</summary>
    public const double SettingsDebounceSeconds = 2.0;

    /// <summary>Minimum spacing of background profile saves while the learner keeps changing it.</summary>
    public const double ProfileIntervalSeconds = 60.0;

    /// <summary>Wait after a failed save callback before trying again (avoids a retry storm at frame rate).</summary>
    public const double RetryDelaySeconds = 10.0;

    private readonly Action _saveSettings;
    private readonly Action _saveProfile;
    private readonly ILog _log;

    /// <summary>Time at which the pending settings save is due; NaN when settings are clean.</summary>
    private double _settingsDue = double.NaN;

    /// <summary>Due time from user edits of the profile (debounced); NaN when none pending.</summary>
    private double _profileEditDue = double.NaN;

    /// <summary>Due time from background profile changes (interval); NaN when none pending.</summary>
    private double _profileBackgroundDue = double.NaN;

    /// <summary>Creates a scheduler.</summary>
    /// <param name="saveSettings">Writes the global settings (must not throw; failures are logged and retried anyway).</param>
    /// <param name="saveProfile">Writes the current car profile (typically asynchronously).</param>
    /// <param name="log">Receives callback failures; optional.</param>
    public SaveScheduler(Action saveSettings, Action saveProfile, ILog log = null)
    {
        _saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
        _saveProfile = saveProfile ?? throw new ArgumentNullException(nameof(saveProfile));
        _log = log ?? NullLog.Instance;
    }

    /// <summary>True while a settings save is pending.</summary>
    public bool SettingsDirty => !double.IsNaN(_settingsDue);

    /// <summary>True while a profile save is pending.</summary>
    public bool ProfileDirty => !double.IsNaN(_profileEditDue) || !double.IsNaN(_profileBackgroundDue);

    /// <summary>Settings changed at <paramref name="now"/> (seconds); restarts the debounce.</summary>
    public void MarkSettingsDirty(double now)
    {
        _settingsDue = now + SettingsDebounceSeconds;
    }

    /// <summary>
    /// The user edited the car profile at <paramref name="now"/> (sensitivity, override, learning lock):
    /// saved once the edits pause for <see cref="SettingsDebounceSeconds"/>.
    /// </summary>
    public void MarkProfileEdited(double now)
    {
        _profileEditDue = now + SettingsDebounceSeconds;
    }

    /// <summary>
    /// The car profile changed in the background at <paramref name="now"/> (learner progress). Cheap to call every
    /// frame: only the first mark after a save starts the <see cref="ProfileIntervalSeconds"/> countdown.
    /// </summary>
    public void MarkProfileDirty(double now)
    {
        if (double.IsNaN(_profileBackgroundDue))
        {
            _profileBackgroundDue = now + ProfileIntervalSeconds;
        }
    }

    /// <summary>Runs the callbacks whose due time has passed. Call once per frame with the wall time in seconds.</summary>
    public void Tick(double now)
    {
        if (now >= _settingsDue)
        {
            _settingsDue = double.NaN;
            if (!TryInvoke(_saveSettings, "settings"))
            {
                _settingsDue = now + RetryDelaySeconds;
            }
        }

        if (now >= _profileEditDue || now >= _profileBackgroundDue)
        {
            _profileEditDue = double.NaN;
            _profileBackgroundDue = double.NaN;
            if (!TryInvoke(_saveProfile, "car profile"))
            {
                _profileBackgroundDue = now + RetryDelaySeconds;
            }
        }
    }

    /// <summary>Immediately saves whatever is dirty (settings first, then the profile) and clears all pending saves.</summary>
    public void FlushAll()
    {
        if (SettingsDirty)
        {
            _settingsDue = double.NaN;
            TryInvoke(_saveSettings, "settings");
        }

        FlushProfile();
    }

    /// <summary>Immediately saves the profile if dirty (e.g. before switching to another car).</summary>
    public void FlushProfile()
    {
        if (ProfileDirty)
        {
            DiscardProfileChanges();
            TryInvoke(_saveProfile, "car profile");
        }
    }

    /// <summary>Forgets pending profile saves without saving (the profile was replaced, e.g. by an import that is saved separately).</summary>
    public void DiscardProfileChanges()
    {
        _profileEditDue = double.NaN;
        _profileBackgroundDue = double.NaN;
    }

    private bool TryInvoke(Action save, string what)
    {
        try
        {
            save();
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("SlipLock: saving " + what + " failed: " + ex.Message);
            return false;
        }
    }
}
