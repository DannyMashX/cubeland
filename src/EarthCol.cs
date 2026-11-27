using System;
using System.Collections.Generic;

namespace Cubeland;

// Earthlike worlds (land 9, part 2b): one chunk column (16 x 16 blocks, the world's whole height) drawn from the plan alone. A
// column reads only the plan, the seed and its own place, so it comes out the same in any order, at any time, in either
// edition (web translates this file as it is). Block ids are data/blocks.json's (EarthIds below; the data check holds them to
// the rows' keys).
// Version 1, the passes in order, for each block column:
//   1. the ground and its rocks, soils, gravels and ores (the plan's RockAt);
//   2. the rivers: the channel carved along the plan's river curves, its banks, its bed;
//   3. the surface by sub-biome (Surf below: the grassy tops, the sub-biomes' own grounds, patches);
//   4. water: the sea up to sea level, lakes up to their level, the rivers' water; Ice on the sea ice and on frozen lakes and rivers;
//   5. snow.
// (Plants and trees come next; drawing salts 7800-7899 are reserved for it all: checked free against the plan.)
public sealed class EarthCol
{
	readonly EarthPlan P;
	readonly WorldGen Gen;   // (the trees' shapes: WorldGen.PlanTree, today's trees as saplings grow them; null: no trees)
	public readonly int H;   // the world's height: 2 x Room (sea level, the top water block, is y Room)
	public EarthCol(EarthPlan plan, WorldGen gen = null) { P = plan; Gen = gen; H = 2 * plan.Room; }

