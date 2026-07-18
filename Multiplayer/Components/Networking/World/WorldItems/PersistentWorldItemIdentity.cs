using System;
using UnityEngine;

namespace Multiplayer.Components.Networking.World.WorldItems;

[DisallowMultipleComponent]
internal sealed class PersistentWorldItemIdentity : MonoBehaviour
{
    [SerializeField] private string value;

    public Guid Value
    {
        get => Guid.TryParse(value, out Guid id) ? id : Guid.Empty;
        set => this.value = value == Guid.Empty ? string.Empty : value.ToString("D");
    }

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
