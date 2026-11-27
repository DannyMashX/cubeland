using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cubeland;

// The staircases (WorldGen.Staircases grows them, in lands 5 and 6; an Earthlike world's: EarthCol.FindStairs, Main.FindEarthStairs). Events on reaching a top step (while WorldGen.EerieOn,
// on a visit whose roll came up: see Main.Eerie), and no music near a standing one (always).
public partial class Main
{
	const int StairOdds = 500;      // an event 1 time in this many on reaching a top step
	int stairOn = -1;               // the staircase whose top step you're on (-1: none)
	bool stairFall;                 // (the staircase just went from under you: no fall damage for that drop)

	List<WorldGen.StairSpot> StairList => world.Gen.StairSpots;
	static (int x, int y, int z) StairTopCell(WorldGen.StairSpot s)
	{
		var d = WorldGen.StairDirs[s.Dir];
		return (s.X + d[0] * (s.N - 1), s.Y + s.N - 1, s.Z + d[1] * (s.N - 1));
	}
	// is this staircase still standing (its top step as it grew)?
	bool StairStanding(WorldGen.StairSpot s)
	{
		var (x, y, z) = StairTopCell(s);
		if (!Blocks.IsPieces(world.GetBlock(x, y, z)) || !world.PieceMap.TryGetValue(World.Idx(x, y, z), out var a)) return false;
		for (int b = 0; b < 8; b++) if (a[b] != ((WorldGen.StairMasks[s.Dir] & (1 << b)) != 0 ? s.Mat : 0)) return false;
		return true;
	}

	// no music near a staircase (within 32 blocks across, 24 up or down)
	float stairNearCheck; bool stairNear;
	bool NearStair()
	{
		if (world == null || menuOpen || StairList.Count == 0) return false;
		if ((stairNearCheck -= (float)GetProcessDeltaTime()) > 0) return stairNear;
		stairNearCheck = 0.5f;
		stairNear = StairList.Any(s => Math.Abs(s.X - player.Pos.X) < 32 && Math.Abs(s.Z - player.Pos.Z) < 32 && Math.Abs(s.Y - player.Pos.Y) < 24 && StairStanding(s));
		return stairNear;
	}
	// which staircase's top step you're standing on (-1: none)
	int StairUnder()
	{
		for (int k = 0; k < StairList.Count; k++)
		{
			var (x, y, z) = StairTopCell(StairList[k]);
			if (Mathf.FloorToInt(player.Pos.X) != x || Mathf.FloorToInt(player.Pos.Z) != z || player.Pos.Y < y + 0.45f || player.Pos.Y > y + 1.1f) continue;
			return StairStanding(StairList[k]) ? k : -1;
		}
		return -1;
	}
	void UpdateStairs()
	{
		if (world == null || !WorldGen.EerieOn || StairList.Count == 0 || dead) { stairOn = -1; return; }   // (see WorldGen.EerieReady)
		StairSearchDone();   // (gen 4: a moved staircase's new spot, once found)
		if (!stairTested && HasArg("--stairtest")) StairTest();
		int on = player.OnGround ? StairUnder() : -1;
		if (on == stairOn) return;
		stairOn = on;
		if (on < 0) return;
		// (test: `--stairevent=N` makes event N happen every time, 0-4)
		int force = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--stairevent=")) is { } fa ? int.Parse(fa[13..]) : -1;
		if (force < 0 && !StairChance()) return;
		StairEvent(force >= 0 ? force : rnd.Next(5), StairList[on]);
	}

