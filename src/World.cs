using System;
using System.Collections.Generic;

namespace Cubeland;

// The world: every block, plus the light (sky and torches) worked out from them.
public partial class World
{
	// the width and depth: new worlds are 1280 x 1280; worlds made before that (and tall worlds) are 640 x 640. Set by the World being made.
	public static int WX { get; private set; } = 640;
	public static int WZ => WX;
	public const int CS = 16;
	// Tall worlds (640 blocks) and the strata (deep stone layers and their stone sets) are saved for a later update: until
	// this is turned on, new worlds are 192 tall and the deep blocks aren't in the inventory or recipes. (A world already made
	// 640 tall still loads and plays as one.) Bedrock is in now: it lines the bottom of every world.
	public const bool TallWorlds = false;
	// the height: new worlds are a 640 cube; worlds made before that stay 192 tall. Set by the World being made (one at a time).
	// A tall world is the same land lifted up by Lift, with solid rock below it (the same ~130 blocks of air above the sea).
	public static int WY { get; private set; } = 192;
	public static int Lift => WY - 192;
	public static int NCX => WX / CS;   // chunks are 16x16x16 cubes: 40 or 80 across and deep, 12 or 40 high
	public static int NCZ => WZ / CS;
	public static int NCY => WY / CS;
	public static int NChunks => NCX * NCY * NCZ;
	public static int ChunkKey(int cx, int cy, int cz) => (cy * NCZ + cz) * NCX + cx;
	public static (int cx, int cy, int cz) FromChunkKey(int k) => (k % NCX, k / (NCX * NCZ), (k / NCX) % NCZ);
	// chunks are BUILT as 16x16x16 cubes (a changed block only rebuilds the small cubes around it), but DRAWN glued
	// together in 32x32x32 groups (2x2x2 chunks, one mesh each), because every separate mesh costs time to draw
	public const int GS = 2;
	public static int NGX => NCX / GS;
	public static int NGZ => NCZ / GS;
	public static int NGY => NCY / GS;
	public static int NGroups => NGX * NGY * NGZ;
	public static int GroupOf(int k) { var (cx, cy, cz) = FromChunkKey(k); return (cy / GS * NGZ + cz / GS) * NGX + cx / GS; }
	public static (int gx, int gy, int gz) FromGroupKey(int g) => (g % NGX, g / (NGX * NGZ), (g / NGX) % NGZ);
	public static int Idx(int x, int y, int z) => (y * WZ + z) * WX + x;
	public static (int x, int y, int z) FromIdx(int i) => (i % WX, i / (WX * WZ), (i / WX) % WZ);

	public readonly BlockStore Blocks;   // every block, chunk by chunk (see BlockStore)
	public byte[] Raw;                   // (while the world grows: one byte per block, packed into Blocks when it's done)
	public readonly int[] Heights;
	public readonly int[] ChunkTop;   // highest block +1 in each 16x16 column of chunks
	public readonly short[] SkyTop;
	public readonly Dictionary<int, int[]> PieceMap = new();   // world index -> which block fills each of its 8 piece spots
	public readonly Dictionary<int, int> Edits = new();          // every block you've changed (what gets saved)
	public readonly HashSet<int> Torches = new();
	public Dictionary<int, int> LightMap = new();               // world index -> torch light level (only lit cells)
	public readonly WorldGen Gen;
	public int SeaLevel => Gen.SeaLevel;

	// called when blocks change, so the game can rebuild the chunks (a set of chunk numbers)
	public Action<HashSet<int>> ChunksChanged;
	public Action<int, int, int> NearbyRebuild;   // (x, z): chunks around here need their sky light redone soon
	public Action<int, int, int, int> BlockChanged;   // (index, old, new, ...) for the game's own bookkeeping

	// height: 640 for tall worlds, 192 otherwise; size: 1280 for new worlds, 640 for ones made before (a tall world is always 640 across)
	public World(long seed, int gen, int height = 192, int size = 640)
	{
		WY = height == 640 ? 640 : 192;
		WX = size == 1280 && WY == 192 ? 1280 : 640;
		Blocks = new BlockStore(WX, WY, WZ);
		Heights = new int[WX * WZ]; ChunkTop = new int[NCX * NCZ]; SkyTop = new short[WX * WZ];
		Gen = new WorldGen(this, seed, gen);
	}

