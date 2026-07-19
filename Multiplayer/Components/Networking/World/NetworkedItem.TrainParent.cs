using DV.CabControls;
using Multiplayer.Components.Networking.Train;

namespace Multiplayer.Components.Networking.World;

public partial class NetworkedItem
{
    /// <summary>
    /// Resolves the train physically receiving this item's forces. A loaded DV interior can live
    /// in a detached hierarchy, so the ordinary component-parent lookup is only the fast path.
    /// </summary>
    internal bool TryGetPhysicalTrainParent(out TrainCar trainCar)
    {
        trainCar = GetComponentInParent<TrainCar>();
        if (trainCar != null)
            return true;

        ItemReparentingBase reparenting = GetComponent<ItemReparentingBase>();
        if (NetworkedTrainCar.TryGetFromInteriorHierarchy(reparenting?.CurrentParent, out trainCar))
            return true;

        return NetworkedTrainCar.TryGetFromInteriorHierarchy(transform, out trainCar);
    }
}
