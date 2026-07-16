using System;
using UnityEngine;

namespace Multiplayer.Components.Networking.World.Containers;

public sealed class ColdContainerIdentity : MonoBehaviour
{
    [SerializeField] private string persistentId = string.Empty;

    public Guid PersistentId
    {
        get
        {
            if (!Guid.TryParse(persistentId, out Guid value) || value == Guid.Empty)
            {
                value = Guid.NewGuid();
                persistentId = value.ToString("D");
            }
            return value;
        }
    }

    public void Restore(Guid value)
    {
        if (value != Guid.Empty) persistentId = value.ToString("D");
    }
}
