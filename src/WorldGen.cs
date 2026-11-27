using System;
using System.Collections.Generic;

namespace Cubeland;

// Terrain generation: the same maths as the web version, so the same seed grows the same world.
public class WorldGen
{
	readonly World W;
	readonly long seed;
	readonly int gen;
	public int Land;   // the land's version (see World.LandVersion): 1 = natural structures, and trunks up into the leaves
	const byte RAW = 255;   // "land, decide grass/dirt/stone later"
	static int Lift => World.Lift;   // (tall worlds: the land is lifted this far, with rock below)
	public int SeaLevel => Lift + (gen == 1 ? 38 : 63);

	public WorldGen(World w, long seed, int gen) { W = w; this.seed = seed; this.gen = gen; }

	// ---- noise (matches the JavaScript bit for bit: Math.imul and >>> become unchecked int maths) ----
	static int Imul(long a, int b) => unchecked((int)a * b);
	public double Hash(int x, int z, int s = 0)
	{
		int h = unchecked(Imul(x, 374761393) + Imul(z, 668265263) + Imul(seed + s, 1442695041));
		h = Imul(h ^ (int)((uint)h >> 13), 1274126177);
		h ^= (int)((uint)h >> 16);
		return (uint)h / 4294967296.0;
	}
	double Hash3(int x, int y, int z, int s)
	{
		int h = unchecked(Imul(x, 374761393) + Imul(y, 1103515245) + Imul(z, 668265263) + Imul(seed + s, 1442695041));
		h = Imul(h ^ (int)((uint)h >> 13), 1274126177);
		h ^= (int)((uint)h >> 16);
		return (uint)h / 4294967296.0;
	}
	public double ValueNoise(double x, double z, int s)
	{
		int xi = (int)Math.Floor(x), zi = (int)Math.Floor(z);
		double fx = x - xi, fz = z - zi;
		double u = fx * fx * (3 - 2 * fx), v = fz * fz * (3 - 2 * fz);
		double a = Hash(xi, zi, s), b = Hash(xi + 1, zi, s), c = Hash(xi, zi + 1, s), d = Hash(xi + 1, zi + 1, s);
		return (a + (b - a) * u) + ((c + (d - c) * u) - (a + (b - a) * u)) * v;
	}
	public double Fbm(double x, double z, int oct, int s)
	{
		double sum = 0, amp = 0.5, f = 1;
		for (int i = 0; i < oct; i++) { sum += ValueNoise(x * f, z * f, s + i * 17) * amp; amp *= 0.5; f *= 2; }
		return sum;
	}
	static double Smoothstep(double a, double b, double v) { double t = Math.Max(0, Math.Min(1, (v - a) / (b - a))); return t * t * (3 - 2 * t); }
	double Noise3(double x, double y, double z, int s)
	{
		int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y), zi = (int)Math.Floor(z);
		double fx = x - xi, fy = y - yi, fz = z - zi;
		fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
		static double L(double a, double b, double t) => a + (b - a) * t;
		return L(
			L(L(Hash3(xi, yi, zi, s), Hash3(xi + 1, yi, zi, s), fx), L(Hash3(xi, yi + 1, zi, s), Hash3(xi + 1, yi + 1, zi, s), fx), fy),
			L(L(Hash3(xi, yi, zi + 1, s), Hash3(xi + 1, yi, zi + 1, s), fx), L(Hash3(xi, yi + 1, zi + 1, s), Hash3(xi + 1, yi + 1, zi + 1, s), fx), fy),
			fz);
	}

	byte[] world => W.Raw;   // (the world grows one byte per block; World packs it into chunks afterwards)
	static int Idx(int x, int y, int z) => (int)World.Idx(x, y, z);   // (gens 1 to 3 grow a whole world at once, one byte a block: never past an int)
	static int WX => World.WX; static int WZ => World.WZ;
	static int WY => World.WY;
	void Put(int x, int y, int z, int id, bool onlyAir = false)
	{
		if (x < 0 || z < 0 || x >= WX || z >= WZ || y < 0 || y >= WY) return;
		int i = Idx(x, y, z);
		if (onlyAir && world[i] != 0) return;
		world[i] = (byte)id;
	}

	public Action<float> Progress;

	// Blocks numbered 256 and up (lying logs, snow): the world grows one byte per block, so `stand` goes in for now, and the
	// real block takes its place once the world is packed, if nothing else has been put there since (see World.Generate).
	public readonly List<(int i, int id, byte stand)> Late = new();

	// --- Biome worlds (gen 3): two slow climate maps, warmth and wetness, pick each spot's biome ---
	public const int Woods = 0, Plains = 1, Desert = 2, Snowy = 3, Swamp = 4, Taiga = 5, Meadow = 6, Badlands = 7;
	// the climate borders (as the web version). Warmth: below COLD snowy, then up to COOL taiga, above HOT hot lands (desert where
	// wetness is below ARID, badlands up to BADLANDS, then woods). In between: swamp above WET, plains below DRY, meadow up to MEADOW, then woods.
	const double COLD = 0.33, COOL = 0.38, HOT = 0.54, ARID = 0.48, BADLANDS = 0.58, DRY = 0.38, MEADOW = 0.45, WET = 0.53;
	public double BiomeScale = 1;   // how big the biomes are: the climate maps are stretched by this (see World.ValidBiomes)
	double WarmthAt(double x, double z) => Fbm(x / (180 * BiomeScale) + 300, z / (180 * BiomeScale) + 300, 3, 1100);
	double WetnessAt(double x, double z) => Fbm(x / (150 * BiomeScale) + 600, z / (150 * BiomeScale) + 600, 3, 1110);
	public int BiomeAt(int x, int z)
	{
		double t = WarmthAt(x, z), w = WetnessAt(x, z);
		if (t < COLD) return Snowy;
		if (t < COOL) return Taiga;
		if (t > HOT) return w < ARID ? Desert : w < BADLANDS ? Badlands : Woods;
		return w > WET ? Swamp : w < DRY ? Plains : w < MEADOW ? Meadow : Woods;
	}
	// how much each biome holds a spot (0-1), fading over a short way (edge e) at the borders, so the land's shape and colours change smoothly
	(double snowy, double taiga, double desert, double badlands, double swamp, double plains, double meadow) Climate(double x, double z, double e)
	{
		double t = WarmthAt(x, z), w = WetnessAt(x, z);
		double cold = 1 - Smoothstep(COLD - e, COLD + e, t), cool = 1 - Smoothstep(COOL - e, COOL + e, t), hot = Smoothstep(HOT - e, HOT + e, t);
		double arid = 1 - Smoothstep(ARID - e, ARID + e, w), bad = 1 - Smoothstep(BADLANDS - e, BADLANDS + e, w);
		double dry = 1 - Smoothstep(DRY - e, DRY + e, w), mead = 1 - Smoothstep(MEADOW - e, MEADOW + e, w), wet = Smoothstep(WET - e, WET + e, w);
		double mild = 1 - cool;   // (warmer than the taiga)
		return (cold, cool - cold, mild * hot * arid, mild * hot * (1 - arid) * bad, mild * (1 - hot) * wet, mild * (1 - hot) * dry, mild * (1 - hot) * (1 - dry) * mead);
	}
	// the colour grass and leaves take on here (multiplies their green pixels; 1,1,1 = as drawn). Fades over a wider way than
	// the land's shape so colours blend between biomes. Only in Biome worlds.
	public Godot.Color GrassTint(double x, double z)
	{
		if (gen != 3) return new Godot.Color(1, 1, 1);
		var c = Climate(x, z, 0.05);   // (snowy bluer, taiga a cool blue-green, desert and badlands yellower, swamp murkier, plains brighter, meadow fresh green)
		return new Godot.Color((float)(1 - 0.20 * c.snowy - 0.16 * c.taiga + 0.30 * c.desert + 0.24 * c.badlands - 0.10 * c.swamp + 0.08 * c.plains - 0.02 * c.meadow),
		                 (float)(1 - 0.08 * c.snowy - 0.04 * c.taiga + 0.02 * c.desert + 0.00 * c.badlands - 0.10 * c.swamp + 0.06 * c.plains + 0.08 * c.meadow),
		                 (float)(1 + 0.30 * c.snowy + 0.17 * c.taiga - 0.30 * c.desert - 0.30 * c.badlands - 0.25 * c.swamp - 0.15 * c.plains - 0.06 * c.meadow));
	}
	byte[] biomeMap = new byte[WX * WZ];

	public void Generate()
	{
		if (gen == 3) System.Threading.Tasks.Parallel.For(0, WZ, z => { for (int x = 0; x < WX; x++) biomeMap[z * WX + x] = (byte)BiomeAt(x, z); });
		if (gen == 1) for (int z = 0; z < WZ; z++) for (int x = 0; x < WX; x++)
		{
			int h0 = GroundHeight(x, z);
			for (int y = 0; y < h0; y++) world[Idx(x, y, z)] = RAW;
		}
		else GenerateHills();
		Progress?.Invoke(0.35f);
		W.RecomputeHeights();
		if (gen == 1) Landmarks();
		Paint();
		W.RecomputeHeights();
		Gravel();
		Progress?.Invoke(0.55f);
		// Biome worlds: swamps get pools, low spots dug out of the boggy ground (the water below fills them)
		if (gen == 3) for (int z = 0; z < WZ; z++) for (int x = 0; x < WX; x++)
		{
			if (biomeMap[z * WX + x] != Swamp || world[Idx(x, SeaLevel + 2, z)] != 0 || Fbm(x / 12.0 + 900, z / 12.0 + 900, 2, 1120) < 0.5) continue;
			for (int y = SeaLevel; y <= SeaLevel + 1; y++) world[Idx(x, y, z)] = 0;
			if (W.Heights[z * WX + x] > SeaLevel) W.Heights[z * WX + x] = SeaLevel;
			if (Blocks.IsGrassy(world[Idx(x, SeaLevel - 1, z)])) world[Idx(x, SeaLevel - 1, z)] = Blocks.Mud;   // (a muddy bottom)
		}
		// Water: every low spot open to the sky fills up to sea level
		for (int z = 0; z < WZ; z++) for (int x = 0; x < WX; x++)
			for (int y = SeaLevel; y > 0 && world[Idx(x, y, z)] == 0; y--) world[Idx(x, y, z)] = Blocks.Water;
		PlaceOres();
		if (gen >= 2) CarveCaves();
		// Biome worlds: in snowy lands the top of open water (seas, lakes) freezes into Ice
		if (gen == 3) for (int z = 0; z < WZ; z++) for (int x = 0; x < WX; x++)
		{
			int i = Idx(x, SeaLevel, z);
			if (biomeMap[z * WX + x] == Snowy && Blocks.IsWater(world[i]) && world[Idx(x, SeaLevel + 1, z)] == 0) world[i] = Blocks.Ice;
		}
		Strata();
		Progress?.Invoke(0.75f);
		Trees();
		Rocks();
		Flowers();
		if (gen == 3 && World.WildCrops(Land)) { WildCrops(); DeadBushes(); }
		if (gen == 3 && Land >= 1) Structures();
		if (gen == 3 && Land >= 1) SnowCover();
		if (gen == 3 && HasStairs) Staircases();
		Progress?.Invoke(0.9f);
	}

	// --- Staircases (lands 5 and 6, Biomes worlds): a short flight of stairs on its own, in a forest high above the sea ---
	// Spots are tried on a grid; the few that get a staircase are picked by hash, far apart. The rest are kept as spare spots
	// (see Main.Stairs). Exactly as the web version, so both grow the same world.
	// EerieReady: while false, new worlds are made without staircases (land 3 or 4) and Main.Eerie and Main.Stairs' events
	// don't run. It must match EERIE_READY in the web game's index.html. (Test: `--eerieon` turns it on anyway.)
	public const bool EerieReady = true;
	public static readonly bool EerieOn = EerieReady || Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--eerieon") >= 0;
	// Realistic worlds ("Earthlike", world type 4, from land 7) are held back for Beta 0.2.0.0: while EarthlikeReady is false none
	// can be made, and everything built for them stays off. It must match EARTHLIKE_READY in the web game. (Test: `--earthlike`
	// turns it on anyway.) The other world types never read it: they grow exactly the same land with it on or off.
	public const bool EarthlikeReady = false;
	public static readonly bool EarthlikeOn = EarthlikeReady || Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--earthlike") >= 0;
	public class StairSpot { public int X, Y, Z, Dir, N, Mat; public double Order; public bool Placed; }
	// an older Biomes world given the staircases (see Worlds.OldStairs): no spot within 2 chunks of a chunk in its skip set
	public bool Os; HashSet<int> osSkip;
	public bool HasStairs => World.Staircases(Land) || Os;
	public void OldStairs(int[] skip) { Os = true; osSkip = new HashSet<int>(skip ?? Array.Empty<int>()); }
	// is a chunk in this set within 2 chunks (a 5 x 5 square) of the chunk at x, z?
	public static bool NearChunks(HashSet<int> chunks, int x, int z)
	{
		if (chunks == null || chunks.Count == 0) return false;
		int n = World.WX / World.CS, m = World.WZ / World.CS, cx = x / World.CS, cz = z / World.CS;
		for (int dz = -2; dz <= 2; dz++) for (int dx = -2; dx <= 2; dx++)
		{
			int a = cx + dx, b = cz + dz;
			if (a >= 0 && b >= 0 && a < n && b < m && chunks.Contains(b * n + a)) return true;
		}
		return false;
	}
	public readonly List<StairSpot> StairSpots = new();
	public const int StairMinUp = 6;    // how far above the sea the ground must be at least (they go on the highest forest ground there is)
	public static readonly int[][] StairDirs = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } };
	public static readonly int[] StairMasks = { 0x33 | 0x88, 0x33 | 0x44, 0x33 | 0xC0, 0x33 | 0x0C };   // (the step's high half faces the way it climbs)
	public static int StairTop(StairSpot s, int k) => s.Y + k;   // (step k's block height)
	public bool StairForest(int x, int z) => Forest(x, z);
	public int StairMat(int x, int z) => Hash(x, z, 7704) < 1 / 3.0 ? Blocks.Cobblestone : biomeMap[z * WX + x] == Taiga ? Blocks.FirPlanks : Blocks.Planks;
	bool Forest(int x, int z) => x >= 0 && z >= 0 && x < WX && z < WZ && (biomeMap[z * WX + x] == Woods || biomeMap[z * WX + x] == Taiga) && W.Heights[z * WX + x] > SeaLevel;
	bool IsTrunk(int b) => b == Blocks.Log || b == Blocks.FirLog;
	// (also used later, on the grown world: can a staircase stand here? ground from `at`, blocks from `get`)
	public static bool StairFits(StairSpot s, Func<int, int> groundAt, Func<int, int, int, int> get)
	{
		var d = StairDirs[s.Dir];
		for (int k = 0; k < s.N; k++)
		{
			int cx = s.X + d[0] * k, cz = s.Z + d[1] * k;
			if (cx < 1 || cz < 1 || cx >= WX - 1 || cz >= WZ - 1) return false;
			int gk = groundAt(cz * WX + cx), top = get(cx, gk, cz);
			if (gk != s.Y - 1 && gk != s.Y - 2) return false;                          // (on level ground: a block lower at most)
			if (top != Blocks.Grass && top != Blocks.Dirt && top != Blocks.SnowyGrass) return false;
			for (int y = gk + 1; y <= s.Y + k + 3; y++) if (get(cx, y, cz) != 0) return false;   // (nothing in the way, and room above)
		}
		return true;
	}
	void Staircases()
	{
		const int Cell = 16;
		for (int gz = 0; gz < WZ / Cell; gz++) for (int gx = 0; gx < WX / Cell; gx++)
		{
			int x = gx * Cell + 2 + (int)Math.Floor(Hash(gx, gz, 7700) * 12), z = gz * Cell + 2 + (int)Math.Floor(Hash(gx, gz, 7701) * 12);
			if (NearChunks(osSkip, x, z)) continue;   // (an older world: not near what had changed in it)
			if (!Forest(x, z)) continue;
			int g = W.Heights[z * WX + x];
			if (g < SeaLevel + StairMinUp) continue;
			bool deep = true;   // deep in the forest: 8 blocks out every way is still forest
			foreach (var e in PalmFronds) if (!Forest(x + e[0] * 8, z + e[1] * 8)) { deep = false; break; }
			if (!deep) continue;
			int trunks = 0;     // and trees nearby
			for (int dz = -6; dz <= 6; dz++) for (int dx = -6; dx <= 6; dx++)
			{
				int c = (z + dz) * WX + x + dx;
				if (IsTrunk(world[Idx(x + dx, W.Heights[c] + 1, z + dz)])) trunks++;
			}
			if (trunks < 2) continue;
			var s = new StairSpot { X = x, Y = g + 1, Z = z, N = 5 + (int)Math.Floor(Hash(x, z, 7702) * 4), Order = Hash(x, z, 7705) };
			s.Mat = Hash(x, z, 7704) < 1 / 3.0 ? Blocks.Cobblestone : biomeMap[z * WX + x] == Taiga ? Blocks.FirPlanks : Blocks.Planks;
			int d0 = (int)Math.Floor(Hash(x, z, 7703) * 4); bool fits = false;   // (a random way first, then the others)
			for (int t = 0; t < 4 && !fits; t++) { s.Dir = (d0 + t) % 4; fits = StairFits(s, c => W.Heights[c], (a, b, c) => world[Idx(a, b, c)]); }
			if (!fits) continue;
			StairSpots.Add(s);
		}
		// the highest first (the staircases stand in the world's highest forests), then by hash
		StairSpots.Sort((a, b) => a.Y != b.Y ? b.Y - a.Y : a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.X != b.X ? a.X - b.X : a.Z - b.Z);
		int want = WX * WZ / 320000 + 1;   // (6 in a 1280 world, 2 in a 640 one)
		var placed = new List<StairSpot>();
		foreach (var s in StairSpots)
		{
			if (placed.Count >= want) break;
			if (placed.Exists(p => Math.Abs(p.X - s.X) + Math.Abs(p.Z - s.Z) < WX / 5)) continue;   // (far apart)
			BuildStair(s, (x, y, z, id) => world[Idx(x, y, z)] = (byte)id, c => W.Heights[c]);
			s.Placed = true; placed.Add(s);
		}
	}
	// the staircase itself: each step is a stairs piece, standing on a solid base of the same block down to the ground
	public void BuildStair(StairSpot s, Action<int, int, int, int> set, Func<int, int> groundAt)
	{
		var d = StairDirs[s.Dir];
		for (int k = 0; k < s.N; k++)
		{
			int cx = s.X + d[0] * k, cz = s.Z + d[1] * k, gk = groundAt(cz * WX + cx);
			for (int y = gk + 1; y < s.Y + k; y++) set(cx, y, cz, s.Mat);
			var a = new int[8];
			for (int b = 0; b < 8; b++) if ((StairMasks[s.Dir] & (1 << b)) != 0) a[b] = s.Mat;
			W.PieceMap[Idx(cx, s.Y + k, cz)] = a;
			set(cx, s.Y + k, cz, Blocks.Pieces);
			W.Heights[cz * WX + cx] = Math.Max(W.Heights[cz * WX + cx], s.Y + k);
		}
	}

	// --- Natural structures (Biomes worlds from Land 1): things the land made by itself. One try in each 64 x 64 cell, at a
	// spot picked by hash; what grows there depends on the biome. Exactly as the web version, so both grow the same world.
	//   Deserts and badlands: stone arches and rock spires (Sandstone, or the badlands' terracotta stripes)
	//   Out at sea (shallow water): sea stacks of stone
	//   Woods, taiga and swamps: fallen giant trees, hollow so you can walk through
	//   Plains and meadows: sinkholes down into the ground, with a pool at the bottom
	//   Taiga and snowy lands: fields of boulders
	//   Swamps: hollows, mounds of peat and mud with a dead willow trunk, ringed by still water
	const int StructCell = 64;
	public readonly List<string> Built = new();   // (what was built where, for testing: printed with --worldhash)
	bool Plant(int b) => b == 0 || Blocks.IsLeaves(b) || b == Blocks.Log || b == Blocks.FirLog || b == Blocks.PalmLog || b == Blocks.WillowLog || Blocks.IsFlower(b) || b == Blocks.RockBlock
		|| b == WildStand;   // (a wild crop or a Dead Bush, from land 5: see WildCrops)
	// the ground under a spot (under any trees, flowers or rocks; the top of the water at sea)
	int GroundAt(int x, int z) { int y = W.Heights[z * WX + x]; while (y > 0 && Plant(world[Idx(x, y, z)])) y--; return y; }
	bool Inside(int x, int z, int m) => x >= m && z >= m && x < WX - m && z < WZ - m;
	// place a structure's block where there's only air or plants (or water too, out at sea)
	void PutOver(int x, int y, int z, int id, bool wet = false)
	{
		if (x < 0 || z < 0 || x >= WX || z >= WZ || y < 1 || y >= WY - 1) return;
		int i = Idx(x, y, z), b = world[i];
		if (!Plant(b) && !(wet && Blocks.IsWater(b))) return;
		world[i] = (byte)id;
		if (id != 0 && y > W.Heights[z * WX + x]) W.Heights[z * WX + x] = y;
	}
	int RockOf(int bi, int y) { if (bi != Badlands) return Blocks.Sandstone; int n = BadlandsBands.Length, k = y % n; return BadlandsBands[k < 0 ? k + n : k]; }
	void Structures()
	{
		for (int gz = 0; gz < WZ / StructCell; gz++) for (int gx = 0; gx < WX / StructCell; gx++)
		{
			int x = gx * StructCell + 12 + (int)Math.Floor(Hash(gx, gz, 8100) * (StructCell - 24)), z = gz * StructCell + 12 + (int)Math.Floor(Hash(gx, gz, 8101) * (StructCell - 24));
			double roll = Hash(gx, gz, 8102);
			int bi = biomeMap[z * WX + x], g = GroundAt(x, z);
			bool sea = Blocks.IsWater(world[Idx(x, g, z)]);
			if (sea) { if (bi != Snowy && roll < 0.3) SeaStack(x, z); continue; }
			if (bi == Desert || bi == Badlands) { if (roll < 0.2) Arch(x, z, bi); else if (roll < 0.42) Spire(x, z, bi); }
			else if (bi == Woods) { if (roll < 0.35) FallenGiant(x, z, Blocks.Log); }
			else if (bi == Taiga) { if (roll < 0.3) FallenGiant(x, z, Blocks.FirLog); else if (roll < 0.5) Boulders(x, z); }
			else if (bi == Snowy) { if (roll < 0.3) Boulders(x, z); }
			else if (bi == Swamp) { if (roll < 0.35) Hollow(x, z); else if (roll < 0.5) FallenGiant(x, z, Blocks.WillowLog); }
			else if (bi == Plains || bi == Meadow) { if (roll < 0.15) Sinkhole(x, z); }
		}
	}
	// an arch: half a ring of rock standing on two legs, 3 blocks thick (5 for the big ones), running along x or z
	void Arch(int x, int z, int bi)
	{
		int g = GroundAt(x, z);
		if (g <= SeaLevel + 1) return;
		bool alongX = Hash(x, z, 8110) < 0.5;
		int R = 5 + (int)Math.Floor(Hash(x, z, 8111) * 5), H = 6 + (int)Math.Floor(Hash(x, z, 8112) * 7), w = R >= 8 ? 2 : 1;
		for (int t = -R - 2; t <= R + 2; t++) for (int a = -w; a <= w; a++)
		{
			int cx = alongX ? x + t : x + a, cz = alongX ? z + a : z + t;
			if (!Inside(cx, cz, 2)) continue;
			int lg = GroundAt(cx, cz);
			for (int y = Math.Min(lg + 1, g); y <= g + H + 2; y++)
			{
				int dy = Math.Max(0, y - g);
				double o1 = t / (R + 1.5), o2 = dy / (H + 1.5), i1 = t / (R - 1.5), i2 = dy / (H - 1.5);
				if (o1 * o1 + o2 * o2 <= 1 && i1 * i1 + i2 * i2 > 1) PutOver(cx, y, cz, RockOf(bi, y));
			}
		}
		Built.Add($"arch {x},{g},{z}");
	}
	// a spire: a tall column of rock, narrowing as it goes up, a little lumpy
	void Spire(int x, int z, int bi)
	{
		int g = GroundAt(x, z);
		if (g <= SeaLevel) return;
		int H = 12 + (int)Math.Floor(Hash(x, z, 8120) * 16); double r0 = 2.0 + Hash(x, z, 8121) * 1.4;
		Column(x, z, g, g + H, r0, (yy) => RockOf(bi, yy), false);
		Built.Add($"spire {x},{g},{z}");
	}
	// a sea stack: the same, of stone, standing in shallow sea a little off the shore
	void SeaStack(int x, int z)
	{
		int f = SeaLevel; while (f > 1 && Blocks.IsWater(world[Idx(x, f, z)])) f--;
		if (SeaLevel - f < 1 || SeaLevel - f > 8) return;
		int H = 6 + (int)Math.Floor(Hash(x, z, 8125) * 12); double r0 = 2.4 + Hash(x, z, 8126) * 1.6;
		Column(x, z, f, SeaLevel + H, r0, _ => Blocks.Stone, true);
		Built.Add($"stack {x},{f},{z}");
	}
	void Column(int x, int z, int bottom, int top, double r0, Func<int, int> mat, bool wet)
	{
		for (int dz = -5; dz <= 5; dz++) for (int dx = -5; dx <= 5; dx++)
		{
			int cx = x + dx, cz = z + dz;
			if (!Inside(cx, cz, 2)) continue;
			int lg = wet ? bottom : GroundAt(cx, cz);
			for (int y = Math.Min(lg + 1, bottom); y <= top; y++)
			{
				double f = (double)(Math.Max(y, bottom) - bottom) / (top - bottom);
				double r = r0 * (1 - 0.4 * f) + (Hash(x + y / 3, z, 8122) - 0.5) * 1.0;
				if (dx * dx + dz * dz <= r * r) PutOver(cx, y, cz, mat(y), wet);
			}
		}
	}
	// a fallen giant: a huge old trunk lying on the ground, 5 blocks thick and hollow inside, its roots (a disc of dirt)
	// standing up at one end, and leaves growing here and there along the top
	void FallenGiant(int x, int z, int log)
	{
		int g = GroundAt(x, z);
		if (g < SeaLevel) return;
		bool alongX = Hash(x, z, 8130) < 0.5;
		int L = 15 + (int)Math.Floor(Hash(x, z, 8131) * 11), ay = g + 2;
		for (int k = 0; k < L; k++)   // (only on ground that's fairly level the whole way, and dry)
		{
			int cx = alongX ? x + k : x, cz = alongX ? z : z + k;
			if (!Inside(cx, cz, 4)) return;
			int lg = GroundAt(cx, cz);
			if (lg < g - 2 || lg > g + 2 || Blocks.IsWater(world[Idx(cx, lg, cz)])) return;
		}
		for (int k = 0; k < L; k++) for (int dy = -3; dy <= 3; dy++) for (int a = -3; a <= 3; a++)
		{
			int cx = alongX ? x + k : x + a, cz = alongX ? z + a : z + k, y = ay + dy, d = dy * dy + a * a;
			if (d <= 5)
			{
				if (k > 0 && d <= 1) { int i = Idx(cx, y, cz); if (Plant(world[i])) world[i] = 0; }   // (hollow, open at the far end)
				else { PutOver(cx, y, cz, log); if (y > 0 && y < WY && world[Idx(cx, y, cz)] == log) Late.Add((Idx(cx, y, cz), Blocks.Turned(log, alongX ? 1 : 2), (byte)log)); }   // (lying along its length)
			}
			else if (k == 0 && d <= 11 && dy >= -2) PutOver(cx, y, cz, Blocks.Dirt);   // (the roots)
		}
		for (int k = 1; k < L; k++)
			if (Hash(k, x + z, 8135) < 0.3) { int cx = alongX ? x + k : x, cz = alongX ? z : z + k; PutOver(cx, ay + 3, cz, log == Blocks.FirLog ? Blocks.FirLeaves : log == Blocks.WillowLog ? Blocks.WillowLeaves : Blocks.Leaves); }
		Built.Add($"giant {x},{g},{z}");
	}
	// Snowy lands: one layer of Snow on top of everything open to the sky that snow can lie on (not water, ice or plants),
	// and on the ground under the trees too (snow sifts down through the leaves). A flower there gets snow round it.
	void SnowCover()
	{
		bool Holds(int b) => Blocks.Get(b) is { } d && d.Shape == null && !d.Liquid && !d.NoCollide && b != Blocks.Ice;
		for (int z = 0; z < WZ; z++) for (int x = 0; x < WX; x++)
		{
			if (biomeMap[z * WX + x] != Snowy) continue;
			int y = WY - 2;
			while (y > 0 && world[Idx(x, y, z)] == 0) y--;
			while (y > 0)   // (exactly as the web version, so both grow the same world)
			{
				int b = world[Idx(x, y, z)];
				if (Blocks.IsFlower(b)) { if (Holds(world[Idx(x, y - 1, z)])) Late.Add((Idx(x, y, z), Blocks.WithSnow(b, 1), (byte)b)); break; }
				if (Holds(b) && world[Idx(x, y + 1, z)] == 0) Late.Add((Idx(x, y + 1, z), Blocks.Snow, 0));
				if (!Blocks.IsLeaves(b)) break;   // (the ground, a log, a boulder...: the snow goes no further down)
				y--;
				while (y > 0 && world[Idx(x, y, z)] == 0) y--;   // (down through the leaves and the air under them)
			}
		}
	}
	// a sinkhole: a round hole straight down into the ground, its grassy rim hanging over the edge, a pool at the bottom
	void Sinkhole(int x, int z)
	{
		int g = GroundAt(x, z);
		if (g < SeaLevel + 3) return;
		int R = 3 + (int)Math.Floor(Hash(x, z, 8140) * 4), yb = Math.Max(6, g - 15 - (int)Math.Floor(Hash(x, z, 8141) * 20));
		for (int dz = -R - 2; dz <= R + 2; dz++) for (int dx = -R - 2; dx <= R + 2; dx++)   // (open ground all round: no trees, no water)
		{
			int cx = x + dx, cz = z + dz;
			if (!Inside(cx, cz, 2)) return;
			int lg = GroundAt(cx, cz);
			if (lg < g - 3 || lg > g + 3 || W.Heights[cz * WX + cx] > lg + 1 || Blocks.IsWater(world[Idx(cx, lg, cz)])) return;
		}
		for (int dz = -R - 1; dz <= R + 1; dz++) for (int dx = -R - 1; dx <= R + 1; dx++)
		{
			int cx = x + dx, cz = z + dz, lg = GroundAt(cx, cz);
			double rr = R + (Hash(cx, cz, 8142) - 0.5) * 1.2, d = Math.Sqrt(dx * dx + dz * dz);
			if (d > rr) continue;
			bool open = d <= rr - 1;   // (the top layer of ground reaches a block further in: the rim hangs over)
			for (int y = yb; y <= lg + 2; y++) { if (y == lg && !open) continue; world[Idx(cx, y, cz)] = 0; }
			if (!open) continue;
			if (world[Idx(cx, yb - 1, cz)] != 0) { world[Idx(cx, yb, cz)] = Blocks.Water; W.Heights[cz * WX + cx] = yb; }
			else W.Heights[cz * WX + cx] = yb - 1;
		}
		Built.Add($"sinkhole {x},{g},{z}");
	}
	// a boulder field: big rounded boulders of stone and cobblestone scattered over the ground
	void Boulders(int x, int z)
	{
		int n = 5 + (int)Math.Floor(Hash(x, z, 8150) * 8);
		for (int k = 0; k < n; k++)
		{
			int bx = x + (int)Math.Floor((Hash(k, x, 8151) - 0.5) * 28), bz = z + (int)Math.Floor((Hash(k, z, 8152) - 0.5) * 28);
			if (!Inside(bx, bz, 5)) continue;
			double r = 1.3 + Hash(k, x + z, 8153) * 1.7;
			int bg = GroundAt(bx, bz), R = (int)Math.Ceiling(r);
			if (bg <= SeaLevel || Blocks.IsWater(world[Idx(bx, bg, bz)])) continue;
			int cy = bg + (int)Math.Floor(r * 0.5);
			for (int dy = -R; dy <= R; dy++) for (int dz = -R; dz <= R; dz++) for (int dx = -R; dx <= R; dx++)
			{
				double e = dy * 1.15;
				if (dx * dx + e * e + dz * dz > r * r) continue;
				PutOver(bx + dx, cy + dy, bz + dz, Hash3(bx + dx, cy + dy, bz + dz, 8154) < 0.35 ? Blocks.Cobblestone : Blocks.Stone);
			}
		}
		Built.Add($"boulders {x},{GroundAt(x, z)},{z}");
	}
	// a swamp hollow: a low mound of peat topped with mud, a dead willow trunk standing out of it, and a ring of still water round it
	void Hollow(int x, int z)
	{
		int g = GroundAt(x, z);
		if (g < SeaLevel || g > SeaLevel + 1 || !Inside(x, z, 8) || Blocks.IsWater(world[Idx(x, g, z)])) return;
		double R = 2.5 + Hash(x, z, 8160) * 2; int Hm = 2 + (int)Math.Floor(Hash(x, z, 8161) * 2);
		for (int dz = -6; dz <= 6; dz++) for (int dx = -6; dx <= 6; dx++)
		{
			int cx = x + dx, cz = z + dz, lg = GroundAt(cx, cz);
			double d = Math.Sqrt(dx * dx + dz * dz);
			if (d <= R)
			{
				double q = d / R;
				int mh = (int)Math.Floor((1 - q * q) * Hm + 0.5);
				for (int y = lg + 1; y <= lg + mh; y++) PutOver(cx, y, cz, y == lg + mh ? Blocks.Mud : Blocks.Peat);
			}
			else if (d <= R + 1.5)
			{
				if (lg == SeaLevel + 1) { for (int y = lg; y <= lg + 2; y++) if (y == lg || Plant(world[Idx(cx, y, cz)])) world[Idx(cx, y, cz)] = 0; lg--; W.Heights[cz * WX + cx] = lg; }
				if (lg == SeaLevel && !Blocks.IsWater(world[Idx(cx, lg, cz)])) { world[Idx(cx, lg, cz)] = Blocks.Water; if (Plant(world[Idx(cx, lg + 1, cz)])) world[Idx(cx, lg + 1, cz)] = 0; W.Heights[cz * WX + cx] = lg; }
			}
		}
		int th = 3 + (int)Math.Floor(Hash(x, z, 8162) * 4), top = GroundAt(x, z);
		for (int y = top + 1; y <= top + th; y++) PutOver(x, y, z, Blocks.WillowLog);
		for (int b = 0; b < 2; b++)   // (a broken branch or two)
			if (Hash(x + b, z, 8166) < 0.6) { var d = PalmFronds[(int)Math.Floor(Hash(x, z + b, 8167) * 4)]; PutOver(x + d[0], top + th - 1 - b, z + d[1], Blocks.WillowLog); }
		Built.Add($"hollow {x},{g},{z}");
	}

	// --- Plateaus (the original style): flat steps at many heights, with curvy swirled edges ---
	const int TerraceStep = 5;
	int GroundHeight(int x, int z)
	{
		double wx = x + (Fbm(x / 90.0, z / 90.0, 2, 7) - 0.45) * 110;
		double wz = z + (Fbm(x / 90.0 + 50, z / 90.0 + 50, 2, 8) - 0.45) * 110;
		double region = Fbm(wx / 220, wz / 220, 2, 200);
		double h = 4 + Fbm(wx / 70, wz / 70, 2, 1) * 70 + region * 50;
		return Lift + (int)(Math.Floor(h / TerraceStep) * TerraceStep);
	}

	// --- Rolling hills (the style for new worlds): density worked out on a coarse grid and blended ---
	void GenerateHills()
	{
		const int GX = 4, GY = 8;
		int nx = WX / GX + 1, ny = WY / GY + 1, nz = WZ / GX + 1;
		var dens = new float[nx * ny * nz];
		System.Threading.Tasks.Parallel.For(0, nz, gz =>
		{
			for (int gx = 0; gx < nx; gx++)
			{
				int x = gx * GX, z = gz * GX;
				double land = Fbm(x / 280.0, z / 280.0, 3, 900);
				double bas = SeaLevel + 1 + (land - 0.44) * 120;
				double rough = Smoothstep(0.38, 0.62, Fbm(x / 170.0 + 40, z / 170.0 + 40, 2, 910));
				double amp = 5 + rough * 34;
				if (gen == 3)   // biomes: plains flatten out, meadows roll gently, deserts rise out of the sea and roll gently, badlands rise
				{               // higher into steep hills, swamps sit at sea level and go nearly flat
					var b = Climate(x, z, 0.02);
					amp *= 1 - 0.75 * b.plains - 0.45 * b.meadow - 0.55 * b.desert + 0.5 * b.badlands - 0.8 * b.swamp;
					amp += 16 * b.badlands;   // (badlands are never flat: their striped sides show)
					bas += (Math.Max(bas, SeaLevel + 5) - bas) * b.desert + (Math.Max(bas, SeaLevel + 10) - bas) * b.badlands + (SeaLevel + 0.3 - bas) * b.swamp;
				}
				for (int gy = 0; gy < ny; gy++)
				{
					int y = gy * GY, ny0 = y - Lift;   // (the same shapes as a short world, just lifted)
					double n = Noise3(x / 52.0, ny0 / 34.0, z / 52.0, 920) * 0.6 + Noise3(x / 22.0, ny0 / 15.0, z / 22.0, 930) * 0.3 + Noise3(x / 9.0, ny0 / 8.0, z / 9.0, 940) * 0.1 - 0.5;
					dens[(gy * nz + gz) * nx + gx] = (float)((bas - y) + n * 2 * amp);
				}
			}
		});
		System.Threading.Tasks.Parallel.For(0, WZ, z =>
		{
			var col = new float[ny];
			int gz = z / GX; float fz = (z % GX) / (float)GX;
			for (int x = 0; x < WX; x++)
			{
				int gx = x / GX; float fx = (x % GX) / (float)GX;
				for (int gy = 0; gy < ny; gy++)
				{
					int o = gy * nz;
					float a = dens[(o + gz) * nx + gx], b = dens[(o + gz) * nx + gx + 1];
					float c = dens[(o + gz + 1) * nx + gx], d = dens[(o + gz + 1) * nx + gx + 1];
					col[gy] = (a + (b - a) * fx) * (1 - fz) + (c + (d - c) * fx) * fz;
				}
				for (int y = 0; y < WY - 2; y++)
				{
					int gy = y / GY; float fy = (y % GY) / (float)GY;
					if (col[gy] + (col[gy + 1] - col[gy]) * fy > 0 || y == 0) world[Idx(x, y, z)] = RAW;
				}
			}
		});
	}

	// --- Landmarks (plateau worlds only): mushroom spires, balanced boulders, stone rings, floating cubes ---
	void Landmarks()
	{
		const int CELL = 24;
		for (int cz = 0; cz < WZ; cz += CELL) for (int cx = 0; cx < WX; cx += CELL)
		{
			int x = cx + 4 + (int)Math.Floor(Hash(cx, cz, 1235) * (CELL - 8));
			int z = cz + 4 + (int)Math.Floor(Hash(cx, cz, 1236) * (CELL - 8));
			int y = W.Heights[z * WX + x];
			if (y > Lift + 115) continue;
			double r = Hash(cx, cz, 1234), v = Hash(cx, cz, 1237);
			if (r < 0.10) MushroomSpire(x, y, z, v);
			else if (r < 0.17) BalancedBoulder(x, y, z, v);
			else if (r < 0.22) StoneRing(x, y, z, v);
			else if (r < 0.32) FloatingCubes(x, y, z, v);
		}
	}
	void MushroomSpire(int x, int y, int z, double v)
	{
		int H = 14 + (int)Math.Floor(v * 30), R = 3 + (int)Math.Floor(Hash(x, z, 31) * 5);
		for (int yy = y + 1; yy <= y + H; yy++) for (int d = -1; d <= 1; d++) { Put(x + d, yy, z, 4); Put(x, yy, z + d, 4); }
		for (int dz = -R; dz <= R; dz++) for (int dx = -R; dx <= R; dx++)
		{
			double d = Math.Sqrt(dx * dx + dz * dz) / R;
			if (d > 1) continue;
			int capTop = y + H + (int)Math.Round((1 - d * d) * 3, MidpointRounding.AwayFromZero);
			for (int yy = y + H - 1 + (int)Math.Round(d * 2, MidpointRounding.AwayFromZero); yy <= capTop; yy++) Put(x + dx, yy, z + dz, RAW);
		}
	}
	void BalancedBoulder(int x, int y, int z, double v)
	{
		int H = 5 + (int)Math.Floor(v * 10), R = 3 + (int)Math.Floor(Hash(x, z, 41) * 3);
		for (int yy = y + 1; yy <= y + H; yy++) Put(x, yy, z, 3);
		int cy = y + H + R - 1;
		for (int dy = -R; dy <= R; dy++) for (int dz = -R; dz <= R; dz++) for (int dx = -R; dx <= R; dx++)
			if (dx * dx + dy * dy + dz * dz <= R * R + 1) Put(x + dx, cy + dy, z + dz, RAW);
	}
	void StoneRing(int x, int y, int z, double v)
	{
		int R = 7 + (int)Math.Floor(v * 8); bool alongX = Hash(x, z, 77) < 0.5; int cy = y + R - 3;
		for (int a = -R - 2; a <= R + 2; a++) for (int b = -R - 2; b <= R + 2; b++)
		{
			if (Math.Abs(Math.Sqrt(a * a + b * b) - R) > 1.7) continue;
			for (int t = -1; t <= 1; t++) Put(alongX ? x + a : x + t, cy + b, alongX ? z + t : z + a, 3);
		}
	}
	void FloatingCubes(int x, int y, int z, double v)
	{
		int n = 3 + (int)Math.Floor(v * 5);
		for (int k = 0; k < n; k++)
		{
			int s = 2 + (int)Math.Floor(Hash(x + k, z, 51) * 3);
			int ox = x + (int)Math.Floor((Hash(x, z + k, 52) - 0.5) * 20), oz = z + (int)Math.Floor((Hash(x + k, z + k, 53) - 0.5) * 20);
			int oy = y + 10 + (int)Math.Floor(Hash(k, x + z, 54) * 30);
			for (int dy = 0; dy < s; dy++) for (int dz = 0; dz < s; dz++) for (int dx = 0; dx < s; dx++) Put(ox + dx, oy + dy, oz + dz, RAW);
		}
	}

	// the badlands' stripes, from the bottom up (they repeat): data/worldgen.json's BADLANDS_BANDS
	public static readonly int[] BadlandsBands = Array.ConvertAll(Nums.Worldgen.BADLANDS_BANDS, DataKeys.IdOf);
	// --- Paint the land: grass on top, then dirt, then stone; sand, gravel and clay by the water ---
	void Paint()
	{
		System.Threading.Tasks.Parallel.For(0, WZ, z =>
		{
			for (int x = 0; x < WX; x++)
			{
				int depth = -1, surf = 1;
				double beach = gen == 1 ? 0 : Fbm(x / 40.0, z / 40.0, 2, 950), gravelly = gen == 1 ? 0 : Fbm(x / 30.0 + 70, z / 30.0 + 70, 2, 960);
				double clayey = gen == 1 ? 0 : Fbm(x / 14.0 + 140, z / 14.0 + 140, 2, 975);
				int bandShift = biomeMap[z * WX + x] == Badlands ? (int)Math.Floor(Fbm(x / 40.0 + 77, z / 40.0 + 77, 2, 1130) * 8) : 0;   // (badlands: the stripes wave up and down a little)
				for (int y = WY - 1; y >= 0; y--)
				{
					int i = Idx(x, y, z);
					if (world[i] == 0) { depth = -1; continue; }
					depth++;
					if (world[i] != RAW) continue;
					if (gen == 1)
					{
						bool low = y <= SeaLevel;
						world[i] = (byte)(depth == 0 ? (low ? 6 : 1) : depth <= 3 ? (low ? 6 : 2) : 3);
						continue;
					}
					if (gen == 3)   // biome worlds: each biome its own ground
					{
						int bi = biomeMap[z * WX + x];
						bool sandy = bi == Desert || bi == Badlands;
						int grass = bi == Snowy ? Blocks.SnowyGrass : bi == Swamp ? Blocks.SwampGrass : sandy ? 6 : 1;
						if (depth == 0)
						{
							if (y > SeaLevel + 1) surf = grass;
							else if (y < SeaLevel && y >= SeaLevel - 8 && clayey > (bi == Swamp ? 0.5 : 0.62)) surf = 63;
							else if (bi == Swamp && y >= SeaLevel - 4) surf = y >= SeaLevel ? Blocks.SwampGrass : 2;
							else if (!sandy && gravelly > 0.6) surf = 11;
							else if (y >= SeaLevel - 4) surf = sandy || beach > 0.4 ? 6 : y >= SeaLevel ? grass : 2;
							else surf = 6;
							if (bi == Swamp && surf == 2) surf = Blocks.Mud;   // (swamps: mud where other lands have dirt)
						}
						// badlands: the hills are stripes of coloured terracotta, 16 deep (sand only on the beaches)
						if (bi == Badlands && surf == 6 && y >= SeaLevel - 3 && depth <= 16 && (depth > 0 || y > SeaLevel + 1))
						{
							int n = BadlandsBands.Length, k = (y + bandShift) % n;
							world[i] = (byte)BadlandsBands[k < 0 ? k + n : k];
							continue;
						}
						// under grass: dirt (in swamps: 3 of mud, then 2 of peat); under desert sand: 3 more sand, then 4 of sandstone; then stone
						bool boggy = bi == Swamp && (surf == Blocks.SwampGrass || surf == Blocks.Mud);
						world[i] = (byte)(depth == 0 ? surf : depth <= 3 ? (boggy ? Blocks.Mud : Blocks.IsGrassy(surf) ? 2 : surf) : boggy && depth <= 5 ? Blocks.Peat
							: bi == Desert && surf == 6 && depth <= 7 ? Blocks.Sandstone : 3);
						continue;
					}
					if (depth == 0)
					{
						if (y > SeaLevel + 1) surf = 1;
						else if (y < SeaLevel && y >= SeaLevel - 8 && clayey > 0.62) surf = 63;
						else if (gravelly > 0.6) surf = 11;
						else if (y >= SeaLevel - 4) surf = beach > 0.4 ? 6 : y >= SeaLevel ? 1 : 2;
						else surf = 6;
					}
					world[i] = (byte)(depth == 0 ? surf : depth <= 3 ? (surf == 1 ? 2 : surf) : 3);
				}
			}
		});
	}

	// --- Gravel: rounded clumps scattered through the stone ---
	void Gravel()
	{
		int PATCHES = (int)Math.Floor(10000.0 * WY / 192);   // (as common in a tall world: spread through more rock)
		for (int i = 0, tries = 0; i < PATCHES && tries < PATCHES * 20; tries++)
		{
			int x = (int)Math.Floor(Hash(tries, 1, 7001) * WX), z = (int)Math.Floor(Hash(tries, 2, 7002) * WZ);
			int y = 1 + (int)Math.Floor(Hash(tries, 3, 7003) * (WY - 2));
			if (world[Idx(x, y, z)] != 3) continue;
			i++;
			double rx = 1.5 + Hash(tries, 4, 7004) * 2.5, ry = 1 + Hash(tries, 5, 7005) * 1.5, rz = 1.5 + Hash(tries, 6, 7006) * 2.5;
			for (int dy = -(int)Math.Ceiling(ry); dy <= Math.Ceiling(ry); dy++)
				for (int dz = -(int)Math.Ceiling(rz); dz <= Math.Ceiling(rz); dz++)
					for (int dx = -(int)Math.Ceiling(rx); dx <= Math.Ceiling(rx); dx++)
					{
						if (Math.Pow(dx / rx, 2) + Math.Pow(dy / ry, 2) + Math.Pow(dz / rz, 2) > 1) continue;
						int gx = x + dx, gy = y + dy, gz = z + dz;
						if (gx < 0 || gz < 0 || gx >= WX || gz >= WZ || gy < 1 || gy >= WY) continue;
						if (world[Idx(gx, gy, gz)] == 3) world[Idx(gx, gy, gz)] = 11;
					}
		}
	}

	// --- Caves (hills worlds): winding "worm" tunnels, never cutting right next to water ---
	// --- Bedrock along the bottom of every world; and strata (640-tall worlds): the stone gets a new layer every 64 blocks down from sea level ---
	void Strata()
	{
		int slab = WX * WZ;
		Array.Fill(world, (byte)Blocks.Bedrock, 0, slab);
		if (WY != 640) return;
		for (int y = 1; y <= SeaLevel - Blocks.StrataThick; y++)
		{
			byte id = (byte)(Blocks.Strata + Math.Min(Blocks.StrataLayers, 1 + (SeaLevel - y) / Blocks.StrataThick) - 2);
			int layer = id - Blocks.Strata + 2;
			for (int i = y * slab, end = i + slab; i < end; i++)
			{
				if (world[i] == 3) world[i] = id;
				else if (World.OreSpread(Land) && AllDepthOres.Contains(world[i]) && Blocks.OreIndex(world[i]) is int k and >= 0) Late.Add((i, Blocks.DeepOreOf(k, layer), world[i]));   // (an allDepths ore in a deep layer: that layer's ore)
			}
		}
	}

	// --- Ores and Fireclay: veins in the ground, by depth below the surface (before the caves, which cut through them) ---
	// data/worldgen.json's ORE_VEINS, grown in their order (a row's place is in its veins' seeds): each one's ore, veins per
	// 10,000 columns, blocks per vein, shallowest and deepest (blocks below the surface), whether it turns Dirt into ore too,
	// and whether it's at every depth from land 2. A vein is a short random walk that turns the Stone it passes through into
	// its ore. (The same as the web version.)
	public static readonly (int ore, int per, int size, int top, int deep, bool takesDirt, bool allDepths)[] OreVeins =
		Array.ConvertAll(Nums.Worldgen.ORE_VEINS, r => (DataKeys.IdOf(r.ore), r.per, r.size, r.top, r.deep, r.takesDirt == true, r.allDepths == true));
	static readonly HashSet<int> AllDepthOres = new(Array.ConvertAll(Array.FindAll(OreVeins, v => v.allDepths), v => v.ore));
	static readonly int[][] OreSteps = { new[] { 1, 0, 0 }, new[] { -1, 0, 0 }, new[] { 0, 1, 0 }, new[] { 0, -1, 0 }, new[] { 0, 0, 1 }, new[] { 0, 0, -1 } };
	void PlaceOres()
	{
		for (int o = 0; o < OreVeins.Length; o++)
		{
			var (ore, per, size, top, deep, takesDirt, allDepths) = OreVeins[o];
			bool even = World.OreSpread(Land) && allDepths;   // (lands with the ore spread, 2 and 4: the allDepths ores are as common at every depth, all the way down)
			if (even) { top = 3; deep = 999; }
			int s = 7001 + o * 10, n = (int)((long)WX * WZ * per / 10000);
			if (even) n = (int)((long)n * WY / 192);
			for (int k = 0; k < n; k++)
			{
				int x = (int)Math.Floor(Hash(k, 1, s) * WX), z = (int)Math.Floor(Hash(k, 2, s) * WZ);
				int h = W.Heights[z * WX + x], hi = h - top, lo = Math.Max(2, h - deep);
				if (hi < lo) continue;
				int y = lo + (int)Math.Floor(Hash(k, 3, s) * (hi - lo + 1));
				for (int j = 0; j < size; j++)
				{
					int i = Idx(x, y, z), b = world[i];
					if (b == Blocks.Stone || (takesDirt && b == Blocks.Dirt)) world[i] = (byte)ore;
					var d = OreSteps[(int)Math.Floor(Hash3(k, j, o, s + 5) * 6)];
					x += d[0]; y += d[1]; z += d[2];
					if (x < 0 || z < 0 || x >= WX || z >= WZ || y < 2 || y >= WY) break;
				}
			}
		}
	}

	void CarveCaves()
	{
		int n = (int)Math.Floor(WX * WZ / 400.0 * WY / 192);   // (more in a tall world, so the deep rock has as many as the shallow)
		void Carve(double cx, double cy, double cz, double r)
		{
			int R = (int)Math.Ceiling(r);
			for (int dy = -R; dy <= R; dy++) for (int dz = -R; dz <= R; dz++) for (int dx = -R; dx <= R; dx++)
			{
				if (dx * dx + dy * dy * 1.6 + dz * dz > r * r) continue;
				int x = (int)Math.Floor(cx) + dx, y = (int)Math.Floor(cy) + dy, z = (int)Math.Floor(cz) + dz;
				if (x < 1 || z < 1 || x >= WX - 1 || z >= WZ - 1 || y < 2 || y >= WY - 1) continue;
				int i = Idx(x, y, z); int b = world[i];
				if (b == 0 || Blocks.IsWater(b)) continue;
				if (Blocks.IsWater(world[i + 1]) || Blocks.IsWater(world[i - 1]) || Blocks.IsWater(world[i + WX]) || Blocks.IsWater(world[i - WX]) ||
					Blocks.IsWater(world[Idx(x, y + 1, z)]) || Blocks.IsWater(world[Idx(x, y - 1, z)])) continue;
				world[i] = 0;
			}
		}
		for (int k = 0; k < n; k++)
		{
			double x = Hash(k, 1, 5001) * WX, z = Hash(k, 2, 5002) * WZ, y = 6 + Hash(k, 3, 5003) * (SeaLevel + 12);
			double yaw = Hash(k, 4, 5004) * Math.PI * 2, pitch = (Hash(k, 5, 5005) - 0.5) * 0.5;
			double len = 60 + Hash(k, 6, 5006) * 140, r0 = 1.3 + Hash(k, 7, 5007) * 1.9;
			for (int s = 0; s < len; s++)
			{
				Carve(x, y, z, r0 * (0.6 + 0.6 * Math.Sin(Math.PI * s / len)));
				x += Math.Cos(yaw) * Math.Cos(pitch); z += Math.Sin(yaw) * Math.Cos(pitch); y += Math.Sin(pitch);
				yaw += (Hash(k, s, 5008) - 0.5) * 0.45;
				pitch = pitch * 0.85 + (Hash(k, s, 5009) - 0.5) * 0.35;
			}
		}
	}

	// --- Trees: normal, lollipop, tall spindly, and big puffballs ---
	void LeafBall(int cx, int cy, int cz, double r)
	{
		int R = (int)Math.Ceiling(r);
		for (int dy = -R; dy <= R; dy++) for (int dz = -R; dz <= R; dz++) for (int dx = -R; dx <= R; dx++)
			if (dx * dx + dy * dy + dz * dz <= r * r) Put(cx + dx, cy + dy, cz + dz, 5, true);
	}
	void Trunk(int x, int y, int z, int h, int log = Blocks.Log) { for (int yy = y + 1; yy <= y + h; yy++) Put(x, yy, z, log, true); }
	// (worlds from Land 1: a trunk that goes up through leaves already there, so it reaches into the tree's crown)
	void TrunkInto(int x, int y, int z, int h) { for (int yy = y + 1; yy <= y + h; yy++) if (world[Idx(x, yy, z)] == 0 || Blocks.IsLeaves(world[Idx(x, yy, z)])) world[Idx(x, yy, z)] = Blocks.Log; }
	double ForestAt(int x, int z) => Smoothstep(0.47, 0.6, Fbm(x / 110.0 + 20, z / 110.0 + 20, 2, 970));
	// biome worlds: each biome's own trees (the Woods have the ones below)
	static readonly int[][] PalmFronds = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 }, new[] { 1, 1 }, new[] { 1, -1 }, new[] { -1, 1 }, new[] { -1, -1 } };

	// ---- Trees and Leaves (Biomes worlds from land 3, see World.NewTrees): the new trees ----
	// Each biome's usual tree spots (where land 1 grows its trees) are shared out between its trees (worldgen.json TREE_MIX), the
	// same kind in patches (TREE_PATCH blocks square), and some trees have spots of their own (TREE_EXTRA: each row its own roll,
	// tried in order before the usual spots). How each one looks is below (its numbers: TREE_SHAPES). Their blocks are numbered
	// over 255, so a log grows as an Oak Log and a leaf as Oak Leaves, and the real block takes its place once the world is packed
	// (see Late). The same maths as the web version, in the same order: both must grow the same worlds.
	bool NewTreesHere => gen == 3 && World.NewTrees(Land);
	public static readonly string[] BiomeNames = { "woods", "plains", "desert", "snowy", "swamp", "taiga", "meadow", "badlands" };   // (biomeMap's numbers)
	static readonly string[] OwnTree = { "oak", "oak", "palm", "fir", "willow", "fir", "oak", null };   // the tree each biome grew before (its code is below)
	// the trees GrowTree knows how to grow (a TREE_SHAPES row must be one of these: see the data check)
	public static readonly string[] GrownTrees = { "maple", "cedar", "birch", "cherry", "orange", "aspen", "redwood", "cypress", "juniper", "larch", "holly", "palo_verde", "dead", "prairie_oak", "shrub" };
	static readonly TreeShapeRow[] TreeShapes = Nums.Worldgen.TREE_SHAPES ?? Array.Empty<TreeShapeRow>();
	static readonly TreeExtraRow[] TreeExtra = Nums.Worldgen.TREE_EXTRA ?? Array.Empty<TreeExtraRow>();
	// a shape's crown: [the number] or [across, up and down]
	public static double[] CrownOf(TreeShapeRow T)
	{
		if (T.crown.ValueKind == System.Text.Json.JsonValueKind.Number) return new[] { T.crown.GetDouble() };
		var c = new List<double>();
		if (T.crown.ValueKind == System.Text.Json.JsonValueKind.Array) foreach (var e in T.crown.EnumerateArray()) c.Add(e.GetDouble());
		return c.ToArray();
	}
	// each shape's place (its seeds), log, leaves and crown, by its name
	static readonly Dictionary<string, (int at, int log, int leaves, double[] crown)> ShapeOf = MakeShapes();
	static Dictionary<string, (int, int, int, double[])> MakeShapes()
	{
		var d = new Dictionary<string, (int, int, int, double[])>();
		for (int k = 0; k < TreeShapes.Length; k++)
			d[TreeShapes[k].tree] = (k, DataKeys.IdOf(TreeShapes[k].log), TreeShapes[k].leaves != null ? DataKeys.IdOf(TreeShapes[k].leaves) : 0, CrownOf(TreeShapes[k]));
		return d;
	}
	static int Span(int[] ab, double v) => ab[0] + (int)Math.Floor(v * (ab[1] - ab[0] + 1));   // a whole number from a to b, picked by v (0 to 1)
	// put a block of a tree: into air (and into leaves too, with into: a trunk going up through leaves)
	void TreeBlock(int id, int x, int y, int z, bool into = false)
	{
		if (x < 0 || z < 0 || x >= WX || z >= WZ || y < 0 || y >= WY) return;
		if (planning)
		{   // (a sapling's tree: grown into empty space first, by the same rules; PlanTree then fits it into the world)
			int p = Idx(x, y, z);
			if (planned.TryGetValue(p, out var c) && !(into && Blocks.IsLeaves(c.id))) return;
			planned[p] = (x, y, z, id); return;
		}
		int i = Idx(x, y, z), b = world[i];
		if (b != 0 && !(into && Blocks.IsLeaves(b))) return;
		if (id < 256) { world[i] = (byte)id; return; }
		byte stand = (byte)(Blocks.B[id].Leaves ? 5 : 4);
		world[i] = stand; Late.Add((i, id, stand));
	}
	// a trunk: n logs above y
	void Stem(int id, int x, int y, int z, int n) { for (int k = 1; k <= n; k++) TreeBlock(id, x, y + k, z, true); }
	// leaves in a ball rx across and ry up and down, around the middle of block (cx, cy, cz) (cx, cz may be half-way between
	// blocks, for a 2 x 2 trunk); holes: the share of its outer blocks left out, so the edge is ragged
	void Crown(int id, double cx, int cy, double cz, double rx, double ry, double holes, int s)
	{
		int X = (int)Math.Ceiling(rx), Y = (int)Math.Ceiling(ry);
		for (int dy = -Y; dy <= Y; dy++) for (int bz = (int)Math.Floor(cz - X); bz <= (int)Math.Ceiling(cz + X); bz++) for (int bx = (int)Math.Floor(cx - X); bx <= (int)Math.Ceiling(cx + X); bx++)
		{
			double d = ((bx - cx) * (bx - cx) + (bz - cz) * (bz - cz)) / (rx * rx) + dy * dy / (ry * ry);
			if (d > 1 || (d > 0.5 && Hash3(bx, cy + dy, bz, s) < holes)) continue;
			TreeBlock(id, bx, cy + dy, bz);
		}
	}
	// a flat round layer of leaves r across; sparse: the share of it left out (not next to the trunk)
	void Layer(int id, double cx, int y, double cz, double r, double sparse, int s)
	{
		for (int bz = (int)Math.Floor(cz - r); bz <= (int)Math.Ceiling(cz + r); bz++) for (int bx = (int)Math.Floor(cx - r); bx <= (int)Math.Ceiling(cx + r); bx++)
		{
			double d = (bx - cx) * (bx - cx) + (bz - cz) * (bz - cz);
			if (d > r * r || (d > 1 && Hash3(bx, y, bz, s) < sparse)) continue;
			TreeBlock(id, bx, y, bz);
		}
	}
	// any of 8 spots `far` away in another biome (a desert's edge)
	bool NearOtherBiome(int x, int z, int far, int bi)
	{
		foreach (var d in PalmFronds)
		{
			int px = Math.Min(WX - 1, Math.Max(0, x + d[0] * far)), pz = Math.Min(WZ - 1, Math.Max(0, z + d[1] * far));
			if (biomeMap[pz * WX + px] != bi) return true;
		}
		return false;
	}
	// low, with sea within `far` (a coast)
	bool NearSea(int x, int h, int z, int far)
	{
		if (h > SeaLevel + 3) return false;
		foreach (var d in PalmFronds)
		{
			int px = Math.Min(WX - 1, Math.Max(0, x + d[0] * far)), pz = Math.Min(WZ - 1, Math.Max(0, z + d[1] * far));
			if (world[Idx(px, SeaLevel, pz)] == Blocks.Water) return true;
		}
		return false;
	}
	// the ground each biome's trees grow on
	bool TreeGround(int bi, int top, int h) => bi == Snowy ? top == Blocks.SnowyGrass : bi == Swamp ? top == Blocks.SwampGrass
		: bi == Desert ? top == 6 && h > SeaLevel : bi == Badlands ? h > SeaLevel && Array.IndexOf(BadlandsBands, top) >= 0 : top == 1;
	// grow tree `name` standing on (x, h, z)
	void GrowTree(string name, int x, int h, int z)
	{
		var (ti, log, leaves, C) = ShapeOf[name];
		var T = TreeShapes[ti];
		int s = 12000 + 10 * ti;
		double v = Hash(x, z, s); int th = T.trunk != null ? Span(T.trunk, v) : 0, top = h + th;
		switch (name)
		{
			case "maple":   // a big round crown
				Stem(log, x, h, z, th + 1); Crown(leaves, x, top + 1, z, C[0], C[0] * 0.8, 0.3, s + 1); break;
			case "cedar":   // flat tiers of leaves, wide at the bottom, and a tuft on top
				Stem(log, x, h, z, th);
				for (int k = 0; k < T.tiers.Length; k++) Layer(leaves, x, top - T.gap * (T.tiers.Length - 1 - k), z, T.tiers[k], 0.25, s + 1 + k);
				TreeBlock(leaves, x, top + 1, z); break;
			case "birch":   // slim, with a small oval crown
			case "cherry":  // short, with a wide round crown
			case "orange":  // small, with a round crown
				Stem(log, x, h, z, th); Crown(leaves, x, name == "birch" ? top - 1 : top + 1, z, C[0], C[1], 0.3, s + 1); break;
			case "aspen":   // tall and thin, with a narrow column of leaves
				Stem(log, x, h, z, th);
				for (int y = top - (int)C[1]; y <= top + 1; y++) Layer(leaves, x, y, z, y >= top ? 1 : C[0], 0.15, s + 1);
				break;
			case "redwood":   // a thick trunk (2 x 2), the tallest tree, with a narrow cone of leaves on its top third
			{
				int[][] cols = { new[] { 0, 0 }, new[] { 1, 0 }, new[] { 0, 1 }, new[] { 1, 1 } };
				foreach (var c in cols) if (Math.Abs(TreeGroundAt(x + c[0], z + c[1]) - h) > 2) return;   // (only on fairly flat ground)
				foreach (var c in cols) { int g = TreeGroundAt(x + c[0], z + c[1]); Stem(log, x + c[0], g, z + c[1], top - g); }
				int tip = top + 3;
				for (int y = top - th / 3; y <= tip; y++)
				{
					if (y < tip - 5 && (tip - y) % 3 == 2) continue;   // (gaps between its lower layers of branches)
					Layer(leaves, x + 0.5, y, z + 0.5, Math.Min(C[0], 1.6 + (tip - y) * 0.22), 0.2, s + 1);
				}
				break;
			}
			case "cypress":   // a tall narrow spire, with roots flaring out at the bottom
			{
				Stem(log, x, h, z, th);
				for (int k = 0; k < 4; k++) { var d = PalmFronds[k]; if (Hash(x + d[0], z + d[1], s + 2) < 0.6) TreeBlock(log, x + d[0], h + 1, z + d[1]); }
				int tip = top + 2;
				for (int y = h + 4; y <= tip; y++) Layer(leaves, x, y, z, 0.6 + C[0] * (tip - y) / (tip - h - 4), 0.2, s + 1);
				break;
			}
			case "juniper":   // short and gnarled: a leaning trunk with a clump of leaves on top and one or two on short branches
			{
				var l = PalmFronds[(int)Math.Floor(Hash(x, z, s + 2) * 4)];
				int cx = x, cz = z;
				for (int k = 1; k <= th; k++) { TreeBlock(log, cx, h + k, cz, true); if (k < th && Hash3(x, k, z, s + 3) < 0.4) { cx += l[0]; cz += l[1]; } }
				Crown(leaves, cx, top + 1, cz, C[0], 1, 0.3, s + 1);
				int n = 1 + (int)Math.Floor(Hash(x, z, s + 4) * 2);
				for (int j = 0; j < n; j++)
				{
					var d = PalmFronds[(int)Math.Floor(Hash3(x, j, z, s + 5) * 8)];
					TreeBlock(log, cx + d[0], top, cz + d[1], true);
					Crown(leaves, cx + 2 * d[0], top, cz + 2 * d[1], C[0] - 0.3, 1, 0.3, s + 6 + j);
				}
				break;
			}
			case "larch":   // a sparse cone
			case "holly":   // small and dense, a cone right down to the ground
			{
				Stem(log, x, h, z, th);
				int tip = top + (name == "larch" ? 1 : 2), low = name == "larch" ? h + 3 : h + 1;
				for (int y = low; y <= tip; y++) Layer(leaves, x, y, z, name == "larch" ? Math.Min(C[0], (tip - y + 1) >> 1) : 0.6 + C[0] * (tip - y) / (tip - low), T.sparse, s + 1);
				break;
			}
			case "palo_verde":   // a short trunk splitting into branches, with a wide, flat, sparse crown
			{
				Stem(log, x, h, z, th);
				int n = 2 + (int)Math.Floor(Hash(x, z, s + 2) * 2), len = Span(T.branch, Hash(x, z, s + 3));
				for (int j = 0; j < n; j++) { var d = PalmFronds[4 + (int)Math.Floor(Hash3(x, j, z, s + 4) * 4)]; for (int k = 1; k <= len; k++) TreeBlock(log, x + d[0] * k, top + k, z + d[1] * k, true); }
				Layer(leaves, x, top + len + 1, z, C[0], T.sparse, s + 1); Layer(leaves, x, top + len + 2, z, C[0] - 1.2, T.sparse, s + 5);
				break;
			}
			case "dead":   // bleached and leafless: a trunk with a few bare branches sticking out
			{
				Stem(log, x, h, z, th);
				int n = Span(T.branches, Hash(x, z, s + 2));
				for (int j = 0; j < n; j++)
				{
					int k4 = (int)Math.Floor(Hash3(x, j, z, s + 3) * 4); var d = PalmFronds[k4];
					int y = h + 2 + (int)Math.Floor(Hash3(x, j, z, s + 4) * (th - 1));
					int len = Span(T.branch, Hash3(x, j, z, s + 5)), along = Blocks.Turned(log, d[0] != 0 ? 1 : 2);
					for (int k = 1; k <= len; k++) TreeBlock(along, x + d[0] * k, y, z + d[1] * k);
				}
				break;
			}
			case "prairie_oak":   // short, with a wide, flattened crown
				Stem(log, x, h, z, th + 1); Crown(leaves, x, top + 1, z, C[0], C[1], 0.3, s + 1); break;
			case "shrub":   // a low bush of leaves, sometimes round a stub of log
				if (Hash(x, z, s + 2) < 0.4) TreeBlock(log, x, h + 1, z);
				Crown(leaves, x, h + 1, z, C[0] + (C[1] - C[0]) * v, 1, 0.35, s + 1); break;
		}
	}
	// ---- Saplings: a tree grown in the finished world. The same shapes and seeds as the world grows at that spot: the new
	// trees (GrowTree, in any world), and the older four as their biomes grow them (OldTree: the oak is the woods' normal one).
	// PlanTree works out the blocks without putting any: the tree grows into empty space first (its own blocks overlap as when
	// the world grows it), then it's fitted into the world: null if any log can't go in (every log has to go into air, leaves
	// or the saplings it grows from, which read as air); leaves only go into air, so a tree never breaks anything built. A
	// Redwood's four trunks stand on its saplings' ground. (The same as the web version.) ----
	bool planning; Func<int, int, int> planGround;
	readonly Dictionary<int, (int x, int y, int z, int id)> planned = new();
	int TreeGroundAt(int x, int z) => planGround != null ? planGround(x, z) : W.Heights[z * WX + x];
	public static readonly string[] OldTrees = { "oak", "fir", "palm", "willow" };
	// the biome at (x, z) (-1: not a Biomes world)
	public int BiomeOf(int x, int z) => gen == 3 && x >= 0 && z >= 0 && x < WX && z < WZ ? biomeMap[z * WX + x] : -1;
	public List<(int x, int y, int z, int id)> PlanTree(string name, int x, int h, int z, Func<int, int, int, int> get, Func<int, int, int> ground)
	{
		planned.Clear(); planning = true; planGround = ground;
		try { if (Array.IndexOf(OldTrees, name) >= 0) OldTree(name, x, h, z); else GrowTree(name, x, h, z); }
		finally { planning = false; planGround = null; }
		var fit = new List<(int x, int y, int z, int id)>();
		foreach (var (bx, by, bz, id) in planned.Values)
		{
			int there = get(bx, by, bz);
			if (Blocks.IsLeaves(id)) { if (there == 0) fit.Add((bx, by, bz, id)); continue; }
			if (there != 0 && !Blocks.IsLeaves(there)) return null;   // (a log can't go in: no room yet)
			fit.Add((bx, by, bz, id));
		}
		return fit.Count == 0 ? null : fit;
	}
	// the four older trees (only for saplings: the world still grows them with the code further down, as it always has)
	void OldTree(string name, int x, int h, int z)
	{
		double v = Hash(x, z, 77);
		switch (name)
		{
			case "oak":   // the woods' normal oak (with its trunk up into its leaves, but in land 0 worlds up to them)
			{
				int th = 4 + (int)(v * 3);
				for (int dy = -2; dy <= 1; dy++)
				{
					int r = dy >= 0 ? 1 : 2, ly = h + th + dy;
					for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++)
					{
						if (Math.Abs(dx) == r && Math.Abs(dz) == r && Hash(x + dx, z + dz, ly) < 0.6) continue;
						TreeBlock(Blocks.Leaves, x + dx, ly, z + dz);
					}
				}
				for (int k = 1; k <= th; k++) TreeBlock(Blocks.Log, x, h + k, z, Land >= 1);
				break;
			}
			case "fir":   // rings of leaves, widest at the bottom, narrowing up to a point
			{
				int th = 6 + (int)(v * 4), tip = h + th + 1;
				Stem(Blocks.FirLog, x, h, z, th);
				for (int ly = h + 3; ly <= tip; ly++)
				{
					int r = Math.Min(3, (tip - ly + 1) >> 1);
					for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++) if (dx * dx + dz * dz <= r * r + 1) TreeBlock(Blocks.FirLeaves, x + dx, ly, z + dz);
				}
				break;
			}
			case "palm":   // a tall bare trunk with a crown of long fronds drooping at the tips
			{
				int th = 6 + (int)(v * 4), cy = h + th;
				Stem(Blocks.PalmLog, x, h, z, th);
				TreeBlock(Blocks.PalmLeaves, x, cy + 1, z);
				foreach (var d in PalmFronds)
				{
					int len = d[0] != 0 && d[1] != 0 ? 2 : 3;
					for (int k = 1; k <= len; k++) TreeBlock(Blocks.PalmLeaves, x + d[0] * k, k < len ? cy + 1 : cy, z + d[1] * k);
					TreeBlock(Blocks.PalmLeaves, x + d[0] * len, cy - 1, z + d[1] * len);
				}
				break;
			}
			case "willow":   // low and wide, with leaves drooping off the edges
			{
				int th = 4 + (int)(v * 2), cy = h + th;
				Stem(Blocks.WillowLog, x, h, z, th);
				for (int dz = -3; dz <= 3; dz++) for (int dx = -3; dx <= 3; dx++)
				{
					int d = dx * dx + dz * dz;
					if (d > 10) continue;
					TreeBlock(Blocks.WillowLeaves, x + dx, cy, z + dz);
					if (d <= 5) TreeBlock(Blocks.WillowLeaves, x + dx, cy + 1, z + dz);
					if (d >= 5)
					{
						double hang = Hash(x + dx, z + dz, 8080);
						if (hang < 0.35) TreeBlock(Blocks.WillowLeaves, x + dx, cy - 1, z + dz);
						if (hang < 0.12) TreeBlock(Blocks.WillowLeaves, x + dx, cy - 2, z + dz);
					}
				}
				break;
			}
		}
	}

	// a spot where biome bi usually grows a tree: which tree grows there (worldgen.json TREE_MIX). True if it grew a new one (false:
	// the biome's own tree, which the code below grows as before)
	bool MixedTree(int bi, int x, int h, int z)
	{
		if (!NewTreesHere) return false;
		var mix = Nums.Worldgen.TREE_MIX != null && Nums.Worldgen.TREE_MIX.TryGetValue(BiomeNames[bi], out var m) ? m : Array.Empty<System.Text.Json.JsonElement[]>();
		int P = Nums.Worldgen.TREE_PATCH;
		double p = Hash((int)Math.Floor((double)x / P), (int)Math.Floor((double)z / P), 12900 + bi); string name = OwnTree[bi];
		foreach (var e in mix) { double share = e[1].GetDouble(); if (p < share) { name = e[0].GetString(); break; } p -= share; }
		if (name == OwnTree[bi]) return false;
		GrowTree(name, x, h, z); return true;
	}
	// the trees with spots of their own (worldgen.json TREE_EXTRA, each row its own roll): true if one grew here
	bool ExtraTree(int bi, int x, int h, int z)
	{
		int top = world[Idx(x, h, z)];
		for (int k = 0; k < TreeExtra.Length; k++)
		{
			var r = TreeExtra[k];
			if (r.biome != BiomeNames[bi] || !TreeGround(bi, top, h) || Hash(x, z, 12100 + k) >= r.chance) continue;
			if (r.spacing > 0)
			{   // (only one spot in each spacing x spacing square can grow it, so they're never too close)
				int n = r.spacing, gx = (int)Math.Floor((double)x / n), gz = (int)Math.Floor((double)z / n);
				if (x != gx * n + (int)Math.Floor(Hash(gx, gz, 12300 + k) * n) || z != gz * n + (int)Math.Floor(Hash(gx, gz, 12400 + k) * n)) continue;
			}
			if (r.grove != null && Fbm(x / r.grove[0] + 31 * k, z / r.grove[0] + 31 * k, 2, 12200 + k) < r.grove[1]) continue;   // (only in its groves)
			if ((r.edge > 0 || r.coast > 0) && !(r.edge > 0 && NearOtherBiome(x, z, r.edge, bi)) && !(r.coast > 0 && NearSea(x, h, z, r.coast))) continue;
			GrowTree(r.tree, x, h, z); return true;
		}
		return false;
	}

	void BiomeTree(int x, int h, int z, int bi)
	{
		int top = world[Idx(x, h, z)]; double v = Hash(x, z, 77), roll = Hash(x, z, 999);
		if (bi == Plains || bi == Meadow)   // now and then a lone round tree (a few more in meadows)
		{
			if (top != 1 || roll > (bi == Meadow ? 0.0015 : 0.0008) || MixedTree(bi, x, h, z)) return;
			int th = 5 + (int)(v * 3);
			Trunk(x, h, z, Land >= 1 ? th + 1 : th); LeafBall(x, h + th + 1, z, 2.3);
		}
		else if (bi == Snowy || bi == Taiga)   // Fir trees, pointed: rings of leaves, widest at the bottom, narrowing up to a point (taiga: thicker forests, no snow)
		{
			if (bi == Snowy ? top != Blocks.SnowyGrass || roll > 0.004 + ForestAt(x, z) * 0.03 : top != 1 || roll > 0.006 + ForestAt(x, z) * 0.045) return;
			if (MixedTree(bi, x, h, z)) return;
			int th = 6 + (int)(v * 4), tip = h + th + 1;
			Trunk(x, h, z, th, Blocks.FirLog);
			for (int ly = h + 3; ly <= tip; ly++)
			{
				int r = Math.Min(3, (tip - ly + 1) >> 1);
				for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++) if (dx * dx + dz * dz <= r * r + 1) Put(x + dx, ly, z + dz, Blocks.FirLeaves, true);
			}
		}
		else if (bi == Desert)     // Palm trees, now and then: a tall bare trunk with a crown of long fronds drooping at the tips
		{
			if (top != 6 || h <= SeaLevel || roll > 0.002 || MixedTree(bi, x, h, z)) return;
			int th = 6 + (int)(v * 4), cy = h + th;
			Trunk(x, h, z, th, Blocks.PalmLog);
			Put(x, cy + 1, z, Blocks.PalmLeaves, true);
			foreach (var d in PalmFronds)
			{
				int len = d[0] != 0 && d[1] != 0 ? 2 : 3;   // (shorter on the diagonals, so the crown looks round)
				for (int k = 1; k <= len; k++) Put(x + d[0] * k, k < len ? cy + 1 : cy, z + d[1] * k, Blocks.PalmLeaves, true);
				Put(x + d[0] * len, cy - 1, z + d[1] * len, Blocks.PalmLeaves, true);
			}
		}
		else if (bi == Swamp)      // Willow trees: low and wide, with leaves drooping off the edges
		{
			if (top != Blocks.SwampGrass || roll > 0.012 || MixedTree(bi, x, h, z)) return;
			int th = 4 + (int)(v * 2), cy = h + th;
			Trunk(x, h, z, th, Blocks.WillowLog);
			for (int dz = -3; dz <= 3; dz++) for (int dx = -3; dx <= 3; dx++)
			{
				int d = dx * dx + dz * dz;
				if (d > 10) continue;
				Put(x + dx, cy, z + dz, Blocks.WillowLeaves, true);
				if (d <= 5) Put(x + dx, cy + 1, z + dz, Blocks.WillowLeaves, true);
				if (d >= 5)
				{
					double hang = Hash(x + dx, z + dz, 8080);
					if (hang < 0.35) Put(x + dx, cy - 1, z + dz, Blocks.WillowLeaves, true);
					if (hang < 0.12) Put(x + dx, cy - 2, z + dz, Blocks.WillowLeaves, true);
				}
			}
		}
	}
	void Trees()
	{
		bool newTrees = NewTreesHere;
		for (int z = 3; z < WZ - 3; z++) for (int x = 3; x < WX - 3; x++)
		{
			int h = W.Heights[z * WX + x];
			if (newTrees && ExtraTree(biomeMap[z * WX + x], x, h, z)) continue;   // (a tree with a spot of its own)
			if (gen == 3 && biomeMap[z * WX + x] != Woods) { BiomeTree(x, h, z, biomeMap[z * WX + x]); continue; }
			if (world[Idx(x, h, z)] != 1 || Hash(x, z, 999) > (gen == 1 ? 0.011 : 0.003 + ForestAt(x, z) * 0.045)) continue;
			if (MixedTree(Woods, x, h, z)) continue;   // (land 3: maples and cedars among the oaks)
			double kind = gen == 1 ? Hash(x, z, 555) : Hash(x, z, 555) < 0.93 ? 0 : 0.95, v = Hash(x, z, 77);
			if (kind < 0.45)
			{
				int th = 4 + (int)(v * 3);
				for (int dy = -2; dy <= 1; dy++)
				{
					int r = dy >= 0 ? 1 : 2, ly = h + th + dy;
					for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++)
					{
						if (Math.Abs(dx) == r && Math.Abs(dz) == r && Hash(x + dx, z + dz, ly) < 0.6) continue;
						Put(x + dx, ly, z + dz, 5, true);
					}
				}
				if (Land >= 1) TrunkInto(x, h, z, th); else Trunk(x, h, z, th);
			}
			else if (kind < 0.75) { int th = 5 + (int)(v * 4); Trunk(x, h, z, Land >= 1 ? th + 2 : th); LeafBall(x, h + th + 2, z, 2.6); }
			else if (kind < 0.92) { int th = 10 + (int)(v * 8); Trunk(x, h, z, Land >= 1 ? th + 1 : th); LeafBall(x, h + th + 1, z, 1.6); LeafBall(x, h + (int)Math.Floor(th * 0.6), z, 1.2); }
			else { Trunk(x, h, z, Land >= 1 ? 7 : 3); LeafBall(x, h + 7, z, 4.5); }
		}
	}

	// --- Rocks: lying on dry sand and gravel beaches, close to the water ---
	void Rocks()
	{
		const double CHANCE = 0.06;
		for (int z = 6; z < WZ - 6; z++) for (int x = 6; x < WX - 6; x++)
		{
			if (Hash(x, z, 4321) > CHANCE) continue;
			int h = W.Heights[z * WX + x], top = world[Idx(x, h, z)];
			if ((top != 6 && top != 11) || h < SeaLevel || world[Idx(x, h + 1, z)] != 0) continue;
			bool wet = false;
			for (int dz = -5; dz <= 5 && !wet; dz++) for (int dx = -5; dx <= 5; dx++) if (Blocks.IsWater(world[Idx(x + dx, SeaLevel, z + dz)])) { wet = true; break; }
			if (wet) { world[Idx(x, h + 1, z)] = Blocks.RockBlock; W.Heights[z * WX + x] = h + 1; }
		}
	}
	// --- Flowers: small patches of one colour on grass (exactly as the web version, so both grow the same world) ---
	// --- Farming & animals (Biomes worlds from land 5): crops growing wild, in patches, then Dead Bushes here and there ---
	// Exactly as data/worldgen.json's notes say (the same maths as the web version, in the same order: both must grow the same
	// worlds). Their blocks are numbered over 255, so each grows as a stand-in: a crop (both of Corn's halves) or a Dead Bush as
	// ripe Grain, a Cantaloupe as Bricks; the real block takes its place once the world is packed (see Late). Later on,
	// structures build over the stand-in Grain as over a flower (see Plant), and snow stops at it; the Bricks are a block.
	public const int WildStand = Blocks.GrainRipe, MelonStand = Blocks.BricksBlock;
	static readonly int[][] MelonSides = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } };   // (+x, -x, +z, -z)
	void WildBlock(int x, int y, int z, int id, int stand)
	{
		int i = Idx(x, y, z);
		world[i] = (byte)stand; Late.Add((i, id, (byte)stand));
		if (y > W.Heights[z * WX + x]) W.Heights[z * WX + x] = y;   // (the column's height goes up to its top)
	}
	void WildCrops()
	{
		foreach (var r in Nums.Worldgen.WILD_CROPS ?? Array.Empty<WildCropRow>())
		{
			var ground = new HashSet<int>(Array.ConvertAll(r.ground, DataKeys.IdOf));
			var biomes = new HashSet<int>(Array.ConvertAll(r.biomes ?? Array.Empty<string>(), b => Array.IndexOf(BiomeNames, b)));
			var near = new HashSet<int>(Array.ConvertAll(r.near ?? Array.Empty<string>(), b => Array.IndexOf(BiomeNames, b)));
			bool corn = r.crop == "corn", melon = r.crop == "cantaloupe";
			int ripe = Blocks.CropBlock(r.crop, 3), top = corn ? Blocks.CropBlock(r.crop, 3, true) : 0;
			bool Near(int x, int z)
			{   // (a column of a near biome within the square, on the map; columns outside the world aren't looked at)
				for (int dz = -r.within; dz <= r.within; dz++) for (int dx = -r.within; dx <= r.within; dx++)
				{
					int cx = x + dx, cz = z + dz;
					if (cx >= 0 && cz >= 0 && cx < WX && cz < WZ && near.Contains(biomeMap[cz * WX + cx])) return true;
				}
				return false;
			}
			for (int z = 8; z < WZ - 8; z++) for (int x = 8; x < WX - 8; x++)
			{
				int bi = biomeMap[z * WX + x];
				if (r.near != null ? near.Contains(bi) : !biomes.Contains(bi)) continue;
				if (Hash(x, z, r.seed) > r.chance) continue;
				if (r.near != null && !Near(x, z)) continue;
				int n = r.tries[0] + (int)Math.Floor(Hash(x, z, r.seed + 1) * (r.tries[1] - r.tries[0] + 1));
				for (int k = 0; k < n; k++)
				{
					int px = x + (int)Math.Floor((Hash(x + k, z, r.seed + 2) - 0.5) * 2 * r.spread + 0.5), pz = z + (int)Math.Floor((Hash(x, z + k, r.seed + 3) - 0.5) * 2 * r.spread + 0.5);
					int h = W.Heights[pz * WX + px];
					if (!ground.Contains(world[Idx(px, h, pz)]) || world[Idx(px, h + 1, pz)] != 0 || (corn && world[Idx(px, h + 2, pz)] != 0)) continue;
					WildBlock(px, h + 1, pz, ripe, WildStand);
					if (corn) WildBlock(px, h + 2, pz, top, WildStand);
					if (!melon) continue;
					int s0 = (int)Math.Floor(Hash(px, pz, r.seed + 4) * 4);   // (the melon: on the first free side, from a random one)
					for (int t = 0; t < 4; t++)
					{
						var d = MelonSides[(s0 + t) % 4]; int mx = px + d[0], mz = pz + d[1];
						if (world[Idx(mx, h + 1, mz)] != 0 || !ground.Contains(world[Idx(mx, h, mz)])) continue;
						WildBlock(mx, h + 1, mz, Blocks.Cantaloupe, MelonStand); break;
					}
				}
			}
		}
	}
	void DeadBushes()
	{
		var r = Nums.Worldgen.DEAD_BUSHES; if (r == null) return;
		var ground = new HashSet<int>(Array.ConvertAll(r.ground, DataKeys.IdOf));
		var biomes = new HashSet<int>(Array.ConvertAll(r.biomes, b => Array.IndexOf(BiomeNames, b)));
		for (int z = 8; z < WZ - 8; z++) for (int x = 8; x < WX - 8; x++)
		{
			if (!biomes.Contains(biomeMap[z * WX + x]) || Hash(x, z, r.seed) > r.chance) continue;
			int h = W.Heights[z * WX + x];
			if (!ground.Contains(world[Idx(x, h, z)]) || world[Idx(x, h + 1, z)] != 0) continue;
			WildBlock(x, h + 1, z, Blocks.DeadBush, WildStand);
		}
	}
	void Flowers()
	{
		const double PATCH_CHANCE = 0.0012;   // out of every grass spot, how many are the middle of a patch
		for (int z = 8; z < WZ - 8; z++) for (int x = 8; x < WX - 8; x++)
		{
			int bi = biomeMap[z * WX + x];
			if (Hash(x, z, 5150) > (bi == Meadow ? PATCH_CHANCE * 15 : bi == Plains ? PATCH_CHANCE * 5 : PATCH_CHANCE)) continue;   // (plains: 5 times as many; meadows: 15 times)
			int flower = Blocks.Flowers[(int)Math.Floor(Hash(x, z, 5151) * Blocks.Flowers.Length)];
			int n = 5 + (int)Math.Floor(Hash(x, z, 5152) * 8);   // 5-12 tries at a flower, within 3 blocks of the middle
			for (int k = 0; k < n; k++)
			{
				int fx = x + (int)Math.Floor((Hash(x + k, z, 5153) - 0.5) * 6 + 0.5), fz = z + (int)Math.Floor((Hash(x, z + k, 5154) - 0.5) * 6 + 0.5);
				int h = W.Heights[fz * WX + fx];
				if (world[Idx(fx, h, fz)] != Blocks.Grass || world[Idx(fx, h + 1, fz)] != 0) continue;   // on grass, with nothing there yet
				world[Idx(fx, h + 1, fz)] = (byte)flower; W.Heights[fz * WX + fx] = h + 1;
			}
		}
	}
}
