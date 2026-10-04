using System;
using DivebombLogistics.Core;
using DivebombLogistics.SpeedDial.Model;
using DivebombLogistics.UI;

namespace DivebombLogistics.SpeedDial.UI.ViewModels;

/// <summary>
/// What SpeedDial learned about one channel of the current car (Setup expander): the effect of the Increase role, the
/// step per press and the range seen so far, plus a reset button. Texts are rebuilt only when a value changed.
/// </summary>
public sealed class LearningItem : ObservableObject
{
    private const string RaisesText = "Increase raises";
    private const string LowersText = "Increase lowers";
    private const string AssumedSuffix = " (assumed)";
    private const string RangeSeparator = " to ";
    private const string PercentSuffix = " %";

    private string _directionText = RaisesText + AssumedSuffix;
    private string _stepText = SpeedDialText.Missing;
    private string _rangeText = SpeedDialText.Missing;
    private bool _built;
    private int _direction;
    private bool _confirmed;
    private double _step;
    private double _min;
    private double _max;

    /// <param name="channel">The channel.</param>
    /// <param name="reset">Forgets the channel's learning.</param>
    /// <param name="canReset">A car is loaded.</param>
    internal LearningItem(DialChannel channel, Action<DialChannel> reset, Func<bool> canReset)
    {
        if (reset == null)
        {
            throw new ArgumentNullException(nameof(reset));
        }

        if (canReset == null)
        {
            throw new ArgumentNullException(nameof(canReset));
        }

        Channel = channel;
        Name = DialChannels.DisplayName(channel);
        ResetCommand = new RelayCommand(() => reset(Channel), canReset);
    }

    /// <summary>The channel.</summary>
    public DialChannel Channel { get; }

    /// <summary>Channel display name.</summary>
    public string Name { get; }

    /// <summary>"Increase raises", "Increase lowers (assumed)", ...</summary>
    public string DirectionText
    {
        get => _directionText;
        private set => SetProperty(ref _directionText, value);
    }

    /// <summary>Smallest change per press seen ("-" until known).</summary>
    public string StepText
    {
        get => _stepText;
        private set => SetProperty(ref _stepText, value);
    }

    /// <summary>"0 to 12" (values seen), "-" until known.</summary>
    public string RangeText
    {
        get => _rangeText;
        private set => SetProperty(ref _rangeText, value);
    }

    /// <summary>Forgets the channel's learning for the current car.</summary>
    public RelayCommand ResetCommand { get; }

    /// <summary>Shows the learning arrays of the snapshot for this channel.</summary>
    internal void Update(int direction, bool confirmed, double step, double min, double max)
    {
        if (_built && direction == _direction && confirmed == _confirmed && step.Equals(_step) && min.Equals(_min) && max.Equals(_max))
        {
            return;
        }

        _built = true;
        _direction = direction;
        _confirmed = confirmed;
        _step = step;
        _min = min;
        _max = max;
        string effect = direction == ChannelLearning.IncreaseLowers ? LowersText : RaisesText;
        DirectionText = confirmed ? effect : effect + AssumedSuffix;
        // Same unit as "Values seen" (brake bias in %); EditText keeps a 0.25 % step exact, FormatValue would round it.
        StepText = MathUtil.IsFinite(step) && step > 0.0
            ? SpeedDialText.EditText(Channel, step) + (DialChannels.Kind(Channel) == DialChannelKind.Continuous ? PercentSuffix : string.Empty)
            : SpeedDialText.Missing;
        RangeText = MathUtil.IsFinite(min) && MathUtil.IsFinite(max)
            ? DialChannels.FormatValue(Channel, min) + RangeSeparator + DialChannels.FormatValue(Channel, max)
            : SpeedDialText.Missing;
    }
}