	// (test: `--stairtest` makes each thing happen once, on a staircase in a Biomes world, and checks it worked)
	bool stairTested;
	void StairTest()
	{
		stairTested = true;
		void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
		Check($"this world is land {current.Land}, with staircases{(world.Gen.Os ? " (an older world given them)" : "")}", world.Gen.HasStairs);
		var s = StairList.First(o => o.Placed);
		if (world.Lazy) { GrowNow(s.X >> 4, s.Z >> 4, 1); GD.Print($"STAIRTEST the first staircase: {s.X},{s.Y},{s.Z},{s.Dir},{s.N},{s.Mat}"); }   // (gen 4: its land grown first)
		int standing = StairList.Count(StairStanding);
		Check($"staircases stand in the world ({standing} of {StairList.Count} spots)", standing >= 1 && StairStanding(s));
		var (x, y, z) = StairTopCell(s);
		player.Pos = new Vector3(x + 0.5f, y + 1.01f, z + 0.5f); player.Flying = false;
		Check("standing on the top step is found", StairUnder() == StairList.IndexOf(s));
		stairNearCheck = 0; bool near = NearStair();
		player.Pos = new Vector3(x + 60.5f, y + 1f, z + 0.5f); stairNearCheck = 0; bool farOff = NearStair();
		Check("no music near a staircase (and music away from them)", near && !farOff);
		player.Pos = new Vector3(x + 0.5f, y - 2f, z + 0.5f);
		Check("not when you're lower down", StairUnder() == -1);
		float t0 = sky.TimeOfDay; StairEvent(0, s);
		Check("the time of day jumps", sky.TimeOfDay != t0);
		player.Pos = new Vector3(x + 0.5f, y + 1.01f, z + 0.5f); var p0 = player.Pos; StairEvent(1, s);
		int px = Mathf.FloorToInt(player.Pos.X), py = Mathf.FloorToInt(player.Pos.Y), pz = Mathf.FloorToInt(player.Pos.Z);
		Check($"you're sent somewhere else, onto dry ground (from {p0.X:0},{p0.Y:0},{p0.Z:0} to {player.Pos.X:0.0},{player.Pos.Y:0.00},{player.Pos.Z:0.0}: in {world.GetBlock(px, py, pz)}, on {world.GetBlock(px, py - 1, pz)})", (player.Pos - p0).Length() > 1 && (world.GetBlock(px, py, pz) == 0 || world.Lazy && EarthRoomAt(px, py, pz)) && world.Solid(px, py - 1, pz) && !Blocks.IsWater(world.GetBlock(px, py - 1, pz)));
		health = 3; gloom = 0; StairEvent(3, s);
		Check("it hurts, but never kills", health >= 2 && !dead);
		health = MaxHealth; StairEvent(3, s);
		Check("  (and does hurt when you're well)", !inv.Survival || health < MaxHealth);
		for (int i = 0; i < Inventory.Armor0; i++) inv.SetSlot(i, 0, 0);
		inv.SetSlot(4, Blocks.Planks, 5); int n4 = Math.Max(1, inv.Count[4]);   // (5, or 1 in creative)
		int kept = drops.All.Count(d => d.Keep); StairEvent(4, s);
		Check($"an item leaves your inventory and lies somewhere else (slot {inv.Id[4]}, {drops.All.Count(d => d.Keep && d.Id == Blocks.Planks)} kept Planks, {kept} kept before)", inv.Id[4] == 0 && drops.All.Count(d => d.Keep && d.Id == Blocks.Planks) == kept + n4);
		// (anywhere in the world on dry ground, as RandomDryGround picks it: no distance from you is promised)
		var lies = drops.All.Where(d => d.Keep && d.Id == Blocks.Planks).Select(d => d.P).FirstOrDefault();
		int lx = Mathf.FloorToInt(lies.X), ly = Mathf.FloorToInt(lies.Y), lz = Mathf.FloorToInt(lies.Z);
		Check($"  (on dry ground, and it stays: at {lies.X:0},{lies.Y:0},{lies.Z:0})", (world.GetBlock(lx, ly, lz) == 0 || world.Lazy && EarthRoomAt(lx, ly, lz)) && world.Solid(lx, ly - 1, lz) && !Blocks.IsWater(world.GetBlock(lx, ly - 1, lz)));
		int extra0 = extraStairs.Count;
		StairEvent(2, s);   // (gen 4: its new spot looked for away from the game, looking again if need be)
		while (stairSearch != null) { try { stairSearch.Wait(); } catch (AggregateException) { } StairSearchDone(); }
		Check("the staircase is gone", !StairStanding(s) && world.GetBlock(x, y, z) == 0);
		Check("and stands somewhere else", StairList.Count(StairStanding) == standing && (!world.Lazy || extraStairs.Count == extra0 + 1 && StairStanding(extraStairs[^1])));   // (gen 4: a new one, built)
	}

	void StairEvent(int which, WorldGen.StairSpot s)
	{
		switch (which)
		{
			case 0:   // a random time of day
				float t = (float)rnd.NextDouble();
				if (netClient) hostConn?.Send(new Dictionary<string, object> { ["t"] = "stime", ["time"] = t });
				else sky.TimeOfDay = t;
				break;
			case 1:   // the player moves to random dry ground
				if (RandomDryGround() is { } to) { player.Pos = to; player.Vel = Vector3.Zero; player.Flying = false; stairOn = -1; }
				break;
			case 2:   // the staircase moves to another spot
				StairMove(s);
				break;
			case 3:   // damage (never fatal)
				int most = health - gloom - 2;
				if (most >= 1) HurtPlayer(Math.Min(most, 2 + rnd.Next(7)));
				break;
			case 4:   // one inventory stack moves to random ground
				StairTakeItem();
				break;
		}
	}

