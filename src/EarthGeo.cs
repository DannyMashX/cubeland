using System;
using System.Collections.Generic;

namespace Cubeland;

// Earthlike worlds (GEOLOGY DRAFT, for the cross-section picture): which rock is where, from the plan's plate settings.
// Per cell, how strongly each setting works (collision range, arc over a sinking ocean, island arc, rift, mid-ocean ridge, hot
// spot, old continent core, big basin, quiet coast and shelf); then, per block, the rock from those (blended between cell
// middles), the depth below the ground and the beds. Only + - * / sqrt floor, like the plan.
public sealed partial class EarthPlan
{
	// (kept from MakeHeight for the geology)
	internal double[] GUp, GCoast, GArc0, GRift0, GMid0, GShelf, GSz, GOceanDist, GJoinDist, GContDist, HotX, HotZ;
	public double[] Oro, ArcC, Isl, RiftZ, MidZ, Hot, Craton, Basin, Margin, SedT, Datum, Frame, MetaTop, BdB, ContF;

	public enum Rock
	{
		Air, Water, Soil, Sand, Andesite, Granite, Basalt, Gabbro, Limestone, Chalk, Shale, Sandstone, Slate, Schist, Gneiss,
		Marble, Quartzite, Bedrock, Laterite, Bauxite, CopperOre, TinOre, IronOre, GoldOre, SilverOre,
		Conglomerate, Siltstone, Dolomite, Travertine, Diorite, Rhyolite, Peridotite, Phyllite, Serpentinite, Hornfels, FlintNodules,
		PrairieSoil, AshenSoil, RedEarth, Loess, DesertSoil, ClaySoil, SandySoil, StonySoil, VolcanicSoil, GlacialTill, RichSoil, Silt, Scree,
		Gravel, Peat, Mud, Permafrost
	}

