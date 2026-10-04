using DivebombLogistics.UI;

namespace DivebombLogistics.SpeedDial.UI.ViewModels;

/// <summary>One SimHub action of the "Button bindings" list, shown with SimHub's <c>ControlsEditor</c>.</summary>
public sealed class BindingItem : ObservableObject
{
    private string _friendlyName;

    /// <param name="actionName">Full SimHub action name (<c>DLP.SpeedDial.Dial1</c>).</param>
    /// <param name="friendlyName">Caption shown by the editor.</param>
    public BindingItem(string actionName, string friendlyName)
    {
        ActionName = actionName;
        _friendlyName = friendlyName;
    }

    /// <summary>Full SimHub action name (<c>DLP.SpeedDial.Dial1</c>); never changes for an item.</summary>
    public string ActionName { get; }

    /// <summary>Caption shown by the editor (follows pair renames).</summary>
    public string FriendlyName
    {
        get => _friendlyName;
        internal set => SetProperty(ref _friendlyName, value);
    }
}
