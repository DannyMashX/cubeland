using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cubeland;

// The staircases in the woods (WorldGen.Staircases grows them). Reaching the top step of one now and then does something.
public partial class Main
{
	const int StairOdds = 500;      // 1 time in this many
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
		if (world.GetBlock(x, y, z) != Blocks.Pieces || !world.PieceMap.TryGetValue(World.Idx(x, y, z), out var a)) return false;
		for (int b = 0; b < 8; b++) if (a[b] != ((WorldGen.StairMasks[s.Dir] & (1 << b)) != 0 ? s.Mat : 0)) return false;
		return true;
	}

	// no music near a staircase (within 32 blocks across, 24 up or down)
	float stairNearCheck; bool stairNear;
	bool NearStair()
	{
		if (world == null || menuOpen || !WorldGen.EerieOn) return false;
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
		if (world == null || !WorldGen.EerieOn || StairList.Count == 0 || dead) { stairOn = -1; return; }   // (held back for now: see WorldGen.EerieReady)
		if (!stairTested && HasArg("--stairtest")) StairTest();
		int on = player.OnGround ? StairUnder() : -1;
		if (on == stairOn) return;
		stairOn = on;
		if (on < 0) return;
		// (test: `--stairevent=N` makes event N happen every time, 0-4)
		int force = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--stairevent=")) is { } fa ? int.Parse(fa[13..]) : -1;
		if (force < 0 && rnd.Next(StairOdds) != 0) return;
		StairEvent(force >= 0 ? force : rnd.Next(5), StairList[on]);
	}

	// (test: `--stairtest` makes each thing happen once, on a staircase in a Biomes world, and checks it worked)
	bool stairTested;
	void StairTest()
	{
		stairTested = true;
		void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
		var s = StairList.First(o => o.Placed);
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
		Check("you're sent somewhere else, onto dry ground", (player.Pos - p0).Length() > 30 && world.GetBlock(px, py, pz) == 0 && world.Solid(px, py - 1, pz) && !Blocks.IsWater(world.GetBlock(px, py - 1, pz)));
		health = 3; gloom = 0; StairEvent(3, s);
		Check("it hurts, but never kills", health >= 2 && !dead);
		health = MaxHealth; StairEvent(3, s);
		Check("  (and does hurt when you're well)", !inv.Survival || health < MaxHealth);
		for (int i = 0; i < Inventory.Armor0; i++) inv.SetSlot(i, 0, 0);
		inv.SetSlot(4, Blocks.Planks, 5);
		int kept = drops.All.Count(d => d.Keep); StairEvent(4, s);
		Check("an item leaves your inventory and lies somewhere else", inv.Id[4] == 0 && drops.All.Count(d => d.Keep && d.Id == Blocks.Planks) == kept + 5);
		var far = drops.All.Where(d => d.Keep).Select(d => d.P).FirstOrDefault();
		Check("  (far from you, and it stays)", (far - player.Pos).Length() > 30);
		StairEvent(2, s);
		Check("the staircase is gone", !StairStanding(s) && world.GetBlock(x, y, z) == 0);
		Check("and stands somewhere else", StairList.Count(StairStanding) == standing);
	}

	void StairEvent(int which, WorldGen.StairSpot s)
	{
		switch (which)
		{
			case 0:   // the time of day jumps
				float t = (float)rnd.NextDouble();
				if (netClient) hostConn?.Send(new Dictionary<string, object> { ["t"] = "stime", ["time"] = t });
				else sky.TimeOfDay = t;
				break;
			case 1:   // you're somewhere else
				if (RandomDryGround() is { } to) { player.Pos = to; player.Vel = Vector3.Zero; player.Flying = false; stairOn = -1; }
				break;
			case 2:   // the staircase is gone, and stands somewhere else
				StairMove(s);
				break;
			case 3:   // it hurts (never enough to kill you)
				int most = health - gloom - 2;
				if (most >= 1) HurtPlayer(Math.Min(most, 2 + rnd.Next(7)));
				break;
			case 4:   // something you carry turns up somewhere else in the world
				StairTakeItem();
				break;
		}
	}

	// a random spot on dry land (not water, ice or treetops), standing on top of it
	Vector3? RandomDryGround()
	{
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
			if (b == 0 || Blocks.IsLeaves(b) || Blocks.NeedsAxe.Contains(b) || Blocks.Get(b)?.Shape is "plant" or "flat") y--; else break;
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
			if (world.GetBlock(cx, s.Y + k, cz) == Blocks.Pieces) world.SetBlock(cx, s.Y + k, cz, 0);
			for (int y = s.Y + k - 1; y >= s.Y - 1; y--) if (world.GetBlock(cx, y, cz) == s.Mat) world.SetBlock(cx, y, cz, 0);
		}
		stairFall = true; stairOn = -1;
		// and stand it up again in another high forest, somewhere it still fits: one of the spots the world kept for it...
		foreach (var o in StairList.Where(o => o != s && Math.Abs(o.X - s.X) + Math.Abs(o.Z - s.Z) > World.WX / 10).OrderBy(_ => rnd.Next()))
		{
			if (StairStanding(o) || !WorldGen.StairFits(o, StairGround, world.GetBlock)) continue;
			world.Gen.BuildStair(o, (x, y, z, id) => world.SetBlock(x, y, z, id), StairGround);
			return;
		}
		// ...or, if none fit any more, anywhere else in a forest high above the sea (kept in the save as an extra spot)
		for (int tries = 0; tries < 4000; tries++)
		{
			int x = 16 + rnd.Next(World.WX - 32), z = 16 + rnd.Next(World.WZ - 32);
			if (Math.Abs(x - s.X) + Math.Abs(z - s.Z) <= World.WX / 10 || !world.Gen.StairForest(x, z)) continue;
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