	// a column's blocks, cube by cube from the bottom: index ((y >> 4) * 4096) + ((y & 15) << 8) + (z << 4) + x, the same order
	// as the store's chunks (so COLHASH reads it straight through)
	public static int At(int x, int y, int z) => ((y >> 4) << 12) | ((y & 15) << 8) | (z << 4) | x;
	// (bare: the land and the trees only, nothing after them: the staircases' own look at it, FindStairs)
	public int[] Draw(int cx, int cz, bool bare = false)
	{
		var col = new int[16 * 16 * H];
		riverCache.Clear();
		var rivers = RiversFor(cx, cz);
		var spots = new Spot[256];
		for (int z = 0; z < 16; z++)
			for (int x = 0; x < 16; x++)
			{
				var s = spots[z * 16 + x] = Probe(cx * 16 + x, cz * 16 + z, rivers);
				DrawBlockColumn(col, x, z, cx * 16 + x, cz * 16 + z, s);
			}
		Falls(col, cx, cz, spots);
		if (Gen != null)
		{   // (0.1.5.5: the staircases, then the wild crops and plants, after the trees)
			Trees(col, cx, cz);
			if (bare) return col;
			DrawStairs(col, cx, cz, spots);
			Life(col, cx, cz, spots);
		}
		for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) Snow(col, x, z, spots[z * 16 + x]);
		return col;
	}
	readonly Dictionary<int, List<RiverBit>> riverCache = new();
	List<RiverBit> RiversFor(int cx, int cz)
	{
		int key = cz * (P.Size / 16) + cx;
		if (!riverCache.TryGetValue(key, out var l)) riverCache[key] = l = RiversNear(cx, cz);
		return l;
	}

	// what a block column's top will be, worked out from the plan alone (the drawing, and the trees beside it, both read it)
	struct Spot
	{
		public EarthPlan.GeoCol c, cs; public int g0, yg, ys, bed, top, sub, channel; public bool wet; public Surf row; public RiverHere rv;
	}
	Spot Probe(int bx, int bz, List<RiverBit> rivers)
	{
		var s = new Spot { c = P.ColAt(bx, bz), ys = -1 };
		int yg = s.g0 = Math.Min(H - 1, P.GroundAt(bx, bz));
		var rv = s.rv = RiverAt(rivers, bx + 0.5, bz + 0.5); s.channel = rv.channel;
		if (rv.channel >= 0)
		{   // in a river's channel: its surface, and its bed (a bowl: depth x (1 - r^2) deep, at least 1)
			s.ys = (int)Math.Floor(P.Room + rv.level);
			yg = s.bed = s.ys - Math.Max(1, (int)Math.Floor(rv.depth * (1 - rv.r * rv.r) + 0.5));
			if (s.g0 < s.bed && StillTop(bx, bz) >= s.ys) { s.channel = -1; s.ys = -1; yg = s.g0; }   // (where it runs out into sea or lake deeper than its bed: just that water, no bed built up)
		}
		else
		{
			int g = yg;
			if (rv.cap < g) g = rv.cap;         // (the banks: cut down towards the water)
			if (rv.levee > g && StillTop(bx, bz) < rv.levee) g = rv.levee;   // (and never under the water beside them; not in the sea or a lake)
			if (g != yg && g >= 2) yg = g;
		}
		s.yg = yg;
		// water's top here: in a river's channel its surface; else the highest of the sea (sea level, Room) and the lakes (their
		// level) among the four plan cells this column's ground is blended from (so the water meets the shore without a gap)
		s.top = s.ys >= 0 ? s.ys : StillTop(bx, bz);
		s.wet = s.top > yg;
		int si = SurfCell(bx, bz, out int jx, out int jz);
		s.cs = si == s.c.i ? s.c : P.ColAt(jx, jz);
		s.sub = P.SubAt(jx, jz); s.row = Surf.Of(s.sub);
		return s;
	}
	bool SurfaceHere(in Spot s) => s.yg >= 1 && (!s.wet && s.ys < 0 || s.wet && s.row.Sea && P.Sea[s.c.i]);

	void DrawBlockColumn(int[] col, int x, int z, int bx, int bz, in Spot s)
	{
		var c = s.c; int yg = s.g0;
		// 1. the ground: the plan's rock at each height (bedrock at the bottom, the soil and what erosion laid down on top)
		for (int y = 0; y <= yg; y++) col[At(x, y, z)] = BlockOf(c, bx, y, bz, RockAt2(c, s.cs, bx, y, bz, yg));
		// 2. the rivers
		if (s.channel >= 0)
		{
			int bed = s.bed, bedBlock = BedBlock(s);
			for (int y = bed + 1; y <= yg; y++) col[At(x, y, z)] = 0;
			for (int y = Math.Max(yg + 1, 2); y <= bed; y++) col[At(x, y, z)] = bedBlock;   // (a river above the ground: its bed built up)
			for (int y = Math.Max(2, bed - 1); y <= bed; y++) col[At(x, y, z)] = bedBlock;
		}
		else if (s.yg != yg)
		{   // (the banks: the ground's top drawn again at its new height, the soil as the plan has it under a top there)
			int g = s.yg;
			for (int y = Math.Max(2, Math.Min(g, yg) - 4); y <= g; y++) col[At(x, y, z)] = BlockOf(c, bx, y, bz, RockAt2(c, s.cs, bx, y, bz, g));
			for (int y = g + 1; y <= yg; y++) col[At(x, y, z)] = 0;
		}
		yg = s.yg;
		// 3. the surface by sub-biome (the land's tops; under water only the sea's own rows)
		if (SurfaceHere(s)) Surface(col, x, z, bx, bz, yg, c, s.cs, s.row);
		// 4. water, and Ice where it stays frozen
		int wb = s.channel >= 0 ? s.rv.water : EarthIds.Water;   // (0.1.5.4: in a river's channel, its running water)
		for (int y = yg + 1; y <= Math.Min(s.top, H - 1); y++) col[At(x, y, z)] = wb;
		if (s.wet && s.top < H && Iced(s)) col[At(x, s.top, z)] = EarthIds.Ice;
	}
	// a wet column whose water's top is frozen (the sea where it's sea ice; a river or lake where summer stays below freezing)
	bool Iced(in Spot s)
	{
		int ic = s.ys >= 0 ? s.channel : s.c.i;
		return P.Sea[s.c.i] && s.ys < 0 ? s.sub == EarthPlan.SeaIce : P.Temp[ic] + P.Swing[ic] < FreezeSummer;
	}
	// (0.1.5.4) falling water: a river channel column whose surface is 2 or more under a channel column beside it (the 4 sides,
	// each one's own Probe, not chained; neither iced over) gets falling water (3915) in its air, from its surface + 1 up to 1
	// under the highest such neighbour's surface. Probes over the chunk's edge are made for it (off the map: none)
	public const int FallingWater = 3915;
	static readonly int[] FallDX = { 1, -1, 0, 0 }, FallDZ = { 0, 0, 1, -1 };
	void Falls(int[] col, int cx, int cz, Spot[] spots)
	{
		for (int z = 0; z < 16; z++)
			for (int x = 0; x < 16; x++)
			{
				var s = spots[z * 16 + x];
				if (s.channel < 0 || s.ys < 0 || Iced(s)) continue;
				int hi = s.ys;
				for (int k = 0; k < 4; k++)
				{
					int nx = x + FallDX[k], nz = z + FallDZ[k], bx = cx * 16 + nx, bz = cz * 16 + nz;
					if (bx < 0 || bz < 0 || bx >= P.Size || bz >= P.Size) continue;
					var n = nx >= 0 && nx < 16 && nz >= 0 && nz < 16 ? spots[nz * 16 + nx] : Probe(bx, bz, RiversFor(bx >> 4, bz >> 4));
					if (n.channel < 0 || n.ys < 0 || Iced(n)) continue;
					if (n.ys > hi) hi = n.ys;
				}
				if (hi - s.ys < 2) continue;
				for (int y = s.ys + 1; y <= Math.Min(hi - 1, H - 1); y++) if (col[At(x, y, z)] == 0) col[At(x, y, z)] = FallingWater;
			}
	}
	int BedBlock(in Spot s) => s.rv.w < 12 * P.Belt ? GravelOf(P.GravelRock[s.channel], null) : EarthIds.Silt;
	// the top block once the surface is drawn (the trees read it for their ground)
	int TopBlock(in Spot s, int bx, int bz)
	{
		if (s.channel >= 0) return BedBlock(s);
		var r = RockAt2(s.c, s.cs, bx, s.yg, bz, s.yg);
		int id = BlockOf(s.c, bx, s.yg, bz, r);
		if (!SurfaceHere(s)) return id;
		int t = SurfBlock(s.row, s.c, s.cs, bx, bz, s.yg, 0, r);
		return t > 0 ? t : id;
	}
	// 6. snow, last (on the ground or the leaves, whichever is on top), where the row has it
	void Snow(int[] col, int x, int z, in Spot s)
	{
		if (s.wet || s.row.Snow <= 0) return;
		int y = H - 1; while (y > 0 && col[At(x, y, z)] == 0) y--;
		if (y + 1 >= H) return;
		int t = col[At(x, y, z)];
		if (t == EarthIds.Water || t == EarthIds.Ice || LifeTop(t) || t == StairStep) return;   // (0.1.5.5: not on the wild crops and plants, or a step)
		if (t == EarthIds.Grass) col[At(x, y, z)] = EarthIds.SnowyGrass;
		col[At(x, y + 1, z)] = s.row.Snow >= 8 ? EarthIds.SnowBlock : EarthIds.Snow + s.row.Snow - 1;
	}
	int StillTop(int bx, int bz)
	{
		int N = P.N, C = P.Cell, top = -1;
		double gx = (bx + 0.5) / C - 0.5, gz = (bz + 0.5) / C - 0.5; int x0 = (int)Math.Floor(gx), z0 = (int)Math.Floor(gz);
		for (int k = 0; k < 4; k++)
		{
			int i = Math.Clamp(z0 + (k >> 1), 0, N - 1) * N + Math.Clamp(x0 + (k & 1), 0, N - 1);
			int t = P.Sea[i] ? P.Room : P.Lake[i] ? (int)Math.Floor(P.Room + P.Level[i]) : -1;
			if (t > top) top = t;
		}
		return top;
	}

	// ---- the trees (pass 5): today's tree shapes (WorldGen.PlanTree: as a sapling grows them, into empty space), by the
	// sub-biome's tree row (TreeRow; else its biome's). Every block within TreeReach of the chunk column, rows from the lowest z,
	// each from the lowest x, may be a tree's spot: Hash(x, z, 7810) under the row's density (and under TreeMax first, so
	// most spots cost one hash); the tree is the row's pick by Hash(floor(x / 8), floor(z / 8), 7811) (patches of a kind, as
	// TREE_PATCH); it grows on dry land outside any channel, on ground its kind takes (TreeGround), from the spot's top. Each
	// tree's blocks inside this column go in, in the planner's order: leaves into air only, logs into air or leaves. So a tree
	// across a column's edge comes out whole, whichever column is drawn first. ----
	public const int TreeReach = 8; public const double TreeMax = 0.05;
	void Trees(int[] col, int cx, int cz)
	{
		int x0 = cx * 16, z0 = cz * 16, R = TreeReach;
		for (int tz = z0 - R; tz < z0 + 16 + R; tz++)
			for (int tx = x0 - R; tx < x0 + 16 + R; tx++)
			{
				if (tx < 0 || tz < 0 || tx >= P.Size || tz >= P.Size) continue;
				string name = TreeKind(tx, tz, out var s); if (name == null) continue;
				int yg = s.yg; bool stilt = name == "stiltroot";
				var plan = Gen.PlanTree(name, tx, yg, tz, (a, b, c) => 0, (a, c) => yg);
				if (plan == null) continue;
				foreach (var (bx, by, bz, id) in plan)
				{
					if (bx < x0 || bx >= x0 + 16 || bz < z0 || bz >= z0 + 16 || by < 0 || by >= H) continue;
					int k = At(bx - x0, by, bz - z0), there = col[k];
					if (there == 0 || !Blocks.IsLeaves(id) && (Blocks.IsLeaves(there) || stilt && there == EarthIds.Water)) col[k] = id;   // (0.1.5.5: a stiltroot's logs may replace Water)
				}
			}
	}
	// (Beta 0.1.5.5) the tree standing at block column (tx, tz), from the plan alone (the trees pass, and the plants pass so it
	// leaves tree spots bare): its kind, or null. s is the spot's probe (when a kind is found). Its spot hash under TreeMax and
	// its row's density; outside a channel, room above; dry, or for a stiltroot still shallow water (StiltWater); its ground
	// (the top block, under water the bed) one its kind takes
	string TreeKind(int tx, int tz, out Spot s)
	{
		s = default;
		double h = P.Hash(tx, tz, 7810); if (h >= TreeMax) return null;
		int cell = SurfCell(tx, tz, out int jx, out int jz), sub = P.SubAt(jx, jz);
		var tr = TreeRow.Of(sub, P.Biome[cell]); if (tr == null || h >= tr.Density) return null;
		s = Probe(tx, tz, RiversFor(Math.Clamp(tx >> 4, 0, P.Size / 16 - 1), Math.Clamp(tz >> 4, 0, P.Size / 16 - 1)));
		if (s.channel >= 0 || s.yg + 1 >= H) return null;
		string name = tr.Pick(P.Hash((int)Math.Floor(tx / 8.0), (int)Math.Floor(tz / 8.0), 7811));
		if (s.wet && !(name == "stiltroot" && StiltWater(s))) return null;
		return TreeGround(name, TopBlock(s, tx, tz)) ? name : null;
	}
	// (0.1.5.5) a stiltroot stands in still water (no river), not iced, at most 2 deep
	bool StiltWater(in Spot s) => s.channel < 0 && s.ys < 0 && !Iced(s) && s.top - s.yg <= 2;
	// the ground a tree takes: grassy tops, dirt and the soils, the mosses and peat, and (0.1.5.5) Charred Ground; and sand too for
	// the dry lands' trees; Mud for the wetland trees; and (0.1.5.5) Mud, Sand and Silt for the stiltroot
	public const int CharredGround = 3935;
	static bool TreeGround(string name, int top) =>
		top == EarthIds.Grass || top == EarthIds.SnowyGrass || top == EarthIds.Dirt || top >= 4800 && top <= 4819 && top != 4814
		|| top == 3920 || top == 3934 || top == EarthIds.Peat || top == 3931 || top == EarthIds.SwampGrass || top == CharredGround
		|| top == EarthIds.Mud && (name == "willow" || name == "cypress" || name == "stiltroot")
		|| top == EarthIds.Sand && (name == "palm" || name == "palo_verde" || name == "dead" || name == "juniper" || name == "stiltroot")
		|| top == EarthIds.Silt && name == "stiltroot";

	// ---- (Beta 0.1.5.5) staircases (pass 5b, after the trees): in land 9 a few short flights of stairs stand in the highest deep
	// woods, at most one in each region (the map cut into n x n, n = Max(1, Floor(Size / 566 + 0.5))). Found once the plan is made
	// (FindStairs: Main.FindEarthStairs, from the plan and the chunks a world's builds are in), then drawn by Draw like the trees.
	// A region's candidates are the plan cells whose middle block is in its middle 60% with a Woods or Taiga biome, the highest
	// first (Elev; ties lowest z, then lowest x); the first of its first 24 to pass every test is its staircase:
	//   a. the cells under the blocks 8 out every way are Woods or Taiga too; f. its chunk isn't within 2 of a skipped one;
	//   b. the drawn ground is at least Room + 6, outside any channel; e. at least 2 logs stand on the ground within 6;
	//   c, d. its steps fit one of the 4 ways (the first from Hash 7703) on the drawn land: level ground with a grassy or bare top
	//   and nothing in the way. Its block: Cobbled Andesite (8) a third of the time (Hash 7704), else the nearest log's planks.
	// Each is [x, y, z, dir, n, mat] (null: none in that region), regions row by row (rz, then rx). The same as the web version. ----
	public List<int[]> Stairs = new();
	public const int StairStep = 70;   // (Blocks.Pieces: each step a stairs piece, its shape the game's: Main.FindEarthStairs)
	public static int StairRegions(int size) => Math.Max(1, (int)Math.Floor(size / 566.0 + 0.5));
	public static bool StairTop(int t) => t == EarthIds.Grass || t == EarthIds.SnowyGrass || t == EarthIds.SwampGrass || t == EarthIds.Dirt
		|| t >= 4800 && t <= 4819 && t != 4814;   // (PlantsLikeGrass or PlantsLikeDirt)
	// the planks of a log's wood: the block keyed <wood>_planks for the log keyed <wood>_log, else Planks
	public static int PlanksOf(int log)
	{
		string k = DataKeys.KeyOf(log);
		return k != null && k.EndsWith("_log") && DataKeys.TryIdOf(k[..^4] + "_planks", out int p) ? p : Blocks.Planks;
	}
	static bool StairSkipped(int x, int z, HashSet<int> skip, int size)
	{
		if (skip == null || skip.Count == 0) return false;
		int nc = size / 16, cx = x >> 4, cz = z >> 4;
		for (int dz = -2; dz <= 2; dz++) for (int dx = -2; dx <= 2; dx++)
		{
			int nx = cx + dx, nz = cz + dz;
			if (nx >= 0 && nz >= 0 && nx < nc && nz < nc && skip.Contains(nz * nc + nx)) return true;
		}
		return false;
	}
	public List<int[]> FindStairs(HashSet<int> skip)
	{
		int n = StairRegions(P.Size); var list = new List<int[]>();
		for (int r = 0; r < n * n; r++) list.Add(RegionStair(r, skip));
		return list;
	}
	// region r's staircase (r = rz * n + rx), or null. (Each region on its own, so they can be found side by side.)
	public int[] RegionStair(int r, HashSet<int> skip)
	{
		int n = StairRegions(P.Size), rx = r % n, rz = r / n, S = P.Size, C = P.Cell, h = C / 2, N = P.N, nc = S / 16;
		int x0 = rx * S / n, x1 = (rx + 1) * S / n, z0 = rz * S / n, z1 = (rz + 1) * S / n;
		int ax = x0 + (x1 - x0) * 2 / 10, bx = x1 - (x1 - x0) * 2 / 10 - 1, az = z0 + (z1 - z0) * 2 / 10, bz = z1 - (z1 - z0) * 2 / 10 - 1;
		var cands = new List<int>();
		for (int gz = 0; gz < N; gz++)
		{
			int mz = gz * C + h; if (mz < az || mz > bz) continue;
			for (int gx = 0; gx < N; gx++)
			{
				int mx = gx * C + h; if (mx < ax || mx > bx) continue;
				int i = gz * N + gx; if (P.Biome[i] == EarthPlan.Woods || P.Biome[i] == EarthPlan.Taiga) cands.Add(i);
			}
		}
		cands.Sort((a, b) => P.Elev[a] != P.Elev[b] ? P.Elev[b].CompareTo(P.Elev[a]) : a.CompareTo(b));
		// the land round a candidate, drawn bare (trees on, nothing after them), kept while this region chooses
		var drawn = new Dictionary<int, int[]>();
		int Get(int x, int y, int z)
		{
			if (x < 0 || z < 0 || x >= S || z >= S || y < 0 || y >= H) return 0;
			int k = (z >> 4) * nc + (x >> 4);
			if (!drawn.TryGetValue(k, out var col)) drawn[k] = col = Draw(x >> 4, z >> 4, true);
			return col[At(x & 15, y, z & 15)];
		}
		int Ground(int x, int z) => Probe(x, z, RiversFor(x >> 4, z >> 4)).yg;
		for (int t = 0; t < Math.Min(24, cands.Count); t++) { var f = StairAt(cands[t], skip, Get, Ground); if (f != null) return f; }
		return null;
	}
	// (the game, Main.StairMove: a staircase that moved) a new spot for it, found the same way on the land drawn bare: from `tries`
	// random cells (seed) of Woods or Taiga further than a tenth of the map from (sx, sz), the plan's ground there at least
	// Room + 6, the highest 400 tested as above (none near skip's chunks), the first to pass (typically a few seconds, away from
	// the game; 10 tries in the 777 1024 world all found one, after 13 to 223 tested). Null: none.
	public int[] StairElsewhere(int sx, int sz, HashSet<int> skip, int seed, int tries = 4000, int test = 400)
	{
		var rnd = new Random(seed); int N = P.N, C = P.Cell, S = P.Size, nc = S / 16;
		var cands = new List<int>(); var seen = new HashSet<int>();
		for (int t = 0; t < tries; t++)
		{
			int i = rnd.Next(N * N), x = i % N * C + C / 2, z = i / N * C + C / 2;
			if (P.Biome[i] != EarthPlan.Woods && P.Biome[i] != EarthPlan.Taiga || Math.Abs(x - sx) + Math.Abs(z - sz) <= S / 10) continue;
			if (x < 16 || z < 16 || x >= S - 16 || z >= S - 16 || P.GroundAt(x, z) < P.Room + 6 || !seen.Add(i)) continue;
			cands.Add(i);
		}
		cands.Sort((a, b) => P.Elev[a] != P.Elev[b] ? P.Elev[b].CompareTo(P.Elev[a]) : a.CompareTo(b));
		var drawn = new Dictionary<int, int[]>();
		int Get(int x, int y, int z)
		{
			if (x < 0 || z < 0 || x >= S || z >= S || y < 0 || y >= H) return 0;
			int k = (z >> 4) * nc + (x >> 4);
			if (!drawn.TryGetValue(k, out var col)) { if (drawn.Count >= 16) drawn.Clear(); drawn[k] = col = Draw(x >> 4, z >> 4, true); }
			return col[At(x & 15, y, z & 15)];
		}
		int Ground(int x, int z) => Probe(x, z, RiversFor(x >> 4, z >> 4)).yg;
		for (int t = 0; t < Math.Min(test, cands.Count); t++) { var f = StairAt(cands[t], skip, Get, Ground); if (f != null) { LastTested = t + 1; return f; } }
		LastTested = Math.Min(test, cands.Count); return null;
	}
	public int LastTested;   // (test: how many StairElsewhere tested)
	int[] StairAt(int i, HashSet<int> skip, Func<int, int, int, int> get, Func<int, int, int> ground)
	{
		int N = P.N, C = P.Cell, x = i % N * C + C / 2, z = i / N * C + C / 2;
		foreach (var d in WorldGen.StairDirs)
		{   // a. deep in the woods
			int px = x + 8 * d[0], pz = z + 8 * d[1];
			if (px < 0 || pz < 0 || px >= P.Size || pz >= P.Size) return null;
			int b = P.Biome[pz / C * N + px / C];
			if (b != EarthPlan.Woods && b != EarthPlan.Taiga) return null;
		}
		if (StairSkipped(x, z, skip, P.Size)) return null;   // f. never near builds
		var s = Probe(x, z, RiversFor(x >> 4, z >> 4));       // b. high enough, not in a channel
		if (s.channel >= 0 || s.yg < P.Room + 6) return null;
		int y = s.yg + 1, n = 5 + (int)Math.Floor(P.Hash(x, z, 7702) * 4), d0 = (int)Math.Floor(P.Hash(x, z, 7703) * 4);
		int logs = 0, best = 1 << 30, near = 0;   // e. trees near: logs standing on the ground within 6, and the nearest
		for (int dz = -6; dz <= 6; dz++) for (int dx = -6; dx <= 6; dx++)
		{
			int b = get(x + dx, ground(x + dx, z + dz) + 1, z + dz);
			if (!Blocks.IsLogBlock(b)) continue;
			logs++;
			int dd = dx * dx + dz * dz; if (dd < best) { best = dd; near = b; }
		}
		if (logs < 2) return null;
		for (int t = 0; t < 4; t++)
		{   // c, d. the way it goes: a random one first, then the others
			int dir = (d0 + t) % 4;
			if (StairFits(x, y, z, dir, n, get, ground)) return new[] { x, y, z, dir, n, P.Hash(x, z, 7704) < 1 / 3.0 ? Blocks.Cobblestone : PlanksOf(near) };
		}
		return null;
	}
	bool StairFits(int x, int y, int z, int dir, int n, Func<int, int, int, int> get, Func<int, int, int> ground)
	{
		int dx = WorldGen.StairDirs[dir][0], dz = WorldGen.StairDirs[dir][1];
		for (int k = 0; k < n; k++)
		{
			int cx = x + dx * k, cz = z + dz * k;
			if (cx < 1 || cz < 1 || cx >= P.Size - 1 || cz >= P.Size - 1) return false;
			int gk = ground(cx, cz), top = get(cx, gk, cz);
			if (gk != y - 1 && gk != y - 2) return false;   // (on level ground: a block lower at most)
			if (!StairTop(top)) return false;
			for (int yy = gk + 1; yy <= y + k + 3; yy++) if (get(cx, yy, cz) != 0) return false;   // (nothing in the way, and room above)
		}
		return true;
	}
	// a staircase's steps in this chunk column: each a stairs piece (Pieces, 70; its shape is the game's), standing on a base of
	// its block down to the ground
	void DrawStairs(int[] col, int cx, int cz, Spot[] spots)
	{
		foreach (var st in Stairs)
		{
			if (st == null) continue;
			int x = st[0], y = st[1], z = st[2], dx = WorldGen.StairDirs[st[3]][0], dz = WorldGen.StairDirs[st[3]][1], n = st[4], mat = st[5];
			for (int k = 0; k < n; k++)
			{
				int bx = x + dx * k - cx * 16, bz = z + dz * k - cz * 16;
				if (bx < 0 || bz < 0 || bx >= 16 || bz >= 16) continue;
				int gk = spots[bz * 16 + bx].yg;
				for (int yy = gk + 1; yy < y + k; yy++) col[At(bx, yy, bz)] = mat;
				col[At(bx, y + k, bz)] = StairStep;
			}
		}
	}

	// ---- (Beta 0.1.5.5) life: wild crops, the melon and Dead Bushes (W), then the ground plants (P). Each block column on its
	// own: it writes only into itself, reading its own drawn blocks (after the trees) and the plan (and the plan's probes of the
	// columns beside it, for the melon). The first thing that takes a column wins. Every hash is the plan's (P.Hash); a column's
	// biome is the plan's Biome of its wobbled surface cell (SurfCell), its sub-biome SubAt of that, as the trees use ----
	public static bool PlantsOn = true;   // (P, the ground plants: Ideas to confirm they belong in 0.1.5.5)
	static readonly WildCropRow[] WildRows = Nums.Worldgen.WILD_CROPS ?? Array.Empty<WildCropRow>();
	static readonly DeadBushNumbers DeadRow = Nums.Worldgen.DEAD_BUSHES;
	static int[] BiomeList(string[] names) => names == null ? Array.Empty<int>() : Array.ConvertAll(names, n => Array.IndexOf(EarthPlan.BiomeNames, n));
	static readonly int[][] WildBiomes = Array.ConvertAll(WildRows, r => BiomeList(r.earthBiomes)), WildNear = Array.ConvertAll(WildRows, r => BiomeList(r.earthNear));
	static readonly int MelonRow = Array.FindIndex(WildRows, r => r.crop == "cantaloupe");
	static readonly int[] DeadBiomes = BiomeList(DeadRow?.earthBiomes);
	static readonly HashSet<int> DeadGround = new(Array.ConvertAll(DeadRow?.earthGround ?? Array.Empty<string>(), DataKeys.IdOf));
	static readonly int[] MelonX = { 1, -1, 0, 0 }, MelonZ = { 0, 0, 1, -1 };   // (WorldGen.MelonSides: +x, -x, +z, -z)
	static readonly int[] NearX = { 1, -1, 0, 0, 1, 1, -1, -1 }, NearZ = { 0, 0, 1, -1, 1, -1, 1, -1 };
	int BiomeAt(int bx, int bz) => P.Biome[SurfCell(bx, bz, out _, out _)];
	static bool Dry(in Spot s) => !s.wet && s.channel < 0;
	// the blocks W and P put in (Snow leaves a column topped by one alone)
	public static bool LifeTop(int t) => t >= 3700 && t <= 3723 || t >= 3940 && t <= 3961 || t >= 194 && t <= 198;
	void Life(int[] col, int cx, int cz, Spot[] spots)
	{
		for (int z = 0; z < 16; z++)
			for (int x = 0; x < 16; x++)
			{
				int bx = cx * 16 + x, bz = cz * 16 + z; var s = spots[z * 16 + x];
				int top = TopBlock(s, bx, bz);
				if (!Wild(col, x, z, bx, bz, s, top) && PlantsOn) Cover(col, x, z, bx, bz, s, top);
			}
	}
	// is block column (x, z) a spot of wild crop row k: some patch middle within the row's spread (lowest z, then lowest x) that
	// is on the map 8 or more from its edge, under the row's chance, in one of its biomes (the cantaloupe: not in a near biome,
	// with one 'within' away in any of the 8 ways, clamped to the map), whose tries (today's) land one on it
	bool CropSpot(int k, int x, int z)
	{
		var r = WildRows[k]; int sp = r.spread;
		for (int mz = z - sp; mz <= z + sp; mz++)
			for (int mx = x - sp; mx <= x + sp; mx++)
			{
				if (mx < 8 || mz < 8 || mx >= P.Size - 8 || mz >= P.Size - 8 || P.Hash(mx, mz, r.seed) > r.chance) continue;
				int bi = BiomeAt(mx, mz);
				if (r.earthNear != null) { if (Array.IndexOf(WildNear[k], bi) >= 0 || !NearBiome(mx, mz, r.within, WildNear[k])) continue; }
				else if (Array.IndexOf(WildBiomes[k], bi) < 0) continue;
				int n = r.tries[0] + (int)Math.Floor(P.Hash(mx, mz, r.seed + 1) * (r.tries[1] - r.tries[0] + 1));
				for (int t = 0; t < n; t++)
				{
					int px = mx + (int)Math.Floor((P.Hash(mx + t, mz, r.seed + 2) - 0.5) * 2 * sp + 0.5), pz = mz + (int)Math.Floor((P.Hash(mx, mz + t, r.seed + 3) - 0.5) * 2 * sp + 0.5);
					if (px == x && pz == z) return true;
				}
			}
		return false;
	}
	bool NearBiome(int x, int z, int far, int[] list)
	{
		for (int k = 0; k < 8; k++)
			if (Array.IndexOf(list, BiomeAt(Math.Clamp(x + NearX[k] * far, 0, P.Size - 1), Math.Clamp(z + NearZ[k] * far, 0, P.Size - 1))) >= 0) return true;
		return false;
	}
	// W: the first wild crop row that lands here and fits (dry, outside a channel; the top like Grass; air above, 2 for Corn),
	// then the melon of a cantaloupe spot beside whose melon side (one, by its hash) is here (the first in MelonSides order;
	// that spot dry with its top like Grass; here dry, top like Grass, air above), then a Dead Bush (its biomes and chance, dry,
	// air above, its ground). True if it took the column
	bool Wild(int[] col, int x, int z, int bx, int bz, in Spot s, int top)
	{
		int yg = s.yg;
		if (!Dry(s) || yg + 2 >= H) return false;
		bool air1 = col[At(x, yg + 1, z)] == 0, air2 = col[At(x, yg + 2, z)] == 0, grass = Blocks.PlantsLikeGrass(top);
		if (grass && air1)
		{
			for (int k = 0; k < WildRows.Length; k++)
			{
				bool corn = WildRows[k].crop == "corn";
				if (corn && !air2 || !CropSpot(k, bx, bz)) continue;
				col[At(x, yg + 1, z)] = Blocks.CropBlock(WildRows[k].crop, 3);
				if (corn) col[At(x, yg + 2, z)] = Blocks.CropBlock(WildRows[k].crop, 3, true);
				return true;
			}
			if (MelonRow >= 0)
				for (int m = 0; m < 4; m++)
				{
					int nx = bx - MelonX[m], nz = bz - MelonZ[m];
					if (nx < 0 || nz < 0 || nx >= P.Size || nz >= P.Size || (int)Math.Floor(P.Hash(nx, nz, WildRows[MelonRow].seed + 4) * 4) != m || !CropSpot(MelonRow, nx, nz)) continue;
					var n = Probe(nx, nz, RiversFor(nx >> 4, nz >> 4));
					if (!Dry(n) || !Blocks.PlantsLikeGrass(TopBlock(n, nx, nz))) continue;
					col[At(x, yg + 1, z)] = Blocks.Cantaloupe; return true;
				}
		}
		if (air1 && DeadRow != null && DeadGround.Contains(top) && P.Hash(bx, bz, DeadRow.seed) <= DeadRow.chance && Array.IndexOf(DeadBiomes, BiomeAt(bx, bz)) >= 0)
		{ col[At(x, yg + 1, z)] = Blocks.DeadBush; return true; }
		return false;
	}
	// P: one roll (Hash 7850) down the column's cover row (its sub-biome's, else its biome's; none in rows with snow), then the
	// pick needs its spot, else the column stays bare; never on a tree's spot (TreeKind)
	const int ShortGrass = 3945, TallGrass = 3946, Fern = 3950, TreeFern = 3951, Vines = 3952, Reeds = 3953, PondLeaves = 3955, DuneGrass = 3956,
		SeaWeed = 3957, ReefFan = 3959, ThornBush = 3960;
	static int TwoTallTop(int id) => id switch { TallGrass => 3947, TreeFern => 3961, Reeds => 3954, SeaWeed => 3958, _ => 0 };
	// the ground a land plant takes: like Grass or Dirt, the mosses, Peat, Rich Soil, Charred Ground, Swamp Grass; and its own
	static bool CoverGround(int id, int top) =>
		Blocks.PlantsLikeGrass(top) || Blocks.PlantsLikeDirt(top) || top == 3920 || top == 3934 || top == EarthIds.Peat || top == 3931 || top == CharredGround || top == EarthIds.SwampGrass
		|| id == DuneGrass && top == EarthIds.Sand || id == ThornBush && (top == EarthIds.Sand || top == 3939 || top == 3928)
		|| id == Reeds && (top == EarthIds.Mud || top == EarthIds.Silt);
	void Cover(int[] col, int x, int z, int bx, int bz, in Spot s, int top)
	{
		if (s.row.Snow > 0) return;
		var list = CoverRow.Of(s.sub, P.Biome[SurfCell(bx, bz, out _, out _)]); if (list == null) return;
		double u = P.Hash(bx, bz, 7850), sum = 0; int pick = 0;
		foreach (var (id, share) in list) { sum += share; if (u < sum) { pick = id; break; } }
		if (pick == 0 || TreeKind(bx, bz, out _) != null) return;
		int yg = s.yg;
		if (pick is SeaWeed or ReefFan)
		{   // (in the sea, on the bed, into its water: at least 3 deep for Sea Weed, 2 for a Reef Fan)
			if (!s.wet || !P.Sea[s.c.i] || s.channel >= 0 || Iced(s) || s.top - yg < (pick == SeaWeed ? 3 : 2)) return;
			if (col[At(x, yg + 1, z)] != EarthIds.Water || pick == SeaWeed && col[At(x, yg + 2, z)] != EarthIds.Water) return;
			col[At(x, yg + 1, z)] = pick; if (pick == SeaWeed) col[At(x, yg + 2, z)] = TwoTallTop(pick);
			return;
		}
		if (pick == PondLeaves)
		{   // (on still water that isn't the sea or a river, 1 to 3 deep, not iced: in the air on its top)
			if (!s.wet || P.Sea[s.c.i] || s.channel >= 0 || Iced(s) || s.top - yg < 1 || s.top - yg > 3 || s.top + 1 >= H || col[At(x, s.top + 1, z)] != 0) return;
			col[At(x, s.top + 1, z)] = PondLeaves; return;
		}
		if (pick == Vines)
		{   // (from the lowest leaves above top + 2 with air under them, 1-3 down, into air only)
			for (int y = yg + 3; y < H; y++)
			{
				if (!Blocks.IsLeaves(col[At(x, y, z)]) || col[At(x, y - 1, z)] != 0) continue;
				int len = 1 + (int)Math.Floor(P.Hash(bx, bz, 7851) * 3);
				for (int k = 1; k <= len && col[At(x, y - k, z)] == 0; k++) col[At(x, y - k, z)] = Vines;
				return;
			}
			return;
		}
		int up = TwoTallTop(pick);   // (a land plant: dry, outside a channel, its ground, air above (2 for the two-tall))
		if (!Dry(s) || yg + 2 >= H || !CoverGround(pick, top) || col[At(x, yg + 1, z)] != 0 || up != 0 && col[At(x, yg + 2, z)] != 0) return;
		col[At(x, yg + 1, z)] = pick; if (up != 0) col[At(x, yg + 2, z)] = up;
	}

	// lakes and rivers freeze over where the summer stays this cold (degrees: the warmest month, Temp + Swing)
	public static double FreezeSummer = 2;

	// a plan rock's block
	int BlockOf(in EarthPlan.GeoCol c, int bx, int y, int bz, EarthPlan.Rock r) =>
		r is EarthPlan.Rock.Gravel or EarthPlan.Rock.Scree ? GravelOf(P.GravelRock[c.i], r == EarthPlan.Rock.Scree ? HostAt(c, bx, y, bz) : null)
			: IsOre(r) ? OreId(r, HostAt(c, bx, y, bz)) : RockId(r);

	// ---- the surface (pass 3) ----
	// The table (Surf) by biome and sub-biome number: top = the top block (Keep: as the ground has it; Grassy: the soil's grassy
	// top; LandGravel: the land's gravel, GravelRock's; HostGravel: the gravel of the rock beneath; else a block id), replacing
	// Depth blocks of soft ground (soils, sand, silt, gravel); rock = what bare rock's top becomes (Keep: the rock); patch and
	// cut = a second top where the patch noise (0-1, salt 7800, about 12 blocks across) is over cut; snow = snow layers on top
	// (8: a Snow Block); sea = the row is for the sea floor (drawn under water).
	void Surface(int[] col, int x, int z, int bx, int bz, int yg, in EarthPlan.GeoCol c, in EarthPlan.GeoCol cs, Surf row)
	{
		var r0 = RockAt2(c, cs, bx, yg, bz, yg);
		int n = Soft(r0) ? (PatchHere(row, bx, bz) ? 1 : row.Depth) : Math.Max(1, row.RockDepth);
		for (int k = 0; k < n && yg - k >= 2; k++)
		{
			var r = k == 0 ? r0 : RockAt2(c, cs, bx, yg - k, bz, yg);
			if (k > 0 && Soft(r0) && !Soft(r)) break;
			int id = SurfBlock(row, c, cs, bx, bz, yg - k, k, r);
			if (id > 0) col[At(x, yg - k, z)] = id;
		}
	}
	bool PatchHere(Surf row, int bx, int bz) => row.Patch != Keep0 && P.Fbm(bx / 12.0, bz / 12.0, 2, 7800) / 0.75 > row.Cut;
	// the surface's block k down from the top, y (r: the plan's rock there): its id, or 0 to leave it as it is. A soft top takes
	// the row's top (or its patch, one deep), Depth deep while the blocks stay soft; a bare rock top takes the row's rock
	int SurfBlock(Surf row, in EarthPlan.GeoCol c, in EarthPlan.GeoCol cs, int bx, int bz, int y, int k, EarthPlan.Rock r)
	{
		int yTop = y + k; var top = k == 0 ? r : RockAt2(c, cs, bx, yTop, bz, yTop);
		if (!Soft(top)) return row.Rock == Keep0 || k >= Math.Max(1, row.RockDepth) ? 0 : Resolve(row.Rock, c, cs, bx, y, bz, r);
		bool patch = PatchHere(row, bx, bz);
		int t = patch ? row.Patch : row.Top;
		if (t == Keep0 || k >= (patch ? 1 : row.Depth)) return 0;
		return Resolve(t, c, cs, bx, y, bz, r);
	}
	int Resolve(int t, in EarthPlan.GeoCol c, in EarthPlan.GeoCol cs, int bx, int y, int bz, EarthPlan.Rock r) => t switch
	{
		Grassy0 => GrassyOf(r),
		LandGravel0 => GravelOf(P.GravelRock[cs.i], null),
		HostGravel0 => GravelOf(P.GravelRock[cs.i], P.Deep(c, bx, y, bz, false)),
		_ => t,
	};
	// (v2) the surface's cell: a block's sub-biome and soil are the plan cell's at a spot wobbled up to about 1.5 cells away
	// (two smooth noises, salts 7820 and 7821, about 8 blocks across), unless that cell is on the other side of a shore (sea or
	// lake: then the block's own), so the cells' edges wind instead of running in squares. Returns that cell, and the spot.
	public static double Wobble = 1.5;
	int SurfCell(int bx, int bz, out int jx, out int jz)
	{
		double a = P.Fbm(bx / 8.0, bz / 8.0, 2, 7820) / 0.375 - 1, b = P.Fbm(bx / 8.0, bz / 8.0, 2, 7821) / 0.375 - 1;
		jx = Math.Clamp(bx + (int)Math.Floor(a * Wobble * P.Cell + 0.5), 0, P.Size - 1);
		jz = Math.Clamp(bz + (int)Math.Floor(b * Wobble * P.Cell + 0.5), 0, P.Size - 1);
		int N = P.N, i = Math.Clamp(bz / P.Cell, 0, N - 1) * N + Math.Clamp(bx / P.Cell, 0, N - 1), j = Math.Clamp(jz / P.Cell, 0, N - 1) * N + Math.Clamp(jx / P.Cell, 0, N - 1);
		if (P.Sea[j] != P.Sea[i] || P.Lake[j] != P.Lake[i]) { jx = bx; jz = bz; return i; }
		return j;
	}
	// (v2) the rock at a height, with the top few blocks' soil (what EarthPlan.SoilAt lays) from the surface column cs, the
	// wobbled cell's; the rock beneath, the bedrock and what's above the ground from the block's own column c
	EarthPlan.Rock RockAt2(in EarthPlan.GeoCol c, in EarthPlan.GeoCol cs, int bx, int y, int bz, int yg)
	{
		if (cs.i == c.i || y > yg || y <= 3) return P.RockAt(c, bx, y, bz, yg);
		var t = P.SoilAt(cs, bx, bz, yg, yg - y);
		return t != EarthPlan.Rock.Air ? t : P.Deep(c, bx, y, bz, true);
	}
	public const int Keep0 = 0, Grassy0 = -1, LandGravel0 = -2, HostGravel0 = -3;
	static bool Soft(EarthPlan.Rock r) => r is EarthPlan.Rock.Soil or EarthPlan.Rock.Sand or EarthPlan.Rock.Laterite or EarthPlan.Rock.Bauxite
		or EarthPlan.Rock.PrairieSoil or EarthPlan.Rock.AshenSoil or EarthPlan.Rock.RedEarth or EarthPlan.Rock.Loess or EarthPlan.Rock.DesertSoil
		or EarthPlan.Rock.ClaySoil or EarthPlan.Rock.SandySoil or EarthPlan.Rock.StonySoil or EarthPlan.Rock.VolcanicSoil or EarthPlan.Rock.GlacialTill
		or EarthPlan.Rock.RichSoil or EarthPlan.Rock.Silt or EarthPlan.Rock.Scree or EarthPlan.Rock.Gravel or EarthPlan.Rock.Peat or EarthPlan.Rock.Mud
		or EarthPlan.Rock.Permafrost;
	// a soil's grassy top (0: it has none, so the top stays as it is). Laterite and Rich Soil grow today's Grass on top
	public static int GrassyOf(EarthPlan.Rock r) => r switch
	{
		EarthPlan.Rock.Soil or EarthPlan.Rock.Laterite or EarthPlan.Rock.Bauxite or EarthPlan.Rock.RichSoil => EarthIds.Grass,
		EarthPlan.Rock.PrairieSoil => 4810, EarthPlan.Rock.AshenSoil => 4811, EarthPlan.Rock.RedEarth => 4812, EarthPlan.Rock.Loess => 4813,
		EarthPlan.Rock.ClaySoil => 4815, EarthPlan.Rock.SandySoil => 4816, EarthPlan.Rock.StonySoil => 4817, EarthPlan.Rock.VolcanicSoil => 4818,
		EarthPlan.Rock.GlacialTill => 4819, EarthPlan.Rock.Mud => EarthIds.SwampGrass,
		_ => 0,
	};

	// ---- the rivers (pass 2) ----
	// Each river cell's stretch is the plan's curve (EarthPlan.MakeRiverLines: B(t) = (1-t)^2 S + 2t(1-t) P + t^2 E), here as 8
	// straight pieces. Along it the width, the depth and the water's level run from the start's (the mean of its own and the next
	// up's) to the end's (the mean of its own and the next down's; into the sea or a lake, or onto the river it joins, that
	// cell's level). A block is in the channel if its middle is within half the width of the curve (the nearest curve by its
	// share of the half width; ties: the lower cell); the channel's bed is a bowl, depth x (1 - r^2) deep (r: the share of the
	// half width out from the middle), at least 1; the bed is the land's gravel (rivers under 12 blocks wide, x Belt) or Silt.
	// Outside the channel the ground within Bank blocks is cut down to 1 over the water + Slope x the distance out (the banks),
	// and the ground within 2 blocks is never under the water's level (so the water never spills over).
	public static double Bank = 8, BankSlope = 0.6;
	const int Pieces = 8;
	sealed class RiverBit
	{
		public int i; public double[] px = new double[Pieces + 1], pz = new double[Pieces + 1];
		public double wS, wE, dS, dE, lS, lE;
		public int pace;   // (0.1.5.4: how fast its water runs, 0 gentle, 1 quick, 2 rapids: RiverPace)
		public bool end;   // (the river ends here: into the sea, a lake, or nothing. Past its last point there's no channel and no banks)
	}
	public struct RiverHere { public int channel; public double r, level, depth, w; public int cap, levee, water; }   // (water: its river water's id, 0.1.5.4)
	// the river cells whose curve (with its banks) reaches this chunk column, lowest cell first
	List<RiverBit> RiversNear(int cx, int cz)
	{
		var list = new List<RiverBit>();
		double reach = 24 * P.Belt + Bank + 2 + Meander * P.Belt, x0 = cx * 16 - reach, x1 = cx * 16 + 16 + reach, z0 = cz * 16 - reach, z1 = cz * 16 + 16 + reach;
		int N = P.N, C = P.Cell;
		int ca = Math.Max(0, (int)Math.Floor(x0 / C) - 1), cb = Math.Min(N - 1, (int)Math.Floor(x1 / C) + 1);
		int ra = Math.Max(0, (int)Math.Floor(z0 / C) - 1), rb = Math.Min(N - 1, (int)Math.Floor(z1 / C) + 1);
		for (int gz = ra; gz <= rb; gz++)
			for (int gx = ca; gx <= cb; gx++)
			{
				int i = gz * N + gx; if (!P.River(i)) continue;
				double sx = P.RiverSX[i], sz = P.RiverSZ[i], qx = P.RiverPX[i], qz = P.RiverPZ[i], ex = P.RiverEX[i], ez = P.RiverEZ[i];
				if (Math.Max(sx, Math.Max(qx, ex)) < x0 || Math.Min(sx, Math.Min(qx, ex)) > x1 || Math.Max(sz, Math.Max(qz, ez)) < z0 || Math.Min(sz, Math.Min(qz, ez)) > z1) continue;
				var b = new RiverBit { i = i };
				for (int k = 0; k <= Pieces; k++)
				{
					double s = (double)k / Pieces, a = (1 - s) * (1 - s), m = 2 * s * (1 - s), e = s * s;
					b.px[k] = a * sx + m * qx + e * ex; b.pz[k] = a * sz + m * qz + e * ez;
				}
				int u = P.RiverUp[i], d = P.Down[i]; bool dr = d >= 0 && P.River(d);
				double w = P.RiverWidth(i), dp = P.RiverDepth(i), lv = P.Level[i];
				b.wS = u >= 0 ? (P.RiverWidth(u) + w) / 2 : w; b.wE = dr ? (w + P.RiverWidth(d)) / 2 : w;
				b.dS = u >= 0 ? (P.RiverDepth(u) + dp) / 2 : dp; b.dE = dr ? (dp + P.RiverDepth(d)) / 2 : dp;
				b.lS = u >= 0 ? (P.Level[u] + lv) / 2 : lv;
				b.lE = d < 0 ? lv : dr && P.RiverUp[d] == i ? (lv + P.Level[d]) / 2 : P.Level[d];
				b.end = !dr;
				if (d >= 0)
				{   // (0.1.5.4) its pace: the cell's fall to its Down, as MakeBraids works it out
					double dd = (i % N != d % N && i / N != d / N) ? C * 1.4142135623730951 : C;
					b.pace = RiverPace(P.Pace(Math.Max(0, P.Level[i] - P.Level[d]) / dd));
				}
				list.Add(b);
			}
		return list;
	}
	// (v2) the rivers wind: a spot is looked up wobbled by up to Meander x Belt blocks (two smooth noises, salts 7830 and 7831, about
	// 64 blocks across), so a river drawn from straight stretches meanders; the same for every spot, so it stays whole
	public static double Meander = 10;
	// (0.1.5.4, running rivers) a river's pace from how steep it runs (EarthPlan.Pace): 0 gentle (under 0.02), 1 quick (under 0.2),
	// 2 rapids; and the river water at a spot: 3891 + 8 x pace + the way it runs there (Dir8 of the river's piece turned back from
	// the wobbled space: WobbleBack). The same in the web version (web's river draft 1)
	public const int RiverWater = 3891;
	public static double QuickPace = 0.02, RapidsPace = 0.2;
	public static int RiverPace(double pace) => pace < QuickPace ? 0 : pace < RapidsPace ? 1 : 2;
	// the 8 ways (0 E, 1 SE, 2 S, 3 SW, 4 W, 5 NW, 6 N, 7 NE; E = +x, S = +z) a direction is nearest, by plain comparisons
	const double Tan22 = 0.41421356237309503;
	public static int Dir8(double wx, double wz)
	{
		if (wx == 0 && wz == 0) return 0;
		double ax = Math.Abs(wx), az = Math.Abs(wz);
		if (az <= ax * Tan22) return wx > 0 ? 0 : 4;
		if (ax <= az * Tan22) return wz > 0 ? 2 : 6;
		return wx > 0 ? (wz > 0 ? 1 : 7) : (wz > 0 ? 3 : 5);
	}
	double WobX(double X, double Z) => P.Fbm(X / 64.0, Z / 64.0, 2, 7830) / 0.375 - 1;
	double WobZ(double X, double Z) => P.Fbm(X / 64.0, Z / 64.0, 2, 7831) / 0.375 - 1;
	// a direction v in the wobbled space, turned back to the world's at X, Z: the wobble moves a spot by A x (WobX, WobZ), so its
	// Jacobian J = I + A x the wobble's slopes (differences over +-0.5), and w = J^-1 v (its sign only: the adjugate, flipped if
	// det < 0)
	int RiverWaterAt(int pace, double vx, double vz, double X, double Z)
	{
		double A = Meander * P.Belt;
		double mxX = WobX(X + 0.5, Z) - WobX(X - 0.5, Z), mxZ = WobX(X, Z + 0.5) - WobX(X, Z - 0.5);
		double mzX = WobZ(X + 0.5, Z) - WobZ(X - 0.5, Z), mzZ = WobZ(X, Z + 0.5) - WobZ(X, Z - 0.5);
		double j00 = 1 + A * mxX, j01 = A * mxZ, j10 = A * mzX, j11 = 1 + A * mzZ, det = j00 * j11 - j01 * j10;
		double wx = j11 * vx - j01 * vz, wz = -j10 * vx + j00 * vz;
		if (det < 0) { wx = -wx; wz = -wz; }
		return RiverWater + 8 * pace + Dir8(wx, wz);
	}
	RiverHere RiverAt(List<RiverBit> rivers, double X, double Z)
	{
		double X0 = X, Z0 = Z, hvx = 0, hvz = 0; int hpace = 0;
		double mx = P.Fbm(X / 64.0, Z / 64.0, 2, 7830) / 0.375 - 1, mz = P.Fbm(X / 64.0, Z / 64.0, 2, 7831) / 0.375 - 1;
		X += mx * Meander * P.Belt; Z += mz * Meander * P.Belt;
		var h = new RiverHere { channel = -1, cap = int.MaxValue, levee = int.MinValue }; double best = 1;
		foreach (var b in rivers)
		{
			// the nearest point on the curve's pieces, and how far along (t, 0-1)
			double bd = double.MaxValue, bt = 0, bvx = 0, bvz = 0; bool past = false;
			for (int k = 0; k < Pieces; k++)
			{
				double ax = b.px[k], az = b.pz[k], vx = b.px[k + 1] - ax, vz = b.pz[k + 1] - az, l2 = vx * vx + vz * vz;
				double u0 = l2 > 0 ? ((X - ax) * vx + (Z - az) * vz) / l2 : 0, u = Math.Clamp(u0, 0, 1);
				double dx = X - (ax + u * vx), dz = Z - (az + u * vz), d2 = dx * dx + dz * dz;
				if (d2 < bd) { bd = d2; bt = (k + u) / Pieces; past = b.end && k == Pieces - 1 && u0 > 1; bvx = vx; bvz = vz; }
			}
			if (past) continue;   // (beyond where the river ends: no round end of channel or banks out into the sea or lake)
			double dist = Math.Sqrt(bd), half = (b.wS + (b.wE - b.wS) * bt) / 2, level = b.lS + (b.lE - b.lS) * bt;
			int ys = (int)Math.Floor(P.Room + level);
			if (dist < half)
			{
				double r = dist / half;
				if (r < best) { best = r; h.channel = b.i; h.r = r; h.level = level; h.depth = b.dS + (b.dE - b.dS) * bt; h.w = half * 2; hvx = bvx; hvz = bvz; hpace = b.pace; }
			}
			else if (dist < half + Bank)
			{
				int cap = ys + 1 + (int)Math.Floor((dist - half) * BankSlope);
				if (cap < h.cap) h.cap = cap;
				if (dist < half + 2 && ys > h.levee) h.levee = ys;
			}
		}
		if (h.channel >= 0) h.water = RiverWaterAt(hpace, hvx, hvz, X0, Z0);
		return h;
	}

	// the rock an ore or scree is in (the rock body there, without ores)
	EarthPlan.Rock? HostAt(in EarthPlan.GeoCol c, int bx, int y, int bz) => P.Deep(c, bx, y, bz, false);
	static bool IsOre(EarthPlan.Rock r) => r is EarthPlan.Rock.CopperOre or EarthPlan.Rock.TinOre or EarthPlan.Rock.IronOre or EarthPlan.Rock.GoldOre or EarthPlan.Rock.SilverOre;

	// a plan rock's block (an ore's: OreId; gravel and scree: GravelOf)
	public static int RockId(EarthPlan.Rock r) => r switch
	{
		EarthPlan.Rock.Air => 0, EarthPlan.Rock.Water => EarthIds.Water, EarthPlan.Rock.Soil => EarthIds.Dirt, EarthPlan.Rock.Sand => EarthIds.Sand,
		EarthPlan.Rock.Andesite => EarthIds.Stone, EarthPlan.Rock.Granite => 4403, EarthPlan.Rock.Basalt => 4404, EarthPlan.Rock.Gabbro => 4405,
		EarthPlan.Rock.Limestone => 4400, EarthPlan.Rock.Chalk => 4402, EarthPlan.Rock.Shale => 4401, EarthPlan.Rock.Sandstone => EarthIds.Sandstone,
		EarthPlan.Rock.Slate => 4406, EarthPlan.Rock.Schist => 4407, EarthPlan.Rock.Gneiss => 4408, EarthPlan.Rock.Marble => 4409, EarthPlan.Rock.Quartzite => 4410,
		EarthPlan.Rock.Bedrock => EarthIds.Bedrock, EarthPlan.Rock.Laterite => 4494,
		EarthPlan.Rock.Bauxite => 4494,   // (Bauxite comes with progression: Laterite until then)
		EarthPlan.Rock.Conglomerate => 4760, EarthPlan.Rock.Siltstone => 4761, EarthPlan.Rock.Dolomite => 4762, EarthPlan.Rock.Travertine => 4763,
		EarthPlan.Rock.Diorite => 4764, EarthPlan.Rock.Rhyolite => 4765, EarthPlan.Rock.Peridotite => 4766, EarthPlan.Rock.Phyllite => 4767,
		EarthPlan.Rock.Serpentinite => 4768, EarthPlan.Rock.Hornfels => 4769, EarthPlan.Rock.FlintNodules => 4820,
		EarthPlan.Rock.PrairieSoil => 4800, EarthPlan.Rock.AshenSoil => 4801, EarthPlan.Rock.RedEarth => 4802, EarthPlan.Rock.Loess => 4803,
		EarthPlan.Rock.DesertSoil => 4804, EarthPlan.Rock.ClaySoil => 4805, EarthPlan.Rock.SandySoil => 4806, EarthPlan.Rock.StonySoil => 4807,
		EarthPlan.Rock.VolcanicSoil => 4808, EarthPlan.Rock.GlacialTill => 4809, EarthPlan.Rock.RichSoil => 3931, EarthPlan.Rock.Silt => EarthIds.Silt,
		EarthPlan.Rock.Peat => EarthIds.Peat, EarthPlan.Rock.Mud => EarthIds.Mud, EarthPlan.Rock.Permafrost => 3921,
		_ => EarthIds.Stone,
	};
	// a rock's gravel (Andesite's is today's Gravel); laid-down gravel is GravelRock's (the rock its catchment wore away most of),
	// scree the rock it lies on (host)
	public static int GravelOf(byte gravelRock, EarthPlan.Rock? host)
	{
		var r = host ?? (EarthPlan.Rock)gravelRock;
		return r switch
		{
			EarthPlan.Rock.Limestone => 5030, EarthPlan.Rock.Shale => 5031, EarthPlan.Rock.Chalk => 5032, EarthPlan.Rock.Granite => 5033,
			EarthPlan.Rock.Basalt => 5034, EarthPlan.Rock.Gabbro => 5035, EarthPlan.Rock.Slate => 5036, EarthPlan.Rock.Schist => 5037,
			EarthPlan.Rock.Gneiss => 5038, EarthPlan.Rock.Marble => 5039, EarthPlan.Rock.Quartzite => 5040, EarthPlan.Rock.Conglomerate => 5041,
			EarthPlan.Rock.Siltstone => 5042, EarthPlan.Rock.Dolomite => 5043, EarthPlan.Rock.Travertine => 5044, EarthPlan.Rock.Diorite => 5045,
			EarthPlan.Rock.Rhyolite => 5046, EarthPlan.Rock.Peridotite => 5047, EarthPlan.Rock.Phyllite => 5048, EarthPlan.Rock.Serpentinite => 5049,
			EarthPlan.Rock.Hornfels => 5050, EarthPlan.Rock.Sandstone => 5051,
			_ => EarthIds.Gravel,   // (Andesite's, and anything without a gravel of its own)
		};
	}
	// an ore in its rock (Geology Draft 2's ores in rocks; today's ores in Andesite; silver waits for progression: its rock)
	public static int OreId(EarthPlan.Rock ore, EarthPlan.Rock? host)
	{
		var h = host == EarthPlan.Rock.Diorite ? EarthPlan.Rock.Granite : host;   // (a granite body's diorite side: the granite ore)
		return (ore, h) switch
		{
			(EarthPlan.Rock.CopperOre, EarthPlan.Rock.Granite) => 4480, (EarthPlan.Rock.CopperOre, EarthPlan.Rock.Basalt) => 4481,
			(EarthPlan.Rock.TinOre, EarthPlan.Rock.Granite) => 4482,
			(EarthPlan.Rock.IronOre, EarthPlan.Rock.Shale) => 4483, (EarthPlan.Rock.IronOre, EarthPlan.Rock.Gabbro) => 4484, (EarthPlan.Rock.IronOre, EarthPlan.Rock.Basalt) => 4485,
			(EarthPlan.Rock.GoldOre, EarthPlan.Rock.Granite) => 4486, (EarthPlan.Rock.GoldOre, EarthPlan.Rock.Schist) => 4487,
			(EarthPlan.Rock.CopperOre, _) => EarthIds.CopperOre, (EarthPlan.Rock.IronOre, _) => EarthIds.IronOre,
			(EarthPlan.Rock.GoldOre, _) => EarthIds.GoldOre, (EarthPlan.Rock.TinOre, _) => EarthIds.TinOre,
			_ => RockId(host ?? EarthPlan.Rock.Andesite),   // (silver: its rock, until progression)
		};
	}
}

