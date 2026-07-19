using UnityEngine;

namespace Multiplayer.Components.Networking.World;

public partial class NetworkedItem
{
    private void OnCollisionEnter(Collision collision)
    {
        if (collision == null || NetId == 0 || NetworkedItemManager.Instance == null)
            return;
        Rigidbody otherBody = collision.rigidbody;
        NetworkedItem other = otherBody == null ? null :
            otherBody.GetComponent<NetworkedItem>() ?? otherBody.GetComponentInParent<NetworkedItem>();
        if (other == null || other == this || other.NetId == 0)
            return;
        NetworkedItemManager.Instance.ObserveTrainItemCollision(this, other, collision);
    }
}
