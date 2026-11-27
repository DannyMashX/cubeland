using System;
using System.Collections.Generic;
using System.Linq;

namespace Cubeland;

// Earthlike worlds, Draft 2j: the plates' history runs FORWARD (the user: "The world should start as a super continent and
// get shifted from there"). Every world starts as one supercontinent, 40% of the map, made of the continents and big islands
// to come (each of its own old blocks). It splits along rifts into those pieces; partners stay joined; every other piece
// drifts out, away from the supercontinent's middle (turned a little by hash), until the open sea around it is wide or it
// reaches the world's edge sea. Today's map is where the drift ends. Today's plates are then laid on the drifting pieces,
// moving their way, so:
//   - the oceans that opened behind them spread from ridges, and their coasts there match (RIFT-BORN coasts: quiet, with wide
//     shelves and thick beds);
//   - their leading coasts push over the ocean floor ahead (young coastal ranges and trenches);
//   - partners' joins are the welds of the supercontinent (middle-aged ranges); the old blocks' sutures inside each piece are
//     older still (worn low);
//   - in YoungSeaChance of worlds one partner pair has only just begun to part: a narrow young sea with matching coasts;
//   - each hot spot leaves a trail of volcanoes along the path of the plate over it, older and more worn further away.
// The drift runs on its own grid, 128 x 128 at every size, in shares of the map, so a seed has the same history and the same
// map at every size.
public sealed partial class EarthPlan
{
	public const int HistSteps = 4;      // ages: 4 the supercontinent ... 0 now
	public const int HG = 128;           // the history's grid: cells across the map, at every size
	public static double YoungSeaChance = 0.6, RiftReach = 0.07, DriftGap = 0.12, DriftEdge = 0.03, DriftTurn = 0.5, GroupMax = 0.6, SuperEdge = 0.1, DriftReach = 1;
	public const int LloydRounds = 3;
	public static int SpreadPasses = 8, SpreadR = 12;
	public string ContGapNote = "";
	public static double SuperLeg = 0.2, SuperSpan = 0.7, SuperBay = 0.14, LobeDeep = 0.6;   // (Draft 2k: leg 0.2-0.32 of the map, the spine fits 0.7 of it, the bay, lobes 0.4-1.6)
	public string SuperShapeNote = "";
	readonly List<int> ForcedJoin = new();   // (Draft 2k: piece pairs a x NC + b joined for want of room to drift apart)
	public static double JoinRoom = 2;       // (Draft 2k: less sea than this (history cells) round a group's best place: no room)
	public static double SuperWide = 0.11;   // (Draft 2k: how far from its spine the supercontinent scores half (share of the map))
	public static double LandShare = 0.4;   // (the supercontinent's share of the map: 40% land)   // (testing can change them)
	public int[] Terrane;                // each continental cell's old block (-1 at sea)
	public int Terranes; public int[] TerraneCont;
	public double[] SutureDist;          // how far each cell is from an old suture (cells)
	public double[] RiftBorn;            // 0-1: near a coast born where the supercontinent split (land and shelf)
	public bool[] YoungSea;              // cells of continent that the young sea took
	public int Groups; public int[] GroupOf;   // each continent's or big island's group (partners drift together)
	public double[] GDirX, GDirZ;        // each group's way
	public double[] GSpin, GCenX, GCenZ; // (Draft 2k) each group's turn (tan of half its whole turn; + turns east toward south) and the middle it turns about (history cells)
	public double[] HSpinC, HSpinS;      // [age x Groups + g]: each group's turn at each age (cos, sin)
	public static double SpinMin = 0.09, SpinMax = 0.41;   // (Draft 2k: tan of half the whole turn, about 10 to 45 degrees)
	public static double AwayRest = 1;
	public static bool DirFromDrift = false;   // (tested: plates moving the way each group really went; no better for the young ranges, so off)
	public static double[] SpinTry = { 1, -1, 0.5, -0.5, 1.5, -1.5, 2, -2 };   // (Draft 2k: the turns the drift may take: the hashed turn, the other way, half, 1.5 and twice each)
	public int[] HOffX, HOffZ;           // [age x Groups + g]: each group's place at each age, as an offset from the supercontinent (history cells)
	public int[] PieceStartX, PieceStartZ;   // (plan cells) where each piece's offset is applied: its offset now, in plan cells
	int[] histCont0, histTer0;           // the supercontinent on the history grid
	public int YsA = -1, YsB = -1;       // the young sea's pair (YsB has a plate of its own)
	bool[] partner;                      // [a x NC + b]: partners (joined since the supercontinent)
	public int[] PiecePlate;             // each piece's own plate (set with the plates)
	public int DriftRounds;
	public readonly List<double[]> Trails = new();   // hot-spot trails: x, z (cells), age, the hot spot
	public double[] RangeYoung, RangeMid, RangeOld;   // how much each kind of range raised each cell (blocks)
	public string HistNote = "", DriftNote = "";

	// distances on the history grid (1 straight, sqrt 2 diagonal; 0 on the source)
	static double[] ChamferH(Func<int, bool> src)
	{
		var d = new double[HG * HG];
		for (int g = 0; g < HG * HG; g++) d[g] = src(g) ? 0 : 1e9;
		for (int z = 0; z < HG; z++) for (int x = 0; x < HG; x++)
		{
			int i = z * HG + x; double v = d[i];
			if (x > 0) v = Math.Min(v, d[i - 1] + 1); if (z > 0) v = Math.Min(v, d[i - HG] + 1);
			if (x > 0 && z > 0) v = Math.Min(v, d[i - HG - 1] + SQ2); if (x + 1 < HG && z > 0) v = Math.Min(v, d[i - HG + 1] + SQ2);
			d[i] = v;
		}
		for (int z = HG - 1; z >= 0; z--) for (int x = HG - 1; x >= 0; x--)
		{
			int i = z * HG + x; double v = d[i];
			if (x + 1 < HG) v = Math.Min(v, d[i + 1] + 1); if (z + 1 < HG) v = Math.Min(v, d[i + HG] + 1);
			if (x + 1 < HG && z + 1 < HG) v = Math.Min(v, d[i + HG + 1] + SQ2); if (x > 0 && z + 1 < HG) v = Math.Min(v, d[i + HG - 1] + SQ2);
			d[i] = v;
		}
		return d;
	}

