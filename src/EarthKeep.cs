using System;
using System.Collections.Generic;

namespace Cubeland;

// Earthlike worlds: what the plan keeps for play time (the FREEZE, Earthly Extravaganza). Once the plan is made:
//   1. PLANHASH is taken (the plan as made, in doubles);
//   2. every KEEP array of doubles is rounded ONCE to float32 (each value stays a double, read as one); nothing else is rounded;
//   3. GravelRock is worked out, from the KEEP arrays only (so from exactly what play time reads);
//   4. KEEPHASH is taken over the KEEP arrays, in the order below;
//   5. every other cell-sized array is dropped (it was only for making the plan), unless KeepAll (testing: the pictures).
// The plan is never saved: it's made again from the seed each time a world opens, so both editions must make the same numbers.
public sealed partial class EarthPlan
{
	public static bool KeepAll = false;   // (testing: keep every array for the pictures)
	public uint PlanHash, KeepHash;

	// the KEEP arrays, in KEEPHASH order (the order is part of the freeze)
	double[][] KeepDoubles() => new[] {
		Elev, Elev0, Temp, Temp0, Rain, Swing, Season, Humid, Inland, Lat,
		Cur, CX, CZ, WX, WZ, MX, MZ,
		Level, Flow, Braid, SeaDist, RiverDist, LakeDist, BigDist,
		RiverPX, RiverPZ, RiverSX, RiverSZ, RiverEX, RiverEZ,
		Wear, IceWas, Trough, Moraine, Alluvium, Loess, Karst, Dune, ScreeT, SurfK,
		Oro, ArcC, Isl, RiftZ, Hot, Craton, Basin, Margin, Datum, Frame, MetaTop, BdB, ContF, GSz };
	int[][] KeepInts() => new[] { Plate, Down, Order, RiverUp };
	byte[][] KeepBytes() => new[] { Biome, Coast, GravelRock };
	bool[][] KeepBools() => new[] { Sea, Lake, Fjord, Head };
	static readonly HashSet<string> KeepNames = new() {
		"Elev", "Elev0", "Temp", "Temp0", "Rain", "Swing", "Season", "Humid", "Inland", "Lat", "Cur", "CX", "CZ", "WX", "WZ", "MX", "MZ",
		"Level", "Flow", "Braid", "SeaDist", "RiverDist", "LakeDist", "BigDist", "RiverPX", "RiverPZ", "RiverSX", "RiverSZ", "RiverEX", "RiverEZ",
		"Wear", "IceWas", "Trough", "Moraine", "Alluvium", "Loess", "Karst", "Dune", "ScreeT", "SurfK",
		"Oro", "ArcC", "Isl", "RiftZ", "Hot", "Craton", "Basin", "Margin", "Datum", "Frame", "MetaTop", "BdB", "ContF", "GSz",
		"Plate", "Down", "Order", "RiverUp", "Biome", "Coast", "GravelRock", "Sea", "Lake", "Fjord", "Head" };
	public int KeepNaN;   // (testing: values that aren't numbers in the KEEP arrays; must be 0, or the two editions could hash them differently)

	void Keep()
	{
		PlanHash = Fingerprint();
		foreach (var a in KeepDoubles())
			if (a != null) for (int i = 0; i < a.Length; i++) { a[i] = (float)a[i]; if (double.IsNaN(a[i])) KeepNaN++; }
		MakeGravelRock();
		KeepHash = KeepFingerprint();
		if (!KeepAll) Drop();
	}

	// FNV-1a 32 over: each double array's float32 bits, then the int arrays, then the byte arrays, then the bool arrays (1 or 0),
	// each array's length first (-1 for a missing one), then the volcano list (its count, then x, z, kind of each)
	public uint KeepFingerprint()
	{
		uint h = 2166136261;
		void Add(int v) { unchecked { h = (h ^ (uint)v) * 16777619; } }
		foreach (var a in KeepDoubles()) { Add(a?.Length ?? -1); if (a != null) foreach (var v in a) Add(BitConverter.SingleToInt32Bits((float)v)); }
		foreach (var a in KeepInts()) { Add(a?.Length ?? -1); if (a != null) foreach (var v in a) Add(v); }
		foreach (var a in KeepBytes()) { Add(a?.Length ?? -1); if (a != null) foreach (var v in a) Add(v); }
		foreach (var a in KeepBools()) { Add(a?.Length ?? -1); if (a != null) foreach (var v in a) Add(v ? 1 : 0); }
		Add(Volcanoes.Count); foreach (var v in Volcanoes) { Add(v[0]); Add(v[1]); Add(v[2]); }
		return h;
	}

