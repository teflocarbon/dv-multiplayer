using System;
using UnityEngine;

namespace Multiplayer.Server.Items.SaveLoad;

[DisallowMultipleComponent]
internal sealed class PersistentWorldItemIdentity : MonoBehaviour
{
    [SerializeField] private string value;

    public Guid Value
    {
        get => Guid.TryParse(value, out Guid id) ? id : Guid.Empty;
        set => this.value = value == Guid.Empty ? string.Empty : value.ToString("D");
    }

    [SerializeField] private string owner;

    /// <summary>
    /// The owner saved with an item the mod's own records don't keep (an installed gadget,
    /// <see cref="PersistentWorldItemIdentityPatch.OwnerKey"/>), until its record takes it.
    /// </summary>
    public Guid Owner
    {
        get => Guid.TryParse(owner, out Guid id) ? id : Guid.Empty;
        set => owner = value == Guid.Empty ? string.Empty : value.ToString("D");
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