	// The forward history, from MakePlates (after the tiny shapes and the continents' shares, before today's plates). Sets the
	// crust (ContId, Crust, Terrane), the groups and their ways, and the history's places at each age
	void MakeForward(double gs, int GX, int NS, double[] ptx, double[] ptz, int[] shapeCells, List<int>[] adj, int[] edgeN, double[] want)
	{
		int NN = N * N, NC = Continents + BigIslands;
		// ---- 1. THE SUPERCONTINENT (Draft 2k, the user: irregular, not a disc: lobes, bays, a long arm or a C like Pangaea). It
		// grows over the tiny shapes along a SPINE: a bent line of 3 legs, each 0.16-0.28 of the map long, turning 14-42 degrees at
		// each joint (tan 0.25-0.9). In 3 seeds of 5 every turn is the same way (a C or a hook), else back and forth (an S). In half
		// the seeds an ARM leaves joint 1 or 2 square to the spine, 0.14-0.26 of the map long. The spine's box is centred on the map
		// (give or take 0.06 of it by hash), shrunk to fit 0.5 of the map if it's bigger. Each shape scores by its distance from the
		// spine, times deep lobes of noise (bays and peninsulas); it stops at 40% of the map, its shapes' points SuperEdge of the map
		// in from the edge
		var spx = new List<double> { 0 }; var spz = new List<double> { 0 }; var segA = new List<int>(); var segB = new List<int>();
		double sux = 2 * Hash(0, 2, 5210) - 1, suz = 2 * Hash(0, 3, 5210) - 1, sul = Math.Sqrt(sux * sux + suz * suz);
		if (sul < 0.01) { sux = 1; suz = 0; sul = 1; }
		sux /= sul; suz /= sul;
		bool oneWay = Hash(0, 4, 5210) < 0.6; double sgn = Hash(0, 5, 5210) < 0.5 ? -1 : 1;
		for (int k = 0; k < 3; k++)
		{
			if (k > 0)
			{
				double tt = (0.25 + 0.65 * Hash(k, 6, 5210)) * (oneWay || k == 1 ? sgn : -sgn), vx = sux - tt * suz, vz = suz + tt * sux, vl = Math.Sqrt(vx * vx + vz * vz);
				sux = vx / vl; suz = vz / vl;
			}
			double L = (SuperLeg + 0.12 * Hash(k, 7, 5210)) * N;
			spx.Add(spx[k] + sux * L); spz.Add(spz[k] + suz * L); segA.Add(k); segB.Add(k + 1);
		}
		if (Hash(0, 8, 5210) < 0.5)
		{
			int j = 1 + (int)Math.Floor(2 * Hash(0, 9, 5210));
			double dx = spx[j + 1] - spx[j - 1], dz = spz[j + 1] - spz[j - 1], dl = Math.Sqrt(dx * dx + dz * dz), side = Hash(1, 9, 5210) < 0.5 ? -1 : 1, L = (0.14 + 0.12 * Hash(2, 9, 5210)) * N;
			spx.Add(spx[j] - side * dz / dl * L); spz.Add(spz[j] + side * dx / dl * L); segA.Add(j); segB.Add(spx.Count - 1);
		}
		double bx0 = 1e9, bx1 = -1e9, bz0 = 1e9, bz1 = -1e9;
		for (int k = 0; k < spx.Count; k++) { bx0 = Math.Min(bx0, spx[k]); bx1 = Math.Max(bx1, spx[k]); bz0 = Math.Min(bz0, spz[k]); bz1 = Math.Max(bz1, spz[k]); }
		double fit = Math.Min(1, SuperSpan * N / Math.Max(1e-9, Math.Max(bx1 - bx0, bz1 - bz0)));
		double cx = N * (0.5 + 0.06 * (2 * Hash(0, 0, 5210) - 1)), cz = N * (0.5 + 0.06 * (2 * Hash(0, 1, 5210) - 1));
		for (int k = 0; k < spx.Count; k++) { spx[k] = cx + (spx[k] - (bx0 + bx1) / 2) * fit; spz[k] = cz + (spz[k] - (bz0 + bz1) / 2) * fit; }
		double Wd = SuperWide * N;
		// (Draft 2k: a C has a BAY inside it opening to the ocean, like Pangaea's Tethys: from the middle of the spine's 4 points
		// straight out through the middle of its two ends and on, SuperBay of the map wide each side (no more than 0.8 of the
		// least distance from the spine at 9 points from the start to the ends' middle; no bay under 0.04, or if the ends' middle
		// is within 0.05 of the map of the start); a shape's score falls to 0 from the bay's edge to half way in)
		double bcx = 0, bcz = 0; for (int k = 0; k < 4; k++) { bcx += spx[k] / 4; bcz += spz[k] / 4; }
		double SegD2(double px, double pz, int k)
		{
			double ax0 = spx[segA[k]], az0 = spz[segA[k]], ex = spx[segB[k]] - ax0, ez = spz[segB[k]] - az0, ll = ex * ex + ez * ez;
			double tq = ll > 0 ? Math.Max(0, Math.Min(1, ((px - ax0) * ex + (pz - az0) * ez) / ll)) : 0;
			double qx = px - ax0 - tq * ex, qz = pz - az0 - tq * ez; return qx * qx + qz * qz;
		}
		double emx = (spx[0] + spx[3]) / 2, emz = (spz[0] + spz[3]) / 2, bayR = 0;
		if (oneWay)
		{
			double db = 1e18;
			for (int k2 = 0; k2 <= 8; k2++) { double px = bcx + (emx - bcx) * k2 / 8, pz = bcz + (emz - bcz) * k2 / 8; for (int k = 0; k < segA.Count; k++) db = Math.Min(db, SegD2(px, pz, k)); }
			bayR = Math.Min(SuperBay * N, 0.8 * Math.Sqrt(db)); if (bayR < 0.04 * N || (emx - bcx) * (emx - bcx) + (emz - bcz) * (emz - bcz) < 0.0025 * N * N) bayR = 0;
		}
		SuperShapeNote = $"spine {spx.Count - 1} lines, {(oneWay ? "a C or hook" : "an S")}{(segA.Count > 3 ? " with an arm" : "")}, fit {fit:0.00}, bay {(bayR > 0 ? $"{bayR / N:0.00} of the map" : "none")}";
		double SScore(int s)
		{
			double d2 = 1e18;
			for (int k = 0; k < segA.Count; k++)
			{
				double ax0 = spx[segA[k]], az0 = spz[segA[k]], ex = spx[segB[k]] - ax0, ez = spz[segB[k]] - az0, ll = ex * ex + ez * ez;
				double tq = ll > 0 ? Math.Max(0, Math.Min(1, ((ptx[s] - ax0) * ex + (ptz[s] - az0) * ez) / ll)) : 0;
				double qx = ptx[s] - ax0 - tq * ex, qz = ptz[s] - az0 - tq * ez; d2 = Math.Min(d2, qx * qx + qz * qz);
			}
			double lobe = 1 - LobeDeep + 2 * LobeDeep * Fbm(ptx[s] / (N * 0.12), ptz[s] / (N * 0.12), 3, 5211);
			if (bayR > 0)
			{
				double fx = emx - bcx, fz = emz - bcz, fl = fx * fx + fz * fz, ft = Math.Max(0, ((ptx[s] - bcx) * fx + (ptz[s] - bcz) * fz) / fl);
				double gx2 = ptx[s] - bcx - ft * fx, gz2 = ptz[s] - bcz - ft * fz; lobe *= Smoothstep(0.5 * bayR, bayR, Math.Sqrt(gx2 * gx2 + gz2 * gz2));
			}
			return lobe / (1 + 1.5 * d2 / (Wd * Wd));
		}
		var inSuper = new bool[NS]; double area = 0;
		{
			int s0 = Shape[Math.Min(N - 1, (int)Math.Floor(spz[1])) * N + Math.Min(N - 1, (int)Math.Floor(spx[1]))];   // (from the spine's first joint)
			var heap = new Heap(NS * 8); heap.Push(-SScore(s0), s0);
			while (area < LandShare * NN && heap.Count > 0)
			{
				int s = heap.Pop(); if (inSuper[s]) continue;
				if (shapeCells[s] == 0 || edgeN[s] * 4 >= shapeCells[s] || Math.Min(Math.Min(ptx[s], ptz[s]), Math.Min(N - ptx[s], N - ptz[s])) < SuperEdge * N) continue;
				inSuper[s] = true; area += shapeCells[s];
				foreach (int t in adj[s]) if (!inSuper[t]) heap.Push(-SScore(t), t);
			}
			for (int pass = 0; pass < 2; pass++)   // (a sea shape with the supercontinent all round it is part of it: twice, in shape order)
				for (int s = 0; s < NS; s++)
				{
					if (inSuper[s] || shapeCells[s] == 0 || adj[s].Count == 0) continue;
					bool all = true; foreach (int t in adj[s]) if (!inSuper[t]) { all = false; break; }
					if (all) { inSuper[s] = true; area += shapeCells[s]; }
				}
		}
		var superL = new List<int>(); for (int s = 0; s < NS; s++) if (inSuper[s]) superL.Add(s);
		{   // (test print)
			var dd = Chamfer(j => !inSuper[Shape[j]]); double sa = 0, sd = 0;
			for (int i = 0; i < NN; i++) if (inSuper[Shape[i]]) { sa++; sd += dd[i]; }
			SuperShapeNote += $"; thinness {sa / Math.Pow(Math.Max(1e-9, sd / Math.Max(1, sa)), 2):0} (a disc 28)";
		}
		// ---- 2. THE PIECES: the continents' and big islands' seeds spread over it (a continent: the farthest of 12 tries by
		// hash from those chosen, the first from the middle; a big island: on its rim, the farthest from all), then each grows
		// over it, the one furthest behind its share first, taking its best shape beside it (near its seed for its size, each
		// stretched its own way by hash, with its own lobes, give or take a hash) until the supercontinent is all shared out.
		// So the pieces are compact, the rifts between them follow the shapes' ragged outlines, and their shares follow the
		// ranks (one or two giants, some mid-size, small ones, big islands)
		var piece = new int[NS]; for (int s = 0; s < NS; s++) piece[s] = -1;
		var seedS = new int[NC];
		for (int c = 0; c < NC; c++) seedS[c] = -1;
		double SDist(int s, int t) { double dx = ptx[s] - ptx[t], dz = ptz[s] - ptz[t]; return Math.Sqrt(dx * dx + dz * dz); }
		for (int c = 0; c < Continents; c++)
		{
			int bs = -1; double bsc = -1;
			for (int t = 0; t < 12; t++)
			{
				int s = superL[Math.Min(superL.Count - 1, (int)Math.Floor(Hash(c * 16 + t, 2, 5003) * superL.Count))];
				bool used = false; for (int j = 0; j < c; j++) if (seedS[j] == s) used = true;
				if (used) continue;
				double far;
				if (c == 0) { double dx = ptx[s] - cx, dz = ptz[s] - cz; far = Math.Sqrt(dx * dx + dz * dz); }
				else { far = 1e18; for (int j = 0; j < c; j++) far = Math.Min(far, SDist(s, seedS[j])); }
				double sc = far * (0.6 + 0.4 * Hash(c * 16 + t, 3, 5003));
				if (sc > bsc) { bsc = sc; bs = s; }
			}
			if (bs < 0) foreach (int s in superL) { bool used = false; for (int j = 0; j < c; j++) if (seedS[j] == s) used = true; if (!used) { bs = s; break; } }
			seedS[c] = bs;
		}
		for (int c = Continents; c < NC; c++)
		{
			int bs = -1; double bd = -1;
			foreach (int s in superL)
			{
				bool rim = false; foreach (int t in adj[s]) if (!inSuper[t]) { rim = true; break; }
				if (!rim) continue;
				bool used = false; double dm = 1e18;
				for (int j = 0; j < c; j++) { if (seedS[j] == s) used = true; dm = Math.Min(dm, SDist(s, seedS[j])); }
				if (used) continue;
				dm *= 0.8 + 0.2 * Hash(s, c, 5214);
				if (dm > bd) { bd = dm; bs = s; }
			}
			if (bs < 0) foreach (int s in superL) { bool used = false; for (int j = 0; j < c; j++) if (seedS[j] == s) used = true; if (!used) { bs = s; break; } }
			seedS[c] = bs;
		}
		var pArea = new double[NC]; var sdx = new double[NC]; var sdz = new double[NC]; var squash = new double[NC]; var rad = new double[NC];
		double wantSum = 0; for (int c = 0; c < NC; c++) wantSum += want[c];
		for (int c = 0; c < NC; c++)
		{
			double a = 2 * Hash(c, 1, 5008) - 1, b = 2 * Hash(c, 2, 5008) - 1, l = Math.Sqrt(a * a + b * b);
			if (l < 0.01) { a = 1; b = 0; l = 1; }
			sdx[c] = a / l; sdz[c] = b / l; squash[c] = 0.45 + 0.4 * Hash(c, 3, 5008);   // (the long way costs 0.45-0.85 of the short way)
			rad[c] = Math.Sqrt(area * want[c] / wantSum / Math.PI);
		}
		double PScore(int s, int c)
		{
			int s0 = seedS[c];
			double ex = ptx[s] - ptx[s0], ez = ptz[s] - ptz[s0], a1 = ex * sdx[c] + ez * sdz[c], a2 = ez * sdx[c] - ex * sdz[c];
			double lobe = 0.55 + 0.9 * Fbm(ptx[s] / (N * 0.12) + 7.3 * c, ptz[s] / (N * 0.12), 3, 5010);
			double r2 = (a1 * a1 * squash[c] + a2 * a2) / (rad[c] * rad[c]) * lobe;
			return (0.85 + 0.3 * Hash(s, c, 5006)) / (1 + 1.5 * r2);
		}
		var heaps = new Heap[NC];
		void Take(int s, int c)
		{
			piece[s] = c; pArea[c] += shapeCells[s];
			foreach (int t in adj[s]) if (inSuper[t] && piece[t] < 0) heaps[c].Push(-PScore(t, c), t);
		}
		// (shared out LloydRounds + 1 times: after each but the last, each continent's seed moves to its own shape nearest its
		// middle, so the pieces come out compact, none wrapped round another; the big islands keep their seeds on the rim)
		for (int it = 0; it <= LloydRounds; it++)
		{
			for (int s = 0; s < NS; s++) piece[s] = -1;
			for (int c = 0; c < NC; c++) { heaps[c] = new Heap(NS * 8); pArea[c] = 0; }
			for (int c = 0; c < NC; c++) if (seedS[c] >= 0 && piece[seedS[c]] < 0) Take(seedS[c], c);
			while (true)
			{
				int bc = -1;
				for (int c = 0; c < NC; c++) if (heaps[c].Count > 0 && (bc < 0 || pArea[c] / want[c] < pArea[bc] / want[bc])) bc = c;
				if (bc < 0) break;
				int s = heaps[bc].Pop();
				if (piece[s] >= 0) continue;
				Take(s, bc);
			}
			if (it == LloydRounds) break;
			for (int c = 0; c < Continents; c++)
			{
				double sx = 0, sz = 0, sn = 0;
				foreach (int s in superL) if (piece[s] == c) { sx += ptx[s] * shapeCells[s]; sz += ptz[s] * shapeCells[s]; sn += shapeCells[s]; }
				if (sn == 0) continue;
				sx /= sn; sz /= sn; int best = seedS[c]; double bd = 1e18;
				foreach (int s in superL) { if (piece[s] != c) continue; double dx = ptx[s] - sx, dz = ptz[s] - sz, dd = dx * dx + dz * dz; if (dd < bd) { bd = dd; best = s; } }
				seedS[c] = best;
			}
		}
		// (the supercontinent's start, in plan cells)
		var cont0 = new int[NN];
		for (int i = 0; i < NN; i++) cont0[i] = piece[Shape[i]];
		// ---- 3. THE OLD BLOCKS: a piece is 1 to 4 of them (3 over 30% of the crust, 2 over 12%, and one more for about 1 in 3
		// by hash; a big island one). Their middles are spread out over the piece (the shape farthest from its middle, then each
		// the shape farthest from those chosen), and each of its shapes joins the nearest, give or take 15% by hash, so the old
		// sutures between them follow the shapes' outlines
		var shT = new int[NS]; for (int s = 0; s < NS; s++) shT[s] = -1;
		var terC = new List<int>(); var seeds = new List<int>();
		{
			var pX = new double[NC]; var pZ = new double[NC]; var pN = new double[NC];
			foreach (int s in superL) { int c = piece[s]; if (c < 0) continue; pX[c] += ptx[s] * shapeCells[s]; pZ[c] += ptz[s] * shapeCells[s]; pN[c] += shapeCells[s]; }
			for (int c = 0; c < NC; c++)
			{
				if (pN[c] == 0) continue;
				pX[c] /= pN[c]; pZ[c] /= pN[c];
				double share = pN[c] / Math.Max(1, area);
				int nT = c >= Continents ? 1 : (share > 0.3 ? 3 : share > 0.12 ? 2 : 1) + (Hash(c, 0, 5200) < 0.35 ? 1 : 0);
				seeds.Clear();
				for (int k = 0; k < nT; k++)
				{
					int best = -1; double bd = -1;
					foreach (int s in superL)
					{
						if (piece[s] != c || seeds.Contains(s)) continue;
						double dm;
						if (k == 0) { double dx = ptx[s] - pX[c], dz = ptz[s] - pZ[c]; dm = dx * dx + dz * dz; }
						else { dm = 1e18; foreach (int q in seeds) { double dx = ptx[s] - ptx[q], dz = ptz[s] - ptz[q]; dm = Math.Min(dm, dx * dx + dz * dz); } }
						if (dm > bd) { bd = dm; best = s; }
					}
					if (best < 0) break;
					seeds.Add(best);
				}
				int t0 = terC.Count; foreach (int q in seeds) terC.Add(c);
				foreach (int s in superL)
				{
					if (piece[s] != c) continue;
					int bk = 0; double bd = 1e18;
					for (int k = 0; k < seeds.Count; k++)
					{
						double dd = SDist(s, seeds[k]) * (0.85 + 0.3 * Hash(s, k, 5202));
						if (dd < bd) { bd = dd; bk = k; }
					}
					shT[s] = t0 + bk;
				}
			}
		}
		Terranes = terC.Count; TerraneCont = terC.ToArray();
		// ---- 4. PARTNERS: of the pieces side by side in the supercontinent, about 1 pair in 3 (MeetChance, by hash, the lowest
		// first) stay joined: each joined to 2 others at most, no ring, and no joined group over GroupMax of the land. In
		// YoungSeaChance of worlds the partner pair with the longest join has only just begun to part (a young sea)
		var touch = new int[NC * NC];
		foreach (int s in superL) { int a = piece[s]; if (a < 0) continue; foreach (int t in adj[s]) { int b = piece[t]; if (b >= 0 && b != a) touch[a * NC + b]++; } }
		var pairs = new List<int>();
		for (int a = 0; a < NC; a++) for (int b = a + 1; b < NC; b++) if (touch[a * NC + b] > 0) pairs.Add(a * NC + b);
		double PH(int q) => Hash(q / NC, q % NC, 5007);
		pairs.Sort((p, q) => PH(p) != PH(q) ? PH(p).CompareTo(PH(q)) : p.CompareTo(q));
		var root = new int[NC]; for (int c = 0; c < NC; c++) root[c] = c;
		int Find(int c) { while (root[c] != c) c = root[c]; return c; }
		var gArea = (double[])pArea.Clone(); var nPart = new int[NC]; partner = new bool[NC * NC]; int links = 0;
		foreach (int q in ForcedJoin)
		{   // (Draft 2k: pieces that had no room to drift apart stay joined first, whatever the limits)
			int a = q / NC, b = q % NC; if (partner[a * NC + b]) continue;
			partner[a * NC + b] = partner[b * NC + a] = true; nPart[a]++; nPart[b]++; links++;
			int ra = Find(a), rb = Find(b); if (ra == rb) continue;
			int lo = Math.Min(ra, rb), hi = Math.Max(ra, rb); root[hi] = lo; gArea[lo] += gArea[hi];
		}
		foreach (int q in pairs)
		{
			if (PH(q) >= MeetChance) break;
			int a = q / NC, b = q % NC; if (nPart[a] >= 2 || nPart[b] >= 2) continue;
			int ra = Find(a), rb = Find(b); if (ra == rb || gArea[ra] + gArea[rb] > GroupMax * area) continue;
			partner[a * NC + b] = partner[b * NC + a] = true; nPart[a]++; nPart[b]++; links++;
			int lo = Math.Min(ra, rb), hi = Math.Max(ra, rb); root[hi] = lo; gArea[lo] += gArea[hi];
		}
		GroupOf = new int[NC]; Groups = 0; var gid = new int[NC]; for (int c = 0; c < NC; c++) gid[c] = -1;
		for (int c = 0; c < NC; c++) { int r = Find(c); if (gid[r] < 0) gid[r] = Groups++; GroupOf[c] = gid[r]; }
		YsA = YsB = -1;
		if (links > 0 && Hash(0, 2, 5201) < YoungSeaChance)
		{
			int bq = -1;
			for (int a = 0; a < NC; a++) for (int b = a + 1; b < NC; b++) if (partner[a * NC + b] && (bq < 0 || touch[a * NC + b] > touch[bq])) bq = a * NC + b;
			int a0 = bq / NC, b0 = bq % NC;
			if (pArea[b0] > pArea[a0]) (a0, b0) = (b0, a0);
			YsA = a0; YsB = b0;   // (the smaller one gets a plate of its own)
		}
		// ---- 5. THE DRIFT, on the history grid (each of its cells takes the plan cell under its middle). Each group heads
		// straight out from the supercontinent's middle: its way is from that middle to its own, turned aside by up to DriftTurn
		// by hash (a group at the middle: its own way by hash), and its goal is DriftReach of the way to as far as it can go
		// that way before any of it would reach the world's edge sea. So the supercontinent's middle opens into a new ocean.
		// Biggest first (ties: the lower group), each is put down at the place nearest its goal (by distance, then z, then x) where
		// all of it lies inside the grid, out of the world's edge sea (DriftEdge of the map; no further in than it starts) and
		// DriftGap of the map from every group put down before it; if there is no such place, the gap eases to 0.6, 0.3 of
		// that, then 1 cell. The ages between lie evenly along the way
		var gc0 = new int[HG * HG]; var gt0 = new int[HG * HG];
		for (int gz = 0; gz < HG; gz++) for (int gx = 0; gx < HG; gx++)
		{
			int x = (int)Math.Floor((gx + 0.5) * N / HG), z = (int)Math.Floor((gz + 0.5) * N / HG), i = z * N + x;
			gc0[gz * HG + gx] = cont0[i]; gt0[gz * HG + gx] = cont0[i] >= 0 ? shT[Shape[i]] : -1;
		}
		histCont0 = gc0; histTer0 = gt0;
		int G = Groups;
		var gcells = new List<int>[G]; for (int g = 0; g < G; g++) gcells[g] = new List<int>();
		double mx = 0, mz = 0; int mn = 0;
		for (int q = 0; q < HG * HG; q++) { int c = gc0[q]; if (c < 0) continue; gcells[GroupOf[c]].Add(q); mx += q % HG + 0.5; mz += q / HG + 0.5; mn++; }
		mx /= Math.Max(1, mn); mz /= Math.Max(1, mn);
		var gX = new double[G]; var gZ = new double[G];
		for (int g = 0; g < G; g++) { foreach (int q in gcells[g]) { gX[g] += q % HG + 0.5; gZ[g] += q / HG + 0.5; } if (gcells[g].Count > 0) { gX[g] /= gcells[g].Count; gZ[g] /= gcells[g].Count; } }
		GDirX = new double[G]; GDirZ = new double[G];
		var goX = new int[G]; var goZ = new int[G];
		for (int g = 0; g < G; g++)
		{
			int gn = gcells[g].Count; double rmx = mx, rmz = mz;   // (Draft 2k: away from the middle of the REST of the supercontinent, so the middle group moves too)
			if (AwayRest > 0 && mn > gn) { rmx = (mx * mn - gX[g] * gn) / (mn - gn); rmz = (mz * mn - gZ[g] * gn) / (mn - gn); }
			double dx = gX[g] - rmx, dz = gZ[g] - rmz, l = Math.Sqrt(dx * dx + dz * dz);
			if (l < 0.03 * HG) { dx = 2 * Hash(g, 0, 5213) - 1; dz = 2 * Hash(g, 1, 5213) - 1; double l2 = Math.Sqrt(dx * dx + dz * dz); if (l2 < 1e-9) { dx = 1; dz = 0; l2 = 1; } dx = dx / l2 * 0.03 * HG; dz = dz / l2 * 0.03 * HG; l = 0.03 * HG; }
			double t = (2 * Hash(g, 2, 5213) - 1) * DriftTurn, ux = dx - t * dz, uz = dz + t * dx, ul = Math.Sqrt(ux * ux + uz * uz);
			GDirX[g] = ux / ul; GDirZ[g] = uz / ul;
		}
		// (Draft 2k, the user: "The pieces should also rotate as well as just move". Each group turns about its own middle as it
		// drifts. Its turn by hash: one way or the other by Hash(g, 0, 5216) (under 0.5: minus, north of east first), the tan of
		// half its whole turn SpinMin to SpinMax (about 10 to 45 degrees) by Hash(g, 1, 5216). The drift may take that turn, the
		// same the other way, or half of either (SpinTry, in that order), whichever leaves it the most open sea (below). A turn
		// whose half has tan u has cos (1 - u^2) / (1 + u^2) and sin 2u / (1 + u^2): no angles; a point turns about the middle as
		// x' = cos dx - sin dz, z' = sin dx + cos dz. At each age the share of the way it has come times its u. The group's cells
		// after a turn: each history cell whose middle, turned back about the group's middle, falls in one of the group's cells)
		GSpin = new double[G]; GCenX = gX; GCenZ = gZ;
		var spin0 = new double[G];
		for (int g = 0; g < G; g++) spin0[g] = (Hash(g, 0, 5216) < 0.5 ? -1 : 1) * (SpinMin + (SpinMax - SpinMin) * Hash(g, 1, 5216));
		const int PW = 3 * HG;   // (a turned cell's place: (x + HG) + (z + HG) x 3 HG, as it may lie off the grid before its drift)
		int QX(int q) => q % PW - HG; int QZ(int q) => q / PW - HG;
		int NT = SpinTry.Length;
		var edge0 = new int[G]; var turnC = new List<int>[G * NT]; var rcells = new List<int>[G]; var spinPick = new int[G];
		for (int g = 0; g < G; g++)
		{
			int we = HG, wx0 = HG, wx1 = -1, wz0 = HG, wz1 = -1;
			foreach (int q in gcells[g]) { int x = q % HG, z = q / HG; we = Math.Min(we, Math.Min(Math.Min(x, z), Math.Min(HG - 1 - x, HG - 1 - z))); wx0 = Math.Min(wx0, x); wx1 = Math.Max(wx1, x); wz0 = Math.Min(wz0, z); wz1 = Math.Max(wz1, z); }
			edge0[g] = we;
			for (int m = 0; m < NT; m++)
			{
				var cl = new List<int>(); turnC[g * NT + m] = cl; if (wx1 < 0) continue;
				double wu = spin0[g] * SpinTry[m], wd = 1 + wu * wu, wc = (1 - wu * wu) / wd, ws = 2 * wu / wd;
				// (the box the turned cells lie in: the group's box's corners turned, 2 cells wider)
				double kbx0 = 1e9, kbx1 = -1e9, kbz0 = 1e9, kbz1 = -1e9;
				foreach (var (px, pz) in new[] { (wx0, wz0), (wx1 + 1, wz0), (wx0, wz1 + 1), (wx1 + 1, wz1 + 1) })
				{
					double ex = px - gX[g], ez = pz - gZ[g], rx = gX[g] + wc * ex - ws * ez, rz = gZ[g] + ws * ex + wc * ez;
					kbx0 = Math.Min(kbx0, rx); kbx1 = Math.Max(kbx1, rx); kbz0 = Math.Min(kbz0, rz); kbz1 = Math.Max(kbz1, rz);
				}
				int ax0 = Math.Max(-HG, (int)Math.Floor(kbx0) - 2), ax1 = Math.Min(2 * HG - 1, (int)Math.Floor(kbx1) + 2);
				int az0 = Math.Max(-HG, (int)Math.Floor(kbz0) - 2), az1 = Math.Min(2 * HG - 1, (int)Math.Floor(kbz1) + 2);
				for (int z = az0; z <= az1; z++) for (int x = ax0; x <= ax1; x++)
				{
					double ex = x + 0.5 - gX[g], ez = z + 0.5 - gZ[g];
					int sx = (int)Math.Floor(gX[g] + wc * ex + ws * ez), sz = (int)Math.Floor(gZ[g] - ws * ex + wc * ez);
					if (sx < 0 || sz < 0 || sx >= HG || sz >= HG) continue;
					int wcc = gc0[sz * HG + sx]; if (wcc < 0 || GroupOf[wcc] != g) continue;
					cl.Add((z + HG) * PW + x + HG);
				}
			}
			for (int m = 0; m < NT; m++)
			{   // (speed only, the same results: each turned list with its edge cells first (a cell with a side neighbour not in it), so
				// a place that fails fails fast)
				var cl = turnC[g * NT + m]; var inL = new HashSet<int>(cl); var edgeL = new List<int>(); var midL = new List<int>();
				foreach (int q in cl) (inL.Contains(q - 1) && inL.Contains(q + 1) && inL.Contains(q - PW) && inL.Contains(q + PW) ? midL : edgeL).Add(q);
				edgeL.AddRange(midL); turnC[g * NT + m] = edgeL;
			}
			rcells[g] = turnC[g * NT];
		}
		var order = new List<int>(); for (int g = 0; g < G; g++) if (gcells[g].Count > 0) order.Add(g);
		order.Sort((a, b) => gcells[a].Count != gcells[b].Count ? gcells[b].Count.CompareTo(gcells[a].Count) : a.CompareTo(b));
		// (the places to try about a goal, nearest first: by distance, then z, then x)
		int HR = HG / 2; var tryAt = new List<int>();
		for (int dz = -HR; dz <= HR; dz++) for (int dx = -HR; dx <= HR; dx++) tryAt.Add((dz + HR) * (2 * HR + 1) + dx + HR);
		int TX(int k) => k % (2 * HR + 1) - HR; int TZ(int k) => k / (2 * HR + 1) - HR;
		tryAt.Sort((p, q) => { int dp = TX(p) * TX(p) + TZ(p) * TZ(p), dq = TX(q) * TX(q) + TZ(q) * TZ(q); return dp != dq ? dp.CompareTo(dq) : p.CompareTo(q); });
		var offX = new int[G]; var offZ = new int[G]; var placedG = new bool[G];
		int edgeH = (int)Math.Ceiling(DriftEdge * HG); double gapH = DriftGap * HG;
		int EdgeAt(int g, int ox, int oz)
		{
			int m = HG;
			foreach (int q in rcells[g]) { int x = QX(q) + ox, z = QZ(q) + oz; m = Math.Min(m, Math.Min(Math.Min(x, z), Math.Min(HG - 1 - x, HG - 1 - z))); }
			return m;
		}
		// (each group's goal: DriftReach of the way to as far as it could go along its way before any of it would reach the
		// world's edge sea)
		for (int g = 0; g < G; g++)
		{
			int e0 = Math.Min(edgeH, edge0[g]), reach = 0;
			for (int t = 1; t <= HG; t++)
			{
				int ox = (int)Math.Floor(GDirX[g] * t + 0.5), oz = (int)Math.Floor(GDirZ[g] * t + 0.5);
				if (EdgeAt(g, ox, oz) < e0) break;
				reach = t;
			}
			goX[g] = (int)Math.Floor(GDirX[g] * reach * DriftReach + 0.5); goZ[g] = (int)Math.Floor(GDirZ[g] * reach * DriftReach + 0.5);
		}
		var occ = new int[HG * HG]; for (int q = 0; q < HG * HG; q++) occ[q] = -1;
		var dPlaced = ChamferH(q => false);
		var gotGap = new double[G]; int retryJoin = -1;
		foreach (int g in order)
		{
			int edgeOk = Math.Min(edgeH, edge0[g]); bool done = false;
			// (Draft 2k: for each turn in SpinTry's order, the nearest place to the goal with all of it DriftGap from the others;
			// the nearest of those, the earlier turn on ties)
			int bestN = tryAt.Count;
			for (int tm = 0; tm < NT; tm++)
			{
				var cl = turnC[g * NT + tm];
				for (int n = 0; n < bestN; n++)
				{
					int k = tryAt[n], ox = goX[g] + TX(k), oz = goZ[g] + TZ(k); bool ok = true;
					foreach (int q in cl)
					{
						int x = QX(q) + ox, z = QZ(q) + oz;
						if (x < edgeOk || z < edgeOk || x > HG - 1 - edgeOk || z > HG - 1 - edgeOk || dPlaced[z * HG + x] < gapH) { ok = false; break; }
					}
					if (!ok) continue;
					bestN = n; offX[g] = ox; offZ[g] = oz; spinPick[g] = tm; done = true; gotGap[g] = gapH; break;
				}
			}
			if (!done)
			{   // (Draft 2k: no room at DriftGap with any turn: the turn and place where the open sea round it is widest, the
				// earlier turn, then the nearest place, on ties. Where it would lie on another group, the sea counts as minus how far
				// that cell is inside the other (from its nearest free cell), so the least overlap wins)
				var dIn = ChamferH(q => occ[q] < 0);
				double best = -1e9;
				for (int tm = 0; tm < NT; tm++)
					foreach (int k in tryAt)
					{
						int ox = goX[g] + TX(k), oz = goZ[g] + TZ(k); double m = 1e9;
						foreach (int q in turnC[g * NT + tm])
						{
							int x = QX(q) + ox, z = QZ(q) + oz;
							if (x < edgeOk || z < edgeOk || x > HG - 1 - edgeOk || z > HG - 1 - edgeOk) { m = -1e9; break; }
							int i = z * HG + x; m = Math.Min(m, occ[i] >= 0 ? -dIn[i] : dPlaced[i]); if (m <= best) break;
						}
						if (m > best) { best = m; offX[g] = ox; offZ[g] = oz; spinPick[g] = tm; done = true; gotGap[g] = m; }
					}
			}
			if (!done) { offX[g] = 0; offZ[g] = 0; gotGap[g] = 0; spinPick[g] = 0; }
			rcells[g] = turnC[g * NT + spinPick[g]];
			if (gotGap[g] < JoinRoom && G > 1)
			{   // (Draft 2k: no room to drift free: of its pieces and those of the groups already put down (else any other
				// group's), the pair that touched most in the supercontinent (ties: the lower pair) stays joined, and all of this
				// runs again)
				int bq = -1;
				for (int pass = 0; pass < 2 && bq < 0; pass++)
					for (int a = 0; a < NC; a++)
					{
						if (GroupOf[a] != g) continue;
						for (int b2 = 0; b2 < NC; b2++)
						{
							int h = GroupOf[b2]; if (h == g || pass == 0 && !placedG[h] || touch[a * NC + b2] == 0) continue;
							int q2 = Math.Min(a, b2) * NC + Math.Max(a, b2);
							if (bq < 0 || touch[a * NC + b2] > touch[bq / NC * NC + bq % NC] || touch[a * NC + b2] == touch[bq / NC * NC + bq % NC] && q2 < bq) bq = q2;
						}
					}
				if (bq >= 0 && !ForcedJoin.Contains(bq)) { retryJoin = bq; break; }
			}
			placedG[g] = true;
			foreach (int q in rcells[g]) { int x = QX(q) + offX[g], z = QZ(q) + offZ[g]; if (x >= 0 && z >= 0 && x < HG && z < HG && occ[z * HG + x] < 0) occ[z * HG + x] = g; }
			dPlaced = ChamferH(q => occ[q] >= 0);
		}
		if (retryJoin >= 0)
		{
			ForcedJoin.Add(retryJoin);
			MakeForward(gs, GX, NS, ptx, ptz, shapeCells, adj, edgeN, want);
			return;
		}
		// (then SpreadPasses passes, in the same order: each group moves, within SpreadR history cells, to the place where the
		// nearest other group is furthest away (by at least half a cell; ties: the nearest place), so the oceans even out and
		// the land reaches into the corners)
		for (int pass = 0; pass < SpreadPasses; pass++)
			foreach (int g in order)
			{
				var dO = ChamferH(q => occ[q] >= 0 && occ[q] != g);
				var dInO = ChamferH(q => !(occ[q] >= 0 && occ[q] != g));   // (Draft 2k: inside another group: minus how far in)
				int edgeOk = Math.Min(edgeH, edge0[g]);
				double MinD(List<int> cl, int ox, int oz, double stop)
				{
					double m = 1e9;
					foreach (int q in cl)
					{
						int x = QX(q) + ox, z = QZ(q) + oz;
						if (x < edgeOk || z < edgeOk || x > HG - 1 - edgeOk || z > HG - 1 - edgeOk) return -1e9;
						int i = z * HG + x; m = Math.Min(m, occ[i] >= 0 && occ[i] != g ? -dInO[i] : dO[i]); if (m <= stop) return m;
					}
					return m;
				}
				// (Draft 2k: and each turn of SpinTry, in its order: a move or a new turn must widen the sea by half a cell)
				int bx = offX[g], bz = offZ[g], bt = spinPick[g]; double bm = MinD(rcells[g], bx, bz, -1e9);
				for (int tm = 0; tm < NT; tm++)
					foreach (int k in tryAt)
					{
						int dx = TX(k), dz = TZ(k); if (dx * dx + dz * dz > SpreadR * SpreadR) continue;
						double m = MinD(turnC[g * NT + tm], offX[g] + dx, offZ[g] + dz, bm + 0.5);
						if (m > bm + 0.5) { bm = m; bx = offX[g] + dx; bz = offZ[g] + dz; bt = tm; }
					}
				if (bx == offX[g] && bz == offZ[g] && bt == spinPick[g]) continue;
				offX[g] = bx; offZ[g] = bz; spinPick[g] = bt; rcells[g] = turnC[g * NT + bt];
				// (the taken cells again, every group in placing order, the first keeping a cell)
				for (int q = 0; q < HG * HG; q++) occ[q] = -1;
				foreach (int h in order) foreach (int q in rcells[h]) { int x = QX(q) + offX[h], z = QZ(q) + offZ[h]; if (x >= 0 && z >= 0 && x < HG && z < HG && occ[z * HG + x] < 0) occ[z * HG + x] = h; }
			}
		var minGap = new double[G];
		for (int g = 0; g < G; g++)
		{
			var dO = ChamferH(q => occ[q] >= 0 && occ[q] != g); double m = 1e9;
			foreach (int q in rcells[g]) { int x = QX(q) + offX[g], z = QZ(q) + offZ[g]; if (x >= 0 && z >= 0 && x < HG && z < HG) m = Math.Min(m, dO[z * HG + x]); }
			minGap[g] = G == 1 ? 0 : m;
		}
		for (int g = 0; g < G; g++) GSpin[g] = spin0[g] * SpinTry[spinPick[g]];
		// (Draft 2k: each group's way is now the way it really went, its drift (if it moved a cell or more), as it may have been
		// put down off its first way; its plate moves that way, and its leading coast is the one that led)
		if (DirFromDrift)
			for (int g = 0; g < G; g++)
			{
				double l = Math.Sqrt((double)offX[g] * offX[g] + (double)offZ[g] * offZ[g]);
				if (l >= 1) { GDirX[g] = offX[g] / l; GDirZ[g] = offZ[g] / l; }
			}
		HSpinC = new double[(HistSteps + 1) * G]; HSpinS = new double[(HistSteps + 1) * G];
		for (int k = 0; k <= HistSteps; k++) for (int g = 0; g < G; g++)
		{
			double wu = GSpin[g] * (HistSteps - k) / HistSteps, wd = 1 + wu * wu;
			HSpinC[k * G + g] = (1 - wu * wu) / wd; HSpinS[k * G + g] = 2 * wu / wd;
		}
		int rounds = 0; for (int g = 0; g < G; g++) rounds = Math.Max(rounds, Math.Max(Math.Abs(offX[g]), Math.Abs(offZ[g])));
		DriftRounds = rounds;
		HOffX = new int[(HistSteps + 1) * G]; HOffZ = new int[(HistSteps + 1) * G];
		for (int k = 0; k <= HistSteps; k++)
			for (int g = 0; g < G; g++)
			{
				HOffX[k * G + g] = (int)Math.Floor((double)offX[g] * (HistSteps - k) / HistSteps + 0.5);
				HOffZ[k * G + g] = (int)Math.Floor((double)offZ[g] * (HistSteps - k) / HistSteps + 0.5);
			}
		var eased = new List<string>(); for (int g = 0; g < G; g++) eased.Add($"{g} {minGap[g] / HG:0.000}");
		string easedNote = string.Join(", ", eased);
		{   // (test print: the open sea from each group with a continent to the nearest other such group)
			var hasC = new bool[G]; for (int c = 0; c < Continents; c++) hasC[GroupOf[c]] = true;
			var cg = new List<string>();
			for (int g = 0; g < G; g++)
			{
				if (!hasC[g]) continue;
				var dO = ChamferH(q => occ[q] >= 0 && occ[q] != g && hasC[occ[q]]); double m = 1e9;
				foreach (int q in rcells[g]) { int x = QX(q) + offX[g], z = QZ(q) + offZ[g]; if (x >= 0 && z >= 0 && x < HG && z < HG) m = Math.Min(m, dO[z * HG + x]); }
				if (m < 1e8) cg.Add($"{g} {m / HG:0.000}");
			}
			ContGapNote = string.Join(", ", cg);
		}
		// ---- 6. TODAY'S CRUST: each piece moved by its group's drift (history cells times N / 128, rounded), in piece order
		// (where two land on one cell, the lower piece keeps it)
		PieceStartX = new int[NC]; PieceStartZ = new int[NC];
		for (int c = 0; c < NC; c++)
		{
			int g = GroupOf[c];
			PieceStartX[c] = (int)Math.Floor(offX[g] * (double)N / HG + 0.5); PieceStartZ[c] = (int)Math.Floor(offZ[g] * (double)N / HG + 0.5);
		}
		ContId = new int[NN]; Terrane = new int[NN]; Crust = new bool[NN];
		for (int i = 0; i < NN; i++) { ContId[i] = -1; Terrane[i] = -1; }
		for (int c = 0; c < NC; c++)
		{
			// (Draft 2k: turned with its group: each plan cell that StartCellOf takes back to one of the piece's cells. The box
			// to look in: the piece's box's corners turned about its group's middle and moved, 2 cells wider)
			int g = GroupOf[c]; double vc = HSpinC[g], vs = HSpinS[g], vcx = GCenX[g] * N / HG, vcz = GCenZ[g] * N / HG;
			int vx0 = N, vx1 = -1, vz0 = N, vz1 = -1;
			for (int i = 0; i < NN; i++) if (cont0[i] == c) { int x = i % N, z = i / N; vx0 = Math.Min(vx0, x); vx1 = Math.Max(vx1, x); vz0 = Math.Min(vz0, z); vz1 = Math.Max(vz1, z); }
			if (vx1 < 0) continue;
			double kbx0 = 1e9, kbx1 = -1e9, kbz0 = 1e9, kbz1 = -1e9;
			foreach (var (px, pz) in new[] { (vx0, vz0), (vx1 + 1, vz0), (vx0, vz1 + 1), (vx1 + 1, vz1 + 1) })
			{
				double ex = px - vcx, ez = pz - vcz, rx = vcx + vc * ex - vs * ez + PieceStartX[c], rz = vcz + vs * ex + vc * ez + PieceStartZ[c];
				kbx0 = Math.Min(kbx0, rx); kbx1 = Math.Max(kbx1, rx); kbz0 = Math.Min(kbz0, rz); kbz1 = Math.Max(kbz1, rz);
			}
			int ax0 = Math.Max(0, (int)Math.Floor(kbx0) - 2), ax1 = Math.Min(N - 1, (int)Math.Floor(kbx1) + 2);
			int az0 = Math.Max(0, (int)Math.Floor(kbz0) - 2), az1 = Math.Min(N - 1, (int)Math.Floor(kbz1) + 2);
			for (int z = az0; z <= az1; z++) for (int x = ax0; x <= ax1; x++)
			{
				int j = z * N + x; if (ContId[j] >= 0) continue;
				int i = StartCellOf(c, x, z); if (i < 0 || cont0[i] != c) continue;
				ContId[j] = c; Terrane[j] = shT[Shape[i]]; Crust[j] = true;
			}
		}
		YoungSea = new bool[NN];
		int moved = 0; for (int g = 0; g < G; g++) if (offX[g] != 0 || offZ[g] != 0) moved++;
		var pl = new List<string>(); for (int a = 0; a < NC; a++) for (int b = a + 1; b < NC; b++) if (partner[a * NC + b]) pl.Add($"{a}-{b}");
		DriftNote = $"{SuperShapeNote}; a supercontinent of {NC} pieces ({Continents} continents, {BigIslands} big islands), {Terranes} old blocks; {links} pairs met (partners, joined since the supercontinent: {(pl.Count == 0 ? "none" : string.Join(" ", pl))}{(ForcedJoin.Count == 0 ? "" : $"; of them, kept joined for want of room: {string.Join(" ", ForcedJoin.Select(q => $"{q / NC}-{q % NC}"))}")}); {G} groups, {moved} drifted (the furthest {rounds / (double)HG:0.00} of the map); open sea round each group (share of the map): {easedNote}; between the continents' groups: {ContGapNote}; turns (degrees, + east toward south): {string.Join(", ", System.Linq.Enumerable.Range(0, G).Select(g => $"{g} {2 * Math.Atan(GSpin[g]) * 180 / Math.PI:0}"))}";
	}

