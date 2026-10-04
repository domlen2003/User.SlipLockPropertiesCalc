using System;
using System.Collections.Generic;
using DivebombLogistics.SpeedDial.Model;
using DivebombLogistics.UI;

namespace DivebombLogistics.SpeedDial.UI.ViewModels;

/// <summary>
/// One preset row of the Speed Dial tab: selection marker, name (inline rename), Dial slot dropdown, per-channel values
/// and the Apply / Capture current / Rename / Delete buttons.
/// <para>
/// Rows are kept across snapshot refreshes (matched by preset id) and updated in place through <see cref="Update"/>,
/// <see cref="ShowSelected"/> and <see cref="ShowSlot"/>, which never call back into the host; only user edits do.
/// </para>
/// </summary>
public sealed class PresetItem : ObservableObject
{
    private readonly IPresetRowOwner _owner;
    private readonly PresetValueItem[] _values;
    private string _name;
    private string _editName = string.Empty;
    private bool _isRenaming;
    private bool _isSelected;
    private int _includedCount;
    private SlotOption _selectedSlot;
    private IReadOnlyList<SlotOption> _slotOptions;

    /// <param name="owner">The page view model.</param>
    /// <param name="summary">The preset as the snapshot shows it.</param>
    /// <param name="slotOptions">Choices of the slot dropdown (first = "no slot").</param>
    /// <param name="supported">Per channel: the sim reports it.</param>
    internal PresetItem(IPresetRowOwner owner, PresetSummary summary, IReadOnlyList<SlotOption> slotOptions, bool[] supported)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        if (summary == null)
        {
            throw new ArgumentNullException(nameof(summary));
        }

        Id = summary.Id;
        _name = summary.Name;
        _slotOptions = slotOptions ?? throw new ArgumentNullException(nameof(slotOptions));
        _selectedSlot = slotOptions.Count > 0 ? slotOptions[0] : null;
        _values = new PresetValueItem[DialChannels.Count];
        for (int i = 0; i < _values.Length; i++)
        {
            _values[i] = new PresetValueItem((DialChannel)i, CommitValue, owner.CurrentValue);
        }

