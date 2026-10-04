using System;
using System.Collections.Generic;
using DivebombLogistics.SpeedDial.Model;
using DivebombLogistics.UI;

namespace DivebombLogistics.SpeedDial.UI.ViewModels;

/// <summary>
/// One set/reset pair definition of the Setup expander: name box and channel checkboxes. Edits go to the global
/// settings through the page; <see cref="Sync"/> shows the stored definition without calling back.
/// </summary>
public sealed class PairDefinitionItem : ObservableObject
{
    private readonly Action<int, string> _rename;
    private readonly PairChannelOption[] _channels;
    private string _name = string.Empty;

    /// <param name="index">Pair index (0-based).</param>
    /// <param name="rename">Stores a name (pair index, name).</param>
    /// <param name="setIncluded">Stores a channel choice (pair index, channel, included).</param>
    internal PairDefinitionItem(int index, Action<int, string> rename, Action<int, DialChannel, bool> setIncluded)
    {
        if (setIncluded == null)
        {
            throw new ArgumentNullException(nameof(setIncluded));
        }

        Index = index;
        Caption = SetResetPairDefinition.DefaultName(index);
        _rename = rename ?? throw new ArgumentNullException(nameof(rename));
        _channels = new PairChannelOption[DialChannels.Count];
        for (int i = 0; i < _channels.Length; i++)
        {
            _channels[i] = new PairChannelOption((DialChannel)i, (channel, included) => setIncluded(Index, channel, included));
        }
    }

    /// <summary>Pair index (0-based).</summary>
    public int Index { get; }

    /// <summary>Fixed caption ("Pair 1"): the actions are numbered, whatever the name.</summary>
    public string Caption { get; }

    /// <summary>User name of the pair (two-way; an empty name is not stored, the old one stays).</summary>
    public string Name
    {
        get => _name;
        set
        {
            string text = value ?? string.Empty;
            if (text == _name)
            {
                return;
            }

            _name = text;
            OnPropertyChanged();
            string cleaned = text.Trim();
            if (cleaned.Length > 0)
            {
                _rename(Index, cleaned);
            }
        }
    }

    /// <summary>Channel checkboxes in enum order.</summary>
    public IReadOnlyList<PairChannelOption> Channels => _channels;

    /// <summary>Shows <paramref name="definition"/> (no host call).</summary>
    internal void Sync(SetResetPairDefinition definition)
    {
        if (definition == null)
        {
            return;
        }

        SetProperty(ref _name, definition.Name ?? string.Empty, nameof(Name));
        for (int i = 0; i < _channels.Length; i++)
        {
            _channels[i].Sync(definition.Includes((DialChannel)i));
        }
    }
}
