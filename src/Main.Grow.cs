using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cubeland;

// Earthlike worlds (gen 4): the land grows column by column (16 x 16 blocks, the full height) as players come near, and is
// dropped again once they're far away (World.InstallColumn / ReleaseColumn; the drawing: EarthCol, on worker threads).
// - A column grows when it's within render distance + 2 chunks of a player (each axis): single player and a friend around
//   themselves; the host around every player in the game. It's dropped beyond render distance + 4 of all of them.
// - The nearest columns go first, a few at a time (one per worker); what's drawn goes in on the main thread, a few
//   milliseconds' worth a frame.
// - Opening the world (or joining) grows the columns right round the spawn or the player before play starts (GrowNow).
public partial class Main
{
	readonly ConcurrentQueue<(int cx, int cz, World.GrownColumn col)> drawn = new();
	readonly ConcurrentBag<EarthCol> drawers = new();   // (one drawing each: an EarthCol and its tree shapes aren't shared between threads)
	readonly HashSet<int> drawing = new();               // (columns being drawn: cz * NCX + cx)
	readonly HashSet<int> grownCols = new();             // (columns grown)
	float dropCheck;
	static int GrowWorkers => Math.Max(1, System.Environment.ProcessorCount - 2);
	int GrowReach => (settings?.RenderDist ?? 10) + 2;
	int DropReach => (settings?.RenderDist ?? 10) + 4;
	EarthCol Drawer()
	{   // (the world's land: its trees grow as its saplings do; and its staircases)
		var d = drawers.TryTake(out var o) ? o : new EarthCol(world.Plan, new WorldGen(world, world.Gen.Seed, 4) { Land = world.Gen.Land, BiomeScale = world.Gen.BiomeScale });
		d.Stairs = earthStairs; return d;
	}

	// the columns the land grows round: yours, and (hosting) every player's
	List<(int cx, int cz)> GrowCentres()
	{
		var list = new List<(int, int)>();
		if (player != null) list.Add(((int)Math.Floor(player.Pos.X) >> 4, (int)Math.Floor(player.Pos.Z) >> 4));
		if (hosting) foreach (var p in others.Values) list.Add(((int)Math.Floor(p.X) >> 4, (int)Math.Floor(p.Z) >> 4));
		return list;
	}
	static bool Near(List<(int cx, int cz)> centres, int cx, int cz, int r)
	{
		foreach (var (x, z) in centres) if (Math.Abs(cx - x) <= r && Math.Abs(cz - z) <= r) return true;
		return false;
	}

	// every frame (gen 4): put in what's drawn, start drawing the nearest columns still wanted, and now and then drop the far ones
	// (test: --glide=dx,dz moves you that many blocks a second, flying; --growlog prints, once a second, the columns grown, being
	// drawn and waiting, the most put in and the longest a frame spent putting them in, and the slowest frame)
	static readonly float[] Glide = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--glide=")) is { } ga ? ga[8..].Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : null;
	static readonly bool GrowLog = OS.GetCmdlineUserArgs().Contains("--growlog");
	float logTime, logFrame, logInstallMs; int logInstalls, logDropped, logTotal;
	void GrowTick(float dt)
	{
		if (world == null || !world.Lazy || world.Plan == null) return;
		if (Glide != null && player != null && ready) { player.Flying = true; player.Pos += new Vector3(Glide[0], 0, Glide[1]) * dt; }
		var centres = GrowCentres(); if (centres.Count == 0) return;
		int R = GrowReach;
		var sw = System.Diagnostics.Stopwatch.StartNew(); int put = 0;
		while (sw.ElapsedMilliseconds < 6 && drawn.TryDequeue(out var g))
		{
			int k = g.cz * World.NCX + g.cx; drawing.Remove(k);
			if (!Near(centres, g.cx, g.cz, DropReach)) continue;   // (wandered off while it was drawn)
			world.InstallColumn(g.cx, g.cz, g.col); grownCols.Add(k); put++;
		}
		if (GrowLog)
		{
			logInstalls = Math.Max(logInstalls, put); logInstallMs = Math.Max(logInstallMs, (float)sw.Elapsed.TotalMilliseconds); logFrame = Math.Max(logFrame, dt); logTotal += put;
			if ((logTime += dt) >= 1)
			{
				var (pcx, pcz) = centres[0];
				GD.Print($"GROWLOG at column {pcx},{pcz} (y {player.Pos.Y:0}): {grownCols.Count} grown, {drawing.Count} drawing, {drawn.Count} waiting; put in {logTotal} (at most {logInstalls} a frame, {logInstallMs:0.0} ms), dropped {logDropped}; slowest frame {logFrame * 1000:0} ms; {chunkParts.Count} chunk meshes in {groupNodes.Count} groups, {far.Count} far");
				logTime = 0; logFrame = 0; logInstallMs = 0; logInstalls = 0; logTotal = 0; logDropped = 0;
			}
		}
		// the nearest wanted columns first: ring by ring round each centre
		for (int r = 0; r <= R && drawing.Count < GrowWorkers; r++)
			foreach (var (x0, z0) in centres)
				for (int dz = -r; dz <= r && drawing.Count < GrowWorkers; dz++)
					for (int dx = -r; dx <= r && drawing.Count < GrowWorkers; dx++)
					{
						if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
						int cx = x0 + dx, cz = z0 + dz;
						if (cx < 0 || cz < 0 || cx >= World.NCX || cz >= World.NCZ) continue;
						int k = cz * World.NCX + cx;
						if (drawing.Contains(k) || world.ColumnGrown(cx, cz)) continue;
						drawing.Add(k);
						Task.Run(() => { var d = Drawer(); try { drawn.Enqueue((cx, cz, World.Prepare(d.Draw(cx, cz)))); } finally { drawers.Add(d); } });
					}
		if ((dropCheck += dt) < 1) return;
		dropCheck = 0;
		var gone = new List<int>();
		foreach (int k in grownCols) if (!Near(centres, k % World.NCX, k / World.NCX, DropReach)) gone.Add(k);
		var groups = new HashSet<int>();
		foreach (int k in gone)
		{
			int cx = k % World.NCX, cz = k / World.NCX;
			world.ReleaseColumn(cx, cz); grownCols.Remove(k);
			// its meshes go now (they're past the render distance, where changed chunks otherwise wait until you come back)
			lock (dirty)
				for (int cy = 0; cy < World.NCY; cy++)
				{
					int c = World.ChunkKey(cx, cy, cz); dirty.Remove(c); soon.Remove(c); far.Remove(c);
					if (chunkParts.Remove(c)) groups.Add(World.GroupOf(c));
				}
		}
		foreach (int g in groups)   // (each group glued again from what's left; one with nothing left loses its nodes)
		{
			MergeGroup(g);
			if (groupNodes.TryGetValue(g, out var n) && n.solid.Mesh == null && n.water.Mesh == null) { n.solid.QueueFree(); n.water.QueueFree(); groupNodes.Remove(g); }
		}
		logDropped += gone.Count;
	}

