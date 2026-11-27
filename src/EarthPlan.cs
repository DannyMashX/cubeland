using System;
using System.Collections.Generic;

namespace Cubeland;

// Earthlike worlds (DRAFT): the whole-map plan, made once from the seed before any land grows. On a coarse grid (N x N cells,
// Cell blocks across each, never more than 512 x 512 so it stays quick at any world size) it works out the plates, the
// height, the temperature, the rain, where water runs (lakes and rivers) and the biomes. Chunks then fill in the detail
// from the plan, the seed and their own surroundings only.
// The web version makes exactly the same plan: only + - * / Math.sqrt Math.floor on doubles, and 32-bit integer maths
// (no sin, cos, exp, pow, log or atan2, whose last digits can differ), and every loop, tie and sort is spelled out.
public sealed partial class EarthPlan
{
	public readonly long Seed; public readonly int Size, Cell, N;
	public readonly int Room;      // how many blocks there are above the sea (320 in an Earthlike world: 640 tall, the sea in the middle)
	public readonly double Amp;    // how high things go: Room / 129, so 1 in a 192-tall world (the sea at 63) and about 2.5 in an Earthlike one
	public readonly double Tall;   // (Draft 2j) Room / 320: 1 up to 2048 wide, taller as the world widens (RoomFor)
	// (Draft 2j, the user: "Taller as they widen". An Earthlike world is 640 tall up to 2048 wide; wider, 640 x the square root
	// of (width / 2048), to the nearest 128 (whole chunks of 16, 8 of them), at most 2048. Half above the sea, half below. The
	// result is never a tie (5 x the square root of (width / 2048) is never a whole number and a half for widths in 1024 steps))
	public static int RoomFor(int size) => size <= 2048 ? 320 : Math.Min(2048, 128 * (int)Math.Floor(5 * Math.Sqrt(size / 2048.0) + 0.5)) / 2;
	public readonly double High;   // mountain country: the bands by height start this far above the sea
	public const double ExtraNoise = 1;   // (the one tuning number for the uplands, basins, hills and plateaus laid over the plates)
	public static double WideMax = 4;   // (Draft 2h: up to 4 at 32768, so the ranges grow with the world, by the square root of its size)
	public readonly double Wide;   // how wide mountain ranges, rifts and trenches are: 1 in a 2048 world, narrower in small ones
	// plates
	public int Plates; public double[] PX, PZ, VX, VZ; public bool[] Cont;
	public int[] Plate, Other; public double[] Bd, Ds;   // each cell's plate, the plate across its nearest edge, how far that edge is, how far its plate's middle is (cells)
	public int[] Shape; public int ShapeSize;   // the tiny shape each cell belongs to, and how far apart the shapes' points are (cells)
	public double[] Lift, Cut;   // (testing: how much the plate edges raise or cut each cell)
	public string ContNote = "";   // (testing: how the continents were picked)
	public byte[] Edge;      // the kind of plate edge a cell is near (pictures): 0 none, 1 continents meet, 2 ocean under continent, 3 ocean meets ocean, 4 pulling apart, 5 sliding past
	// land and climate (heights in blocks above the sea; temperature in degrees, the year's average; rain 0 to 1, the year's)
	public double[] Elev, Temp, Rain;
	// how the climate swings through the year, kept for the weather later (cheap to read at play time):
	//   Swing: half the summer-to-winter range (summer is about Temp + Swing, winter Temp - Swing): small in the tropics and by
	//     the sea, big deep inland and towards the poles;
	//   Season: when the rain falls, -1 all in winter, 0 all year, +1 all in summer (the savanna's dry season, the wet winters
	//     and dry summers of the warm west coasts, the summer rains deep inland);
	//   Humid: how much water the wind carries here (0-1), the air the clouds would form from;
	//   Inland: 0 by the sea or on a sea wind, 1 deep inland.
	// The wind: WX, WZ (the year's), MX, MZ (the monsoon), read with WindAt; the currents: Cur (warmth), CX, CZ.
	public double[] Swing, Season, Humid, Inland;
	// water: the level water stands at, which way it runs (the next cell, -1 at the sea), how much runs through, the order cells drain
	public double[] Level, Flow; public int[] Down, Order; public bool[] Sea, Lake;
	// how far each cell is (in cells) from the sea, a river, a lake and a big river (for the sub-biomes)
	public double[] SeaDist, RiverDist, LakeDist, BigDist;
	public byte[] Biome;
	public readonly int[] IslSkip = new int[4];
	public readonly List<double[]> Islands = new();   // the island chains' islands: middle x, z (cells), radius (blocks), top (blocks over the sea), the plate on top, the sinking plate
	public readonly List<int[]> Volcanoes = new();   // x, z (cells), kind: 1 coast over a sinking ocean, 2 island chain, 3 rift, 4 hot spot

	// biomes: today's 8, then the new ones (15 and 16 are the sea and lakes)
	public const int Woods = 0, Plains = 1, Desert = 2, Snowy = 3, Swamp = 4, Taiga = 5, Meadow = 6, Badlands = 7,
		Tundra = 8, Glacier = 9, Rainforest = 10, Savanna = 11, Alpine = 12, Crags = 13, Peaks = 14, Ocean = 15, LakeB = 16,
		ColdWaste = 17, HotWaste = 18, DryScrub = 19, DryForest = 20;
	public static readonly string[] BiomeNames = { "woods", "plains", "desert", "snowy", "swamp", "taiga", "meadow", "badlands",
		"tundra", "glacier", "rainforest", "savanna", "alpine", "crags", "peaks", "ocean", "lake", "cold_waste", "hot_waste", "dry_scrub", "dry_forest" };

	const double SQ2 = 1.4142135623730951;

	public EarthPlan(long seed, int size, int room = 0)
	{
		if (room <= 0) room = RoomFor(size);
		Seed = seed; Size = size; Room = room; Tall = room / 320.0;
		Cell = Math.Max(4, size / 512); N = size / Cell;
		Amp = room / 129.0; High = 40 * Amp;
		Wide = Math.Max(0.55, Math.Min(WideMax, Math.Sqrt(size / 2048.0)));
		Belt = size / 2048.0;   // (Draft 2i: what plates do at their edges is as wide as a share of the map, the same at every size)
		var sw = System.Diagnostics.Stopwatch.StartNew(); long t0 = 0;
		void Lap(string name) { long t = sw.ElapsedMilliseconds; StepTimes += $"{name} {t - t0} ms, "; t0 = t; }
		MakePlates(); Lap("plates");
		MakeHistory(); Lap("history");
		MakeHeight(); Lap("height");
		Elev0 = (double[])Elev.Clone();
		MakeClimate(); Lap("climate");
		Temp0 = Temp; Rain0 = Rain;   // (the climate the land wore down under; the geology's beds are laid down in it too)
		MakeGeo(); Lap("geology");
		Erode(); Lap("erosion");
		MakeClimate(); Lap("climate again");
		MakeWater(); Lap("water");
		MakeDistances(); MakeBraids(); MakeRiverLines(); Lap("distances");
		MakeBiomes(); Lap("biomes");
		Keep(); Lap("keep");   // (the freeze: PLANHASH, the once-only rounding, KEEPHASH, dropping the rest; EarthKeep.cs)
	}
	public string StepTimes = "";   // (testing: how long each step took)

	// ---- the same hash and noise as WorldGen (and the web version) ----
	static int Imul(long a, int b) => unchecked((int)a * b);
	public double Hash(int x, int z, int s)
	{
		int h = unchecked(Imul(x, 374761393) + Imul(z, 668265263) + Imul(Seed + s, 1442695041));
		h = Imul(h ^ (int)((uint)h >> 13), 1274126177);
		h ^= (int)((uint)h >> 16);
		return (uint)h / 4294967296.0;
	}
	double ValueNoise(double x, double z, int s)
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
	// a bump: 1 at d = 0, falling smoothly to 0 at d = w (either side)
	static double Bump(double d, double w) { if (d < 0) d = -d; if (d >= w) return 0; double t = d / w; t = 1 - t * t; return t * t; }

