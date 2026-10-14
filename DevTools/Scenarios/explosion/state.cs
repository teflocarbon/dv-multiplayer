// Either peer: exploded and derailed state and position of each car. Args: cars (comma-separated).
return ctx.Args["cars"].Split(',').Select(id => Trains.Find(id)).Select(c => new { id = c.ID, exploded = c.isExploded, derailed = c.derailed, abs = Dev.Abs(c.transform.position) }).ToList();
