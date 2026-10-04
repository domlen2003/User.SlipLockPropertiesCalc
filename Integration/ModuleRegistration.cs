using System;
using System.Windows;
using DivebombLogistics.Framework;

namespace DivebombLogistics.Integration;

/// <summary>
/// A module plus the factory of its settings tab. The shell's module list is an array of these, so adding a module
/// is one line: <c>ModuleRegistration.Create(new XModule(), m =&gt; new XView(new XViewModel(m)))</c>.
/// </summary>
internal sealed class ModuleRegistration
{
    private readonly Func<FrameworkElement> createView;

    private ModuleRegistration(IDlpModule module, Func<FrameworkElement> createView)
    {
        Module = module;
        this.createView = createView;
    }

    /// <summary>The module.</summary>
    public IDlpModule Module { get; }

    /// <summary>Registers <paramref name="module"/> with a typed view factory (the module is its own UI host).</summary>
    public static ModuleRegistration Create<TModule>(TModule module, Func<TModule, FrameworkElement> createView)
        where TModule : class, IDlpModule
    {
        if (module == null)
        {
            throw new ArgumentNullException(nameof(module));
        }

        if (createView == null)
        {
            throw new ArgumentNullException(nameof(createView));
        }

        return new ModuleRegistration(module, () => createView(module));
    }

    /// <summary>Creates the module's tab content (UI thread).</summary>
    public FrameworkElement CreateView() => createView();
}
