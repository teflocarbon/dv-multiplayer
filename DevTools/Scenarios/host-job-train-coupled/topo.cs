// Either peer: how the given cars are coupled. For each car (by NetId), the NetIds of the cars on its
// front and rear couplers (0: none), and in how many trainsets the cars are. Args: cars (NetIds,
// comma-separated).
var ids = ctx.Args["cars"].Split(',').Select(ushort.Parse).OrderBy(id => id).ToList();
string Neighbour(Coupler c) =>
    c != null && c.coupledTo != null && c.coupledTo.train != null && NetworkedTrainCar.TryGetNetId(c.coupledTo.train, out ushort n) ? n.ToString() : "0";
var parts = new List<string>();
var sets = new HashSet<Trainset>();
int missing = 0, couplerEnds = 0;
foreach (ushort id in ids)
{
    if (!NetworkedTrainCar.TryGet(id, out TrainCar car) || car == null)
    {
        missing++;
        parts.Add(id + ":missing");
        continue;
    }
    if (car.trainset != null)
        sets.Add(car.trainset);
    string front = Neighbour(car.frontCoupler), rear = Neighbour(car.rearCoupler);
    couplerEnds += (front != "0" ? 1 : 0) + (rear != "0" ? 1 : 0);
    parts.Add($"{id}:{front}/{rear}");
}
return new { topo = string.Join(" ", parts), sets = sets.Count, couplings = couplerEnds / 2, missing };
