using System.Windows;

namespace DivebombLogistics.UI;

/// <summary>
/// Styles, converters and templates shared by the DLP tabs. A compiled dictionary (x:Class) so the views merge it by
/// instantiation instead of a pack URI, which a plugin assembly loaded by SimHub cannot always resolve.
/// </summary>
public partial class SharedStyles : ResourceDictionary
{
    /// <summary>Loads the dictionary's content.</summary>
    public SharedStyles()
    {
        InitializeComponent();
    }
}