	// (Draft 2k) where a plan cell of piece c lay in the supercontinent: its middle less the piece's offset, turned back about its
	// group's middle (history cells x N / 128) by the group's whole turn, then the cell it falls in (-1 off the map)
	int StartCellOf(int c, int x, int z)
	{
		int g = GroupOf[c]; double vc = HSpinC[g], vs = HSpinS[g], vcx = GCenX[g] * N / HG, vcz = GCenZ[g] * N / HG;
		double ex = x + 0.5 - PieceStartX[c] - vcx, ez = z + 0.5 - PieceStartZ[c] - vcz;
		int sx = (int)Math.Floor(vcx + vc * ex + vs * ez), sz = (int)Math.Floor(vcz - vs * ex + vc * ez);
		return sx < 0 || sz < 0 || sx >= N || sz >= N ? -1 : sz * N + sx;
	}

	// (from MakePlates, once today's plates are made) the young sea: the plate edge between the young sea's pair's plates: the
	// continent within 0.006 of the map (2 cells at least) of it, on both plates, becomes ocean floor
	string MakeYoungSea()
	{
		if (YsA < 0 || PiecePlate == null) return "none";
		int a = PiecePlate[YsA], b = PiecePlate[YsB]; if (a < 0 || b < 0 || a == b) return "none";
		int NN = N * N; double w = Math.Max(2, 0.006 * N); int taken = 0;
		for (int i = 0; i < NN; i++)
		{
			if (!Crust[i] || Other[i] < 0 || Bd[i] > w) continue;
			int p = Plate[i], o = Other[i];
			if (!(p == a && o == b || p == b && o == a)) continue;
			Crust[i] = false; ContId[i] = -1; Terrane[i] = -1; YoungSea[i] = true; taken++;
		}
		if (taken > 0) EdgeSweep();
		return $"pieces {YsA} and {YsB}, plates {a} and {b} ({taken} cells)";
	}

