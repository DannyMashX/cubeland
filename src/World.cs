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
	// (a block's number is 64-bit: a world can hold more blocks than an int counts to; the same formula as always)
	public static long Idx(int x, int y, int z) => ((long)y * WZ + z) * WX + x;
	public static (int x, int y, int z) FromIdx(long i) => ((int)(i % WX), (int)(i / ((long)WX * WZ)), (int)(i / WX % WZ));

	public readonly BlockStore Blocks;   // every block, chunk by chunk (see BlockStore)
	public byte[] Raw;                   // (while the world grows: one byte per block, packed into Blocks when it's done)
	public readonly int[] Heights;
	public readonly int[] ChunkTop;   // highest block +1 in each 16x16 column of chunks
	public readonly short[] SkyTop;
	public readonly Dictionary<long, int[]> PieceMap = new();   // world index -> which block fills each of its 8 piece spots
	public readonly Dictionary<long, int> Edits = new();          // every block you've changed (what gets saved)
	public readonly HashSet<long> Torches = new();
	public Dictionary<long, int> LightMap = new();               // world index -> torch light level (only lit cells)
	public readonly WorldGen Gen;
	public int SeaLevel => Gen.SeaLevel;

	// called when blocks change, so the game can rebuild the chunks (a set of chunk numbers)
	public Action<HashSet<int>> ChunksChanged;
	public Action<int, int, int> NearbyRebuild;   // (x, z): chunks around here need their sky light redone soon
	public Action<long, int, int, int> BlockChanged;   // (index, old, new, ...) for the game's own bookkeeping

	// The sizes a world can be made (the slider when you make one): 256 to 2048 across, in steps of 128. Worlds made before
	// you could choose are 1280 (or, before that, 640). A tall world is always 640 across. (The same as the web version.)
	public const int MinSize = 256, MaxSize = 2048, SizeStep = 128, DefaultSize = 1280;
	public static int ValidSize(int size, int height) => height == 640 ? 640 : size >= MinSize && size <= MaxSize && size % SizeStep == 0 ? size : 640;
	// How big the biomes are (Biomes worlds): 0.5 (half size) to 4 (4 times as big), in quarters; 1 is the usual size
	public const double MinBiomes = 0.5, MaxBiomes = 4;
	public static double ValidBiomes(double b) => b >= MinBiomes && b <= MaxBiomes && Math.Abs(b * 4 - Math.Round(b * 4)) < 1e-9 ? Math.Round(b * 4) / 4 : 1;
	// The land's version: which features a world grows with. 0 = worlds made before Beta 0.1.1.3 (they keep growing exactly as they did);
	// 1 = natural structures (Biomes worlds) and trees whose trunks go up into their leaves. New worlds get NewLand.
	// 2 = the ores as common at every depth, and in every layer of stone (deep ores: "the ore spread"). 3 = the Trees and Leaves
	// trees (Biomes worlds), without the ore spread; 4 = the trees and the ore spread; 5 and 6 = lands 3 and 4 with the Farming
	// & animals update's world growth (so far: the staircases, Biomes worlds). Until that update is out, more can still be added
	// to lands 5 and 6. 7 and 8 = lands 5 and 6 with blobs of the new rocks in the Andesite (Earthly Extravaganza, every world type
	// but Earthlike: WorldGen.RockBlobs). LandVersion: the newest this code knows.
	public const int LandVersion = 9;   // (9: Earthlike's, gen 4 only)
	public static int ValidLand(int l) => Math.Clamp(l, 0, LandVersion);   // (a saved world keeps the land it was made with, up to the newest)
	public static bool OreSpread(int land) => land == 2 || land == 4 || land == 6 || land == 8;   // (lands with the ore spread)
	public static bool RockBlobs(int land) => land == 7 || land == 8;   // (lands with the rock blobs)
	// The rock blobs (their pictures are in): new worlds are made with them (land 7, or 8). While RockBlobsReady was false they
	// were made without (land 5, or 6), and --rockblobs turned them on for testing. The same as the web version.
	public const bool RockBlobsReady = true;
	public static readonly bool RockBlobsOn = RockBlobsReady || System.Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--rockblobs") >= 0;
	public static bool NewTrees(int land) => land >= 3;                  // (lands with the Trees and Leaves trees)
	public static bool Staircases(int land) => land >= 5;   // (lands with the staircases)
	public static bool WildCrops(int land) => land >= 5;   // (Farming & animals: crops growing wild, and Dead Bushes, in Biomes worlds)
	// The ore spread is held back for now (until the Survival & light update): while OreSpreadReady is false, new worlds are made
	// without it (land 3, or 5). --orespread turns it on for testing (land 4, or 6). Likewise the staircases: new worlds get them
	// (land 5 or 6) only once WorldGen.EerieReady. The same as the web version.
	public const bool OreSpreadReady = false;
	public static readonly bool OreSpreadOn = OreSpreadReady || System.Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--orespread") >= 0;
	public static int NewLand => RockBlobsOn && WorldGen.EerieOn ? (OreSpreadOn ? 8 : 7) : (OreSpreadOn ? 4 : 3) + (WorldGen.EerieOn ? 2 : 0);

	// height: 640 for tall worlds, 192 otherwise; size: how far across (see ValidSize); biomes: how big the biomes are
	public World(long seed, int gen, int height = 192, int size = 640, double biomes = 1, int land = 0)
	{
		WY = gen == 4 || height == 640 ? 640 : 192;   // (gen 4: Earthlike, 640 tall, 1024 or 2048 across)
		WX = gen == 4 ? (size == 1024 ? 1024 : 2048) : ValidSize(size, WY);
		Lazy = gen == 4;
		Blocks = new BlockStore(WX, WY, WZ, Lazy, Lazy ? Cubeland.Blocks.Bedrock : 0);
		if (Lazy) Blocks.Away = i => Edits.TryGetValue(i, out int e) ? e : Cubeland.Blocks.Bedrock;
		Heights = new int[WX * WZ]; ChunkTop = new int[NCX * NCZ]; SkyTop = new short[WX * WZ];
		Gen = new WorldGen(this, seed, gen) { BiomeScale = ValidBiomes(biomes), Land = ValidLand(land) };
	}

	public void Generate(Action<float> progress)
	{
		if (Lazy) { GenerateEarth(progress); return; }
		Gen.Progress = progress;
		Raw = new byte[WX * WY * WZ];
		Gen.Generate();
		var late = Gen.Late.FindAll(l => Raw[l.i] == l.stand);   // (the blocks numbered 256 and up that are still wanted)
		if (System.Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--worldhash") >= 0)   // (testing: a fingerprint of the new world, to check the web version grows the same one)
		{
			uint h = 2166136261; foreach (byte b in Raw) h = (h ^ b) * 16777619;
			foreach (var l in late) { h = (h ^ (uint)l.i) * 16777619; h = (h ^ (uint)l.id) * 16777619; }
			Godot.GD.Print($"WORLDHASH {h:x8}");
			Godot.GD.Print($"STRUCTURES {Gen.Built.Count}: " + string.Join(" | ", Gen.Built));
			Godot.GD.Print("WILD " + string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.GroupBy(late.FindAll(l => l.stand == WorldGen.WildStand || l.stand == WorldGen.MelonStand), l => DataKeys.KeyOf(l.id)), g => $"{g.Key} {System.Linq.Enumerable.Count(g)} (at {System.Linq.Enumerable.First(g).i % WX},{System.Linq.Enumerable.First(g).i / (WX * WZ)},{System.Linq.Enumerable.First(g).i / WX % WZ})")));   // (what grew wild: crops, melons, Dead Bushes)
			Godot.GD.Print($"STAIRS {Gen.StairSpots.Count} spots: " + string.Join(" ", Gen.StairSpots.FindAll(s => s.Placed).ConvertAll(s => $"{s.X},{s.Y},{s.Z},{s.Dir},{s.N},{s.Mat}")));
		}
		var packTime = System.Diagnostics.Stopwatch.StartNew();
		// (lands 7 and 8: the rock blobs go in as the world is packed, each chunk filling its own: WorldGen.RockBlobs)
		var blobs = RockBlobs(Gen.Land) ? Gen.MakeBlobs() : null; long blobMs = packTime.ElapsedMilliseconds;
		var raw = Raw; Blocks.Load(Raw, blobs == null ? null : (x0, y0, z0, over) => Gen.BlobsIn(blobs, raw, x0, y0, z0, over)); Raw = null;
		foreach (var l in late) Blocks[l.i] = l.id;
		if (blobs != null && System.Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--worldhash") >= 0)
		{
			long stone = 0; foreach (byte b in raw) if (b == Cubeland.Blocks.Stone) stone++;
			long all = 0; foreach (var n in blobs.Made) all += n;
			var parts = new List<string>();
			for (int k = 0; k < WorldGen.BlobRockList.Length; k++) parts.Add($"{DataKeys.KeyOf(WorldGen.BlobRockList[k])} {blobs.Made[k]}");
			Godot.GD.Print($"BLOBS {blobs.Count} blobs, {all} of {stone} Andesite ({100.0 * all / Math.Max(1, stone):0.0}%), the list in {blobMs} ms, packed with them in {packTime.ElapsedMilliseconds} ms: " + string.Join(", ", parts));
		}
		if (System.Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--worldhash") >= 0)   // (testing: how much the packed blocks take)
		{
			var (bytes, uniform) = Blocks.Usage();
			Godot.GD.Print($"BLOCKSTORE {bytes / 1048576.0:0.0} MB (was {Blocks.Length / 1048576.0:0.0} MB), {uniform} chunks all one block, packed in {packTime.ElapsedMilliseconds} ms");
			uint h2 = 2166136261; var count = new SortedDictionary<int, int>();
			for (long i = 0; i < Blocks.Length; i++) { int b = Blocks[i]; h2 = (h2 ^ (byte)b) * 16777619; if (b >= 2200 && b < 3700) count[b] = count.GetValueOrDefault(b) + 1; }
			Godot.GD.Print($"PACKEDHASH {h2:x8}");
			// (the same over every block's full id, as the world stands once the late list is in: the same as --mixtest's LIVE at
			// the world's birth, and the web version's "packed")
			uint h3 = 2166136261;
			for (long i = 0; i < Blocks.Length; i++) h3 = (h3 ^ (uint)Blocks[i]) * 16777619;
			Godot.GD.Print($"FULLHASH {h3:x8}");
			// (and, with --layerhash=<file>, the same per layer (y; z then x) and per chunk column (cx,cz; y, z, x), to find where
			// two editions' worlds differ; with --spot=x,y,z,r the blocks round a spot too)
			if (System.Linq.Enumerable.FirstOrDefault(Godot.OS.GetCmdlineUserArgs(), a => a.StartsWith("--layerhash=")) is { } lh)
			{
				var o = new System.Text.StringBuilder("LAYERS\n");
				for (int y = 0; y < WY; y++) { uint hy = 2166136261; for (int z = 0; z < WZ; z++) for (int x = 0; x < WX; x++) hy = (hy ^ (uint)Blocks[Idx(x, y, z)]) * 16777619; o.Append($"{y} {hy:x8}\n"); }
				o.Append("COLUMNS\n");
				for (int cz = 0; cz < NCZ; cz++) for (int cx = 0; cx < NCX; cx++)
				{
					uint hc = 2166136261;
					for (int y = 0; y < WY; y++) for (int z = cz * CS; z < cz * CS + CS; z++) for (int x = cx * CS; x < cx * CS + CS; x++) hc = (hc ^ (uint)Blocks[Idx(x, y, z)]) * 16777619;
					o.Append($"{cx},{cz} {hc:x8}\n");
				}
				if (System.Linq.Enumerable.FirstOrDefault(Godot.OS.GetCmdlineUserArgs(), a => a.StartsWith("--spot=")) is { } sp)
				{
					var q = sp[7..].Split(','); int sx = int.Parse(q[0]), sy = int.Parse(q[1]), sz = int.Parse(q[2]), r = int.Parse(q[3]);
					o.Append($"SPOT {sx},{sy},{sz} r {r} (y, z, x: full ids)\n");
					for (int y = sy - r; y <= sy + r; y++) for (int z = sz - r; z <= sz + r; z++)
					{
						o.Append($"{y} {z}:");
						for (int x = sx - r; x <= sx + r; x++) o.Append(' ').Append(x < 0 || z < 0 || x >= WX || z >= WZ || y < 0 || y >= WY ? -1 : Blocks[Idx(x, y, z)]);
						o.Append('\n');
					}
				}
				System.IO.File.WriteAllText(lh[12..], o.ToString());
			}
			// (and, with --storeread, how long reading every block once takes, row by row, twice: to see a change to the store doesn't slow it)
			if (System.Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--storeread") >= 0)
			for (int pass = 0; pass < 2; pass++)
			{
				var readTime = System.Diagnostics.Stopwatch.StartNew(); long sum = 0;
				for (int y = 0; y < WY; y++) for (int z = 0; z < WZ; z++) for (int x = 0; x < WX; x++) sum += Blocks.Get(x, y, z);
				Godot.GD.Print($"STOREREAD pass {pass + 1}: every block read in {readTime.ElapsedMilliseconds} ms ({sum % 997})");
			}
			// (and how many of each block of the Trees and Leaves woods there are: to compare with the web version's)
			var parts = new List<string>(); foreach (var (b, n) in count) parts.Add($"{Cubeland.Blocks.B[b].Name} {b}: {n}");
			Godot.GD.Print("TREECOUNT " + string.Join(" | ", parts));
		}
		if (System.Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--treeplans") >= 0)
		{   // (testing: each tree a sapling grows, planned at (500, 100, 500) into empty space: its blocks as "x,y,z:key" lines sorted
			//  in ordinal order, joined by \n, and an FNV-1a of their UTF-16 letters, to check the web version plans the same)
			var names = new List<string>(WorldGen.OldTrees) { "prairie_oak" };
			foreach (var t in Nums.Worldgen.TREE_SHAPES ?? Array.Empty<TreeShapeRow>()) if (!names.Contains(t.tree)) names.Add(t.tree);
			foreach (var name in names)
			{
				var lines = new List<string>();
				foreach (var p in Gen.PlanTree(name, 500, 100, 500, (a, b, c) => 0, (a, c) => 100) ?? new()) lines.Add($"{p.x},{p.y},{p.z}:{DataKeys.KeyOf(p.id)}");
				lines.Sort(string.CompareOrdinal);
				uint h3 = 2166136261; foreach (char c in string.Join("\n", lines)) h3 = (h3 ^ c) * 16777619;
				Godot.GD.Print($"TREEPLAN {name} {lines.Count} {h3:x8}");
			}
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
		if (Lazy && !Blocks.Grown(x >> 4, z >> 4)) return true;   // (gen 4: and at the land not grown yet)
		int b = Blocks.Get(x, y, z);
		return b != 0 && !Cubeland.Blocks.B[b].NoCollide && !(Cubeland.Blocks.B[b].Shape == "door" && Cubeland.Blocks.B[b].Open == 1);   // (an open door can be walked past)
	}
	// the block at a spot on the half-block grid (2x the world position): a piece, or the whole block there
	public int SubBlock(int X, int Y, int Z)
	{
		int x = FloorDiv(X, 2), y = FloorDiv(Y, 2), z = FloorDiv(Z, 2), b = GetBlock(x, y, z);
		if (b != 0 && Cubeland.Blocks.B[b]?.Shape == "half") return Y - 2 * y != 0 ? 0 : b;   // (a half-tall block fills only the bottom half)
		if (!Cubeland.Blocks.IsPieces(b)) return b;
		return PieceMap.TryGetValue(Idx(x, y, z), out var a) ? SpotBlock(a[(X - 2 * x) + (Y - 2 * y) * 2 + (Z - 2 * z) * 4]) : 0;
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
	public float SkyExposure(int x, int y, int z, Dictionary<long, float> cache)
	{
		if (x < 0 || z < 0 || x >= WX || z >= WZ || y >= WY) return 1;
		if (y < 0) return 0;
		if (y > SkyTop[z * WX + x]) return 1;
		long key = Idx(x, y, z);
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
		foreach (long i in Torches)
		{
			var (tx, ty, tz) = FromIdx(i);
			if (Math.Abs(tx - x) <= MaxLight && Math.Abs(ty - y) <= MaxLight && Math.Abs(tz - z) <= MaxLight) return true;
		}
		return false;
	}
	static readonly int[][] Steps6 = { new[] { 1, 0, 0 }, new[] { -1, 0, 0 }, new[] { 0, 1, 0 }, new[] { 0, -1, 0 }, new[] { 0, 0, 1 }, new[] { 0, 0, -1 } };
	public void Relight(HashSet<int> dirty)
	{
		var old = LightMap; var nm = new Dictionary<long, int>();
		var q = new List<(int x, int y, int z, int l)>();
		foreach (long ti in Torches)
		{
			var tb = Cubeland.Blocks.B[Blocks[ti]];
			if (tb.Burn == 2) continue;   // burnt: no light
			int full = TorchLight + TorchExtra * (tb.Count - 1);
			var (x0, y0, z0) = FromIdx(ti);
			q.Clear(); q.Add((x0, y0, z0, tb.Burn == 1 ? (int)Math.Round(full / 2.0, MidpointRounding.AwayFromZero) : full));
			for (int h = 0; h < q.Count; h++)
			{
				var (x, y, z, l) = q[h]; long i = Idx(x, y, z);
				if (nm.TryGetValue(i, out var have) && have >= l) continue;
				nm[i] = l;
				if (l > 1) foreach (var d in Steps6) if (LightPasses(x + d[0], y + d[1], z + d[2])) q.Add((x + d[0], y + d[1], z + d[2], l - 1));
			}
		}
		foreach (var (i, l) in nm) if (!old.TryGetValue(i, out var o) || o != l) { var (x, y, z) = FromIdx(i); MarkDirty(x, y, z, dirty); }
		foreach (long i in old.Keys) if (!nm.ContainsKey(i)) { var (x, y, z) = FromIdx(i); MarkDirty(x, y, z, dirty); }
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

	// ---- Earthlike worlds (gen 4): the land grows column by column (16 x 16, the full height) as players come near ----
	// Nothing about the land is saved: the plan is made from the seed, the size and Room every time the world opens (or a friend
	// joins), and a column is drawn from the plan alone (EarthCol), then its changes (Edits) put back. A column the players have
	// left is dropped (its changes stay in Edits) and grown again on their return. The game decides which columns grow and drop
	// (Main.Grow.cs); the drawing runs on worker threads, and InstallColumn / ReleaseColumn on the main thread.
	public readonly bool Lazy;               // (gen 4)
	public EarthPlan Plan;                   // (made by Generate)
	public const int EarthRoom = 320;        // (the sea at y 320, 640 tall)
	readonly Dictionary<int, List<long>> colEdits = new();   // each column's changed blocks (world indexes, maybe changed back since)
	void NoteEdit(long i)
	{
		if (!Lazy) return;
		var (x, _, z) = FromIdx(i); int k = (z >> 4) * NCX + (x >> 4);
		if (!colEdits.TryGetValue(k, out var l)) colEdits[k] = l = new List<long>();
		l.Add(i);
	}
	void GenerateEarth(Action<float> progress)
	{
		progress?.Invoke(0.05f);
		Plan = new EarthPlan(Gen.Seed, WX, EarthRoom);
		Array.Fill(SkyTop, (short)-1);
		progress?.Invoke(1);
	}
	public bool ColumnGrown(int cx, int cz) => Blocks.Grown(cx, cz);
	// a column drawn (EarthCol.Draw's blocks) goes in: its changes put back, its heights and sky worked out, its chunks (and its
	// neighbours' edge chunks) built again
	// a drawn column made ready to go in, on the worker that drew it: packed, with each spot's height (the top block, 0 if none)
	// and sky top (the top block light can't pass, -1 if none), as InstallColumn would work them out
	public sealed class GrownColumn { public BlockStore.Column C; public readonly int[] Heights = new int[256]; public readonly short[] Sky = new short[256]; public int Top; }
	public static GrownColumn Prepare(int[] col)
	{
		var g = new GrownColumn { C = BlockStore.Pack(col, NCY) };
		for (int s = 0; s < 256; s++)
		{
			int y = WY - 1; while (y > 0 && col[y * 256 + s] == 0) y--;
			g.Heights[s] = y; g.Top = Math.Max(g.Top, y + 1);
			int t = y; while (t >= 0 && (col[t * 256 + s] == 0 || Cubeland.Blocks.B[col[t * 256 + s]].Transparent)) t--;
			g.Sky[s] = (short)t;
		}
		return g;
	}
	public void InstallColumn(int cx, int cz, int[] col) => InstallColumn(cx, cz, Prepare(col));
	public void InstallColumn(int cx, int cz, GrownColumn g)
	{
		if (!Lazy || Blocks.Grown(cx, cz)) return;
		Blocks.PutColumn(cx, cz, g.C);
		int k = cz * NCX + cx, top = g.Top; bool torch = false;
		for (int s = 0; s < 256; s++) { int c = (cz * CS + (s >> 4)) * WX + cx * CS + (s & 15); Heights[c] = g.Heights[s]; SkyTop[c] = g.Sky[s]; }
		if (colEdits.TryGetValue(k, out var l))
		{
			var seen = new HashSet<long>(); var spots = new HashSet<int>();
			foreach (long i in l)
			{
				if (!seen.Add(i) || !Edits.TryGetValue(i, out int id)) continue;
				var (x, y, z) = FromIdx(i); Blocks.Set(x, y, z, id); spots.Add(z * WX + x);
				if (Cubeland.Blocks.IsTorch(id)) torch = true;
			}
			foreach (int c in spots)   // (the spots changed: their height and sky top again)
			{
				int x = c % WX, z = c / WX, y = WY - 1; while (y > 0 && Blocks.Get(x, y, z) == 0) y--;
				Heights[c] = y; top = Math.Max(top, y + 1);
				SkyTop[c] = (short)ComputeSkyTop(x, z, y);
			}
		}
		ChunkTop[k] = Math.Min(WY, top);
		var dirty = ColumnChunks(cx, cz, true);
		if (torch) Relight(dirty);
		ChunksChanged?.Invoke(dirty);
	}
	// a column the players have left: its blocks go (its changes stay in Edits)
	public void ReleaseColumn(int cx, int cz)
	{
		if (!Lazy || !Blocks.Grown(cx, cz)) return;
		var dirty = ColumnChunks(cx, cz, false);
		Blocks.DropColumn(cx, cz);
		int k = cz * NCX + cx; ChunkTop[k] = 0;
		for (int z = cz * CS; z < cz * CS + CS; z++) for (int x = cx * CS; x < cx * CS + CS; x++) { Heights[z * WX + x] = 0; SkyTop[z * WX + x] = -1; }
		ChunksChanged?.Invoke(dirty);
	}
	// the chunks of a column up to its top (and, with sides, the chunks of the four columns beside it, whose faces it touches)
	HashSet<int> ColumnChunks(int cx, int cz, bool sides)
	{
		var dirty = new HashSet<int>();
		void Add(int x, int z) { if (x < 0 || z < 0 || x >= NCX || z >= NCZ) return; int t = (ChunkTop[z * NCX + x] + CS - 1) / CS; for (int cy = 0; cy < Math.Min(NCY, Math.Max(t, 1)); cy++) dirty.Add(ChunkKey(x, cy, z)); }
		Add(cx, cz);
		if (sides) { Add(cx - 1, cz); Add(cx + 1, cz); Add(cx, cz - 1); Add(cx, cz + 1); }
		return dirty;
	}

	// ---- Changing blocks ----
	public void SetBlock(int x, int y, int z, int id)
	{
		if (!Inside(x, y, z)) return;
		long i = Idx(x, y, z); int old = Blocks.Get(x, y, z);
		Blocks.Set(x, y, z, id);
		Edits[i] = id; NoteEdit(i); Grown.Remove(i);   // (changed: no longer as a sapling grew it)
		if (!Cubeland.Blocks.IsPieces(id)) PieceMap.Remove(i);
		if (Cubeland.Blocks.IsTorch(old)) Torches.Remove(i);
		if (Cubeland.Blocks.IsTorch(id)) Torches.Add(i);
		// a torch spot that's newly lit or smoldering starts its burn-down timer (adding one to a spot keeps its timer)
		if (!BurnsDown(id)) TorchBurn.Remove(i);
		else if (!BurnsDown(old) || Cubeland.Blocks.B[old].Burn != Cubeland.Blocks.B[id].Burn) TorchBurn[i] = BurnTime(Cubeland.Blocks.B[id].Burn);
		if (Cubeland.Blocks.Plain(id) == Cubeland.Blocks.ClayBallBlock) { if (Cubeland.Blocks.Plain(old) != Cubeland.Blocks.ClayBallBlock) Drying[i] = DryTime; } else Drying.Remove(i);
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

	// the chunks round (x, y, z) drawn again (nothing changed in the world: a door that's finished swinging)
	public void Redraw(int x, int y, int z) { var dirty = new HashSet<int>(); MarkDirty(x, y, z, dirty); ChunksChanged?.Invoke(dirty); }

	// a change only for show (the lone doorway): not saved, not one of your edits, not sent to anyone, nothing else told
	public void SetLooks(int x, int y, int z, int id)
	{
		if (!Inside(x, y, z)) return;
		Blocks.Set(x, y, z, id);
		int ck = (z / CS) * NCX + x / CS;
		if (id != 0) ChunkTop[ck] = Math.Max(ChunkTop[ck], y + 1);
		var dirty = new HashSet<int>();
		MarkDirty(x, y, z, dirty);
		ChunksChanged?.Invoke(dirty);
	}

	// ---- Loading a save: the blocks you changed, put back on top of the freshly grown terrain (before any chunk is built) ----
	public void ApplyEdits(IList<long> idxs, IList<int> ids)
	{
		var cols = new HashSet<int>();
		for (int k = 0; k < idxs.Count && k < ids.Count; k++)
		{
			long i = idxs[k]; int id = ids[k];
			if (i < 0 || i >= Blocks.Length || (id != 0 && Cubeland.Blocks.Get(id) == null)) continue;
			if (Cubeland.Blocks.IsPieces(id) && !PieceMap.ContainsKey(i)) continue;   // (pieces with nothing saved for them: leave the spot as it grew)
			if (!Cubeland.Blocks.IsPieces(id)) PieceMap.Remove(i);
			var (x, y, z) = FromIdx(i);
			Blocks.Set(x, y, z, id); Edits[i] = id; NoteEdit(i);
			if (Cubeland.Blocks.IsTorch(id)) Torches.Add(i); else Torches.Remove(i);
			if (BurnsDown(id)) TorchBurn[i] = BurnTime(Cubeland.Blocks.B[id].Burn); else TorchBurn.Remove(i);   // (then the save puts back how long each has left)
			if (Cubeland.Blocks.Plain(id) == Cubeland.Blocks.ClayBallBlock) Drying[i] = DryTime; else Drying.Remove(i);
			FarmChanged(i, 0, id, true);
			WakeWater(x, y, z);   // water that was still flowing when you saved carries on
			cols.Add(z * WX + x);
			if (id != 0) { int c = (z / CS) * NCX + x / CS; ChunkTop[c] = Math.Max(ChunkTop[c], y + 1); }
		}
		foreach (int c in cols)
		{
			int x = c % WX, z = c / WX;
			SkyTop[c] = (short)ComputeSkyTop(x, z, WY - 1);   // (from the top: something built higher than the land grew is a roof too)
			int y = WY - 1; while (y > 0 && Blocks.Get(x, y, z) == 0) y--; Heights[c] = y;
		}
		if (Torches.Count > 0) Relight(new HashSet<int>());
	}

	// ---- Torches burn down a stage when their time runs out (lit -> smoldering -> burnt); Clay Balls dry into Bricks ----
	public static readonly float[][] BurnTimes = Nums.N.BURN_TIMES;   // lit 15-25 min, smoldering 5-10 min
	public const float DryTime = 120;
	public readonly Dictionary<long, float> TorchBurn = new(), Drying = new();
	static readonly Random burnRnd = new();
	public static float BurnTime(int burn) => BurnTimes[burn][0] + (float)burnRnd.NextDouble() * (BurnTimes[burn][1] - BurnTimes[burn][0]);
	public static bool BurnsDown(int id) => Cubeland.Blocks.IsTorch(id) && Cubeland.Blocks.B[id].Burn < 2;   // (burnt is done; Ever Torches never burn down)
	public void TickTimers(float dt)
	{
		var done = new List<long>();
		foreach (var i in new List<long>(TorchBurn.Keys)) { float left = TorchBurn[i] - dt; if (left > 0) TorchBurn[i] = left; else done.Add(i); }
		foreach (long i in done)
		{
			int id = Blocks[i];
			if (!BurnsDown(id)) { TorchBurn.Remove(i); continue; }
			var b = Cubeland.Blocks.B[id]; var (x, y, z) = FromIdx(i);
			SetBlock(x, y, z, Cubeland.Blocks.WithSnow(Cubeland.Blocks.TorchVariant(b.Wi, b.Count, b.Burn + 1), b.Snowed));   // (starts the next stage's timer, or ends it once burnt)
		}
		done.Clear();
		foreach (var i in new List<long>(Drying.Keys)) { float left = Drying[i] - dt; if (left > 0) Drying[i] = left; else done.Add(i); }
		foreach (long i in done)
		{
			Drying.Remove(i);
			var (x, y, z) = FromIdx(i);
			if (Cubeland.Blocks.Plain(Blocks[i]) == Cubeland.Blocks.ClayBallBlock) SetBlock(x, y, z, Cubeland.Blocks.WithSnow(Cubeland.Blocks.BrickBlock, Cubeland.Blocks.SnowIn(Blocks[i])));
		}
		TickFarm(dt);
	}

	// ---- Water flow ----
	// Still water stays put. Water pours down into empty space below it, and spreads sideways across solid ground,
	// getting one step weaker per block until it runs out after 7. Take away what feeds it and it drains away.
	// Running water washes torches off. Only spots near a change are checked, a few times a second.
	public readonly HashSet<long> WaterQueue = new();
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
	public readonly HashSet<long> FallQueue = new();
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
		if (Cubeland.Blocks.IsStillWater(cur)) return cur;                                           // still water never moves (river water neither)
		if (cur != 0 && !Cubeland.Blocks.IsWater(cur) && !Cubeland.Blocks.IsTorch(cur)) return cur;  // solid blocks hold water back
		{   // (0.1.5.4) a river heals: touching running river water on 2 or more of the 4 sides, it becomes that river water (the first such side's, in Sides order)
			int rv = 0, rn = 0;
			foreach (var s in Sides) { int n = GetBlock(x + s[0], y, z + s[1]); if (Cubeland.Blocks.IsRunningRiver(n)) { rn++; if (rv == 0) rv = n; } }
			if (rn >= 2) return rv;
		}
		if (Cubeland.Blocks.IsWater(GetBlock(x, y + 1, z))) return W0 + 1;                           // pouring down from above
		int best = 8;
		foreach (var s in Sides)
		{
			int n = GetBlock(x + s[0], y, z + s[1]);
			if (!Cubeland.Blocks.IsWater(n)) continue;
			// water only spreads sideways from water that rests on something (ground, or still water)
			int under = GetBlock(x + s[0], y - 1, z + s[1]);
			if (Cubeland.Blocks.IsStillWater(under) || (under != 0 && !Cubeland.Blocks.IsWater(under) && !Cubeland.Blocks.IsTorch(under))) best = Math.Min(best, Cubeland.Blocks.WaterLevel(n) + 1);
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
		foreach (long i in new List<long>(WaterQueue))
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
			long i = Idx(x, y, z);
			if (Cubeland.Blocks.IsTorch(Blocks[i]))
			{   // water puts a torch out and washes it away (it drops as Burnt Torches); that also changes the light
				for (int k = Cubeland.Blocks.B[Blocks[i]].Count; k > 0; k--) Drop?.Invoke(Cubeland.Blocks.TorchDrop(Blocks[i]), x, y, z);
				SetBlock(x, y, z, id); continue;
			}
			Blocks.Set(x, y, z, id); Edits[i] = id; NoteEdit(i);
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
		void Snowballs(int tx, int ty, int tz, int layers) { for (int n = Cubeland.Blocks.SnowballsFor(layers, Random.Shared); n > 0; n--) Drop?.Invoke(Cubeland.Blocks.Snowball, tx, ty, tz); }
		// (a torch on a wall falls off it, but the snow round it still lies on the ground, so that stays)
		void Fall(int tx, int ty, int tz, int t, bool keepSnow = false)
		{
			for (int n = Cubeland.Blocks.B[t].Count; n > 0; n--) Drop?.Invoke(Cubeland.Blocks.TorchDrop(t), tx, ty, tz);
			if (keepSnow && Cubeland.Blocks.SnowIn(t) > 0) SetBlock(tx, ty, tz, Cubeland.Blocks.Snow);
			else { Snowballs(tx, ty, tz, Cubeland.Blocks.SnowIn(t)); SetBlock(tx, ty, tz, 0); }
		}
		int above = GetBlock(x, y + 1, z);
		if (Cubeland.Blocks.IsTorch(above) && Cubeland.Blocks.B[above].Wall == null) Fall(x, y + 1, z, above);
		else if (Cubeland.Blocks.Get(above)?.Shape is "flat" or "plant") { Drop?.Invoke(dropFor(above), x, y + 1, z); Snowballs(x, y + 1, z, Cubeland.Blocks.SnowIn(above)); SetBlock(x, y + 1, z, 0); }   // (a rock lying on it, or a flower growing in it)
		else if (Cubeland.Blocks.SnowIn(above) > 0 && !Cubeland.Blocks.IsPieces(above)) { Snowballs(x, y + 1, z, 1); SetBlock(x, y + 1, z, Cubeland.Blocks.Plain(above)); }   // (the snow round a wall torch, stairs or the Cutter, with nothing under it now)
		if (Cubeland.Blocks.IsSnow(above)) { Snowballs(x, y + 1, z, Cubeland.Blocks.B[above].Layers); SetBlock(x, y + 1, z, 0); }   // (snow lying on it)
		foreach (var s in Sides)
		{
			int n = GetBlock(x + s[0], y, z + s[1]);
			if (Cubeland.Blocks.IsTorch(n) && Cubeland.Blocks.B[n].Wall is { } w && w[0] == -s[0] && w[1] == -s[1]) Fall(x + s[0], y, z + s[1], n, true);
		}
	}

	// fill (id) or empty (0) the spots in `mask` of a block space all at once; 8 pieces of one block become that block
	public bool SetSpots(int x, int y, int z, int mask, int id)
	{
		if (!Inside(x, y, z)) return false;
		long i = Idx(x, y, z); int cur = Blocks.Get(x, y, z);
		int[] a = Cubeland.Blocks.IsPieces(cur) && PieceMap.TryGetValue(i, out var have) ? have : null;
		if (a == null)
		{
			if (id == 0 || (cur != 0 && !Cubeland.Blocks.IsWater(cur) && cur != Cubeland.Blocks.Snow)) return false;   // (into one layer of snow too: it stays round them)
			a = new int[8];
		}
		a = (int[])a.Clone();
		int v = id == 0 ? 0 : id + GroupMul * FreeGroup(a);
		for (int s = 0; s < 8; s++) if ((mask & (1 << s)) != 0) { if (id != 0 && a[s] != 0) return false; a[s] = v; }
		ApplyPieces(x, y, z, a, cur == Cubeland.Blocks.Snow || cur == Cubeland.Blocks.SnowyPieces);
		return true;
	}
	// Each of a pieces space's 8 spots holds block + GroupMul x group. Everything put down at once (a slab, stairs, a quarter,
	// a vertical shape, or one loose piece) shares a group, 1-15, the lowest free in that space; spots saved before groups came
	// in are group 0 (they keep the old rule: the same block's spots are one shape). Things never join together: two slabs
	// stay a double slab, 8 pieces stay 8 pieces. Same numbers as the web version.
	// GroupMul is 8192 (Earthlike's blocks go past 4096). Saves and welcomes from before held block + 4096 x group, and a save
	// still does unless a piece's block is 4096 or more (then it says "pg": 8192): so a save that never needed it is byte for
	// byte as before. SpotIn reads a spot packed either way; SpotOut packs one the old way.
	public const int GroupMul = 8192, OldGroupMul = 4096;
	public static int SpotBlock(int v) => v % GroupMul;
	public static long SpotIn(long v, int pg) => pg == OldGroupMul && v > 0 ? v % OldGroupMul + GroupMul * (v / OldGroupMul) : v;
	public static long SpotOut(int v) => SpotBlock(v) + (long)OldGroupMul * (v / GroupMul);
	public static bool PiecesNeedWide(IEnumerable<int[]> spots) { foreach (var a in spots) foreach (int v in a) if (v != 0 && SpotBlock(v) >= OldGroupMul) return true; return false; }
	static int FreeGroup(int[] a) { for (int g = 1; g <= 15; g++) if (Array.TrueForAll(a, v => v / GroupMul != g)) return g; return 15; }
	public bool SetPiece(int x, int y, int z, int s, int id)
	{
		if (!Inside(x, y, z)) return false;
		long i = Idx(x, y, z); int cur = Blocks.Get(x, y, z);
		int[] a = Cubeland.Blocks.IsPieces(cur) && PieceMap.TryGetValue(i, out var have) ? have : null;
		if (a == null)
		{
			if (id == 0 || (cur != 0 && !Cubeland.Blocks.IsWater(cur) && cur != Cubeland.Blocks.Snow)) return false;
			a = new int[8];
		}
		if (id != 0 && a[s] != 0) return false;
		a = (int[])a.Clone(); a[s] = id == 0 ? 0 : id + GroupMul * FreeGroup(a);
		ApplyPieces(x, y, z, a, cur == Cubeland.Blocks.Snow || cur == Cubeland.Blocks.SnowyPieces);
		return true;
	}
	// (in snow: the space keeps its layer of snow while it holds pieces, and is left as Snow when the last one goes)
	void ApplyPieces(int x, int y, int z, int[] a, bool snowy = false)
	{
		bool all = a[0] != 0, any = false;
		for (int s = 0; s < 8; s++) { if (a[s] != a[0]) all = false; if (a[s] != 0) any = true; }
		if (all && a[0] < GroupMul) SetBlock(x, y, z, a[0]);   // (only spots from before groups ever join into a whole block)
		else if (any) { PieceMap[Idx(x, y, z)] = a; SetBlock(x, y, z, snowy ? Cubeland.Blocks.SnowyPieces : Cubeland.Blocks.Pieces); }
		else if (snowy && Cubeland.Blocks.Get(GetBlock(x, y - 1, z)) is { Shape: null, Liquid: false, NoCollide: false, Item: false }) SetBlock(x, y, z, Cubeland.Blocks.Snow);
		else { if (snowy) for (int n = Cubeland.Blocks.SnowballsFor(1, Random.Shared); n > 0; n--) Drop?.Invoke(Cubeland.Blocks.Snowball, x, y, z); SetBlock(x, y, z, 0); }   // (nothing under it to hold the snow: Snowballs)
	}
	// the shape the piece in spot s belongs to (the spots of its group: put down together), or null if it's a loose piece
	public static (string shape, int mask, int block)? ShapeAt(int[] a, int s)
	{
		int b = a[s];
		if (b == 0) return null;
		int mask = 0;
		for (int k = 0; k < 8; k++) if (a[k] == b) mask |= 1 << k;
		var shape = Cubeland.Blocks.ShapeOfMask(mask);
		return shape != null ? (shape, mask, SpotBlock(b)) : null;
	}
}
