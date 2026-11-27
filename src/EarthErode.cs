using System;
using System.Collections.Generic;

namespace Cubeland;

// Earthlike worlds (EROSION DRAFT, the user's ask): before anyone plays, the plan wears the land down as ages of weather would
// have: rain and rivers cut valleys and lay the waste down as fans, floodplains and deltas; the ice of the last cold age cuts
// U-shaped troughs (fjords where they reach the sea) and leaves moraines; slopes round off and over-steep ones slump into
// scree; limestone dissolves; wind blows dust from the deserts and the ice's edge into loess; waves cut back soft coasts.
// Hard rock wears slowly and soft rock fast (the rock at the surface, from the geology), so hard beds stand up as ridges,
// mesas and tors. All on the plan grid, with only + - * / sqrt floor and the plan's hash and noise, like the rest of the plan.
// The beds stay where the plates put them (the geology is worked out from the land before erosion, Elev0), so cutting down
// through them shows deeper beds. Rivers still always run downhill: the water is worked out afresh on the worn land after.
public sealed partial class EarthPlan
{
	public static bool NoErode;   // (testing: plans without erosion, for before-and-after pictures)
	public double[] Elev0, Temp0, Rain0;   // the land as the plates made it, and the climate it wore down under
	// what erosion left, for the sub-biomes and the ground (blocks unless said): Wear: taken off (+) or laid down (-);
	// IceWas: the old ice's flow (0: never under ice); Trough: how much a glacier's trough widening took off; Moraine, Alluvium,
	// Loess: how thick each is; Karst 0-1; Dune 0-1 (a sand sea's sand); Scree: slumped rock; Coast: 1 waves cut the coast
	// back here (a platform under the sea), 2 a sea cliff; Fjord: a glacier trough the sea came into; Head: a cirque
	public double[] Wear, IceWas, Trough, Moraine, Alluvium, Loess, Karst, Dune, ScreeT, SurfK;
	public static double LoessShare = 0.10;   // (the most of the land loess may cover, a block or more thick)
	public byte[] Coast; public bool[] Fjord, Head;
	public const int ErodeRounds = 5;
	public static double KRiver = 6, KIce = 6;   // (testing: how hard water and ice cut)
	// how hard each rock is to wear away (1: shale; lower is harder)
	public static double Erodibility(Rock r) => r switch
	{
		Rock.Shale => 1.0, Rock.Siltstone => 0.85, Rock.Chalk => 0.9, Rock.Travertine => 0.8, Rock.Laterite => 1.2, Rock.Bauxite => 1.0,
		Rock.Limestone => 0.5, Rock.Dolomite => 0.4, Rock.Sandstone => 0.45, Rock.Conglomerate => 0.35, Rock.Slate => 0.4, Rock.Phyllite => 0.5,
		Rock.Schist => 0.4, Rock.Serpentinite => 0.6, Rock.Marble => 0.35, Rock.Andesite => 0.3, Rock.Rhyolite => 0.25, Rock.Basalt => 0.2,
		Rock.Gabbro => 0.2, Rock.Diorite => 0.15, Rock.Granite => 0.15, Rock.Gneiss => 0.2, Rock.Quartzite => 0.1, Rock.Hornfels => 0.12,
		Rock.Peridotite => 0.25, Rock.Silt or Rock.Loess or Rock.GlacialTill or Rock.Gravel or Rock.Sand => 1.3, _ => 0.4
	};
	static bool Dissolves(Rock r) => r == Rock.Limestone || r == Rock.Dolomite || r == Rock.Chalk || r == Rock.Marble || r == Rock.Travertine;

	// the drainage on some heights: flooding in from the map's edge, lowest first (as MakeWater does), each height nudged by a
	// slight smooth unevenness (rn, up to about 0.15 x Amp either way) so the ways across flat ground wander instead of running
	// in straight lines
	double[] rn;
	void Route(double[] h, double[] lvl, int[] down, int[] order)
	{
		int NN = N * N; var seen = new bool[NN]; var heap = new Heap(NN); int n = 0;
		for (int i = 0; i < NN; i++) down[i] = -1;
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
			if (x == 0 || z == 0 || x == N - 1 || z == N - 1) { int i = z * N + x; seen[i] = true; lvl[i] = h[i] + rn[i]; heap.Push(lvl[i], i); }
		while (heap.Count > 0)
		{
			int c = heap.Pop(); order[n++] = c;
			int cx = c % N, cz = c / N;
			for (int k = 0; k < 8; k++)
			{
				int x = cx + DX[k], z = cz + DZ[k];
				if (x < 0 || z < 0 || x >= N || z >= N) continue;
				int j = z * N + x;
				if (seen[j]) continue;
				seen[j] = true; lvl[j] = Math.Max(h[j] + rn[j], lvl[c]); down[j] = c;
				heap.Push(lvl[j], j);
			}
		}
	}