	void MakeHistory()
	{
		int NN = N * N, NC = Continents + BigIslands, G = Groups;
		RangeYoung = new double[NN]; RangeMid = new double[NN]; RangeOld = new double[NN];
		// old sutures: between two old blocks of one piece
		var sut = new bool[NN];
		for (int i = 0; i < NN; i++)
		{
			int t = Terrane[i]; if (t < 0) continue; int x = i % N, z = i / N, c = ContId[i];
			if (x + 1 < N && ContId[i + 1] == c && Terrane[i + 1] != t || x > 0 && ContId[i - 1] == c && Terrane[i - 1] != t
				|| z + 1 < N && ContId[i + N] == c && Terrane[i + N] != t || z > 0 && ContId[i - N] == c && Terrane[i - N] != t) sut[i] = true;
		}
		SutureDist = Chamfer(j => sut[j]);
		// rift-born coasts: a piece's cells that, in the supercontinent, lay beside another piece it no longer touches; their coast
		// and shelf, fading out over RiftReach of the map
		var touchNow = new bool[NC * NC];
		for (int i = 0; i < NN; i++)
		{
			int a = ContId[i]; if (a < 0) continue; int x = i % N, z = i / N;
			if (x + 1 < N && ContId[i + 1] >= 0 && ContId[i + 1] != a) touchNow[a * NC + ContId[i + 1]] = touchNow[ContId[i + 1] * NC + a] = true;
			if (z + 1 < N && ContId[i + N] >= 0 && ContId[i + N] != a) touchNow[a * NC + ContId[i + N]] = touchNow[ContId[i + N] * NC + a] = true;
		}
		var rbG = new bool[HG * HG]; int rbPairs = 0; var seenPair = new bool[NC * NC];
		for (int q = 0; q < HG * HG; q++)
		{
			int a = histCont0[q]; if (a < 0) continue; int x = q % HG, z = q / HG;
			for (int k = 0; k < 4; k++)
			{
				int xx = x + DX[k], zz = z + DZ[k]; if (xx < 0 || zz < 0 || xx >= HG || zz >= HG) continue;
				int b = histCont0[zz * HG + xx];
				if (b >= 0 && b != a && !touchNow[a * NC + b]) { rbG[q] = true; if (!seenPair[a * NC + b]) { seenPair[a * NC + b] = seenPair[b * NC + a] = true; rbPairs++; } }
			}
		}
		var rbDist = Chamfer(j =>
		{
			int c = ContId[j]; if (c < 0) return false;
			int i0 = StartCellOf(c, j % N, j / N); if (i0 < 0) return false; int x = i0 % N, z = i0 / N;
			int gx = Math.Min(HG - 1, (int)Math.Floor((x + 0.5) * HG / N)), gz = Math.Min(HG - 1, (int)Math.Floor((z + 0.5) * HG / N));
			return rbG[gz * HG + gx];
		});
		RiftBorn = new double[NN];
		for (int i = 0; i < NN; i++) RiftBorn[i] = 1 - Smoothstep(0, RiftReach * N, rbDist[i]);
		int sutN = 0, landN = 0, rbN = 0;
		for (int i = 0; i < NN; i++) if (Crust[i]) { landN++; if (SutureDist[i] * Cell < 60 * Belt) sutN++; if (RiftBorn[i] > 0.5) rbN++; }
		HistNote = $"{DriftNote}; {rbPairs} pairs split apart; young sea: {YoungSeaNote}; land within 60 x Belt blocks of an old suture {100.0 * sutN / Math.Max(1, landN):0.0}% of the continents, on rift-born coasts {100.0 * rbN / Math.Max(1, landN):0.0}%";
	}
	string YoungSeaNote = "none";