	public void MakeGeo()
	{
		int NN = N * N; double C = Cell, A = Amp;
		Oro = new double[NN]; ArcC = new double[NN]; Isl = new double[NN]; RiftZ = new double[NN]; MidZ = new double[NN];
		Hot = new double[NN]; Craton = new double[NN]; Basin = new double[NN]; Margin = new double[NN]; SedT = new double[NN];
		Datum = new double[NN]; BdB = new double[NN]; ContF = new double[NN];
		// (how far each cell is inside a continent, in blocks: below 0 out at sea)
		var contDist = Chamfer(j => Crust[j]);
		for (int i = 0; i < NN; i++) ContF[i] = Crust[i] ? (GOceanDist[i] - 0.5) * C : -(contDist[i] - 0.5) * C;
		// (the ground evened out over about 100 blocks: what the sea floor's beds, and a collision range's, lie under)
		var E0 = Elev0 ?? Elev;   // (the land before erosion: the beds stay put as it wears down)
		var esm = (double[])E0.Clone(); int r = Math.Max(2, (int)Math.Floor(100.0 / C + 0.5)); BoxBlur(esm, r); BoxBlur(esm, r);
		// (the land's level, evened out over a wide area (about 250 blocks each way, three times over), the sea floor counted as no
		// deeper than 12 x Amp: on the continents the beds lie flat at this level, under lowlands and ordinary hills alike)
		var reg = new double[NN]; for (int i = 0; i < NN; i++) reg[i] = Math.Max(E0[i], -12 * A);
		int rr = Math.Max(2, (int)Math.Floor(250 * Wide / C + 0.5)); BoxBlur(reg, rr); BoxBlur(reg, rr); BoxBlur(reg, rr);
		Frame = new double[NN]; MetaTop = new double[NN];
		// (a collision range's own smooth height, the same as MakeHeight's range term without its roughness, and that evened out
		// like the land's level: the beds rise over the range's shape, not over each valley in it)
		var rl = new double[NN];
		for (int i = 0; i < NN; i++) { double w0 = GSz[i] * Belt; rl[i] = Crust[i] ? 85 * A * (0.45 + 0.55 * Math.Min(1, w0)) * GUp[i] * Bump(Bd[i] * C, 110 * w0) : 0; }
		var rlb = (double[])rl.Clone(); BoxBlur(rlb, rr); BoxBlur(rlb, rr); BoxBlur(rlb, rr);
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			int i = z * N + x, a = Plate[i]; double d = Bd[i] * C, w = GSz[i] * Belt, u = x + 0.5, v = z + 0.5; bool cont = Crust[i];
			BdB[i] = d;
			// a collision range and its folded flanks; Draft 2i: also a join's (middle-aged) and an old suture's, worn down to their
			// crystalline cores with slices of old ocean floor along the line (the distance the rock counts from is then the join's
			// or the suture's)
			double oP = cont ? GUp[i] * Bump(d, 170 * w) : 0;
			double oJ = cont ? 0.8 * Bump(GJoinDist[i] * C, 100 * Belt) * (1 - Math.Min(1, GUp[i])) : 0;
			double oS = cont ? 0.7 * Bump(SutureDist[i] * C, 120 * Belt) * (1 - Math.Min(1, GUp[i])) : 0;
			Oro[i] = Math.Max(oP, Math.Max(oJ, oS));
			if (oJ > oP && oJ >= oS) BdB[i] = GJoinDist[i] * C; else if (oS > oP && oS > oJ) BdB[i] = SutureDist[i] * C;
			ArcC[i] = cont ? GCoast[i] * Bump(d - 50 * w, 120 * w) : 0;       // the volcanic arc over a sinking ocean
			Isl[i] = !cont ? GArc0[i] * Bump(d - 18 * w, 45 * w) : 0;         // an island arc
			RiftZ[i] = cont ? GRift0[i] * Bump(d, 70 * w) : 0;                // a rift
			MidZ[i] = !cont ? GMid0[i] * Bump(d, 70 * w) : 0;                 // a ridge under the sea
			double hot = 0;
			for (int k = 0; k < HotX.Length; k++) { double dx = (u - HotX[k]) * C, dz = (v - HotZ[k]) * C; hot = Math.Max(hot, Bump(Math.Sqrt(dx * dx + dz * dz), 80 * Wide)); }
			foreach (var tr in Trails) { double dx = (u - tr[0]) * C, dz = (v - tr[1]) * C; hot = Math.Max(hot, (1 - 0.12 * tr[2]) * Bump(Math.Sqrt(dx * dx + dz * dz), 80 * Wide * (1 - 0.06 * tr[2]))); }   // (Draft 2i: the trails' volcanic rock)
			Hot[i] = hot;
			// an old continent's core (Draft 2i: the middle of an old block, away from its sutures, joins and coasts)
			Craton[i] = cont ? Smoothstep(0.03 * N, 0.09 * N, Math.Min(SutureDist[i], Math.Min(GOceanDist[i], GJoinDist[i]))) * (1 - Oro[i]) * (1 - ArcC[i]) * (1 - RiftZ[i]) : 0;
			Basin[i] = cont ? Smoothstep(0.45, 0.62, Fbm(u / (N * 0.09), v / (N * 0.09), 3, 7001)) * (1 - Oro[i]) : 0;  // a big sunken basin
			Margin[i] = cont ? (1 - Smoothstep(0, 160 * Belt * (1 + 0.8 * RiftBorn[i]), GOceanDist[i] * C)) * (1 - ArcC[i]) : GShelf[i];   // a quiet coast, a shelf (wider where rift-born)
			// how thick the beds are (blocks): thin on old cores and the deep ocean floor, thick in basins, along quiet coasts and in rifts
			SedT[i] = cont ? A * (5 + 10 * (1 - Craton[i]) + 32 * Basin[i] + 22 * Margin[i] * (1 + 0.6 * RiftBorn[i]) + 10 * RiftZ[i]) : A * (1.5 + 3 * Margin[i]) * (1 - MidZ[i]);
			// the beds' frame (the level the stack of beds is counted from: flat on the continents, at the land's level without its
			// ranges) and their base, that thickness below it. Over a collision range the frame rises by a third of the range's
			// smooth height (the beds tilt up its flanks; the folds come on top, per block), while the base rises by all of it and
			// more toward the core, so the range's crystalline heart comes to the surface. Out at sea the thin beds lie on the sea
			// floor. MetaTop: the range's smooth top, that heat and pressure are counted down from.
			double m = Oro[i] * Oro[i], land = Smoothstep(-100, 60, ContF[i]), level = Room + reg[i] - rlb[i];
			Frame[i] = land * (level + 0.35 * rl[i]) + (1 - land) * (Room + esm[i]);
			Datum[i] = land * (level - SedT[i] + m * (SedT[i] + 35 * A + rl[i])) + (1 - land) * (Room + esm[i] - SedT[i]);
			MetaTop[i] = level + rl[i];
		}
	}

	// the plan's box blur (each cell the average of those within r along x, then along z, cut off at the map's edge), done with
	// running sums along each line so it costs the same for any r (the sums are kept in the order written, in both versions)
	void BoxBlur(double[] a, int r)
	{
		var tmp = new double[a.Length]; var pre = new double[N + 1];
		for (int z = 0; z < N; z++)
		{
			pre[0] = 0; for (int x = 0; x < N; x++) pre[x + 1] = pre[x] + a[z * N + x];
			for (int x = 0; x < N; x++) { int lo = Math.Max(0, x - r), hi = Math.Min(N, x + r + 1); tmp[z * N + x] = (pre[hi] - pre[lo]) / (hi - lo); }
		}
		for (int x = 0; x < N; x++)
		{
			pre[0] = 0; for (int z = 0; z < N; z++) pre[z + 1] = pre[z] + tmp[z * N + x];
			for (int z = 0; z < N; z++) { int lo = Math.Max(0, z - r), hi = Math.Min(N, z + r + 1); a[z * N + x] = (pre[hi] - pre[lo]) / (hi - lo); }
		}
	}
	// a cell value at a block, blended between the cell middles
	public double At(double[] f, double bx, double bz)
	{
		double gx = bx / Cell - 0.5, gz = bz / Cell - 0.5;
		int x0 = (int)Math.Floor(gx), z0 = (int)Math.Floor(gz); double fx = gx - x0, fz = gz - z0;
		int xa = Math.Clamp(x0, 0, N - 1), xb = Math.Clamp(x0 + 1, 0, N - 1), za = Math.Clamp(z0, 0, N - 1), zb = Math.Clamp(z0 + 1, 0, N - 1);
		double a = f[za * N + xa], b = f[za * N + xb], c = f[zb * N + xa], d = f[zb * N + xb];
		return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fz;
	}
	double Hash3(int x, int y, int z, int s) => Hash(x, z + y * 65537, s);
	double Noise3(double x, double y, double z, int s)
	{
		int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y), zi = (int)Math.Floor(z);
		double fx = x - xi, fy = y - yi, fz = z - zi;
		double u = fx * fx * (3 - 2 * fx), v = fy * fy * (3 - 2 * fy), w = fz * fz * (3 - 2 * fz);
		double L(double p, double q, double t) => p + (q - p) * t;
		double c00 = L(Hash3(xi, yi, zi, s), Hash3(xi + 1, yi, zi, s), u), c10 = L(Hash3(xi, yi + 1, zi, s), Hash3(xi + 1, yi + 1, zi, s), u);
		double c01 = L(Hash3(xi, yi, zi + 1, s), Hash3(xi + 1, yi, zi + 1, s), u), c11 = L(Hash3(xi, yi + 1, zi + 1, s), Hash3(xi + 1, yi + 1, zi + 1, s), u);
		return L(L(c00, c10, v), L(c01, c11, v), w);
	}
	static double Wave(double t) { double f = t - Math.Floor(t), g = 2 * f - 1; return 1 - 2 * g * g; }   // (-1 to 1 and back, once per 1)
	static double Frac(double t) => t - Math.Floor(t);

	public int GroundAt(int bx, int bz) => (int)Math.Floor(Room + At(Elev, bx + 0.5, bz + 0.5) + 5 * (Fbm(bx / 30.0, bz / 30.0, 3, 7090) - 0.47));

	// a column's rock settings (everything that doesn't change with height), worked out once per column
	public struct GeoCol
	{
		public int i, plate; public double X, Z, cf, oro, arc, isl, rift, hot, crat, basin, marg, temp, w, dB, frameY, baseY, meta, g0;
		public double allu, loess, mor, scree;
	}
	public GeoCol ColAt(int bx, int bz)
	{
		var c = new GeoCol(); double X = bx + 0.5, Z = bz + 0.5, A = Amp; c.X = X; c.Z = Z;
		c.i = Math.Clamp(bz / Cell, 0, N - 1) * N + Math.Clamp(bx / Cell, 0, N - 1); c.plate = Plate[c.i];
		c.cf = At(ContF, X, Z); c.oro = At(Oro, X, Z); c.arc = At(ArcC, X, Z); c.isl = At(Isl, X, Z); c.rift = At(RiftZ, X, Z); c.hot = At(Hot, X, Z);
		c.crat = At(Craton, X, Z); c.basin = At(Basin, X, Z); c.marg = At(Margin, X, Z); c.temp = At(Temp0 ?? Temp, X, Z);
		c.w = At(GSz, X, Z); c.dB = At(BdB, X, Z); c.meta = At(MetaTop, X, Z);
		c.g0 = Room + At(Elev0 ?? Elev, X, Z);   // (the ground before erosion: the volcanic piles lie on it)
		// the beds: flat, except near a collision range (strongly folded in waves along it, up to about 34 x Amp up and down),
		// a rift (gently tilted blocks, broken at faults) and an arc (gentle waves)
		double fold = c.oro * (4 + 30 * c.oro) * A * Wave(c.dB / (90 * c.w) + 0.6 * Fbm(X / 200, Z / 200, 2, 7007));
		fold += c.rift * 6 * A * (Frac(c.dB / (70 * c.w) + 0.5 * Fbm(X / 300, Z / 300, 2, 7008)) - 0.5);
		fold += c.arc * 5 * A * Wave(c.dB / (150 * c.w) + 0.5 * Fbm(X / 300, Z / 300, 2, 7009));
		c.frameY = At(Frame, X, Z) + fold; c.baseY = At(Datum, X, Z) + fold;
		if (Alluvium != null) { c.allu = At(Alluvium, X, Z); c.loess = At(Loess, X, Z); c.mor = At(Moraine, X, Z); c.scree = At(ScreeT, X, Z); }
		return c;
	}

	public Rock RockAt(int bx, int y, int bz, int yg) => RockAt(ColAt(bx, bz), bx, y, bz, yg);
	public Rock RockAt(in GeoCol c, int bx, int y, int bz, int yg)
	{
		if (y > yg) return y <= Room ? Rock.Water : Rock.Air;
		if (y <= 1 + (int)Math.Floor(Hash(bx, bz, 7091) * 3)) return Rock.Bedrock;
		int s = yg - y;
		// the top: the soil, and what erosion laid down
		var top = SoilAt(c, bx, bz, yg, s);
		if (top != Rock.Air) return top;
		return Deep(c, bx, y, bz, true);
	}

	// the ground's top few blocks: what the rivers, the ice and the wind laid down, else the soil the climate and the rock
	// beneath make (Air: none here, the rock itself)
	public Rock SoilAt(in GeoCol c, int bx, int bz, int yg, int s)
	{
		int i = c.i, b = Biome[i]; int soil = 2 + (int)Math.Floor(Hash(bx, bz, 7092) * 2);
		// (sand on the beaches: low ground within about 20-30 blocks of the sea, or a few blocks of a lake)
		if (yg <= Room + 2 && (At(SeaDist, bx + 0.5, bz + 0.5) * Cell < 20 + 12 * Hash(bx >> 3, bz >> 3, 7094) || At(LakeDist, bx + 0.5, bz + 0.5) * Cell < 5))
			return s < soil ? Rock.Sand : Rock.Air;
		if (c.loess >= 1 && s < c.loess + 1) return Rock.Loess;
		if (c.allu >= 1.5 && s < Math.Min(c.allu, 12)) return s < 2 && Temp[i] >= 5 && Rain[i] >= 0.2 ? Rock.RichSoil : Rock.Silt;
		if (c.mor >= 1 && s < Math.Min(c.mor + 1, 10)) return Rock.GlacialTill;
		if (c.scree >= 3 && s < Math.Min(c.scree, 6)) return Rock.Scree;
		if (s >= soil + (b == Rainforest || b == Savanna ? 6 : 0)) return Rock.Air;
		if (b == Rainforest || b == Savanna) return s < soil ? Rock.Laterite : Noise3(bx / 4.0, (yg - s) / 3.0, bz / 4.0, 7093) > 0.62 ? Rock.Bauxite : Rock.Laterite;
		double sl = Slope(i % N, i / N), r = Rain[i], t = Temp[i];
		if (b == Swamp) return s < 1 ? Rock.Mud : Rock.Peat;
		if (b == Tundra || b == ColdWaste) return s < 1 ? Rock.StonySoil : Rock.Permafrost;
		if (b == Glacier || b == Peaks) return Rock.Air;
		if (sl > 0.6) return Rock.Air;   // (too steep: bare rock)
		if (b == Alpine || b == Crags || sl > 0.45) return Rock.StonySoil;
		if (IceWas != null && IceWas[i] >= 0.3 && sl < 0.1 && t > -4) return Rock.GlacialTill;   // (the old ice's ground)
		var parent = Deep(c, bx, yg - soil - 1, bz, false);
		bool volcanic = parent == Rock.Basalt || parent == Rock.Andesite || parent == Rock.Rhyolite;
		bool lime = parent == Rock.Limestone || parent == Rock.Dolomite || parent == Rock.Marble || parent == Rock.Chalk;
		bool sandy = parent == Rock.Sandstone || parent == Rock.Granite || parent == Rock.Conglomerate || parent == Rock.Diorite;
		bool clayey = parent == Rock.Shale || parent == Rock.Siltstone || parent == Rock.Slate || parent == Rock.Phyllite;
		if (b == Desert || b == HotWaste || b == Badlands) return s < soil ? Rock.DesertSoil : Rock.Air;
		if (volcanic && r >= 0.35) return Rock.VolcanicSoil;
		if (b == DryScrub || b == DryForest) return lime || r < 0.3 ? Rock.RedEarth : sandy ? Rock.SandySoil : Rock.Soil;
		if (b == Taiga || b == Snowy) return Rock.AshenSoil;
		if (b == Plains || b == Meadow) return sandy ? Rock.SandySoil : Rock.PrairieSoil;
		if (sandy) return Rock.SandySoil;
		if (clayey) return Rock.ClaySoil;
		return Rock.Soil;   // (today's Dirt: the woods' brown soil)
	}

	// the rock body at a height (not the soil): independent of the ground as it is now, so wearing the land down shows deeper rock
	public Rock Deep(in GeoCol c, int bx, int y, int bz, bool ores)
	{
		double A = Amp, X = c.X, Z = c.Z; int plate = c.plate;
		double oro = c.oro, arc = c.arc, isl = c.isl, rift = c.rift, hot = c.hot, crat = c.crat, basin = c.basin, marg = c.marg, temp = c.temp;
		// (continental crust: at the sea's level out to the plate's edge, deeper down further in, so the continent's crust slopes
		// away under its coast instead of standing in a wall)
		double T = Tall;
		bool cont = c.cf > Math.Max(0, Room - y) / 6.0, contTop = c.cf > 0;
		// laid down by erosion above the old ground (thick fills, deltas, loess, moraines)
		if (y > c.g0 - 0.5) return c.loess >= c.allu && c.loess >= c.mor && c.loess > 0 ? Rock.Loess : c.mor > c.allu ? Rock.GlacialTill : c.allu > 0 ? Rock.Silt : Rock.Gravel;
		double s0 = c.g0 - y;
		// granite bodies (plutons): big blobs in the continents' crust, more under arcs and ranges and in old cores
		double pl = Noise3(X / 110.0, y / (70.0 * T), Z / 110.0, 7020), thr = 0.68 - 0.24 * arc - 0.12 * oro - 0.06 * crat;
		double pe = pl - thr;   // (above 0: granite; just above: its edge, where ores gather)
		// volcanic rock on top of the old ground: a hot spot's basalt (on a continent with layers of rhyolite), an arc's andesite
		// (rhyolite in its top third now and then), an island arc's andesite and basalt
		if (s0 < hot * 55 * A)
			return contTop && Hash(plate, (int)Math.Floor(y / (10.0 * T)), 7012) < 0.3 ? Rock.Rhyolite : ores ? BasaltOres(bx, y, bz) : Rock.Basalt;
		if (s0 < arc * 40 * A)
		{
			if (ores && arc > 0.25 && pe > -0.06 && Noise3(X / 5, y / 5.0, Z / 5, 7031) > 0.76) return Rock.CopperOre;   // (copper in the andesite over a granite body)
			if (ores && arc > 0.25 && pe > -0.09 && Noise3(X / 5, y / 5.0, Z / 5, 7032) > 0.86) return Rock.SilverOre;
			return s0 < arc * 14 * A && Hash(plate, (int)Math.Floor(y / (9.0 * T)), 7013) < 0.3 ? Rock.Rhyolite : Rock.Andesite;
		}
		if (s0 < isl * 35 * A) return Hash(plate, (int)Math.Floor(y / (8.0 * T)), 7005) < 0.5 ? (ores ? BasaltOres(bx, y, bz) : Rock.Basalt) : Rock.Andesite;
		double frameY = c.frameY, baseY = c.baseY;
		if (y >= baseY && (!cont || !(pe > 0.004 * (y - baseY) && y - baseY < 40 * A * (arc + 0.5 * oro))))
		{
			// (one stack of beds for the whole world, counted from the frame (up above it under hills, down below it to the base),
			// so the beds run on flat across plates; each bed 5.5-11.5 blocks thick; which rock a bed is follows the setting, so a
			// bed changes from sandstone to limestone and so on gradually)
			double sF = y - frameY;
			double sT = sF / T;   // (Draft 2j: the beds as thick as the world is tall, Tall x 5.5-11.5 blocks: the same beds at every size)
			int k = (int)Math.Floor(sT / 7.5 + 0.1 * Wave(sT / 31 + 0.37) + 0.035 * Wave(sT / 13 + 0.71));
			bool young = k >= -2, oldest = y - baseY < 15 * T;
			double warm = Smoothstep(8, 22, temp);
			Rock bed;
			if (!contTop) bed = warm > 0.5 && marg > 0.2 ? Rock.Chalk : Rock.Shale;   // (the sea floor's ooze and mud)
			else if (crat > 0.35 && oldest && Hash(k, 0, 7004) < 0.6)
				return ores && y % 3 == 0 && Hash3(bx, y, bz, 7040) > 0.3 ? Rock.IronOre : Rock.Shale;   // (banded iron, in old cores' oldest beds)
			else if (rift > 0.3 && Hash(k, 0, 7006) < 0.4 * rift) return ores ? BasaltOres(bx, y, bz) : Rock.Basalt;   // (lava flows between a rift's beds)
			else
			{
				// conglomerate (rounded pebbles) where rivers came fast off young mountains (a range's foreland) and down a rift's
				// sides; siltstone everywhere a little
				double wSand = 0.28 + 0.4 * rift + 0.2 * marg * (1 - warm), wCong = 0.45 * Bump(oro - 0.22, 0.2) + 0.3 * rift, wSilt = 0.18;
				double wShale = 0.38 + 0.1 * (1 - warm), wLime = 0.12 + 0.38 * warm * (0.5 + 0.5 * Math.Max(marg, basin)), wChalk = young ? 0.45 * warm * marg : 0;
				// (the picks step on by about 0.6 from one bed to the next, so neighbouring beds tend to differ, in cycles like real
				// ones; each bed's pick wanders a little from place to place, so where the setting changes, beds change rock at different
				// places, in long thin wedges (20 blocks along for each block up), interfingering, instead of all at one line)
				double pick = Math.Clamp(Frac(0.618 * k + 0.45 * Hash(k, 0, 7003)) + 0.22 * (ValueNoise((X + 20 * sF) / 200 + 13.7 * k, Z / 200, 7010) - 0.5), 0, 0.9999)
					* (wSand + wCong + wSilt + wShale + wLime + wChalk);
				bed = pick < wSand ? Rock.Sandstone : (pick -= wSand) < wCong ? Rock.Conglomerate : (pick -= wCong) < wSilt ? Rock.Siltstone
					: (pick -= wSilt) < wShale ? Rock.Shale : (pick -= wShale) < wLime ? Rock.Limestone : Rock.Chalk;
				// (old limestone turns to dolomite: on old cores and deep in the stack)
				if (bed == Rock.Limestone && (crat > 0.3 || k <= -6) && Hash(k, 1, 7011) < 0.5) bed = Rock.Dolomite;
			}
			// heat and pressure in a collision range: deeper and nearer its core, the beds turn to metamorphic rock
			double g = oro * (0.35 + 0.85 * Math.Clamp((c.meta - y) / (35 * A), 0, 2));   // (hotter deeper below the range's smooth top, not below the ground: no draping; still rising well down, so the edges slope)
			if (bed == Rock.Shale || bed == Rock.Siltstone) bed = g >= 0.7 ? Rock.Gneiss : g >= 0.42 ? Rock.Schist : g >= 0.33 ? Rock.Phyllite : g >= 0.25 ? Rock.Slate : bed;
			else if ((bed == Rock.Sandstone || bed == Rock.Conglomerate) && g >= 0.38) bed = Rock.Quartzite;
			else if ((bed == Rock.Limestone || bed == Rock.Chalk || bed == Rock.Dolomite) && g >= 0.35) bed = Rock.Marble;
			// (baked hard beside a granite body: hornfels)
			if (cont && pe > -0.04 && pe <= 0 && arc + oro > 0.1 && bed != Rock.Marble && bed != Rock.Quartzite && bed != Rock.Gneiss) bed = Rock.Hornfels;
			if (!ores) return bed;
			if (bed == Rock.Chalk && Noise3(X / 4, y / 3.0, Z / 4, 7042) > 0.8) return Rock.FlintNodules;   // (flint in the chalk)
			if (bed == Rock.Limestone && oldest && (basin > 0.3 || crat > 0.3) && Noise3(X / 5, y / 4.0, Z / 5, 7038) > 0.84) return Rock.SilverOre;   // (silver with lead)
			if (bed == Rock.Schist && GoldVein(bx, y, bz)) return Hash3(bx, y, bz, 7041) < 0.35 ? Rock.GoldOre : Rock.Schist;
			return bed;
		}
		// under the beds: the ocean's crust (basalt, then gabbro, then the mantle's peridotite deep down), or the continents'
		// (gneiss, with granite and diorite bodies, gabbro deep, and old ocean floor caught where continents met)
		if (!cont)
		{
			if (y > baseY - 15 * A) return ores ? BasaltOres(bx, y, bz) : Rock.Basalt;
			if (y < (30 + 15 * Noise3(X / 80, 0.5, Z / 80, 7401)) * T) return Rock.Peridotite;
			return contTop && ores ? GabbroOres(bx, y, bz) : Rock.Gabbro;   // (no ores in the deep ocean crust: out of reach)
		}
		if (oro > 0.6 && c.dB < 25 * c.w && Noise3(X / 50, y / (35.0 * T), Z / 50, 7400) > 0.64) return y > 200 * T ? Rock.Serpentinite : Rock.Peridotite;   // (a suture's old ocean floor)
		if (pe > 0)
		{
			bool dio = arc > 0.3 && Noise3(X / 90, y / (60.0 * T), Z / 90, 7023) > 0.45;   // (under arcs: diorite as well as granite)
			if (!ores) return dio ? Rock.Diorite : Rock.Granite;
			if (GoldVein(bx, y, bz)) { double hv = Hash3(bx, y, bz, 7041); if (hv < 0.35) return Rock.GoldOre; if (hv < 0.45) return Rock.SilverOre; }
			if (arc > 0.2)
			{   // (a porphyry: copper at a granite or diorite body's edge under an arc, silver a little further in)
				if (pe < 0.035 && Noise3(X / 5, y / 5.0, Z / 5, 7031) > 0.72) return Rock.CopperOre;
				if (pe < 0.06 && Noise3(X / 5, y / 5.0, Z / 5, 7032) > 0.86) return Rock.SilverOre;
			}
			else if (pe < 0.05 && Noise3(X / 5, y / 5.0, Z / 5, 7033) > 0.8) return Rock.TinOre;   // (tin at the edges of granite under ranges and old cores)
			return dio ? Rock.Diorite : Rock.Granite;
		}
		if (y < (70 + 30 * Noise3(X / 90, 0.5, Z / 90, 7022)) * T && Noise3(X / 70, y / (40.0 * T), Z / 70, 7021) > 0.45) return ores ? GabbroOres(bx, y, bz) : Rock.Gabbro;
		return Rock.Gneiss;
	}
	public void DebugCol(int x, int z)
	{
		if (Oro == null) MakeGeo();
		int g = GroundAt(x, z); double X = x + 0.5, Z = z + 0.5, w = At(GSz, X, Z), dB = At(BdB, X, Z), oro = At(Oro, X, Z);
		double fold = oro * (4 + 30 * oro) * Amp * Wave(dB / (90 * w) + 0.6 * Fbm(X / 200, Z / 200, 2, 7007));
		Godot.GD.Print($"GEOCOL x {x} z {z}: ground {g}, frame {At(Frame, X, Z):0.0}, datum {At(Datum, X, Z):0.0}, fold {fold:0.0}, oro {oro:0.00}, dB {dB:0}, w {w:0.00}, metatop {At(MetaTop, X, Z):0.0}, plate {Plate[Math.Clamp(z / Cell, 0, N - 1) * N + Math.Clamp(x / Cell, 0, N - 1)]}, other {Other[Math.Clamp(z / Cell, 0, N - 1) * N + Math.Clamp(x / Cell, 0, N - 1)]}, rift {At(RiftZ, X, Z):0.00}");
		var sb = new System.Text.StringBuilder();
		for (int y = g; y > g - 100; y -= 3) sb.Append($"{y}:{RockAt(x, y, z, g)} ");
		Godot.GD.Print("GEOCOL " + sb);
	}
	bool GoldVein(int bx, int y, int bz) => Math.Abs(Noise3(bx / 50.0, y / 50.0, bz / 50.0, 7034) - 0.5) < 0.006 && Noise3(bx / 16.0, y / 16.0, bz / 16.0, 7039) > 0.58;
	Rock BasaltOres(int bx, int y, int bz)
	{
		if (Noise3(bx / 5.0, y / 5.0, bz / 5.0, 7402) > 0.86) return Rock.CopperOre;   // (native copper)
		if (Noise3(bx / 5.0, y / 5.0, bz / 5.0, 7036) > 0.88) return Rock.IronOre;
		return Rock.Basalt;
	}
	Rock GabbroOres(int bx, int y, int bz) => Noise3(bx / 5.0, y / 5.0, bz / 5.0, 7037) > 0.88 ? Rock.IronOre : Rock.Gabbro;   // (magnetite)

	public static readonly uint[] RockColors =
	{
		0xbfe3ff, 0x2f62b8, 0x6b4a2c, 0xe8d79a, 0x8a8a8a, 0xd29a86, 0x34343a, 0x223028, 0xd9cfa8, 0xf6f3ea, 0x5f5a52, 0xcfa86a,
		0x48586c, 0x7e9178, 0xa3948c, 0xe9edf3, 0xe2c6c0, 0x101010, 0xb2482a, 0xd98b56, 0xff7a1a, 0x30e0ff, 0xff2040, 0xffd700, 0xb070ff,
		0xa08c70, 0x8c8270, 0xd8c8b0, 0xefe3c8, 0xb8b4ae, 0xc98f8a, 0x5f7a3a, 0x6d7f8a, 0x2f6b4f, 0x3d3a38, 0x1d1d26,
		0x2b231c, 0x9c9890, 0x9e3b22, 0xc9a65f, 0xd9c7a0, 0x8a6a50, 0xb49a6a, 0x7c7066, 0x3a3030, 0x8f8a80, 0x3e2e20, 0xb5a88e, 0x9a9590,
		0x8c8680, 0x3a2a1a, 0x4d3a2a, 0x7d8c94
	};

	// the cross-section pictures: the whole width along one row (2 blocks a pixel across, 1 a pixel down), and a closer look
	// (1 block = 2 x 2 pixels) where the most is going on; returns (the row, the close look's left edge, its bottom, its top)
	public (int row, int x0, int y0, int y1) SaveSlices(string dir, int forceRow = -1, string tag = "", bool collide = false)
	{
		if (Oro == null) MakeGeo();
		// the row: the one crossing the most settings, with land on 30-75% of it
		int bestRow = 0; double bestScore = -1;
		for (int z = N / 10; z < N - N / 10; z++)
		{
			double sc = 0; int land = 0;
			for (int x = 0; x < N; x++)
			{
				int i = z * N + x; if (!Sea[i]) land++;
				sc += collide ? Oro[i] * Oro[i] : Oro[i] * 2 + ArcC[i] * 2 + Isl[i] + RiftZ[i] * 1.5 + Hot[i] + Basin[i] * 0.6 + Margin[i] * 0.3 + Craton[i] * 0.4;
			}
			if (land < 0.3 * N || land > 0.75 * N) continue;
			if (sc > bestScore) { bestScore = sc; bestRow = z; }
		}
		if (forceRow >= 0) bestRow = forceRow / Cell;
		int bz = bestRow * Cell + Cell / 2;
		var yg = new int[Size]; int top = 0;
		for (int x = 0; x < Size; x++) { yg[x] = GroundAt(x, bz); top = Math.Max(top, yg[x]); }
		int yTop = Math.Min(Room * 2 - 1, top + 12);
		var counts = new long[RockColors.Length];
		Godot.Color Col(Rock r) { uint c = RockColors[(int)r]; return new Godot.Color(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f); }
		bool IsOre(Rock r) => r >= Rock.CopperOre && r <= Rock.SilverOre || r == Rock.Bauxite || r == Rock.FlintNodules;
		// the whole row
		int W = Size / 2, H = yTop + 1;
		var img = Godot.Image.CreateEmpty(W, H, false, Godot.Image.Format.Rgb8);
		var ores = new List<(int, int, Rock)>();
		for (int px = 0; px < W; px++)
		{
			int x = px * 2;
			for (int y = 0; y <= yTop; y++)
			{
				var r = RockAt(x, y, bz, yg[x]); counts[(int)r]++;
				img.SetPixel(px, yTop - y, Col(IsOre(r) ? (y > yg[x] - 8 ? Rock.Laterite : Rock.Gneiss) : r));
				if (IsOre(r)) ores.Add((px, yTop - y, r));
			}
		}
		foreach (var (px, py, r) in ores) for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
		{
			int X = px + dx, Y = py + dy;
			if (X >= 0 && Y >= 0 && X < W && Y < H && (r != Rock.Bauxite || (dx == 0 && dy == 0))) img.SetPixel(X, Y, Col(r));
		}
		// (sea level marked at the edges)
		for (int k = 0; k < 6; k++) { img.SetPixel(k, yTop - Room, new Godot.Color(1, 1, 1)); img.SetPixel(W - 1 - k, yTop - Room, new Godot.Color(1, 1, 1)); }
		img.SavePng($"{dir}/geology_slice{tag}.png");
		// the close look: 400 blocks around the strongest collision range or arc on the row
		int cx = Size / 2; double cBest = -1;
		for (int x = 200; x < Size - 200; x += 4) { double v = collide ? At(Oro, x, bz) : At(Oro, x, bz) * 1.2 + At(ArcC, x, bz) + At(RiftZ, x, bz) * 0.8 + At(Hot, x, bz) * 0.8; if (v > cBest) { cBest = v; cx = x; } }
		int x0 = Math.Clamp(cx - 200, 0, Size - 400);
		(int, int) Zoom(int zz, int[] gy, int x0, string name)
		{
			int lo = int.MaxValue, hi = 0;
			for (int x = x0; x < x0 + 400; x++) { lo = Math.Min(lo, gy[x]); hi = Math.Max(hi, gy[x]); }
			int y1 = Math.Min(yTop, hi + 8), y0 = Math.Max(0, Math.Min(lo, Room) - (int)(170 * Tall));
			var zoom = Godot.Image.CreateEmpty(800, (y1 - y0 + 1) * 2, false, Godot.Image.Format.Rgb8);
			for (int x = x0; x < x0 + 400; x++) for (int y = y0; y <= y1; y++)
			{
				var c = Col(RockAt(x, y, zz, gy[x]));
				int px = (x - x0) * 2, py = (y1 - y) * 2;
				zoom.SetPixel(px, py, c); zoom.SetPixel(px + 1, py, c); zoom.SetPixel(px, py + 1, c); zoom.SetPixel(px + 1, py + 1, c);
			}
			zoom.SavePng($"{dir}/{name}{tag}.png");
			return (y0, y1);
		}
		var (y0, y1) = Zoom(bz, yg, x0, "geology_zoom");
		if (!collide)
		{   // (and, anywhere in the world, the hilliest 400 blocks of land well away from ranges, arcs, rifts and hot spots: the flat
			// beds, cut by the valleys)
			var E0h = Elev0 ?? Elev; int win = 400 / Cell, hz = -1, hx = -1; double hBest = -1;
			for (int z = N / 10; z < N - N / 10; z += 2) for (int x = 0; x + win <= N; x += 2)
			{
				double lo = 1e9, hi = -1e9, quiet = 0; int landN = 0;
				for (int k = x; k < x + win; k++)
				{
					int i = z * N + k; lo = Math.Min(lo, E0h[i]); hi = Math.Max(hi, E0h[i]); if (E0h[i] > 0) landN++;
					quiet = Math.Max(quiet, Math.Max(Math.Max(Oro[i], ArcC[i]), Math.Max(Math.Max(RiftZ[i], Hot[i]), Isl[i])));
				}
				if (quiet < 0.1 && landN >= win * 0.9 && hi - lo > hBest) { hBest = hi - lo; hz = z; hx = x; }
			}
			if (hz >= 0)
			{
				int zb = hz * Cell + Cell / 2, xb = hx * Cell; var gy = new int[Size];
				for (int x = xb; x < xb + 400; x++) gy[x] = GroundAt(x, zb);
				var (h0, h1) = Zoom(zb, gy, xb, "geology_zoom_hills");
				Godot.GD.Print($"GEOHILLS row z {zb}, x {xb}-{xb + 399}, y {h0}-{h1}: ground {hBest:0} blocks up and down");
			}
			{   // (testing: the 400 blocks of land erosion wore down most, or the row given in GEOWORN="z,x", for before and after)
				int wz = -1, wx = -1; double wBest = 0;
				if (System.Environment.GetEnvironmentVariable("GEOWORN") is { } gw) { var q = gw.Split(','); wz = int.Parse(q[0]) / Cell; wx = int.Parse(q[1]) / Cell; }
				else if (Wear != null)
					for (int z = 0; z < N; z += 2) for (int x = 0; x + win <= N; x += 2)
					{
						double sum = 0; for (int k = x; k < x + win; k++) { int i = z * N + k; if (!Sea[i]) sum += Math.Max(0, Wear[i]); }
						if (sum > wBest) { wBest = sum; wz = z; wx = x; }
					}
				if (wz >= 0)
				{
					int zb = wz * Cell + Cell / 2, xb = wx * Cell; var gy = new int[Size];
					for (int x = xb; x < xb + 400; x++) gy[x] = GroundAt(x, zb);
					var (h0, h1) = Zoom(zb, gy, xb, "geology_zoom_worn");
					Godot.GD.Print($"GEOWORN row z {zb}, x {xb}-{xb + 399}, y {h0}-{h1}: worn {wBest * Cell / 400:0.0} blocks on average");
				}
			}
		}
		long rocks = 0; for (int k = (int)Rock.Andesite; k < counts.Length; k++) if (k != (int)Rock.Bedrock) rocks += counts[k];
		var parts = new List<string>();
		for (int k = (int)Rock.Andesite; k < counts.Length; k++) if (counts[k] > 0 && k != (int)Rock.Bedrock) parts.Add($"{(Rock)k} {100.0 * counts[k] / rocks:0.00}%");
		Godot.GD.Print($"GEOSLICE{tag} row z {bz} (cell row {bestRow}); close look x {x0}-{x0 + 399}, y {y0}-{y1}; rock under the ground: {string.Join(", ", parts)}");
		return (bz, x0, y0, y1);
	}

	// (Draft 2j, testing: one range cut across, the same at every size: a quarter of the map's width round a plan cell (given, or
	// the one the plates raised most), 512 pixels across and 2 blocks a pixel down at every size, so a taller world's range
	// stands taller in the picture. Rock colours; sky pale blue; the sea dark blue)
	public void SaveRangeCut(string dir, int cx = -1, int cz = -1)
	{
		if (Oro == null) MakeGeo();
		if (cx < 0) { double best = -1e9; for (int i = 0; i < N * N; i++) { double v = RangeYoung[i] + RangeMid[i]; if (v > best) { best = v; cx = i % N; cz = i / N; } } }
		int win = Size / 4, bz = cz * Cell + Cell / 2, x0 = Math.Clamp(cx * Cell + Cell / 2 - win / 2, 0, Size - win);
		const int W = 512; var gy = new int[W]; var xs = new int[W]; int top = 0, lo = int.MaxValue;
		for (int px = 0; px < W; px++) { xs[px] = x0 + (int)Math.Floor((px + 0.5) * win / (double)W); gy[px] = GroundAt(xs[px], bz); top = Math.Max(top, gy[px]); lo = Math.Min(lo, gy[px]); }
		int yHi = top + 16, yLo = Math.Max(0, Math.Min(lo, Room) - (int)(60 * Tall));
		if (System.Environment.GetEnvironmentVariable("CUTSPAN") is { } cspan) { var cq = cspan.Split(','); yHi = Room + int.Parse(cq[0]); yLo = Room - int.Parse(cq[1]); }   // (testing: the same blocks above and below the sea in every picture)
		int H = (yHi - yLo) / 2 + 1;
		var img = Godot.Image.CreateEmpty(W, H, false, Godot.Image.Format.Rgb8);
		for (int px = 0; px < W; px++)
		{
			var col = ColAt(xs[px], bz);
			for (int py = 0; py < H; py++)
			{
				int y = yHi - py * 2; uint c = RockColors[(int)RockAt(col, xs[px], y, bz, gy[px])];
				img.SetPixel(px, py, new Godot.Color(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f));
			}
		}
		for (int k = 0; k < 8; k++) { int py = (yHi - Room) / 2; img.SetPixel(k, py, new Godot.Color(1, 1, 1)); img.SetPixel(W - 1 - k, py, new Godot.Color(1, 1, 1)); }
		img.SavePng($"{dir}/range_cut.png");
		Godot.GD.Print($"GEOCUT cell {cx},{cz}: block x {x0}-{x0 + win - 1} at z {bz}; the ground {lo - Room} to {top - Room} above the sea (room {Room}); picture {W} x {H}, {win / (double)W:0.##} blocks a pixel across, 2 down");
	}
}
