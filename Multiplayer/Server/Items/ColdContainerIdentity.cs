using System;
using UnityEngine;
using Multiplayer.Shared.Items;

namespace Multiplayer.Server.Items;

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
