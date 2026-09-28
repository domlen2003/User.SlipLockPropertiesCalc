namespace User.SlipLockPropertiesCalc.UI.ViewModels;

/// <summary>
/// A combo-box choice: a value plus its display text. The text is observable because some entries describe live
/// state (e.g. "Auto (GT)" follows the auto-detected class preset).
/// </summary>
public sealed class OptionItem<T> : ObservableObject
{
    private string _text;

    public OptionItem(T value, string text)
    {
        Value = value;
        _text = text;
    }

    public T Value { get; }

    public string Text
    {
        get => _text;
        internal set => SetProperty(ref _text, value);
    }

    /// <inheritdoc />
    public override string ToString() => _text;
}
