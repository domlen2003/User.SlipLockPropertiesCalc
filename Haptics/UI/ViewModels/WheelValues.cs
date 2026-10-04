using System.Collections.Generic;
using DivebombLogistics.Core;
using DivebombLogistics.UI.Controls;

namespace DivebombLogistics.Haptics.UI.ViewModels;

/// <summary>
/// Four per-wheel values (FL, FR, RL, RR — the <see cref="Wheels"/> order) displayed by a <see cref="WheelQuad"/>.
/// The item instances are fixed; only their values change, so bindings stay attached.
/// </summary>
public sealed class WheelValues
{
    private readonly WheelValue[] _items = new WheelValue[Wheels.Count];

    public WheelValues()
    {
        for (int i = 0; i < Wheels.Count; i++)
        {
            _items[i] = new WheelValue(Wheels.ShortNames[i]);
        }
    }

    /// <summary>FL, FR, RL, RR (a 2×2 grid shows them in car layout).</summary>
    public IReadOnlyList<WheelValue> Items => _items;

    /// <summary>Copies four values (FL, FR, RL, RR) into the items.</summary>
    public void Update(double[] values)
    {
        for (int i = 0; i < Wheels.Count; i++)
        {
            _items[i].Value = values[i];
        }
    }
}
