using System;
using UnityEngine;

namespace Multiplayer.Server.Items.SaveLoad;

[DisallowMultipleComponent]
internal sealed class ItemIdentity : MonoBehaviour
{
    [SerializeField] private string value;

    public Guid Value
    {
        get => Guid.TryParse(value, out Guid id) ? id : Guid.Empty;
        set => this.value = value == Guid.Empty ? string.Empty : value.ToString("D");
    }

    /// <summary>The id this item's identity was copied from, when the server gave it a new one (OI-ID-18). Not saved.</summary>
    [NonSerialized] public Guid CopiedFrom;

    public Guid Ensure()
    {
        Guid id = Value;
        if (id == Guid.Empty)
        {
            id = Guid.NewGuid();
            Value = id;
        }
        return id;
    }
}