// the surface table (pass 3), by biome (0-20) and sub-biome (32-111) number. (Draft: to become worldgen.json's EARTH_SURFACE once
// agreed, read by both editions.) Ids: EarthIds and the rows' numbers; Keep0, Grassy0, LandGravel0, HostGravel0 as EarthCol says.
public sealed class Surf
{
	public int Top = EarthCol.Grassy0, Depth = 1, Rock = EarthCol.Keep0, RockDepth = 1, Patch = EarthCol.Keep0, Snow = 0; public double Cut = 1; public bool Sea;
	static readonly Surf Plain = new();
	static readonly Dictionary<int, Surf> Rows = Make();
	public static Surf Of(int n) => Rows.TryGetValue(n, out var r) ? r : Plain;
	const int K = EarthCol.Keep0, G = EarthCol.Grassy0, LG = EarthCol.LandGravel0, HG = EarthCol.HostGravel0;
	const int TundraMoss = 3920, GlacierIce = 3922, BlueIce = 3923, FrozenCrust = 3924, FrostSplit = 3925, LichenStone = 3926, SaltCrust = 3927,
		CrackedClay = 3928, DesertPavement = 3929, BogMoss = 3934, ReefStone = 3937, MossyStone = 3938, Hardpan = 3939,
		OrangeTerracotta = 218, Clay = 63;
	static Dictionary<int, Surf> Make()
	{
		var d = new Dictionary<int, Surf>();
		void R(int n, int top = G, int depth = 1, int rock = K, int rockDepth = 1, int patch = K, double cut = 1, int snow = 0, bool sea = false) =>
			d[n] = new Surf { Top = top, Depth = depth, Rock = rock, RockDepth = rockDepth, Patch = patch, Cut = cut, Snow = snow, Sea = sea };
		// biomes (where no sub-biome applies)
		R(EarthPlan.Woods); R(EarthPlan.Plains); R(EarthPlan.Desert, top: K, patch: EarthIds.Sand, cut: 0.55);
		R(EarthPlan.Snowy, snow: 1); R(EarthPlan.Swamp, patch: EarthIds.Mud, cut: 0.6); R(EarthPlan.Taiga); R(EarthPlan.Meadow);
		R(EarthPlan.Badlands, top: K, patch: OrangeTerracotta, cut: 0.5);
		R(EarthPlan.Tundra, top: TundraMoss, rock: LichenStone, patch: G, cut: 0.62);
		R(EarthPlan.Glacier, top: GlacierIce, depth: 6, rock: GlacierIce, rockDepth: 6, snow: 2);
		R(EarthPlan.Rainforest); R(EarthPlan.Savanna); R(EarthPlan.Alpine); R(EarthPlan.Crags, top: K);
		R(EarthPlan.Peaks, top: EarthIds.SnowBlock, depth: 2, rock: EarthIds.SnowBlock, rockDepth: 2, patch: K, cut: 1);
		R(EarthPlan.Ocean, top: K); R(EarthPlan.LakeB, top: K);
		R(EarthPlan.ColdWaste, top: FrozenCrust, rock: FrostSplit);
		R(EarthPlan.HotWaste, top: K, patch: EarthIds.Sand, cut: 0.5);
		R(EarthPlan.DryScrub); R(EarthPlan.DryForest);
		// cold
		R(EarthPlan.Crevasses, top: GlacierIce, depth: 6, rock: GlacierIce, rockDepth: 6, patch: BlueIce, cut: 0.6);
		R(EarthPlan.Meltwater, top: BlueIce, depth: 4, rock: BlueIce, rockDepth: 4);
		R(EarthPlan.Nunataks, top: K, rock: K, patch: FrostSplit, cut: 0.6);
		R(EarthPlan.GlacialGravel, top: LG, depth: 2, rock: FrostSplit, patch: FrostSplit, cut: 0.62);
		R(EarthPlan.FrostStone, top: FrostSplit, depth: 2, rock: FrostSplit);
		R(EarthPlan.WindGravel, top: LG, depth: 2, rock: FrostSplit, patch: FrozenCrust, cut: 0.55);
		R(EarthPlan.FrozenSalt, top: SaltCrust, depth: 2, snow: 1);
		R(EarthPlan.LichenField, top: TundraMoss, rock: LichenStone, patch: LichenStone, cut: 0.55);
		R(EarthPlan.FrostCracks, top: TundraMoss, rock: LichenStone, patch: LG, cut: 0.6);
		R(EarthPlan.ThawBogs, top: BogMoss, patch: EarthIds.Peat, cut: 0.6);
		R(EarthPlan.ForestTundra, top: TundraMoss, rock: LichenStone, patch: G, cut: 0.5);
		R(EarthPlan.LichenWoods, top: TundraMoss, rock: LichenStone, patch: G, cut: 0.45);
		R(EarthPlan.LarchStand);
		R(EarthPlan.Muskeg, top: BogMoss, patch: EarthIds.Peat, cut: 0.6);
		R(EarthPlan.OldBurn, patch: EarthCol.CharredGround, cut: 0.55);   // (0.1.5.5: patches of Charred Ground)
		// temperate
		R(EarthPlan.OldGrowth, rock: MossyStone); R(EarthPlan.BirchGrove); R(EarthPlan.Clearing); R(EarthPlan.FernRavine, rock: MossyStone);
		R(EarthPlan.Parkland); R(EarthPlan.Tallgrass); R(EarthPlan.Shortgrass); R(EarthPlan.Steppe, patch: K, cut: 0.6);
		R(EarthPlan.Wildflowers); R(EarthPlan.WetMeadow, patch: EarthIds.Mud, cut: 0.65);
		R(EarthPlan.ReedMarsh, top: EarthIds.Mud); R(EarthPlan.SwampForest, top: EarthIds.Grass, patch: EarthIds.Mud, cut: 0.55);
		R(EarthPlan.Fen, top: EarthIds.Peat, patch: BogMoss, cut: 0.5);
		R(EarthPlan.RockyScrub, top: K, patch: LG, cut: 0.55); R(EarthPlan.FireScar, patch: EarthCol.CharredGround, cut: 0.6);   // (0.1.5.5: patches of Charred Ground)
		// hot
		R(EarthPlan.FernUnder); R(EarthPlan.FloodForest); R(EarthPlan.CloudForest, rock: MossyStone); R(EarthPlan.WoodedSavanna);
		R(EarthPlan.Waterhole, patch: EarthIds.Mud, cut: 0.55); R(EarthPlan.Mounds, patch: Hardpan, cut: 0.68);
		R(EarthPlan.ThornScrub, top: K, patch: G, cut: 0.55);
		R(EarthPlan.DuneSea, top: EarthIds.Sand, depth: 3); R(EarthPlan.Pavement, top: DesertPavement);
		R(EarthPlan.Oasis, top: EarthIds.Grass); R(EarthPlan.DryRiverbed, top: CrackedClay, patch: LG, cut: 0.55);
		R(EarthPlan.SaltFlats, top: SaltCrust, depth: 2); R(EarthPlan.CrackedBed, top: CrackedClay, depth: 2);
		R(EarthPlan.SandSea, top: EarthIds.Sand, depth: 3);
		// mountains
		R(EarthPlan.Treeline); R(EarthPlan.Scree, top: HG, depth: 2, rock: HG); R(EarthPlan.Cliffs, top: K);
		R(EarthPlan.Snowfield, top: EarthIds.SnowBlock, depth: 2, rock: EarthIds.SnowBlock, rockDepth: 2);
		R(EarthPlan.PeakGlacier, top: GlacierIce, depth: 5, rock: GlacierIce, rockDepth: 5);
		// water and coasts
		R(EarthPlan.Floodplain); R(EarthPlan.Canyon, top: K); R(EarthPlan.Delta, top: EarthIds.Silt, patch: G, cut: 0.55);
		R(EarthPlan.ReedBed, patch: EarthIds.Mud, cut: 0.6); R(EarthPlan.BogEdge, top: BogMoss);
		R(EarthPlan.SandBeach, top: EarthIds.Sand, depth: 2); R(EarthPlan.Shingle, top: LG, depth: 2); R(EarthPlan.SeaCliffs, top: K);
		R(EarthPlan.Dunes, top: EarthIds.Sand, depth: 3); R(EarthPlan.SaltMarsh, top: EarthIds.Mud, patch: EarthIds.Silt, cut: 0.5);
		R(EarthPlan.StiltSwamp, top: EarthIds.Mud);
		R(EarthPlan.Reef, top: ReefStone, rock: ReefStone, sea: true); R(EarthPlan.WeedBeds, top: K);
		R(EarthPlan.SeaIce, top: K); R(EarthPlan.DeepSea, top: LG, patch: Clay, cut: 0.55, sea: true); R(EarthPlan.Trench, top: K);
		// what erosion left
		R(EarthPlan.FjordS, top: K); R(EarthPlan.TroughS); R(EarthPlan.CirqueS, top: K); R(EarthPlan.MoraineS, patch: LG, cut: 0.65);
		R(EarthPlan.Erratics); R(EarthPlan.Fan, patch: LG, cut: 0.6); R(EarthPlan.Mesas, top: K); R(EarthPlan.Tors);
		R(EarthPlan.Gullies, top: K); R(EarthPlan.KarstS); R(EarthPlan.TowerKarst); R(EarthPlan.LoessHills); R(EarthPlan.Yardangs, top: K);
		R(EarthPlan.Platform, top: K); R(EarthPlan.FogCoast);
		return d;
	}
}