	// a random spot on dry land (not water, ice or treetops), standing on top of it
	Vector3? RandomDryGround()
	{
		if (world.Lazy) return EarthDryGround();
		for (int tries = 0; tries < 400; tries++)
		{
			int x = 16 + rnd.Next(World.WX - 32), z = 16 + rnd.Next(World.WZ - 32), y = StairGround(z * World.WX + x);
			int b = world.GetBlock(x, y, z);
			if (y <= world.SeaLevel || b == 0 || Blocks.IsWater(b) || b == Blocks.Ice || Blocks.B[b].NoCollide) continue;
			if (world.GetBlock(x, y + 1, z) != 0 || world.GetBlock(x, y + 2, z) != 0) continue;
			return new Vector3(x + 0.5f, y + 1.01f, z + 0.5f);
		}
		return null;
	}
	// the ground in a column, under any trees or plants
	int StairGround(int c)
	{
		int x = c % World.WX, z = c / World.WX, y = Math.Min(World.WY - 1, world.Heights[c] + 8);
		while (y > 0)
		{
			int b = world.GetBlock(x, y, z);
			if (b == 0 || Blocks.IsLeaves(b) || NeededTool(b) == "axe" || Blocks.Get(b)?.Shape is "plant" or "flat") y--; else break;
		}
		return y;
	}

