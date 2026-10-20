// A service station's cash register with modules (not a shop's). Args: op, and (except for find)
// register (NetId).
//   find (host; args x, z): the module register nearest that point (abs), and where it is.
//   seed (host; arg amount): cash deposited in the register, as a player's deposit leaves it: set on
//     the host and sent to the players (SetFunds).
//   deposit (host; arg amount): the host's player pays cash in, as DV does when money touches the
//     register (taken from the wallet, then CashRegisterBase.AddCash), and waits for the request to
//     finish; busyFor is how long the register stayed busy waiting for the server's answer.
//   buy (host): the host's player presses Buy.
//   state (either): deposited cash, units in the basket, whether a request is waiting, the wallet.
string op = ctx.Args["op"];
if (op == "find")
{
    var p = new Vector3(float.Parse(ctx.Args["x"]), 0, float.Parse(ctx.Args["z"]));
    var nearest = NetworkedCashRegisterWithModules.All.Where(r => r != null && r.CashRegister != null && !r.IsShopRegister)
        .OrderBy(r => { var a = Dev.Abs(r.transform.position); return new Vector2(a.x - p.x, a.z - p.z).magnitude; }).First();
    var abs = Dev.Abs(nearest.transform.position);
    ctx.Result = new { register = nearest.NetId, name = nearest.transform.parent?.parent?.name + " - " + nearest.transform.parent?.name, x = abs.x, z = abs.z };
    yield break;
}

NetworkedCashRegisterWithModules.Get(ushort.Parse(ctx.Args["register"]), out NetworkedCashRegisterWithModules reg);
var register = reg.CashRegister;
var wallet = DV.InventorySystem.Inventory.Instance;
float busyFor = 0f;
if (op == "seed")
{
    double amount = double.Parse(ctx.Args["amount"]);
    register.SetCash(amount);
    Multiplayer.Server.Session.NetworkServer.Instance.SendCashRegisterAction(new CommonCashRegisterWithModulesActionPacket
    {
        NetId = reg.NetId,
        Action = CashRegisterAction.SetFunds,
        Amount = amount
    });
    yield return new WaitForSecondsRealtime(0.5f);
}
else if (op == "deposit")
{
    double amount = double.Parse(ctx.Args["amount"]);
    wallet.RemoveMoney(amount);
    register.AddCash(amount);
    float start = Time.realtimeSinceStartup;
    yield return null;
    while (reg.IsBusy && Time.realtimeSinceStartup - start < 5f)
        yield return null;
    busyFor = Time.realtimeSinceStartup - start;
}
else if (op == "buy")
{
    register.OnBuyPressed();
    yield return new WaitForSecondsRealtime(0.5f);
}
ctx.Result = new
{
    deposited = register.DepositedCash,
    units = register.TotalUnitsInBasket(),
    busy = reg.IsBusy,
    busyFor,
    wallet = wallet.PlayerMoney,
};