	void Erode()
	{
		int NN = N * N; double C = Cell, A = Amp;
		double sk = Tall / Belt;   // (Draft 2j: a slope in a 2048 world, in this one: heights x Tall, distances x Belt)
		var h = (double[])Elev.Clone();
		Wear = new double[NN]; IceWas = new double[NN]; Trough = new double[NN]; Moraine = new double[NN]; Alluvium = new double[NN];
		Loess = new double[NN]; Karst = new double[NN]; Dune = new double[NN]; ScreeT = new double[NN]; SurfK = new double[NN];
		Coast = new byte[NN]; Fjord = new bool[NN]; Head = new bool[NN];
		if (NoErode) { Wear = null; return; }   // (testing: no erosion; the fields stay empty and nothing reads them as erosion)
		var lvl = new double[NN]; var down = new int[NN]; var order = new int[NN];
		var q = new double[NN]; var qi = new double[NN]; var iceIn = new double[NN]; var kr = new double[NN];
		var ef = new double[NN]; var eg = new double[NN]; var qs = new double[NN]; var qg = new double[NN]; var dh = new double[NN];
		var dis = new bool[NN];
		rn = new double[NN]; for (int i = 0; i < NN; i++) rn[i] = 0.3 * A * (ValueNoise((i % N) * C / 24.0, (i / N) * C / 24.0, 7131) - 0.5);
		// each cell's rock column (the parts that don't change with height), for the rock at the surface as it wears down
		var cols = new GeoCol[NN]; var hasCol = new bool[NN];
		for (int i = 0; i < NN; i++) if (Elev0[i] > -30 * A) { cols[i] = ColAt((i % N) * Cell + Cell / 2, (i / N) * Cell + Cell / 2); hasCol[i] = true; }
		double fjordMax = 30 * A;   // (how far below the sea a glacier can cut)
		// small islands are kept: on a landmass smaller than about 250 x 250 blocks (as the land was before erosion) nothing goes
		// under the sea, each cell keeping at least half its height (and the waves don't cut its coast back)
		var keep = new bool[NN];
		{
			var lab = new int[NN]; var stack = new Stack<int>(); var cells = new List<int>(); int next = 0;
			double most = 62500.0 * Belt * Belt / (C * C);   // (Draft 2j: a share of the map, 250 x 250 blocks in a 2048 world)
			for (int s0 = 0; s0 < NN; s0++)
			{
				if (Elev0[s0] <= 0 || lab[s0] != 0) continue;
				lab[s0] = ++next; stack.Push(s0); cells.Clear();
				while (stack.Count > 0)
				{
					int c = stack.Pop(); cells.Add(c); int cx = c % N, cz = c / N;
					for (int dz = -1; dz <= 1; dz++)
						for (int dx = -1; dx <= 1; dx++)
						{
							int x = cx + dx, z = cz + dz; if (x < 0 || z < 0 || x >= N || z >= N) continue;
							int j = z * N + x; if (Elev0[j] > 0 && lab[j] == 0) { lab[j] = next; stack.Push(j); }
						}
				}
				if (cells.Count < most) foreach (int c in cells) keep[c] = true;
			}
		}
		// (Draft 2h, the same layout at every size: land on a continent more than 1.5 cells (KeepCells) from its coast, or where two
		// continents joined, doesn't go under the sea, and the waves don't cut it back: in a small world the waves' 30 blocks are many
		// cells and could cut a continent's neck through. Glacier troughs still may: fjords. And the open sea more than 1.5 cells
		// from a continent isn't filled into land (a small world's deltas could close a strait))
		var stay = new bool[NN]; var open = new bool[NN];
		for (int i = 0; i < NN; i++)
		{
			stay[i] = Crust[i] && Elev0[i] > 0 && (GOceanDist[i] > KeepCells || GJoinDist[i] <= 1.5);
			open[i] = !Crust[i] && Elev0[i] <= 0 && (GContDist[i] > KeepCells || YoungSea[i]);   // (Draft 2l: and the young sea, however narrow)
		}
		for (int round = 0; round < ErodeRounds; round++)
		{
			Route(h, lvl, down, order);
			// water: the rain added up downhill (land only); ice: in the last cold age (7 degrees colder, and warmer again as the land
			// wears lower), snow piles up where it's below freezing and melts where it's above, flowing down the same ways
			for (int i = 0; i < NN; i++) { q[i] = lvl[i] > 0 ? Rain0[i] : 0; qi[i] = 0; iceIn[i] = 0; }
			for (int k = NN - 1; k >= 0; k--)
			{
				int i = order[k], r = down[i];
				if (lvl[i] <= 0) continue;
				double ti = Temp0[i] - 7 + Lapse / A * Math.Max(0, Elev0[i] - Math.Max(0, h[i]));
				iceIn[i] = qi[i];
				if (ti < 0) qi[i] += 0.02 + Rain0[i] * Math.Min(1, -ti / 4); else qi[i] = Math.Max(0, qi[i] - 0.35 * ti);
				if (r >= 0 && lvl[r] > 0) { q[r] += q[i]; qi[r] += qi[i]; }
				if (qi[i] > IceWas[i]) IceWas[i] = qi[i];
			}
			// the rock at the surface (under the soil), and how easily it wears; laid-down waste wears easily
			for (int i = 0; i < NN; i++)
			{
				if (!hasCol[i] || lvl[i] <= 0) { kr[i] = 0.5; dis[i] = false; continue; }
				int bx = (i % N) * Cell + Cell / 2, bz = (i / N) * Cell + Cell / 2;
				var rock = Deep(cols[i], bx, (int)Math.Floor(Room + h[i]) - 3, bz, false);
				kr[i] = Alluvium[i] + Loess[i] + Moraine[i] > 2 ? 1.3 : Erodibility(rock); dis[i] = Dissolves(rock);
			}
			// cutting down, receivers first (so each cell is worked out against its receiver's new height): rivers by how much water
			// runs through and how steep, glaciers by how much ice; the lower of the two (stream power, solved implicitly so it's
			// steady for any step)
			for (int k = 0; k < NN; k++)
			{
				int i = order[k], r = down[i]; ef[i] = 0; eg[i] = 0;
				if (r < 0 || lvl[i] <= 0) continue;
				double d = (i % N != r % N && i / N != r / N) ? C * SQ2 : C, hi = h[i];
				// (a river cuts down to the sea's level at most, and always keeps a slight fall, 3 blocks in 1000, so it never cuts
				// inland channels the sea could come into)
				double baseR = (lvl[r] <= 0 ? Math.Max(h[r], 0) : h[r]) + d * 0.003 * sk;
				double F = KRiver * kr[i] * Math.Sqrt(q[i] * C * C / 2500) / d;
				double hf = hi > baseR ? (hi + F * baseR) / (1 + F) : hi, hg = hi;
				if (qi[i] > 0)
				{
					double baseG = lvl[r] <= 0 ? Math.Max(h[r], -fjordMax) : h[r];
					double Fg = KIce * (0.4 + 0.6 * Math.Min(1, kr[i])) * Math.Sqrt(qi[i] * C * C / 2500) / d;
					if (hi > baseG) hg = (hi + Fg * baseG) / (1 + Fg);
				}
				double hn = Math.Min(hf, hg);
				ef[i] = hf <= hg ? hi - hn : 0; eg[i] = hi - hn - ef[i];   // (which wore it: water or ice)
				h[i] = hn; Trough[i] += eg[i];   // (Trough: how much the ice took away, down and to the sides)
			}
			// a glacier's trough: the ice wears its valley wide as well as deep, so the floor is flat and the walls steep (a U):
			// beside each glacier cell, out to the ice's width, the land is brought down toward its floor, less further out
			for (int i = 0; i < NN; i++) dh[i] = 0;
			for (int i = 0; i < NN; i++)
			{
				if (eg[i] <= 0.2 || lvl[i] <= 0) continue;
				double wb = 12 * Belt * Math.Sqrt(Math.Sqrt(qi[i] * C * C / (2500 * Belt * Belt))), w = Math.Min(10, wb / C);
				if (w < 1) continue;
				int R = (int)Math.Floor(w), cx = i % N, cz = i / N; double fl = h[i];
				for (int dz = -R; dz <= R; dz++) for (int dx = -R; dx <= R; dx++)
				{
					int x = cx + dx, z = cz + dz; if (x < 0 || z < 0 || x >= N || z >= N) continue;
					int j = z * N + x; double t2 = (dx * dx + dz * dz) / (w * w);
					if (t2 >= 1 || h[j] <= fl) continue;
					double cut = (h[j] - fl) * (1 - t2) * 0.85;
					if (cut > dh[j]) dh[j] = cut;
				}
			}
			for (int i = 0; i < NN; i++) if (dh[i] > 0) { h[i] -= dh[i]; Trough[i] += dh[i]; eg[i] += dh[i]; }
			// slopes: soil creeps (the land rounds off: most on old worn cores, more on soft rock), and slopes steeper than the rock
			// can stand slump into scree at their foot (hard rock stands steeper)
			for (int i = 0; i < NN; i++) dh[i] = 0;
			for (int z = 1; z < N - 1; z++) for (int x = 1; x < N - 1; x++)
			{
				int i = z * N + x; if (h[i] <= 0) continue;
				double sum = 0; int nl4 = 0;   // (creep only between land cells: the coast isn't pulled into the sea)
				for (int k = 0; k < 4; k++) { int j = (z + DZ[k]) * N + x + DX[k]; if (h[j] > 0) { sum += h[j]; nl4++; } }
				double c = (0.04 + 0.16 * Craton[i]) * (0.6 + 0.4 * Math.Min(1, kr[i]));
				if (nl4 > 0) dh[i] += c * (sum / nl4 - h[i]);
				double smax = (0.6 + 2.4 * (1 - Math.Min(1, kr[i]))) * C;   // (what the rock can stand: a real slope, blocks up per block across)   // (blocks up per cell across)
				for (int k = 0; k < 4; k++)
				{
					int j = (z + DZ[k]) * N + x + DX[k];
					double drop = h[i] - h[j];
					if (drop > smax) { double m = (drop - smax) * 0.25; dh[i] -= m; dh[j] += m; ScreeT[j] += m; }
				}
			}
			for (int i = 0; i < NN; i++) h[i] += dh[i];
			// the waste carried down, from the top: a river drops what it can't carry where it slows (fans at the foot of the hills,
			// floodplains along big rivers, filling lakes), never so much that it would stop running down; at the sea, a delta.
			// What the ice carried is dropped where the ice ends, as a moraine, and some goes on as the meltwater's sand
			for (int i = 0; i < NN; i++) { qs[i] = 0; qg[i] = 0; }
			for (int k = NN - 1; k >= 0; k--)
			{
				int i = order[k], r = down[i];
				if (lvl[i] <= 0 || r < 0) continue;
				qs[i] += ef[i]; qg[i] += eg[i];
				double d = (i % N != r % N && i / N != r / N) ? C * SQ2 : C, a = Math.Sqrt(q[i] * C * C / 2500);
				double S = Math.Max(0, h[i] - h[r]) / d, cap = 60 * a * S;
				if (qs[i] > cap)
				{
					double sdep = Math.Min(0.03, 0.08 / Math.Max(1, a / Belt)) * sk, room = lvl[i] > h[i] + 0.01 ? lvl[i] - h[i] : Math.Max(0, h[r] + d * sdep - h[i]);
					double dep = Math.Min(0.5 * (qs[i] - cap), room);
					if (dep > 0) { h[i] += dep; qs[i] -= dep; Alluvium[i] += dep; }
				}
				if (qg[i] > 0 && qi[i] <= 0 && iceIn[i] > 0)
				{   // (the ice ends here: a moraine across the valley)
					double m = Math.Min(6 * A, 0.5 * qg[i]);
					int cx = i % N, cz = i / N;
					for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
					{
						int x = cx + dx, z = cz + dz; if (x < 0 || z < 0 || x >= N || z >= N) continue;
						int j = z * N + x; if (lvl[j] <= 0) continue;
						double mm = dx == 0 && dz == 0 ? m : m * 0.6; h[j] += mm; Moraine[j] += mm;
					}
					qs[i] += 0.5 * (qg[i] - m); qg[i] = 0;
				}
				if (lvl[r] <= 0)
				{   // (a river mouth: a delta built out into the sea, nearest cells first, up to just above the water)
					double vol = 0.35 * qs[i]; int cx = r % N, cz = r / N;
					for (int ring = 0; ring <= 3 && vol > 0.01; ring++)
						for (int dz = -ring; dz <= ring && vol > 0.01; dz++) for (int dx = -ring; dx <= ring && vol > 0.01; dx++)
						{
							if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
							int x = cx + dx, z = cz + dz; if (x < 0 || z < 0 || x >= N || z >= N) continue;
							int j = z * N + x; if (h[j] >= 0.3) continue;
							double add = Math.Min(vol, 0.3 - h[j]); h[j] += add; vol -= add; Alluvium[j] += add;
						}
					continue;
				}
				qs[r] += qs[i]; qg[r] += qg[i];
			}
			// limestone and its kin dissolve in rain, most in warm wet places (the land lowers; the sinkholes, caves and towers are
			// worked out block by block later)
			for (int i = 0; i < NN; i++)
				if (dis[i] && lvl[i] > 0 && h[i] > 1)
				{
					double kk = Rain0[i] * (0.3 + Smoothstep(5, 25, Temp0[i]));
					h[i] = Math.Max(1, h[i] - 1.5 * A * kk / ErodeRounds * 3); Karst[i] = Math.Max(Karst[i], Math.Min(1, kk));
				}
			for (int i = 0; i < NN; i++)
			{
				SurfK[i] = kr[i]; if (keep[i]) h[i] = Math.Max(h[i], Math.Min(Elev0[i], Math.Max(1, 0.5 * Elev0[i])));
				if (stay[i] && IceWas[i] < 0.3) h[i] = Math.Max(h[i], Math.Min(Elev0[i], 1));
				if (open[i]) h[i] = Math.Min(h[i], Math.Max(Elev0[i], -1));
			}
		}
		// wind: dust from the dry lands and from the old ice's outwash, carried by the year's wind (WX, WZ, after the land has
		// turned it) the way the air is: four sweeps a round, two rounds, each cell taking the dust of the cells upwind of it.
		// It settles as loess where grass holds it (rain 0.25 up); the driest lands keep their sand as sand seas
		{
			// (what's left after crossing a cell, block by block: 1 / 1.004^C in the air, 1 / 1.02^C over the sea or grass)
			double kAir = 1, kWet = 1; for (int b = 0; b < (int)C; b++) { kAir /= 1.004; kWet /= 1.02; }
			var dust = new double[NN]; var src = new double[NN];
			for (int i = 0; i < NN; i++)
			{
				if (h[i] <= 0) continue;
				double dry = Math.Max(0, 0.2 - Rain0[i]) / 0.2;
				// (the old ice's outwash: the silt its meltwater left on the plains in front of it)
				double outwash = IceWas[i] >= 0.3 && (Moraine[i] > 0 || Alluvium[i] >= 1) ? 1 : 0;
				src[i] = (dry * (0.3 + 0.7 * Math.Min(1, SurfK[i])) + outwash) * 2.6 * C * A / 2.5;
				Dune[i] = Rain0[i] < 0.12 ? Math.Min(1, dry * (0.4 + 0.6 * Math.Min(1, SurfK[i])) + Alluvium[i] / (4 * A)) : 0;
			}
			for (int round = 0; round < 2; round++)
				for (int o = 0; o < 4; o++)
				{
					bool xr = (o & 1) == 1, zr = (o & 2) == 2;
					for (int zs = 0; zs < N; zs++)
					{
						int z = zr ? N - 1 - zs : zs;
						for (int xs = 0; xs < N; xs++)
						{
							int x = xr ? N - 1 - xs : xs, i = z * N + x;
							double wx = WX[i], wz = WZ[i], awx = Math.Abs(wx), awz = Math.Abs(wz), sum = awx + awz;
							double ax = sum > 1e-9 ? awx / sum : 0.5, az = 1 - ax;
							int ux = x - (wx >= 0 ? 1 : -1), uz = z - (wz >= 0 ? 1 : -1);
							double dIn = (ux < 0 || ux >= N ? 0 : ax * dust[z * N + ux]) + (uz < 0 || uz >= N ? 0 : az * dust[uz * N + x]);
							if (h[i] <= 0) { dust[i] = dIn * kWet; continue; }
							double d = dIn * kAir + src[i];
							if (Rain0[i] >= 0.25) { double dep = d * (1 - kWet); Loess[i] = dep; d -= dep; }
							dust[i] = d;
						}
					}
				}
		}
		// (the dust spreads sideways too: smoothed over about 30 blocks, so it lies in sheets rather than streaks along the wind;
		// none on the sea or the dry lands)
		{ int rl = Math.Max(1, (int)Math.Floor(32.0 / C + 0.5)); BoxBlur(Loess, rl); BoxBlur(Loess, rl); }
		for (int i = 0; i < NN; i++) if (h[i] <= 0 || Rain0[i] < 0.2) Loess[i] = 0;
		// (the user: loess on about 10% of the land, Earth's share, at most. Where more of the land would have a block or more of
		// it, only the thickest LoessShare keeps that much: everything is thinned by the same amount, (the thickness at that
		// share) - 1, so the sheets keep their shape and the thinnest edges go)
		{
			var lv = new List<double>(); for (int i = 0; i < NN; i++) if (h[i] > 0) lv.Add(Loess[i]);
			lv.Sort();
			int k = lv.Count - 1 - (int)Math.Floor(LoessShare * lv.Count);   // (the thickness at that share, counted from the thickest)
			double thin = k >= 0 && k < lv.Count ? lv[k] - 1 : 0;
			if (thin > 0) for (int i = 0; i < NN; i++) Loess[i] = Math.Max(0, Loess[i] - thin);
		}
		for (int i = 0; i < NN; i++) { Loess[i] = Math.Min(8 * A, Loess[i]); if (h[i] > 0) h[i] += Loess[i]; }
		// waves: exposed coasts of soft rock are cut back (a wave-cut platform just under the sea, the cliff behind); hard rock stands
		// as headlands. Up to about 30 blocks back, a cell at a time
		int reach = Math.Max(2, (int)Math.Floor(80.0 / C)), passes = (int)Math.Floor(30.0 / C);
		var expo = new double[NN];
		for (int pass = 0; pass <= passes; pass++)
		{
			for (int i = 0; i < NN; i++)
			{
				expo[i] = 0; if (h[i] <= 0) continue;
				int cx = i % N, cz = i / N; bool shore = false;
				for (int k = 0; k < 8 && !shore; k++) { int x = cx + DX[k], z = cz + DZ[k]; if (x >= 0 && z >= 0 && x < N && z < N && h[z * N + x] <= 0) shore = true; }
				if (!shore) continue;
				int sea = 0, all = 0;
				for (int dz = -reach; dz <= reach; dz += 2) for (int dx = -reach; dx <= reach; dx += 2)
				{
					int x = cx + dx, z = cz + dz; if (x < 0 || z < 0 || x >= N || z >= N) continue;
					all++; if (h[z * N + x] <= 0) sea++;
				}
				expo[i] = (double)sea / all;
			}
			for (int i = 0; i < NN; i++)
			{
				if (expo[i] <= 0) continue;
				// (by a smooth noise along the coast, so whole stretches go back: bays between the headlands)
				if (pass < passes && !keep[i] && !stay[i] && SurfK[i] >= 0.5 && expo[i] * Math.Min(1, SurfK[i]) > 0.3 + 0.6 * ValueNoise((i % N) * C / 60.0, (i / N) * C / 60.0, 7120)) { h[i] = -1.5; Coast[i] = 1; }
				else if (pass == passes && expo[i] > 0.4 && h[i] > 3 * A) Coast[i] = 2;
			}
		}
		// (in worlds whose cells are wider than the waves cut back, a soft exposed coast's cells are left and the sea cells
		// in front of it are marked as the platform, worked out block by block later)
		if (passes == 0)
			for (int z = 1; z < N - 1; z++) for (int x = 1; x < N - 1; x++)
			{
				int i = z * N + x; if (expo[i] <= 0.4 || keep[i] || SurfK[i] < 0.5) continue;
				for (int k = 0; k < 4; k++) { int j = (z + DZ[k]) * N + x + DX[k]; if (h[j] <= 0 && h[j] > -6) Coast[j] = 1; }
			}
		// glacier troughs the sea came into are fjords; a cirque is where a glacier began high on a steep slope
		for (int i = 0; i < NN; i++)
		{
			if (h[i] <= 0 && Elev0[i] > 0 && IceWas[i] >= 0.3 && Trough[i] + Elev0[i] - h[i] > 4 * A) Fjord[i] = true;
			if (h[i] > High * 0.6 && IceWas[i] >= 0.3 && IceWas[i] < 1.5 && Elev0[i] - h[i] > 6 * A) Head[i] = true;
			Wear[i] = Elev0[i] - h[i];
			Elev[i] = h[i];
		}
	}
}