	void StairMove(WorldGen.StairSpot s)
	{
		// take it all away: the steps, and the base under them down to the ground
		var d = WorldGen.StairDirs[s.Dir];
		for (int k = 0; k < s.N; k++)
		{
			int cx = s.X + d[0] * k, cz = s.Z + d[1] * k;
			if (Blocks.IsPieces(world.GetBlock(cx, s.Y + k, cz))) world.SetBlock(cx, s.Y + k, cz, 0);
			for (int y = s.Y + k - 1; y >= s.Y - 1; y--) if (world.GetBlock(cx, y, cz) == s.Mat) world.SetBlock(cx, y, cz, 0);
		}
		stairFall = true; stairOn = -1;
		// (never within 2 chunks of a chunk with anything changed in it now)
		var changed = new HashSet<int>(world.Edits.Keys.Select(i => (int)(i / World.WX % World.WZ / World.CS * (World.WX / World.CS) + i % World.WX / World.CS)));
		// and stand it up again in another high forest, somewhere it still fits: one of the spots the world kept for it...
		foreach (var o in StairList.Where(o => o != s && Math.Abs(o.X - s.X) + Math.Abs(o.Z - s.Z) > World.WX / 10).OrderBy(_ => rnd.Next()))
		{
			if (StairStanding(o) || WorldGen.NearChunks(changed, o.X, o.Z) || !WorldGen.StairFits(o, StairGround, world.GetBlock, world.Lazy ? EarthCol.StairTop : null)) continue;
			world.Gen.BuildStair(o, (x, y, z, id) => world.SetBlock(x, y, z, id), StairGround);
			return;
		}
		if (world.Lazy) { EarthStairElsewhere(s, changed); return; }
		// ...or, if none fit any more, anywhere else in a forest high above the sea (kept in the save as an extra spot)
		for (int tries = 0; tries < 4000; tries++)
		{
			int x = 16 + rnd.Next(World.WX - 32), z = 16 + rnd.Next(World.WZ - 32);
			if (Math.Abs(x - s.X) + Math.Abs(z - s.Z) <= World.WX / 10 || WorldGen.NearChunks(changed, x, z) || !world.Gen.StairForest(x, z)) continue;
			int g = StairGround(z * World.WX + x);
			if (g < world.SeaLevel + WorldGen.StairMinUp) continue;
			var o = new WorldGen.StairSpot { X = x, Y = g + 1, Z = z, N = 5 + rnd.Next(4), Mat = world.Gen.StairMat(x, z), Placed = true };
			int d0 = rnd.Next(4); bool fits = false;
			for (int t = 0; t < 4 && !fits; t++) { o.Dir = (d0 + t) % 4; fits = WorldGen.StairFits(o, StairGround, world.GetBlock); }
			if (!fits) continue;
			world.Gen.BuildStair(o, (bx, by, bz, id) => world.SetBlock(bx, by, bz, id), StairGround);
			StairList.Add(o); extraStairs.Add(o);
			return;
		}
	}
	// (gen 4) a random spot on dry land, the plan picking it: a cell of woods, plains, meadow, taiga, savanna or dry forest, not sea,
	// lake or river, its ground over the sea; its columns grown now, then the ground there as above (a plant there is fine)
	Vector3? EarthDryGround()
	{
		var P = world.Plan;
		for (int tries = 0, grown = 0; tries < 4000 && grown < 16; tries++)
		{
			int i = rnd.Next(P.N * P.N), b = P.Biome[i], x = i % P.N * P.Cell + P.Cell / 2, z = i / P.N * P.Cell + P.Cell / 2;
			if (b is not (EarthPlan.Woods or EarthPlan.Plains or EarthPlan.Meadow or EarthPlan.Taiga or EarthPlan.Savanna or EarthPlan.DryForest)) continue;
			if (P.Sea[i] || P.Lake[i] || P.River(i) || x < 16 || z < 16 || x >= World.WX - 16 || z >= World.WZ - 16 || P.GroundAt(x, z) <= P.Room + 1) continue;
			GrowNow(x >> 4, z >> 4, 1); grown++;
			int y = StairGround(z * World.WX + x), t = world.GetBlock(x, y, z);
			if (y <= world.SeaLevel || t == 0 || Blocks.IsWater(t) || t == Blocks.Ice || Blocks.B[t].NoCollide) continue;
			if (!EarthRoomAt(x, y + 1, z) || !EarthRoomAt(x, y + 2, z)) continue;
			return new Vector3(x + 0.5f, y + 1.01f, z + 0.5f);
		}
		return null;
	}
	bool EarthRoomAt(int x, int y, int z) { int b = world.GetBlock(x, y, z); return b == 0 || Blocks.B[b].NoCollide && !Blocks.IsWater(b); }
	// (gen 4) the staircase stood up again elsewhere: its spot looked for on a drawer of its own, away from the game
	// (EarthCol.StairElsewhere: as the world's staircases are found, on the land drawn bare; never near a chunk with changes now);
	// then, once found, its land grown and the staircase built there if it still fits (plants and snow in its way go)
	System.Threading.Tasks.Task<int[]> stairSearch;
	// (since Beta 0.1.6.0: no spot found, or the one found no longer fits once its land has grown, it looks again with a new
	// seed and that spot's chunk avoided, up to 3 searches in all, so a moved staircase stands up again somewhere)
	int stairSearchesLeft, stairFromX, stairFromZ; HashSet<int> stairAvoid;
	void EarthStairElsewhere(WorldGen.StairSpot s, HashSet<int> changed)
	{
		if (stairSearch != null) return;
		stairAvoid = new HashSet<int>(changed);   // (and never within 2 chunks of a staircase the world already has)
		foreach (var o in StairList) stairAvoid.Add((o.Z / World.CS) * (World.WX / World.CS) + o.X / World.CS);
		stairFromX = s.X; stairFromZ = s.Z; stairSearchesLeft = 3;
		StartStairSearch();
	}
	void StartStairSearch()
	{
		int sx = stairFromX, sz = stairFromZ, seed = rnd.Next(); var avoid = new HashSet<int>(stairAvoid);
		stairSearchesLeft--;
		bool miss = stairSearchesLeft == 2 && TestRun && OS.GetCmdlineUserArgs().Contains("--stairmiss");   // (test: the first finds nothing)
		stairSearch = System.Threading.Tasks.Task.Run(() => { var d = Drawer(); try { return miss ? null : d.StairElsewhere(sx, sz, avoid, seed); } finally { drawers.Add(d); } });
	}
	void StairSearchDone()
	{
		if (stairSearch == null || !stairSearch.IsCompleted) return;
		var st = stairSearch.IsCompletedSuccessfully ? stairSearch.Result : null;
		if (TestRun && stairSearch.IsFaulted) GD.Print($"STAIRMOVE search failed: {stairSearch.Exception?.InnerException?.Message}");
		stairSearch = null;
		if (TestRun) GD.Print($"STAIRMOVE {(st == null ? "none" : string.Join(",", st))}{(st == null && stairSearchesLeft > 0 ? " (looking again)" : "")}");
		if (world == null) return;
		if (st == null) { if (stairSearchesLeft > 0) StartStairSearch(); return; }
		var o = new WorldGen.StairSpot { X = st[0], Y = st[1], Z = st[2], Dir = st[3], N = st[4], Mat = st[5], Placed = true };
		GrowNow(o.X >> 4, o.Z >> 4, 1);
		bool Clears(int b) => Blocks.IsSnow(b) || Blocks.Get(b)?.Shape is "plant" or "flat";
		bool misfit = stairSearchesLeft == 2 && TestRun && OS.GetCmdlineUserArgs().Contains("--stairmisfit");   // (test: the first spot doesn't fit)
		if (misfit || !WorldGen.StairFits(o, EarthStairGround, (x, y, z) => world.GetBlock(x, y, z) is var b && Clears(b) ? 0 : b, EarthCol.StairTop))
		{
			if (TestRun) GD.Print($"STAIRMOVE it doesn't fit there now{(stairSearchesLeft > 0 ? " (looking again)" : "")}");
			stairAvoid?.Add((o.Z / World.CS) * (World.WX / World.CS) + o.X / World.CS);
			if (stairSearchesLeft > 0) StartStairSearch();
			return;
		}
		var d = WorldGen.StairDirs[o.Dir];
		for (int k = 0; k < o.N; k++)
		{   // (the plants and snow in its way go first)
			int cx = o.X + d[0] * k, cz = o.Z + d[1] * k;
			for (int y = EarthStairGround(cz * World.WX + cx) + 1; y <= o.Y + k + 3; y++) if (world.GetBlock(cx, y, cz) != 0) world.SetBlock(cx, y, cz, 0);
		}
		world.Gen.BuildStair(o, (bx, by, bz, id) => world.SetBlock(bx, by, bz, id), EarthStairGround);
		StairList.Add(o); extraStairs.Add(o);
	}
	// (gen 4) the ground in a column under any trees, plants or snow
	int EarthStairGround(int c)
	{
		int x = c % World.WX, z = c / World.WX, y = StairGround(c);
		while (y > 0 && Blocks.IsSnow(world.GetBlock(x, y, z))) y--;
		return y;
	}
	// staircases that turned up somewhere the world didn't grow a spot for (saved: x, y, z, way, steps, block)
	readonly List<WorldGen.StairSpot> extraStairs = new();
	int[] SaveExtraStairs() => extraStairs.SelectMany(o => new[] { o.X, o.Y, o.Z, o.Dir, o.N, o.Mat }).ToArray();
	void LoadExtraStairs(int[] a)
	{
		extraStairs.Clear();
		for (int k = 0; a != null && k + 5 < a.Length; k += 6)
		{
			var o = new WorldGen.StairSpot { X = a[k], Y = a[k + 1], Z = a[k + 2], Dir = Math.Clamp(a[k + 3], 0, 3), N = Math.Clamp(a[k + 4], 1, 16), Mat = a[k + 5], Placed = true };
			extraStairs.Add(o); StairList.Add(o);
		}
	}

