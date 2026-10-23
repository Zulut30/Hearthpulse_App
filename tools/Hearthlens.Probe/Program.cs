using Hearthlens.Core.Memory;
using Hearthlens.Core.Power;

// Developer probe.
//   heroes <Power.log> [maxLines]   dump Battlegrounds hero entities at a point in the log
//   mem                             read live values from a running Hearthstone client
//   class <Name> [field...]         dump a class's fields / follow a static path
return args.FirstOrDefault() switch
{
    "heroes" => Heroes(args[1], args.Length > 2 ? int.Parse(args[2]) : int.MaxValue),
    "mem" => Memory(),
    "hover" => Hover(args.Length > 1 ? int.Parse(args[1]) : 30),
    "class" => ClassDump(args[1], args.Skip(2).ToArray()),
    "upgrade" => Upgrade(),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("usage: heroes <Power.log> [maxLines] | mem | upgrade | class <Name> [path...]");
    return 1;
}

static int Heroes(string path, int maxLines)
{
    var parser = new PowerParser(DateOnly.FromDateTime(DateTime.Today));
    var count = 0;
    foreach (var line in File.ReadLines(path))
    {
        if (++count > maxLines) break;
        parser.Feed(line);
    }

    parser.Flush();
    var game = parser.Game!;
    Console.WriteLine($"{game.GameType} turn={game.Turn} local={game.LocalPlayerId} opp={game.OpponentPlayerId}");
    foreach (var e in game.Entities.Values.Where(e => e.IsHero).OrderBy(e => e.GetInt(GameTag.PlayerId)).ThenBy(e => e.Id))
    {
        Console.WriteLine($"pid={e.GetInt(GameTag.PlayerId),-2} #{e.Id,-5} {e.Zone,-15} c={e.Controller,-2} {e.CardId,-24} hp={e.GetInt(GameTag.Health)} dmg={e.GetInt(GameTag.Damage)} arm={e.GetInt(GameTag.Armor)} place={e.GetInt(GameTag.PlayerLeaderboardPlace)} tier={e.GetInt(GameTag.PlayerTechLevel)} created=T{e.CreatedTurn}");
    }

    return 0;
}

static int Memory()
{
    using var memory = new HearthstoneMemory();
    if (!memory.EnsureAttached())
    {
        Console.WriteLine("Not attached: " + memory.Status);
        return 2;
    }

    Console.WriteLine(memory.Status);
    Console.WriteLine($"BG rating: {memory.GetBattlegroundsRating()}");
    Console.WriteLine($"Post-game rating: {memory.GetPostGameBattlegroundsRating()}");
    var races = memory.GetBattlegroundsRaces();
    Console.WriteLine($"BG races: {(races is null ? "n/a" : string.Join(",", races))}");
    Console.WriteLine($"BG upgrade cost: {memory.GetTavernUpgradeCost()?.ToString() ?? "n/a"}");
    Console.WriteLine($"BG buttons: {memory.GetBattlegroundsButtonCosts()?.ToString() ?? "n/a"}");
    var players = memory.GetMatchPlayers();
    Console.WriteLine($"Match: local={players?.Local} opponent={players?.Opponent}");
    return 0;
}

static int Hover(int seconds)
{
    using var memory = new HearthstoneMemory();
    if (!memory.EnsureAttached())
    {
        Console.WriteLine("Not attached: " + memory.Status);
        return 2;
    }

    string? last = "";
    var end = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < end)
    {
        var hero = memory.GetHoveredLeaderboardHero();
        if (hero != last)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} hovered={hero ?? "-"}");
            last = hero;
        }

        Thread.Sleep(100);
    }

    return 0;
}

static int Upgrade()
{
    using var memory = new HearthstoneMemory();
    if (!memory.EnsureAttached() || memory.Runtime is not { } runtime)
    {
        Console.WriteLine("Not attached: " + memory.Status);
        return 2;
    }

    var zoneMgr = runtime.FindClass("ZoneMgr")?.GetStatic("s_instance") ?? default;
    if (zoneMgr.IsNull)
    {
        Console.WriteLine("ZoneMgr not available");
        return 3;
    }

    foreach (var zone in zoneMgr["m_zones"].ListItems())
    {
        Console.WriteLine($"{zone.ClassName} slot={zone.Int("m_ButtonSlot")} side={zone.Int("m_Side")}");
        foreach (var card in zone["m_cards"].ListItems())
        {
            var entity = card["m_entity"];
            Console.WriteLine($"  card={entity.String("m_cardIdInternal")} cost={entity.Int("m_realTimeCost")} tech={entity.Int("m_realTimePlayerTechLevel")}");
        }
    }

    return 0;
}

static int ClassDump(string className, string[] path)
{
    using var memory = new HearthstoneMemory();
    if (!memory.EnsureAttached() || memory.Runtime is not { } runtime)
    {
        Console.WriteLine("Not attached: " + memory.Status);
        return 2;
    }

    if (className == "netcache")
    {
        foreach (var value in HearthstoneMemory.FindService(runtime, "NetCache")["m_netCache"].MapValues())
        {
            Console.WriteLine(value.ClassName);
        }

        return 0;
    }

    var cls = runtime.FindClass(className);
    if (cls is null)
    {
        foreach (var name in runtime.ClassNames.Where(n => n.Contains(className, StringComparison.OrdinalIgnoreCase)).Take(40))
        {
            Console.WriteLine("  " + name);
        }

        return 3;
    }

    foreach (var field in cls.AllFields())
    {
        Console.WriteLine("  " + field);
    }

    if (path.Length == 0)
    {
        return 0;
    }

    var obj = cls.GetStatic(path[0]);
    foreach (var segment in path.Skip(1))
    {
        obj = obj[segment];
    }

    Console.WriteLine($"=> {obj}");
    foreach (var field in obj.Class?.AllFields() ?? Enumerable.Empty<MonoField>())
    {
        if (field.IsStatic) continue;
        Console.WriteLine($"  {field.Name,-40} int={obj.Int(field.Name),-12} ptr=0x{obj.Object(field.Name).Address:X}");
    }

    return 0;
}