	// ---- plates (Draft 2h, the user: "7 major plates, the 8 to 10 minor plates, and dozens of micro plates ... so instead of
	// there being more continents on the larger worlds, the continents are just larger. It's like scaling the world up"). The
	// same set at every world size, laid out in fractions of the map, so a bigger world is the same kind of planet, just bigger
	// (from 2048 up the plan grid is the same 512 x 512, so a seed gives the same layout at every size, only stretched; 640 and
	// 1280 nearly the same, on coarser grids). As on Earth, a plate carries continent and ocean floor both: the continents are
	// a layer of their own (Crust), riding on the plates. Ranges rise where plates push together, what kind by the crust on
	// each side; a coast in the middle of a plate is a quiet one with a wide shelf ----
	public double[] Rate;
	public double[] PlateR;   // each plate's size: the radius of a circle as big, in blocks
	public bool[] Crust;      // each cell's crust: continental (true) or oceanic
	public bool[] EdgeCrust, OtherCrust;   // the crust on this side of the nearest plate edge, and just across it
	public double[] LeadFace, EdgeLead;    // (Draft 2l) on a group's leading coast cells and the ocean cell ahead of each, how squarely the coast faces the group's way (0: not one); the same at the nearest plate edge
	public static bool LeadEdge = true;    // (Draft 2l: a group's plate ends at its leading coast; testing can turn it off)
	public const int LeadMax = 12;         // (Draft 2l: at most this many leading-edge plates)
	public static double LeadMin = 0.25;   // (Draft 2l: the smallest leading-edge plate, in shapes)
	public static double LeadWob = 0.02;   // (Draft 2l: how far sideways a cell's looks start, at most about, a share of the map, so the ocean ahead's sides wander)
	public static double LeadTarget = 0.04, LeadSMin = 0.15, LeadSMax = 2;   // (Draft 2l: the young ranges' share of the continents the leading coasts' ranges are sized to; the size's limits)
	public double LeadSz = 1, LeadH = 1;   // (Draft 2l: this world's size for its leading coasts' ranges, as the plates' size factor: their width, and their height a little; and, if they are as narrow as allowed, their strength)
	public static double LeadHMin = 0.2;
	public int LeadMade;
	public int LeadFirst;   // (the first leading-edge plate's number)
	public static double LeadCoastMin = 0.25;   // (Draft 2m: a leading-edge plate holds the first cell ahead of at least this many shapes' width of leading coast)
	public static int LeadThin = 4;   // (Draft 2m: the leftover ocean ahead is opened by a disc this many cells across at a 512 plan (scaled), so arms narrower than about twice it stay with the group's plate)
	public static bool LeadLocal = true;   // (Draft 2m: a leading-edge plate's edges pushed or slid by the edge's own way across)
	public double[] EdgeNX, EdgeNZ;   // (Draft 2m: at a leading-edge plate's edge, the edge's way across from this side; 0, 0 elsewhere)
	bool IsLead(int k) => LeadMade > 0 && k >= LeadFirst && k < LeadFirst + LeadMade;
	public string LeadNote = "";
	public int[] ContId;      // which continent (or big island) each continental cell is part of; -1 at sea
	public int Majors, Minors, Micros, MicrosMade, Continents, BigIslands;
	public static double MeetChance = 0.33, MeetPull = 1.5, ShareFall = 1.6, GapShare = 0.2, IslandGap = 0.45, EdgeLo = 0.04, EdgeHi = 0.15, EdgeFade = 0.03;   // (testing can change them)
	public const int ShapesAcross = 46;   // (the tiny shapes: this many across the map, at every size)
	public double Belt;
	public static double LeadCost = 1000, GroupSpeed = 0.4;   // (Draft 2j: how hard a group's plate finds the ocean ahead of it (so it ends at its leading coast); its slowest pace)
	public static double JoinHigh = 36, JoinWide = 50, SutureHigh = 24, SutureWide = 65;   // (Draft 2i: joins' and old sutures' ranges: x Amp high, x Belt blocks each side)
	public static double KeepCells = 1.5;   // (how far, in plan cells, the coast may wander either way from the crust's edge)
	void MakePlates()
	{
		int NN = N * N;
		Majors = 7; Minors = 8 + (int)Math.Floor(3 * Hash(0, 7, 5001)); Micros = 30 + (int)Math.Floor(11 * Hash(0, 8, 5001));
		int PB = Majors + Minors, P = PB + Micros + LeadMax;   // (Draft 2l: room for the leading-edge plates after the micros)
		PX = new double[P]; PZ = new double[P]; VX = new double[P]; VZ = new double[P]; Cont = new bool[P]; Rate = new double[P];
		var SX = new double[P]; var SZ = new double[P];
		// 1. the tiny shapes (the user's idea): a mosaic of small ragged shapes, 46 across the map at every size (points on a
		// jittered grid; each cell goes to the nearest point after its own place is pushed about by two layers of noise, so the
		// outlines come out ragged). The supercontinent and its pieces are made of whole shapes; today's plates take the plate
		// that covers most of each shape (a tie: the lower plate number), so the plates' edges follow the shapes' outlines
		double gs = (double)N / ShapesAcross; int GX = ShapesAcross, NS = GX * GX; ShapeSize = Math.Max(1, (int)Math.Floor(gs + 0.5));
		var ptx = new double[NS]; var ptz = new double[NS];
		for (int gz = 0; gz < GX; gz++) for (int gx = 0; gx < GX; gx++)
		{
			int s = gz * GX + gx;
			ptx[s] = (gx + 0.1 + 0.8 * Hash(gx, gz, 5101)) * gs; ptz[s] = (gz + 0.1 + 0.8 * Hash(gx, gz, 5102)) * gs;
		}
		Shape = new int[NN];
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			double u = x + 0.5, v = z + 0.5;
			double qx = u + gs * (1.8 * (Fbm(u / (gs * 2.5), v / (gs * 2.5), 3, 5103) - 0.47) + 0.7 * (Fbm(u / (gs * 0.8), v / (gs * 0.8), 2, 5105) - 0.47));
			double qz = v + gs * (1.8 * (Fbm(u / (gs * 2.5), v / (gs * 2.5), 3, 5702) - 0.47) + 0.7 * (Fbm(u / (gs * 0.8), v / (gs * 0.8), 2, 5106) - 0.47));
			int cgx = (int)Math.Floor(qx / gs), cgz = (int)Math.Floor(qz / gs), best = -1; double bd = 1e18;
			for (int dz = -2; dz <= 2; dz++) for (int dx = -2; dx <= 2; dx++)
			{
				int gx = cgx + dx, gz = cgz + dz;
				if (gx < 0 || gz < 0 || gx >= GX || gz >= GX) continue;
				int s = gz * GX + gx; double ex = qx - ptx[s], ez = qz - ptz[s], dd = ex * ex + ez * ez;
				if (dd < bd || (dd == bd && s < best)) { bd = dd; best = s; }
			}
			if (best < 0) best = Math.Min(GX - 1, Math.Max(0, cgz)) * GX + Math.Min(GX - 1, Math.Max(0, cgx));
			Shape[z * N + x] = best;
		}
		// (each shape's cells listed together, so each shape's count of cells on each plate is made one shape at a time)
		var start = new int[NS + 1];
		for (int i = 0; i < NN; i++) start[Shape[i] + 1]++;
		for (int s = 0; s < NS; s++) start[s + 1] += start[s];
		var cellsOf = new int[NN]; var fillAt = (int[])start.Clone();
		for (int i = 0; i < NN; i++) cellsOf[fillAt[Shape[i]]++] = i;
		var shapeCells = new int[NS]; for (int s = 0; s < NS; s++) shapeCells[s] = start[s + 1] - start[s];
		// which shapes touch (side by side), in the order the map is read
		var adj = new List<int>[NS]; for (int s = 0; s < NS; s++) adj[s] = new List<int>();
		void Link(int a, int b) { if (a == b || adj[a].Contains(b)) return; adj[a].Add(b); adj[b].Add(a); }
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			int i = z * N + x;
			if (x + 1 < N) Link(Shape[i], Shape[i + 1]);
			if (z + 1 < N) Link(Shape[i], Shape[i + N]);
		}
		double FromEdge(double px, double pz) => Math.Min(Math.Min(px, pz), Math.Min(N - px, N - pz)) / N;   // (0 at the map's edge, 0.5 in the middle)
		// (each shape's cells in the world's edge sea, EdgeFade of the map: a shape a quarter or more in it is never land)
		var edgeN = new int[NS];
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++) if (FromEdge(x + 0.5, z + 0.5) < EdgeFade) edgeN[Shape[z * N + x]]++;
		// 2. how many continents and big islands, and their shares: 5 to 7 continents and 2 to 4 big islands (by hash). Each
		// continent's share by its rank (as Draft 2g: the ranks shuffled by hash, share = (0.8 to 1.2) / (1 + 1.6 rank), so one or
		// two giants, some mid-size and some small ones); a big island's 0.1 to 0.18 of an average continent
		Continents = 5 + (int)Math.Floor(3 * Hash(0, 0, 5009)); BigIslands = 2 + (int)Math.Floor(3 * Hash(0, 1, 5009));
		int NC = Continents + BigIslands;
		var want = new double[NC]; double shareSum = 0;
		for (int c = 0; c < NC; c++)
		{
			if (c < Continents)
			{
				int rank = 0; double hc = Hash(c, 9, 5005);
				for (int c2 = 0; c2 < Continents; c2++) { double h2 = Hash(c2, 9, 5005); if (h2 < hc || h2 == hc && c2 < c) rank++; }
				want[c] = (0.8 + 0.4 * Hash(c, 0, 5005)) / (1 + ShareFall * rank);
			}
			else want[c] = 0.1 + 0.08 * Hash(c, 0, 5005);
			shareSum += want[c];
		}
		double avgShare = 0; for (int c = 0; c < Continents; c++) avgShare += want[c]; avgShare /= Continents;
		for (int c = Continents; c < NC; c++) { shareSum -= want[c]; want[c] *= avgShare; shareSum += want[c]; }
		for (int c = 0; c < NC; c++) want[c] = 0.4 * NN * want[c] / shareSum;   // (cells)
		// 3. Draft 2j: the forward history (EarthHistory.cs): the supercontinent, its pieces and old blocks, the partners, the
		// drift; today's crust is where the drift ends
		MakeForward(gs, GX, NS, ptx, ptz, shapeCells, adj, edgeN, want);
		// 4. TODAY'S PLATES, laid on the drift. Each group (biggest first; ties: the lower group) gets a plate of its own: a major
		// while 6 are taken (at least one major stays an ocean plate), then a minor; its seed the group's crust cell nearest its
		// middle; it moves the group's way. The young sea's smaller piece gets a minor of its own, moving its group's way plus half
		// a pace away from its partner (so they pull apart). The other majors and minors are ocean plates, seeded as in 2h (the
		// farthest from those placed of a few tries by hash, 8 for a major, 16 for a minor), off the crust where they can be, each
		// drifting its own way. The majors grow out faster (pace 1 to 1.4) than the minors (0.45 to 0.65)
		PlateGroup = new int[P]; for (int k = 0; k < P; k++) PlateGroup[k] = -1;
		PiecePlate = new int[NC]; for (int c = 0; c < NC; c++) PiecePlate[c] = -1;
		var placed = new bool[P];
		{
			var gArea = new int[Groups]; var gsx = new double[Groups]; var gsz = new double[Groups];
			var pArea = new int[NC]; var psx = new double[NC]; var psz = new double[NC];
			for (int i = 0; i < NN; i++)
			{
				int c = ContId[i]; if (c < 0) continue; int g = GroupOf[c];
				gArea[g]++; gsx[g] += i % N + 0.5; gsz[g] += i / N + 0.5; pArea[c]++; psx[c] += i % N + 0.5; psz[c] += i / N + 0.5;
			}
			int Nearest(double mx, double mz, Func<int, bool> ok)
			{
				int bi = -1; double bd = 1e18;
				for (int i = 0; i < NN; i++)
				{
					if (!ok(i)) continue;
					double dx = i % N + 0.5 - mx, dz = i / N + 0.5 - mz, dd = dx * dx + dz * dz;
					if (dd < bd) { bd = dd; bi = i; }
				}
				return bi;
			}
			var gOrder = new List<int>(); for (int g = 0; g < Groups; g++) if (gArea[g] > 0) gOrder.Add(g);
			gOrder.Sort((a, b) => gArea[a] != gArea[b] ? gArea[b].CompareTo(gArea[a]) : a.CompareTo(b));
			int nMaj = 0, nMin = 0;
			foreach (int g in gOrder)
			{
				int k;
				if (nMaj < Majors - 1) k = nMaj++; else if (nMin < Minors) k = Majors + nMin++; else continue;
				int cell = Nearest(gsx[g] / gArea[g], gsz[g] / gArea[g], i => ContId[i] >= 0 && GroupOf[ContId[i]] == g && ContId[i] != YsB);
				if (cell < 0) cell = Nearest(gsx[g] / gArea[g], gsz[g] / gArea[g], i => ContId[i] >= 0 && GroupOf[ContId[i]] == g);
				SX[k] = cell % N + 0.5; SZ[k] = cell / N + 0.5; PlateGroup[k] = g; placed[k] = true;
				for (int c = 0; c < NC; c++) if (GroupOf[c] == g) PiecePlate[c] = k;
			}
			if (YsB >= 0 && pArea[YsB] > 0 && nMin < Minors)
			{
				int k = Majors + nMin++;
				int cell = Nearest(psx[YsB] / pArea[YsB], psz[YsB] / pArea[YsB], i => ContId[i] == YsB);
				SX[k] = cell % N + 0.5; SZ[k] = cell / N + 0.5; PlateGroup[k] = GroupOf[YsB]; placed[k] = true; PiecePlate[YsB] = k;
			}
			for (int k = 0; k < PB; k++)
			{
				if (placed[k]) continue;
				int tries = k < Majors ? 8 : 16; double bx = 0, bz = 0, bd = -2;
				for (int t = 0; t < tries; t++)
				{
					double x = 0.06 + 0.88 * Hash(k * 16 + t, 0, 5001), z = 0.06 + 0.88 * Hash(k * 16 + t, 1, 5001), dm = 1e9;
					for (int j = 0; j < PB; j++) if (placed[j]) { double dx = x - SX[j] / N, dz = z - SZ[j] / N; dm = Math.Min(dm, dx * dx + dz * dz); }
					int ci = Math.Min(N - 1, (int)Math.Floor(z * N)) * N + Math.Min(N - 1, (int)Math.Floor(x * N));
					if (Crust[ci]) dm = -1;   // (on the crust: only if every try is)
					if (dm > bd) { bd = dm; bx = x; bz = z; }
				}
				SX[k] = bx * N; SZ[k] = bz * N; placed[k] = true;
			}
			for (int k = 0; k < PB; k++) Rate[k] = k < Majors ? 1 + 0.4 * Hash(k, 6, 5002) : 0.45 + 0.2 * Hash(k, 6, 5002);
			// every plate (the microplates too) drifts its own way, at 0.4 to 1; a group's plate its group's way
			for (int k = 0; k < P; k++)
			{
				double vx = Hash(k, 3, 5002) * 2 - 1, vz = Hash(k, 4, 5002) * 2 - 1, len = Math.Sqrt(vx * vx + vz * vz);
				if (len < 1e-6) { vx = 1; vz = 0; len = 1; }
				double sp = 0.4 + 0.6 * Hash(k, 5, 5002);
				int g = PlateGroup[k];
				if (g >= 0) { vx = GDirX[g]; vz = GDirZ[g]; len = 1; sp = GroupSpeed + (1 - GroupSpeed) * Hash(k, 5, 5002); }   // (Draft 2j: a drifting group's plate among the fastest)
				if (g >= 0 && YsB >= 0 && PiecePlate[YsB] == k && pArea[YsA] > 0)
				{
					double ux = psx[YsB] / pArea[YsB] - psx[YsA] / pArea[YsA], uz = psz[YsB] / pArea[YsB] - psz[YsA] / pArea[YsA], ul = Math.Sqrt(ux * ux + uz * uz);
					if (ul > 1e-9) { vx += 0.5 * ux / ul; vz += 0.5 * uz / ul; }
					len = Math.Sqrt(vx * vx + vz * vz); if (len < 1e-6) { vx = 1; vz = 0; len = 1; }
				}
				VX[k] = vx / len * sp; VZ[k] = vz / len * sp;
			}
		}
		// the growth: a flood from every seed at once, cheapest first (ties: the lower cell number). A step costs its length, times
		// a slow wobble (so edges curve), divided by the plate's pace; and (Draft 2j) times 0.35 over its own pieces' crust, 5 over
		// another plate's pieces, and, for a group's plate, up to 1 + LeadCost over the ocean ahead of it (so the ocean plates ahead reach its
		// leading coasts, and the plate behind it reaches out to the ridge midway to its neighbours)
		Plate = new int[NN]; Ds = new double[NN];
		var cost = new double[NN]; var done = new bool[NN]; var heap = new Heap(NN * 4);
		for (int i = 0; i < NN; i++) { Plate[i] = -1; cost[i] = 1e18; }
		var wob = new double[NN];
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++) wob[z * N + x] = 0.5 + Fbm((x + 0.5) / (N * 0.1), (z + 0.5) / (N * 0.1), 3, 5012);
		double Over(int j, int p)
		{
			int c = ContId[j];
			if (c >= 0) { int pp = PiecePlate[c]; return pp < 0 ? 1 : pp == p ? 0.35 : 5; }
			int g = PlateGroup[p]; if (g < 0) return 1;
			double dx = j % N + 0.5 - SX[p], dz = j / N + 0.5 - SZ[p], l = Math.Sqrt(dx * dx + dz * dz);
			return l < 1e-9 ? 1 : 1 + LeadCost * Math.Max(0, (dx * GDirX[g] + dz * GDirZ[g]) / l);
		}
		for (int k = 0; k < PB; k++)
		{
			int i = Math.Min(N - 1, (int)Math.Floor(SZ[k])) * N + Math.Min(N - 1, (int)Math.Floor(SX[k]));
			if (Plate[i] >= 0) continue;   // (two seeds in one cell: the first keeps it)
			Plate[i] = k; cost[i] = 0; heap.Push(0, i);
		}
		while (heap.Count > 0)
		{
			double c0 = heap.TopKey; int c = heap.Pop();
			if (done[c] || c0 > cost[c]) continue;
			done[c] = true;
			int cx = c % N, cz = c / N, p = Plate[c];
			for (int k = 0; k < 8; k++)
			{
				int x = cx + DX[k], z = cz + DZ[k];
				if (x < 0 || z < 0 || x >= N || z >= N) continue;
				int j = z * N + x;
				if (done[j]) continue;
				double nc = c0 + (k < 4 ? 1 : SQ2) * (wob[c] + wob[j]) * 0.5 / Rate[p] * Over(j, p);
				if (nc < cost[j]) { cost[j] = nc; Plate[j] = p; heap.Push(nc, j); }
			}
		}
		// each plate's size, and its middle (for which way its edges face): the average of its cells; and how far each cell is
		// from its plate's seed
		var cnt = new int[P]; PlateR = new double[P];
		void Measure()
		{
			for (int k = 0; k < P; k++) { cnt[k] = 0; PX[k] = 0; PZ[k] = 0; }
			for (int z = 0; z < N; z++) for (int x = 0; x < N; x++) { int p = Plate[z * N + x]; PX[p] += x + 0.5; PZ[p] += z + 0.5; cnt[p]++; }
			for (int k = 0; k < P; k++) if (cnt[k] > 0) { PX[k] /= cnt[k]; PZ[k] /= cnt[k]; } else { PX[k] = SX[k]; PZ[k] = SZ[k]; }
			for (int k = 0; k < P; k++) PlateR[k] = Math.Sqrt(cnt[k] / Math.PI) * Cell;
			for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
			{
				int i = z * N + x, p = Plate[i]; double dx = x + 0.5 - SX[p], dz = z + 0.5 - SZ[p];
				Ds[i] = Math.Sqrt(dx * dx + dz * dz);
			}
		}
		// each shape joins the plate that covers most of it (a tie: the lower plate number)
		var cover = new int[P]; var shapePlate = new int[NS];
		for (int s = 0; s < NS; s++)
		{
			int pick = -1;
			for (int q = start[s]; q < start[s + 1]; q++) cover[Plate[cellsOf[q]]]++;
			for (int q = start[s]; q < start[s + 1]; q++)
			{
				int k = Plate[cellsOf[q]];
				if (pick < 0 || cover[k] > cover[pick] || (cover[k] == cover[pick] && k < pick)) pick = k;
			}
			for (int q = start[s]; q < start[s + 1]; q++) cover[Plate[cellsOf[q]]] = 0;
			shapePlate[s] = pick;   // (-1: a shape no cell went to)
		}
		for (int i = 0; i < NN; i++) Plate[i] = shapePlate[Shape[i]];
		Measure();
		// 5. the microplates, at the busiest edges (as on Earth: where the big plates crowd, round junctions and hard pushes). Each
		// shape on a plate edge scores how hard its plate and its neighbours' push together or slide past (the most of them), more
		// where three or more plates meet, less near the map's edges, give or take a hash. The best go first, none within 0.045 of
		// the map of another; each takes the shapes whose points lie within 0.8 to 1.6 shapes of its own (its own always), and
		// drifts its own way. So they add short ranges, island arcs and rifts where the edges are busy, not everywhere
		var busy = new double[NS]; var seenP = new List<int>();
		for (int s = 0; s < NS; s++)
		{
			busy[s] = -1; int a = shapePlate[s]; if (a < 0) continue;
			double best = -1; seenP.Clear();
			foreach (int t in adj[s])
			{
				int b = shapePlate[t]; if (b < 0 || b == a) continue;
				if (!seenP.Contains(b)) seenP.Add(b);
				double nx = PX[b] - PX[a], nz = PZ[b] - PZ[a], len = Math.Max(1e-9, Math.Sqrt(nx * nx + nz * nz));
				double rx = VX[a] - VX[b], rz = VZ[a] - VZ[b];
				double v = Math.Abs(rx * nx + rz * nz) / len + 0.5 * Math.Abs(rx * nz - rz * nx) / len;
				if (v > best) best = v;
			}
			if (best < 0) continue;
			busy[s] = (best + (seenP.Count >= 2 ? 0.6 : 0)) * (0.5 + Hash(s, 3, 5706)) * Smoothstep(0.04, 0.12, FromEdge(ptx[s], ptz[s]));
		}
		var order = new List<int>(); for (int s = 0; s < NS; s++) if (busy[s] > 0) order.Add(s);
		order.Sort((p, q) => busy[q] != busy[p] ? busy[q].CompareTo(busy[p]) : p.CompareTo(q));   // (the busiest first; ties: the lower shape)
		var microAt = new int[NS]; for (int s = 0; s < NS; s++) microAt[s] = -1;
		double sep = 0.045 * N; MicrosMade = 0;
		foreach (int s in order)
		{
			if (MicrosMade >= Micros) break;
			double cx = ptx[s], cz = ptz[s]; bool ok = true;
			for (int m = 0; m < MicrosMade; m++) { double dx = cx - SX[PB + m], dz = cz - SZ[PB + m]; if (dx * dx + dz * dz < sep * sep) { ok = false; break; } }
			if (!ok) continue;
			int k = PB + MicrosMade; SX[k] = cx; SZ[k] = cz;
			double rm = gs * (0.8 + 0.8 * Hash(MicrosMade, 0, 5121));
			int g0x = (int)Math.Floor(cx / gs), g0z = (int)Math.Floor(cz / gs), R = (int)Math.Ceiling(rm / gs) + 1;
			for (int gz = g0z - R; gz <= g0z + R; gz++) for (int gx = g0x - R; gx <= g0x + R; gx++)
			{
				if (gx < 0 || gz < 0 || gx >= GX || gz >= GX) continue;
				int t = gz * GX + gx; if (shapePlate[t] < 0 || microAt[t] >= 0) continue;
				double dx = ptx[t] - cx, dz = ptz[t] - cz;
				if (t == s || dx * dx + dz * dz <= rm * rm) microAt[t] = k;
			}
			MicrosMade++;
		}
		Plates = PB + MicrosMade;
		for (int s = 0; s < NS; s++) if (microAt[s] >= 0) shapePlate[s] = microAt[s];
		for (int i = 0; i < NN; i++) Plate[i] = shapePlate[Shape[i]];
		Measure();
		// 6. (Draft 2l, the user: "Make every drifting group's leading coast meet an ocean plate, so every world gets 3-5%".) A
		// group's plate ends at its leading coast. Each ocean cell on a group's plate looks back (against the group's way) and
		// ahead, half a cell at a time, over any plate, until it meets crust or the map's edge. It is the plate's ocean ahead if the
		// look back ends on crust of its own group and the look ahead does not (that would be water between its own pieces), and
		// it is more than the young sea's width + 3 cells from the plate edge between the young sea's pair (so that edge still runs
		// out to sea, and the young sea is cut all the way through). Both looks start from the cell's middle moved sideways (square
		// to the group's way) by 2 LeadWob N (Fbm(x / 0.08 N, z / 0.08 N, 2 octaves, salt 5400) - 0.47) cells, so the sides of the
		// ocean ahead wander instead of running dead straight.
		// The ocean ahead goes to the ocean plates beside it (a flood, 4 ways, from every cell of it next to an ocean plate's cell,
		// those taken in cell order), so the plate edge lies on the coast. The leading coast: each crust cell of a group whose
		// first cell ahead is ocean ahead, or an ocean plate's ocean; it and that cell are marked with how squarely the coast faces
		// the group's way (the coast's way out to sea from the crust smoothed over 3 cells, twice; 0 to 1), and an edge at a marked
		// cell pushes at least the group's pace times that (Leading). Ocean ahead that no ocean plate touches (shut in by its own plate and the map's edge) becomes
		// a plate of its own, each piece (4 ways) as big as a quarter of a shape or more (LeadMin), the biggest first (ties: the lower first cell), at
		// most LeadMax: a leading-edge plate (as the Nazca plate ahead of South America), moving toward the coast at 0.4 to 1.
		// Smaller pieces stay with the group's plate
		LeadFace = new double[NN]; LeadMade = 0;
		if (LeadEdge)
		{
			var ahead = new bool[NN];
			int lcN = 0, ahN = 0, movN = 0;
			var lcPairs = new List<int>();   // (each leading coast cell and its first cell ahead, for the note)
			var crSm = new double[NN]; for (int i = 0; i < NN; i++) crSm[i] = Crust[i] ? 1 : 0;
			Blur(crSm, 3); Blur(crSm, 3);
			bool OwnCrust(int j, int g) { int cj = ContId[j]; return Crust[j] && cj >= 0 && GroupOf[cj] == g; }
			// (the look from cell i, sgn 1 ahead or -1 back: the first crust cell; -1 off the map)
			int Look(int i, int a, int g, double sgn)
			{
				double off = 2 * LeadWob * N * (Fbm((i % N + 0.5) / (0.08 * N), (i / N + 0.5) / (0.08 * N), 2, 5400) - 0.47);
				double x0 = i % N + 0.5 - off * GDirZ[g], z0 = i / N + 0.5 + off * GDirX[g]; int last = i;
				for (int st = 1; ; st++)
				{
					double x = x0 + sgn * 0.5 * st * GDirX[g], z = z0 + sgn * 0.5 * st * GDirZ[g];
					if (x < 0 || z < 0 || x >= N || z >= N) return -1;
					int j = (int)Math.Floor(z) * N + (int)Math.Floor(x);
					if (j == last) continue;
					last = j;
					if (Crust[j]) return j;
				}
			}
			double[] ysD = null;   // (how far from the young sea's pair's plate edge)
			if (YsA >= 0 && YsB >= 0 && PiecePlate[YsA] >= 0 && PiecePlate[YsB] >= 0 && PiecePlate[YsA] != PiecePlate[YsB])
			{
				int ya = PiecePlate[YsA], yb = PiecePlate[YsB];
				ysD = Chamfer(j =>
				{
					int p0 = Plate[j]; if (p0 != ya && p0 != yb) return false; int q0 = p0 == ya ? yb : ya, jx = j % N, jz = j / N;
					return jx + 1 < N && Plate[j + 1] == q0 || jx > 0 && Plate[j - 1] == q0 || jz + 1 < N && Plate[j + N] == q0 || jz > 0 && Plate[j - N] == q0;
				});
			}
			double ysKeep = Math.Max(2, 0.006 * N) + 3;
			for (int i = 0; i < NN; i++)
			{
				if (Crust[i]) continue;
				int a = Plate[i], g = PlateGroup[a]; if (g < 0) continue;
				if (ysD != null && ysD[i] <= ysKeep) continue;
				int bk = Look(i, a, g, -1); if (bk < 0 || !OwnCrust(bk, g)) continue;
				int fw = Look(i, a, g, 1); if (fw >= 0 && OwnCrust(fw, g)) continue;
				ahead[i] = true; ahN++;
			}
			for (int i = 0; i < NN; i++)
			{
				if (!Crust[i]) continue;
				int a = Plate[i], g = PlateGroup[a]; if (g < 0 || !OwnCrust(i, g)) continue;
				double x0 = i % N + 0.5, z0 = i / N + 0.5; int j = i;
				for (int st = 1; j == i; st++)
				{
					double x = x0 + 0.5 * st * GDirX[g], z = z0 + 0.5 * st * GDirZ[g];
					if (x < 0 || z < 0 || x >= N || z >= N) { j = -1; break; }
					j = (int)Math.Floor(z) * N + (int)Math.Floor(x);
				}
				if (j < 0 || Crust[j] || !(ahead[j] || PlateGroup[Plate[j]] < 0)) continue;
				int ix = i % N, iz = i / N;
				double gx = crSm[iz * N + Math.Min(N - 1, ix + 1)] - crSm[iz * N + Math.Max(0, ix - 1)];
				double gz = crSm[Math.Min(N - 1, iz + 1) * N + ix] - crSm[Math.Max(0, iz - 1) * N + ix], gl = Math.Sqrt(gx * gx + gz * gz);
				double f = gl < 1e-9 ? 1 : Math.Max(0, -(gx * GDirX[g] + gz * GDirZ[g]) / gl);
				if (f <= 0) continue;
				LeadFace[i] = Math.Max(LeadFace[i], f); LeadFace[j] = Math.Max(LeadFace[j], f); lcN++; lcPairs.Add(i); lcPairs.Add(j);
			}
			var got = new bool[NN]; var lq = new int[NN]; int qh = 0, qt = 0;
			for (int i = 0; i < NN; i++)
			{
				if (!ahead[i]) continue;
				int cx = i % N, cz = i / N;
				for (int k = 0; k < 4; k++)
				{
					int x = cx + DX[k], z = cz + DZ[k];
					if (x < 0 || z < 0 || x >= N || z >= N) continue;
					int j = z * N + x;
					if (ahead[j] || PlateGroup[Plate[j]] >= 0) continue;
					Plate[i] = Plate[j]; got[i] = true; lq[qt++] = i; break;
				}
			}
			while (qh < qt)
			{
				int c = lq[qh++], cx = c % N, cz = c / N;
				for (int k = 0; k < 4; k++)
				{
					int x = cx + DX[k], z = cz + DZ[k];
					if (x < 0 || z < 0 || x >= N || z >= N) continue;
					int j = z * N + x;
					if (!ahead[j] || got[j]) continue;
					Plate[j] = Plate[c]; got[j] = true; lq[qt++] = j;
				}
			}
			movN = qt;
			// (Draft 2m: no thin slivers. The ocean ahead left over is opened: a cell of it stays only if a disc of radius thinR
			// (LeadThin cells at a 512 plan, scaled; at least 2) that holds it fits wholly in the leftover and continent (continent
			// counts as solid, so the ocean in a coast's bays stays). Arms, strips and specks of open ocean narrower than about twice
			// that stay with the group's plate. The map's edge doesn't count as an edge here)
			int thinR = LeadThin <= 0 ? 0 : Math.Max(2, (int)Math.Floor(LeadThin * N / 512.0 + 0.5)), thinCut = 0, lcCut = 0;
			if (thinR > 0)
			{
				var dIn = Chamfer(j => !(ahead[j] && !got[j]) && !Crust[j]);
				var dCore = Chamfer(j => (ahead[j] && !got[j] || Crust[j]) && dIn[j] >= thinR);
				for (int i = 0; i < NN; i++) if (ahead[i] && !got[i] && dCore[i] >= thinR) { ahead[i] = false; thinCut++; }
			}
			// the pieces left: each its own plate if big enough
			var pieceOf = new int[NN]; for (int i = 0; i < NN; i++) pieceOf[i] = -1;
			var pFirst = new List<int>(); var pSize = new List<int>(); var pCells = new List<int>();
			for (int i = 0; i < NN; i++)
			{
				if (!ahead[i] || got[i] || pieceOf[i] >= 0) continue;
				int id = pFirst.Count; pFirst.Add(i); pieceOf[i] = id; int h0 = pCells.Count; pCells.Add(i);
				for (int h = h0; h < pCells.Count; h++)
				{
					int c = pCells[h], cx = c % N, cz = c / N;
					for (int k = 0; k < 4; k++)
					{
						int x = cx + DX[k], z = cz + DZ[k];
						if (x < 0 || z < 0 || x >= N || z >= N) continue;
						int j = z * N + x;
						if (!ahead[j] || got[j] || pieceOf[j] >= 0) continue;
						pieceOf[j] = id; pCells.Add(j);
					}
				}
				pSize.Add(pCells.Count - h0);
			}
			var pOrder = new List<int>(); for (int q = 0; q < pFirst.Count; q++) pOrder.Add(q);
			pOrder.Sort((u, w) => pSize[u] != pSize[w] ? pSize[w].CompareTo(pSize[u]) : pFirst[u].CompareTo(pFirst[w]));
			var pPlate = new int[pFirst.Count]; for (int q = 0; q < pFirst.Count; q++) pPlate[q] = -1;
			double minCells = LeadMin * gs * gs; int newN = 0; LeadFirst = PB + MicrosMade;
			// (Draft 2m: a piece becomes a plate only if somewhere it is 2 x thinR cells across or more (a cell of it at least thinR
			// from every cell not in it, continent and all; the map's edge doesn't count) and it holds the first cell ahead of at least a quarter of a shape's width of
			// leading coast (LeadCoastMin, at least 2 cells): a piece out at sea, away from the coast it would meet, stays with the group's plate)
			var pCoast = new int[pFirst.Count]; var pSeen = new bool[NN]; var pWide = new double[pFirst.Count];
			if (thinR > 0) { var dW = Chamfer(j => !(ahead[j] && !got[j])); for (int i = 0; i < NN; i++) if (pieceOf[i] >= 0) pWide[pieceOf[i]] = Math.Max(pWide[pieceOf[i]], dW[i]); }
			for (int q = 1; q < lcPairs.Count; q += 2) { int j = lcPairs[q]; if (pieceOf[j] >= 0 && !got[j] && ahead[j] && !pSeen[j]) { pSeen[j] = true; pCoast[pieceOf[j]]++; } }
			int coastMin = LeadCoastMin <= 0 ? 0 : Math.Max(2, (int)Math.Floor(LeadCoastMin * gs + 0.5));
			foreach (int q in pOrder)
			{
				if (LeadMade >= LeadMax || pSize[q] < minCells) break;
				if (pCoast[q] < coastMin) { lcCut += pSize[q]; continue; }
				if (thinR > 0 && pWide[q] < thinR) { thinCut += pSize[q]; continue; }   // (Draft 2m: a piece thinner than 2 x thinR everywhere, as a cove's water between headlands, stays too)
				int k = PB + MicrosMade + LeadMade++, g = PlateGroup[Plate[pFirst[q]]];
				double sp = 0.4 + 0.6 * Hash(k, 5, 5002);
				VX[k] = -GDirX[g] * sp; VZ[k] = -GDirZ[g] * sp; PlateGroup[k] = -1; Rate[k] = 1; pPlate[q] = k;
				double mx = 0, mz = 0; int n = 0;
				for (int i = 0; i < NN; i++) if (pieceOf[i] == q) { mx += i % N + 0.5; mz += i / N + 0.5; n++; }
				SX[k] = mx / n; SZ[k] = mz / n; newN += n;
			}
			for (int i = 0; i < NN; i++) if (pieceOf[i] >= 0 && pPlate[pieceOf[i]] >= 0) Plate[i] = pPlate[pieceOf[i]];
			Plates = PB + MicrosMade + LeadMade;
			int lcMeet = 0; var gLc = new int[Groups]; var gMeet = new int[Groups];
			for (int q = 0; q < lcPairs.Count; q += 2)
			{
				int gq = GroupOf[ContId[lcPairs[q]]]; gLc[gq]++;
				if (PlateGroup[Plate[lcPairs[q + 1]]] < 0 && Plate[lcPairs[q + 1]] != Plate[lcPairs[q]]) { lcMeet++; gMeet[gq]++; }
			}
			var gNote = new List<string>(); for (int gq = 0; gq < Groups; gq++) if (gLc[gq] > 0) gNote.Add($"{gq} {100 * gMeet[gq] / gLc[gq]}% of {gLc[gq]}");
			LeadNote = $"leading coasts: {lcN} cells, {100.0 * lcMeet / Math.Max(1, lcN):0}% meeting an ocean plate (by group: {string.Join(", ", gNote)}); the ocean ahead {100.0 * ahN / NN:0.0}% of the map, {100.0 * movN / Math.Max(1, ahN):0}% of it to the ocean plates beside it, {100.0 * newN / Math.Max(1, ahN):0}% to {LeadMade} leading-edge plates, {100.0 * (ahN - movN - newN) / Math.Max(1, ahN):0}% left ({100.0 * thinCut / Math.Max(1, ahN):0}% of it thin arms, under {2 * thinR} cells, {100.0 * lcCut / Math.Max(1, ahN):0}% pieces away from the leading coast)";
			Measure();
		}
		// (a plate mostly continent counts as continental, for the pictures and notes only)
		var crustN = new int[P];
		for (int i = 0; i < NN; i++) if (Crust[i]) crustN[Plate[i]]++;
		for (int k = 0; k < P; k++) Cont[k] = crustN[k] * 2 > cnt[k];
		ContNote = $"{Majors} major, {Minors} minor, {MicrosMade} micro and {LeadMade} leading-edge plates; {LeadNote}; {DriftNote}";
		EdgeSweep();
		YoungSeaNote = MakeYoungSea();   // (Draft 2j: the young sea at the plate edge between its pair)
	}

	// how far each cell is from its plate's edge, which plate is over it, and the crust on each side of that edge: a two-pass
	// distance sweep (again after a young sea takes some continent, Draft 2i)
	void EdgeSweep()
	{
		int NN = N * N;
		Bd = new double[NN]; Other = new int[NN]; EdgeCrust = new bool[NN]; OtherCrust = new bool[NN]; EdgeLead = new double[NN];
		EdgeNX = new double[NN]; EdgeNZ = new double[NN];
		for (int i = 0; i < NN; i++) { Bd[i] = 1e9; Other[i] = -1; }
		// (Draft 2m: where a leading-edge plate meets another plate, the edge's way across, from the leading-edge plates' cells
		// blurred over about 3 cells (scaled), twice: out of the leading-edge plate on its side, into it on the other)
		double[] lsm = null;
		if (LeadLocal && LeadMade > 0)
		{
			lsm = new double[NN]; for (int i = 0; i < NN; i++) lsm[i] = IsLead(Plate[i]) ? 1 : 0;
			int lr = Math.Max(2, (int)Math.Floor(3 * N / 512.0 + 0.5)); Blur(lsm, lr); Blur(lsm, lr);
		}
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			int i = z * N + x, p = Plate[i];
			int j = x + 1 < N && Plate[i + 1] != p ? i + 1 : x > 0 && Plate[i - 1] != p ? i - 1
				: z + 1 < N && Plate[i + N] != p ? i + N : z > 0 && Plate[i - N] != p ? i - N : -1;
			if (j >= 0)
			{
				Bd[i] = 0.5; Other[i] = Plate[j]; EdgeCrust[i] = Crust[i]; OtherCrust[i] = Crust[j]; EdgeLead[i] = LeadFace != null ? Math.Max(LeadFace[i], LeadFace[j]) : 0;
				if (lsm != null && IsLead(p) != IsLead(Plate[j]))
				{
					double gx = lsm[z * N + Math.Min(N - 1, x + 1)] - lsm[z * N + Math.Max(0, x - 1)], gz = lsm[Math.Min(N - 1, z + 1) * N + x] - lsm[Math.Max(0, z - 1) * N + x];
					double gl = Math.Sqrt(gx * gx + gz * gz), sg = IsLead(p) ? -1 : 1;
					if (gl > 1e-9) { EdgeNX[i] = sg * gx / gl; EdgeNZ[i] = sg * gz / gl; }
				}
			}
		}
		void Try(int i, int j, double w)
		{
			if (Plate[j] == Plate[i] && Bd[j] + w < Bd[i]) { Bd[i] = Bd[j] + w; Other[i] = Other[j]; EdgeCrust[i] = EdgeCrust[j]; OtherCrust[i] = OtherCrust[j]; EdgeLead[i] = EdgeLead[j]; EdgeNX[i] = EdgeNX[j]; EdgeNZ[i] = EdgeNZ[j]; }
		}
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			int i = z * N + x;
			if (x > 0) Try(i, i - 1, 1);
			if (z > 0) { Try(i, i - N, 1); if (x > 0) Try(i, i - N - 1, SQ2); if (x + 1 < N) Try(i, i - N + 1, SQ2); }
		}
		for (int z = N - 1; z >= 0; z--) for (int x = N - 1; x >= 0; x--)
		{
			int i = z * N + x;
			if (x + 1 < N) Try(i, i + 1, 1);
			if (z + 1 < N) { Try(i, i + N, 1); if (x + 1 < N) Try(i, i + N + 1, SQ2); if (x > 0) Try(i, i + N - 1, SQ2); }
		}
	}

	// ---- height: continents and ocean floors, raised and lowered where plates meet ----
	void MakeHeight()
	{
		int NN = N * N; Elev = new double[NN]; Edge = new byte[NN]; Lift = new double[NN]; Cut = new double[NN];
		double A = Amp, C = Cell, cs = Cell / Belt;   // (cs: a cell in 2048 blocks, 4 from 2048 up)
		// hot spots: 2 to 4 at every size (Draft 2h: the same kind of planet at every size), anywhere away from the edges
		int hs = 2 + (int)Math.Floor(Hash(0, 0, 5050) * 3);
		var hx = new double[hs]; var hz = new double[hs];
		for (int k = 0; k < hs; k++) { hx[k] = N * (0.15 + 0.7 * Hash(k, 1, 5050)); hz[k] = N * (0.15 + 0.7 * Hash(k, 2, 5050)); Volcanoes.Add(new[] { (int)Math.Floor(hx[k]), (int)Math.Floor(hz[k]), 4 }); }
		double top = Room - 6, deep = Math.Min(Room - 16, 50 * A), margin = Math.Max(48, Size * EdgeFade);
		// Draft 2i: each hot spot's trail: the plate over it has carried its earlier volcanoes off the way it drifts, 0.035 of the
		// map per age at speed 1; one every half age, back 4 ages (8 a trail), dropped where off the map
		var trailPts = new List<double[]>();
		for (int k = 0; k < hs; k++)
		{
			int hi = Math.Min(N - 1, (int)Math.Floor(hz[k])) * N + Math.Min(N - 1, (int)Math.Floor(hx[k])), p = Plate[hi];
			for (int j = 1; j <= 2 * HistSteps; j++)
			{
				double ag = j * 0.5; var (tx, tz) = TrailAt(hx[k], hz[k], p, ag);   // (Draft 2j: along the path of the plate over it)
				if (tx < 0 || tz < 0 || tx >= N || tz >= N) continue;
				var pt = new[] { tx, tz, ag, k }; trailPts.Add(pt); Trails.Add(pt);
			}
		}
		// first, how strongly each kind of plate edge works on each cell (from its nearest edge), then those are evened out, so a
		// mountain range tapers off where plates meet instead of stopping dead: the kinds that are the same on both sides of an
		// edge across both plates, the one-sided kinds (a coast's range, a trench, an island chain) within each plate
		RangeYoung = new double[NN]; RangeMid = new double[NN]; RangeOld = new double[NN];
		var up = new double[NN]; var coast = new double[NN]; var trench = new double[NN]; var arc = new double[NN];
		var rift = new double[NN]; var mid = new double[NN]; var slide = new double[NN]; var shelf = new double[NN];
		var sz = new double[NN]; var coastL = new double[NN]; var szL = new double[NN];   // (Draft 2l: the marked leading coasts' coastal ranges; where they set the size, sized below)   // (how big the plates either side of the nearest edge are: small plates get narrower, lower ranges)
		for (int i = 0; i < NN; i++)
		{
			int a = Plate[i], b = Other[i];
			sz[i] = b < 0 ? 1 : Math.Max(0.3, Math.Min(2, Math.Min(PlateR[a], PlateR[b]) / (330 * Belt)));
			if (b < 0) continue;
			double nx = PX[b] - PX[a], nz = PZ[b] - PZ[a], len = Math.Sqrt(nx * nx + nz * nz);
			if (EdgeNX[i] != 0 || EdgeNZ[i] != 0) { nx = EdgeNX[i]; nz = EdgeNZ[i]; len = 1; }   // (Draft 2m: a leading-edge plate's edge, by its own way across)
			double rx = VX[a] - VX[b], rz = VZ[a] - VZ[b];
			double conv = (rx * nx + rz * nz) / len, shear = Math.Abs(rx * nz - rz * nx) / len;
			bool ca = EdgeCrust[i], cb = OtherCrust[i];   // (the crust on each side of the edge: a plate carries both)
			bool lead = Leading(a, b, ca, cb, nx, nz, len, ref conv, EdgeLead[i]);
			if (lead && EdgeLead[i] > 0) { sz[i] = 0; szL[i] = 1; }   // (Draft 2l: a marked leading coast's size is the world's LeadSz, below)
			// how active this edge is (one number per pair of plates, by hash): about a quarter of edges are quiet (an old join between
			// continents, or a coast with only a shelf), a quarter work at full strength, the rest in between
			double act = Smoothstep(0.2, 0.8, Hash(Math.Min(a, b), Math.Max(a, b), 5004));
			if (lead) act = Math.Max(act, LeadAct);
			byte kind = 0;
			if (conv > 0.1)
			{
				double k = Math.Min(1, conv / 0.8) * act;
				if (ca && cb) { up[i] = k; kind = 1; }
				else if (ca) { if (lead && EdgeLead[i] > 0) coastL[i] = k; else coast[i] = k; kind = 2; }
				else if (cb) { trench[i] = k; kind = 2; }
				else { kind = 3; if (a < b) arc[i] = k; else trench[i] = 0.8 * k; }
			}
			else if (conv < -0.1)
			{
				double k = Math.Min(1, -conv / 0.8) * act;
				if (ca && cb) rift[i] = k; else if (!ca && !cb) mid[i] = k;
				kind = 4;
			}
			else if (shear > 0.4) { slide[i] = act; kind = 5; }
			if (Bd[i] <= 1.5 && act > 0) Edge[i] = kind;
		}
		int rr = Math.Max(2, (int)Math.Floor(48.0 * Belt / C + 0.5));
		foreach (var f in new[] { up, rift, mid, slide, sz, szL }) { Blur(f, rr); Blur(f, rr); }
		var oceanDist = Chamfer(j => !Crust[j]);   // (how far each cell is from ocean floor: where a continent's coast is)
		foreach (var f in new[] { coast, coastL, trench, arc }) { PlateBlur(f, rr); PlateBlur(f, rr); }
		// (the ocean's shelf: shallow by every coast, deepening over about 160 blocks, whether a plate's edge is there or not)
		var contDist = Chamfer(j => Crust[j]);
		for (int i = 0; i < NN; i++) shelf[i] = Crust[i] ? 0 : 1 - Smoothstep(0, 160 * Wide * (1 + 0.8 * RiftBorn[i]), contDist[i] * C);   // (wider off a rift-born coast)
		// (where two continents joined: how far each cell is from the join)
		var joinDist = Chamfer(j =>
		{
			int c0 = ContId[j]; if (c0 < 0) return false; int jx = j % N, jz = j / N;
			return jx + 1 < N && ContId[j + 1] >= 0 && ContId[j + 1] != c0 || jx > 0 && ContId[j - 1] >= 0 && ContId[j - 1] != c0
				|| jz + 1 < N && ContId[j + N] >= 0 && ContId[j + N] != c0 || jz > 0 && ContId[j - N] >= 0 && ContId[j - N] != c0;
		});
		// (Draft 2l: one number for the world, LeadSz, sizes the marked leading coasts' ranges (as the plates' size factor: their
		// width, and their height a little, as for every edge), so the young ranges come to about LeadTarget of the continents (the
		// user: 3-5% in every world): narrower in a world with much leading coast, wider in one with little. The size, like every
		// edge's, is smoothed over the cells round it: each cell's is its others' part plus LeadSz times its leading part. The young
		// ranges are counted on the crust as the plan's note counts them on the land: raised 30 x Tall or more, with young the
		// strongest of the three ages. LeadSz: found by halving LeadSMin to LeadSMax 24 times (the upper end kept), or the limit
		// if the share can't be reached. If even LeadSMin gives too much (a world with very long leading coasts), the leading
		// coasts' ranges get lower too: LeadH, their strength, found the same way from LeadHMin to 1)
		LeadSz = 1; LeadH = 1;
		if (LeadEdge && LeadTarget > 0)
		{
			var cl = new List<int>(); var pRo = new List<double>(); var pWo = new List<double>(); var pM = new List<double>(); var pT = new List<double>();
			double sp = 150 * Wide;
			for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
			{
				int i = z * N + x; if (!Crust[i]) continue;
				double u = x + 0.5, v = z + 0.5;
				double rough = 0.55 + 0.9 * Fbm(u * cs / 70.0, v * cs / 70.0, 3, 5030);
				double wob = (Fbm(u * C / (4 * sp), v * C / (4 * sp), 2, 5300) - 0.47) * 1.2 * sp;
				double Rid(double dist) { double q = (Math.Abs(dist) + wob) / sp + 0.5; double f = q - Math.Floor(q); return 0.45 + 2.2 * f * (1 - f); }
				double jT = JoinHigh * A * Bump(joinDist[i] * C, JoinWide * Belt) * rough * Rid(joinDist[i] * C) * (1 - Math.Min(1, up[i]));
				double oT = SutureHigh * A * Bump(SutureDist[i] * C, SutureWide * Belt) * (0.6 + 0.4 * rough) * (0.6 + 0.4 * Rid(SutureDist[i] * C)) * (1 - Math.Min(1, up[i])) * (1 - Bump(joinDist[i] * C, JoinWide * Belt));
				cl.Add(i); pRo.Add(rough); pWo.Add(wob); pM.Add(Math.Max(jT, oT)); pT.Add(jT + oT);
			}
			int need = (int)Math.Ceiling(LeadTarget * cl.Count);
			int Count(double s, double h)
			{
				int n = 0;
				for (int q = 0; q < cl.Count; q++)
				{
					int i = cl[q]; double d = Bd[i] * C, rough = pRo[q], wob = pWo[q];
					double Rid(double dist) { double qq = (Math.Abs(dist) + wob) / sp + 0.5; double f = qq - Math.Floor(qq); return 0.45 + 2.2 * f * (1 - f); }
					double s1 = sz[i] + s * szL[i], w = s1 * Belt, am = 0.45 + 0.55 * Math.Min(1, s1);
					double y = 85 * A * am * up[i] * Bump(d, 110 * w) * rough * Rid(d)
					         + (coast[i] + h * coastL[i]) * (12 * Tall * Bump(d, 100 * w) + 55 * A * am * Bump(d - 45 * w, 75 * w) * rough * Rid(d - 45 * w))
					         - 18 * A * trench[i] * Bump(d - 6 * w, 22 * w) + 18 * A * am * arc[i] * Bump(d - 20 * w, 34 * w) * (1 - shelf[i]);
					if (y + pT[q] >= 30 * Tall && y >= pM[q]) n++;
				}
				return n;
			}
			double lo = LeadSMin, hi = LeadSMax;
			if (Count(hi, 1) < need) LeadSz = hi;
			else if (Count(lo, 1) >= need)
			{
				LeadSz = lo; double hl = LeadHMin, hh = 1;
				if (Count(lo, hl) >= need) LeadH = hl;
				else { for (int it = 0; it < 24; it++) { double mm = 0.5 * (hl + hh); if (Count(lo, mm) >= need) hh = mm; else hl = mm; } LeadH = hh; }
			}
			else { for (int it = 0; it < 24; it++) { double mm = 0.5 * (lo + hi); if (Count(mm, 1) >= need) hi = mm; else lo = mm; } LeadSz = hi; }
		}
		for (int i = 0; i < NN; i++) { sz[i] += LeadSz * szL[i]; coast[i] += LeadH * coastL[i]; }
		double keep = KeepCells;   // (cells: from 2048 up the plan grid is the same 512 across, so this is the same share of the world)
		GUp = up; GCoast = coast; GArc0 = arc; GRift0 = rift; GMid0 = mid; GShelf = shelf; GSz = sz; GOceanDist = oceanDist; GJoinDist = joinDist; GContDist = contDist; HotX = hx; HotZ = hz;
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			int i = z * N + x, a = Plate[i], b = Other[i];
			double u = x + 0.5, v = z + 0.5, d = Bd[i] * C;   // (d in blocks)
			double t = Ds[i] / (Ds[i] + Bd[i]);              // 0 at the plate's middle, 1 at its edge
			double detail = Fbm(u / (N * 0.05), v / (N * 0.05), 4, 5020) - 0.47;
			// (Draft 2j: the plan's hills and roughness by share of the map, 90 and 70 blocks apart in a 2048 world, so a big world's
			// 64-block cells don't catch them as speckle; the blocks (2b) add hills that size)
			double hills = Fbm(u * cs / 90.0, v * cs / 90.0, 3, 5021) - 0.47;
			double ridge = Fbm(u * cs / 70.0, v * cs / 70.0, 3, 5030);
			// a continent slopes down to the sea near an ocean plate, or where it's pulling apart hard (a weaker rift stays a valley on
			// land), not where it meets another continent
			double fall = Math.Min(1, (1 - Smoothstep(0, 120 * Belt * (1 + 0.8 * RiftBorn[i]), oceanDist[i] * C)) + Math.Max(0, rift[i] - 0.8) * 5 * (1 - Smoothstep(0, 120 * Belt, d)));   // (Draft 2i: the coastal lowland a share of the map)
			fall *= 1 - 0.6 * Math.Min(1, coast[i]);   // (over a sinking ocean the land stays high behind its coastal range: a narrow coastal plain, no lagoons)
			// (continents: highest in the middle, the coast at the plate's edge where it faces an ocean or pulls apart; oceans: a shelf by a
			// continent, deepening over about 160 blocks)
			double e = Crust[i] ? Tall * (20 - 24 * fall + detail * 30 + hills * 14)   // (Draft 2j: lowland heights x Tall, as tall as the world)
			                    : -28 * A + Tall * detail * 10 + shelf[i] * (28 * A - 6 * Tall);
			double rough = 0.55 + 0.9 * ridge, w = sz[i] * Belt, am = 0.45 + 0.55 * Math.Min(1, sz[i]);   // (edge widths and heights by the plates' size; widths a share of the map)
			// Draft 2i: a range is a belt of parallel ridges and valleys about 150 x Wide blocks apart (so a big world's wider belt
			// has more of them, as steep and tall): a crest on the line itself, 0.45 of the height in the valleys; the ridges wander a
			// little. Rid(dist): 0.45 to 1
			double sp = 150 * Wide, wob = (Fbm(u * C / (4 * sp), v * C / (4 * sp), 2, 5300) - 0.47) * 1.2 * sp;
			double Rid(double dist) { double q = (Math.Abs(dist) + wob) / sp + 0.5; double f = q - Math.Floor(q); return 0.45 + 2.2 * f * (1 - f); }
			double e0 = e;
			e += 85 * A * am * up[i] * Bump(d, 110 * w) * rough * Rid(d);                              // continents meet: high ranges
			e += coast[i] * (12 * Tall * Bump(d, 100 * w) + 55 * A * am * Bump(d - 45 * w, 75 * w) * rough * Rid(d - 45 * w));   // over a sinking ocean: a coastal range
			e -= 18 * A * trench[i] * Bump(d - 6 * w, 22 * w);                                            // the sinking ocean's trench
			e += 18 * A * am * arc[i] * Bump(d - 20 * w, 34 * w) * (1 - shelf[i]);   // an island chain's ridge (under the sea: the islands come after)
			RangeYoung[i] = e - e0;   // (Draft 2i: the young ranges, where plates push now)
			// where two continents joined (later than the gathering: middle-aged): a worn range along the join (unless plates push
			// there anyway, which raise their own, young one)
			double jT = Crust[i] ? JoinHigh * A * Bump(joinDist[i] * C, JoinWide * Belt) * rough * Rid(joinDist[i] * C) * (1 - Math.Min(1, up[i])) : 0;
			// Draft 2i: an old suture, where a continent's old blocks collided as the supercontinent came together: low, broad and
			// rounded (worn for ages: less of the ground's roughness), not over a join or a young range
			double oT = Crust[i] ? SutureHigh * A * Bump(SutureDist[i] * C, SutureWide * Belt) * (0.6 + 0.4 * rough) * (0.6 + 0.4 * Rid(SutureDist[i] * C)) * (1 - Math.Min(1, up[i])) * (1 - Bump(joinDist[i] * C, JoinWide * Belt)) : 0;
			e += jT + oT; RangeMid[i] = jT; RangeOld[i] = oT;
			double e1 = e;
			e -= rift[i] * Tall * (6 + 26 * Smoothstep(0.7, 1, rift[i])) * Bump(d, 34 * w);                     // rift valleys (shallow; below the sea only where plates pull apart hard)
			Lift[i] = e1 - e0; Cut[i] = e1 - e;
			e += 12 * mid[i] * Bump(d, 50 * w);                                                   // ridges under the sea
			e += 6 * slide[i] * Bump(d, 30 * w) * (ridge * 2 - 1) * Smoothstep(0.4, 0.6, Fbm(u * C / 120.0, v * C / 120.0, 2, 5032));   // broken ridges where plates slide past
			double hot = 0;
			for (int k = 0; k < hs; k++)
			{
				double dx = (u - hx[k]) * C, dz = (v - hz[k]) * C;
				hot += 50 * A * Bump(Math.Sqrt(dx * dx + dz * dz), 45 * Wide);
			}
			// Draft 2i: the hot spots' trails, older and lower further back: on land worn volcanic hills, out at sea volcanoes
			// that sink as the floor under them cools (islands only near the young end, then seamounts)
			foreach (var tr in trailPts)
			{
				double dx = (u - tr[0]) * C, dz = (v - tr[1]) * C, ag = tr[2];
				double amp = Crust[i] ? 30 * A * (1 - 0.18 * ag) : 50 * A * (1 - 0.12 * ag) - 8 * A * ag;
				if (amp > 0) hot += amp * Bump(Math.Sqrt(dx * dx + dz * dz), 45 * Wide * (1 - 0.06 * ag));
			}
			e += hot;
			// coasts wander: bays, headlands and small islands near the sea's level, so coasts don't trace the plates' edges
			e += (Fbm(u * C / 110.0, v * C / 110.0, 3, 5090) - 0.47) * 44 * Bump(e, 22);
			// a little extra roughness over the plates (before the water is worked out, so rivers still run downhill): broad
			// uplands and basins, rolling hills, and steep-sided flat-topped plateaus, on land only (fading out at the coasts)
			double land = Smoothstep(-10, 10, e);
			if (land > 0)
			{
				double upl = (Fbm(u / (N * 0.12) + 7.1, v / (N * 0.12) + 3.3, 3, 5080) - 0.47) * 2; if (upl < 0) upl *= 0.4;   // (basins shallower than uplands are high)
				double hill = Fbm(u * C / 160.0, v * C / 160.0, 3, 5081) - 0.47;
				double pl = Smoothstep(0.58, 0.64, Fbm(u * C / 300.0, v * C / 300.0, 2, 5082));
				e += ExtraNoise * A * land * (22 * upl + 16 * hill + 24 * pl);
			}
			// Draft 2h, the same layout at every size: the land and the sea keep to the crust. More than 1.5 cells (KeepCells) from
			// the crust's edge the sea stays at least 2 under (but for a hot spot's island) and the land at least 2 over (but for a rift
			// where plates pull apart hard); and where two continents joined, the join stays land. So a narrow neck of a continent
			// stays land in a small world too (block-sized slopes would drown it), and no strait closes up. The coast still wanders
			// inside that band, and the islands come after. (Soft limits, so nothing is flattened: (a + b +- sqrt((a - b)^2 + 4)) / 2.)
			if (Crust[i] ? oceanDist[i] > keep || joinDist[i] <= 1.5 : false)
			{
				double f = 2 - Cut[i] * Smoothstep(0.7, 0.9, rift[i]);
				e = (e + f + Math.Sqrt((e - f) * (e - f) + 4)) * 0.5;
			}
			else if (!Crust[i] && (contDist[i] > keep || YoungSea[i]))   // (Draft 2l: and the young sea, however narrow, stays open)
			{
				double f = -2 + hot * Smoothstep(keep, keep + 3, contDist[i]);   // (a hot spot's island keeps 3 cells of sea from the coast's band)
				e = (e + f - Math.Sqrt((e - f) * (e - f) + 4)) * 0.5;
			}
			// the world's edges are open sea (a wobbly way in from the edge)
			double eb = Math.Min(Math.Min(u, v), Math.Min(N - u, N - v)) * C + (Fbm(u / (N * 0.08), v / (N * 0.08), 3, 5070) - 0.44) * margin * 1.2;
			if (eb < margin) e = -30 * A + (e + 30 * A) * Smoothstep(0, margin, eb);
			Elev[i] = Math.Max(-deep, Math.Min(top, e));
		}
		// island chains (the user: wherever two ocean plates push together): separate volcanic islands in a curved line along the
		// edge, on the plate that stays on top (the trench is on the sinking plate's side). One try per square about 60 blocks
		// across (x Wide), 4 in 5 taken by hash: the cell in the square nearest the line 20 x w blocks in from the edge where an
		// island chain works (any push: strength 0.04 or more; a little jitter by hash). Each island a dome 20-48 blocks across (x Wide),
		// 1 in 6 nearly twice that, its top 8-36 x Amp blocks over the sea (more where the plates push harder), raised from the
		// sea floor over the ridge; each one a volcano spot (island chain). Away from the world's edges.
		int ig = Math.Max(3, (int)Math.Floor(60 * Wide / C + 0.5));
		for (int gz = 0; gz < N; gz += ig) for (int gx = 0; gx < N; gx += ig)
		{
			int best = -1; double bdd = 1e18;
			for (int z = gz; z < Math.Min(N, gz + ig); z++) for (int x = gx; x < Math.Min(N, gx + ig); x++)
			{
				int i = z * N + x, o = Other[i];
				if (Crust[i] || o < 0 || OtherCrust[i] || arc[i] < 0.04) continue;
				double dd = Math.Abs(Bd[i] * C - 20 * sz[i] * Belt) + 6 * Hash(x, z, 5111);
				if (dd < bdd) { bdd = dd; best = i; }
			}
			if (best < 0) continue;
			if (bdd > 14 * Wide + 6) { IslSkip[0]++; continue; }
			if (Hash(gx, gz, 5110) >= 0.85) { IslSkip[1]++; continue; }
			double cx = best % N + 0.5, cz = best / N + 0.5;
			double edgeIn = Math.Min(Math.Min(cx, cz), Math.Min(N - cx, N - cz)) * C;
			if (edgeIn < margin * 0.6) { IslSkip[2]++; continue; }
			bool big = Hash(gx, gz, 5113) < 1.0 / 6;
			double r = (10 + 14 * Hash(gx, gz, 5112)) * Wide * (big ? 1.8 : 1);
			double peak = (8 + 28 * Hash(gx, gz, 5704)) * A * (0.55 + 0.45 * Math.Min(1, arc[best])) * (big ? 1.25 : 1);
			int R = (int)Math.Ceiling(1.25 * r / C) + 1;
			// (Draft 2h: an island stays an island: none whose widest shore would come within 1.5 cells of a coast's band, so in a
			// small world, where an island is big for the map, it can't fill a strait between continents)
			bool nearCoast = false;
			for (int dz = -R; dz <= R && !nearCoast; dz++) for (int dx = -R; dx <= R; dx++)
			{
				int x = (int)Math.Floor(cx) + dx, z = (int)Math.Floor(cz) + dz;
				if (x < 0 || z < 0 || x >= N || z >= N) continue;
				double ex = (x + 0.5 - cx) * C, ez = (z + 0.5 - cz) * C;
				if (Math.Sqrt(ex * ex + ez * ez) < 1.25 * r && contDist[z * N + x] <= keep + 1.5) { nearCoast = true; break; }
			}
			if (nearCoast) { IslSkip[3]++; continue; }
			for (int dz = -R; dz <= R; dz++) for (int dx = -R; dx <= R; dx++)
			{
				int x = (int)Math.Floor(cx) + dx, z = (int)Math.Floor(cz) + dz;
				if (x < 0 || z < 0 || x >= N || z >= N) continue;
				double ex = (x + 0.5 - cx) * C, ez = (z + 0.5 - cz) * C, dist = Math.Sqrt(ex * ex + ez * ez);
				double rr2 = r * (0.75 + 0.5 * Fbm((x + 0.5) * C / 28.0, (z + 0.5) * C / 28.0, 2, 5705));   // (a ragged shore)
				if (dist >= rr2) continue;
				int j = z * N + x;
				Elev[j] = Math.Max(Elev[j], Math.Min(top, -28 * A + (peak + 28 * A) * Bump(dist, rr2)));
			}
			Islands.Add(new[] { cx, cz, r, peak, Plate[best], Other[best] });
			Volcanoes.Add(new[] { best % N, best / N, 2 });
		}
		// volcano spots: along coasts over a sinking ocean and rifts (one per stretch, picked by hash; island chains have theirs)
		int vc = Math.Max(4, N / 8);
		for (int gz = 0; gz < N; gz += vc) for (int gx = 0; gx < N; gx += vc)
		{
			int bi = -1; double bh = 0;
			for (int z = gz; z < Math.Min(N, gz + vc); z++) for (int x = gx; x < Math.Min(N, gx + vc); x++)
			{
				int i = z * N + x;
				int kind = Plate[i] >= 0 && Other[i] >= 0 ? EdgeKind(i) : 0;
				if (kind == 0 || kind == 2) continue;
				double h = Elev[i] + Hash(x, z, 5060) * 20;
				if (bi < 0 || h > bh) { bi = i; bh = h; }
			}
			if (bi >= 0 && Hash(gx, gz, 5061) < 0.6) Volcanoes.Add(new[] { bi % N, bi / N, EdgeKind(bi) });
		}
	}
	// (Draft 2j, the user: young ranges on the leading edges. A drifting group's coast that faces the way it drifts pushes over the
	// ocean plate ahead however that plate moves (as the Andes over the Pacific floor): the push is at least the group's own pace
	// toward the edge, and the edge is at least LeadAct active)
	public static double LeadAct = 0.6;
	// (Draft 2l: at a marked leading coast the push is at least the group's pace its own way, times how squarely the coast faces it)
	bool Leading(int a, int b, bool ca, bool cb, double nx, double nz, double len, ref double conv, double here)
	{
		int ga = PlateGroup[a], gb = PlateGroup[b];
		if (ga >= 0 && gb < 0 && ca && !cb)
		{
			double v = (VX[a] * nx + VZ[a] * nz) / len; if (here > 0) v = Math.Max(v, here * (VX[a] * GDirX[ga] + VZ[a] * GDirZ[ga]));
			if (v > 0.1) { conv = Math.Max(conv, v); return true; }
		}
		if (gb >= 0 && ga < 0 && cb && !ca)
		{
			double v = -(VX[b] * nx + VZ[b] * nz) / len; if (here > 0) v = Math.Max(v, here * (VX[b] * GDirX[gb] + VZ[b] * GDirZ[gb]));
			if (v > 0.1) { conv = Math.Max(conv, v); return true; }
		}
		return false;
	}
	// which kind of volcano could stand here (0: none): a coast over a sinking ocean, an island chain, a rift
	int EdgeKind(int i)
	{
		int a = Plate[i], b = Other[i];
		double d = Bd[i] * Cell, nx = PX[b] - PX[a], nz = PZ[b] - PZ[a], len = Math.Sqrt(nx * nx + nz * nz);
		if (EdgeNX[i] != 0 || EdgeNZ[i] != 0) { nx = EdgeNX[i]; nz = EdgeNZ[i]; len = 1; }   // (Draft 2m)
		double conv = ((VX[a] - VX[b]) * nx + (VZ[a] - VZ[b]) * nz) / len;
		bool ca = EdgeCrust[i], cb = OtherCrust[i];
		Leading(a, b, ca, cb, nx, nz, len, ref conv, EdgeLead[i]);
		if (conv > 0.1 && ca && !cb && d > 20 * Belt && d < 50 * Belt && Elev[i] > 20) return 1;
		if (conv > 0.1 && !ca && !cb && a < b && d > 10 * Belt && d < 30 * Belt && Elev[i] > 2) return 2;
		if (conv < -0.1 && ca && cb && d < 12 * Belt) return 3;
		return 0;
	}

	// ---- climate (Draft 2g: with a wind field and ocean currents, for the terrain only; nothing pushes the player): warm at the
	// equator (the middle, north to south), colder towards the edges and with height. The winds: the big belts (from the east
	// near the equator and the poles, from the west between, and towards or away from the equator), turned aside by high ground
	// and smoothed so they bend round mountain ranges; on big continents a monsoon blows in from the sea in summer and out in
	// winter. The oceans: each basin's currents circle, warm ones running poleward along an ocean's western side (the continents'
	// east coasts) in the subtropics, cold ones equatorward along its eastern side (the west coasts), the other way round in the
	// subpolar seas; they warm or cool the sea and the coasts beside it. The rain: sea air carried by the wind (summer's and
	// winter's), wrung out going uphill, more off warm seas and less off cold ones ----
	public double[] WX, WZ;   // the year's wind at each cell (about 1 long; x east, z south), after the land has turned it
	public double[] MX, MZ;   // the monsoon: summer's wind is the year's plus this, winter's the year's minus it
	public double[] Cur;      // the currents' warmth (degrees): at sea, how much warmer (+) or colder (-) the water is; on land, the coast's
	public double[] CX, CZ;   // the currents' way at sea (for the pictures and the weather later)
	public double[] Lat;      // each cell's distance from the equator, 0 to 1, its edges wandering a little (the climate bands)
	public static int WindSweeps = 2;   // (rounds of the four sweeps that carry the air)
	void MakeClimate()
	{
		int NN = N * N; Temp = new double[NN]; Rain = new double[NN];
		Swing = new double[NN]; Season = new double[NN]; Humid = new double[NN]; Inland = new double[NN];
		WX = new double[NN]; WZ = new double[NN]; MX = new double[NN]; MZ = new double[NN];
		Cur = new double[NN]; CX = new double[NN]; CZ = new double[NN]; Lat = new double[NN];
		// (Draft 2i: the air's reach is a share of the map, as the layout is, so a big world's continents are as wet inside as a
		// small one's: every rate is per cell at 2048's scale, cs = Cell / Belt (4 at every size from 2048 up). Heights, and so
		// the rain wrung out going uphill, stay in blocks)
		double cs = Cell / Belt;
		double perCell = 0.0025 * cs;   // (moisture rained out per cell crossed over flat land)
		// the bands' edges wander a little north and south, so they don't run dead straight
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			double lat = Math.Abs(z + 0.5 - N / 2.0) / (N / 2.0), u = x + 0.5, v = z + 0.5;
			Lat[z * N + x] = Math.Max(0, Math.Min(1, lat + (Fbm(u / (N * 0.12), v / (N * 0.12), 2, 5700) - 0.47) * 0.12));
		}
		// 1. the winds. The belts at each cell; then high ground turns them: the part blowing up the slope of the land's broad
		// shape (heights over 40 blocks, smoothed over about 60 blocks, as a share of the high ground) is taken away, the more the
		// higher it is, so the wind runs along ranges; then smoothed over about 50 blocks twice, so it bends round them
		var hb = new double[NN];
		for (int i = 0; i < NN; i++) hb[i] = Math.Max(0, Math.Min(2, (Elev[i] - 40) / High));
		int rb = Math.Max(1, (int)Math.Floor(60.0 * Belt / Cell + 0.5)); BoxBlur(hb, rb); BoxBlur(hb, rb);
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			int i = z * N + x; var (bx, bz) = BeltWind(Lat[i], z + 0.5);
			double gx = (hb[z * N + Math.Min(N - 1, x + 1)] - hb[z * N + Math.Max(0, x - 1)]) * 0.5, gz = (hb[Math.Min(N - 1, z + 1) * N + x] - hb[Math.Max(0, z - 1) * N + x]) * 0.5;
			double gg = gx * gx + gz * gz, up = bx * gx + bz * gz, block = Smoothstep(0.05, 0.6, hb[i]);
			if (gg > 1e-12 && up > 0) { bx -= block * up / gg * gx; bz -= block * up / gg * gz; }
			double slow = 1 - 0.3 * block; WX[i] = bx * slow; WZ[i] = bz * slow;
		}
		int rw = Math.Max(1, (int)Math.Floor(50.0 * Belt / Cell + 0.5)); BoxBlur(WX, rw); BoxBlur(WZ, rw); BoxBlur(WX, rw); BoxBlur(WZ, rw);
		// the monsoon: the share of land around (over about an eighth of the world across, twice), and its slope: in summer the
		// wind is drawn in towards the middle of big continents, in winter out of them; strongest in the tropics and subtropics,
		// none on small land
		var lf = new double[NN]; for (int i = 0; i < NN; i++) lf[i] = Elev[i] >= 0 ? 1 : 0;
		int rm = Math.Max(2, (int)Math.Floor(N / 8.0 + 0.5)); BoxBlur(lf, rm); BoxBlur(lf, rm);
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			int i = z * N + x;
			double gx = (lf[z * N + Math.Min(N - 1, x + 1)] - lf[z * N + Math.Max(0, x - 1)]) * 0.5, gz = (lf[Math.Min(N - 1, z + 1) * N + x] - lf[Math.Max(0, z - 1) * N + x]) * 0.5;
			double k = 2.5 * rm * Hump(0.06, 0.28, 0.55, Lat[i]) * Smoothstep(0.25, 0.55, lf[i]);
			MX[i] = Math.Max(-1, Math.Min(1, gx * k)); MZ[i] = Math.Max(-1, Math.Min(1, gz * k));
		}
		// 2. the ocean currents, row by row over the sea: how far the nearest land is to the west and to the east (blocks); near
		// land to the west (an ocean's western side) the water runs warm in the subtropics and cold in the subpolar seas, near
		// land to the east the other way round; only in basins wide enough for the currents to circle (6-18% of the world
		// across, fully). Then smoothed over the sea (about 1.5% of the world across)
		double Lw = 0.03 * Size, Le = 0.06 * Size;
		var dW = new double[N]; var dE = new double[N];
		for (int z = 0; z < N; z++)
		{
			double last = -1e9; for (int x = 0; x < N; x++) { if (Elev[z * N + x] >= 0) last = x; dW[x] = (x - last) * Cell; }
			last = 1e9; for (int x = N - 1; x >= 0; x--) { if (Elev[z * N + x] >= 0) last = x; dE[x] = (last - x) * Cell; }
			for (int x = 0; x < N; x++)
			{
				int i = z * N + x; if (Elev[i] >= 0) continue;
				double a = dW[x] / Lw, b = dE[x] / Le, fW = 1 / (1 + a * a), fE = 1 / (1 + b * b);
				double bw = Smoothstep(0.06 * Size, 0.18 * Size, Math.Min(2.0 * Size, dW[x] + dE[x]));
				double g1 = Hump(0.08, 0.3, 0.5, Lat[i]), g2 = Hump(0.48, 0.65, 0.82, Lat[i]);
				Cur[i] = 6 * bw * (g1 * (fW - 0.8 * fE) + g2 * (0.9 * fE - 0.7 * fW));
			}
		}
		int rc = Math.Max(1, (int)Math.Floor(0.015 * N + 0.5));
		var seaM = new double[NN]; for (int i = 0; i < NN; i++) seaM[i] = Elev[i] < 0 ? 1 : 0;
		var curS = (double[])Cur.Clone(); var seaS = (double[])seaM.Clone(); BoxBlur(curS, rc); BoxBlur(seaS, rc);
		for (int i = 0; i < NN; i++) if (Elev[i] < 0) Cur[i] = curS[i] / Math.Max(1e-9, seaS[i]);
		// (the coast's: the sea's warmth averaged over about 3% of the world across, for the land beside it)
		var curL = new double[NN]; for (int i = 0; i < NN; i++) curL[i] = Elev[i] < 0 ? Cur[i] : 0;
		var seaL = (double[])seaM.Clone(); int rl = Math.Max(1, (int)Math.Floor(0.03 * N + 0.5)); BoxBlur(curL, rl); BoxBlur(seaL, rl);
		for (int i = 0; i < NN; i++) if (Elev[i] >= 0) Cur[i] = seaL[i] > 1e-9 ? curL[i] / seaL[i] * Math.Min(1, seaL[i] * 2.5) : 0;
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			int i = z * N + x; if (Elev[i] >= 0) continue;
			double s = Math.Max(-1, Math.Min(1, Cur[i] / 4)), pole = z + 0.5 < N / 2.0 ? -1 : 1;
			CX[i] = WX[i] * 0.6 * (1 - Math.Abs(s)); CZ[i] = s * pole + WZ[i] * 0.3 * (1 - Math.Abs(s));
		}
		// 3. the air: carried by summer's wind and by winter's, four sweeps a round (each way across and down), every cell taking
		// its air from the cells upwind of it (across and down, by how much the wind blows each way; off the map's edge is sea
		// air). Over the sea it soaks up water (more off warm water, less off cold); over land it rains out, more going uphill,
		// and loses its sea air
		var rawS = new double[NN]; var rawW = new double[NN]; var hum = new double[NN]; var seaAir = new double[NN];
		var m = new double[NN]; var ma = new double[NN]; var raw = new double[NN];
		for (int season = 0; season < 2; season++)
		{
			double sg = season == 0 ? 1 : -1;
			for (int i = 0; i < NN; i++) { m[i] = 1; ma[i] = 1; raw[i] = 0; }
			for (int round = 0; round < WindSweeps; round++)
				for (int o = 0; o < 4; o++)
				{
					bool xr = (o & 1) == 1, zr = (o & 2) == 2;
					for (int zs = 0; zs < N; zs++)
					{
						int z = zr ? N - 1 - zs : zs;
						for (int xs = 0; xs < N; xs++)
						{
							int x = xr ? N - 1 - xs : xs, i = z * N + x;
							double wx = WX[i] + sg * MX[i], wz = WZ[i] + sg * MZ[i], awx = Math.Abs(wx), awz = Math.Abs(wz), sum = awx + awz;
							double ax = sum > 1e-9 ? awx / sum : 0.5, az = 1 - ax;
							int ux = x - (wx >= 0 ? 1 : -1), uz = z - (wz >= 0 ? 1 : -1);
							double mIn = 0, maIn = 0, eIn = 0;
							if (ux < 0 || ux >= N) { mIn += ax; maIn += ax; eIn -= ax; } else { int j = z * N + ux; mIn += ax * m[j]; maIn += ax * ma[j]; eIn += ax * Elev[j]; }
							if (uz < 0 || uz >= N) { mIn += az; maIn += az; eIn -= az; } else { int j = uz * N + x; mIn += az * m[j]; maIn += az * ma[j]; eIn += az * Elev[j]; }
							double e = Elev[i], r;
							if (e < 0)
							{
								double mSea = 1.2 * Math.Max(0.6, Math.Min(1.4, 1 + 0.06 * Cur[i]));
								m[i] = Math.Min(mSea, mIn + 0.015 * cs); r = 0.25 * perCell * m[i]; ma[i] = 1;
							}
							else
							{
								double up = Math.Max(0, e - eIn) / Amp;
								// (cold coasts: steady air, little rain; warm coasts: more)
								r = Math.Min(mIn * 0.5, mIn * (perCell + 0.012 * up)) * Math.Max(0.4, Math.Min(1.4, 1 + 0.07 * Cur[i]));
								m[i] = mIn - r * 0.6 + 0.00045 * cs;
								ma[i] = Math.Max(0, maIn - cs / 600.0 - up * 0.006);
							}
							raw[i] = r;
						}
					}
				}
			var dst = season == 0 ? rawS : rawW;
			for (int i = 0; i < NN; i++) { dst[i] = raw[i]; hum[i] += m[i] * 0.5; seaAir[i] += ma[i] * 0.5; }
		}
		var near = new double[NN];
		for (int i = 0; i < NN; i++) { raw[i] = (rawS[i] + rawW[i]) * 0.5; near[i] = Elev[i] < 0 ? 1 : 0; }
		// spread it out a little (two box blurs, about 24 blocks), then the bands: wet at the equator, dry about a third of the
		// way out (the deserts), wetter again in the middle latitudes, dry near the poles
		int rad = Math.Max(1, (int)Math.Floor(24.0 * Belt / Cell + 0.5)), rs = Math.Max(1, (int)Math.Floor(40.0 * Belt / Cell + 0.5));
		Blur(raw, rad); Blur(raw, rad); Blur(rawS, rad); Blur(rawW, rad);
		Blur(seaAir, rad); Blur(seaAir, rad); Blur(hum, rad); Blur(hum, rad);
		Blur(near, rs); Blur(near, rs);   // (how much sea there is close by, any way round)
		for (int z = 0; z < N; z++)
		{
			for (int x = 0; x < N; x++)
			{
				int i = z * N + x;
				double u = x + 0.5, v = z + 0.5, wl = Lat[i], lat = Math.Abs(z + 0.5 - N / 2.0) / (N / 2.0);
				double t0 = 27 - 52 * lat * lat, sw = 1 + 24 * lat;
				double band = Knots(wl, BandAt, BandVal);
				Rain[i] = Math.Max(0, Math.Min(1, raw[i] / (perCell * 0.8) * band));
				double mar = Math.Max(seaAir[i], Math.Min(1, near[i] * 1.6)), inl = 1 - mar;
				// (the currents: the sea itself by its warmth; the land beside it by the coast's, as much as the sea air reaches it)
				Temp[i] = t0 + (Fbm(u / (N * 0.1), v / (N * 0.1), 3, 5040) - 0.47) * 6 - Lapse / Amp * Math.Max(0, Elev[i]) + Cur[i] * (Elev[i] < 0 ? 1 : 0.8 * mar);
				Inland[i] = inl;
				Humid[i] = Math.Min(1, hum[i] / 1.2);
				Swing[i] = sw * (0.4 + 0.6 * inl);
				// when the rain comes: summer rains in the tropics either side of the always-wet equator (the savanna's dry
				// winters), wet winters and dry summers on warm coasts facing the westerlies, summer rain deep inland in the middle
				// latitudes and winter storms on their windward coasts, a little more in summer near the poles; and the monsoon's
				// wet summers and dry winters, from summer's rain against winter's
				double trop = Hump(0.05, 0.2, 0.36, wl), warmWest = Hump(0.28, 0.4, 0.52, wl), midLat = Hump(0.42, 0.6, 0.85, wl), polar = Smoothstep(0.75, 0.95, wl);
				double mq = (rawS[i] - rawW[i]) / Math.Max(1e-9, rawS[i] + rawW[i]);
				double q = 0.85 * trop - 0.8 * warmWest * mar + 0.35 * midLat * inl - 0.25 * midLat * mar + 0.2 * polar + 0.6 * mq + (Fbm(u / (N * 0.06), v / (N * 0.06), 2, 5701) - 0.47) * 0.4;
				Season[i] = Math.Max(-1, Math.Min(1, q));
			}
		}
	}
	public static bool OldLake = System.Environment.GetEnvironmentVariable("OLDLAKE") != null;   // (testing: Draft 2f's lake rule)
	public double LakeBig => LakeMinArea * Belt * Belt;   // (Draft 2j: a share of the map, (Size / 2048) squared)
	public static double LakeMinArea = 4000;   // (a lake smaller than this many square blocks is drained unless it's in a rift, a basin or old-ice country)
	public const double LakeDeep = 5;   // (the deepest a lake fills, in a 192-tall world; times Amp)
	public static double BasinDeep = 2;   // (the deepest a lake fills outside rifts and the old ice's country; times Amp)
	public static double PanDeep = 3;   // (outside them, a hollow shallower than this at its deepest is filled flat, not a lake; times Amp)
	public const double Lapse = 0.32;   // (degrees colder per block up, in a 192-tall world)
	static double Hump(double a, double b, double c, double v) => Smoothstep(a, b, v) * (1 - Smoothstep(b, c, v));
	// the seasons run opposite either side of the equator (summer in the north is winter in the south, as on Earth): +1 in the north
	// half, -1 in the south. The weather multiplies a place's Swing and Season by this and by where the year is (+1 midsummer in the north)
	public double Hemisphere(double zc) => zc < N / 2.0 ? 1 : -1;
	// the wind at a block (x, z), blended between the cell middles: the year's (season 0), or with the monsoon, summer's (+1) or
	// winter's (-1); for the weather later. x east, z south, about 1 long
	public (double x, double z) WindAt(double bx, double bz, double season = 0)
	{
		double fx = Math.Max(0, Math.Min(N - 1.0, bx / Cell - 0.5)), fz = Math.Max(0, Math.Min(N - 1.0, bz / Cell - 0.5));
		int x0 = (int)Math.Floor(fx), z0 = (int)Math.Floor(fz), x1 = Math.Min(N - 1, x0 + 1), z1 = Math.Min(N - 1, z0 + 1);
		double tx = fx - x0, tz = fz - z0;
		double L(double[] a) { double top = a[z0 * N + x0] + (a[z0 * N + x1] - a[z0 * N + x0]) * tx, bot = a[z1 * N + x0] + (a[z1 * N + x1] - a[z1 * N + x0]) * tx; return top + (bot - top) * tz; }
		return (L(WX) + season * L(MX), L(WZ) + season * L(MZ));
	}
	// the belts' own wind at a distance from the equator (0-1) on row zc (in cells, from the north edge), about 1 long: x from the
	// east (-1) near the equator and the poles, from the west (+1) between; z towards the equator (the trade and polar winds) or
	// away from it (the westerlies). Smooth across the bands
	(double x, double z) BeltWind(double lat, double zc)
	{
		double toEq = zc < N / 2.0 ? 1 : -1;
		double wx = Knots(lat, WxAt, WxVal);
		double wz = Knots(lat, WzAt, WzVal) * toEq;
		return (wx, wz);
	}
	// the climate at a block (x, z), blended between the cell middles: the year's average temperature, half its summer-to-winter
	// swing, the year's rain, when it falls, and the air's moisture (for the weather; the plan's own numbers, nothing more)
	public (double temp, double swing, double rain, double season, double humid) ClimateAt(double bx, double bz)
	{
		double fx = Math.Max(0, Math.Min(N - 1.0, bx / Cell - 0.5)), fz = Math.Max(0, Math.Min(N - 1.0, bz / Cell - 0.5));
		int x0 = (int)Math.Floor(fx), z0 = (int)Math.Floor(fz), x1 = Math.Min(N - 1, x0 + 1), z1 = Math.Min(N - 1, z0 + 1);
		double tx = fx - x0, tz = fz - z0;
		double L(double[] a) { double top = a[z0 * N + x0] + (a[z0 * N + x1] - a[z0 * N + x0]) * tx, bot = a[z1 * N + x0] + (a[z1 * N + x1] - a[z1 * N + x0]) * tx; return top + (bot - top) * tz; }
		return (L(Temp), L(Swing), L(Rain), L(Season), L(Humid));
	}
	// a box blur that only mixes cells of the same plate (two passes, across then down)
	void PlateBlur(double[] a, int r)
	{
		var tmp = new double[a.Length];
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			int i = z * N + x, p = Plate[i]; double s = 0; int n = 0;
			for (int k = -r; k <= r; k++) { int xx = x + k; if (xx < 0 || xx >= N || Plate[i + k] != p) continue; s += a[i + k]; n++; }
			tmp[i] = s / n;
		}
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			int i = z * N + x, p = Plate[i]; double s = 0; int n = 0;
			for (int k = -r; k <= r; k++) { int zz = z + k; if (zz < 0 || zz >= N || Plate[i + k * N] != p) continue; s += tmp[i + k * N]; n++; }
			a[i] = s / n;
		}
	}
	static readonly double[] BandAt = { 0, 0.12, 0.2, 0.3, 0.38, 0.5, 0.62, 0.8, 1 }, BandVal = { 1.0, 0.8, 0.35, 0.12, 0.2, 0.55, 0.75, 0.4, 0.12 };
	static readonly double[] WxAt = { 0, 0.28, 0.38, 0.62, 0.72, 1 }, WxVal = { -1.0, -1, 1, 1, -1, -1 };
	static readonly double[] WzAt = { 0, 0.12, 0.28, 0.38, 0.62, 0.72, 0.9, 1 }, WzVal = { 0, 0.4, 0.4, -0.3, -0.3, 0.3, 0.3, 0.1 };
	static double Knots(double v, double[] at, double[] val)
	{
		if (v <= at[0]) return val[0];
		for (int k = 1; k < at.Length; k++) if (v <= at[k]) return val[k - 1] + (val[k] - val[k - 1]) * Smoothstep(at[k - 1], at[k], v);
		return val[^1];
	}
	void Blur(double[] a, int r)
	{
		var tmp = new double[a.Length];
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			double s = 0; int n = 0;
			for (int k = -r; k <= r; k++) { int xx = x + k; if (xx < 0 || xx >= N) continue; s += a[z * N + xx]; n++; }
			tmp[z * N + x] = s / n;
		}
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			double s = 0; int n = 0;
			for (int k = -r; k <= r; k++) { int zz = z + k; if (zz < 0 || zz >= N) continue; s += tmp[zz * N + x]; n++; }
			a[z * N + x] = s / n;
		}
	}

	// ---- water: flooding in from the world's edge, lowest first, finds where every cell drains (always downhill, or across a
	// lake to its lowest edge); hollows deep enough become lakes, shallow ones are filled in; then the rain is added up downhill ----
	static readonly int[] DX = { 1, -1, 0, 0, 1, -1, 1, -1 }, DZ = { 0, 0, 1, -1, 1, 1, -1, -1 };
	// (Draft 2k, the user: "many small lakes where the old ice was (like Canada and Finland)", all lakes about 1-2% of the land.
	// The old ice scoured hollows into the gentle country it covered. Of the old-ice country (IceWas 0.3 up) that is low (2 x Tall
	// to 30 x Amp up) and not a fjord, the gentlest IceGentle (by the height range over the 5 x 5 cells round it (IceWin 2 each
	// way), over 4 cells' width, in a 2048 world's terms; ties to the lower cell); of those, the cells where a blob noise is
	// highest (Fbm, 2 octaves, about 48 blocks across in a 2048 world, hash 7140; ties to the lower cell), IceLakeShare of all the
	// land at most, become bowls, each holding water to its lowest rim (below), so the flood fills them as lakes. The lake rules
	// keep any lake in a bowl from a 24th of LakeBig)
	public static double IceLakeShare = 0.016, IceGentle = 0.25; public static int IceWin = 2;
	public static double LakeCap = 0.02;   // (Draft 2m: lakes at most this share of the land; the ice-bowl lakes give way)
	public int LakeGaveWay;   // (testing: how many ice-bowl lakes gave way)
	public bool[] IceBowl;
	void DigIceLakes()
	{
		int NN = N * N; IceBowl = new bool[NN]; if (IceWas == null) return;
		double cs = Cell / Belt; int land = 0; var cand = new List<int>(); var bv = new double[NN]; var sv = new double[NN];
		for (int i = 0; i < NN; i++)
		{
			if (Elev[i] <= 0) continue;
			land++;
			if (IceWas[i] < 0.3 || Elev[i] < 2 * Tall || Elev[i] > 30 * Amp || Fjord != null && Fjord[i]) continue;
			int x = i % N, z = i / N; double hi = Elev[i], lo = Elev[i];
			for (int dz = -IceWin; dz <= IceWin; dz++) for (int dx = -IceWin; dx <= IceWin; dx++)
			{
				int xx = x + dx, zz = z + dz; if (xx < 0 || zz < 0 || xx >= N || zz >= N) continue;
				double e = Elev[zz * N + xx]; if (e > hi) hi = e; if (e < lo) lo = e;
			}
			sv[i] = (hi - lo) / (2 * IceWin * Cell) * Belt / Tall; cand.Add(i);
		}
		cand.Sort((a, b) => sv[a] != sv[b] ? sv[a].CompareTo(sv[b]) : a.CompareTo(b));
		int keep = (int)Math.Ceiling(IceGentle * cand.Count); if (keep < cand.Count) cand.RemoveRange(keep, cand.Count - keep);
		foreach (int i in cand) bv[i] = Fbm((i % N + 0.5) * cs / 48, (i / N + 0.5) * cs / 48, 2, 7140);
		cand.Sort((a, b) => bv[a] != bv[b] ? bv[b].CompareTo(bv[a]) : a.CompareTo(b));
		int K = Math.Min(cand.Count, (int)Math.Floor(IceLakeShare * land));
		if (K == 0) return;
		double thr = bv[cand[K - 1]], span = Math.Max(1e-9, Math.Min(0.06, bv[cand[0]] - thr));
		for (int k = 0; k < K; k++) IceBowl[cand[k]] = true;
		// each bowl (the chosen cells joined side to side, found in cell order) holds water up to its lowest rim: its cells
		// more than 6 x Amp over that rim stay land (shores and islands); the rest go down to the rim's height or lower, less
		// 0.35 to 1 x 4 x Amp (deepest where the noise is highest)
		var seen = new bool[NN]; var comp = new List<int>(); var stk = new Stack<int>();
		for (int i0 = 0; i0 < NN; i0++)
		{
			if (!IceBowl[i0] || seen[i0]) continue;
			comp.Clear(); seen[i0] = true; stk.Push(i0);
			while (stk.Count > 0)
			{
				int c = stk.Pop(); comp.Add(c); int cx = c % N, cz = c / N;
				for (int k = 0; k < 4; k++) { int xx = cx + DX[k], zz = cz + DZ[k]; if (xx < 0 || zz < 0 || xx >= N || zz >= N) continue; int j = zz * N + xx; if (IceBowl[j] && !seen[j]) { seen[j] = true; stk.Push(j); } }
			}
			double rim = 1e18;
			foreach (int c in comp) { int cx = c % N, cz = c / N; for (int k = 0; k < 4; k++) { int xx = cx + DX[k], zz = cz + DZ[k]; if (xx < 0 || zz < 0 || xx >= N || zz >= N) continue; int j = zz * N + xx; if (!IceBowl[j]) rim = Math.Min(rim, Elev[j]); } }
			if (rim > 1e17) continue;
			foreach (int c in comp)
			{
				if (Elev[c] > rim + 6 * Amp) { IceBowl[c] = false; continue; }
				Elev[c] = Math.Min(Elev[c], rim) - 4 * Amp * (0.35 + 0.65 * Smoothstep(thr, thr + span, bv[c]));
			}
		}
	}
	void MakeWater()
	{
		int NN = N * N;
		Level = new double[NN]; Down = new int[NN]; Order = new int[NN]; Sea = new bool[NN]; Lake = new bool[NN]; Flow = new double[NN];
		void Flood()
		{
			var seen = new bool[NN]; var heap = new Heap(NN); int n = 0;
			for (int i = 0; i < NN; i++) Down[i] = -1;
			for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
				if (x == 0 || z == 0 || x == N - 1 || z == N - 1) { int i = z * N + x; seen[i] = true; Level[i] = Elev[i]; heap.Push(Level[i], i); }
			while (heap.Count > 0)
			{
				int c = heap.Pop(); Order[n++] = c;
				int cx = c % N, cz = c / N;
				for (int k = 0; k < 8; k++)
				{
					int x = cx + DX[k], z = cz + DZ[k];
					if (x < 0 || z < 0 || x >= N || z >= N) continue;
					int j = z * N + x;
					if (seen[j]) continue;
					// (across flat ground the level rises a hair, 1/10000 of a block, each step out, so the flood crosses a flat
					// nearest-first and rivers take the short way to its outlet instead of winding about)
					seen[j] = true; Level[j] = Math.Max(Elev[j], Level[c] + 0.0001); Down[j] = c;
					heap.Push(Level[j], j);
				}
			}
		}
		DigIceLakes();
		Flood();
		// no lake deeper than LakeDeep: where a hollow would fill deeper, the water's way out is cut down from its lowest point (a
		// gorge through the rim, as rivers do), then the flood is worked out again. Each filled hollow (one water level, found in
		// cell order) is looked at once.
		double lakeDeep = LakeDeep * Amp;
		var mark = new int[NN]; var q = new int[NN]; int stamp = 0; bool cutAny = false; var cuts = new List<(int pit, double cut)>();
		for (int i0 = 0; i0 < NN; i0++)
		{
			if (mark[i0] != 0 || Level[i0] <= 0 || Level[i0] - Elev[i0] <= 0) continue;
			stamp++; int qh = 0, qt = 0; q[qt++] = i0; mark[i0] = stamp; double L = Level[i0]; int pit = i0;
			while (qh < qt)
			{
				int c = q[qh++]; if (Elev[c] < Elev[pit]) pit = c;
				int cx = c % N, cz = c / N;
				for (int k = 0; k < 8; k++)
				{
					int x = cx + DX[k], z = cz + DZ[k];
					if (x < 0 || z < 0 || x >= N || z >= N) continue;
					int j = z * N + x;
					if (mark[j] != 0 || Math.Abs(Level[j] - L) > 0.05 || Level[j] - Elev[j] <= 0) continue;   // (one body of water: its level the same, give or take the hair added each step across it)
					mark[j] = stamp; q[qt++] = j;
				}
			}
			// (Draft 2g, the user: "fewer lakes". Real lakes stay: big ones (LakeMinArea square blocks of lake 3 or more deep in a
			// 2048 world, more in bigger worlds: times Size / 2048), ones a third that big in a basin, a sixth that big in a rift or
			// where the old ice was. A small hollow anywhere else is filled up (below). Lakes in a rift or where the old ice was may
			// be LakeDeep deep (deep, narrow lakes); the others only BasinDeep, their way out cut down further, so a wide shallow
			// basin keeps a lake in its deepest part and the river drains through the rest)
			int deepN = 0; for (int k = 0; k < qt; k++) if (L - Elev[q[k]] >= 3 * Tall) deepN++;   // (the part that would be lake: 3 or more deep)
			double areaL = deepN * (double)Cell * Cell, big = LakeBig;
			bool rift = RiftZ != null && RiftZ[pit] > 0.3, iced = IceWas != null && IceWas[pit] >= 0.3;
			bool real = areaL >= big || rift && areaL * 6 >= big || Basin != null && Basin[pit] > 0.5 && areaL * 3 >= big || iced && areaL * 6 >= big || IceBowl[pit] && areaL * 24 >= big;
			if (real && !rift && !iced && !OldLake && L - Elev[pit] < PanDeep * Amp) real = false;   // (a wide shallow pan: filled flat)
			double allow = !real ? 2 * Tall : rift || iced || OldLake ? lakeDeep : BasinDeep * Amp;
			if (L - Elev[pit] <= allow) continue;
			// (a small hollow is filled up instead, to 2 below its water, as small lakes silt up; then it's filled in flat below. Its
			// way out isn't cut: a cut runs on down the river and could drain a real lake below it)
			if (!real && !OldLake) { for (int k = 0; k < qt; k++) Elev[q[k]] = Math.Max(Elev[q[k]], L - allow); cutAny = true; continue; }
			// from the lowest point, the way the water goes: nothing on it stays higher than the lake should be (stepping down a
			// hair each cell), until the way is lower than that anyway
			double cut = Elev[pit] + allow;
			// (but the way out is never cut below 1 block above the sea, or the sea would come up it: a hollow whose floor is that
			// low, as the old ice could dig, has its floor filled up instead, as lakes silt up)
			if (cut < 1 && !OldLake) { for (int k = 0; k < qt; k++) Elev[q[k]] = Math.Max(Elev[q[k]], 1 - lakeDeep); cut = 1; }
			// (the cuts are made after every hollow is looked at, so a small hollow filled up later can't block a way out already cut)
			if (OldLake) CutOut(pit, cut); else cuts.Add((pit, cut));
			cutAny = true;
		}
		foreach (var (pit, cut) in cuts) CutOut(pit, cut);
		void CutOut(int pit, double cut)
		{
			for (int r = Down[pit]; r >= 0; r = Down[r])
			{
				cut -= 0.001;
				if (Level[r] <= cut) break;
				if (Elev[r] > cut) Elev[r] = cut;
			}
		}
		if (cutAny) Flood();
		for (int i = 0; i < NN; i++)
		{
			Sea[i] = Level[i] <= 0;
			if (Sea[i]) { Level[i] = 0; Down[i] = -1; continue; }
			if (Level[i] - Elev[i] >= 3 * Tall) Lake[i] = true; else Elev[i] = Math.Max(Elev[i], Level[i]);   // (shallow hollows: filled in)
		}
		// (and each lake on its own, as a patch of lake cells touching, side or corner: one too small by the same rule is filled
		// in too, like the deep spots left in a wide hollow, so the land isn't speckled with little lakes)
		{
			var seenL = new bool[NN]; var lst = new List<int>(); var stk = new Stack<int>(); double big = LakeBig;
			for (int i0 = 0; i0 < NN; i0++)
			{
				if (!Lake[i0] || seenL[i0]) continue;
				lst.Clear(); seenL[i0] = true; stk.Push(i0); int pit = i0;
				while (stk.Count > 0)
				{
					int c = stk.Pop(); lst.Add(c); if (Elev[c] < Elev[pit] || Elev[c] == Elev[pit] && c < pit) pit = c;
					int cx = c % N, cz = c / N;
					for (int k = 0; k < 8; k++)
					{
						int x = cx + DX[k], z = cz + DZ[k]; if (x < 0 || z < 0 || x >= N || z >= N) continue;
						int j = z * N + x; if (Lake[j] && !seenL[j]) { seenL[j] = true; stk.Push(j); }
					}
				}
				double areaL = lst.Count * (double)Cell * Cell;
				bool real = areaL >= big || RiftZ != null && RiftZ[pit] > 0.3 && areaL * 6 >= big || Basin != null && Basin[pit] > 0.5 && areaL * 3 >= big
					|| IceWas != null && IceWas[pit] >= 0.3 && areaL * 6 >= big || IceBowl[pit] && areaL * 24 >= big;
				if (!real) foreach (int c in lst) { Lake[c] = false; Elev[c] = Level[c]; }
			}
		}
		// (Draft 2m, the user: lakes about 1-2% of the land, and 2% at most in every world. While the lakes are over LakeCap of the
		// land (every cell not sea), the ice-bowl lakes give way: each lake (lake cells touching, side or corner, found in cell
		// order) more than half of whose cells are bowl cells, in the order of Hash(its first cell, 7161) (lowest first), is filled
		// in flat to its water, as small lakes are, until the lakes are at LakeCap or under. Every other lake stays: a big basin
		// lake is fine as long as the bowls make room for it)
		LakeGaveWay = 0;
		if (LakeCap > 0)
		{
			int landN = 0, lakeN = 0; for (int i = 0; i < NN; i++) if (!Sea[i]) { landN++; if (Lake[i]) lakeN++; }
			double cap = LakeCap * landN;
			if (lakeN > cap)
			{
				var seenB = new bool[NN]; var bowls = new List<(double key, List<int> cells)>(); var stk = new Stack<int>();
				for (int i0 = 0; i0 < NN; i0++)
				{
					if (!Lake[i0] || seenB[i0]) continue;
					var lst = new List<int>(); seenB[i0] = true; stk.Push(i0); int nb = 0;
					while (stk.Count > 0)
					{
						int c = stk.Pop(); lst.Add(c); if (IceBowl[c]) nb++;
						int cx = c % N, cz = c / N;
						for (int k = 0; k < 8; k++)
						{
							int x = cx + DX[k], z = cz + DZ[k]; if (x < 0 || z < 0 || x >= N || z >= N) continue;
							int j = z * N + x; if (Lake[j] && !seenB[j]) { seenB[j] = true; stk.Push(j); }
						}
					}
					if (nb * 2 > lst.Count) bowls.Add((Hash(i0 % N, i0 / N, 7161), lst));
				}
				bowls.Sort((u, w) => u.key != w.key ? u.key.CompareTo(w.key) : u.cells[0].CompareTo(w.cells[0]));
				foreach (var (_, cells) in bowls)
				{
					if (lakeN <= cap) break;
					foreach (int c in cells) { Lake[c] = false; Elev[c] = Level[c]; }
					lakeN -= cells.Count; LakeGaveWay++;
				}
			}
		}
		// (Draft 2k, the user: "Rivers should be less jagged and better flowing". The flood's way down is the neighbour it came
		// from, so on an even slope water runs dead straight at 45 or 90 degrees, then kinks. On dry land (not sea or lake) the way
		// down is picked again: the neighbour (side or corner, in DX order, ties to the first) whose water level is lowest below
		// its own for the distance, a corner's fall counted over 2 - Hash(i, k, 7150) (a random weight, as Rho8 does: so the
		// ways mix and follow the land's true slope on average). Only strictly lower neighbours; the level falls along every way,
		// so the flood's order still runs downstream first)
		if (RiverRho8)
			for (int i = 0; i < NN; i++)
			{
				if (Sea[i] || Lake[i] || Down[i] < 0) continue;
				int x = i % N, z = i / N, best = Down[i]; double bs = -1;
				for (int k = 0; k < 8; k++)
				{
					int xx = x + DX[k], zz = z + DZ[k]; if (xx < 0 || zz < 0 || xx >= N || zz >= N) continue;
					int j = zz * N + xx; double drop = Level[i] - Level[j]; if (drop <= 0) continue;
					double s = DX[k] != 0 && DZ[k] != 0 ? drop / (2 - Hash(i, k, 7150)) : drop;
					if (s > bs) { bs = s; best = j; }
				}
				Down[i] = best;
			}
		for (int i = 0; i < NN; i++) Flow[i] = Sea[i] ? 0 : Rain[i];
		for (int k = NN - 1; k >= 0; k--) { int i = Order[k]; if (!Sea[i] && Down[i] >= 0) Flow[Down[i]] += Flow[i]; }
	}
	public static bool RiverRho8 = true;

	// (Draft 2k, the user: "Rivers should be less jagged and better flowing". Each river cell gets a POINT: its middle, smoothed
	// along its river RiverSmooth times (each time every point goes to a quarter of the next one up, half its own and a quarter
	// of the next one down, all at once; the next up is the river cell running into it with the most flow, ties to the lower
	// cell, a source has none and uses its own; where the next down isn't a river (the sea, a lake) that cell's middle stays
	// put), then kept inside its own cell, a 20th of a cell in from its sides. The river runs as a CURVE through the points:
	// each river cell's stretch is a quadratic curve guided by its point (B(t) = (1-t)^2 S + 2t(1-t) P + t^2 E), from S, halfway
	// between the next up's point and its own (a source: its own point), to E, halfway to the next down's point; a side river
	// ends on the river it joins, at the middle of that cell's curve ((S + 2P + E) / 4); into the sea or a lake it ends at that
	// cell's middle. Its width runs from the mean of its own and the next up's to the mean of its own and the next down's. Worked
	// out downstream first (the flood's order), so a side river's end is known. 2b carves the rivers along these curves)
	public double[] RiverPX, RiverPZ, RiverSX, RiverSZ, RiverEX, RiverEZ; public int[] RiverUp;
	public static int RiverSmooth = 6;
	void MakeRiverLines()
	{
		int NN = N * N; RiverPX = new double[NN]; RiverPZ = new double[NN]; RiverUp = new int[NN];
		RiverSX = new double[NN]; RiverSZ = new double[NN]; RiverEX = new double[NN]; RiverEZ = new double[NN];
		var isR = new bool[NN];
		for (int i = 0; i < NN; i++) { isR[i] = River(i); RiverUp[i] = -1; RiverPX[i] = (i % N + 0.5) * Cell; RiverPZ[i] = (i / N + 0.5) * Cell; }
		for (int i = 0; i < NN; i++)
		{
			if (!isR[i]) continue; int d = Down[i]; if (d < 0 || !isR[d]) continue;
			int u = RiverUp[d]; if (u < 0 || Flow[i] > Flow[u] || Flow[i] == Flow[u] && i < u) RiverUp[d] = i;
		}
		var nx = new double[NN]; var nz = new double[NN];
		for (int pass = 0; pass < RiverSmooth; pass++)
		{
			for (int i = 0; i < NN; i++)
			{
				if (!isR[i]) continue;
				int u = RiverUp[i], d = Down[i];
				double ux = u >= 0 ? RiverPX[u] : RiverPX[i], uz = u >= 0 ? RiverPZ[u] : RiverPZ[i];
				double dx = d >= 0 ? RiverPX[d] : RiverPX[i], dz = d >= 0 ? RiverPZ[d] : RiverPZ[i];
				nx[i] = (ux + 2 * RiverPX[i] + dx) / 4; nz[i] = (uz + 2 * RiverPZ[i] + dz) / 4;
			}
			for (int i = 0; i < NN; i++) if (isR[i]) { RiverPX[i] = nx[i]; RiverPZ[i] = nz[i]; }
		}
		for (int i = 0; i < NN; i++)
		{
			if (!isR[i]) continue;
			double x0 = (i % N) * Cell, z0 = (i / N) * Cell, m = 0.05 * Cell;
			RiverPX[i] = Math.Clamp(RiverPX[i], x0 + m, x0 + Cell - m); RiverPZ[i] = Math.Clamp(RiverPZ[i], z0 + m, z0 + Cell - m);
		}
		for (int k = 0; k < NN; k++)
		{
			int i = Order[k]; if (!isR[i]) continue;
			int u = RiverUp[i], d = Down[i];
			RiverSX[i] = u >= 0 ? (RiverPX[u] + RiverPX[i]) / 2 : RiverPX[i]; RiverSZ[i] = u >= 0 ? (RiverPZ[u] + RiverPZ[i]) / 2 : RiverPZ[i];
			if (d < 0) { RiverEX[i] = RiverPX[i]; RiverEZ[i] = RiverPZ[i]; }
			else if (!isR[d]) { RiverEX[i] = RiverPX[d]; RiverEZ[i] = RiverPZ[d]; }
			else if (RiverUp[d] == i) { RiverEX[i] = (RiverPX[i] + RiverPX[d]) / 2; RiverEZ[i] = (RiverPZ[i] + RiverPZ[d]) / 2; }
			else { RiverEX[i] = (RiverSX[d] + 2 * RiverPX[d] + RiverEX[d]) / 4; RiverEZ[i] = (RiverSZ[d] + 2 * RiverPZ[d] + RiverEZ[d]) / 4; }
		}
	}
	// (pictures) a river cell's curve in pixels: n + 1 points, and the radius at each (blocks to pixels by ppb, at least half a pixel)
	void RiverCurve(int i, double ox, double oz, double ppb, int n, double[] px, double[] pz, double[] pr)
	{
		int u = RiverUp[i], d = Down[i];
		double w = RiverWidth(i), wS = u >= 0 ? (RiverWidth(u) + w) / 2 : w, wE = d >= 0 && River(d) ? (w + RiverWidth(d)) / 2 : w;
		for (int k = 0; k <= n; k++)
		{
			double s = (double)k / n, a = (1 - s) * (1 - s), b = 2 * s * (1 - s), c = s * s;
			px[k] = (a * RiverSX[i] + b * RiverPX[i] + c * RiverEX[i] - ox) * ppb; pz[k] = (a * RiverSZ[i] + b * RiverPZ[i] + c * RiverEZ[i] - oz) * ppb;
			pr[k] = Math.Max(0.5, (wS + (wE - wS) * s) * ppb / 2);
		}
	}
	// how far every cell is from the sea, a river, a lake and a big river (one that's 6 or more blocks wide)
	void MakeDistances()
	{
		SeaDist = Chamfer(i => Sea[i]);
		RiverDist = Chamfer(River);
		LakeDist = Chamfer(i => Lake[i]);
		BigDist = Chamfer(i => River(i) && Catchment(i) >= 8 * RiverMin);
	}
	// each cell's distance (in cells) to the nearest cell where src is true: a two-pass sweep (1 across, 1.414... corner to corner)
	double[] Chamfer(Func<int, bool> src)
	{
		int NN = N * N; var d = new double[NN];
		for (int i = 0; i < NN; i++) d[i] = src(i) ? 0 : 1e9;
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			int i = z * N + x;
			if (x > 0) d[i] = Math.Min(d[i], d[i - 1] + 1);
			if (z > 0) { d[i] = Math.Min(d[i], d[i - N] + 1); if (x > 0) d[i] = Math.Min(d[i], d[i - N - 1] + SQ2); if (x + 1 < N) d[i] = Math.Min(d[i], d[i - N + 1] + SQ2); }
		}
		for (int z = N - 1; z >= 0; z--) for (int x = N - 1; x >= 0; x--)
		{
			int i = z * N + x;
			if (x + 1 < N) d[i] = Math.Min(d[i], d[i + 1] + 1);
			if (z + 1 < N) { d[i] = Math.Min(d[i], d[i + N] + 1); if (x + 1 < N) d[i] = Math.Min(d[i], d[i + N + 1] + SQ2); if (x > 0) d[i] = Math.Min(d[i], d[i + N - 1] + SQ2); }
		}
		return d;
	}
	// how steep the land is around a cell (blocks up per block across), from its neighbours
	public double Slope(int x, int z)
	{
		double gx = Elev[z * N + Math.Min(N - 1, x + 1)] - Elev[z * N + Math.Max(0, x - 1)], gz = Elev[Math.Min(N - 1, z + 1) * N + x] - Elev[Math.Max(0, z - 1) * N + x];
		return Math.Sqrt(gx * gx + gz * gz) / (2 * Cell);
	}
	// a fingerprint of the plan, to check the web version makes the same one: FNV-1a over each cell's plate, where it drains, its
	// biome, and its height, temperature, rain, water level, swing, rain season, moisture and distance to the sea, to 1/1024
	public uint Fingerprint()
	{
		uint h = 2166136261;
		void Add(int v) { unchecked { h = (h ^ (uint)v) * 16777619; } }
		for (int i = 0; i < N * N; i++)
		{
			Add(Plate[i]); Add(Down[i]); Add(Biome[i]);
			Add((int)Math.Floor(Elev[i] * 1024)); Add((int)Math.Floor(Temp[i] * 1024)); Add((int)Math.Floor(Rain[i] * 1024)); Add((int)Math.Floor(Level[i] * 1024));
			Add((int)Math.Floor(Swing[i] * 1024)); Add((int)Math.Floor(Season[i] * 1024)); Add((int)Math.Floor(Humid[i] * 1024)); Add((int)Math.Floor(Math.Min(SeaDist[i], 65536) * 1024));
		}
		return h;
	}
	// how much water runs through a cell, as the wet ground (in blocks) draining through it
	public double Catchment(int i) => Flow[i] * Cell * Cell;
	public const double RiverStart = 2500;   // (a river starts where this much wet ground drains through)
	public double RiverMin => RiverStart * Belt * Belt;   // (Draft 2j: a share of the map, (Size / 2048) squared: the same rivers at every size)
	public bool River(int i) => !Sea[i] && !Lake[i] && Catchment(i) >= Math.Max(RiverMin, 3.0 * Cell * Cell);   // (at least 3 cells drain through)
	// (Draft 2g, the user: "thicker rivers". Width 2.5 x (catchment / 2500)^0.75 blocks (worked out with square roots only), so a
	// stream starts about 2.5 wide, middling rivers are 8-16 and the great rivers near their mouths 40 or more, up to 48 in a
	// 2048 world and more in bigger ones: 48 x the fourth root of (Size / 2048), so 36 at 640 and 96 at 32768. The channel's
	// depth goes with it: 1 + the square root of the width (2.6 to about 11).
	public double RiverWidth(int i)
	{
		// (Draft 2j, the user: "rivers and lakes should also scale": as wide as a share of the map, Belt x what a 2048 world has,
		// without the old fourth-root cap; as deep as the world is tall, Tall x (1 + the square root of the 2048 width))
		double r = Math.Max(1, Catchment(i) / RiverMin);
		return Math.Max(2, Belt * Math.Min(48, 2.5 * Math.Sqrt(r) * Math.Sqrt(Math.Sqrt(r))));
	}
	public double RiverDepth(int i) => Tall * (1 + Math.Sqrt(RiverWidth(i) / Belt));
	// (Draft 2j: how steep a river runs, read as a 2048 world would: its fall per block x Belt / Tall, the same at every size)
	public double Pace(double fall) => fall * Belt / Tall;
	// how much a river braids (0-1): wide (10 to 20 blocks up), slow (its water falling under 6 blocks in 1000, fully under 2) and
	// carrying a lot of gravel and silt (on river fill, or where the old ice was; half as much otherwise). The blocks then split
	// the channel around gravel and silt bars where this is high.
	public double[] Braid;
	void MakeBraids()
	{
		int NN = N * N; Braid = new double[NN];
		for (int i = 0; i < NN; i++)
		{
			if (!River(i)) continue;
			int r = Down[i]; if (r < 0) continue;
			double d = (i % N != r % N && i / N != r / N) ? Cell * SQ2 : Cell, fall = Math.Max(0, Level[i] - Level[r]) / d;
			bool load = Alluvium != null && Alluvium[i] >= 1 || IceWas != null && IceWas[i] >= 0.3;
			Braid[i] = Smoothstep(10, 20, RiverWidth(i) / Belt) * Smoothstep(0.006, 0.002, Pace(fall)) * (load ? 1 : 0.5);
		}
	}

	// a binary heap of cells, lowest level first (ties: lower cell number first), the same in both versions
	sealed class Heap
	{
		readonly double[] key; readonly int[] id; public int Count;
		public Heap(int cap) { key = new double[cap]; id = new int[cap]; }
		public double TopKey => key[0];
		bool Less(int a, int b) => key[a] < key[b] || key[a] == key[b] && id[a] < id[b];
		void Swap(int a, int b) { (key[a], key[b]) = (key[b], key[a]); (id[a], id[b]) = (id[b], id[a]); }
		public void Push(double k, int i)
		{
			int c = Count++; key[c] = k; id[c] = i;
			while (c > 0) { int p = (c - 1) >> 1; if (!Less(c, p)) break; Swap(c, p); c = p; }
		}
		public int Pop()
		{
			int top = id[0]; Count--;
			if (Count > 0)
			{
				key[0] = key[Count]; id[0] = id[Count];
				int c = 0;
				while (true)
				{
					int l = 2 * c + 1, r = l + 1, m = c;
					if (l < Count && Less(l, m)) m = l;
					if (r < Count && Less(r, m)) m = r;
					if (m == c) break;
					Swap(c, m); c = m;
				}
			}
			return top;
		}
	}

	// ---- biomes, from the climate (the year's average temperature with height, its summer warmth, its winter cold), the rain,
	// when the rain falls, and the height. Trees need a summer of about 9 degrees (the treeline, on mountains and towards the poles
	// alike); below that is tundra (alpine meadows up high), then bare rock, then ice. The coldest and the hottest dry places are
	// desolate wastes. ----
	// how dry the wastes are: the rain below which the cold lands (and, hot, the hot lowlands) are desolate. At least the driest
	// 2% of the world's land becomes each waste, wherever it has cold or hot dry land, so most worlds have both somewhere.
	public double ColdDry = 0.1, HotDry = 0.06;
	public int BiomeFor(double T, double S, double R, double Q, double e)
	{
		double Ts = T + S, Tw = T - S;   // (summer and winter)
		bool high = e > High;
		if (Ts < 0) return R < ColdDry ? ColdWaste : high ? Peaks : Glacier;
		if (high && Ts < 9 && R < ColdDry + 0.03) return ColdWaste;   // (a high, cold, dry plateau)
		if (high && Ts < 4) return Crags;
		if (high && Ts < 9) return Alpine;
		if (Ts < 9) return R < ColdDry ? ColdWaste : Tundra;
		if (T < 2) return R < 0.16 ? Plains : Tw < -22 ? Snowy : Taiga;   // (the cold forests: larch-and-snow lands where winters are fiercest)
		if (T < 18)
			return R < 0.1 ? Desert : R < 0.28 ? Plains : Q < -0.3 && T >= 10 && R < 0.6 ? DryScrub : R < 0.4 ? Meadow : R > 0.75 && e < 8 ? Swamp : Woods;
		return R < HotDry && T >= 20 && !high ? HotWaste : R < 0.16 ? (e > 22 * Amp ? Badlands : Desert) : R < 0.4 ? (Q < -0.3 ? DryScrub : Savanna)
			: R > 0.85 && e < 6 ? Swamp : T >= 21 && R >= 0.62 ? Rainforest : Q > 0.3 ? DryForest : Woods;
	}
	void MakeBiomes()
	{
		int NN = N * N; Biome = new byte[NN];
		// the wastes' rain: count the cold and the hot dry land by its rain (in 1/1024 steps), then take the level that holds 2% of all land
		var cold = new int[1025]; var hot = new int[1025]; int land = 0;
		for (int i = 0; i < NN; i++)
		{
			if (Sea[i] || Lake[i]) continue;
			land++;
			int r = (int)Math.Floor(Math.Min(1, Rain[i]) * 1024);
			if (Temp[i] + Swing[i] < 9) cold[r]++;
			else if (Temp[i] >= 20 && Elev[i] <= High) hot[r]++;
		}
		double Level2(int[] h, double least, double most)
		{
			int want = (int)Math.Floor(land * 0.02), sum = 0;
			for (int r = 0; r <= 1024; r++) { sum += h[r]; if (sum >= want) return Math.Max(least, Math.Min(most, (r + 1) / 1024.0)); }
			return most;
		}
		ColdDry = Level2(cold, 0.1, 0.35); HotDry = Level2(hot, 0.06, 0.16);
		for (int i = 0; i < NN; i++)
			Biome[i] = (byte)(Sea[i] ? Ocean : Lake[i] ? LakeB : BiomeFor(Temp[i], Swing[i], Rain[i], Season[i], Elev[i]));
	}

	// ---- sub-biomes: the kinds of land inside a biome, and between two (the transitions), picked from the plan's climate, height,
	// slope and distance to water, with patches of noise. Each is local (rule A): this spot's cells and the seed only. Where none
	// applies, it's the biome itself. (Draft: the nearest cell's numbers; chunks will blend the plan between cell middles.) ----
	public const int FirstSub = 32;
	public const int Crevasses = 32, Meltwater = 33, Nunataks = 34, GlacialGravel = 35, FrostStone = 36, WindGravel = 37, FrozenSalt = 38,
		LichenField = 39, FrostCracks = 40, ThawBogs = 41, ForestTundra = 42, LichenWoods = 43, LarchStand = 44, Muskeg = 45, OldBurn = 46,
		OldGrowth = 47, BirchGrove = 48, Clearing = 49, FernRavine = 50, Parkland = 51, Tallgrass = 52, Shortgrass = 53, Steppe = 54,
		Wildflowers = 55, WetMeadow = 56, ReedMarsh = 57, SwampForest = 58, Fen = 59, RockyScrub = 60, FireScar = 61,
		FernUnder = 62, FloodForest = 63, CloudForest = 64, WoodedSavanna = 65, Waterhole = 66, Mounds = 67, ThornScrub = 68,
		DuneSea = 69, Pavement = 70, Oasis = 71, DryRiverbed = 72, SaltFlats = 73, CrackedBed = 74, SandSea = 75,
		Treeline = 76, Scree = 77, Cliffs = 78, Snowfield = 79, PeakGlacier = 80,
		Floodplain = 81, Canyon = 82, Delta = 83, ReedBed = 84, BogEdge = 85, SandBeach = 86, Shingle = 87, SeaCliffs = 88, Dunes = 89,
		SaltMarsh = 90, StiltSwamp = 91, Reef = 92, WeedBeds = 93, SeaIce = 94, DeepSea = 95, Trench = 96,
		FjordS = 97, TroughS = 98, CirqueS = 99, MoraineS = 100, Erratics = 101, Fan = 102, Mesas = 103, Tors = 104, Gullies = 105,
		KarstS = 106, TowerKarst = 107, LoessHills = 108, Yardangs = 109, Platform = 110, FogCoast = 111;
	public static readonly string[] SubNames = {
		"crevasse field", "meltwater pools", "nunataks (peaks through the ice)", "glacial gravel", "frost-split stone field", "wind-scoured gravel", "frozen salt pan",
		"lichen field", "frost-crack ground", "thaw ponds and bogs", "forest-tundra", "lichen woodland", "larch stand", "muskeg", "old burn",
		"old-growth", "birch and aspen grove", "flower clearing", "fern ravine", "parkland", "tallgrass prairie", "shortgrass prairie", "steppe",
		"wildflower patch", "wet meadow", "reed marsh", "swamp forest", "fen", "rocky shrubland", "old fire scar",
		"fern understory", "floodplain forest", "cloud forest", "wooded savanna", "waterhole", "termite mounds", "thorn scrub",
		"dune sea", "rocky pavement", "oasis", "dry riverbed", "salt flats", "cracked lake bed", "lifeless sand sea",
		"treeline (wind-bent trees)", "scree slopes", "cliffs", "snowfield", "peak glacier",
		"floodplain", "canyon", "delta", "reed bed", "bog edge", "sand beach", "shingle beach", "sea cliffs", "coastal dunes",
		"salt marsh", "stiltroot swamp", "warm reef", "weed beds", "sea ice", "deep ocean", "trench",
		"fjord", "glacial trough (U-valley)", "cirque", "moraine", "erratic boulder field", "alluvial fan", "mesas and buttes", "tors",
		"badland gullies", "sinkhole country (karst)", "tower karst", "loess hills", "yardangs", "wave-cut platform and sea stacks",
		"fog coast" };
	// (Draft 2n, the user: "Make the sea ice edge smaller and wavier". The sea's warmth changes only with latitude, so a plain
	// "colder than" made a ruler-straight band. Now a colder line (about half as deep) and a wobble in degrees: broad bays and
	// tongues (3 octaves, the noise 0.12 of the map across) and smaller ragged bits (2 octaves, 0.03 of the map). Both are in
	// shares of the map, so the bays and tongues are the same share of the map at every size, like the rest of the plan. Salts
	// 5270 and 5280, which nothing else uses. The mean of each part is taken off (an octave's mean is 0.5), so the band keeps its
	// depth. Option: IceSeal, extra cold near the map's north and south edges (fading in as a square over IceSealW of the map),
	// so no bay opens all the way to the world's edge)
	public static bool IceOld = false;                                     // (testing: the 2m rule, T < -8)
	public static double IceCut = -17.5, IceBroad = 30, IceRagged = 14;    // (degrees)
	public static double IceBroadW = 0.12, IceRaggedW = 0.03;              // (the noise's size, a share of the map)
	public static double IceSeal = 0, IceSealW = 0.03;                     // (degrees at the very edge; how far in it fades out, a share of the map. OFF: the user, 2026-11-26: "Let bays reach it")
	public static bool IceTurn = true;   // (testing: off = the noise's grid square to the map)
	public double IceWob(int bx, int bz)
	{
		double a = Size * IceBroadW, b = Size * IceRaggedW, w;
		if (!IceTurn) w = (Fbm(bx / a, bz / a, 3, 5270) - 0.4375) * IceBroad + (Fbm(bx / b, bz / b, 2, 5280) - 0.375) * IceRagged;
		else   // (value noise lines up with its grid, so square bays; each part's grid is turned: 3-4-5 and 7-24-25, no angles needed)
			w = (Fbm((0.8 * bx - 0.6 * bz) / a, (0.6 * bx + 0.8 * bz) / a, 3, 5270) - 0.4375) * IceBroad
				+ (Fbm((0.28 * bx + 0.96 * bz) / b, (0.28 * bz - 0.96 * bx) / b, 2, 5280) - 0.375) * IceRagged;
		if (IceSeal > 0) { double d = Math.Min(bz + 0.5, Size - bz - 0.5) / (Size * IceSealW); if (d < 1) w -= IceSeal * (1 - d) * (1 - d); }
		return w;
	}
	public int SubAt(int bx, int bz)
	{
		int cx = Math.Max(0, Math.Min(N - 1, bx / Cell)), cz = Math.Max(0, Math.Min(N - 1, bz / Cell)), i = cz * N + cx;
		int b = Biome[i];
		double T = Temp[i], S = Swing[i], R = Rain[i], e = Elev[i], Ts = T + S, Tw = T - S;
		double p = Fbm(bx / 220.0, bz / 220.0, 3, 5250), p2 = Fbm(bx / 90.0, bz / 90.0, 2, 5260);   // (big and small patches; Draft 2k: salts 5250 and 5260, which nothing else uses (5200 and 5210 are the history's))
		double sl = Slope(cx, cz), sd = SeaDist[i] * Cell, rd = RiverDist[i] * Cell, ld = LakeDist[i] * Cell, bd = BigDist[i] * Cell;
		if (Sea[i])
		{
			if (Fjord != null && Fjord[i]) return FjordS;   // (a glacier's trough the sea came into)
			if (IceOld ? T < -8 : T + IceWob(bx, bz) < IceCut) return SeaIce;   // (the sea that stays frozen; the weather can freeze more in winter later)
			if (Coast != null && Coast[i] == 1 && e > -6) return Platform;   // (the waves cut the coast back: a rocky shelf, sea stacks)
			if (e < -40 * Amp) return Trench;
			if (e < -24 * Amp) return DeepSea;
			if (e > -12 && T >= 22) return Reef;
			if (e > -14 && T < 12) return WeedBeds;
			return Ocean;
		}
		if (Lake[i]) return LakeB;
		bool desolate = b == ColdWaste || b == HotWaste, icy = b == Glacier || b == Peaks || b == Crags;
		// along the coast
		double coast = Math.Max(10, Cell * 1.5);
		if (!desolate && !icy && sd <= coast)
		{
			if (sl > 0.5 || Coast != null && Coast[i] == 2) return SeaCliffs;
			if (Cur != null && Cur[i] < -1.5 && R < 0.4) return FogCoast;   // (a cold current offshore: cool, foggy and dry)
			if (rd <= coast && sl < 0.08 && e < 6) return Delta;
			if (e < 5 && sl < 0.08)
			{
				if (T >= 22 && R >= 0.35) return StiltSwamp;
				if (T >= 6 && R >= 0.3 && p2 > 0.45) return SaltMarsh;
			}
			if (T < 5) return Shingle;
			if (R < 0.3 && p2 > 0.5) return Dunes;
			return SandBeach;
		}
		// by rivers and lakes
		if (!desolate && !icy)
		{
			bool dry = b == Desert || b == Badlands || b == DryScrub || b == Savanna && R < 0.25 || b == Plains && R < 0.17;
			if (River(i) && dry && sl > 0.2) return Canyon;
			if (bd <= 24 && sl < 0.08 && e < High && Ts >= 9) return b == Rainforest ? FloodForest : Floodplain;
			if (ld <= Math.Max(8, Cell) && sl < 0.1) return T < 5 ? BogEdge : ReedBed;
		}
		// what ages of erosion left (the plan's erosion): the old ice's marks in the high mountains too, the rest below them
		if (Wear != null && b != Glacier)
		{
			if (Head[i]) return CirqueS;
			if (Trough[i] >= 3 * Amp && sl < 0.25) return TroughS;
		}
		if (Wear != null && !icy && b != Glacier)
		{
			if (Moraine[i] >= 1.5 * Amp) return MoraineS;
			if (!desolate && Karst[i] > 0.3) return T >= 20 && R >= 0.55 ? TowerKarst : KarstS;
			if (!desolate && Loess[i] >= 2 * Amp) return LoessHills;
			if (Alluvium[i] >= 1.2 * Amp && sl > 0.03 && sl < 0.2 && !River(i)) return Fan;
			bool dryland = R < 0.3;
			if (dryland && SurfK[i] < 0.3 && sl > 0.2 && Oro[i] < 0.3 && e < High) return Mesas;
			if (dryland && SurfK[i] >= 0.8 && sl > 0.15) return Gullies;
			// (p2 is 0 to 0.75, about 0.375 on average)
			if (R < 0.1 && SurfK[i] >= 0.8 && Dune[i] < 0.5 && p2 > 0.4) return Yardangs;
			if (Craton[i] > 0.4 && SurfK[i] <= 0.2 && sl > 0.03 && sl < 0.3 && p2 > 0.4) return Tors;
			if (IceWas[i] >= 0.3 && sl < 0.08 && e < High && T > -4 && p2 > 0.45) return Erratics;
		}
		switch (b)
		{
			case Glacier: return e > 60 * Amp && sl > 0.3 ? Nunataks : Ts > -1.5 && p2 < 0.35 ? Meltwater : p > 0.6 ? Crevasses : Glacier;
			case ColdWaste: return sl < 0.05 && p2 < 0.3 ? FrozenSalt : p > 0.56 ? FrostStone : p < 0.42 ? WindGravel : ColdWaste;
			case Tundra: return Ts < 1.5 ? GlacialGravel : Ts >= 7.5 ? ForestTundra : R > 0.45 && sl < 0.06 ? ThawBogs : R < 0.22 ? LichenField : p > 0.6 ? FrostCracks : Tundra;
			case Snowy:
			case Taiga:
				if (e > High && Ts < 10.5) return Treeline;
				if (Ts < 10.5) return LichenWoods;
				if (R > 0.5 && sl < 0.05) return Muskeg;
				if (b == Taiga && R < 0.5 && p2 > 0.74) return OldBurn;
				if (b == Taiga && p > 0.6) return LarchStand;
				return b;
			case Alpine: return Ts >= 7.5 ? Treeline : sl > 0.45 ? Scree : Alpine;
			case Crags: return sl > 0.7 ? Cliffs : sl > 0.35 ? Scree : Crags;
			case Peaks: return sl < 0.2 ? Snowfield : p > 0.55 ? PeakGlacier : Peaks;
			case Woods:
				if (e > High && Ts < 10.5) return Treeline;
				if (T < 18 && R < 0.46) return Parkland;
				if (sl > 0.3 && rd <= 24) return FernRavine;
				if (p > 0.62) return OldGrowth;
				if (p < 0.36 && T < 11) return BirchGrove;
				if (p2 > 0.74) return Clearing;
				return Woods;
			case Plains:
				if (T < 2 || R < 0.17) return Steppe;
				if (R >= 0.25) return Parkland;
				if (p2 > 0.7) return Wildflowers;
				return R < 0.21 ? Shortgrass : Tallgrass;
			case Meadow: return rd <= 12 && sl < 0.06 ? WetMeadow : p2 > 0.66 ? Wildflowers : Meadow;
			case Swamp: return T < 8 ? Fen : p < 0.42 ? ReedMarsh : p > 0.58 ? SwampForest : Swamp;
			case DryScrub: return sl > 0.3 ? RockyScrub : p2 > 0.72 ? FireScar : DryScrub;
			case Desert:
			case Badlands:
				if (!River(i) && Catchment(i) >= RiverMin / 4) return DryRiverbed;
				if (rd <= 16 || ld <= 16) return Oasis;
				if (b == Badlands) return Badlands;
				if (R >= 0.1) return T < 18 ? Steppe : ThornScrub;
				if ((Dune != null && Dune[i] > 0.6 || p > 0.6) && R < 0.09) return DuneSea;
				if (p < 0.38) return Pavement;
				return Desert;
			case HotWaste: return sl < 0.04 && p2 < 0.38 ? SaltFlats : sl < 0.06 && p2 < 0.46 ? CrackedBed : p > 0.55 ? SandSea : HotWaste;
			case Savanna: return R < 0.19 ? ThornScrub : R >= 0.33 ? WoodedSavanna : p2 > 0.8 && sl < 0.05 ? Waterhole : p > 0.6 ? Mounds : Savanna;
			case Rainforest: return e > 28 * Amp && R > 0.7 ? CloudForest : p2 > 0.62 ? FernUnder : Rainforest;
			default: return b;
		}
	}

	// ---- (test) pictures of the plan: --planmap=<folder> ----
	public static readonly uint[] BiomeColors = {
		0x3d7a2a, 0x9ac25a, 0xe8d58c, 0xdfeaf0, 0x4f6b3a, 0x2f5a46, 0x7fcf6a, 0xc66a3a,
		0x9aa58a, 0xbfe6ff, 0x1f6a1a, 0xc8b45a, 0xa7d07a, 0x8a8a8a, 0xffffff, 0x2a5a9a, 0x4a8ad0,
		0xb4aea6, 0xf7efdc, 0x9a8f55, 0x7a8a2a };
	public static readonly uint[] SubColors = {
		0x9fd4f5, 0x5fb0ff, 0x6a737c, 0xa89f94, 0x8f939a, 0xc3beb6, 0xf4f0e8,
		0xc8c27a, 0x8c9479, 0x5f8f86, 0x6e8a52, 0x58775a, 0xa0a83a, 0x3f4f38, 0x3a2a24,
		0x174a12, 0xb4d68a, 0xe0d070, 0x2f7a4a, 0x6fa24a, 0xa8c860, 0xc8c880, 0xd0c090,
		0xe890c8, 0x5fb28a, 0x7a9a3a, 0x2a4422, 0x8a8456, 0xa09070, 0x5a4a3a,
		0x2a9a3a, 0x3a7a5a, 0x5a9a8a, 0x9a9a38, 0x5a9ad0, 0xb07a4a, 0xbba070,
		0xf8e4a8, 0xa88a6a, 0x30b080, 0xd8c098, 0xffffff, 0xc8a080, 0xf0d6a8,
		0x5a7a40, 0xb0b0b0, 0x505050, 0xf0f8ff, 0x9ad4ff,
		0x86b060, 0xb85a30, 0x9ac07a, 0x7a9a4a, 0x6a7a5a, 0xf0e0a0, 0x9a9a92, 0x686868, 0xead890,
		0x8a9a6a, 0x3a5a2a, 0x30c8c8, 0x2a7a5a, 0xe0f0ff, 0x1a3a7a, 0x0a1a4a,
		0x1d5e8c, 0x7fae9a, 0xc9d8e0, 0x8d7f9a, 0xa6a39a, 0xc8b070, 0xb5652e, 0x9a8a80, 0xd08a5a, 0xa0a070, 0x3f8a50, 0xd2b46a, 0xc8a888, 0x5aa0b0, 0x9fb4b0 };
	// what each sub-biome belongs to (pictures and the list): the biome, or the two it lies between
	public static readonly string[] SubParent = {
		"glacier", "glacier", "glacier", "glacier to tundra", "cold waste", "cold waste", "cold waste",
		"tundra", "tundra", "tundra", "tundra to taiga", "taiga to tundra", "taiga", "taiga, snowy lands", "taiga",
		"woods", "woods", "woods", "woods", "woods to prairie", "prairie", "prairie", "prairie to desert",
		"prairie, meadow", "meadow", "swamp", "swamp", "swamp", "dry scrub", "dry scrub",
		"rainforest", "rainforest", "rainforest", "savanna", "savanna", "savanna", "savanna to desert",
		"desert", "desert", "desert", "desert, badlands", "hot waste", "hot waste", "hot waste",
		"mountains: forest to alpine", "mountains", "mountains", "snow peaks", "snow peaks",
		"rivers", "rivers", "rivers", "lakes", "lakes", "coasts", "coasts", "coasts", "coasts",
		"coasts", "coasts", "sea", "sea", "sea", "sea", "sea",
		"sea (the old ice's troughs)", "mountains, taiga (the old ice)", "mountains (the old ice)", "the old ice's edge", "lowlands the old ice covered",
		"the foot of hills", "dry lands (hard caps)", "old cores (granite)", "dry lands (soft rock)", "limestone lands", "limestone, wet tropics",
		"downwind of deserts and the old ice", "deserts (soft rock)", "coasts (soft rock)", "coasts (cold currents)" };
	// the rivers drawn at their true width (Draft 2g): each river cell's line to the cell it runs into, as wide as the river is (at
	// least a pixel); frozen ones pale. Braided stretches teal.
	void DrawRivers(Godot.Image img, int scale)
	{
		int W = N * scale; double ppb = (double)scale / Cell;   // (pixels per block)
		const int S8 = 8; var qx = new double[S8 + 1]; var qz = new double[S8 + 1]; var qr = new double[S8 + 1];
		for (int i = 0; i < N * N; i++)
		{
			if (!River(i) || Down[i] < 0) continue;
			RiverCurve(i, 0, 0, ppb, S8, qx, qz, qr);   // (Draft 2k: along its curve)
			var col = Temp[i] < -1 ? new Godot.Color(0.85f, 0.95f, 1f) : Braid != null && Braid[i] > 0.5 ? new Godot.Color(0.2f, 0.72f, 0.72f) : new Godot.Color(0.15f, 0.35f, 0.95f);
			for (int s = 0; s < S8; s++)
			{
				double ax = qx[s], az = qz[s], bx = qx[s + 1], bz = qz[s + 1], r0 = qr[s], r1 = qr[s + 1], rm = Math.Max(r0, r1);
				int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - rm)), x1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(ax, bx) + rm));
				int z0 = Math.Max(0, (int)Math.Floor(Math.Min(az, bz) - rm)), z1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(az, bz) + rm));
				double ex = bx - ax, ez = bz - az, ll = ex * ex + ez * ez;
				for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
				{
					double px = x + 0.5 - ax, pz = z + 0.5 - az, tt = ll > 0 ? Math.Max(0, Math.Min(1, (px * ex + pz * ez) / ll)) : 0;
					double dx = px - tt * ex, dz = pz - tt * ez, rr = r0 + (r1 - r0) * tt;
					if (dx * dx + dz * dz <= rr * rr) img.SetPixel(x, z, col);
				}
			}
		}
	}
	// the winds and the ocean currents (Draft 2g): the sea red where a current runs warm and blue where cold; the land grey;
	// white arrows the year's wind, yellow the summer monsoon (where it's strong); on the sea, red and blue arrows the currents
	void SaveWinds(string dir, int scale)
	{
		int W = N * scale;
		var img = Godot.Image.CreateEmpty(N, N, false, Godot.Image.Format.Rgb8);
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			int i = z * N + x;
			double l = Elev[z * N + Math.Max(0, x - 1)], r = Elev[z * N + Math.Min(N - 1, x + 1)], u = Elev[Math.Max(0, z - 1) * N + x], d = Elev[Math.Min(N - 1, z + 1) * N + x];
			float s = (float)Math.Clamp(0.85 + ((l - r) + (u - d)) * 0.03 / Cell * 4, 0.55, 1.15);
			if (Elev[i] < 0)
			{
				float c = (float)Math.Max(-1, Math.Min(1, Cur[i] / 5));
				var b = new Godot.Color(0.16f, 0.22f, 0.36f);
				img.SetPixel(x, z, c > 0 ? b.Lerp(new Godot.Color(0.85f, 0.25f, 0.2f), c) : b.Lerp(new Godot.Color(0.2f, 0.55f, 0.95f), -c));
			}
			else img.SetPixel(x, z, new Godot.Color(0.55f * s, 0.57f * s, 0.52f * s));
		}
		img.Resize(W, W, Godot.Image.Interpolation.Nearest);
		void Line(double x0, double z0, double x1, double z1, Godot.Color col)
		{
			double len = Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0)); int n = Math.Max(1, (int)Math.Ceiling(len));
			for (int k = 0; k <= n; k++) { int X = (int)Math.Floor(x0 + (x1 - x0) * k / n), Z = (int)Math.Floor(z0 + (z1 - z0) * k / n); if (X >= 0 && Z >= 0 && X < W && Z < W) img.SetPixel(X, Z, col); }
		}
		void Arrow(double cx, double cz, double vx, double vz, double len, Godot.Color col)
		{
			double l = Math.Sqrt(vx * vx + vz * vz); if (l < 1e-6) return;
			double ux = vx / l, uz = vz / l, hx = cx + ux * len / 2, hz = cz + uz * len / 2, tx = cx - ux * len / 2, tz = cz - uz * len / 2;
			Line(tx, tz, hx, hz, col); Line(tx + 1, tz, hx + 1, hz, col);
			double hl = Math.Max(3, len * 0.35);
			Line(hx, hz, hx - (ux * 0.8 - uz * 0.55) * hl, hz - (uz * 0.8 + ux * 0.55) * hl, col);
			Line(hx, hz, hx - (ux * 0.8 + uz * 0.55) * hl, hz - (uz * 0.8 - ux * 0.55) * hl, col);
		}
		int G = Math.Max(12, W / 24);
		for (int pz = G / 2; pz < W; pz += G) for (int px = G / 2; px < W; px += G)
		{
			int x = Math.Min(N - 1, px / scale), z = Math.Min(N - 1, pz / scale), i = z * N + x;
			double wl = Math.Sqrt(WX[i] * WX[i] + WZ[i] * WZ[i]);
			Arrow(px, pz, WX[i], WZ[i], G * 0.7 * Math.Min(1, 0.3 + wl), Elev[i] >= 0 ? new Godot.Color(1, 1, 1) : new Godot.Color(0.75f, 0.78f, 0.85f));
			double ml = Math.Sqrt(MX[i] * MX[i] + MZ[i] * MZ[i]);
			if (ml >= 0.25) Arrow(px + G * 0.25, pz + G * 0.25, WX[i] + MX[i], WZ[i] + MZ[i], G * 0.6, new Godot.Color(1, 0.85f, 0.1f));
		}
		int Gc = Math.Max(10, W / 36);
		for (int pz = Gc / 2; pz < W; pz += Gc) for (int px = Gc / 2; px < W; px += Gc)
		{
			int x = Math.Min(N - 1, px / scale), z = Math.Min(N - 1, pz / scale), i = z * N + x;
			if (Elev[i] >= 0 || Math.Abs(Cur[i]) < 0.8) continue;
			double cl = Math.Sqrt(CX[i] * CX[i] + CZ[i] * CZ[i]); if (cl < 0.15) continue;
			var col = Cur[i] > 0 ? new Godot.Color(1, 0.2f, 0.15f) : new Godot.Color(0.15f, 0.85f, 1);
			for (int t = -1; t <= 1; t++) Arrow(px, pz + t, CX[i], CZ[i], Gc * 0.8, col);
		}
		img.SavePng($"{dir}/winds.png");
	}
	// (testing, Draft 2g) the biggest river's lower course and mouth at one pixel per block, 512 x 512 blocks: the ground by its
	// height (blended between the cell middles) and shading, the sea, lakes, and the rivers at their true width, with gravel and
	// silt bars where they braid (a preview: the blocks will add the bends and the banks)
	public void SaveRiverMouth(string dir)
	{
		int NN = N * N, mouth = -1;
		for (int i = 0; i < NN; i++) if (River(i) && Down[i] >= 0 && Sea[Down[i]] && (mouth < 0 || Flow[i] > Flow[mouth])) mouth = i;
		if (mouth < 0) return;
		int Wb = Math.Min(512, Size);
		double P = System.Environment.GetEnvironmentVariable("MOUTHSHARE") != null ? Math.Max(1, Belt) : 1, T = P > 1 ? Tall : 1;   // (blocks a pixel; heights read x Tall)
		// up the main stream from the mouth, about half the window
		int c = mouth; double mx = (mouth % N + 0.5) * Cell, mz = (mouth / N + 0.5) * Cell, ux = mx, uz = mz;
		var ups = new List<int>[NN];
		for (int i = 0; i < NN; i++) if (Down[i] >= 0 && River(i)) { int r = Down[i]; (ups[r] ??= new List<int>()).Add(i); }
		for (int step = 0; step < 4 * N; step++)
		{
			if (ups[c] == null) break;
			int best = -1; foreach (int u in ups[c]) if (best < 0 || Flow[u] > Flow[best]) best = u;
			c = best; ux = (c % N + 0.5) * Cell; uz = (c / N + 0.5) * Cell;
			if ((ux - mx) * (ux - mx) + (uz - mz) * (uz - mz) >= 0.42 * Wb * P * 0.42 * Wb * P) break;
		}
		int WB = (int)(Wb * P), ox = (int)Math.Floor((mx + ux) / 2 - WB / 2.0), oz = (int)Math.Floor((mz + uz) / 2 - WB / 2.0);
		ox = Math.Max(0, Math.Min(Size - WB, ox)); oz = Math.Max(0, Math.Min(Size - WB, oz));
		var img = Godot.Image.CreateEmpty(Wb, Wb, false, Godot.Image.Format.Rgb8);
		var hgt = new double[Wb * Wb];
		for (int z = 0; z < Wb; z++) for (int x = 0; x < Wb; x++) hgt[z * Wb + x] = At(Elev, ox + (x + 0.5) * P, oz + (z + 0.5) * P);
		for (int z = 0; z < Wb; z++) for (int x = 0; x < Wb; x++)
		{
			double e = hgt[z * Wb + x]; int cx = Math.Min(N - 1, (int)((ox + (x + 0.5) * P) / Cell)), cz = Math.Min(N - 1, (int)((oz + (z + 0.5) * P) / Cell)), ci = cz * N + cx;
			Godot.Color col;
			if (e < 0) { float d = (float)Math.Min(1, -e / (30 * T)); col = new Godot.Color(0.25f, 0.55f, 0.85f).Lerp(new Godot.Color(0.08f, 0.2f, 0.45f), d); }
			else if (Lake[ci]) col = new Godot.Color(0.3f, 0.55f, 0.9f);
			else
			{
				double l = hgt[z * Wb + Math.Max(0, x - 1)], r = hgt[z * Wb + Math.Min(Wb - 1, x + 1)], u = hgt[Math.Max(0, z - 1) * Wb + x], dd = hgt[Math.Min(Wb - 1, z + 1) * Wb + x];
				float sh = (float)Math.Clamp(0.9 + ((l - r) + (u - dd)) * 0.12 / T, 0.6, 1.2);
				float t = (float)Math.Min(1, e / (120 * T));
				var g = e < 3 * T ? new Godot.Color(0.82f, 0.78f, 0.6f) : new Godot.Color(0.42f, 0.62f, 0.3f).Lerp(new Godot.Color(0.55f, 0.5f, 0.38f), t);
				col = new Godot.Color(g.R * sh, g.G * sh, g.B * sh);
			}
			img.SetPixel(x, z, col);
		}
		// (first every river piece marks its water: how far in from the nearer bank, how much it braids there and which way it runs;
		// then the bars, so one piece's water never cuts another's bar into rings. The bars are long lenses along the flow. Draft
		// 2k: each river cell's water along its curve)
		var inner = new double[Wb * Wb]; var brv = new double[Wb * Wb]; var fdx = new double[Wb * Wb]; var fdz = new double[Wb * Wb]; var froz = new bool[Wb * Wb];
		for (int k = 0; k < Wb * Wb; k++) inner[k] = -1;
		const int S8 = 8; var qx = new double[S8 + 1]; var qz = new double[S8 + 1]; var qr = new double[S8 + 1];
		for (int i = 0; i < NN; i++)
		{
			if (!River(i)) continue;
			int r = Down[i]; if (r < 0) continue;
			RiverCurve(i, ox, oz, 1 / P, S8, qx, qz, qr);
			double b0 = Braid[i], b1 = River(r) ? Braid[r] : b0;
			for (int s = 0; s < S8; s++)
			{
				double ax = qx[s], az = qz[s], bx = qx[s + 1], bz = qz[s + 1], r0 = qr[s], r1 = qr[s + 1], rm = Math.Max(r0, r1);
				if (Math.Max(ax, bx) + rm < 0 || Math.Min(ax, bx) - rm >= Wb || Math.Max(az, bz) + rm < 0 || Math.Min(az, bz) - rm >= Wb) continue;
				int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - rm)), x1 = Math.Min(Wb - 1, (int)Math.Ceiling(Math.Max(ax, bx) + rm));
				int z0 = Math.Max(0, (int)Math.Floor(Math.Min(az, bz) - rm)), z1 = Math.Min(Wb - 1, (int)Math.Ceiling(Math.Max(az, bz) + rm));
				double ex = bx - ax, ez = bz - az, ll = ex * ex + ez * ez, el = Math.Sqrt(ll);
				for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
				{
					double px = x + 0.5 - ax, pz = z + 0.5 - az, tq = ll > 0 ? Math.Max(0, Math.Min(1, (px * ex + pz * ez) / ll)) : 0;
					double dx = px - tq * ex, dz = pz - tq * ez, rr = r0 + (r1 - r0) * tq, dist = Math.Sqrt(dx * dx + dz * dz);
					if (dist > rr) continue;
					int k = z * Wb + x;
					if (rr - dist > inner[k])
					{
						inner[k] = rr - dist; brv[k] = b0 + (b1 - b0) * (s + tq) / S8; froz[k] = Temp[i] < -1;
						fdx[k] = el > 0 ? ex / el : 1; fdz[k] = el > 0 ? ez / el : 0;
					}
				}
			}
		}
		for (int z = 0; z < Wb; z++) for (int x = 0; x < Wb; x++)
		{
			int k = z * Wb + x; if (inner[k] < 0) continue;
			double gx = ox + x * P, gz = oz + z * P, along = (gx * fdx[k] + gz * fdz[k]) / (28.0 * P), across = (gz * fdx[k] - gx * fdz[k]) / (7.0 * P);
			double nb = ValueNoise(along, across, 7300) * 0.85 + ValueNoise(gx / 5.0, gz / 5.0, 7301) * 0.15;
			bool bar = brv[k] > 0.3 && inner[k] * P > 3 * P && nb > 0.86 - 0.14 * brv[k];
			img.SetPixel(x, z, bar ? new Godot.Color(0.8f, 0.74f, 0.6f) : froz[k] ? new Godot.Color(0.85f, 0.95f, 1f) : new Godot.Color(0.16f, 0.4f, 0.92f));
		}
		// a scale bar: 100 blocks
		for (int z = Wb - 14; z < Wb - 10; z++) for (int x = 10; x < 110; x++) img.SetPixel(x, z, new Godot.Color(1, 1, 1));
		img.SavePng($"{dir}/river_mouth.png");
		Godot.GD.Print($"PLANMOUTH the biggest river's mouth at x {mx:0}, z {mz:0} ({RiverWidth(mouth):0} blocks wide, {RiverDepth(mouth):0.0} deep, braids {Braid[mouth]:0.00}); close-up x {ox}-{ox + WB - 1}, z {oz}-{oz + WB - 1}, {P:0} blocks a pixel");
	}
	public void SavePictures(string dir, int scale)
	{
		Godot.DirAccess.MakeDirRecursiveAbsolute(dir);
		Godot.Color Rgb(uint c) => new Godot.Color(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f);
		double Shade(int x, int z)
		{
			double l = Elev[z * N + Math.Max(0, x - 1)], r = Elev[z * N + Math.Min(N - 1, x + 1)], u = Elev[Math.Max(0, z - 1) * N + x], d = Elev[Math.Min(N - 1, z + 1) * N + x];
			return Math.Clamp(0.85 + ((l - r) + (u - d)) * 0.03 / Cell * 4, 0.55, 1.15);
		}
		Godot.Color HeightColor(int i, int x, int z)
		{
			double e = Elev[i];
			Godot.Color c;
			if (Sea[i]) c = new Godot.Color(0.10f, 0.25f, 0.55f).Lerp(new Godot.Color(0.35f, 0.6f, 0.9f), (float)Math.Clamp((e + 50) / 50, 0, 1));
			else if (Lake[i]) c = new Godot.Color(0.3f, 0.55f, 0.9f);
			else
			{
				float t = (float)Math.Clamp(e / (120 * Amp), 0, 1);
				c = t < 0.15f ? new Godot.Color(0.35f, 0.6f, 0.3f).Lerp(new Godot.Color(0.55f, 0.7f, 0.35f), t / 0.15f)
					: t < 0.5f ? new Godot.Color(0.55f, 0.7f, 0.35f).Lerp(new Godot.Color(0.6f, 0.5f, 0.35f), (t - 0.15f) / 0.35f)
					: new Godot.Color(0.6f, 0.5f, 0.35f).Lerp(new Godot.Color(1, 1, 1), (t - 0.5f) / 0.5f);
				float s = (float)Shade(x, z); c = new Godot.Color(c.R * s, c.G * s, c.B * s);
			}
			return c;
		}
		void Save(string name, Func<int, int, Godot.Color> px, bool rivers = false, bool marks = false)
		{
			var img = Godot.Image.CreateEmpty(N, N, false, Godot.Image.Format.Rgb8);
			for (int z = 0; z < N; z++) for (int x = 0; x < N; x++) img.SetPixel(x, z, px(x, z));
			img.Resize(N * scale, N * scale, Godot.Image.Interpolation.Nearest);
			if (rivers) DrawRivers(img, scale);
			if (marks) foreach (var v in Volcanoes)
				for (int dz = -2; dz <= 2; dz++) for (int dx = -2; dx <= 2; dx++)
				{
					int X = v[0] * scale + scale / 2 + dx, Z = v[1] * scale + scale / 2 + dz;
					if (X >= 0 && Z >= 0 && X < N * scale && Z < N * scale && (Math.Abs(dx) == 2 || Math.Abs(dz) == 2)) img.SetPixel(X, Z, v[2] == 4 ? new Godot.Color(1, 0, 1) : new Godot.Color(1, 0.1f, 0));
				}
			img.SavePng($"{dir}/{name}.png");
		}
		Save("height", (x, z) => HeightColor(z * N + x, x, z), rivers: true);
		SaveWinds(dir, scale); SaveRiverMouth(dir);
		Save("biomes", (x, z) => { int i = z * N + x; var c = Rgb(BiomeColors[Biome[i]]); float s = Sea[i] || Lake[i] ? 1 : (float)Shade(x, z); return new Godot.Color(c.R * s, c.G * s, c.B * s); }, rivers: true);
		if (Wear != null)
		{
			// what erosion did: red worn away, green laid down (up to 40 blocks either way), on the land's shading; sea dark
			Save("wear", (x, z) =>
			{
				int i = z * N + x; if (Sea[i]) return Fjord[i] ? new Godot.Color(0.2f, 0.75f, 0.95f) : new Godot.Color(0.12f, 0.14f, 0.22f);
				float s = (float)Shade(x, z), w = (float)Math.Clamp(Wear[i] / 40, -1, 1);
				var c = w > 0 ? new Godot.Color(0.75f, 0.75f, 0.72f).Lerp(new Godot.Color(0.9f, 0.15f, 0.1f), w) : new Godot.Color(0.75f, 0.75f, 0.72f).Lerp(new Godot.Color(0.15f, 0.75f, 0.2f), -w);
				return new Godot.Color(c.R * s, c.G * s, c.B * s);
			});
			// erosion's features: the old ice (pale blue; troughs deeper blue, moraines purple, cirques white), river fill (yellow-green),
			// loess (tan), karst (orange), sand seas (sand), coasts cut back (red) and sea cliffs (white), scree (grey)
			Save("erosion", (x, z) =>
			{
				int i = z * N + x;
				if (Sea[i]) return Fjord[i] ? new Godot.Color(0.2f, 0.75f, 0.95f) : Coast[i] == 1 ? new Godot.Color(0.95f, 0.25f, 0.2f) : new Godot.Color(0.12f, 0.14f, 0.22f);
				var c = new Godot.Color(0.55f, 0.6f, 0.5f);
				if (IceWas[i] >= 0.3) c = new Godot.Color(0.72f, 0.82f, 0.92f);
				if (Trough[i] >= 3 * Amp) c = new Godot.Color(0.25f, 0.45f, 0.85f);
				if (Head[i]) c = new Godot.Color(1, 1, 1);
				if (Moraine[i] >= 1.5 * Amp) c = new Godot.Color(0.55f, 0.35f, 0.7f);
				if (Alluvium[i] >= 1.5) c = new Godot.Color(0.7f, 0.8f, 0.3f);
				if (Loess[i] >= 1) c = new Godot.Color(0.82f, 0.68f, 0.4f).Lerp(new Godot.Color(0.7f, 0.5f, 0.2f), (float)Math.Min(1, Loess[i] / (6 * Amp)));
				if (Karst[i] > 0.3) c = new Godot.Color(0.95f, 0.55f, 0.15f);
				if (Dune[i] > 0.6) c = new Godot.Color(0.95f, 0.88f, 0.6f);
				if (ScreeT[i] >= 3) c = new Godot.Color(0.5f, 0.5f, 0.5f);
				if (Coast[i] == 2) c = new Godot.Color(1, 1, 1);
				float s = (float)Shade(x, z); return new Godot.Color(c.R * s, c.G * s, c.B * s);
			}, rivers: true);
		}
		Save("temperature", (x, z) => { double t = Math.Clamp((Temp[z * N + x] + 25) / 55, 0, 1); return new Godot.Color((float)t, 0.25f, (float)(1 - t)); });
		Save("rain", (x, z) => { double r = Rain[z * N + x]; return Sea[z * N + x] ? new Godot.Color(0.15f, 0.15f, 0.25f) : new Godot.Color((float)(0.85 - 0.7 * r), (float)(0.75 - 0.2 * r), (float)(0.4 + 0.5 * r)); });
		// sub-biomes at each cell's middle (the biome's own colour where none applies)
		Save("subbiomes", (x, z) =>
		{
			int i = z * N + x, s = SubAt(x * Cell + Cell / 2, z * Cell + Cell / 2);
			var c = Rgb(s >= FirstSub ? SubColors[s - FirstSub] : BiomeColors[s]); float k = Sea[i] || Lake[i] ? 1 : (float)Shade(x, z);
			return new Godot.Color(c.R * k, c.G * k, c.B * k);
		}, rivers: true);
		// the summer-to-winter range (2 x Swing): pale small, deep purple big (40 degrees or more)
		Save("swing", (x, z) =>
		{
			int i = z * N + x; float t = (float)Math.Clamp(2 * Swing[i] / 40, 0, 1);
			var c = new Godot.Color(0.95f, 0.95f, 0.85f).Lerp(new Godot.Color(0.35f, 0.1f, 0.5f), t);
			return Sea[i] ? c.Darkened(0.55f) : c;
		});
		// when the rain falls: orange-brown wet winters (dry summers), pale green all year, teal wet summers (dry winters)
		Save("rainseason", (x, z) =>
		{
			int i = z * N + x; double q = Season[i];
			var c = q < 0 ? new Godot.Color(0.85f, 0.9f, 0.75f).Lerp(new Godot.Color(0.8f, 0.45f, 0.15f), (float)Math.Min(1, -q))
			              : new Godot.Color(0.85f, 0.9f, 0.75f).Lerp(new Godot.Color(0.05f, 0.55f, 0.55f), (float)Math.Min(1, q));
			return Sea[i] ? c.Darkened(0.55f) : c;
		});
		uint[] plateCols = { 0xe6194b, 0x3cb44b, 0xffe119, 0x4363d8, 0xf58231, 0x911eb4, 0x46f0f0, 0xf032e6, 0xbcf60c, 0xfabebe, 0x008080, 0xe6beff, 0x9a6324, 0xaaffc3, 0x808000, 0xffd8b1, 0x000075, 0x800000, 0x469990, 0xdcbeff, 0xa9a9a9, 0xfffac8, 0x42d4f4, 0xbfef45, 0xf58231 };
		Godot.Color[] edgeCols = { default, new(1, 0, 0), new(1, 0.55f, 0), new(1, 1, 0), new(0, 1, 1), new(0.6f, 0.6f, 0.6f) };
		Save("plates", (x, z) =>
		{
			int i = z * N + x; if (Edge[i] > 0) return edgeCols[Edge[i]];
			var c = Rgb(plateCols[Plate[i] % plateCols.Length]); float s = Crust[i] ? 1f : 0.45f; return new Godot.Color(c.R * s, c.G * s, c.B * s);
		}, marks: true);
		// the tiny shapes over land and sea (each shape a little lighter or darker, with its outline), and the plates' edges on top:
		// coloured by kind as in the plates picture, dark grey where an edge is quiet
		Save("shapes", (x, z) =>
		{
			int i = z * N + x;
			bool plateEdge = (x + 1 < N && Plate[i + 1] != Plate[i]) || (z + 1 < N && Plate[i + N] != Plate[i]);
			if (plateEdge) return Edge[i] > 0 ? edgeCols[Edge[i]] : new Godot.Color(0.15f, 0.15f, 0.15f);
			var c = Sea[i] ? new Godot.Color(0.22f, 0.38f, 0.68f) : Lake[i] ? new Godot.Color(0.35f, 0.55f, 0.85f) : new Godot.Color(0.58f, 0.7f, 0.42f);
			float f = 0.82f + 0.36f * (float)Hash(Shape[i], 0, 5703);
			bool shapeEdge = (x + 1 < N && Shape[i + 1] != Shape[i]) || (z + 1 < N && Shape[i + N] != Shape[i]);
			if (shapeEdge) f *= 0.72f;
			return new Godot.Color(Math.Min(1, c.R * f), Math.Min(1, c.G * f), Math.Min(1, c.B * f));
		}, marks: false);
		// the colours' names, for the sheets: code, name, colour, what it belongs to
		var sb = new System.Text.StringBuilder();
		for (int k = 0; k < BiomeNames.Length; k++) sb.Append($"{k}\t{BiomeNames[k].Replace('_', ' ')}\t{BiomeColors[k]:x6}\tbiome\n");
		for (int k = 0; k < SubNames.Length; k++) sb.Append($"{FirstSub + k}\t{SubNames[k]}\t{SubColors[k]:x6}\t{SubParent[k]}\n");
		System.IO.File.WriteAllText($"{dir}/legend.txt", sb.ToString());
	}
	// (test) the sub-biomes block by block over a square of the world (x0, z0, size blocks), rivers drawn over them
	public void SaveSubZoom(string path, int x0, int z0, int size)
	{
		Godot.DirAccess.MakeDirRecursiveAbsolute(System.IO.Path.GetDirectoryName(path));
		var img = Godot.Image.CreateEmpty(size, size, false, Godot.Image.Format.Rgb8);
		for (int z = 0; z < size; z++) for (int x = 0; x < size; x++)
		{
			int bx =Math.Min(Size - 1, x0 + x), bz = Math.Min(Size - 1, z0 + z), s = SubAt(bx, bz), i = Math.Min(N - 1, bz / Cell) * N + Math.Min(N - 1, bx / Cell);
			uint c = River(i) ? (Temp[i] < -1 ? 0xd8f0ffu : 0x2659f2u) : s >= FirstSub ? SubColors[s - FirstSub] : BiomeColors[s];
			img.SetPixel(x, z, new Godot.Color(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f));
		}
		img.SavePng(path);
	}
}