	// one stack from your inventory (not what you're wearing) lands on the ground somewhere far away, and stays there
	void StairTakeItem()
	{
		var full = Enumerable.Range(0, Inventory.Armor0).Where(i => inv.Id[i] != 0 && (!netClient || inv.Data[i] == null)).ToList();
		if (full.Count == 0 || RandomDryGround() is not { } to) return;
		int i = full[rnd.Next(full.Count)], id = inv.Id[i], n = Math.Max(1, inv.Count[i]), wear = inv.Wear[i];
		var data = inv.Data[i];
		inv.SetSlot(i, 0, 0);
		if (netClient) { hostConn?.Send(new Dictionary<string, object> { ["t"] = "sdrop", ["id"] = id, ["n"] = n, ["w"] = wear, ["x"] = to.X, ["y"] = to.Y, ["z"] = to.Z }); return; }
		StairDrop(id, n, wear, data, to);
	}
	void StairDrop(int id, int n, int wear, Inventory.PackData data, Vector3 at)
	{
		if (Blocks.Get(id) == null) return;
		for (int k = 0; k < Math.Clamp(n, 1, 64); k++) { var dr = drops.Add(id, at.X, at.Y, at.Z); dr.V = Vector3.Zero; dr.Wear = wear; dr.Data = data; dr.Keep = true; }
	}
}