// the trees by sub-biome and biome (pass 5; to become worldgen.json's EARTH_TREES once agreed): the kinds and their shares, and the
// density (a tree's chance at each block). Draft 2's table; in 0.1.5.5 the new woods (Mahogany, Acacia, Stiltroot, Teak) took
// the stand-ins' places, and burnt trees stand in the old burns and fire scars.
// A sub-biome without a row uses its biome's; a row with density 0 has none (a clearing).
public sealed class TreeRow
{
	public string[] Names; public double[] Shares; public double Density;
	public string Pick(double p)
	{
		for (int k = 0; k < Names.Length; k++) { if (p < Shares[k]) return Names[k]; p -= Shares[k]; }
		return Names[^1];
	}
	static readonly Dictionary<int, TreeRow> Rows = Make();
	public static TreeRow Of(int sub, int biome) => Rows.TryGetValue(sub, out var r) || Rows.TryGetValue(biome, out r) ? (r.Density > 0 ? r : null) : null;
	static Dictionary<int, TreeRow> Make()
	{
		var d = new Dictionary<int, TreeRow>();
		void R(int n, double density, params (string, double)[] mix) =>
			d[n] = new TreeRow { Names = Array.ConvertAll(mix, m => m.Item1), Shares = Array.ConvertAll(mix, m => m.Item2), Density = density };
		// biomes
		R(EarthPlan.Woods, 0.03, ("oak", 0.45), ("maple", 0.25), ("birch", 0.15), ("cedar", 0.15));
		R(EarthPlan.Plains, 0.0015, ("prairie_oak", 0.7), ("shrub", 0.3));
		R(EarthPlan.Desert, 0.0006, ("palo_verde", 0.5), ("dead", 0.5));
		R(EarthPlan.Snowy, 0.02, ("fir", 0.6), ("larch", 0.25), ("holly", 0.15));
		R(EarthPlan.Swamp, 0.012, ("willow", 0.6), ("cypress", 0.4));
		R(EarthPlan.Taiga, 0.035, ("fir", 0.55), ("larch", 0.2), ("birch", 0.15), ("aspen", 0.1));
		R(EarthPlan.Meadow, 0.002, ("cherry", 0.6), ("prairie_oak", 0.4));
		R(EarthPlan.Badlands, 0.002, ("juniper", 0.6), ("dead", 0.4));
		R(EarthPlan.Rainforest, 0.04, ("mahogany", 0.55), ("palm", 0.45));
		R(EarthPlan.Savanna, 0.002, ("acacia", 1));
		R(EarthPlan.Alpine, 0.004, ("fir", 0.5), ("larch", 0.5));
		R(EarthPlan.DryScrub, 0.006, ("juniper", 0.4), ("cedar", 0.2), ("holly", 0.2), ("shrub", 0.2));
		R(EarthPlan.DryForest, 0.015, ("teak", 0.75), ("acacia", 0.25));
		// sub-biomes with trees of their own
		R(EarthPlan.ForestTundra, 0.004, ("fir", 0.6), ("larch", 0.4));
		R(EarthPlan.LichenWoods, 0.008, ("fir", 0.7), ("larch", 0.3));
		R(EarthPlan.LarchStand, 0.03, ("larch", 1));
		R(EarthPlan.Muskeg, 0.004, ("fir", 0.5), ("larch", 0.5));
		R(EarthPlan.OldBurn, 0.008, ("birch", 0.35), ("aspen", 0.35), ("burnt", 0.3));
		R(EarthPlan.OldGrowth, 0.04, ("oak", 0.4), ("maple", 0.3), ("cedar", 0.3));
		R(EarthPlan.BirchGrove, 0.035, ("birch", 0.6), ("aspen", 0.4));
		R(EarthPlan.Clearing, 0);
		R(EarthPlan.Parkland, 0.004, ("prairie_oak", 0.7), ("oak", 0.3));
		R(EarthPlan.Tallgrass, 0.0008, ("prairie_oak", 1)); R(EarthPlan.Shortgrass, 0.0005, ("prairie_oak", 0.5), ("shrub", 0.5));
		R(EarthPlan.Steppe, 0.001, ("shrub", 1)); R(EarthPlan.Wildflowers, 0);
		R(EarthPlan.WetMeadow, 0.002, ("willow", 1));
		R(EarthPlan.ReedMarsh, 0); R(EarthPlan.SwampForest, 0.03, ("willow", 0.5), ("cypress", 0.5)); R(EarthPlan.Fen, 0.002, ("larch", 1));
		R(EarthPlan.RockyScrub, 0.004, ("juniper", 1)); R(EarthPlan.FireScar, 0.003, ("shrub", 0.6), ("burnt", 0.4));
		R(EarthPlan.CloudForest, 0.02, ("oak", 1));
		R(EarthPlan.FloodForest, 0.03, ("mahogany", 0.5), ("palm", 0.5));
		R(EarthPlan.WoodedSavanna, 0.006, ("acacia", 0.8), ("teak", 0.2)); R(EarthPlan.Waterhole, 0);
		R(EarthPlan.ThornScrub, 0.002, ("shrub", 0.5), ("dead", 0.5));
		R(EarthPlan.DuneSea, 0); R(EarthPlan.Pavement, 0); R(EarthPlan.Oasis, 0.02, ("palm", 1)); R(EarthPlan.DryRiverbed, 0.002, ("dead", 1));
		R(EarthPlan.SaltFlats, 0); R(EarthPlan.CrackedBed, 0); R(EarthPlan.SandSea, 0);
		R(EarthPlan.Treeline, 0.006, ("fir", 0.5), ("larch", 0.3), ("juniper", 0.2));
		R(EarthPlan.Scree, 0); R(EarthPlan.Cliffs, 0); R(EarthPlan.Snowfield, 0); R(EarthPlan.PeakGlacier, 0);
		R(EarthPlan.Floodplain, 0.006, ("oak", 0.5), ("willow", 0.5)); R(EarthPlan.Delta, 0.002, ("willow", 1));
		R(EarthPlan.ReedBed, 0); R(EarthPlan.BogEdge, 0); R(EarthPlan.SandBeach, 0); R(EarthPlan.Shingle, 0); R(EarthPlan.SeaCliffs, 0);
		R(EarthPlan.Dunes, 0); R(EarthPlan.SaltMarsh, 0); R(EarthPlan.StiltSwamp, 0.03, ("stiltroot", 1));
		return d;
	}
}