	// grow every column within r of (cx, cz) now (opening the world, joining, a far teleport): drawn side by side, put in in order
	void GrowNow(int cx, int cz, int r)
	{
		if (world == null || !world.Lazy || world.Plan == null) return;
		var want = new List<(int cx, int cz)>();
		for (int z = Math.Max(0, cz - r); z <= Math.Min(World.NCZ - 1, cz + r); z++)
			for (int x = Math.Max(0, cx - r); x <= Math.Min(World.NCX - 1, cx + r); x++)
				if (!world.ColumnGrown(x, z)) want.Add((x, z));
		var cols = new World.GrownColumn[want.Count];
		Parallel.For(0, want.Count, new ParallelOptions { MaxDegreeOfParallelism = GrowWorkers }, n =>
		{
			var d = Drawer(); try { cols[n] = World.Prepare(d.Draw(want[n].cx, want[n].cz)); } finally { drawers.Add(d); }
		});
		for (int n = 0; n < want.Count; n++) { world.InstallColumn(want[n].cx, want[n].cz, cols[n]); grownCols.Add(want[n].cz * World.NCX + want[n].cx); }
	}

	// (Beta 0.1.5.5) an Earthlike world's staircases (EarthCol.FindStairs; the regions side by side): found once the plan is made,
	// before any column grows, then drawn by every drawer, each step given its stairs shape now (a step changed since: the save's
	// change wins as it loads, as in the Biomes worlds). None within 2 chunks of the chunks that held builds when the world was
	// first opened in a version with them (Worlds.OldStairs; a friend has them from the host's "info").
	List<int[]> earthStairs = new();
	void FindEarthStairs()
	{
		if (!WorldGen.EerieOn || world == null || !world.Lazy || world.Plan == null) return;
		var skip = new HashSet<int>(current?.OsSkip ?? Array.Empty<int>());
		var sw = System.Diagnostics.Stopwatch.StartNew();
		int n = EarthCol.StairRegions(world.Plan.Size); var found = new int[n * n][];
		Parallel.For(0, n * n, new ParallelOptions { MaxDegreeOfParallelism = GrowWorkers }, r =>
		{
			var d = Drawer(); try { found[r] = d.RegionStair(r, skip); } finally { drawers.Add(d); }
		});
		earthStairs = found.ToList();
		foreach (var st in found)
		{
			if (st == null) continue;
			var o = new WorldGen.StairSpot { X = st[0], Y = st[1], Z = st[2], Dir = st[3], N = st[4], Mat = st[5], Placed = true };
			world.Gen.StairSpots.Add(o);
			var dd = WorldGen.StairDirs[o.Dir];
			for (int k = 0; k < o.N; k++)
			{
				var a = new int[8]; for (int b = 0; b < 8; b++) if ((WorldGen.StairMasks[o.Dir] & (1 << b)) != 0) a[b] = o.Mat;
				world.PieceMap[World.Idx(o.X + dd[0] * k, o.Y + k, o.Z + dd[1] * k)] = a;
			}
		}
		if (TestRun) GD.Print($"EARTHSTAIRS {string.Join(" ", found.Select(st => st == null ? "none" : string.Join(",", st)))} (skip [{string.Join(",", skip.OrderBy(c => c))}], {sw.ElapsedMilliseconds} ms)");
	}

	// where a new Earthlike world's players start: from the plan, the nearest dry land to the map's middle (cells outward, ring
	// by ring): not sea, a lake or a river, not ice or bare peaks, its ground over the sea. Its block (the cell's middle).
	(int x, int z) EarthSpawn() => EarthSpawn(world.Plan);
	public static (int x, int z) EarthSpawn(EarthPlan P)
	{
		int N = P.N, c = N / 2;
		for (int r = 0; r < N / 2; r++)
			for (int dz = -r; dz <= r; dz++)
				for (int dx = -r; dx <= r; dx++)
				{
					if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
					int x = c + dx, z = c + dz; if (x < 0 || z < 0 || x >= N || z >= N) continue;
					int i = z * N + x, b = P.Biome[i];
					if (P.Sea[i] || P.Lake[i] || P.River(i) || b == EarthPlan.Glacier || b == EarthPlan.Peaks || b == EarthPlan.Crags) continue;
					int bx = x * P.Cell + P.Cell / 2, bz = z * P.Cell + P.Cell / 2;
					if (P.GroundAt(bx, bz) <= P.Room + 1) continue;
					return (bx, bz);
				}
		return (P.Size / 2, P.Size / 2);
	}
}