        ApplyCommand = new RelayCommand(() => _owner.Apply(this), () => _owner.CanEditPresets && _includedCount > 0);
        CaptureCommand = new RelayCommand(() => _owner.Capture(this), () => _owner.CanEditPresets);
        BeginRenameCommand = new RelayCommand(BeginRename, () => _owner.CanEditPresets);
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(CancelRename);
        DeleteCommand = new RelayCommand(() => _owner.Delete(this), () => _owner.CanEditPresets);
        SelectCommand = new RelayCommand(() => _owner.Select(this), () => _owner.CanEditPresets);
        Update(summary, supported);
    }

    /// <summary>Preset id (<see cref="DialPreset.Id"/>).</summary>
    public string Id { get; }

    /// <summary>Preset name.</summary>
    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    /// <summary>Text of the rename box (local until committed).</summary>
    public string EditName
    {
        get => _editName;
        set => SetProperty(ref _editName, value ?? string.Empty);
    }

    /// <summary>The inline rename box is shown.</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        private set
        {
            if (SetProperty(ref _isRenaming, value))
            {
                OnPropertyChanged(nameof(IsNotRenaming));
            }
        }
    }

    /// <summary>Inverse of <see cref="IsRenaming"/> (for visibility bindings).</summary>
    public bool IsNotRenaming => !_isRenaming;

    /// <summary>This preset is the one Next/Previous/Apply selected use.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        private set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Number of included channels.</summary>
    public int IncludedCount
    {
        get => _includedCount;
        private set
        {
            if (SetProperty(ref _includedCount, value))
            {
                ApplyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Per-channel cells in enum order.</summary>
    public IReadOnlyList<PresetValueItem> Values => _values;

    /// <summary>Choices of the slot dropdown ("no slot", "Dial 1".."Dial n").</summary>
    public IReadOnlyList<SlotOption> SlotOptions
    {
        get => _slotOptions;
        private set => SetProperty(ref _slotOptions, value);
    }

    /// <summary>
    /// Slot the preset is assigned to (two-way). A null written by WPF while the option list is replaced is ignored, so
    /// swapping the list never clears an assignment.
    /// </summary>
    public SlotOption SelectedSlot
    {
        get => _selectedSlot;
        set
        {
            if (value == null || ReferenceEquals(value, _selectedSlot))
            {
                return;
            }

            SlotOption previous = _selectedSlot;
            _selectedSlot = value;
            OnPropertyChanged();
            _owner.AssignSlot(this, previous, value);
        }
    }

    /// <summary>Dials the preset now.</summary>
    public RelayCommand ApplyCommand { get; }

    /// <summary>Overwrites the preset with the current values.</summary>
    public RelayCommand CaptureCommand { get; }

    /// <summary>Shows the rename box.</summary>
    public RelayCommand BeginRenameCommand { get; }

    /// <summary>Stores the typed name.</summary>
    public RelayCommand CommitRenameCommand { get; }

    /// <summary>Hides the rename box without renaming.</summary>
    public RelayCommand CancelRenameCommand { get; }

    /// <summary>Deletes the preset (after confirmation).</summary>
    public RelayCommand DeleteCommand { get; }

    /// <summary>Selects the preset for Next/Previous/Apply selected.</summary>
    public RelayCommand SelectCommand { get; }

    /// <summary>Shows <paramref name="summary"/> (same id) without calling the host.</summary>
    internal void Update(PresetSummary summary, bool[] supported)
    {
        Name = summary.Name;
        IncludedCount = summary.IncludedCount;
        for (int i = 0; i < _values.Length; i++)
        {
            bool channelSupported = supported == null || i >= supported.Length || supported[i];
            _values[i].Update(summary.GetValue((DialChannel)i), channelSupported);
        }
    }

    /// <summary>Shows whether the preset is selected (no host call).</summary>
    internal void ShowSelected(bool selected) => IsSelected = selected;

    /// <summary>Replaces the slot choices (slot count changed) and shows <paramref name="slot"/> (no host call).</summary>
    internal void ShowSlot(IReadOnlyList<SlotOption> options, SlotOption slot)
    {
        if (!ReferenceEquals(options, _slotOptions))
        {
            SlotOptions = options;
        }

        if (slot != null && !ReferenceEquals(slot, _selectedSlot))
        {
            _selectedSlot = slot;
            OnPropertyChanged(nameof(SelectedSlot));
        }
    }

    /// <summary>Per-car edit availability changed: re-query the buttons.</summary>
    internal void RefreshCommands()
    {
        ApplyCommand.RaiseCanExecuteChanged();
        CaptureCommand.RaiseCanExecuteChanged();
        BeginRenameCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        SelectCommand.RaiseCanExecuteChanged();
    }

    private void CommitValue(DialChannel channel, double? value)
    {
        int included = 0;
        for (int i = 0; i < _values.Length; i++)
        {
            if (_values[i].IsIncluded && SpeedDialText.TryParseValue(_values[i].ValueText, out _))
            {
                included++;
            }
        }

        // Shown at once so Apply is enabled before the snapshot confirms the edit.
        IncludedCount = included;
        _owner.SetValue(this, channel, value);
    }

    private void BeginRename()
    {
        EditName = _name;
        IsRenaming = true;
    }

    private void CommitRename()
    {
        if (!_isRenaming)
        {
            return;
        }

        IsRenaming = false;
        string name = (_editName ?? string.Empty).Trim();
        if (name.Length > 0 && name != _name)
        {
            _owner.Rename(this, name);

            // Shown at once (cleaned like the host does) so the old name does not flash until the snapshot confirms.
            Name = DialPreset.CleanName(name, _name);
        }
    }

    private void CancelRename() => IsRenaming = false;
}