	// a hot-spot trail point's place: where the volcano made at the hot spot `ag` ages ago is now, carried by the plate over the
	// hot spot. A group's plate carries it along that group's own path; an ocean plate by its drift (0.035 of the map an age)
	(double x, double z) TrailAt(double hx, double hz, int p, double ag)
	{
		int g = PlateGroup != null && p >= 0 && p < PlateGroup.Length ? PlateGroup[p] : -1;
		if (g < 0 || HOffX == null) return (hx - VX[p] * 0.035 * N * ag, hz - VZ[p] * 0.035 * N * ag);
		int G = Groups, k0 = (int)Math.Floor(ag); double f = ag - k0; int k1 = Math.Min(HistSteps, k0 + 1); k0 = Math.Min(HistSteps, k0);
		double ox = HOffX[k0 * G + g] * (1 - f) + HOffX[k1 * G + g] * f, oz = HOffZ[k0 * G + g] * (1 - f) + HOffZ[k1 * G + g] * f;
		// (Draft 2k: and turned with the group: back to the supercontinent by that age's turn (cos and sin between the two ages,
		// then made length 1), then forward by today's)
		double ca = HSpinC[k0 * G + g] * (1 - f) + HSpinC[k1 * G + g] * f, sa = HSpinS[k0 * G + g] * (1 - f) + HSpinS[k1 * G + g] * f, la = Math.Sqrt(ca * ca + sa * sa); ca /= la; sa /= la;
		double sc = N / (double)HG, cx = GCenX[g] * sc, cz = GCenZ[g] * sc;
		double dx = hx - cx - ox * sc, dz = hz - cz - oz * sc, ux = ca * dx + sa * dz, uz = -sa * dx + ca * dz;
		double c0 = HSpinC[g], s0 = HSpinS[g];
		return (cx + HOffX[g] * sc + c0 * ux - s0 * uz, cz + HOffZ[g] * sc + s0 * ux + c0 * uz);
	}
	public int[] PlateGroup;             // each plate's group (-1: an ocean plate)

