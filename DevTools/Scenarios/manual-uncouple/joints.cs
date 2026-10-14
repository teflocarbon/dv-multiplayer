// Every joint linking the loco and the wagon's rigidbodies, on this peer.
var a = Trains.Find(ctx.Args["loco"]); var b = Trains.Find(ctx.Args["wagon"]);
var list = new List<string>();
foreach (var (x, y) in new[] { (a, b), (b, a) })
    foreach (var j in x.GetComponentsInChildren<Joint>(true))
        if (j.connectedBody != null && (j.connectedBody == y.rb || j.connectedBody.transform.IsChildOf(y.transform)))
            list.Add($"{x.ID}:{j.GetType().Name} on {j.gameObject.name} -> {y.ID} break={j.breakForce}");
return new { joints = list, rigidA = new[] { a.frontCoupler, a.rearCoupler }.Count(c => c.rigidCJ != null), rigidB = new[] { b.frontCoupler, b.rearCoupler }.Count(c => c.rigidCJ != null) };