// (Beta 0.1.5.5) the ground plants by sub-biome and biome (P; desktop draft 1, agreed with web): the plants and their shares of
// the columns, rolled once a column, in this order; Flowers (-1) is the 5 Flowers 194-198 at a fifth of its share each, in that
// order. A sub-biome without a row uses its biome's; none: no plants
public static class CoverRow
{
	const int ShortGrass = 3945, TallGrass = 3946, Bunch = 3948, GreyShrub = 3949, Fern = 3950, TreeFern = 3951, Vines = 3952, Reeds = 3953,
		Pond = 3955, Dune = 3956, SeaWeed = 3957, ReefFan = 3959, Thorn = 3960, TundraShrub = 3940, Cotton = 3941, Fireweed = 3942,
		Gentian = 3943, Edelweiss = 3944, Flowers = -1;
	static readonly Dictionary<int, (int id, double share)[]> Rows = Make();
	public static (int id, double share)[] Of(int sub, int biome) => Rows.TryGetValue(sub, out var r) || Rows.TryGetValue(biome, out r) ? r : null;
	static Dictionary<int, (int, double)[]> Make()
	{
		var d = new Dictionary<int, (int, double)[]>();
		void R(int n, params (int id, double share)[] mix)
		{
			var l = new List<(int, double)>();
			foreach (var (id, share) in mix) if (id == Flowers) for (int f = 194; f <= 198; f++) l.Add((f, share / 5)); else l.Add((id, share));
			d[n] = l.ToArray();
		}
		// biomes (desert, badlands, snowy lands, glacier, bare rock, snow peaks, lakes and both wastes: none)
		R(EarthPlan.Woods, (ShortGrass, 0.25), (Fern, 0.04), (Flowers, 0.02));
		R(EarthPlan.Plains, (ShortGrass, 0.3), (TallGrass, 0.15), (Flowers, 0.02));
		R(EarthPlan.Taiga, (ShortGrass, 0.08), (Fern, 0.06));
		R(EarthPlan.Meadow, (ShortGrass, 0.35), (TallGrass, 0.05), (Flowers, 0.05));
		R(EarthPlan.Swamp, (ShortGrass, 0.15), (Reeds, 0.06), (Pond, 0.08));
		R(EarthPlan.Tundra, (TundraShrub, 0.06), (Cotton, 0.02));
		R(EarthPlan.Rainforest, (Fern, 0.2), (TreeFern, 0.04), (Vines, 0.03));
		R(EarthPlan.Savanna, (ShortGrass, 0.2), (TallGrass, 0.15));
		R(EarthPlan.Alpine, (ShortGrass, 0.25), (Gentian, 0.02), (Edelweiss, 0.01));
		R(EarthPlan.DryScrub, (GreyShrub, 0.06), (Bunch, 0.06));
		R(EarthPlan.DryForest, (ShortGrass, 0.12), (Bunch, 0.05));
		R(EarthPlan.Ocean, (SeaWeed, 0.02));
		// cold and boreal
		foreach (int n in new[] { EarthPlan.LichenField, EarthPlan.FrostCracks, EarthPlan.ForestTundra, EarthPlan.LichenWoods }) R(n, (TundraShrub, 0.06), (Cotton, 0.02));
		foreach (int n in new[] { EarthPlan.ThawBogs, EarthPlan.Muskeg, EarthPlan.BogEdge }) R(n, (Cotton, 0.12));
		R(EarthPlan.Fen, (Reeds, 0.12), (Cotton, 0.06), (Pond, 0.1));
		R(EarthPlan.OldBurn, (Fireweed, 0.2), (ShortGrass, 0.1));
		// temperate and grassland
		R(EarthPlan.OldGrowth, (Fern, 0.15), (ShortGrass, 0.1));
		R(EarthPlan.BirchGrove, (ShortGrass, 0.25), (Fern, 0.03));
		R(EarthPlan.Clearing, (ShortGrass, 0.4), (Flowers, 0.08));
		R(EarthPlan.FernRavine, (Fern, 0.35));
		R(EarthPlan.Parkland, (ShortGrass, 0.35), (TallGrass, 0.05), (Flowers, 0.01));
		R(EarthPlan.Tallgrass, (TallGrass, 0.45), (ShortGrass, 0.1), (Flowers, 0.03));
		R(EarthPlan.Shortgrass, (ShortGrass, 0.4), (Bunch, 0.1));
		R(EarthPlan.Steppe, (Bunch, 0.25), (GreyShrub, 0.05));
		R(EarthPlan.Wildflowers, (Flowers, 0.3), (ShortGrass, 0.2));
		R(EarthPlan.WetMeadow, (ShortGrass, 0.3), (Reeds, 0.1));
		R(EarthPlan.Treeline, (ShortGrass, 0.15), (Edelweiss, 0.01));
		// wetlands and shores
		R(EarthPlan.ReedMarsh, (Reeds, 0.3), (Pond, 0.15));
		R(EarthPlan.ReedBed, (Reeds, 0.25), (Pond, 0.1));
		R(EarthPlan.SwampForest, (ShortGrass, 0.1), (Fern, 0.05), (Pond, 0.05));
		R(EarthPlan.Floodplain, (ShortGrass, 0.25), (Reeds, 0.06));
		R(EarthPlan.Delta, (Reeds, 0.15));
		R(EarthPlan.SaltMarsh, (Reeds, 0.08));
		R(EarthPlan.Waterhole, (Reeds, 0.15), (Pond, 0.1));
		// tropical
		R(EarthPlan.FernUnder, (Fern, 0.3), (TreeFern, 0.15));
		R(EarthPlan.FloodForest, (Fern, 0.1), (Reeds, 0.04), (TreeFern, 0.03));
		R(EarthPlan.CloudForest, (Fern, 0.15), (TreeFern, 0.06), (Vines, 0.08));
		R(EarthPlan.WoodedSavanna, (ShortGrass, 0.2), (TallGrass, 0.15));
		// dry, and the sea
		R(EarthPlan.RockyScrub, (GreyShrub, 0.05));
		R(EarthPlan.FireScar, (Fireweed, 0.08), (Bunch, 0.08));
		R(EarthPlan.ThornScrub, (Thorn, 0.05), (Bunch, 0.08));
		R(EarthPlan.Oasis, (ShortGrass, 0.3));
		R(EarthPlan.Dunes, (Dune, 0.15));
		R(EarthPlan.WeedBeds, (SeaWeed, 0.2));
		R(EarthPlan.Reef, (ReefFan, 0.15));
		return d;
	}
}

// the ids the column drawing uses that today's blocks already have (the new ones are written as numbers above, from the rows)
public static class EarthIds
{
	public const int Grass = 1, Dirt = 2, Stone = 3, Sand = 6, Gravel = 11, Water = 50, Sandstone = 97, SnowyGrass = 98, Ice = 99, Bedrock = 120,
		CopperOre = 142, IronOre = 143, GoldOre = 144, TinOre = 145, Mud = 203, Peat = 205, Snow = 380, SnowBlock = 387, Silt = 3930,
		SwampGrass = 58;
}