	public void Generate(Action<float> progress)
	{
		Gen.Progress = progress;
		Raw = new byte[WX * WY * WZ];
		Gen.Generate();
		if (System.Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--worldhash") >= 0)   // (testing: a fingerprint of the new world, to check the web version grows the same one)
		{
			uint h = 2166136261; foreach (byte b in Raw) h = (h ^ b) * 16777619;
			Godot.GD.Print($"WORLDHASH {h:x8}");
		}
		var packTime = System.Diagnostics.Stopwatch.StartNew();
		Blocks.Load(Raw); Raw = null;
		if (System.Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--worldhash") >= 0)   // (testing: how much the packed blocks take)
		{
			var (bytes, uniform) = Blocks.Usage();
			Godot.GD.Print($"BLOCKSTORE {bytes / 1048576.0:0.0} MB (was {Blocks.Length / 1048576.0:0.0} MB), {uniform} chunks all one block, packed in {packTime.ElapsedMilliseconds} ms");
			uint h2 = 2166136261; for (int i = 0; i < Blocks.Length; i++) h2 = (h2 ^ (byte)Blocks[i]) * 16777619;
			Godot.GD.Print($"PACKEDHASH {h2:x8}");
		}
		RecomputeHeights();
		Array.Clear(ChunkTop);
		for (int z = 0; z < WZ; z++) for (int x = 0; x < WX; x++)
		{
			int k = (z / CS) * NCX + x / CS;
			ChunkTop[k] = Math.Max(ChunkTop[k], Math.Min(WY, Heights[z * WX + x] + 1));
		}
		for (int z = 0; z < WZ; z++) for (int x = 0; x < WX; x++) SkyTop[z * WX + x] = (short)ComputeSkyTop(x, z);
		progress?.Invoke(1);
	}

	public void RecomputeHeights()
	{
		System.Threading.Tasks.Parallel.For(0, WZ, z =>
		{
			for (int x = 0; x < WX; x++)
			{
				int y = WY - 1;
				if (Raw != null) while (y > 0 && Raw[Idx(x, y, z)] == 0) y--;   // (while the world grows)
				else while (y > 0 && Blocks.Get(x, y, z) == 0) y--;
				Heights[z * WX + x] = y;
			}
		});
	}

	public static bool Inside(int x, int y, int z) => x >= 0 && z >= 0 && x < WX && z < WZ && y >= 0 && y < WY;
	public int GetBlock(int x, int y, int z)
	{
		if (y < 0) return 3;
		if (x < 0 || z < 0 || x >= WX || z >= WZ || y >= WY) return 0;
		return Blocks.Get(x, y, z);
	}
	public bool Solid(int x, int y, int z)
	{
		if (x < 0 || z < 0 || x >= WX || z >= WZ || y < 0) return true;   // invisible walls at the world's edge
		if (y >= WY) return false;
		int b = Blocks.Get(x, y, z);
		return b != 0 && !Cubeland.Blocks.B[b].NoCollide;
	}
	// the block at a spot on the half-block grid (2x the world position): a piece, or the whole block there
	public int SubBlock(int X, int Y, int Z)
	{
		int x = FloorDiv(X, 2), y = FloorDiv(Y, 2), z = FloorDiv(Z, 2), b = GetBlock(x, y, z);
		if (b != 0 && Cubeland.Blocks.B[b]?.Shape == "half") return Y - 2 * y != 0 ? 0 : b;   // (a half-tall block fills only the bottom half)
		if (b != Cubeland.Blocks.Pieces) return b;
		return PieceMap.TryGetValue(Idx(x, y, z), out var a) ? a[(X - 2 * x) + (Y - 2 * y) * 2 + (Z - 2 * z) * 4] : 0;
	}
	public static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

	// ---- Sky light: how much open sky a spot can see. Tunnels and caves fade to dark the deeper you go. ----
	public bool OpaqueAt(int x, int y, int z) { int b = Blocks.Get(x, y, z); return b != 0 && !Cubeland.Blocks.B[b].Transparent; }
	public int ComputeSkyTop(int x, int z, int from = -1)
	{
		if (from < 0) from = Heights[z * WX + x];
		for (int y = Math.Min(WY - 1, from); y >= 0; y--) if (OpaqueAt(x, y, z)) return y;
		return -1;
	}
	static readonly float[][] SkyDirs;
	static readonly float[] SkyWeights;
	static readonly float SkyTotal;
	const float SkyStep = 0.6f; const int SkySteps = 32;
	static World()
	{
		var dirs = new List<float[]> { new[] { 0f, 1f, 0f } };
		for (int k = 0; k < 8; k++)
		{
			double a = k * Math.PI / 4;
			foreach (double el in new[] { 0.8, 0.3, 0 }) dirs.Add(new[] { (float)(Math.Cos(a) * Math.Cos(el)), (float)Math.Sin(el), (float)(Math.Sin(a) * Math.Cos(el)) });
		}
		SkyDirs = dirs.ToArray();
		SkyWeights = new float[SkyDirs.Length];
		for (int i = 0; i < SkyDirs.Length; i++) { SkyWeights[i] = SkyDirs[i][1] == 0 ? 2.5f : 1; SkyTotal += SkyWeights[i]; }
	}
	public float SkyExposure(int x, int y, int z, Dictionary<int, float> cache)
	{
		if (x < 0 || z < 0 || x >= WX || z >= WZ || y >= WY) return 1;
		if (y < 0) return 0;
		if (y > SkyTop[z * WX + x]) return 1;
		int key = Idx(x, y, z);
		if (cache != null && cache.TryGetValue(key, out var hit)) return hit;
		float open = 0;
		for (int di = 0; di < SkyDirs.Length; di++)
		{
			var d = SkyDirs[di];
			float fx = x + 0.5f, fy = y + 0.5f, fz = z + 0.5f;
			for (int s = 0; s < SkySteps; s++)
			{
				fx += d[0] * SkyStep; fy += d[1] * SkyStep; fz += d[2] * SkyStep;
				int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy), iz = (int)Math.Floor(fz);
				if (ix < 0 || iz < 0 || ix >= WX || iz >= WZ || iy >= WY || iy > SkyTop[iz * WX + ix]) { open += SkyWeights[di] * (1 - 0.6f * s / SkySteps); break; }
				if (iy >= 0 && OpaqueAt(ix, iy, iz)) break;
			}
		}
		float v = MathF.Pow(open / SkyTotal, 0.6f);
		if (cache != null) cache[key] = v;
		return v;
	}

	// ---- Torch light: flood-fills out from every torch, losing one level per block, blocked by solid blocks ----
	public const int TorchLight = 14, TorchExtra = 3, MaxLight = TorchLight + 3 * TorchExtra;
	public static float LightCurve(float l) => MathF.Min(1.2f, MathF.Pow(l / TorchLight, 1.4f));
	bool LightPasses(int x, int y, int z) { if (!Inside(x, y, z)) return false; int b = Blocks.Get(x, y, z); return b == 0 || Cubeland.Blocks.B[b].Transparent; }
	public int LightAt(int x, int y, int z) => !Inside(x, y, z) ? 0 : LightMap.TryGetValue(Idx(x, y, z), out var l) ? l : 0;
	public bool NearTorch(int x, int y, int z)
	{
		foreach (int i in Torches)
		{
			var (tx, ty, tz) = FromIdx(i);
			if (Math.Abs(tx - x) <= MaxLight && Math.Abs(ty - y) <= MaxLight && Math.Abs(tz - z) <= MaxLight) return true;
		}
		return false;
	}
	static readonly int[][] Steps6 = { new[] { 1, 0, 0 }, new[] { -1, 0, 0 }, new[] { 0, 1, 0 }, new[] { 0, -1, 0 }, new[] { 0, 0, 1 }, new[] { 0, 0, -1 } };
	public void Relight(HashSet<int> dirty)
	{
		var old = LightMap; var nm = new Dictionary<int, int>();
		var q = new List<(int x, int y, int z, int l)>();
		foreach (int ti in Torches)
		{
			var tb = Cubeland.Blocks.B[Blocks[ti]];
			if (tb.Burn == 2) continue;   // burnt: no light
			int full = TorchLight + TorchExtra * (tb.Count - 1);
			var (x0, y0, z0) = FromIdx(ti);
			q.Clear(); q.Add((x0, y0, z0, tb.Burn == 1 ? (int)Math.Round(full / 2.0, MidpointRounding.AwayFromZero) : full));
			for (int h = 0; h < q.Count; h++)
			{
				var (x, y, z, l) = q[h]; int i = Idx(x, y, z);
				if (nm.TryGetValue(i, out var have) && have >= l) continue;
				nm[i] = l;
				if (l > 1) foreach (var d in Steps6) if (LightPasses(x + d[0], y + d[1], z + d[2])) q.Add((x + d[0], y + d[1], z + d[2], l - 1));
			}
		}
		foreach (var (i, l) in nm) if (!old.TryGetValue(i, out var o) || o != l) { var (x, y, z) = FromIdx(i); MarkDirty(x, y, z, dirty); }
		foreach (int i in old.Keys) if (!nm.ContainsKey(i)) { var (x, y, z) = FromIdx(i); MarkDirty(x, y, z, dirty); }
		LightMap = nm;
	}
	public static void MarkDirty(int x, int y, int z, HashSet<int> dirty)   // the chunk holding (x, y, z) plus any neighbour chunk touching it
	{
		for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
		{
			int gx = x + dx, gy = y + dy, gz = z + dz;
			if (Inside(gx, gy, gz)) dirty.Add(ChunkKey(gx / CS, gy / CS, gz / CS));
		}
	}

	// ---- Changing blocks ----
	public void SetBlock(int x, int y, int z, int id)
	{
		if (!Inside(x, y, z)) return;
		int i = Idx(x, y, z), old = Blocks.Get(x, y, z);
		Blocks.Set(x, y, z, id);
		Edits[i] = id;
		if (id != Cubeland.Blocks.Pieces) PieceMap.Remove(i);
		if (Cubeland.Blocks.IsTorch(old)) Torches.Remove(i);
		if (Cubeland.Blocks.IsTorch(id)) Torches.Add(i);
		// a torch spot that's newly lit or smoldering starts its burn-down timer (adding one to a spot keeps its timer)
		if (!BurnsDown(id)) TorchBurn.Remove(i);
		else if (!BurnsDown(old) || Cubeland.Blocks.B[old].Burn != Cubeland.Blocks.B[id].Burn) TorchBurn[i] = BurnTime(Cubeland.Blocks.B[id].Burn);
		if (id == Cubeland.Blocks.ClayBallBlock) { if (old != id) Drying[i] = DryTime; } else Drying.Remove(i);
		FarmChanged(i, old, id, false);
		BlockChanged?.Invoke(i, old, id, 0);
		// keep the sky map up to date, and relight nearby chunks if what can see the sky changed
		int col = z * WX + x;
		bool wasOpaque = old != 0 && !Cubeland.Blocks.B[old].Transparent, isOpaque = id != 0 && !Cubeland.Blocks.B[id].Transparent;
		if (wasOpaque != isOpaque)
		{
			if (isOpaque && y > SkyTop[col]) SkyTop[col] = (short)y;
			else if (!isOpaque && y == SkyTop[col]) SkyTop[col] = (short)ComputeSkyTop(x, z, y);
			NearbyRebuild?.Invoke(x, y, z);
		}
		int ck = (z / CS) * NCX + x / CS;
		if (id != 0) ChunkTop[ck] = Math.Max(ChunkTop[ck], y + 1);
		var dirty = new HashSet<int>();
		MarkDirty(x, y, z, dirty);
		if (Cubeland.Blocks.IsTorch(id) || Cubeland.Blocks.IsTorch(old) || NearTorch(x, y, z)) Relight(dirty);
		ChunksChanged?.Invoke(dirty);
		WakeWater(x, y, z);
		WakeFall(x, y, z);
	}

	// ---- Loading a save: the blocks you changed, put back on top of the freshly grown terrain (before any chunk is built) ----
	public void ApplyEdits(IList<int> idxs, IList<int> ids)
	{
		var cols = new HashSet<int>();
		for (int k = 0; k < idxs.Count && k < ids.Count; k++)
		{
			int i = idxs[k], id = ids[k];
			if (i < 0 || i >= Blocks.Length || (id != 0 && Cubeland.Blocks.Get(id) == null)) continue;
			if (id == Cubeland.Blocks.Pieces && !PieceMap.ContainsKey(i)) continue;   // (pieces with nothing saved for them: leave the spot as it grew)
			if (id != Cubeland.Blocks.Pieces) PieceMap.Remove(i);
			var (x, y, z) = FromIdx(i);
			Blocks.Set(x, y, z, id); Edits[i] = id;
			if (Cubeland.Blocks.IsTorch(id)) Torches.Add(i); else Torches.Remove(i);
			if (BurnsDown(id)) TorchBurn[i] = BurnTime(Cubeland.Blocks.B[id].Burn); else TorchBurn.Remove(i);   // (then the save puts back how long each has left)
			if (id == Cubeland.Blocks.ClayBallBlock) Drying[i] = DryTime; else Drying.Remove(i);
			FarmChanged(i, 0, id, true);
			WakeWater(x, y, z);   // water that was still flowing when you saved carries on
			cols.Add(z * WX + x);
			if (id != 0) { int c = (z / CS) * NCX + x / CS; ChunkTop[c] = Math.Max(ChunkTop[c], y + 1); }
		}
		foreach (int c in cols)
		{
			int x = c % WX, z = c / WX;
			SkyTop[c] = (short)ComputeSkyTop(x, z);
			int y = WY - 1; while (y > 0 && Blocks.Get(x, y, z) == 0) y--; Heights[c] = y;
		}
		if (Torches.Count > 0) Relight(new HashSet<int>());
	}

	// ---- Torches burn down a stage when their time runs out (lit -> smoldering -> burnt); Clay Balls dry into Bricks ----
	public static readonly float[][] BurnTimes = { new[] { 900f, 1500f }, new[] { 300f, 600f } };   // lit 15-25 min, smoldering 5-10 min
	public const float DryTime = 120;
	public readonly Dictionary<int, float> TorchBurn = new(), Drying = new();
	static readonly Random burnRnd = new();
	public static float BurnTime(int burn) => BurnTimes[burn][0] + (float)burnRnd.NextDouble() * (BurnTimes[burn][1] - BurnTimes[burn][0]);
	public static bool BurnsDown(int id) => Cubeland.Blocks.IsTorch(id) && Cubeland.Blocks.B[id].Burn < 2;   // (burnt is done; Ever Torches never burn down)
	public void TickTimers(float dt)
	{
		var done = new List<int>();
		foreach (var i in new List<int>(TorchBurn.Keys)) { float left = TorchBurn[i] - dt; if (left > 0) TorchBurn[i] = left; else done.Add(i); }
		foreach (int i in done)
		{
			int id = Blocks[i];
			if (!BurnsDown(id)) { TorchBurn.Remove(i); continue; }
			var b = Cubeland.Blocks.B[id]; var (x, y, z) = FromIdx(i);
			SetBlock(x, y, z, Cubeland.Blocks.TorchVariant(b.Wi, b.Count, b.Burn + 1));   // (starts the next stage's timer, or ends it once burnt)
		}
		done.Clear();
		foreach (var i in new List<int>(Drying.Keys)) { float left = Drying[i] - dt; if (left > 0) Drying[i] = left; else done.Add(i); }
		foreach (int i in done)
		{
			Drying.Remove(i);
			var (x, y, z) = FromIdx(i);
			if (Blocks[i] == Cubeland.Blocks.ClayBallBlock) SetBlock(x, y, z, Cubeland.Blocks.BrickBlock);
		}
		TickFarm(dt);
	}

	// ---- Water flow ----
	// Still water stays put. Water pours down into empty space below it, and spreads sideways across solid ground,
	// getting one step weaker per block until it runs out after 7. Take away what feeds it and it drains away.
	// Running water washes torches off. Only spots near a change are checked, a few times a second.
	public readonly HashSet<int> WaterQueue = new();
	float waterTimer;
	public Action<int, int, int, int> Drop;   // (item, x, y, z): something fell out of the world (a washed-off torch...)
	public bool NoWater;   // (in a joined game the host's game runs the water)
	public void WakeWater(int x, int y, int z)
	{
		if (NoWater) return;
		if (Inside(x, y, z)) WaterQueue.Add(Idx(x, y, z));
		foreach (var d in Steps6) if (Inside(x + d[0], y + d[1], z + d[2])) WaterQueue.Add(Idx(x + d[0], y + d[1], z + d[2]));
	}
	// a block changed here: it and the blocks next to it get checked for loose Sand, Gravel and Dirt (see Falling).
	// Not when a save is loaded, so land that floats stays put until something next to it changes.
	public readonly HashSet<int> FallQueue = new();
	public void WakeFall(int x, int y, int z)
	{
		if (NoWater) return;   // (in a joined game the host's game does this)
		if (Inside(x, y, z)) FallQueue.Add(Idx(x, y, z));
		foreach (var d in Steps6) if (Inside(x + d[0], y + d[1], z + d[2])) FallQueue.Add(Idx(x + d[0], y + d[1], z + d[2]));
	}
	static readonly int[][] Sides = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } };
	int WaterWants(int x, int y, int z, int cur)
	{
		int W0 = Cubeland.Blocks.Water;
		if (cur == W0) return cur;                                                                   // still water never moves
		if (cur != 0 && !Cubeland.Blocks.IsWater(cur) && !Cubeland.Blocks.IsTorch(cur)) return cur;  // solid blocks hold water back
		if (Cubeland.Blocks.IsWater(GetBlock(x, y + 1, z))) return W0 + 1;                           // pouring down from above
		int best = 8;
		foreach (var s in Sides)
		{
			int n = GetBlock(x + s[0], y, z + s[1]);
			if (!Cubeland.Blocks.IsWater(n)) continue;
			// water only spreads sideways from water that rests on something (ground, or still water)
			int under = GetBlock(x + s[0], y - 1, z + s[1]);
			if (under == W0 || (under != 0 && !Cubeland.Blocks.IsWater(under) && !Cubeland.Blocks.IsTorch(under))) best = Math.Min(best, n - W0 + 1);
		}
		if (best <= 7) return W0 + best;
		return Cubeland.Blocks.IsWater(cur) ? 0 : cur;                                               // nothing feeding it: dries up
	}
	public void UpdateWater(float dt)
	{
		waterTimer -= dt;
		if (waterTimer > 0 || WaterQueue.Count == 0) return;
		waterTimer = 0.25f;
		// look at every waiting spot first, then change them all at once, so water spreads one block per step
		var changes = new List<(int x, int y, int z, int id)>();
		int n = 0;
		foreach (int i in new List<int>(WaterQueue))
		{
			WaterQueue.Remove(i);
			var (x, y, z) = FromIdx(i); int cur = Blocks[i];
			int want = WaterWants(x, y, z, cur);
			if (want != cur) changes.Add((x, y, z, want));
			if (++n >= 6000) break;
		}
		var dirty = new HashSet<int>();
		foreach (var (x, y, z, id) in changes)
		{
			int i = Idx(x, y, z);
			if (Cubeland.Blocks.IsTorch(Blocks[i]))
			{   // water puts a torch out and washes it away (it drops as Burnt Torches); that also changes the light
				for (int k = Cubeland.Blocks.B[Blocks[i]].Count; k > 0; k--) Drop?.Invoke(Cubeland.Blocks.TorchDrop(Blocks[i]), x, y, z);
				SetBlock(x, y, z, id); continue;
			}
			Blocks.Set(x, y, z, id); Edits[i] = id;
			int c = (z / CS) * NCX + x / CS;
			if (id != 0) ChunkTop[c] = Math.Max(ChunkTop[c], y + 1);
			MarkDirty(x, y, z, dirty);
			WakeWater(x, y, z);
			WakeFall(x, y, z);
			BlockChanged?.Invoke(i, -1, id, 1);
		}
		if (dirty.Count > 0) ChunksChanged?.Invoke(dirty);
	}

	// when a block is removed, torches standing on it or stuck to its sides fall off (and a rock lying on it)
	public void DropTorchesAround(int x, int y, int z, Func<int, int> dropFor)
	{
		void Fall(int tx, int ty, int tz, int t) { for (int n = Cubeland.Blocks.B[t].Count; n > 0; n--) Drop?.Invoke(Cubeland.Blocks.TorchDrop(t), tx, ty, tz); SetBlock(tx, ty, tz, 0); }
		int above = GetBlock(x, y + 1, z);
		if (Cubeland.Blocks.IsTorch(above) && Cubeland.Blocks.B[above].Wall == null) Fall(x, y + 1, z, above);
		if (Cubeland.Blocks.Get(above)?.Shape is "flat" or "plant") { Drop?.Invoke(dropFor(above), x, y + 1, z); SetBlock(x, y + 1, z, 0); }   // (a rock lying on it, or a flower growing in it)
		foreach (var s in Sides)
		{
			int n = GetBlock(x + s[0], y, z + s[1]);
			if (Cubeland.Blocks.IsTorch(n) && Cubeland.Blocks.B[n].Wall is { } w && w[0] == -s[0] && w[1] == -s[1]) Fall(x + s[0], y, z + s[1], n);
		}
	}

	// fill (id) or empty (0) the spots in `mask` of a block space all at once; 8 pieces of one block become that block
	public bool SetSpots(int x, int y, int z, int mask, int id)
	{
		if (!Inside(x, y, z)) return false;
		int i = Idx(x, y, z), cur = Blocks.Get(x, y, z);
		int[] a = cur == Cubeland.Blocks.Pieces && PieceMap.TryGetValue(i, out var have) ? have : null;
		if (a == null)
		{
			if (id == 0 || (cur != 0 && !Cubeland.Blocks.IsWater(cur))) return false;
			a = new int[8];
		}
		a = (int[])a.Clone();
		for (int s = 0; s < 8; s++) if ((mask & (1 << s)) != 0) { if (id != 0 && a[s] != 0) return false; a[s] = id; }
		ApplyPieces(x, y, z, a);
		return true;
	}
	public bool SetPiece(int x, int y, int z, int s, int id)
	{
		if (!Inside(x, y, z)) return false;
		int i = Idx(x, y, z), cur = Blocks.Get(x, y, z);
		int[] a = cur == Cubeland.Blocks.Pieces && PieceMap.TryGetValue(i, out var have) ? have : null;
		if (a == null)
		{
			if (id == 0 || (cur != 0 && !Cubeland.Blocks.IsWater(cur))) return false;
			a = new int[8];
		}
		if (id != 0 && a[s] != 0) return false;
		a = (int[])a.Clone(); a[s] = id;
		ApplyPieces(x, y, z, a);
		return true;
	}
	void ApplyPieces(int x, int y, int z, int[] a)
	{
		bool all = a[0] != 0, any = false;
		for (int s = 0; s < 8; s++) { if (a[s] != a[0]) all = false; if (a[s] != 0) any = true; }
		if (all) SetBlock(x, y, z, a[0]);
		else if (any) { PieceMap[Idx(x, y, z)] = a; SetBlock(x, y, z, Cubeland.Blocks.Pieces); }
		else SetBlock(x, y, z, 0);
	}
	// the shape the piece in spot s belongs to (its block's pieces in this space), or null if it's a loose piece
	public static (string shape, int mask, int block)? ShapeAt(int[] a, int s)
	{
		int b = a[s];
		if (b == 0) return null;
		int mask = 0;
		for (int k = 0; k < 8; k++) if (a[k] == b) mask |= 1 << k;
		var shape = Cubeland.Blocks.ShapeOfMask(mask);
		return shape != null ? (shape, mask, b) : null;
	}
}