	// (test pictures) the history: the continents at each age, from the supercontinent (history_4) to now (history_0): each
	// continent its own colour, its old blocks lighter and darker, the hot spots magenta, 3 pixels per history cell; and today's
	// ranges by age with the hot-spot trails
	public void SaveHistory(string dir)
	{
		if (HOffX == null) return;
		int sc = 3, G = Groups;
		uint[] cols = { 0xe6194b, 0x3cb44b, 0xffe119, 0x4363d8, 0xf58231, 0x911eb4, 0x46f0f0, 0xf032e6, 0xbcf60c, 0xfabebe, 0x008080, 0xe6beff };
		var gnow = new int[HG * HG]; var tnow = new int[HG * HG];
		for (int gz = 0; gz < HG; gz++) for (int gx = 0; gx < HG; gx++)
		{
			int x = (int)Math.Floor((gx + 0.5) * N / HG), z = (int)Math.Floor((gz + 0.5) * N / HG), i = z * N + x;
			gnow[gz * HG + gx] = ContId[i]; tnow[gz * HG + gx] = Terrane[i];
		}
		for (int st = 0; st <= HistSteps; st++)
		{
			var img = Godot.Image.CreateEmpty(HG * sc, HG * sc, false, Godot.Image.Format.Rgb8);
			img.Fill(new Godot.Color(0.1f, 0.2f, 0.42f));
			for (int q = 0; q < HG * HG; q++)
			{
				int c = -1, t = -1, x = q % HG, z = q / HG;
				if (st == 0) { c = gnow[q]; t = tnow[q]; }
				else
					for (int g = 0; g < G && c < 0; g++)
					{   // (Draft 2k: each group moved and turned as at that age)
						double cc = HSpinC[st * G + g], ss = HSpinS[st * G + g], ex = x + 0.5 - HOffX[st * G + g] - GCenX[g], ez = z + 0.5 - HOffZ[st * G + g] - GCenZ[g];
						int sx = (int)Math.Floor(GCenX[g] + cc * ex + ss * ez), sz = (int)Math.Floor(GCenZ[g] - ss * ex + cc * ez);
						if (sx < 0 || sz < 0 || sx >= HG || sz >= HG) continue;
						int c1 = histCont0[sz * HG + sx]; if (c1 < 0 || GroupOf[c1] != g) continue;
						c = c1; t = histTer0[sz * HG + sx];
					}
				if (c < 0 || t < 0) continue;
				uint u = cols[c % cols.Length]; float f = 0.7f + 0.3f * (float)Hash(t, 0, 5204);
				img.FillRect(new Godot.Rect2I(x * sc, z * sc, sc, sc), new Godot.Color(((u >> 16) & 255) / 255f * f, ((u >> 8) & 255) / 255f * f, (u & 255) / 255f * f));
			}
			if (HotX != null) for (int k = 0; k < HotX.Length; k++)
			{
				int X = (int)Math.Floor(HotX[k] / N * HG * sc), Z = (int)Math.Floor(HotZ[k] / N * HG * sc);
				img.FillRect(new Godot.Rect2I(X - 3, Z - 3, 7, 7), new Godot.Color(1, 0, 1));
			}
			img.SavePng($"{dir}/history_{st}.png");
		}
		// today's ranges by age: young red, middle orange, old violet (the strongest that raised the cell 15 blocks or more), on
		// grey land; the rift-born coasts' shelf pale cyan; the young sea teal; hot-spot trails as dots, yellow young to dark red old
		if (RangeYoung == null) return;
		int s2 = 2; var im = Godot.Image.CreateEmpty(N * s2, N * s2, false, Godot.Image.Format.Rgb8);
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			int i = z * N + x; Godot.Color c;
			if (Elev0[i] <= 0) c = YoungSea[i] ? new Godot.Color(0.1f, 0.6f, 0.6f) : RiftBorn[i] > 0.35 && !Crust[i] ? new Godot.Color(0.35f, 0.55f, 0.75f) : new Godot.Color(0.12f, 0.17f, 0.3f);
			else
			{
				double y = RangeYoung[i], m = RangeMid[i], o = RangeOld[i], mxr = Math.Max(y, Math.Max(m, o));
				c = mxr < 15 ? new Godot.Color(0.55f, 0.55f, 0.52f) : y >= mxr ? new Godot.Color(0.9f, 0.15f, 0.1f) : m >= mxr ? new Godot.Color(0.95f, 0.6f, 0.1f) : new Godot.Color(0.6f, 0.35f, 0.85f);
				if (RiftBorn[i] > 0.6 && Elev0[i] < 20) c = c.Lerp(new Godot.Color(0.6f, 0.9f, 0.95f), 0.35f);
			}
			im.FillRect(new Godot.Rect2I(x * s2, z * s2, s2, s2), c);
		}
		foreach (var t in Trails)
		{
			float a = (float)Math.Min(1, t[2] / HistSteps); var col = new Godot.Color(1, 0.9f, 0.1f).Lerp(new Godot.Color(0.45f, 0.05f, 0.05f), a);
			int X = (int)Math.Floor(t[0] * s2), Z = (int)Math.Floor(t[1] * s2);
			im.FillRect(new Godot.Rect2I(Math.Max(0, X - 3), Math.Max(0, Z - 3), 7, 7), new Godot.Color(0, 0, 0));
			im.FillRect(new Godot.Rect2I(Math.Max(0, X - 2), Math.Max(0, Z - 2), 5, 5), col);
		}
		if (HotX != null) for (int k = 0; k < HotX.Length; k++)
			im.FillRect(new Godot.Rect2I(Math.Max(0, (int)Math.Floor(HotX[k] * s2) - 4), Math.Max(0, (int)Math.Floor(HotZ[k] * s2) - 4), 9, 9), new Godot.Color(1, 0, 1));
		im.SavePng($"{dir}/ages.png");
	}

	// (test print: how thin each continent and big island is: its area over its mean depth from its own coast, squared. A disc is
	// 28, an ellipse twice as long as wide about 45, a strip 4 times as long as wide 64, 8 times 128)
	public string ThinNote()
	{
		int NN = N * N, NC = Continents + BigIslands; var parts = new List<string>(); var thin = new List<string>(); double worst = 0;
		for (int c = 0; c < NC; c++)
		{
			var d = Chamfer(j => ContId[j] != c); double a = 0, sum = 0;
			for (int i = 0; i < NN; i++) if (ContId[i] == c) { a++; sum += d[i]; }
			if (a == 0) continue;
			double m = sum / a, tt = a / (m * m); worst = Math.Max(worst, tt);
			parts.Add($"{c} {tt:0}"); if (tt > 80) thin.Add($"{c} ({100.0 * a / NN:0.0}% of the map)");
		}
		return $"thinness (area / mean depth squared; disc 28, 4:1 strip 64, 8:1 strip 128): {string.Join(", ", parts)}; the thinnest {worst:0}; over 80: {(thin.Count == 0 ? "none" : string.Join(", ", thin))}";
	}
}
