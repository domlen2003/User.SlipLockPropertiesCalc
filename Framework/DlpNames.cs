namespace DivebombLogistics.Framework;

/// <summary>
/// Names that tie DLP to SimHub and to the file system. Changing any of them breaks user setups (ShakeIT profiles,
/// dashboards, Control Mapper bindings, data folders), so they are defined once here.
/// </summary>
internal static class DlpNames
{
    /// <summary>
    /// Prefix SimHub puts in front of every property and action of this plugin. SimHub derives it from the
    /// <b>runtime</b> name of the plugin class (<c>DivebombLogistics.DLP</c>), so the two must stay in sync.
    /// </summary>
    public const string PropertyPrefix = "DLP.";

    /// <summary>Folder below <c>&lt;SimHub&gt;\PluginsData</c> that holds every DLP file.</summary>
    public const string DataFolderName = "DLP";

    /// <summary>Full SimHub name of a property or action registered as <paramref name="name"/>.</summary>
    public static string FullName(string name) => PropertyPrefix + name;
}