	// GRAVELROCK: for each cell, the rock its gravel is made of (a Rock number, one byte): the rock that erosion wore away most of
	// over the cell's whole catchment (the cell and every cell draining into it). Read by the land drawing (2b): laid-down gravel,
	// river bars and braids, alluvial fans, moraines' gravel pockets, shingle at a river mouth.
	// - Each cell's wear (Wear above 0: taken off by water, ice, creep or slides), in whole quarter blocks, floor(Wear x 4 + 0.5),
	//   counts for the rock at the middle of the worn depth: Deep (no ores) at y = floor(g0 - Wear / 2) at the cell's middle block
	//   (bx = x * Cell + Cell / 2, likewise bz), g0 being ColAt's ground before erosion. Laid-down ground found there (loess,
	//   till, silt, gravel: only where the wear is under a block or so) doesn't count.
	// - The sums go downstream along Down, upstream first (Order from the end, as Flow does); the sea passes nothing on.
	// - The pick is the rock with the biggest sum; ties go to the lower Rock number. A cell with nothing worn in its catchment
	//   takes its own top rock: Deep (no ores) at y = floor(g0) - 1.
	// The sums are whole numbers (no rounding questions), at most the whole map's wear (well under 2^31 in every test world).
	public byte[] GravelRock;
	public long GravelMaxSum;   // (testing: the biggest catchment sum, to show the headroom)
	// the rocks that have a gravel (everything Deep gives without ores, except what erosion laid down), in Rock order
	static readonly Rock[] GravelRocks = {
		Rock.Andesite, Rock.Granite, Rock.Basalt, Rock.Gabbro, Rock.Limestone, Rock.Chalk, Rock.Shale, Rock.Sandstone, Rock.Slate,
		Rock.Schist, Rock.Gneiss, Rock.Marble, Rock.Quartzite, Rock.Conglomerate, Rock.Siltstone, Rock.Dolomite, Rock.Travertine,
		Rock.Diorite, Rock.Rhyolite, Rock.Peridotite, Rock.Phyllite, Rock.Serpentinite, Rock.Hornfels };
	void MakeGravelRock()
	{
		int NN = N * N, R = GravelRocks.Length;
		var slot = new int[256]; Array.Fill(slot, -1);
		for (int r = 0; r < R; r++) slot[(int)GravelRocks[r]] = r;
		var sum = new int[NN * R];   // (each cell's catchment so far, per rock; about 24 MB at 512 x 512, freed straight after)
		GravelRock = new byte[NN]; GravelMaxSum = 0;
		for (int k = NN - 1; k >= 0; k--)
		{
			int i = Order[k], bx = i % N * Cell + Cell / 2, bz = i / N * Cell + Cell / 2, o = i * R;
			var c = ColAt(bx, bz);
			if (Wear != null)
			{
				int q = (int)Math.Floor(Wear[i] * 4 + 0.5);
				if (q > 0) { int s = slot[(int)Deep(c, bx, (int)Math.Floor(c.g0 - 0.5 * Wear[i]), bz, false)]; if (s >= 0) sum[o + s] += q; }
			}
			int best = -1, bv = 0;
			for (int r = 0; r < R; r++) if (sum[o + r] > bv) { bv = sum[o + r]; best = r; }
			if (bv > GravelMaxSum) GravelMaxSum = bv;
			if (best < 0) { int s = slot[(int)Deep(c, bx, (int)Math.Floor(c.g0) - 1, bz, false)]; best = s >= 0 ? s : 0; }
			GravelRock[i] = (byte)GravelRocks[best];
			int d = Down[i];
			if (!Sea[i] && d >= 0) { int od = d * R; for (int r = 0; r < R; r++) sum[od + r] += sum[o + r]; }
		}
	}

	// drop every cell-sized array that isn't kept (per-plate and per-group arrays are small and stay)
	void Drop()
	{
		int NN = N * N;
		foreach (var f in typeof(EarthPlan).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
		{
			if (!f.FieldType.IsArray || f.IsInitOnly || KeepNames.Contains(f.Name)) continue;
			if (f.GetValue(this) is Array a && a.Length >= NN) f.SetValue(this, null);
		}
	}
}
