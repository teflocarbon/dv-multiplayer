// Either peer: this game's comms radio damage mode used on a car, as a radio click does:
// mode=damage (level 1-10) or derail. Returns the car's health and derailed state here, before
// and after.
var car = Trains.Find(ctx.Args["car"]);
var radio = Resources.FindObjectsOfTypeAll<CommsRadioDamage>().FirstOrDefault(r => r.gameObject.scene.IsValid());
if (radio == null) { ctx.Result = "no comms radio damage mode in this game"; yield break; }
float before = car.CarDamage.currentHealth;
radio.opMode = ctx.Args["mode"] == "derail" ? CommsRadioDamage.OperationMode.Derail : CommsRadioDamage.OperationMode.Damage;
radio.damageLevel = int.Parse(ctx.Args.TryGetValue("level", out var l) ? l : "5");
radio.pointedCar = car;
radio.OnUse();
yield return new WaitForSecondsRealtime(3f);
ctx.Result = new { before = Math.Round(before), after = Math.Round(car.CarDamage.currentHealth), max = Math.Round(car.CarDamage.maxHealth), derailed = car.derailed };
