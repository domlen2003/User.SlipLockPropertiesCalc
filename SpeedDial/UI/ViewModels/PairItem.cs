using System;
using System.Text;
using DivebombLogistics.UI;

namespace DivebombLogistics.SpeedDial.UI.ViewModels;

/// <summary>
/// One set/reset pair row: name, channels, the current car's stored values and the Set now / Reset now buttons.
/// Immutable apart from the button states; the page rebuilds the rows when the pair definitions or stored values
/// change (<see cref="SpeedDialSnapshot.PairsVersion"/>).
/// </summary>
public sealed class PairItem
{
    /// <summary>Shown when the car has no stored values for the pair.</summary>
    public const string NothingStoredText = "nothing stored";

    private const string NoChannelsText = "no channels (choose them under Setup)";
    private const string CapturedPrefix = "stored ";

    private readonly Func<bool> _canEdit;

    /// <param name="summary">The pair as the snapshot shows it.</param>
    /// <param name="set">Runs "Set" of a pair index.</param>
    /// <param name="reset">Runs "Reset" of a pair index.</param>
    /// <param name="canEdit">A car is loaded.</param>
    internal PairItem(PairSummary summary, Action<int> set, Action<int> reset, Func<bool> canEdit)
    {
        if (summary == null)
        {
            throw new ArgumentNullException(nameof(summary));
        }

        if (set == null)
        {
            throw new ArgumentNullException(nameof(set));
        }

        if (reset == null)
        {
            throw new ArgumentNullException(nameof(reset));
        }

        _canEdit = canEdit ?? throw new ArgumentNullException(nameof(canEdit));
        Index = summary.Index;
        Name = summary.Name;
        ChannelsText = SpeedDialText.ChannelList(summary.Channels, NoChannelsText);
        HasStoredValues = summary.HasStoredValues;
        StoredText = HasStoredValues ? BuildStoredText(summary) : NothingStoredText;
        CapturedText = HasStoredValues ? CapturedPrefix + SpeedDialText.LocalTime(summary.CapturedUtc) : string.Empty;
        HasChannels = summary.Channels.Count > 0;
        SetCommand = new RelayCommand(() => set(Index), () => _canEdit() && HasChannels);
        ResetCommand = new RelayCommand(() => reset(Index), () => _canEdit() && HasStoredValues);
    }

    /// <summary>Pair index (0-based).</summary>
    public int Index { get; }

    /// <summary>Pair name.</summary>
    public string Name { get; }

    /// <summary>"TC · ABS · BB".</summary>
    public string ChannelsText { get; }

    /// <summary>The pair has at least one channel.</summary>
    public bool HasChannels { get; }

    /// <summary>The car has stored values for the pair.</summary>
    public bool HasStoredValues { get; }

    /// <summary>"TC 3 · ABS 5" or <see cref="NothingStoredText"/>.</summary>
    public string StoredText { get; }

    /// <summary>"stored 14:32:05" (local time); empty when nothing is stored.</summary>
    public string CapturedText { get; }

    /// <summary>Stores the current values of the pair's channels.</summary>
    public RelayCommand SetCommand { get; }

    /// <summary>Dials back to the stored values.</summary>
    public RelayCommand ResetCommand { get; }

    /// <summary>Per-car edit availability changed: re-query the buttons.</summary>
    internal void RefreshCommands()
    {
        SetCommand.RaiseCanExecuteChanged();
        ResetCommand.RaiseCanExecuteChanged();
    }

    private static string BuildStoredText(PairSummary summary)
    {
        var text = new StringBuilder();
        for (int i = 0; i < summary.Channels.Count; i++)
        {
            if (i > 0)
            {
                text.Append(SpeedDialText.Separator);
            }

            DialChannel channel = summary.Channels[i];
            text.Append(SpeedDialText.Chip(channel, summary.GetStoredValue(channel)));
        }

        return text.ToString();
    }
}
