using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Cubeland;

// The game: makes the world, keeps its chunks' meshes up to date, and runs you around in it.
public partial class Main : Node3D
{
	// The same version as the web game (project.godot config/version), so the two always match, multiplayer included.
	public static string Version => (string)ProjectSettings.GetSetting("application/config/version", "Alpha 0.0.5.1");
	// The name shown in the window and menus ("CubeLand" + "Alpha 0.0.5.1"). project.godot's config/name stays
	// "CubeLand Alpha", because that names the folder your worlds are saved in.
	public const string Title = "CubeLand";

	World world;
	Player player;
	ChunkMesher mesher;
	Camera3D cam;
	ShaderMaterial blockMat, waterMat;
	readonly Dictionary<int, (MeshInstance3D solid, MeshInstance3D water)> groupNodes = new();   // one pair of meshes per 32x32x32 group
	readonly Dictionary<int, (ChunkMesher.Part solid, ChunkMesher.Part water)> chunkParts = new();   // each built 16x16x16 chunk's pieces
	readonly HashSet<int> dirty = new(), soon = new();
	readonly HashSet<int> far = new();   // chunks waiting to be built that are past the render distance (built once you come near)
	Task genTask; volatile float genProgress; bool ready;
	Label info, loading;
	MeshInstance3D outline;
	Inventory inv; Drops drops; Falling falling; Kilns kilns;
	Sky sky; Godot.Environment env;
	float camSky = 1, camSkyCheck; bool nightMusic, wasUnder;
	readonly Random rnd = new();
	static bool HasArg(string a) => OS.GetCmdlineUserArgs().Contains(a);
	static int ArgInt(string prefix, int fallback) => OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith(prefix)) is { } a ? int.Parse(a[prefix.Length..]) : fallback;

	public override void _Ready()
	{
		DisplayServer.WindowSetTitle($"{Title} {Version}");
		// a test run's window doesn't take the focus (from here on: Windows may still bring it up when it opens) and never
		// touches the real pointer (see TestRun)
		if (TestRun && DisplayServer.GetName() != "headless") DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
		if (DataTools()){ SetProcess(false); GetTree().Quit(); return; }   // (testing the shared data files: see Main.Data.cs)
		// menus and the hotbar follow your Windows display scaling, like the web edition does in a browser, but always
		// by a whole number (150% and up makes them twice as big), so every pixel of the pixel art stays the same size.
		// --uiscale=N for testing.
		var uiArg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--uiscale="));
		float uiScale = uiArg != null ? float.Parse(uiArg[10..], System.Globalization.CultureInfo.InvariantCulture)
			: MathF.Floor(DisplayServer.ScreenGetDpi() / 96f + 0.5f);
		uiScale = Mathf.Max(1, MathF.Round(uiScale) - (uiArg != null ? 0 : 1));   // (one step smaller than your display scaling: 150% -> 1x)
		autoGui = (int)uiScale;
		LoadSettings();
		if (uiArg == null && settings.Gui > 0) uiScale = settings.Gui;   // (the GUI scale you picked in Settings)
		GetTree().Root.ContentScaleFactor = uiScale;
		if (uiScale > 1 && DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Windowed)
		{
			// the window grows to match, so there's as much room as at 100% (but never bigger than the screen)
			var usable = DisplayServer.ScreenGetUsableRect();
			var size = DisplayServer.WindowGetSize();
			var want = new Vector2I(Math.Min((int)(size.X * uiScale), usable.Size.X - 16), Math.Min((int)(size.Y * uiScale), usable.Size.Y - 48));
			DisplayServer.WindowSetSize(want);
			DisplayServer.WindowSetPosition(usable.Position + (usable.Size - want) / 2 + new Vector2I(0, 16));
		}
		Atlas.Build();
		_ = WorldGen.OreVeins;   // (data/worldgen.json is read here, not first in the world-making thread, so a mistake in it stops the game at load like the other files)
		if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--atlasdump=")) is { } ad) { Atlas.Dump(ad[12..]); SetProcess(false); GetTree().Quit(); return; }   // (testing)
		if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--sheetcheck=")) is { } sc) { Atlas.SheetCheck(sc[13..]); SetProcess(false); GetTree().Quit(); return; }   // (testing)
		if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--colmap=")) is { } cm)
		{   // (testing, 2b: the column drawing. COLHASH over a 5 x 5 grid of columns across the map, each column's blocks in cube order
			// as 4-byte numbers (FNV-1a); the time per column; a top-down picture of 16 x 16 columns (--at=cx,cz their middle, in
			// columns; --river: round the first river 8-20 blocks wide from the map's middle out) and a cut through its middle row)
			string st = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--seed="))?[7..] ?? "pumpkin";
			if (OS.GetCmdlineUserArgs().Contains("--noplants")) EarthCol.PlantsOn = false;   // (0.1.5.5 test: the trees and W only)
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var plan = new EarthPlan(Worlds.SeedFrom(st), ArgInt("--size=", 2048), ArgInt("--room=", 0));
			long planMs = sw.ElapsedMilliseconds; var tw = OS.GetCmdlineUserArgs().Contains("--notrees") ? null : new World(Worlds.SeedFrom(st), 4, 640, plan.Size, 1, 9); var cols = new EarthCol(plan, tw?.Gen); int ncx = plan.Size / 16;   // (the trees: land 9, Earthlike's, trunks up into the leaves)
			if (tw != null && !OS.GetCmdlineUserArgs().Contains("--nostairs"))
			{   // (0.1.5.5) STAIRS: the staircases found with nothing skipped, each region's "x,y,z,dir,n,mat" or "none", rows of z then x
				var ssw = System.Diagnostics.Stopwatch.StartNew(); cols.Stairs = cols.FindStairs(new System.Collections.Generic.HashSet<int>());
				GD.Print($"STAIRS {string.Join(" ", cols.Stairs.Select(st => st == null ? "none" : string.Join(",", st)))} (found in {ssw.ElapsedMilliseconds} ms)");
			}
			if (tw != null && OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--stairsurvey=")) is { } ssa)
			{   // (test: a moved staircase's new spot, StairElsewhere, for seeds 1-10: found or not, how many tested, how long)
				var q = ssa[14..].Split(','); int tries = int.Parse(q[0]), test = int.Parse(q[1]);
				for (int sd = 1; sd <= 10; sd++)
				{
					var ssw = System.Diagnostics.Stopwatch.StartNew(); var f = cols.StairElsewhere(plan.Size / 4, plan.Size / 4, new System.Collections.Generic.HashSet<int>(), sd, tries, test);
					GD.Print($"STAIRSURVEY seed {sd}: {(f == null ? "none" : string.Join(",", f))}, tested {cols.LastTested}, {ssw.ElapsedMilliseconds} ms");
				}
			}
			uint h = 2166136261; sw.Restart();
			for (int kz = 0; kz < 5; kz++) for (int kx = 0; kx < 5; kx++)
			{
				int cx = (2 * kx + 1) * ncx / 10, cz = (2 * kz + 1) * ncx / 10;
				foreach (int v in cols.Draw(cx, cz)) unchecked { h = (h ^ (uint)v) * 16777619; }
			}
			{   // (0.1.5.4) RIVERCOUNT: how many of each river water id (3891-3915) the COLHASH columns hold, id:count for those there
				var rc = new System.Collections.Generic.SortedDictionary<int, int>();
				for (int kz = 0; kz < 5; kz++) for (int kx = 0; kx < 5; kx++)
					foreach (int v in cols.Draw((2 * kx + 1) * ncx / 10, (2 * kz + 1) * ncx / 10)) if (Cubeland.Blocks.IsRiverWater(v)) rc[v] = rc.GetValueOrDefault(v) + 1;
				GD.Print("RIVERCOUNT " + (rc.Count == 0 ? "none" : string.Join(" ", rc.Select(kv => $"{kv.Key}:{kv.Value}"))));
			}
			{   // (0.1.5.5) LIFECOUNT: the same for the life blocks: logs (upright or lying, in the log group), Burnt Logs, Earthlike's
				// plants, the Flowers, crops, Cantaloupe and Dead Bush
				// (web's form: over the 25 COLHASH columns and the 9 SPAWNHASH ones; crops 3700-3721)
				static bool Life(int v) => Cubeland.Blocks.Get(Cubeland.Blocks.LogOf(v))?.Log == true || v >= 3994 && v <= 3996 || v >= 3940 && v <= 3961
					|| v >= 194 && v <= 198 || v >= 3700 && v <= 3723;
				var lc = new System.Collections.Generic.SortedDictionary<int, int>();
				var (lsx, lsz) = EarthSpawn(plan);
				var lifeCols = new System.Collections.Generic.List<(int, int)>();
				for (int kz = 0; kz < 5; kz++) for (int kx = 0; kx < 5; kx++) lifeCols.Add(((2 * kx + 1) * ncx / 10, (2 * kz + 1) * ncx / 10));
				for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) lifeCols.Add((Math.Clamp((lsx >> 4) + dx, 0, ncx - 1), Math.Clamp((lsz >> 4) + dz, 0, ncx - 1)));
				foreach (var (acx, acz) in lifeCols) foreach (int v in cols.Draw(acx, acz)) if (v > 0 && Life(v)) lc[v] = lc.GetValueOrDefault(v) + 1;
				GD.Print("LIFECOUNT " + (lc.Count == 0 ? "none" : string.Join(" ", lc.Select(kv => $"{kv.Key}:{kv.Value}"))));
			}
			if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--startsurvey=")) is { } sva)
			{   // (test: the ground's top blocks round the start, dry or under up to 3 of water, and the nearest of each kind)
				int R = int.Parse(sva[14..]); var (ssx, ssz) = EarthSpawn(plan);
				var tops = new System.Collections.Generic.SortedDictionary<int, int>(); var near = new System.Collections.Generic.Dictionary<string, double>(); var nearAt = new System.Collections.Generic.Dictionary<string, (int, int, int)>();
				string Kind(int v) => v == 11 || v >= 5030 && v <= 5051 ? "gravel" : v == 63 ? "clay" : v == 3928 ? "cracked_clay" : v == 4805 || v == 4815 ? "clay_soil"
					: v == 6 ? "sand" : Cubeland.Blocks.Get(v)?.Needs == "pickaxe" ? "rock" : null;
				for (int acz = (ssz >> 4) - R; acz <= (ssz >> 4) + R; acz++) for (int acx = (ssx >> 4) - R; acx <= (ssx >> 4) + R; acx++)
				{
					if (acx < 0 || acz < 0 || acx >= ncx || acz >= ncx) continue;
					var c = cols.Draw(acx, acz);
					for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
					{
						int wet = 0, top = 0;
						for (int y = cols.H - 1; y >= 0; y--)
						{
							int v = c[EarthCol.At(x, y, z)]; if (v == 0) continue; var b = Cubeland.Blocks.Get(v);
							if (Cubeland.Blocks.IsWater(v))
							{   // (and the nearest falling water and rapids, for pictures)
								string wk = v == Cubeland.Blocks.FallingWater ? "falling" : v >= 3907 && v <= 3914 ? "rapids" : v >= 3899 && v <= 3906 ? "quick" : null;
								if (wk != null && wet == 0) { double dx = acx * 16 + x - ssx, dz = acz * 16 + z - ssz, d = Math.Sqrt(dx * dx + dz * dz); if (!near.TryGetValue(wk, out var o) || d < o) { near[wk] = d; nearAt[wk] = (acx * 16 + x, y, acz * 16 + z); } }
								wet++; continue;
							}
							if (b == null || b.NoCollide || b.Leaves || b.Log || Cubeland.Blocks.IsSnow(v)) continue;
							top = v; break;
						}
						if (top == 0 || wet > 3) continue;
						tops[top] = tops.GetValueOrDefault(top) + 1;
						if (Kind(top) is { } k) { double dx = acx * 16 + x - ssx, dz = acz * 16 + z - ssz, d = Math.Sqrt(dx * dx + dz * dz); if (!near.TryGetValue(k, out var o) || d < o) near[k] = d; }
					}
				}
				GD.Print($"STARTSURVEY within {R} chunks of {ssx},{ssz}: " + string.Join(" ", tops.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}")));
				foreach (var wk in new[] { "falling", "rapids", "quick" }) if (nearAt.TryGetValue(wk, out var wat)) GD.Print($"STARTWATER {wk} at {wat.Item1},{wat.Item2},{wat.Item3}");
				GD.Print("STARTNEAR " + string.Join(" ", new[] { "gravel", "clay", "cracked_clay", "clay_soil", "sand", "rock", "falling", "rapids", "quick" }.Select(k => $"{k}:{(near.TryGetValue(k, out var d) ? d.ToString("0") : "none")}")));
			}
			GD.Print($"COLHASH {h:x8} (seed {st}, {plan.Size} wide, {cols.H} tall; plan {planMs} ms, {sw.ElapsedMilliseconds / 25.0:0} ms a column; PLANHASH {plan.PlanHash:x8} KEEPHASH {plan.KeepHash:x8})");
			// (and SPAWNHASH: the same over the 3 x 3 columns round where a new world's players start, Main.EarthSpawn, its column
			// in the middle, rows of z then x)
			var (spx, spz) = EarthSpawn(plan); uint hs = 2166136261;
			for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
			{
				int cx = Math.Clamp((spx >> 4) + dx, 0, ncx - 1), cz = Math.Clamp((spz >> 4) + dz, 0, ncx - 1);
				foreach (int v in cols.Draw(cx, cz)) unchecked { hs = (hs ^ (uint)v) * 16777619; }
			}
			GD.Print($"SPAWNHASH {hs:x8} (the start at block {spx},{spz}, column {spx >> 4},{spz >> 4})");
			if (OS.GetCmdlineUserArgs().Contains("--pacesurvey"))
			{   // (test, 0.1.5.4 rivers: how the river cells' paces spread, by count and by river length, with their widths)
				var paces = new System.Collections.Generic.List<(double pace, double len, double w)>(); int Nn = plan.N;
				for (int i = 0; i < Nn * Nn; i++)
				{
					if (!plan.River(i)) continue; int r = plan.Down[i]; if (r < 0) continue;
					double d = (i % Nn != r % Nn && i / Nn != r / Nn) ? plan.Cell * 1.4142135623730951 : plan.Cell;
					paces.Add((plan.Pace(Math.Max(0, plan.Level[i] - plan.Level[r]) / d), d, plan.RiverWidth(i)));
				}
				paces.Sort((p1, p2) => p1.pace.CompareTo(p2.pace)); double tot = paces.Sum(q => q.len);
				string At(double f) { double acc = 0; foreach (var q in paces) { acc += q.len; if (acc >= f * tot) return q.pace.ToString("0.00000"); } return "-"; }
				GD.Print($"PACESURVEY {paces.Count} river cells, {tot:0} blocks of river; pace by length: 10% {At(0.1)}, 25% {At(0.25)}, 50% {At(0.5)}, 75% {At(0.75)}, 90% {At(0.9)}, 95% {At(0.95)}, 99% {At(0.99)}, max {paces.Last().pace:0.00000}");
				foreach (var pcut in new[] { 0.001, 0.002, 0.004, 0.006, 0.01, 0.015, 0.02, 0.03, 0.05 })
					GD.Print($"PACESURVEY share of river length over {pcut}: {paces.Where(q => q.pace > pcut).Sum(q => q.len) / tot:P1} (of wide rivers, 12+ blocks: {paces.Where(q => q.pace > pcut && q.w >= 12).Sum(q => q.len) / Math.Max(1, paces.Where(q => q.w >= 12).Sum(q => q.len)):P1})");
			}
			if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--probe=")) is { } pr)   // (test: what the plan has at blocks x,z;x,z...)
				foreach (var one in pr[8..].Split(';'))
				{
					var q = one.Split(','); int px = int.Parse(q[0]), pz = int.Parse(q[1]), pi = Math.Clamp(pz / plan.Cell, 0, plan.N - 1) * plan.N + Math.Clamp(px / plan.Cell, 0, plan.N - 1);
					int[] nv = null; double nd = 1e9;
					foreach (var v in plan.Volcanoes) { double dd = Math.Sqrt(Math.Pow(v[0] * plan.Cell + plan.Cell / 2 - px, 2) + Math.Pow(v[1] * plan.Cell + plan.Cell / 2 - pz, 2)); if (dd < nd) { nd = dd; nv = v; } }
					var gc = plan.ColAt(px, pz); int gy = plan.GroundAt(px, pz);
					GD.Print($"PROBE {px},{pz}: biome {plan.Biome[pi]}, sub {plan.SubAt(px, pz)}, sea {plan.Sea[pi]}, lake {plan.Lake[pi]}, ground {gy} (sea {plan.Room}), top rock {plan.RockAt(gc, px, gy, pz, gy)}, 5 down {plan.RockAt(gc, px, gy - 5, pz, gy)}; nearest volcano {(nv == null ? "none" : $"kind {nv[2]} at block {nv[0] * plan.Cell + plan.Cell / 2},{nv[1] * plan.Cell + plan.Cell / 2}, {nd:0} blocks away")}");
				}
			int mx = ncx / 2, mz = ncx / 2;
			if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--at=")) is { } atc) { var q2 = atc[5..].Split(','); mx = int.Parse(q2[0]); mz = int.Parse(q2[1]); }
			else if (OS.GetCmdlineUserArgs().Contains("--river"))
			{
				int N = plan.N; double bestD = 1e18;
				for (int i = 0; i < N * N; i++)
				{
					if (!plan.River(i)) continue; double w = plan.RiverWidth(i) / plan.Belt; if (w < 8 || w > 20) continue;
					double dx = i % N - N / 2.0, dz = i / N - N / 2.0, d = dx * dx + dz * dz;
					if (d < bestD) { bestD = d; mx = (int)(plan.RiverPX[i] / 16); mz = (int)(plan.RiverPZ[i] / 16); }
				}
			}
			int n = 256, c0x = mx - 8, c0z = mz - 8; var img = Godot.Image.CreateEmpty(n, n, false, Godot.Image.Format.Rgb8);
			int y0 = plan.Room - 48, y1 = plan.Room + 80; var cut = Godot.Image.CreateEmpty(n, y1 - y0, false, Godot.Image.Format.Rgb8);
			var rockOf = new System.Collections.Generic.Dictionary<int, int>();
			foreach (EarthPlan.Rock r in Enum.GetValues(typeof(EarthPlan.Rock))) rockOf.TryAdd(EarthCol.RockId(r), (int)r);
			var own = new System.Collections.Generic.Dictionary<int, uint> {
				[EarthIds.Water] = 0x2f62b8, [EarthIds.Ice] = 0xcfe4ff, [EarthIds.Grass] = 0x5f9f3a, [EarthIds.SnowyGrass] = 0xeef4f8, [EarthIds.SnowBlock] = 0xffffff,
				[EarthIds.Sand] = 0xe3d39a, [EarthIds.Mud] = 0x5a4632, [EarthIds.Peat] = 0x3e2c1e, [EarthIds.Silt] = 0xa89a7a, [3931] = 0x3a2a1a,
				[4810] = 0x8fae4a, [4811] = 0x557a4a, [4812] = 0x7a9a3a, [4813] = 0x9ab05a, [4815] = 0x6a9a4a, [4816] = 0x8aa860, [4817] = 0x7a8a5a,
				[4818] = 0x4f7a3a, [4819] = 0x6f8f5a, [3920] = 0x8a9a6a, [3922] = 0xa8d0f0, [3923] = 0x6aa8ff, [3924] = 0xd8e0e8, [3925] = 0x8a8f96,
				[3926] = 0xa0a888, [3927] = 0xf4f0e8, [3928] = 0xc0906a, [3929] = 0xa08a6a, [3934] = 0x6a8a4a, [3937] = 0xd0a090, [3938] = 0x5a7a5a,
				[3939] = 0xb07a4a, [58] = 0x4f7a3a, [218] = 0xb5652e, [65] = 0x9a5a3a, [63] = 0x9aa0b0 };
			for (int s = 380; s < 387; s++) own[s] = 0xf8fbff;
			uint RiverCol(int id) { int pc = (id - 3891) / 8; return pc == 0 ? 0x3a74c8u : pc == 1 ? 0x5a9ae0u : 0xa8d8ffu; }   // (0.1.5.4: running river water, lighter the faster)
			uint ColorOf(int id) => Cubeland.Blocks.IsRiverWater(id) ? RiverCol(id) : own.TryGetValue(id, out uint u) ? u : Cubeland.Blocks.IsLeaves(id) ? 0x2f7a2au : Cubeland.Blocks.B[id] is { } bd && bd.Name.EndsWith("Log") ? 0x6a4a2au : id >= 5030 && id <= 5051 || id == EarthIds.Gravel ? 0x8c8680u
				: rockOf.TryGetValue(id, out int rk) ? EarthPlan.RockColors[rk] : 0xff00ffu;
			Color Shade(uint rgb, float f) => new Color(Math.Min(1, ((rgb >> 16) & 255) / 255f * f), Math.Min(1, ((rgb >> 8) & 255) / 255f * f), Math.Min(1, (rgb & 255) / 255f * f));
			var seen = new System.Collections.Generic.Dictionary<int, int>(); sw.Restart();
			int[] topY = new int[n * n], topId = new int[n * n];   // (0.1.5.4: the top block of each column, for the flow check)
			for (int q = 0; q < 16 * 16; q++)
			{
				int cx = c0x + q % 16, cz = c0z + q / 16; var col = cols.Draw(cx, cz);
				for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
				{
					int y = cols.H - 1; while (y > 0 && col[EarthCol.At(x, y, z)] == 0) y--;
					topY[(q / 16 * 16 + z) * n + q % 16 * 16 + x] = y; topId[(q / 16 * 16 + z) * n + q % 16 * 16 + x] = col[EarthCol.At(x, y, z)];
					int id = col[EarthCol.At(x, y, z)]; seen[id] = seen.GetValueOrDefault(id) + 1;
					img.SetPixel((cx - c0x) * 16 + x, (cz - c0z) * 16 + z, Shade(ColorOf(id), (float)Math.Clamp(0.6 + (y - plan.Room) / 120.0, 0.35, 1.2)));
				}
				if (cz == mz) for (int x = 0; x < 16; x++) for (int y = y0; y < y1; y++)
				{
					int id = col[EarthCol.At(x, y, 8)];
					cut.SetPixel((cx - c0x) * 16 + x, y1 - 1 - y, id == 0 ? new Color(0.85f, 0.92f, 1f) : Shade(ColorOf(id), 1));
				}
			}
			System.IO.Directory.CreateDirectory(cm[9..]); int zm = ArgInt("--zoom=", 3); img.Resize(n * zm, n * zm, Godot.Image.Interpolation.Nearest); cut.Resize(n * zm, (y1 - y0) * zm, Godot.Image.Interpolation.Nearest); img.SavePng($"{cm[9..]}/columns_top.png"); cut.SavePng($"{cm[9..]}/columns_cut.png");
			GD.Print($"COLMAP columns {c0x}-{c0x + 15} x {c0z}-{c0z + 15} (blocks {c0x * 16},{c0z * 16} to {c0x * 16 + 255},{c0z * 16 + 255}; cut along block row z {mz * 16 + 8}, y {y0}-{y1 - 1}); {sw.ElapsedMilliseconds / 256.0:0} ms a column");
			{   // (0.1.5.4 --colmap: does river water run downhill? From each river-water top, 8 blocks on its way: river water there
				// should be no higher; also how often it's lower, and the paces)
				int[] dxs = { 1, 1, 0, -1, -1, -1, 0, 1 }, dzs = { 0, 1, 1, 1, 0, -1, -1, -1 }; int river = 0, ahead = 0, up = 0, down = 0; var pc = new int[3];
				for (int i = 0; i < n * n; i++)
				{
					if (!Cubeland.Blocks.IsRiverWater(topId[i])) continue; river++; int f = (topId[i] - 3891) % 8, p = (topId[i] - 3891) / 8; pc[Math.Min(2, p)]++;
					if (f > 7) continue; int ax = i % n + 8 * dxs[f], az = i / n + 8 * dzs[f]; if (ax < 0 || az < 0 || ax >= n || az >= n) continue;
					int j = az * n + ax; if (!Cubeland.Blocks.IsRiverWater(topId[j])) continue; ahead++;
					if (topY[j] > topY[i]) up++; else if (topY[j] < topY[i]) down++;
				}
				GD.Print($"FLOWCHECK {river} river-water tops (gentle {pc[0]}, quick {pc[1]}, rapids {pc[2]}); 8 ahead still river {ahead}: higher {up}, lower {down}, level {ahead - up - down}");
			}
			GD.Print("COLMAP tops: " + string.Join(", ", seen.OrderByDescending(kv => kv.Value).Take(24).Select(kv => $"{kv.Key} {kv.Value}")));
			SetProcess(false); GetTree().Quit(); return;
		}
		if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--planmap=")) is { } pm)
		{   // (testing: pictures of an Earthlike world's plan: --planmap=<folder> [--seed=<text or number>] [--size=N] [--room=blocks above the sea] [--scale=N] [--subzoom=x,z,size])
			var sw = System.Diagnostics.Stopwatch.StartNew();
			string st = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--seed="))?[7..] ?? "pumpkin";
			EarthPlan.NoErode = HasArg("--noerode"); foreach (var (ev, set) in new (string, Action<double>)[] { ("G_MEET", v => EarthPlan.MeetChance = v), ("G_DRIFTGAP", v => EarthPlan.DriftGap = v), ("G_LAND", v => EarthPlan.LandShare = v), ("G_DRIFTEDGE", v => EarthPlan.DriftEdge = v), ("G_TURN", v => EarthPlan.DriftTurn = v), ("G_GROUPMAX", v => EarthPlan.GroupMax = v), ("G_LEAD", v => EarthPlan.LeadCost = v), ("G_SPREADP", v => EarthPlan.SpreadPasses = (int)v), ("G_SPREADR", v => EarthPlan.SpreadR = (int)v), ("G_SUPERW", v => EarthPlan.SuperWide = v), ("G_SPINMIN", v => EarthPlan.SpinMin = v), ("G_DIRDRIFT", v => EarthPlan.DirFromDrift = v > 0), ("G_LEADEDGE", v => EarthPlan.LeadEdge = v > 0), ("G_LEADTARGET", v => EarthPlan.LeadTarget = v), ("G_LEADMIN", v => EarthPlan.LeadMin = v), ("G_LEADWOB", v => EarthPlan.LeadWob = v), ("G_LEADACT", v => EarthPlan.LeadAct = v), ("G_LEADTHIN", v => EarthPlan.LeadThin = (int)v), ("G_LEADHMIN", v => EarthPlan.LeadHMin = v), ("G_LEADCOASTMIN", v => EarthPlan.LeadCoastMin = v), ("G_LEADLOCAL", v => EarthPlan.LeadLocal = v > 0), ("G_DRIFTREACH", v => EarthPlan.DriftReach = v), ("G_RHO8", v => EarthPlan.RiverRho8 = v > 0), ("G_RSMOOTH", v => EarthPlan.RiverSmooth = (int)v), ("G_ICELAKE", v => EarthPlan.IceLakeShare = v), ("G_LAKECAP", v => EarthPlan.LakeCap = v), ("G_ICEGENTLE", v => EarthPlan.IceGentle = v), ("G_ICEWIN", v => EarthPlan.IceWin = (int)v), ("G_JOINROOM", v => EarthPlan.JoinRoom = v), ("G_SUPERLEG", v => EarthPlan.SuperLeg = v), ("G_SUPERSPAN", v => EarthPlan.SuperSpan = v), ("G_SUPERBAY", v => EarthPlan.SuperBay = v), ("G_LOBEDEEP", v => EarthPlan.LobeDeep = v), ("G_AWAY", v => EarthPlan.AwayRest = v), ("G_SPINMAX", v => EarthPlan.SpinMax = v), ("G_SUPEREDGE", v => EarthPlan.SuperEdge = v), ("G_GROUPSP", v => EarthPlan.GroupSpeed = v), ("G_PULL", v => EarthPlan.MeetPull = v), ("G_FALL", v => EarthPlan.ShareFall = v), ("G_LAKEMIN", v => EarthPlan.LakeMinArea = v), ("G_BASINDEEP", v => EarthPlan.BasinDeep = v), ("G_PANDEEP", v => EarthPlan.PanDeep = v), ("G_LOESSSHARE", v => EarthPlan.LoessShare = v), ("G_WIDEMAX", v => EarthPlan.WideMax = v), ("G_KEEP", v => EarthPlan.KeepCells = v), ("G_YOUNGSEA", v => EarthPlan.YoungSeaChance = v), ("G_GAP", v => EarthPlan.GapShare = v), ("G_EDGELO", v => EarthPlan.EdgeLo = v), ("G_EDGEHI", v => EarthPlan.EdgeHi = v), ("G_ISLGAP", v => EarthPlan.IslandGap = v), ("G_EDGEFADE", v => EarthPlan.EdgeFade = v), ("G_JOINH", v => EarthPlan.JoinHigh = v), ("G_JOINW", v => EarthPlan.JoinWide = v), ("G_SUTH", v => EarthPlan.SutureHigh = v), ("G_SUTW", v => EarthPlan.SutureWide = v), ("G_ICEOLD", v => EarthPlan.IceOld = v > 0), ("G_ICECUT", v => EarthPlan.IceCut = v), ("G_ICEBROAD", v => EarthPlan.IceBroad = v), ("G_ICERAG", v => EarthPlan.IceRagged = v), ("G_ICEBW", v => EarthPlan.IceBroadW = v), ("G_ICERW", v => EarthPlan.IceRaggedW = v), ("G_ICETURN", v => EarthPlan.IceTurn = v > 0), ("G_ICESEAL", v => EarthPlan.IceSeal = v), ("G_ICESEALW", v => EarthPlan.IceSealW = v) }) if (System.Environment.GetEnvironmentVariable(ev) is { } evs) set(double.Parse(evs, System.Globalization.CultureInfo.InvariantCulture)); if (System.Environment.GetEnvironmentVariable("KRIVER") is { } kre) EarthPlan.KRiver = double.Parse(kre, System.Globalization.CultureInfo.InvariantCulture); if (System.Environment.GetEnvironmentVariable("KICE") is { } kie) EarthPlan.KIce = double.Parse(kie, System.Globalization.CultureInfo.InvariantCulture);   // (testing: the plan before erosion)
			if (System.Environment.GetEnvironmentVariable("G_SPINTRY") is { } stv) EarthPlan.SpinTry = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(stv.Split(','), sx => double.Parse(sx, System.Globalization.CultureInfo.InvariantCulture)));
			EarthPlan.KeepAll = true;   // (the pictures need every array)
			var plan = new EarthPlan(Worlds.SeedFrom(st), ArgInt("--size=", 2048), ArgInt("--room=", 0));
			GD.Print($"PLAN seed {st}, {plan.Size} wide, {plan.Room * 2} tall (sea at {plan.Room}), {plan.N} x {plan.N} cells of {plan.Cell}: made in {sw.ElapsedMilliseconds} ms; {plan.Plates} plates ({plan.Continents} continents, {plan.BigIslands} big islands); {plan.Volcanoes.Count} volcano spots; PLANHASH {plan.PlanHash:x8}; KEEPHASH {plan.KeepHash:x8} (not-a-number values {plan.KeepNaN})"); GD.Print($"PLANTIME {plan.StepTimes}"); GD.Print($"PLANHISTORY {plan.HistNote}; hot-spot trail points {plan.Trails.Count}");
			if (System.Environment.GetEnvironmentVariable("KEEPCHECK") != null)
			{   // (the freeze's test: everything play time reads from the plan, with every array kept and with the rest dropped: must match)
				static uint PlayHash(EarthPlan p)
				{
					uint h = 2166136261; void Add(int v) { unchecked { h = (h ^ (uint)v) * 16777619; } }
					void AddD(double v) => Add(BitConverter.SingleToInt32Bits((float)v));
					for (int z = 0; z < p.N; z++) for (int x = 0; x < p.N; x++)
					{
						int bx = x * p.Cell + p.Cell / 2, bz = z * p.Cell + p.Cell / 2, i = z * p.N + x;
						Add(p.SubAt(bx, bz)); Add(p.GroundAt(bx, bz)); Add(p.River(i) ? 1 : 0);
						if ((x & 7) == 0 && (z & 7) == 0)
						{
							var c = p.ColAt(bx, bz); int yg = p.GroundAt(bx, bz);
							foreach (int d in new[] { 0, 1, 10, 40, 120, 300 }) Add((int)p.RockAt(c, bx, yg - d, bz, yg));
							var cl = p.ClimateAt(bx + 1.5, bz + 2.5); AddD(cl.temp); AddD(cl.swing); AddD(cl.rain); AddD(cl.season); AddD(cl.humid);
							var w = p.WindAt(bx + 1.5, bz + 2.5, 1); AddD(w.x); AddD(w.z);
							if (p.River(i)) { AddD(p.RiverWidth(i)); AddD(p.RiverDepth(i)); }
						}
					}
					return h;
				}
				uint a = PlayHash(plan);
				EarthPlan.KeepAll = false; var p2 = new EarthPlan(Worlds.SeedFrom(st), ArgInt("--size=", 2048), ArgInt("--room=", 0)); EarthPlan.KeepAll = true;
				uint b = PlayHash(p2);
				GD.Print($"PLANPLAYHASH {a:x8} (with the plan-only arrays dropped: {b:x8}, {(a == b && p2.KeepHash == plan.KeepHash && p2.PlanHash == plan.PlanHash ? "SAME" : "DIFFERENT")})");
			}
			if (plan.GravelRock != null)
			{   // (testing: GravelRock, each cell's gravel rock: a fingerprint of the bytes alone, the shares on land and in rivers, and a picture)
				int NNg = plan.N * plan.N; uint gh = 2166136261; var onLand = new int[256]; var inRiver = new int[256]; int nl = 0, nr = 0;
				var gi = Godot.Image.CreateEmpty(plan.N, plan.N, false, Godot.Image.Format.Rgb8);
				for (int i = 0; i < NNg; i++)
				{
					int g = plan.GravelRock[i]; unchecked { gh = (gh ^ (uint)g) * 16777619; }
					if (!plan.Sea[i]) { onLand[g]++; nl++; if (plan.River(i)) { inRiver[g]++; nr++; } }
					uint col = EarthPlan.RockColors[g]; double f = plan.Sea[i] ? 0.3 : plan.River(i) ? 1.0 : 0.75;
					gi.SetPixel(i % plan.N, i / plan.N, new Color((float)(((col >> 16) & 255) / 255.0 * f), (float)(((col >> 8) & 255) / 255.0 * f), (float)((col & 255) / 255.0 * f)));
				}
				string Shares(int[] n, int tot) => string.Join(", ", System.Linq.Enumerable.Range(0, 256).Where(r => n[r] > 0).OrderByDescending(r => n[r]).ThenBy(r => r).Select(r => $"{(EarthPlan.Rock)r} {100.0 * n[r] / Math.Max(1, tot):0.0}"));
				GD.Print($"PLANGRAVELHASH {gh:x8} (the GravelRock bytes alone; biggest catchment sum {plan.GravelMaxSum} quarter blocks)");
				GD.Print($"PLANGRAVEL land: {Shares(onLand, nl)}");
				GD.Print($"PLANGRAVEL rivers: {Shares(inRiver, nr)}");
				System.IO.Directory.CreateDirectory(pm[10..]); gi.SavePng($"{pm[10..]}/gravelrock.png");
			}
			if (plan.Wear != null)
			{   // (testing: what erosion did)
				int NNe = plan.N * plan.N; double cut = 0, laid = 0, maxCut = 0; int land = 0, ice = 0, trough = 0, mor = 0, allu = 0, loess = 0, karst = 0, dune = 0, fj = 0, coast1 = 0, cliff = 0, head = 0, scree = 0;
				for (int i = 0; i < NNe; i++)
				{
					if (plan.Fjord[i]) fj++; if (plan.Coast[i] == 1) coast1++; if (plan.Coast[i] == 2) cliff++;
					if (plan.Sea[i]) continue; land++;
					if (plan.Wear[i] > 0) cut += plan.Wear[i]; else laid -= plan.Wear[i]; maxCut = Math.Max(maxCut, plan.Wear[i]);
					if (plan.IceWas[i] >= 0.3) ice++; if (plan.Trough[i] >= 3 * plan.Amp) trough++; if (plan.Moraine[i] >= 1.5 * plan.Amp) mor++; if (plan.Alluvium[i] >= 1.5) allu++;
					if (plan.Loess[i] >= 1) loess++; if (plan.Karst[i] > 0.3) karst++; if (plan.Dune[i] > 0.6) dune++; if (plan.Head[i]) head++; if (plan.ScreeT[i] >= 3) scree++;
				}
				string P(int n) => $"{100.0 * n / Math.Max(1, land):0.0}%";
				{   // (rivers always downhill: the water's level never rises from a river cell to the cell it runs into, nor does the ground)
					int rc = 0, upW = 0, upG = 0;
					for (int i = 0; i < NNe; i++)
					{
						if (!plan.River(i)) continue; rc++; int r = plan.Down[i]; if (r < 0) continue;
						if (plan.Level[r] > plan.Level[i] + 1e-9) upW++;
						if (!plan.Lake[r] && !plan.Sea[r] && plan.Elev[r] > plan.Elev[i] + 1e-9 && plan.Elev[i] >= plan.Level[i] - 1e-9) upG++;
					}
					GD.Print($"PLANRIVERCHECK river cells {rc}; water running uphill {upW}; ground rising under the river {upG}");
					{ int w8 = 0, w16 = 0, w40 = 0, br = 0, im = -1; double wm = 0; for (int i = 0; i < NNe; i++) if (plan.River(i)) { double w = plan.RiverWidth(i), w2 = w / plan.Belt; if (w > wm) { wm = w; im = i; } if (w2 >= 8) w8++; if (w2 >= 16) w16++; if (w2 >= 40) w40++; if (plan.Braid[i] > 0.5) br++; }
					  GD.Print($"PLANRIVERS widest {wm:0} blocks (depth {(im >= 0 ? plan.RiverDepth(im) : 0):0.0}); river cells as wide as 8+, 16+, 40+ in a 2048 world (x {plan.Belt:0.##} here): {w8}, {w16}, {w40}; braided {br}"); }
					{ var cs = new List<double>(); for (int i = 0; i < NNe; i++) if (plan.River(i)) cs.Add(plan.Catchment(i)); cs.Sort(); if (cs.Count > 0) GD.Print($"PLANCATCH river catchments (square blocks of wet ground): median {cs[cs.Count / 2]:0}, 90% {cs[cs.Count * 9 / 10]:0}, 99% {cs[cs.Count * 99 / 100]:0}, most {cs[^1]:0}"); }
					int drown = 0, drownFar = 0;
					for (int i = 0; i < NNe; i++)
						if (plan.Sea[i] && plan.Elev0[i] > 0 && !plan.Fjord[i] && plan.Coast[i] != 1) { drown++; if (plan.SeaDist != null) { int x = i % plan.N, z = i / plan.N; bool nearOld = false; for (int dz = -3; dz <= 3 && !nearOld; dz++) for (int dx = -3; dx <= 3; dx++) { int xx = x + dx, zz = z + dz; if (xx >= 0 && zz >= 0 && xx < plan.N && zz < plan.N && plan.Elev0[zz * plan.N + xx] <= 0) { nearOld = true; break; } } if (!nearOld) { drownFar++; } } }
					GD.Print($"PLANDROWNED land cells now sea (not fjord, not cut back by waves): {drown}, of them more than 3 cells from the old sea: {drownFar}");
				}
				GD.Print($"PLANEROSION land worn {cut / Math.Max(1, land):0.0} blocks on average (most {maxCut:0}), laid down {laid / Math.Max(1, land):0.0}; of the land: under the old ice {P(ice)}, troughs {P(trough)}, cirques {P(head)}, moraines {P(mor)}, river fill {P(allu)}, loess {P(loess)}, karst {P(karst)}, sand seas {P(dune)}, scree {P(scree)}; fjord cells {fj}, coast cut back {coast1} cells, sea cliffs {cliff} cells");
			}
			int[] count = new int[EarthPlan.BiomeNames.Length]; foreach (var b in plan.Biome) count[b]++;
			int landN = plan.Biome.Length - count[EarthPlan.Ocean];
			GD.Print($"PLAN sea {100.0 * count[EarthPlan.Ocean] / plan.Biome.Length:0.0}% of the world; of the land: " + string.Join(", ", Enumerable.Range(0, count.Length).Where(k => count[k] > 0 && k != EarthPlan.Ocean).OrderByDescending(k => count[k]).Select(k => $"{EarthPlan.BiomeNames[k]} {100.0 * count[k] / landN:0.0}%")));
			{   // (testing: how the land lies: in ranges, rifts, how many continents and how big the biggest)
				int NNp = plan.N * plan.N, landC = 0, rangeC = 0, riftC = 0; var comp = new int[NNp]; for (int i = 0; i < NNp; i++) comp[i] = -1;
				var sizes = new List<int>(); var zs = new List<double>(); var stack = new Stack<int>();
				for (int i = 0; i < NNp; i++)
				{
					if (plan.Sea[i]) continue; landC++; if (plan.Lift[i] > 30 * plan.Tall) rangeC++; if (plan.Cut[i] > 8 * plan.Tall) riftC++;
					if (comp[i] >= 0) continue;
					int id = sizes.Count, n = 0; double zsum = 0; comp[i] = id; stack.Push(i);
					while (stack.Count > 0)
					{
						int c = stack.Pop(); n++; zsum += c / plan.N; int cx = c % plan.N, cz = c / plan.N;
						foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
						{
							int x = cx + dx, z = cz + dz; if (x < 0 || z < 0 || x >= plan.N || z >= plan.N) continue;
							int j = z * plan.N + x; if (plan.Sea[j] || comp[j] >= 0) continue; comp[j] = id; stack.Push(j);
						}
					}
					sizes.Add(n); zs.Add(zsum / n / plan.N);
				}
				var big = Enumerable.Range(0, sizes.Count).Where(k => sizes[k] >= 0.05 * landC).OrderByDescending(k => sizes[k]).ToList();
				// (testing: the open sea between the big landmasses: for each, how far to the nearest other big one, in blocks)
				var gaps = new List<double>(); var dist = new double[NNp]; var isBig = new HashSet<int>(big);
				foreach (int bb in big)
				{
					for (int i = 0; i < NNp; i++) dist[i] = comp[i] == bb ? 0 : 1e9;
					int Nn = plan.N;
					for (int z = 0; z < Nn; z++) for (int x = 0; x < Nn; x++)
					{
						int i = z * Nn + x; double d = dist[i];
						if (x > 0) d = Math.Min(d, dist[i - 1] + 1); if (z > 0) d = Math.Min(d, dist[i - Nn] + 1);
						if (x > 0 && z > 0) d = Math.Min(d, dist[i - Nn - 1] + 1.4142); if (x + 1 < Nn && z > 0) d = Math.Min(d, dist[i - Nn + 1] + 1.4142);
						dist[i] = d;
					}
					for (int z = Nn - 1; z >= 0; z--) for (int x = Nn - 1; x >= 0; x--)
					{
						int i = z * Nn + x; double d = dist[i];
						if (x + 1 < Nn) d = Math.Min(d, dist[i + 1] + 1); if (z + 1 < Nn) d = Math.Min(d, dist[i + Nn] + 1);
						if (x + 1 < Nn && z + 1 < Nn) d = Math.Min(d, dist[i + Nn + 1] + 1.4142); if (x > 0 && z + 1 < Nn) d = Math.Min(d, dist[i + Nn - 1] + 1.4142);
						dist[i] = d;
					}
					double g = 1e9; for (int i = 0; i < NNp; i++) if (comp[i] >= 0 && comp[i] != bb && isBig.Contains(comp[i])) g = Math.Min(g, dist[i]);
					if (g < 1e9) gaps.Add(g * plan.Cell);
				}
				GD.Print($"PLANGAPS big landmasses (5%+ of the land) by size: {string.Join(" ", big.Select(k => $"{100.0 * sizes[k] / Math.Max(1, landC):0}%"))}; each one's open sea to the nearest other (blocks): {string.Join(" ", gaps.Select(v => $"{v:0}"))}");
				{   // (testing, Draft 2h: which continents make up each big landmass, and the narrowest crust gap between continents that don't touch)
					int ncs = plan.Continents + plan.BigIslands; var inBig = big.Select(bb => new SortedSet<int>()).ToList();
					for (int i = 0; i < NNp; i++) { int c = plan.ContId[i]; if (c < 0 || plan.Sea[i]) continue; int bi = big.IndexOf(comp[i]); if (bi >= 0) inBig[bi].Add(c); }
					var gapC = new double[ncs, ncs]; int Nn = plan.N; var dd = new double[NNp];
					for (int a0 = 0; a0 < ncs; a0++)
					{
						for (int i = 0; i < NNp; i++) dd[i] = plan.ContId[i] == a0 ? 0 : 1e9;
						for (int z = 0; z < Nn; z++) for (int x = 0; x < Nn; x++) { int i = z * Nn + x; double d = dd[i]; if (x > 0) d = Math.Min(d, dd[i - 1] + 1); if (z > 0) d = Math.Min(d, dd[i - Nn] + 1); if (x > 0 && z > 0) d = Math.Min(d, dd[i - Nn - 1] + 1.4142); if (x + 1 < Nn && z > 0) d = Math.Min(d, dd[i - Nn + 1] + 1.4142); dd[i] = d; }
						for (int z = Nn - 1; z >= 0; z--) for (int x = Nn - 1; x >= 0; x--) { int i = z * Nn + x; double d = dd[i]; if (x + 1 < Nn) d = Math.Min(d, dd[i + 1] + 1); if (z + 1 < Nn) d = Math.Min(d, dd[i + Nn] + 1); if (x + 1 < Nn && z + 1 < Nn) d = Math.Min(d, dd[i + Nn + 1] + 1.4142); if (x > 0 && z + 1 < Nn) d = Math.Min(d, dd[i + Nn - 1] + 1.4142); dd[i] = d; }
						for (int b0 = 0; b0 < ncs; b0++) { double g = 1e9; for (int i = 0; i < NNp; i++) if (plan.ContId[i] == b0) g = Math.Min(g, dd[i]); gapC[a0, b0] = g; }
					}
					var pairs = new List<string>();
					for (int a0 = 0; a0 < ncs; a0++) for (int b0 = a0 + 1; b0 < ncs; b0++) if (gapC[a0, b0] > 1.01 && gapC[a0, b0] < 0.05 * Nn) pairs.Add($"{a0}-{b0} {gapC[a0, b0] - 1:0} cells ({100.0 * (gapC[a0, b0] - 1) / Nn:0.0}%)");
					GD.Print("PLANTHIN " + plan.ThinNote());
					{ int cE = 0, cR = 0, cO = 0, cY = 0, cT = 0; for (int q = 0; q < plan.Crust.Length; q++) { if (!plan.Crust[q]) continue; cT++; if (!plan.Sea[q]) continue; if (plan.YoungSea != null && plan.YoungSea[q]) cY++; else if (plan.GOceanDist[q] <= EarthPlan.KeepCells) cE++; else if (plan.GRift0[q] > 0.7) cR++; else { int qx = q % plan.N, qz = q / plan.N; double eb = Math.Min(Math.Min(qx + 0.5, qz + 0.5), Math.Min(plan.N - qx - 0.5, plan.N - qz - 0.5)) * plan.Cell; if (eb < 1.6 * Math.Max(48, plan.Size * EarthPlan.EdgeFade)) cY++; else cO++; } } GD.Print($"PLANCRUSTSEA crust under the sea, of the crust: near the world edge {100.0 * cY / cT:0.0}%, within 1.5 cells of its edge {100.0 * cE / cT:0.0}%, rifts {100.0 * cR / cT:0.0}%, else {100.0 * cO / cT:0.0}%"); }
GD.Print($"PLANCRUST crust {100.0 * plan.Crust.Count(q => q) / plan.Crust.Length:0.0}% of the world; continents in each big landmass: {string.Join(" ", inBig.Select(t => "[" + string.Join(",", t) + "]"))}; crust gaps under 5% of the world between continents that don't touch: {(pairs.Count == 0 ? "none" : string.Join(", ", pairs))}; touching: {string.Join(" ", Enumerable.Range(0, ncs * ncs).Where(q => q / ncs < q % ncs && gapC[q / ncs, q % ncs] <= 1.01).Select(q => $"{q / ncs}-{q % ncs}"))}");
				}
				{   // (testing, Draft 2i: the land raised 30+ by each age of range, the strongest at each cell)
					int ry = 0, rm2 = 0, ro = 0, ln = 0;
					for (int i = 0; i < NNp; i++)
					{
						if (plan.Sea[i]) continue; ln++;
						double y = plan.RangeYoung[i], m = plan.RangeMid[i], o = plan.RangeOld[i], mx = Math.Max(y, Math.Max(m, o));
						if (y + m + o < 30 * plan.Tall) continue; if (y >= mx) ry++; else if (m >= mx) rm2++; else ro++;
					}
					{   // (testing, ecology draft: each biome's climate; growing days a year (of 24) with warmth = Temp + Swing s over 5 and over 12)
						int nb = EarthPlan.BiomeNames.Length; var bc = new int[nb]; var bt = new double[nb]; var bs = new double[nb]; var br = new double[nb]; var bse = new double[nb]; var g5 = new double[nb]; var g12 = new double[nb];
						static double Days(double T, double Sw, double th) { if (Sw < 1e-6) return T >= th ? 24 : 0; double c = (th - T) / Sw; return c <= -1 ? 24 : c >= 1 ? 0 : 24 * Math.Acos(c) / Math.PI; }
						for (int q = 0; q < NNp; q++) { if (plan.Sea[q]) continue; int b = plan.Biome[q]; if (b >= nb) continue; bc[b]++; bt[b] += plan.Temp[q]; bs[b] += plan.Swing[q]; br[b] += plan.Rain[q]; bse[b] += plan.Season[q]; g5[b] += Days(plan.Temp[q], plan.Swing[q], 5); g12[b] += Days(plan.Temp[q], plan.Swing[q], 12); }
						GD.Print("PLANECO " + string.Join("; ", Enumerable.Range(0, nb).Where(b => bc[b] > 0).Select(b => $"{EarthPlan.BiomeNames[b]} {100.0 * bc[b] / NNp:0.0}% T {bt[b] / bc[b]:0.0} S {bs[b] / bc[b]:0.0} R {br[b] / bc[b]:0.00} Se {bse[b] / bc[b]:0.00} g5 {g5[b] / bc[b]:0.0} g12 {g12[b] / bc[b]:0.0}")));
					}
					if (System.Environment.GetEnvironmentVariable("LEADDBG") != null)
					{   // (testing, Draft 2l: the leading-edge plates)
						int Nn = plan.N, k0 = plan.Majors + plan.Minors + plan.MicrosMade;
						var dd = new int[NNp]; var qq = new Queue<int>();
						for (int i = 0; i < NNp; i++)
						{
							int x = i % Nn, z = i / Nn, p0 = plan.Plate[i];
							bool edge = x == 0 || z == 0 || x == Nn - 1 || z == Nn - 1 || plan.Plate[i - 1] != p0 || plan.Plate[i + 1] != p0 || plan.Plate[i - Nn] != p0 || plan.Plate[i + Nn] != p0;
							dd[i] = edge ? 1 : 0; if (edge) qq.Enqueue(i);
						}
						while (qq.Count > 0)
						{
							int c = qq.Dequeue(), x = c % Nn, z = c / Nn;
							foreach (int j in new[] { x > 0 ? c - 1 : -1, x < Nn - 1 ? c + 1 : -1, z > 0 ? c - Nn : -1, z < Nn - 1 ? c + Nn : -1 })
								if (j >= 0 && dd[j] == 0 && plan.Plate[j] == plan.Plate[c]) { dd[j] = dd[c] + 1; qq.Enqueue(j); }
						}
						int sIsl = 0, sArc = 0, sArcOpen = 0, sSlide = 0, sThin = 99, sPl = 0;
						var cdist = new int[NNp]; { var q2 = new Queue<int>(); for (int i = 0; i < NNp; i++) { cdist[i] = plan.Crust[i] ? 0 : 99999; if (plan.Crust[i]) q2.Enqueue(i); }
							while (q2.Count > 0) { int c = q2.Dequeue(), x = c % Nn, z = c / Nn; foreach (int j in new[] { x > 0 ? c - 1 : -1, x < Nn - 1 ? c + 1 : -1, z > 0 ? c - Nn : -1, z < Nn - 1 ? c + Nn : -1 }) if (j >= 0 && cdist[j] > cdist[c] + 1) { cdist[j] = cdist[c] + 1; q2.Enqueue(j); } } }
						for (int i = 0; i < NNp; i++) if (plan.Plate[i] >= k0 || plan.Other[i] >= k0 && plan.Edge[i] != 0) { if (plan.Edge[i] == 3) { sArc++; if (cdist[i] >= 12) sArcOpen++; } if (plan.Edge[i] == 5) sSlide++; }
						for (int k = k0; k < plan.Plates; k++)
						{
							int n = 0, thick = 0, arc = 0; var nb = new Dictionary<int, int>();
							for (int i = 0; i < NNp; i++)
							{
								if (plan.Plate[i] != k) continue; n++; thick = Math.Max(thick, dd[i]);
								int x = i % Nn, z = i / Nn;
								foreach (int j in new[] { x > 0 ? i - 1 : -1, x < Nn - 1 ? i + 1 : -1, z > 0 ? i - Nn : -1, z < Nn - 1 ? i + Nn : -1 })
									if (j >= 0 && plan.Plate[j] != k) nb[plan.Plate[j]] = nb.GetValueOrDefault(plan.Plate[j]) + 1;
							}
							for (int i = 0; i < NNp; i++) if (plan.Edge[i] == 3 && (plan.Plate[i] == k || plan.Other[i] == k)) arc++;
							int isl = plan.Islands.Count(v => (int)v[4] == k || (int)v[5] == k);
							sIsl += isl; sThin = Math.Min(sThin, thick); sPl++;
							double mx = 0, mz = 0; int x0 = Nn, x1 = 0, z0 = Nn, z1 = 0; for (int i = 0; i < NNp; i++) if (plan.Plate[i] == k) { int x = i % Nn, z = i / Nn; mx += x; mz += z; x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); z0 = Math.Min(z0, z); z1 = Math.Max(z1, z); }
							GD.Print($"DBGLEAD plate {k}: middle {mx / Math.Max(1, n) / Nn:0.00},{mz / Math.Max(1, n) / Nn:0.00} box x {x0}-{x1} z {z0}-{z1}; {n} cells, {thick} thick, v ({plan.VX[k]:0.00},{plan.VZ[k]:0.00}); next to {string.Join(" ", nb.OrderByDescending(e => e.Value).Select(e => $"{e.Key}{(plan.PlateGroup[e.Key] >= 0 ? "G" : "o")}:{e.Value}"))}; ocean-ocean edge cells {arc}; island-chain islands {isl}");
						}
						GD.Print($"DBGLEADSUM {sPl} leading-edge plates, the thinnest {sThin} deep; island-chain islands on them {sIsl}; ocean-ocean edge cells {sArc} ({sArcOpen} of them 12+ cells from land); sliding edge cells {sSlide}");
						var img = Godot.Image.CreateEmpty(Nn, Nn, false, Godot.Image.Format.Rgb8);
						for (int i = 0; i < NNp; i++)
						{
							int k = plan.Plate[i], e = plan.Edge[i]; bool lead = k >= k0 || (plan.Other[i] >= k0 && e != 0);
							Godot.Color col = plan.Crust[i] ? new Godot.Color(0.5f, 0.5f, 0.5f) : plan.PlateGroup[k] >= 0 ? new Godot.Color(0.16f, 0.18f, 0.3f) : new Godot.Color(0.1f, 0.1f, 0.12f);
							if (k >= k0) col = Godot.Color.FromHsv((k - k0) * 0.13f % 1f, 0.6f, plan.Crust[i] ? 0.5f : 0.85f);
							if (e != 0)
							{
								var ec = e == 1 ? new Godot.Color(1, 0, 0) : e == 2 ? new Godot.Color(1, 0.55f, 0) : e == 3 ? new Godot.Color(1, 1, 0) : e == 4 ? new Godot.Color(0, 1, 1) : new Godot.Color(0.8f, 0.8f, 0.8f);
								col = lead ? ec : col.Lerp(ec, 0.35f);
							}
							img.SetPixel(i % Nn, i / Nn, col);
						}
						img.Resize(Nn * 2, Nn * 2, Godot.Image.Interpolation.Nearest);
						foreach (var v in plan.Islands) if ((int)v[4] >= k0 || (int)v[5] >= k0)
							for (int dz = -3; dz <= 3; dz++) for (int dx = -3; dx <= 3; dx++) { int X = (int)(v[0] * 2) + dx, Z = (int)(v[1] * 2) + dz; if (X >= 0 && Z >= 0 && X < Nn * 2 && Z < Nn * 2 && Math.Max(Math.Abs(dx), Math.Abs(dz)) == 3) img.SetPixel(X, Z, new Godot.Color(1, 1, 1)); }
						img.SavePng($"{pm[10..]}/leadplates.png");
						if (System.Environment.GetEnvironmentVariable("DBGROW") is { } drw) foreach (var rw in drw.Split(';'))
						{   // (testing: the plates along a row z from x0 to x1: plate/group/edge kind/crust)
							var pr = rw.Split(','); int rz = int.Parse(pr[0]), rx0 = int.Parse(pr[1]), rx1 = int.Parse(pr[2]);
							GD.Print($"DBGROW z {rz} x {rx0}-{rx1}: " + string.Join(" ", Enumerable.Range(rx0, rx1 - rx0 + 1).Select(x => { int i = rz * Nn + x; return $"{plan.Plate[i]}{(plan.PlateGroup[plan.Plate[i]] >= 0 ? "g" + plan.PlateGroup[plan.Plate[i]] : "")}{(plan.Edge[i] > 0 ? "e" + plan.Edge[i] : "")}{(plan.Crust[i] ? "C" : "")}"; })));
						}
					}
					GD.Print($"PLANRANGES raised 30+ x Tall (share of the land): young {100.0 * ry / Math.Max(1, ln):0.0}%, middle-aged (joins) {100.0 * rm2 / Math.Max(1, ln):0.0}%, old (sutures) {100.0 * ro / Math.Max(1, ln):0.0}%; the leading coasts' ranges sized {plan.LeadSz:0.000}, strength {plan.LeadH:0.000}");
				}
				GD.Print($"PLANLAND land {100.0 * landC / NNp:0.0}%; in ranges (raised 30+ by plate edges) {100.0 * rangeC / Math.Max(1, landC):0.0}% of the land, in rifts (cut 8+) {100.0 * riftC / Math.Max(1, landC):0.0}%; landmasses 1%+ of the land: {sizes.Count(s => s >= 0.01 * landC)}, 5%+: {big.Count}; the biggest {100.0 * sizes.DefaultIfEmpty(0).Max() / Math.Max(1, landC):0.0}% of the land; the big ones' middles north to south: {string.Join(" ", big.Select(k => $"{zs[k]:0.00}"))}; {plan.ContNote}; shapes {plan.ShapeSize} cells ({plan.ShapeSize * plan.Cell} blocks), plates about {Math.Sqrt((double)NNp / plan.Plates) * plan.Cell:0} blocks across");
			}
			{   // (testing: the island chains: islands by the pair of plates they stand between; a chain is 2 or more)
				var chains = plan.Islands.GroupBy(v => ((int)v[4], (int)v[5])).Where(g => g.Count() >= 2).OrderByDescending(g => g.Count()).ToList();
				int above = 0; foreach (var v in plan.Islands) { int ci = (int)v[1] * plan.N + (int)v[0]; if (!plan.Sea[ci]) above++; }
				var bestC = chains.FirstOrDefault();
				string where = bestC == null ? "" : $"; the longest: {bestC.Count()} islands, cells x {bestC.Min(v => v[0]):0}-{bestC.Max(v => v[0]):0}, z {bestC.Min(v => v[1]):0}-{bestC.Max(v => v[1]):0}";
				var arcPairs = Enumerable.Range(0, plan.N * plan.N).Where(i => plan.Edge[i] == 3).GroupBy(i => (Math.Min(plan.Plate[i], plan.Other[i]), Math.Max(plan.Plate[i], plan.Other[i]))).Where(g => g.Count() >= 10).ToList();
				var chainPairs = chains.Select(g => (Math.Min(g.Key.Item1, g.Key.Item2), Math.Max(g.Key.Item1, g.Key.Item2))).ToHashSet();
				GD.Print($"PLANISLANDS ocean-meets-ocean edges pushing together: {arcPairs.Count} ({arcPairs.Count(g => chainPairs.Contains(g.Key))} with a chain; edge cells {string.Join(" ", arcPairs.Select(g => g.Count()))}); {plan.Islands.Count} islands ({above} above the sea; tries skipped: far from the line {plan.IslSkip[0]}, by hash {plan.IslSkip[1]}, by the edge {plan.IslSkip[2]}, near a coast {plan.IslSkip[3]}), {chains.Count} chains (lengths {string.Join(" ", chains.Select(g => g.Count()))}){where}");
			}
			GD.Print($"PLAN wastes below rain {plan.ColdDry:0.000} (cold), {plan.HotDry:0.000} (hot); rivers: {Enumerable.Range(0, plan.N * plan.N).Count(plan.River)} cells; lakes {plan.Lake.Count(l => l)} cells");
			if (HasArg("--plandump=0,0,0") || OS.GetCmdlineUserArgs().Any(a => a.StartsWith("--plandump="))) GD.Print("PLAN plates (number: radius in blocks, C continent or o ocean): " + string.Join(" ", Enumerable.Range(0, plan.Plates).Select(k => $"{k}:{plan.PlateR[k]:0}{(plan.Cont[k] ? "C" : "o")}")));
			if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--plandump=")) is { } pd)   // (z,x0,x1: one row's numbers)
			{
				var q = pd[11..].Split(',').Select(int.Parse).ToArray();
				for (int x = q[1]; x <= q[2]; x++) { int i = q[0] * plan.N + x; GD.Print($"PLANDUMP x {x}: elev {plan.Elev[i]:0.00} level {plan.Level[i]:0.00} plate {plan.Plate[i]} other {plan.Other[i]} bd {plan.Bd[i]:0.00} ds {plan.Ds[i]:0.00} lake {plan.Lake[i]}"); }
			}
			if (EarthPlan.SubNames.Length != EarthPlan.SubColors.Length || EarthPlan.SubNames.Length != EarthPlan.SubParent.Length || EarthPlan.BiomeNames.Length != EarthPlan.BiomeColors.Length)
				GD.PrintErr("PLAN lists differ in length");
			var subs = new Dictionary<int, int>();
			for (int z = 0; z < plan.N; z++) for (int x = 0; x < plan.N; x++) { int s = plan.SubAt(x * plan.Cell + plan.Cell / 2, z * plan.Cell + plan.Cell / 2); subs[s] = subs.GetValueOrDefault(s) + 1; }
			GD.Print("PLAN sub-biomes: " + string.Join(", ", subs.Where(k => k.Key >= EarthPlan.FirstSub).OrderByDescending(k => k.Value).Select(k => $"{EarthPlan.SubNames[k.Key - EarthPlan.FirstSub]} {100.0 * k.Value / (plan.N * plan.N):0.0}%")));
			GD.Print($"PLAN missing sub-biomes: {string.Join(", ", Enumerable.Range(0, EarthPlan.SubNames.Length).Where(k => !subs.ContainsKey(EarthPlan.FirstSub + k)).Select(k => EarthPlan.SubNames[k]))}");
			{   // (Draft 2n testing: the sea ice, new rule and the 2m one, by column: how deep from the map's edge, top and bottom)
				int Nn = plan.N; var iceMask = new byte[Nn * Nn]; double dTop = 0, dBot = 0, oTop = 0, oBot = 0; int newC = 0, oldC = 0, minT = int.MaxValue, maxT = 0, minB = int.MaxValue, maxB = 0;
				for (int x = 0; x < Nn; x++)
				{
					int nt = 0, nb = 0, ot = 0, ob = 0;
					for (int z = 0; z < Nn; z++)
					{
						int i = z * Nn + x; bool ice = plan.SubAt(x * plan.Cell + plan.Cell / 2, z * plan.Cell + plan.Cell / 2) == EarthPlan.SeaIce; iceMask[i] = (byte)(ice ? 1 : 0);
						bool old = plan.Sea[i] && !(plan.Fjord != null && plan.Fjord[i]) && plan.Temp[i] < -8;
						if (ice) { newC++; if (z < Nn / 2) nt++; else nb++; }
						if (old) { oldC++; if (z < Nn / 2) ot++; else ob++; }
					}
					dTop += nt; dBot += nb; oTop += ot; oBot += ob; minT = Math.Min(minT, nt); maxT = Math.Max(maxT, nt); minB = Math.Min(minB, nb); maxB = Math.Max(maxB, nb);
				}
				GD.Print($"PLANICE sea ice {100.0 * newC / (Nn * Nn):0.00}% of the map (2m rule {100.0 * oldC / (Nn * Nn):0.00}%, new/old {(double)newC / Math.Max(1, oldC):0.00}); ice cells per column top {dTop / Nn:0.0} (min {minT} max {maxT}; 2m {oTop / Nn:0.0}), bottom {dBot / Nn:0.0} (min {minB} max {maxB}; 2m {oBot / Nn:0.0}); cell {plan.Cell} blocks, grid {Nn}");
				{   // (for web: the sea ice cells' count and an FNV-1a 32 over one byte a cell, 1 sea ice or 0, row by row from z 0, each row from x 0; SubAt at each cell's middle)
					uint fh = 2166136261; foreach (byte bb in iceMask) { fh ^= bb; fh *= 16777619; }
					GD.Print($"PLANICEHASH sea ice cells {newC} of {Nn * Nn}, mask FNV-1a {fh:x8}");
				}
				if (System.Environment.GetEnvironmentVariable("ICEROW") is { } irs)
				{   // (testing: one row's temperature parts, z,x0,x1)
					var q = irs.Split(',').Select(int.Parse).ToArray();
					for (int x = q[1]; x <= q[2]; x++) { int i = q[0] * Nn + x; GD.Print($"PLANICEROW x {x}: T {plan.Temp[i]:0.00} cur {plan.Cur[i]:0.00} wob {plan.IceWob(x * plan.Cell + plan.Cell / 2, q[0] * plan.Cell + plan.Cell / 2):0.00} sea {plan.Sea[i]}"); }
				}
			}
			if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--subzoom=")) is { } sz)   // (x,z,size in blocks: the sub-biomes block by block)
			{
				var q = sz[10..].Split(',').Select(int.Parse).ToArray();
				plan.SaveSubZoom($"{pm[10..]}/subzoom.png", q[0], q[1], q[2]);
			}
			plan.SavePictures(pm[10..], ArgInt("--scale=", Math.Max(1, 1024 / plan.N)));
			plan.SaveHistory(pm[10..]);
			if (HasArg("--subdiff"))
			{   // (testing: which sub-biomes erosion changed into which, sampled at each cell's middle)
				EarthPlan.NoErode = true; var p0 = new EarthPlan(Worlds.SeedFrom(st), ArgInt("--size=", 2048), ArgInt("--room=", 0)); EarthPlan.NoErode = false;
				var tally = new Dictionary<string, int>(); int nAll = 0;
				for (int i = 0; i < plan.N * plan.N; i++)
				{
					int bx = (i % plan.N) * plan.Cell + plan.Cell / 2, bz = (i / plan.N) * plan.Cell + plan.Cell / 2;
					int s0 = p0.SubAt(bx, bz), s1 = plan.SubAt(bx, bz); nAll++; if (s0 == s1) continue;
					string N0(int q) => q >= EarthPlan.FirstSub ? EarthPlan.SubNames[q - EarthPlan.FirstSub] : EarthPlan.BiomeNames[q]; string k = N0(s0) + " -> " + N0(s1); tally[k] = tally.TryGetValue(k, out int v) ? v + 1 : 1;
				}
				var l = new List<KeyValuePair<string, int>>(tally); l.Sort((x, y) => y.Value.CompareTo(x.Value));
				for (int k = 0; k < Math.Min(30, l.Count); k++) GD.Print($"SUBDIFF {100.0 * l[k].Value / nAll:0.00}% {l[k].Key}");
			}
			{   // (testing: the lakes, as groups of lake cells, by size)
				int NNl = plan.N * plan.N; var seenL = new bool[NNl]; var st2 = new Stack<int>(); int[] bins = new int[5]; int nLakes = 0, lakeCells = 0, landCells = 0; int[] kinds = new int[4]; double lakeMost = 0, lakeDeep = 0;
				for (int i = 0; i < NNl; i++) if (!plan.Sea[i]) landCells++;
				for (int i0 = 0; i0 < NNl; i0++)
				{
					if (!plan.Lake[i0] || seenL[i0]) continue;
					int n = 0, pit = i0; seenL[i0] = true; st2.Push(i0);
					while (st2.Count > 0) { int c = st2.Pop(); n++; if (plan.Elev[c] < plan.Elev[pit] || plan.Elev[c] == plan.Elev[pit] && c < pit) pit = c; int cx = c % plan.N, cz = c / plan.N; for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) { int x = cx + dx, z = cz + dz; if (x < 0 || z < 0 || x >= plan.N || z >= plan.N) continue; int j = z * plan.N + x; if (plan.Lake[j] && !seenL[j]) { seenL[j] = true; st2.Push(j); } } }
					nLakes++; lakeCells += n; double a2 = n * (double)plan.Cell * plan.Cell; if (a2 > lakeMost) lakeMost = a2; lakeDeep = Math.Max(lakeDeep, plan.Level[pit] - plan.Elev[pit]);
					bins[a2 < 10000 ? 0 : a2 < 40000 ? 1 : a2 < 160000 ? 2 : a2 < 640000 ? 3 : 4]++;
					kinds[plan.RiftZ != null && plan.RiftZ[pit] > 0.3 ? 0 : plan.Basin != null && plan.Basin[pit] > 0.5 ? 1 : plan.IceWas != null && plan.IceWas[pit] >= 0.3 ? 2 : 3]++;
				}
				GD.Print($"PLANLAKEBIG the biggest lake {lakeMost:0} square blocks (about {Math.Sqrt(lakeMost):0} across), the deepest {lakeDeep:0.0} deep; big from {plan.LakeBig:0} square blocks");
				{ double hi = -1e9, lo = 1e9, hi0 = -1e9; var lv = new List<double>(); for (int i = 0; i < NNl; i++) { hi = Math.Max(hi, plan.Elev[i]); lo = Math.Min(lo, plan.Elev[i]); if (plan.Elev0 != null) hi0 = Math.Max(hi0, plan.Elev0[i]); if (plan.Elev[i] > 0) lv.Add(plan.Elev[i]); } lv.Sort(); double P(double f) => lv.Count > 0 ? lv[Math.Min(lv.Count - 1, (int)(f * lv.Count))] : 0; GD.Print($"PLANPEAKS the highest ground {hi:0} above the sea (before erosion {hi0:0}; room {plan.Room}, x {plan.Tall:0.00} of 2048's), the deepest sea floor {-lo:0} below; the land: half under {P(0.5):0}, 90% under {P(0.9):0}, 99% {P(0.99):0}, 99.9% {P(0.999):0}"); }
				GD.Print($"PLANLAKES {nLakes} lakes, {100.0 * lakeCells / Math.Max(1, landCells):0.0}% of the land; by size (square blocks): under 10k {bins[0]}, 10-40k {bins[1]}, 40-160k {bins[2]}, 160-640k {bins[3]}, 640k up {bins[4]}; in a rift {kinds[0]}, a basin {kinds[1]}, where the old ice was {kinds[2]}, elsewhere (big ones) {kinds[3]}; ice-bowl lakes that gave way {plan.LakeGaveWay}");
			}
			if (HasArg("--soils"))
			{   // (testing: the top block of every land cell: which soil, or bare rock)
				var cnt = new Dictionary<string, int>(); int landc = 0;
				for (int i = 0; i < plan.N * plan.N; i++)
				{
					if (plan.Sea[i] || plan.Lake[i]) continue; landc++;
					int bx = (i % plan.N) * plan.Cell + plan.Cell / 2, bz = (i / plan.N) * plan.Cell + plan.Cell / 2, yg = plan.GroundAt(bx, bz);
					var col = plan.ColAt(bx, bz); var r = plan.RockAt(col, bx, yg, bz, yg);
					string k = r == EarthPlan.Rock.Air ? "(bare rock)" : r.ToString(); cnt[k] = cnt.TryGetValue(k, out int v) ? v + 1 : 1;
				}
				var parts = new List<string>(); foreach (var kv in cnt) parts.Add($"{kv.Key} {100.0 * kv.Value / landc:0.0}%");
				parts.Sort(); GD.Print("GEOSOILS top of the land: " + string.Join(", ", parts));
			}
			if (HasArg("--rocks"))
			{   // (testing, Draft 2i: the rock 25 blocks under the ground of every land cell; all the land, and the land within 60 x Belt blocks of an old suture or a join)
				var cA = new Dictionary<string, int>(); var cS = new Dictionary<string, int>(); int nA = 0, nS = 0;
				for (int i = 0; i < plan.N * plan.N; i++)
				{
					if (plan.Sea[i] || plan.Lake[i]) continue;
					int bx = (i % plan.N) * plan.Cell + plan.Cell / 2, bz = (i / plan.N) * plan.Cell + plan.Cell / 2, yg = plan.GroundAt(bx, bz);
					string k = plan.RockAt(plan.ColAt(bx, bz), bx, yg - 25, bz, yg).ToString();
					nA++; cA[k] = cA.TryGetValue(k, out int v) ? v + 1 : 1;
					if (plan.SutureDist[i] * plan.Cell < 60 * plan.Belt || plan.GJoinDist[i] * plan.Cell < 60 * plan.Belt) { nS++; cS[k] = cS.TryGetValue(k, out int w) ? w + 1 : 1; }
				}
				string Top(Dictionary<string, int> d, int n) { var l = new List<KeyValuePair<string, int>>(d); l.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key)); var s = new List<string>(); for (int j = 0; j < Math.Min(9, l.Count); j++) s.Add($"{l[j].Key} {100.0 * l[j].Value / Math.Max(1, n):0.0}%"); return string.Join(", ", s); }
				GD.Print($"GEOROCKS 25 under the ground, all the land: {Top(cA, nA)}");
				GD.Print($"GEOROCKS 25 under, near old sutures and joins ({100.0 * nS / Math.Max(1, nA):0.0}% of the land): {Top(cS, nS)}");
			}
			if (HasArg("--rangecut")) { var rc = (System.Environment.GetEnvironmentVariable("RANGECUT") ?? "-1,-1").Split(','); plan.SaveRangeCut(pm[10..], int.Parse(rc[0]), int.Parse(rc[1])); }
			if (HasArg("--geoslice")) { plan.SaveSlices(pm[10..], ArgInt("--georow=", -1)); plan.SaveSlices(pm[10..], -1, "_range", true); if (System.Environment.GetEnvironmentVariable("GEOCOL") is { } gc) foreach (var c in gc.Split(';')) { var q = c.Split(','); plan.DebugCol(int.Parse(q[0]), int.Parse(q[1])); } }
			SetProcess(false); GetTree().Quit(); return;
		}
		Skin.Load();   // your font and gui_atlas.png, for every menu and label
		blockMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://src/blocks.gdshader") };
		waterMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://src/water.gdshader"), RenderPriority = 1 };
		blockMat.SetShaderParameter("atlas", Atlas.Texture);
		waterMat.SetShaderParameter("atlas", Atlas.Texture);

		env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0x8ec9ffff) };
		AddChild(new WorldEnvironment { Environment = env });
		cam = new Camera3D { Fov = 75, Near = 0.05f, Far = 1000 };
		AddChild(cam);
		AddChild(new Sound());
		sky = new Sky(); AddChild(sky);
		var at = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--time="));   // for testing: start at a time of day
		if (at != null) sky.TimeOfDay = float.Parse(at[7..], System.Globalization.CultureInfo.InvariantCulture);
		BuildUi();
		PickWorld();   // (the last world you played, or the one you just picked; --seed=N for testing: the same world every time)
		BuildMenus();
		ApplySettings();
		DisplayServer.WindowSetTitle($"{Title} {Version}");
		if (current == null) return;   // (the main menu, with no world: see PickWorld)
		world = new World(current.Seed, current.Gen, current.H, current.S, current.B, current.Land);
		if (current.Os && current.Gen == 3 && current.Land < 5 && WorldGen.EerieOn) world.Gen.OldStairs(current.OsSkip);   // (see Worlds.OldStairs)
		world.DuckNear = DuckNear;   // (ducks help the field: see Main.Animals.cs)
		genTask = Task.Run(() => { world.Generate(p => genProgress = p); FindEarthStairs(); LoadSaveBlocks(); });   // (0.1.5.5: an Earthlike world's staircases)
	}

	// ---- the screen: info in the corner, the crosshair, the hotbar ----
	void BuildUi()
	{
		var ui = new CanvasLayer(); AddChild(ui);
		info = new Label { Position = new Vector2(8, 6) };
		info.AddThemeColorOverride("font_color", Colors.White);
		info.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.6f));
		ui.AddChild(info);
		loading = new Label { Text = "Making the world...", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		loading.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		loading.AddThemeFontSizeOverride("font_size", 24);
		ui.AddChild(loading);
		var cross = new Control { MouseFilter = Control.MouseFilterEnum.Ignore }; cross.SetAnchorsPreset(Control.LayoutPreset.Center); ui.AddChild(cross);
		cross.AddChild(new ColorRect { Color = new Color(1, 1, 1, 0.8f), Position = new Vector2(-9, -1), Size = new Vector2(18, 2), MouseFilter = Control.MouseFilterEnum.Ignore });   // (the hidden pointer sits under it: clicks pass through)
		cross.AddChild(new ColorRect { Color = new Color(1, 1, 1, 0.8f), Position = new Vector2(-1, -9), Size = new Vector2(2, 18), MouseFilter = Control.MouseFilterEnum.Ignore });
		uiLayer = ui;
	}

	// ---- once the world is made ----
	void StartPlaying()
	{
		ready = true;
		RollVisit();
		stages.Clear(); foreach (var (m, _) in cracks.Values) if (GodotObject.IsInstanceValid(m)) m.QueueFree(); cracks.Clear();   // (a new world: no blocks half broken)
		ClearDoorSwings();
		loading.Visible = false;
		mesher = new ChunkMesher(world);
		player = new Player(world);
		inv = new Inventory(current.Survival);
		drops = new Drops(); AddChild(drops); drops.Init(world, player, inv); drops.DayTint = () => sky.DayTint; drops.HeldLight = HeldLightAt;
		falling = new Falling { Client = netClient }; AddChild(falling); falling.Init(world, Collect, DropFor); falling.DayTint = () => sky.DayTint; falling.HeldLight = HeldLightAt;
		kilns = new Kilns(world) { Survival = inv.Survival };
		world.Drop = Collect;
		world.NoWater = netClient;   // (in a joined game the host's game runs the water)
		drops.Client = netClient; drops.AskPickup = d => hostConn?.Send(new Dictionary<string, object> { ["t"] = "pickup", ["k"] = d.K });
		world.BlockChanged = (i, old, now, _) => { NetBlockChanged(i, now); if (Blocks.IsFurnace(old) && !Blocks.IsFurnace(now)) { if (kilnAt == i) CloseInventory(); kilns.Empty(i, Collect); } };
		BuildHud(); BuildTips(); BuildHeld();
		BuildLifeUi(); BuildArmorBar();
		InitMobs();
		world.ChunksChanged = set => { lock (dirty) foreach (int c in set) dirty.Add(c); };
		world.NearbyRebuild = (x, y, z) =>
		{
			// (sky rays only go up or sideways, so only spots at or below the change can see the difference)
			const int R = 18;
			for (int cy = 0; cy <= Math.Min(World.NCY - 1, (y + 1) / World.CS); cy++)
				for (int cz = Math.Max(0, (z - R) / World.CS); cz <= Math.Min(World.NCZ - 1, (z + R) / World.CS); cz++)
					for (int cx = Math.Max(0, (x - R) / World.CS); cx <= Math.Min(World.NCX - 1, (x + R) / World.CS); cx++)
						soon.Add(World.ChunkKey(cx, cy, cz));
		};
		for (int c = 0; c < World.NChunks; c++) { var (cx, cy, cz) = World.FromChunkKey(c); if (cy * World.CS < world.ChunkTop[cz * World.NCX + cx]) dirty.Add(c); }   // (chunks of empty sky have nothing to build)
		if (world.Lazy)
		{   // (gen 4: the land round the start grows first; then round wherever the save puts you)
			var (sx, sz) = EarthSpawn(); GrowNow(sx >> 4, sz >> 4, 3);
			player.Pos = spawnPoint = FindSpawn(sx, sz);
			if (TestRun) GD.Print($"EARTHSTART {player.Pos.X:0.0},{player.Pos.Y:0.##},{player.Pos.Z:0.0} (EarthSpawn {sx},{sz})");
		}
		else player.Pos = spawnPoint = FindSpawn();
		outline = MakeOutline(); AddChild(outline);
		ApplyPlayerSave();
		// (test: `--at=x,y,z[,yaw,pitch]` puts you there, flying)
		if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--at=")) is { } atArg)
		{
			var v = atArg[5..].Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
			player.Pos = new Vector3(v[0], v[1], v[2]); player.Flying = true;
			if (v.Length > 4) { player.Yaw = v[3]; player.Pitch = v[4]; }
		}
		if (world.Lazy) GrowNow((int)Math.Floor(player.Pos.X) >> 4, (int)Math.Floor(player.Pos.Z) >> 4, 3);
		if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--look=")) is { } lookArg)   // (test: then turn to face x,y,z)
		{
			var v = lookArg[7..].Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
			var to = new Vector3(v[0], v[1], v[2]) - (player.Pos + new Vector3(0, 1.62f, 0));
			player.Yaw = MathF.Atan2(-to.X, -to.Z); player.Pitch = MathF.Atan2(to.Y, new Vector2(to.X, to.Z).Length());
		}
		if (netClient) ConnectToHost();
		// tests go straight in; otherwise the main menu (or the Ready screen) is showing
		if ((tempWorld || HasArg("--play")) && !menuOpen) Lock(); else Unlock();
	}
	Vector3 FindSpawn(int cx = -1, int cz = -1)
	{
		// spiral out from the middle of the world (gen 4: from the plan's start) until there's dry land
		if (cx < 0) { cx = World.WX / 2; cz = World.WZ / 2; }
		for (int r = 0; r < World.WX / 2; r++)
			for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++)
			{
				if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
				int x = cx + dx, z = cz + dz, h = world.Heights[z * World.WX + x];
				int top = world.GetBlock(x, h, z);
				if (world.Lazy && (Blocks.IsLeaves(top) || Blocks.IsLogBlock(top))) continue;   // (gen 4: on the ground, not in a tree's crown)
				if (top != 0 && !Blocks.IsWater(top) && !Blocks.IsTorch(top)) return new Vector3(x + 0.5f, h + 1.01f, z + 0.5f);
			}
		return new Vector3(cx + 0.5f, World.WY - 2, cz + 0.5f);
	}

	public override void _Process(double delta)
	{
		float dt = (float)Math.Min(delta, 0.1);
		NetUpdate(dt);
		Atlas.SpinSaw(delta);   // (the Cutter's saw blade)
		if (current == null) { MenuOnly(dt); return; }
		PlaceToolRow(); PlaceTip(); UpdateHeld();
		if (ready) { HoldToBreak(dt); UpdateCracks(dt); UpdateDoorSwings(dt); }
		if (!ready)
		{
			if (genTask.IsFaulted) { loading.Text = "Couldn't make the world:\n" + genTask.Exception?.InnerException?.Message; return; }
			if (genTask.IsCompleted) StartPlaying();
			else { loading.Text = $"Making the world... {(int)(genProgress * 100)}%"; loading.Visible = !menuOpen; return; }
		}
		GrowTick(dt);   // (gen 4: the land round the players)
		bool moving = Locked && !invOpen && !dead && (!netClient || welcomed);   // (a joined game waits for the host's blocks)
		// (gen 4: held still while the land you're in hasn't grown, so you don't fall through it or get pushed up out of it)
		bool held = world.Lazy && !world.ColumnGrown(Math.Clamp((int)Math.Floor(player.Pos.X) >> 4, 0, World.NCX - 1), Math.Clamp((int)Math.Floor(player.Pos.Z) >> 4, 0, World.NCZ - 1));
		if (!dead && !held && (moving || (inv.Survival && invOpen && (!netClient || welcomed))))   // (survival keeps falling while the inventory is open)
		{
			var ev = player.Update(dt, !moving ? default : new Player.Keys
			{
				F = Input.IsPhysicalKeyPressed(Key.W), B = Input.IsPhysicalKeyPressed(Key.S),
				L = Input.IsPhysicalKeyPressed(Key.A), R = Input.IsPhysicalKeyPressed(Key.D),
				Jump = Input.IsPhysicalKeyPressed(Key.Space), Down = Input.IsPhysicalKeyPressed(Key.Shift),   // (Shift: sprint, dive, fly down)
				Sprint = Input.IsPhysicalKeyPressed(Key.Shift),
			});
			PlayerSounds(ev);
		}
		MouseLook();
		if (moving)
		{   // the arrow keys look around too (for laptops, or when the mouse won't)
			float turn = 2.4f * dt;
			if (Input.IsPhysicalKeyPressed(Key.Left)) player.Yaw += turn;
			if (Input.IsPhysicalKeyPressed(Key.Right)) player.Yaw -= turn;
			if (Input.IsPhysicalKeyPressed(Key.Up)) player.Pitch = Math.Min(1.55f, player.Pitch + turn * 0.75f);
			if (Input.IsPhysicalKeyPressed(Key.Down)) player.Pitch = Math.Max(-1.55f, player.Pitch - turn * 0.75f);
		}
		cam.Position = player.EyePos;
		cam.Rotation = new Vector3(player.Pitch, player.Yaw, 0);
		Panorama(dt);
		if (!cam.IsInsideTree()) return;   // (the scene is being swapped for another world)
		SetSkyPlace();
		sky.Tick(dt, cam);
		UpdateSkyAndWater(dt);
		UpdateHealth(dt);
		if (moving) { UpdateStairs(); UpdateEerie(dt); }
		TickLifeUi(dt);
		UpdateMobs(moving || invOpen || hosting || netClient ? dt : 0);   // (mobs wait while you're paused, like the rest of the world; a joined game follows the host)
		UpdateHand(dt);
		if ((moving || invOpen || hosting) && !netClient)
		{   // (torches burning down, clay drying and kilns all wait while you're paused)
			world.TickTimers(dt);
			kilns.Tick(dt, i => { if (i == kilnAt) DrawKiln(); });
		}
		world.UpdateWater(dt);
		UpdateScreens(dt);
		drops.Update(dt);
		falling.Update(dt);
		TickHud(dt);
		TickEverSparks(dt);
		BuildChunks();
		UpdateOutline();
		TestShot(dt);
		var p = player.Pos;
		info.Text = $"{Title} {Version}\n{Engine.GetFramesPerSecond()} fps\nx {p.X:0.0}  y {p.Y:0.0}  z {p.Z:0.0}" + (player.Flying ? "\nflying" : "") + (settings.Muted ? "\nSound off (M)" : "") + NetInfoLine();
	}

	// The main menu's panorama: behind the title, the camera slowly turns all the way round.
	// It shows the update's own panorama: six pictures (panorama/0.jpg .. 5.jpg, a cube around one spot) of somewhere that shows
	// off what the update adds, taken with make-panorama.ps1. Without them it looks out over your own world instead, from a little
	// above where you are (above the ground, even in a cave or a house). Leaving the menu puts the camera back.
	const float PanoTurn = 0.05f;   // how fast it turns (radians a second: once round in about 2 minutes)
	float panoYaw = float.NaN;
	Node3D panoBox; bool panoTried;
	// the main menu with no world (see PickWorld): only the panorama turning (or the sky, without its pictures)
	float menuShotTimer;
	void MenuOnly(float dt)
	{
		loading.Visible = false; uiLayer.Visible = false;
		if (!panoTried) { panoTried = true; panoBox = MakePanoBox(); }
		if (float.IsNaN(panoYaw)) panoYaw = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--panoyaw=")) is { } py ? float.Parse(py[10..], System.Globalization.CultureInfo.InvariantCulture) : 0;
		panoYaw += dt * PanoTurn;
		cam.Position = new Vector3(0, 100, 0);
		cam.Rotation = new Vector3(-0.1f, panoYaw, 0);
		if (panoBox != null) { panoBox.Visible = true; panoBox.GlobalPosition = cam.GlobalPosition; }
		sky.Tick(dt, cam);
		// (test: --shot=file.png takes a picture of the menu and quits)
		if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--shot=")) is { } shot && (menuShotTimer += dt) > 1.5f)
		{
			var open = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--open="));
			if (open == "--open=worlds" && !worldsPanel.Visible) { ShowWorlds(true); menuShotTimer = 0; return; }
			if (open == "--open=multi" && !multiPanel.Visible) { multiPanel.Visible = true; Host(); menuShotTimer = 0; return; }
			GetViewport().GetTexture().GetImage().SavePng(shot[7..]);
			GD.Print("shot saved (main menu, no world)");
			GetTree().Quit();
		}
	}
	// the cube's six sides: which way each picture looks, and which way is up in it (the same in the web version)
	public static readonly Vector3[] PanoDirs = { new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1) };
	public static readonly Vector3[] PanoUps = { new(0, 1, 0), new(0, 1, 0), new(0, 0, 1), new(0, 0, -1), new(0, 1, 0), new(0, 1, 0) };
	void Panorama(float dt)
	{
		if (!panoTried) { panoTried = true; panoBox = MakePanoBox(); }
		if (panoBox != null) panoBox.Visible = menuOpen;
		if (!menuOpen) { panoYaw = float.NaN; return; }
		if (float.IsNaN(panoYaw)) panoYaw = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--panoyaw=")) is { } py ? float.Parse(py[10..], System.Globalization.CultureInfo.InvariantCulture)   // (test: start turned this far)
			: panoBox != null ? 0 : player.Yaw;
		panoYaw += dt * PanoTurn;
		var e = player.EyePos;
		int x = Math.Clamp((int)MathF.Floor(e.X), 0, World.WX - 1), z = Math.Clamp((int)MathF.Floor(e.Z), 0, World.WZ - 1);
		cam.Position = new Vector3(e.X, Math.Max(e.Y, world.Heights[z * World.WX + x] + 3.5f), e.Z);
		cam.Rotation = new Vector3(-0.1f, panoYaw, 0);
		if (panoBox != null) panoBox.GlobalPosition = cam.GlobalPosition;   // (it goes wherever the camera is, so it's all you see)
	}
	// the panorama's cube: six pictures on the inside of a small cube around the camera, drawn over everything else
	Node3D MakePanoBox()
	{
		var tex = new Texture2D[6];
		for (int k = 0; k < 6; k++)
		{
			string p = $"res://panorama/{k}.jpg";
			if (!ResourceLoader.Exists(p)) return null;
			tex[k] = GD.Load<Texture2D>(p);
		}
		var box = new Node3D { Visible = false }; AddChild(box);
		for (int k = 0; k < 6; k++)
		{
			Vector3 d = PanoDirs[k], u = PanoUps[k], r = d.Cross(u);   // (a picture's right, as the camera that took it saw it)
			var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
			void V(Vector3 at, float tu, float tv) { st.SetUV(new Vector2(tu, tv)); st.AddVertex(at); }
			V(d - r + u, 0, 0); V(d + r + u, 1, 0); V(d + r - u, 1, 1);
			V(d - r + u, 0, 0); V(d + r - u, 1, 1); V(d - r - u, 0, 1);
			var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoTexture = tex[k], NoDepthTest = true, DisableFog = true,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, RenderPriority = 127,
				TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear };
			box.AddChild(new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
		}
		return box;
	}
	// (make-panorama.ps1: `--panorama=<folder>` takes the six pictures from where you are, then quits)
	bool panoCapturing;
	async void CapturePanorama(string dir)
	{
		panoCapturing = true;
		if (handRoot != null) handRoot.Visible = false;
		if (outline != null) outline.Visible = false;
		var vp = new SubViewport { Size = new Vector2I(1024, 1024), RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
		var c = new Camera3D { Fov = 90, Near = 0.05f, Far = 1000 };
		vp.AddChild(c); AddChild(vp);
		DirAccess.MakeDirRecursiveAbsolute(dir);
		var at = cam.GlobalPosition;
		for (int k = 0; k < 6; k++)
		{
			c.LookAtFromPosition(at, at + PanoDirs[k], PanoUps[k]);
			for (int f = 0; f < 4; f++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			vp.GetTexture().GetImage().SaveJpg($"{dir}/{k}.jpg", 0.9f);
		}
		GD.Print($"panorama saved to {dir} from {at}");
		GetTree().Quit();
	}

	// footsteps, jumps, landings and splashes; a hard landing hurts (survival)
	void PlayerSounds(Player.Events ev)
	{
		var s = Sound.I; if (s == null) return;
		if (ev.Jumped) s.Jump(player.BlockUnder());
		if (ev.Splashed) s.Splash(ev.FallSpeed);
		if (ev.Stroke) s.Swim();
		if (ev.Step) s.Step(player.BlockUnder());
		if (ev.Landed)
		{
			if (ev.Impact > 5) s.Land(player.BlockUnder(), ev.Impact);
			float fell = ev.Impact * ev.Impact / 56;   // (about how many blocks you fell)
			if (fell > 3.5f && !stairFall) HurtPlayer((int)MathF.Floor(fell - 3));
			stairFall = false;
		}
	}
	// the sky colour behind everything, the haze, and how it all looks with your head underwater
	void UpdateSkyAndWater(float dt)
	{
		var e = cam.GlobalPosition;
		int ex = (int)MathF.Floor(e.X), ey = (int)MathF.Floor(e.Y), ez = (int)MathF.Floor(e.Z);
		// in a cave the haze darkens (less sky reaches you)
		if ((camSkyCheck -= dt) <= 0) { camSkyCheck = 0.2f; target = world.SkyExposure(ex, ey, ez, null); }
		camSky += (target - camSky) * Math.Min(1, dt * 1.5f);
		bool under = Blocks.IsWater(world.GetBlock(ex, ey, ez));
		Vector3 fog;
		if (under)
		{
			float l = Math.Max(0.15f, sky.DayTint.Y * camSky);
			fog = new Vector3(0.07f, 0.2f, 0.45f) * l;
			env.BackgroundColor = new Color(fog.X, fog.Y, fog.Z);
		}
		else
		{
			var c = sky.Now; env.BackgroundColor = c;
			fog = new Vector3(c.R, c.G, c.B) * camSky;
		}
		RenderingServer.GlobalShaderParameterSet("fog_color", fog);
		if (under != wasUnder)
		{
			wasUnder = under;
			RenderingServer.GlobalShaderParameterSet("fog_thickness", under ? 0.22f : FogThickness);
			RenderingServer.GlobalShaderParameterSet("fog_max", under ? 0.97f : 0.82f);
			cam.Far = under ? 40 : 1000;
			waterOverlay.Visible = under;
			Sound.I?.Underwater(under);
		}
		// night music comes on once it's properly dark, and goes back to day music once it's light again
		nightMusic = nightMusic ? sky.SkyDay < 0.5f : sky.SkyDay < 0.12f;
		Sound.I?.Music(nightMusic ? "night" : "day");
		if ((riverScan -= dt) <= 0) { riverScan = 0.5f; var (calm, rapid) = RiverHeard(); Sound.I?.River(calm, rapid); }   // (Beta 0.1.5.4: the river's sound)
		if (Sound.I != null) Sound.I.Hushed = NearStair();
	}
	float target = 1;

	// for testing: `-- --shot=file.png` saves a picture of the screen once the world has loaded, then quits
	(int x, int g, int z)? fallAt;   // (--falltest)
	bool caveTested, caveFound;   // (--cavetest)
	bool clockItemsTested, mixClockGiven;   // (--clockitems; --mixtest: the host's Clock)
	// (test, Beta 0.1.6.1: --mixtest) the shared clock on this side, the same line as the web version's: drift = the clock less the
	// wall time (Unix seconds; the same on both sides once a friend has snapped to the host's clock), the date and the part of the day
	// here, the moon, what's held, and the live pictures this side shows for a Clock or Calendar (its own hand, other players' hands,
	// dropped ones)
	void Mix8(string tag)
	{
		var ic = System.Globalization.CultureInfo.InvariantCulture;
		double wall = Time.GetUnixTimeFromSystem();
		int i = Sky.PartOf(sky.TimeOfDay), s = Sky.SeasonOf(sky.Day, sky.PlaceSouth);
		string Pic(int id) => $"\"{id}:{Atlas.Names[Blocks.B[id].Frames[Atlas.LiveFrame(id)]]}\"";
		var shown = new List<int>();
		if (handBody?.HeldMesh != null && Blocks.IsLive(handBody.HeldId)) shown.Add(handBody.HeldId);
		foreach (var o in others.Values) if (o.Av?.HeldMesh != null && Blocks.IsLive(o.Av.HeldId)) shown.Add(o.Av.HeldId);
		foreach (var d in drops.All) if (Blocks.IsLive(d.Id)) shown.Add(d.Id);
		GD.Print(string.Format(ic, "MIX8{0} {{\"ver\":\"{1}\",\"drift\":{2:F1},\"clock\":{3:F1},\"date\":\"{4}\",\"part\":\"{5}\",\"live\":[{6},{7}],\"el\":{8:F4},\"held\":{9},\"clockPics\":[{10}]}}",
			tag, Version, sky.Clock - wall, sky.Clock, Sky.CalendarWords(sky.Clock, sky.PlaceSouth), Sky.Parts[i], i, s, sky.MoonEl, inv.Id[inv.Selected], string.Join(",", shown.Select(Pic).Distinct())));
	}
	float shotTimer; bool timeArgDone, skinTested, dropsTested, farmTested, cowsTested, snowTested, cutTested, throwTested, throwShown, hudTested, stageTested, testGear, heldTested, heldPlaced, longChecked, doorTested, saplingTested, cropTested, wildTested, fenceTested, animalTested, clothShown, neckTested, cowShown, animalChecked, bagTested, bagShown, bagNetSet, bagNetDone, osTested, osNetDone, netTested, earthLifeTested; float bagNetT, mixT; int mixStep;
	(int x, int y, int z, int id) mixRefill;   // (Beta 0.1.5.4: the river spot an Earthlike host refills in --mixtest)
	// (test, --mixtest: the world as it stands, every block's full id in index order, as an FNV-1a fingerprint; and the changed blocks)
	// (gen 4: over the 7 x 7 chunk columns round the plan's start, EarthSpawn's column, clamped to the map: y, then z, then x over
	// the box's blocks; grown first, so both editions read the same land. A column not grown would read Bedrock.)
	void MixLive(int n)
	{
		var sw = System.Diagnostics.Stopwatch.StartNew(); uint h = 2166136261;
		if (world.Lazy)
		{
			var (sx, sz) = EarthSpawn(); int cx = sx >> 4, cz = sz >> 4;
			int x0 = Math.Max(0, cx - 3) * 16, x1 = Math.Min(World.NCX - 1, cx + 3) * 16 + 15, z0 = Math.Max(0, cz - 3) * 16, z1 = Math.Min(World.NCZ - 1, cz + 3) * 16 + 15;
			GrowNow(cx, cz, 3);
			for (int y = 0; y < World.WY; y++) for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++) h = (h ^ (uint)world.Blocks.Get(x, y, z)) * 16777619;
			GD.Print($"MIXLIVE {n} {(hosting ? "host" : "friend")}: {h:x8} (the 7 x 7 round the start, blocks {x0}-{x1}, {z0}-{z1}), {world.Edits.Count} changed blocks (read in {sw.ElapsedMilliseconds} ms)");
			return;
		}
		for (int y = 0; y < World.WY; y++) for (int z = 0; z < World.WZ; z++) for (int x = 0; x < World.WX; x++) h = (h ^ (uint)world.Blocks.Get(x, y, z)) * 16777619;
		GD.Print($"MIXLIVE {n} {(hosting ? "host" : "friend")}: {h:x8}, {world.Edits.Count} changed blocks (read in {sw.ElapsedMilliseconds} ms)");
	} List<(Mob m, Vector3 at, float yaw, bool graze)> picHold;
	void TestShot(float dt)
	{
		var args = OS.GetCmdlineUserArgs();
		string arg = args.FirstOrDefault(a => a.StartsWith("--shot="));
		if (arg == null) return;
		if (args.Contains("--scene") && !sceneBuilt) BuildTestScene();
		if (args.Contains("--piecescene") && !pieceSceneBuilt && !menuOpen) BuildPieceScene();
		if (args.Contains("--holescene") && !holeSceneBuilt && !menuOpen) BuildHoleScene();
		if (args.Contains("--casingscene") && !casingSceneBuilt && !menuOpen) BuildCasingScene();
		if (args.Contains("--aoscene") && !aoSceneBuilt && !menuOpen) BuildAoScene();
		if (args.Contains("--icescene") && !iceSceneBuilt && !menuOpen) BuildIceScene();
		if (args.Contains("--doorscene") && !doorSceneBuilt && !menuOpen) BuildDoorScene();
		if (args.Contains("--selftest") && !sceneBuilt) { sceneBuilt = true; SelfTest(); }
		if (args.Contains("--die") && !diedTest && inv.Survival) { diedTest = true; gloomDeath = true; HurtPlayer(99); if (!dead) { gloomDeath = true; Die(); } }
		// multiplayer tests: --host (host this world), --join=CODE (join a game)
		if (args.Contains("--host") && peer == null && !hosting && !netTried) { netTried = true; Host(); }
		var jn = args.FirstOrDefault(a => a.StartsWith("--join="));
		if (jn != null && peer == null && !netClient && !netTried) { netTried = true; Join(jn[7..]); }
		if (args.Contains("--nettest") && !netTested && !menuOpen && shotTimer > 0.3f) { netTested = true; NetTest(); }
		if (args.Contains("--savetest") && !savetestDone)
		{   // run twice: the first time builds a little tower and saves; the second time checks it came back
			savetestDone = true;
			int x = (int)spawnPoint.X + 3, z = (int)spawnPoint.Z, y = world.Heights[z * World.WX + x] + 1;
			bool has = world.Edits.Count > 0;
			if (!has)
			{
				for (int k = 0; k < 4; k++) world.SetBlock(x, y + k, z, Blocks.Brick == 0 ? 12 : 12);
				world.SetPiece(x + 1, y, z, 0, Blocks.Cobblestone);
				inv.SetSlot(2, 12, 0); sky.TimeOfDay = 0.3f; player.Yaw = 1.0f;
				GD.Print("savetest: built and saved = " + SaveWorld());
			}
			else
			{
				// (the tower from the saved edits: an Earthlike world's start can land on top of it, which moves x)
				var tower = world.Edits.Where(e => e.Value == 12).Select(e => World.FromIdx(e.Key)).ToList();
				bool towerBack = tower.Count == 4 && tower.All(t => t.x == tower[0].x && t.z == tower[0].z && world.GetBlock(t.x, t.y, t.z) == 12);
				GD.Print((towerBack ? "PASS" : "FAIL") + " tower came back");
				GD.Print((world.PieceMap.Count > 0 ? "PASS" : "FAIL") + " piece came back");
				GD.Print((MathF.Abs(sky.TimeOfDay - 0.3f) < 0.01f ? "PASS" : "FAIL") + " time of day came back");
				GD.Print((MathF.Abs(player.Yaw - 1.0f) < 0.01f ? "PASS" : "FAIL") + " where you look came back");
				GD.Print("worlds: " + string.Join(", ", Worlds.List().Select(w => w.Name + " " + w.Seed)));
			}
		}
		if (args.Contains("--play") && menuOpen) { ShowMainMenu(false); Lock(); }   // menus closed, as if playing
		if (!clockArgDone)
		{   // (test, Beta 0.1.6.1: --clock=SECONDS, --day=N (keeping the time of day), --season=spring|summer|autumn|winter (its first day, for where you are))
			clockArgDone = true; var inv0 = System.Globalization.CultureInfo.InvariantCulture;
			if (args.FirstOrDefault(a => a.StartsWith("--clock=")) is { } ca) sky.Clock = double.Parse(ca[8..], inv0);
			if (args.FirstOrDefault(a => a.StartsWith("--day=")) is { } da) sky.Clock = long.Parse(da[6..], inv0) * Sky.DayLength + (sky.Clock - sky.Day * Sky.DayLength);
			if (args.FirstOrDefault(a => a.StartsWith("--season=")) is { } sa && sa.Length > 9 && Array.IndexOf(Sky.Seasons, char.ToUpper(sa[9]) + sa[10..]) is int si and >= 0)
			{
				SetSkyPlace(); long day = sky.Day; int dl = Sky.DayOfYear(day, sky.PlaceSouth);
				sky.Clock += ((si * 6 - dl) % 24 + 24) % 24 * Sky.DayLength;
			}
		}
		var timeArg = args.FirstOrDefault(a => a.StartsWith("--time="));   // (test: the time of day again, after the world loaded its own)
		if (timeArg != null && !timeArgDone) { timeArgDone = true; sky.TimeOfDay = float.Parse(timeArg[7..], System.Globalization.CultureInfo.InvariantCulture); }
		var holdArg = args.FirstOrDefault(a => a.StartsWith("--hold="));   // (test: put item ID in the first hotbar slot and hold it)
		if (holdArg != null && int.TryParse(holdArg[7..], out int holdId) && inv.Id[0] != holdId) { inv.SetSlot(0, holdId); SelectSlot(0); }
		if (args.Contains("--gear") && !gearShown && inv.Survival && !menuOpen)
		{   // (test: survival gear to look at: iron armor, an iron backpack with things in it, a bronite belt, tools, Pockets)
			gearShown = true;
			for (int k = 0; k < 4; k++) inv.SetSlot(Inventory.Armor0 + k, Blocks.ArmorItem(2, k), 1, k == 1 ? 120 : 0);
			inv.SetSlot(Inventory.BackSlot, Blocks.Backpacks[2]); inv.SetSlot(Inventory.BeltSlot, Blocks.Belts[3]);
			inv.SetSlot(Inventory.Tool0, Blocks.Tools[1]); inv.SetSlot(Inventory.Tool0 + 1, 649 + 1, 1, 200); inv.SetSlot(Inventory.Tool0 + 2, Blocks.CrudeAxe);
			inv.SetSlot(Inventory.Off0, Blocks.Torch, 12); inv.SetSlot(Inventory.Off0 + 1, Blocks.Cobblestone, 40);
			inv.SetSlot(Inventory.Pocket0, Blocks.Leather, 9); inv.SetSlot(Inventory.Pocket0 + 1, Blocks.MetalBrick["iron"], 14); inv.SetSlot(Inventory.Pocket0 + 2, Blocks.Backpacks[0]);
			inv.SetSlot(Inventory.Pack0, Blocks.Rock, 30); inv.SetSlot(Inventory.Pack0 + 1, Blocks.Grain, 8); inv.SetSlot(Inventory.Pack0 + 2, Blocks.Planks, 64);
			inv.SetSlot(0, Blocks.Dirt, 20); inv.SetSlot(1, Blocks.Tools[1]);
		}
		if (args.Contains("--inventory") && !invOpen && !menuOpen) OpenPanel(invPanel);   // (test: the inventory open)
		var varArg = args.FirstOrDefault(a => a.StartsWith("--search="));   // (test: the creative inventory searching for this)
		if (varArg != null && invOpen && creativeSearch != null && creativeSearch.Text == "") { creativeSearch.Text = varArg[9..].Replace("_", " "); ShowCreative(); }
		var pressArg = args.FirstOrDefault(a => a.StartsWith("--press="));   // (test: hold this key down, e.g. --press=Left)
		if (pressArg != null && !menuOpen && !Input.IsPhysicalKeyPressed(Enum.Parse<Key>(pressArg[8..])))
			Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Enum.Parse<Key>(pressArg[8..]), Pressed = true });
		var clicksArg = args.FirstOrDefault(a => a.StartsWith("--clicks="));   // (test: click these buttons in turn, by their words, e.g. --clicks=Singleplayer,Play)
		if (clicksArg != null && clickStep < clicksArg[9..].Split(',').Length && clickWait-- <= 0)
		{   // (test: click whichever "Play" button is showing, with real-looking mouse clicks, as a person would)
			clickWait = 30;
			Button FindPlay(Node n) { foreach (var ch in n.GetChildren()) { if (ch is Button b && b.IsVisibleInTree() && b.Text.StartsWith(clicksArg[9..].Split(',')[clickStep], StringComparison.OrdinalIgnoreCase)) return b; var f = FindPlay(ch); if (f != null) return f; } return null; }
			var pb = FindPlay(this);
			if (pb != null)
			{
				var at = pb.GetGlobalRect().GetCenter() * GetWindow().ContentScaleFactor;   // (the window's pixels)
				clickStep++;
				GD.Print($"clicks: clicking \"{pb.Text}\" ({pb.GetPath()}) at {at}");
				Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at, Device = TestDevice });
				Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left, Device = TestDevice });
				Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = false, Device = TestDevice });
			}
		}
		if (args.Contains("--mousemove") && !menuOpen && Locked)   // (test: the mouse moving right: a made-up movement, 8 pixels a frame; the real pointer is left alone)
		{
			var mid = (Vector2)(DisplayServer.WindowGetSize() / 2);
			Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(8, 0), Position = mid, GlobalPosition = mid, Device = TestDevice });
			if ((int)(shotTimer * 2) != (int)((shotTimer - (float)GetProcessDeltaTime()) * 2))
				GD.Print($"mousemove: yaw {player.Yaw:F2}, mouse over: {GetViewport().GuiGetHoveredControl()?.GetPath()}");
		}
		var dropArg = args.FirstOrDefault(a => a.StartsWith("--drops="));     // (test: these items lying in a row in front of you)
		if (dropArg != null && !dropsTested && !menuOpen && shotTimer > 0.5f)
		{
			dropsTested = true;
			var ids = dropArg[8..].Split(',').Select(int.Parse).ToArray();
			var fwd = new Vector3(-MathF.Sin(player.Yaw), 0, -MathF.Cos(player.Yaw)); var side = new Vector3(fwd.Z, 0, -fwd.X);
			for (int i = 0; i < ids.Length; i++)
			{
				var p = player.Pos + fwd * 2.2f + side * ((i - (ids.Length - 1) / 2f) * 0.45f);
				var d = drops.Add(ids[i], p.X, player.Pos.Y, p.Z); d.V = Vector3.Zero;
			}
		}
		var swingArg = args.FirstOrDefault(a => a.StartsWith("--swing="));   // (test: your hand frozen part way through a swing, 0-1)
		if (swingArg != null) testSwing = float.Parse(swingArg[8..], System.Globalization.CultureInfo.InvariantCulture);
		var slotArg = args.FirstOrDefault(a => a.StartsWith("--slot="));   // (test: hold what's in hotbar slot N, 1-10)
		if (slotArg != null && int.TryParse(slotArg[7..], out int slotN) && inv.Selected != slotN - 1) SelectSlot(slotN - 1);
		if (args.Contains("--skineditor") && !skinTested)
		{   // (test: the skin editor open, with a mirrored stroke, a fill, and a spot painted through the 3D figure)
			skinTested = true; OpenSkinEditor();			skMirror = true; Skin.Pickable(skMirrorBtn, true); SkChoose("pencil");
			for (int y = 20; y < 24; y++) SkPaint(4, y);
			GD.Print((skEdit.GetPixel(28, 22).ToHtml(false) == skColour.ToHtml(false) ? "PASS" : "FAIL") + " mirror paints the other arm");
			SkChoose("fill"); SkSetColour(new Color(0xf2c12eff), true); SkPaint(40, 10);
			GD.Print((skEdit.GetPixel(44, 15).ToHtml(false) == "f2c12e" && skEdit.GetPixel(36, 10).ToHtml(false) != "f2c12e" ? "PASS" : "FAIL") + " fill stays on its face");
			SkChoose("pencil"); SkPlaceCamera();
			var mid = (Vector2)skView.Size / 2; var spot = FigureSpot(mid);
			GD.Print((spot != null && SkFaceAt(spot.Value.Item1, spot.Value.Item2) != null ? "PASS" : "FAIL") + $" the figure under the middle of the view is on the picture ({spot})");
			if (PlayerSkin.Image == null)
			{   // (only when you have no skin saved: Done saves this one, your hand is made in it, then it's removed again)
				CloseSkinEditor(true); UpdateHand(0.016f);
				bool ok = PlayerSkin.Image != null && handBody?.Mats.Any(m => (Texture2D)m.GetShaderParameter("tex") == PlayerSkin.Texture) == true;
				GD.Print((ok ? "PASS" : "FAIL") + " Done saves the skin and your hand wears it");
				GD.Print((PlayerSkin.ImageFrom(PlayerSkin.DataUrl) != null ? "PASS" : "FAIL") + " the skin goes over the network and back");
				PlayerSkin.Set(null); OpenSkinEditor();
			}
			if (args.Contains("--skinpicker"))
			{   // (test: the colour picker open, after dragging in its square and typing a colour)
				GetTree().CreateTimer(0.2).Timeout += () => OpenColourPicker(skSwatch.GetParent<Control>());
				skSat = 1; skVal = 1; SkFromHsv();
				GD.Print((skColour.ToHtml(false) == "ffc12e" || skColour.R > 0.99f ? "PASS" : "FAIL") + $" the picker's square sets the colour ({skColour.ToHtml(false)})");
				skHexIn.Text = "7b3fb0"; skHexIn.EmitSignal(LineEdit.SignalName.TextChanged, "7b3fb0");
				GD.Print((skColour.ToHtml(false) == "7b3fb0" ? "PASS" : "FAIL") + " typing a colour sets it");
			}
		}
		if (args.Contains("--mobs") && !mobsShown)
		{   // a Dan and the night mob (Grr Angry until the Zombie is switched on) just in front of you, facing you
			mobsShown = true;
			if (menuOpen) ShowMainMenu(false);
			Lock();   // playing, so the pause screen stays away
			var ya = args.FirstOrDefault(a => a.StartsWith("--yaw="));
			if (ya != null) player.Yaw = float.Parse(ya[6..], System.Globalization.CultureInfo.InvariantCulture);
			var fwd = new Vector3(-MathF.Sin(player.Yaw), 0, -MathF.Cos(player.Yaw)); var side = new Vector3(fwd.Z, 0, -fwd.X);
			// --figures: the Player and Zombie models instead (test only: shows their jointed animations, even with the Zombie off)
			var list = args.Contains("--figures") ? new[] { ("player", -1.0f), ("zombie", 1.0f) } : new[] { ("danthechocolateman", -1.2f), (ZombieReady ? "zombie" : "grrangry", 1.4f) };
			// --mobtypes=skeleton,gloomling: these mobs instead, side by side (test)
			var mtArg = args.FirstOrDefault(a => a.StartsWith("--mobtypes="));
			if (mtArg != null) { var ts = mtArg[11..].Split(','); list = ts.Select((ty, i) => (ty, (i - (ts.Length - 1) / 2f) * 2.2f)).ToArray(); }
			foreach (var (type, off) in list)
			{
				var at = player.Pos + fwd * 4.5f + side * off; int x = (int)MathF.Floor(at.X), z = (int)MathF.Floor(at.Z), y = world.Heights[z * World.WX + x] + 1;
				Mob m;
				if (MobTypes.ContainsKey(type)) m = SpawnMob(type, at.X, y, at.Z);
				else
				{
					m = new Mob { K = ++mobCount, Type = type, T = new MobType { Name = type, Health = 10, Scale = 0.9f, Shamble = type == "zombie" }, X = at.X, Y = y, Z = at.Z, Health = 10 };
					BuildMob(m); mobList.Add(m);
				}
				m.Yaw = player.Yaw + MathF.PI + off * 0.3f; m.Timer = 99; m.GoalCheck = 99;
				if (args.Contains("--figures")) m.Yaw = player.Yaw + MathF.PI / 2;   // side on, to see how the arms and legs move
				if (type == "player") SetHeld(m, holdArg != null && int.TryParse(holdArg[7..], out int hid) ? hid : Blocks.CrudeAxe);   // and holding something
				if (type == "player" && args.Contains("--gear")) SetGear(m, new[] { Blocks.ArmorItem(2, 0), Blocks.ArmorItem(2, 1), Blocks.ArmorItem(2, 2), Blocks.ArmorItem(2, 3), Blocks.Backpacks[2], Blocks.Belts[3] });   // (test: wearing gear)
			}
			player.Pitch = -0.15f;
			var pa = args.FirstOrDefault(a => a.StartsWith("--pitch="));
			if (pa != null) player.Pitch = float.Parse(pa[8..], System.Globalization.CultureInfo.InvariantCulture);
			if (inv.Survival) { health = 13; gloom = 3; changeTime = 0; DrawHearts(); }
		}
		if (args.Contains("--cows") && !cowsTested && !menuOpen)
		{   // (test: cows on a grass field up in the air: the 8-a-chunk limit, feeding, growing up, mating, crowding, grazing, drops)
			cowsTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			foreach (var o in mobList.ToArray()) RemoveMob(o);
			int x0 = (int)MathF.Floor(player.Pos.X / 16) * 16 - 16, z0 = (int)MathF.Floor(player.Pos.Z / 16) * 16, fy = 150;
			for (int x = x0; x < x0 + 64; x++) for (int z = z0; z < z0 + 16; z++) { world.SetBlock(x, fy, z, Blocks.Grass); world.SetBlock(x, fy + 1, z, 0); world.SetBlock(x, fy + 2, z, 0); }
			for (int x = x0 - 1; x <= x0 + 64; x++) for (int z = z0 - 1; z <= z0 + 16; z++)
				if (x < x0 || x >= x0 + 64 || z < z0 || z >= z0 + 16) { world.SetBlock(x, fy + 1, z, Blocks.Stone); world.SetBlock(x, fy + 2, z, Blocks.Stone); }   // (a wall round it)
			float Y = fy + 1;
			var A = (x0 / 16, z0 / 16);
			// 1. the 8-a-chunk limit is only for breeding: 12 cows can all be in one chunk
			for (int k = 0; k < 12; k++) SpawnCow(x0 + 2 + k % 6 * 2, Y, z0 + 3 + k / 6 * 4);
			Check($"12 cows can all be in one chunk (got {CowsInChunk(A)})", CowsInChunk(A) == 12);
			foreach (var o in mobList.ToArray()) RemoveMob(o);
			// 2. two happy cows with room, in a chunk with 8 cows in all (a calf and a fat cow among them): no calf.
			//    With one of the others gone (7), they have one.
			Mob MakePair(out Mob b2) { var a2 = SpawnCow(x0 + 8, Y, z0 + 8); b2 = SpawnCow(x0 + 9, Y, z0 + 8); a2.Happy = b2.Happy = 100; return a2; }
			var others = new List<Mob> { SpawnCow(x0 + 0.5f, Y, z0 + 0.5f, CalfTime), SpawnCow(x0 + 15.5f, Y, z0 + 0.5f), SpawnCow(x0 + 0.5f, Y, z0 + 15.5f),
				SpawnCow(x0 + 15.5f, Y, z0 + 15.5f), SpawnCow(x0 + 0.5f, Y, z0 + 8), SpawnCow(x0 + 15.5f, Y, z0 + 8) };
			for (int k = 0; k < FatAt; k++) Feed(others[1]);
			others[1].Happy = 0;
			var pa = MakePair(out var pb);
			bool Born() => mobList.Count(o => o.T.Cow && o.Baby > 0) > 1;
			for (int f = 0; f < 600 && !Born(); f++) { CowThink(pa, 0.1f); CowThink(pb, 0.1f); }
			Check($"8 cows in the chunk (calf and fat cow count too): no breeding ({CowsInChunk(A)} cows)", CowsInChunk(A) == 8 && !Born());
			RemoveMob(others[5]); pa.MateCool = pb.MateCool = 0; pa.Happy = pb.Happy = 100;
			for (int f = 0; f < 600 && !Born(); f++) { CowThink(pa, 0.1f); CowThink(pb, 0.1f); }
			Check($"7 cows: they have a calf ({CowsInChunk(A)} cows now)", Born() && CowsInChunk(A) == 8);
			foreach (var o in mobList.ToArray()) RemoveMob(o);
			// 3. feeding Grain: fatter (the fat model after FatAt), happy, and it stops wanting it when full
			var c = SpawnCow(x0 + 40.5f, Y, z0 + 8);
			for (int k = 0; k < FatAt; k++) Feed(c);
			Check($"after {FatAt} Grain it's a fat cow (fed {c.Fed}, model {ModelOf(c)})", c.Fed == FatAt && c.Variant == 2 && ModelOf(c) == "cowfat" && c.Happy > 0);
			Feed(c); Feed(c);
			Check($"fed up to {FedMax}, then it turns Grain down", c.Fed == FedMax && !Feed(c));
			// 4. drops: 1 + Grain eaten Raw Meat; Leather by room (all the room in the world: 3)
			c.Room = 1; int before = drops.All.Count; MobDies(c);
			int meat = drops.All.Skip(before).Count(d => d.Id == Blocks.RawMeat), leather = drops.All.Skip(before).Count(d => d.Id == Blocks.Leather);
			Check($"a well-fed cow with room drops {meat} Raw Meat and {leather} Leather (expect {1 + FedMax} and 3)", meat == 1 + FedMax && leather == 3);
			var crowdedCow = SpawnCow(x0 + 44.5f, Y, z0 + 8); crowdedCow.Room = 0.2f; before = drops.All.Count; MobDies(crowdedCow);
			Check("a crowded, unfed cow drops 1 Raw Meat and 1 Leather", drops.All.Skip(before).Count(d => d.Id == Blocks.RawMeat) == 1 && drops.All.Skip(before).Count(d => d.Id == Blocks.Leather) == 1);
			foreach (var o in mobList.ToArray()) RemoveMob(o);
			foreach (var d in drops.All.ToArray()) drops.Remove(d);
			// 5. a calf grows up into a cow
			var calf = SpawnCow(x0 + 40.5f, Y, z0 + 8, 1);
			Check("a calf wears the calf model", calf.Variant == 1 && ModelOf(calf) == "cowbaby" && calf.Health == CalfHealth);
			CowThink(calf, 1.5f);
			Check("and grows up into a cow", calf.Baby == 0 && calf.Variant == 0 && calf.Health == MobTypes["cow"].Health);
			RemoveMob(calf);
			// 6. two happy cows with room: a calf
			var a = SpawnCow(x0 + 40.5f, Y, z0 + 8); var b = SpawnCow(x0 + 41.5f, Y, z0 + 8);
			a.Happy = b.Happy = 100;
			for (int f = 0; f < 600 && mobList.Count(o => o.T.Cow) == 2; f++) { CowThink(a, 0.1f); CowThink(b, 0.1f); }
			Check($"two happy cows with room have a calf ({mobList.Count(o => o.T.Cow && o.Baby > 0)} calf)", mobList.Count(o => o.T.Cow && o.Baby > 0) == 1 && a.MateCool > 0 && b.MateCool > 0);
			foreach (var o in mobList.ToArray()) RemoveMob(o);
			// 7. crowded: five happy cows close together never mate
			var five = Enumerable.Range(0, 5).Select(k => SpawnCow(x0 + 34.5f + k % 3, Y, z0 + 7 + k / 3)).ToList();
			foreach (var o in five) { o.Happy = 100; o.RoomIn = 0; }
			for (int f = 0; f < 600; f++) foreach (var o in five) { CowThink(o, 0.1f); o.Grazing = 0; }
			Check("five happy cows crowded together don't mate", mobList.Count(o => o.T.Cow) == 5);
			Check("happy cows' hearts don't make them mate: feeding alone doesn't either", five.All(o => o.Mate == null));
			foreach (var o in mobList.ToArray()) RemoveMob(o);
			// 8. grazing: an unhappy cow eats the grass under it (Dirt), gets happy, and the grass grows back
			var gz = SpawnCow(x0 + 52.5f, Y, z0 + 8); gz.Walking = false;
			int gx = (int)MathF.Floor(gz.X), gzz = (int)MathF.Floor(gz.Z);
			for (int f = 0; f < 400 && world.GetBlock(gx, fy, gzz) == Blocks.Grass; f++) { gz.Walking = false; CowThink(gz, 0.1f); }
			Check("an unhappy cow grazes: the grass under it is eaten", world.GetBlock(gx, fy, gzz) == Blocks.Dirt && world.Grazed.ContainsKey(World.Idx(gx, fy, gzz)) && gz.Happy > 0);
			world.TickTimers(World.RegrowMax + 2);
			world.TickTimers(1.1f);
			Check("and it grows back to Grass", world.GetBlock(gx, fy, gzz) == Blocks.Grass && !world.Grazed.ContainsKey(World.Idx(gx, fy, gzz)));
			RemoveMob(gz);
			// for the picture: a cow, a calf and a fat cow side by side, happy (hearts), in front of you
			player.Pos = new Vector3(x0 + 40.5f, Y + 0.3f, z0 + 10.5f); player.Yaw = MathF.PI; player.Pitch = -0.3f; player.Flying = true;
			var p1 = SpawnCow(x0 + 37f, Y, z0 + 5); var p2 = SpawnCow(x0 + 43.5f, Y, z0 + 5.5f, CalfTime); var p3 = SpawnCow(x0 + 40.3f, Y, z0 + 5);
			for (int k = 0; k < FatAt; k++) Feed(p3);
			foreach (var o in new[] { p1, p2, p3 }) { o.Yaw = 2.5f; o.TurnTo = o.Yaw; o.Walking = false; o.Timer = 999; o.Happy = 100; o.MateCool = 999; o.GrazeIn = 999; o.Grazing = 0; }
			p1.Grazing = 999;   // (head down, eating)
		}
		if (args.Contains("--falltest") && fallAt == null && !menuOpen && shotTimer > 0.5f)   // (once the world is built)
		{   // (test: Sand, Gravel, Dirt and Sand put down in the air in front of you; they fall and stack up on the ground)
			player.Yaw = 0; player.Pitch = 1.2f; player.Flying = true;
			int x = (int)MathF.Floor(player.Pos.X), z = (int)MathF.Floor(player.Pos.Z) - 6, g = world.Heights[z * World.WX + x];
			for (int u = 1; u < 20; u++) world.SetBlock(x, g + u, z, 0);
			int[] ids = { Blocks.Sand, Blocks.Gravel, Blocks.Dirt, Blocks.Sand };
			for (int k = 0; k < 4; k++) world.SetBlock(x, (int)player.Pos.Y + 25 + k * 2, z, ids[k]);
			fallAt = (x, g, z);
		}
		if (args.FirstOrDefault(a => a.StartsWith("--cavetest=")) is { } ct && !caveTested && !menuOpen)
		{   // (test: find an open cave near the middle of the world at about this height, light it with torches and look around it)
			caveTested = true;
			int want = int.Parse(ct[11..]);
			bool Open(int x, int y, int z) => world.GetBlock(x, y, z) == 0 && world.GetBlock(x, y + 1, z) == 0;
			for (int r = 0; r < 200 && !caveFound; r += 2)
				for (int dz = -r; dz <= r && !caveFound; dz += 2) for (int dx = -r; dx <= r && !caveFound; dx += 2)
				{
					int x = World.WX / 2 + dx, z = World.WZ / 2 + dz;
					for (int y = want + 20; y > want - 20 && !caveFound; y--)
						if (Open(x, y, z) && Open(x + 3, y, z) && Open(x - 3, y, z) && Open(x, y, z + 3) && Open(x, y, z - 3))
						{
							caveFound = true;
							player.Pos = new Vector3(x + 0.5f, y, z + 0.5f); player.Flying = true; player.Pitch = -0.2f;
							for (int ox = -9; ox <= 9; ox += 3) for (int oz = -9; oz <= 9; oz += 3)
								for (int ty = y + 4; ty > y - 10; ty--)
									if (world.GetBlock(x + ox, ty, z + oz) == 0 && world.GetBlock(x + ox, ty - 1, z + oz) is int u && u != 0 && !Blocks.B[u].Transparent) { world.SetBlock(x + ox, ty, z + oz, Blocks.Torch); break; }
							GD.Print($"cavetest: at {x},{y},{z}: stone layers here " + string.Join(" ", Enumerable.Range(-12, 25).Select(d => Blocks.StoneLayer(world.GetBlock(x, y + d, z + 4)))));
						}
				}
		}
		if (args.Contains("--riverplay") && !riverPlayed && !menuOpen && shotTimer > 0.3f)
		{   // (test, Beta 0.1.5.4: a real Earthlike river near the start carries you its way)
			int px = (int)player.Pos.X, pz = (int)player.Pos.Z; bool ready = true;
			for (int cz = (pz - 56) >> 4; cz <= (pz + 56) >> 4 && ready; cz++) for (int cx = (px - 56) >> 4; cx <= (px + 56) >> 4; cx++)
				if (cx >= 0 && cz >= 0 && cx < World.WX / 16 && cz < World.WZ / 16 && !world.ColumnGrown(cx, cz)) { ready = false; break; }
			if (ready)
			{
				riverPlayed = true;
				void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
				int bx = -1, by = -1, bz = -1, bid = 0, bd = int.MaxValue, found = 0, falls = 0;
				for (int z = Math.Max(1, pz - 56); z <= Math.Min(World.WZ - 2, pz + 56); z++)
					for (int x = Math.Max(1, px - 56); x <= Math.Min(World.WX - 2, px + 56); x++)
						for (int y = World.WY - 2; y > 1; y--)
						{
							int b = world.GetBlock(x, y, z);
							if (b == Blocks.FallingWater) falls++;
							if (!Blocks.IsRunningRiver(b)) continue;
							found++;
							if (Blocks.IsRunningRiver(world.GetBlock(x, y - 1, z)) && world.GetBlock(x, y + 1, z) == 0)
							{
								int d = (x - px) * (x - px) + (z - pz) * (z - pz);
								if (d < bd) { bd = d; bx = x; by = y - 1; bz = z; bid = b; }
							}
							break;
						}
				GD.Print($"RIVERPLAY {found} columns of running river water and {falls} falling-water blocks within 56 of the start; nearest 2-deep at {bx},{by},{bz} ({(bid > 0 ? DataKeys.KeyOf(bid) : "none")})");
				Check("there's running river water near the start, 2 deep somewhere", bx >= 0);
				if (bx >= 0)
				{
					Blocks.RiverCurrent(bid, out float ccx, out float ccz);
					player.Flying = false; player.Pos = new Vector3(bx + 0.5f, by + 0.05f, bz + 0.5f); player.Vel = Vector3.Zero;
					var keys = new Player.Keys(); var p0 = player.Pos;
					for (int k = 0; k < 180; k++) player.Update(1 / 60f, keys);
					var mv = player.Pos - p0; float along = (mv.X * ccx + mv.Z * ccz) / MathF.Sqrt(ccx * ccx + ccz * ccz);
					GD.Print($"RIVERPLAY 3 s in it: moved {mv.X:0.00}, {mv.Y:0.00}, {mv.Z:0.00}; along its current {along:0.00} (its speed {MathF.Sqrt(ccx * ccx + ccz * ccz):0.00} a second)");
					Check($"it carries you its way ({along:0.00} blocks along its current in 3 s)", along > 0.5f);
					player.Pos = new Vector3(bx + 0.5f, by + 2.05f, bz + 0.5f); var (hc, hr) = RiverHeard();
					GD.Print($"RIVERPLAY heard beside it: calm {hc:0.00}, rapid {hr:0.00}");
					Check($"you hear it there (calm {hc:0.00} + rapid {hr:0.00} > 1), and nothing in an ordinary world", hc + hr > 1);
				}
			}
		}
		if (args.Contains("--rivertest") && !riverTested && !menuOpen && shotTimer > 0.3f)
		{   // (test, Beta 0.1.5.4: running river water, its current, drops in it and the refill rule; a trough in front of you)
			riverTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int R = Blocks.RiverWater;
			Check("river water counts as still water: water, a source, full height, level 0 (and plain water as before)", Enumerable.Range(R, 25).All(i => Blocks.IsWater(i) && Blocks.IsStillWater(i) && Blocks.WaterLevel(i) == 0 && Blocks.WaterHeight(i, 0) == 0.875f && Blocks.IsRiverWater(i))
				&& Blocks.IsWater(Blocks.Water) && Blocks.IsStillWater(Blocks.Water) && !Blocks.IsStillWater(Blocks.Water + 3) && Blocks.WaterLevel(Blocks.Water + 3) == 3 && !Blocks.IsWater(R - 1) && !Blocks.IsWater(R + 25)
				&& Blocks.IsRunningRiver(R) && Blocks.IsRunningRiver(R + 23) && !Blocks.IsRunningRiver(Blocks.FallingWater) && Blocks.FallingWater == R + 24);
			{   // the river's sound: two 6 s loops that join up without a seam, not too loud
				foreach (bool rp in new[] { false, true })
				{
					var lp = Sound.RiverLoop(rp); float big = 0; for (int i = 1; i < lp.Length; i++) big = Math.Max(big, MathF.Abs(lp[i] - lp[i - 1]));
					float seam = MathF.Abs(lp[0] - lp[^1]), peak = lp.Max(MathF.Abs);
					Check($"the {(rp ? "rapids" : "river")} loop: 6 s, peak {peak:0.00}, seam step {seam:0.0000} no bigger than its biggest step ({big:0.0000})", lp.Length == 44100 * 6 && MathF.Abs(peak - (rp ? 0.7f : 0.6f)) < 0.001f && seam <= big);
				}
				Check("an ordinary world hears no river", RiverHeard() == (0, 0));
			}
			Check("the currents: E gentle 0.75, SE quick 1.75 x sqrt(1/2) each way, N rapids 3; falling water and plain water none",
				Blocks.RiverCurrent(R, out float c1x, out float c1z) && c1x == 0.75f && c1z == 0 && Blocks.RiverCurrent(R + 9, out float c2x, out float c2z) && MathF.Abs(c2x - 1.2374369f) < 1e-5f && MathF.Abs(c2z - 1.2374369f) < 1e-5f
				&& Blocks.RiverCurrent(R + 22, out float c3x, out float c3z) && c3x == 0 && c3z == -3 && !Blocks.RiverCurrent(Blocks.FallingWater, out _, out _) && !Blocks.RiverCurrent(Blocks.Water, out _, out _));
			// the trough: 60 long (+x), 5 wide, 3 deep, stone round it, air above, 6 blocks in front of you
			int x0 = (int)player.Pos.X - 30, z0 = (int)player.Pos.Z - 8, y0 = Math.Min(World.WY - 12, world.Heights[z0 * World.WX + x0] + 4);
			void Trough(int water, int deep)
			{
				for (int x = x0 - 1; x <= x0 + 60; x++) for (int z = z0 - 3; z <= z0 + 3; z++)
				{
					world.SetBlock(x, y0, z, Blocks.Stone);
					for (int y = y0 + 1; y <= y0 + 8; y++) world.SetBlock(x, y, z, x < x0 || x >= x0 + 60 || Math.Abs(z - z0) == 3 ? (y <= y0 + 3 ? Blocks.Stone : 0) : y <= y0 + deep ? water : 0);
				}
				world.WaterQueue.Clear();
			}
			var keys = new Player.Keys();
			(float dx, float dz) Drift(int water, int deep, float secs)
			{
				Trough(water, deep);
				player.Flying = false; player.Pos = new Vector3(x0 + 15.5f, y0 + 1, z0 + 0.5f); player.Vel = Vector3.Zero;
				for (int k = 0; k < secs * 60; k++) player.Update(1 / 60f, keys);
				return (player.Pos.X - (x0 + 15.5f), player.Pos.Z - (z0 + 0.5f));
			}
			var g = Drift(R, 2, 5); var q = Drift(R + 8, 2, 5); var r = Drift(R + 16, 2, 4);
			Check($"5 s in gentle water (E): carried about 0.75 a second, not sideways (moved {g.dx:0.00}, {g.dz:0.00})", g.dx > 3.2f && g.dx < 3.8f && MathF.Abs(g.dz) < 0.05f);
			Check($"5 s in quick water (E): about 1.75 a second (moved {q.dx:0.00})", q.dx > 7.6f && q.dx < 8.8f && MathF.Abs(q.dz) < 0.05f);
			Check($"4 s in rapids (E): about 3 a second (moved {r.dx:0.00})", r.dx > 10.8f && r.dx < 12.1f && MathF.Abs(r.dz) < 0.05f);
			var w = Drift(R + 8, 1, 5);
			Check($"wading in quick water (1 deep, legs only): half the current (moved {w.dx:0.00})", w.dx > 3.6f && w.dx < 4.5f);
			var wst = Drift(R + 8 + 4, 2, 3);
			Check($"quick water running W carries you W (moved {wst.dx:0.00})", wst.dx < -4.4f && wst.dx > -5.3f);
			var dg = Drift(R + 1, 2, 4);
			Check($"gentle water running SE carries you both ways at 0.75 x sqrt(1/2) (moved {dg.dx:0.00}, {dg.dz:0.00}; the trough's side stops you)", dg.dx > 1.6f && dg.dx < 2.2f && dg.dz > 0.9f);
			var still = Drift(Blocks.Water, 2, 3);
			Check($"plain still water carries you nowhere (moved {still.dx:0.00}, {still.dz:0.00})", MathF.Abs(still.dx) < 0.01f && MathF.Abs(still.dz) < 0.01f);
			// falling water: a shaft of it, 8 deep
			Trough(Blocks.FallingWater, 8);
			player.Pos = new Vector3(x0 + 15.5f, y0 + 6, z0 + 0.5f); player.Vel = Vector3.Zero;
			float topVy = 0; for (int k = 0; k < 40; k++) { player.Update(1 / 60f, keys); topVy = player.Vel.Y; }
			Check($"falling water: you sink towards 5.6 a second, not pushed sideways (after 0.67 s: {topVy:0.00} down, moved {player.Pos.X - (x0 + 15.5f):0.00} sideways)", topVy < -4.5f && topVy > -5.7f && MathF.Abs(player.Pos.X - (x0 + 15.5f)) < 0.01f);
			// a drop in quick water: it floats up to the top and drifts E
			Trough(R + 8, 3);
			player.Pos = new Vector3(x0 + 55.5f, y0 + 9, z0 - 10.5f);   // (out of reach of it)
			var dr = drops.Add(Blocks.Dirt, x0 + 10.5f, y0 + 1.2f, z0 + 0.5f); float sx = dr.P.X;
			for (int k = 0; k < 180; k++) drops.Update(1 / 60f);
			Check($"a drop in quick water floats up to the top and drifts the river's way (3 s: moved {dr.P.X - sx:0.00}, height {dr.P.Y - y0:0.00} over the floor)", dr.P.X - sx > 4.5f && dr.P.X - sx < 5.6f && dr.P.Y > y0 + 2.4f && dr.P.Y < y0 + 3.6f && MathF.Abs(dr.P.Z - (z0 + 0.5f)) < 0.01f);
			drops.Remove(dr);
			{   // (Beta 0.1.5.5) a river carries mobs, but not ducks: a cow swimming (3 deep) and one wading (1 deep) in quick water
				// running E drift E (it stands still of itself); a duck paddles where it likes; in falling water a cow sinks
				(float dx, float y) MobDrift(string type, int water, int deep, float secs)
				{
					Trough(water, deep);
					var m = type == "cow" ? SpawnCow(x0 + 10.5f, y0 + 1, z0 + 0.5f) : SpawnAnimal(type, x0 + 10.5f, y0 + 1, z0 + 0.5f);
					float sx0 = m.X;
					for (int k = 0; k < secs * 20; k++) { m.Walking = false; m.Timer = 99; m.Vx = m.Vz = 0; m.X = Math.Clamp(m.X, x0 + 1f, x0 + 58f); UpdateMobs(0.05f); }
					var res = (m.X - sx0, m.Y - y0); RemoveMob(m); return res;
				}
				var cs = MobDrift("cow", R + 8, 3, 3); var cw = MobDrift("cow", R + 8, 1, 3); var du = MobDrift("duck", R + 8, 3, 3); var cf = MobDrift("cow", Blocks.FallingWater, 6, 1);
				GD.Print($"RIVERMOBS cow swimming {cs.dx:0.00} (at {cs.y:0.00}), wading {cw.dx:0.00} (at {cw.y:0.00}); duck {du.dx:0.00}; cow in falling water ends at {cf.y:0.00}");
				Check($"a river carries a cow swimming its way ({cs.dx:0.00} in 3 s, 1.75 a second) and one wading ({cw.dx:0.00})", cs.dx > 4.5f && cs.dx < 5.6f && cw.dx > 4.5f && cw.dx < 5.6f);
				Check($"but not a duck ({du.dx:0.00})", MathF.Abs(du.dx) < 0.5f);
				Check($"a cow in falling water sinks ({cf.y:0.00} over the floor after 1 s)", cf.y < 2.5f);
			}
			// the refill rule: a block put in the river and taken out fills back with its water; a notch in the bank (one side) with flowing water
			Trough(R + 2, 2);
			world.SetBlock(x0 + 20, y0 + 2, z0, Blocks.Stone); for (int k = 0; k < 8; k++) world.UpdateWater(0.3f);
			world.SetBlock(x0 + 20, y0 + 2, z0, 0); for (int k = 0; k < 8; k++) world.UpdateWater(0.3f);
			Check($"a block put in a river and taken out again fills back with that river water ({world.GetBlock(x0 + 20, y0 + 2, z0)})", world.GetBlock(x0 + 20, y0 + 2, z0) == R + 2);
			world.SetBlock(x0 + 20, y0 + 2, z0 + 3, 0); world.SetBlock(x0 + 20, y0 + 1, z0 + 3, Blocks.Stone); for (int k = 0; k < 8; k++) world.UpdateWater(0.3f);
			int notch = world.GetBlock(x0 + 20, y0 + 2, z0 + 3);
			Check($"a notch in the bank touching it on one side fills with flowing water, not river water ({notch})", notch == Blocks.Water + 1);
			Check("the river itself never changes on its own (the trough is still all its water)", Enumerable.Range(1, 58).All(k => world.GetBlock(x0 + k, y0 + 1, z0) == R + 2 && world.GetBlock(x0 + k, y0 + 2, z0 - 2) == R + 2));
			player.Pos = new Vector3(x0 + 30.5f, y0 + 9, z0 - 10.5f); player.Vel = Vector3.Zero;
		}
		if (args.Contains("--earthrecipes") && !earthRecipesTested && !menuOpen && shotTimer > 0.3f) EarthRecipeTest();   // (test, Beta 0.1.6.0: Main.Recipes.cs)
		if (args.Contains("--earthlife") && !earthLifeTested && !menuOpen && shotTimer > 0.3f)
		{   // (test, Beta 0.1.5.5: animals in an Earthlike world)
			earthLifeTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int Bi(string n) => Array.IndexOf(EarthPlan.BiomeNames, n);
			Check("an Earthlike world, its biome at your feet from the plan", Earthlike && EarthBiomeAt((int)player.Pos.X, (int)player.Pos.Z) is >= 0 and < 21);
			Check("the Earthlike lists: cows in 5 biomes; goats on the crags, on the rocks and their gravels; boar under Mahogany and Teak too",
				CowEarthBiomes.Length == 5 && CowEarthBiomes.Contains(Bi("savanna")) && AnimalEarthBiomes["goat"].Contains(Bi("crags")) && AnimalEarthBiomes["chicken"].Contains(Bi("dry_forest"))
				&& AnimalEarthGround["goat"].Contains(4403) && AnimalEarthGround["goat"].Contains(5033) && AnimalEarthGround["goat"].Contains(Blocks.Grass) && LogsOf("boar").Contains(4000) && LogsOf("boar").Contains(4300) && !LogsOf("boar").Contains(4100));
			Check("grass is any grassy top here (not the bare soils); what grazing or rooting leaves", GrassFor(Blocks.Grass) && GrassFor(4812) && GrassFor(Blocks.SnowyGrass) && !GrassFor(4802) && !GrassFor(Blocks.Dirt)
				&& BareOf(4812) == 4802 && BareOf(Blocks.Grass) == Blocks.Dirt && World.Regrows(4802) && !World.Regrows(4804) && World.Regrows(Blocks.Dirt));
			int x = (int)player.Pos.X + 3, z = (int)player.Pos.Z + 3, y = World.WY - 2;
			while (y > 1 && world.GetBlock(x, y, z) == 0) y--;
			world.SetBlock(x, y, z, 4812); world.SetBlock(x, y + 1, z, 0); world.SetBlock(x, y + 2, z, 0);
			Check("a chicken can start on a grassy soil", AnimalSpot("chicken", x, z) is { } cs && (int)cs.Y == y + 1);
			Graze(x, y, z);
			Check("a cow grazing Grassy Red Earth leaves Red Earth, which it counts down", world.GetBlock(x, y, z) == 4802 && world.Grazed.ContainsKey(World.Idx(x, y, z)));
			int secs = 0; while (world.GetBlock(x, y, z) == 4802 && secs < 400) { world.TickTimers(1); secs++; }
			Check($"and it grows back to Grassy Red Earth ({secs} s)", world.GetBlock(x, y, z) == 4812 && !world.Grazed.ContainsKey(World.Idx(x, y, z)));
			{   // (A5) a night mob's spot: on a field of Grassy Red Earth round you (open sky, nothing above), any spot counts
				int px = (int)MathF.Floor(player.Pos.X), pz = (int)MathF.Floor(player.Pos.Z), y0 = (int)MathF.Floor(player.Pos.Y) - 1;
				for (int dz = -14; dz <= 14; dz++) for (int dx = -14; dx <= 14; dx++)
				{
					world.SetBlock(px + dx, y0, pz + dz, 4812);
					for (int u = 1; u <= 40; u++) if (world.GetBlock(px + dx, y0 + u, pz + dz) != 0) world.SetBlock(px + dx, y0 + u, pz + dz, 0);
				}
				int got = 0;
				for (int k = 0; k < 40; k++) if (FindMobSpot(10, 12) is { } ms && world.GetBlock((int)MathF.Floor(ms.X), (int)MathF.Floor(ms.Y) - 1, (int)MathF.Floor(ms.Z)) == 4812) got++;
				Check($"(A5) a mob's spot counts a grassy soil as grass here ({got} of 40 on Grassy Red Earth)", EarthMobGrass && got == 40);
			}
		}
		if (args.Contains("--farm") && !farmTested && !menuOpen)
		{   // (test: a row of soil in front of you with water at one end, tilled and planted the way you would, then 200 seconds of growing)
			farmTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			player.Yaw = 0;   // (lined up with the blocks)
			var fwd = new Vector3(0, 0, -1); var side = new Vector3(-1, 0, 0);
			var c0 = player.Pos + fwd * 3.5f; int y = world.Heights[(int)c0.Z * World.WX + (int)c0.X];
			(int x, int z) At(int k) { var p = c0 + side * (k - 4); return ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z)); }
			for (int k = -1; k < 10; k++)
			{   // the row, with a stone kerb round it so the water stays put
				var (x, z) = At(k);
				for (int d = -1; d <= 1; d++) { world.SetBlock(x, y - 1, z + d, Blocks.Dirt); world.SetBlock(x, y, z + d, Blocks.Stone); for (int u = 1; u < 4; u++) world.SetBlock(x, y + u, z + d, 0); }
				if (k >= 0 && k < 9) world.SetBlock(x, y, z, k == 0 ? Blocks.Water : Blocks.Grass);
			}
			int hoe = Blocks.Tools.First(t => Blocks.Get(t)?.Tool == "hoe");
			for (int k = 1; k < 9; k++)
			{
				var (x, z) = At(k);
				inv.SetSlot(0, hoe); SelectSlot(0); FarmUse(x, y, z, Blocks.Grass);
				inv.SetSlot(0, Blocks.GrainSeeds); FarmUse(x, y, z, world.GetBlock(x, y, z));
				if (k == 1) { inv.SetSlot(0, Blocks.BoneAsh); FarmUse(x, y + 1, z, world.GetBlock(x, y + 1, z)); }   // (fertilized by clicking the crop)
			}
			Check("hoe tills grass and seeds go in", Enumerable.Range(1, 8).All(k => { var (x, z) = At(k); return Blocks.IsSoil(world.GetBlock(x, y, z)) && world.GetBlock(x, y + 1, z) == Blocks.GrainStages[0]; }));
			for (int s = 0; s < 3; s++) world.TickTimers(1);
			string Soil() => string.Join(" ", Enumerable.Range(1, 8).Select(k => { var (x, z) = At(k); return world.GetBlock(x, y, z) + "/" + world.GetBlock(x, y + 1, z); }));
			GD.Print("farm after 3s (soil/crop): " + Soil());
			Check("soil joined to water is wet up to 5 away, the next is dry", Enumerable.Range(1, 6).All(k => { var (x, z) = At(k); return Blocks.SoilWet(world.GetBlock(x, y, z)); }) && !Blocks.SoilWet(world.GetBlock(At(7).x, y, At(7).z)));
			for (int s = 0; s < 400; s++) world.TickTimers(1);
			GD.Print("farm after 403s (soil/crop): " + Soil());
			var (fx, fz) = At(1);
			Check("wet + fertilized is ripe", world.GetBlock(fx, y + 1, fz) == Blocks.GrainRipe);
			double Avg(int a, int b) => Enumerable.Range(a, b - a + 1).Average(k => { var (x, z) = At(k); return world.GetBlock(x, y + 1, z); });
			Check("wet grows faster than dry (on average)", Avg(2, 6) > Avg(7, 8));
			{   // random growth: how long 2000 plants take from seed to ripe, at each growing speed
				var r = new Random(1);
				foreach (var (name, rate) in new[] { ("wet", 1f), ("dry", World.DryRate), ("wet+fertilized", World.FertRate) })
				{
					var times = new List<int>();
					for (int p = 0; p < 2000; p++) { int t = 0, st = 0; while (st < 7 * World.StageHits) { t++; if (r.NextDouble() < World.StageChance(rate, 1)) st++; } times.Add(t); }
					times.Sort(); double want = 7 * World.StageTime / rate;
					GD.Print($"ripe ({name}): average {times.Average():0}s (fixed timers gave {want:0}s), half of them between {times[500]}s and {times[1500]}s, fastest {times[0]}s, slowest {times[^1]}s");
					Check($"random growth keeps the average ({name})", Math.Abs(times.Average() - want) < want * 0.05);
				}
			}
			world.SetBlock(fx, y + 1, fz, 0);
			Check("a harvest uses up one of the fertilizer's 2", world.FertLeft.GetValueOrDefault(World.Idx(fx, y, fz)) == 1);
			Check("Flour by hand, Bread in the kiln, Bread is food", Inventory.Recipes.Any(r => r.Out == Blocks.Flour) && Kilns.Smelts.Any(s => s.Out == Blocks.Bread && s.From == Blocks.Flour) && Blocks.Get(Blocks.Bread)?.Food > 0);
			{   // Earthlike's soils (Beta 0.1.5.3, "Make them count"): a row of the 9 grassy and 10 bare soils and a Stone, 3 blocks past
				// the farm, each tilled by the hoe and planted with seeds; and a sapling, a flower and a Cantaloupe's fruit allowed on each
				int[] soils = Enumerable.Range(4800, 20).Where(i => i != 4814).ToArray();
				var r0 = c0 + fwd * 3f; int ry = world.Heights[(int)r0.Z * World.WX + (int)r0.X];
				(int x, int z) RAt(int k) { var p = r0 + side * (k - 10); return ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z)); }
				for (int k = 0; k <= soils.Length; k++)
				{
					var (x, z) = RAt(k);
					world.SetBlock(x, ry - 1, z, Blocks.Dirt); world.SetBlock(x, ry, z, k < soils.Length ? soils[k] : Blocks.Stone);
					for (int u = 1; u < 4; u++) world.SetBlock(x, ry + u, z, 0);
				}
				for (int k = 0; k <= soils.Length; k++)
				{
					var (x, z) = RAt(k);
					inv.SetSlot(0, hoe); SelectSlot(0); FarmUse(x, ry, z, world.GetBlock(x, ry, z));
					inv.SetSlot(0, Blocks.GrainSeeds); FarmUse(x, ry, z, world.GetBlock(x, ry, z));
				}
				var (sx, sz) = RAt(soils.Length);
				Check("the hoe tills all 19 Earthlike soils and seeds go in (Stone isn't tilled)", Enumerable.Range(0, soils.Length).All(k => { var (x, z) = RAt(k); return Blocks.IsSoil(world.GetBlock(x, ry, z)) && world.GetBlock(x, ry + 1, z) == Blocks.GrainStages[0]; }) && world.GetBlock(sx, ry, sz) == Blocks.Stone && world.GetBlock(sx, ry + 1, sz) == 0);
				Check("saplings, flowers and a Cantaloupe's fruit go on every Earthlike soil (not on Stone)", soils.All(s => SaplingGround(s, false) && PlantGround(s) && Blocks.MelonGround(s)) && !SaplingGround(Blocks.Stone, true) && !PlantGround(Blocks.Stone) && !Blocks.MelonGround(Blocks.Stone));
				Check("the grassy soils plant like Grass (Grain Seeds, deer), the bare ones like Dirt; IsGrassy (the world makers') unchanged", soils.All(s => Blocks.PlantsLikeGrass(s) == (s >= 4810) && Blocks.PlantsLikeDirt(s) == (s < 4810) && !Blocks.IsGrassy(s)) && Blocks.PlantsLikeGrass(Blocks.Grass) && Blocks.PlantsLikeGrass(Blocks.SnowyGrass) && Blocks.PlantsLikeGrass(Blocks.SwampGrass) && !Blocks.PlantsLikeGrass(Blocks.Dirt) && Blocks.PlantsLikeDirt(Blocks.Dirt) && !Blocks.PlantsLikeDirt(Blocks.Mud) && !Blocks.PlantsLikeGrass(4814) && !Blocks.PlantsLikeDirt(4814));
			}
			inv.SetSlot(0, Blocks.Bread); player.Pitch = -0.45f;
		}
		if (args.Contains("--clockitems") && !clockItemsTested && !menuOpen && inv.Survival && shotTimer > 0.3f)
		{   // (test, Beta 0.1.6.1: the Clock and the Calendar, the user's answers C1-C4, with the rules agreed with the web version)
			clockItemsTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int K(string key) => DataKeys.IdOf(key);
			int Clk = Blocks.Clock, Cal = Blocks.Calendar, Off = Inventory.Off0 + inv.OffSel;
			// the data
			Check("the Clock is 744 (key clock) and the Calendar 745 (key calendar), items, one to a slot, live, not put down",
				K("clock") == 744 && K("calendar") == 745 && Clk == 744 && Cal == 745 && Blocks.B[Clk].Name == "Clock" && Blocks.B[Cal].Name == "Calendar"
				&& Blocks.B[Clk].Item && Blocks.B[Cal].Item && Blocks.StackMaxOf(Clk) == 1 && Blocks.StackMaxOf(Cal) == 1
				&& Blocks.B[Clk].Live == "clock" && Blocks.B[Cal].Live == "season" && Blocks.B[Clk].Frames.Length == 8 && Blocks.B[Cal].Frames.Length == 4
				&& !CanPutDown(Clk) && !CanPutDown(Cal) && Blocks.B[Clk].Tool == null && Blocks.B[Cal].Tool == null);
			Check("their pictures: clock_0..clock_7 and calendar_spring/summer/autumn/winter, in the atlas",
				string.Join(" ", Blocks.B[Clk].Frames.Select(t => Atlas.Names[t])) == "clock_0 clock_1 clock_2 clock_3 clock_4 clock_5 clock_6 clock_7"
				&& string.Join(" ", Blocks.B[Cal].Frames.Select(t => Atlas.Names[t])) == "calendar_spring calendar_summer calendar_autumn calendar_winter");
			var pics = Blocks.B[Clk].Frames.Concat(Blocks.B[Cal].Frames).ToArray();
			var files = pics.Select(t => Atlas.Names[t]).Where(n => ResourceLoader.Exists($"res://textures/items/{n}.png")).Count();
			var looks = pics.Select(t => Convert.ToBase64String(Atlas.Image.GetRegion(new Rect2I(t * Atlas.TILE, 0, Atlas.TILE, Atlas.TILE)).GetData())).Distinct().Count();
			Check($"the 12 pictures are drawn (textures/items: {files} of 12 files) and all different ({looks} different)", files == 12 && looks == 12);
			// the recipes (the user's C1: a tin clock; a linen calendar), both at the bench, made one at a time and one to a slot
			var rc = Inventory.Recipes.Where(r => r.Out == Clk).ToList(); var rl = Inventory.Recipes.Where(r => r.Out == Cal).ToList();
			Check("the Clock: 2 Tin Bricks + 1 Glass at the bench; the Calendar: 2 Linen + 1 Stick at the bench (one recipe each)",
				rc.Count == 1 && rc[0].At == "bench" && rc[0].N == 1 && string.Join(",", rc[0].Needs.Select(n => $"{n.n} {DataKeys.KeyOf(n.id)}")) == "2 tin_brick,1 glass"
				&& rl.Count == 1 && rl[0].At == "bench" && rl[0].N == 1 && string.Join(",", rl[0].Needs.Select(n => $"{n.n} {DataKeys.KeyOf(n.id)}")) == "2 linen,1 stick");
			for (int i = 0; i < Inventory.Armor0; i++) inv.SetSlot(i, 0);
			ClearHeld();
			inv.SetSlot(Inventory.Pocket0, K("tin_brick"), 4); inv.SetSlot(Inventory.Pocket0 + 1, Blocks.Glass, 2);
			inv.SetSlot(Inventory.Pocket0 + 2, K("linen"), 2); inv.SetSlot(Inventory.Pocket0 + 3, Blocks.Stick, 1);
			bool c1 = inv.Craft(rc[0]) == true, c2 = inv.Craft(rc[0]) == true, c3 = inv.Craft(rc[0]) == true, l1 = inv.Craft(rl[0]) == true;
			int Slots(int id) => Enumerable.Range(0, Inventory.Armor0).Count(i => inv.Id[i] == id && inv.Count[i] == 1);
			Check($"crafted: 2 Clocks in 2 slots (a third needs more tin), 1 Calendar; the bricks, glass, linen and stick used up",
				c1 && c2 && !c3 && l1 && Slots(Clk) == 2 && Slots(Cal) == 1 && inv.CountOf(K("tin_brick")) == 0 && inv.CountOf(Blocks.Glass) == 0 && inv.CountOf(K("linen")) == 0 && inv.CountOf(Blocks.Stick) == 0);
			for (int i = 0; i < Inventory.Armor0; i++) inv.SetSlot(i, 0);
			// the pictures follow the sky at your spot, in place: one texture each, redrawn when the part of the day or the season changes
			bool SameTile(Image a, int tile)
			{   // (the image the live texture is redrawn from: a headless run's dummy renderer can't read a redrawn texture back)
				if (a == null || a.GetWidth() != Atlas.TILE) return false;
				for (int y = 0; y < Atlas.TILE; y++) for (int x = 0; x < Atlas.TILE; x++) if (!a.GetPixel(x, y).IsEqualApprox(Atlas.Image.GetPixel(tile * Atlas.TILE + x, y))) return false;
				return true;
			}
			var tc = Atlas.IconTexture(Clk); var tl = Atlas.IconTexture(Cal);
			bool picsOk = true; var seen = new List<string>();
			for (int part = 0; part < 8; part++)
			{   // (each part of the day: the middle of it, by the sun at your spot)
				double f = part / 8.0; long day = sky.Day;
				sky.Clock = day * Sky.DayLength + Sky.SecondsAt(f, day, sky.PlaceK, sky.PlaceSouth);
				TickHud(0.01f);
				int i = Sky.PartOf(sky.TimeOfDay);
				bool ok = i == part && Atlas.LiveFrame(Clk) == part && Atlas.IconTexture(Clk) == tc && SameTile(Atlas.Icon(Clk), Blocks.B[Clk].Frames[part]);
				picsOk &= ok; seen.Add($"{part}:{i}/{Atlas.LiveFrame(Clk)}{(ok ? "" : "!")}");
			}
			Check($"the Clock's picture is frames[i] at each of the 8 parts of the day, the same texture redrawn in place ({string.Join(" ", seen)})", picsOk);
			bool seasOk = true; seen.Clear();
			for (int s = 0; s < 4; s++)
			{
				sky.Clock = (s * 6 + 2) * Sky.DayLength + 600;   // (the third day of each season)
				TickHud(0.01f);
				bool ok = Sky.SeasonOf(sky.Day, sky.PlaceSouth) == s && Atlas.LiveFrame(Cal) == s && Atlas.IconTexture(Cal) == tl && SameTile(Atlas.Icon(Cal), Blocks.B[Cal].Frames[s]);
				seasOk &= ok; seen.Add($"{s}:{Atlas.LiveFrame(Cal)}{(ok ? "" : "!")}");
			}
			Check($"the Calendar's picture is frames[the season] in each season, the same texture redrawn in place ({string.Join(" ", seen)})", seasOk);
			if (DisplayServer.GetName() != "headless")
			{   // (on a real screen: the redrawn textures themselves, read back)
				long dd = sky.Day; bool r1, r2, r3;
				sky.Clock = dd * Sky.DayLength + Sky.SecondsAt(6 / 8.0, dd, sky.PlaceK, sky.PlaceSouth); TickHud(0.01f); r1 = SameTile(tc.GetImage(), Blocks.B[Clk].Frames[6]);
				sky.Clock = dd * Sky.DayLength + Sky.SecondsAt(2 / 8.0, dd, sky.PlaceK, sky.PlaceSouth); TickHud(0.01f); r2 = SameTile(tc.GetImage(), Blocks.B[Clk].Frames[2]);
				sky.Clock = 20 * Sky.DayLength + 600; TickHud(0.01f); r3 = SameTile(tl.GetImage(), Blocks.B[Cal].Frames[3]);
				Check($"on screen, the textures themselves are redrawn: the Clock at midnight then midday, the Calendar in winter ({r1}, {r2}, {r3})", r1 && r2 && r3);
			}
			// everywhere it's drawn shows that one texture: a hotbar slot, the hand, a dropped one
			sky.Clock = 2 * Sky.DayLength + 300; TickHud(0.01f);
			inv.SetSlot(0, Clk); inv.SetSlot(1, Cal); DrawHotbar(); SelectSlot(0); UpdateHand(0.01f);
			var dr = drops.Add(Cal, player.Pos.X + 3, player.Pos.Y + 1, player.Pos.Z);
			bool handPic = handBody?.HeldMesh?.MaterialOverride is ShaderMaterial hm && (Texture2D)hm.GetShaderParameter("tex") == tc;
			Check($"the hotbar shows them, the hand holds the Clock's texture, a dropped Calendar shows its texture (hand {(handBody?.HeldMesh != null ? "drawn" : "none")})",
				hotSlots[0].Pic.Texture == tc && hotSlots[1].Pic.Texture == tl && (handBody?.HeldMesh == null || handPic) && dr?.Mat != null && (Texture2D)dr.Mat.GetShaderParameter("tex") == tl);
			// the words: at once when picked, steady while held, both hands, a passing note, refreshed once a second, gone when not held
			string ClockW() => Sky.ClockWords(sky.Clock, sky.PlaceK, sky.PlaceSouth); string CalW() => Sky.CalendarWords(sky.Clock, sky.PlaceSouth);
			TickHud(0.01f);
			Check($"picking the Clock shows its words at once, not its name (\"{nameLabel.Text}\")", nameLabel.Text == ClockW() && nameLabel.Text.EndsWith("of daylight left") && nameLabel.Modulate == Colors.White && nameTimer <= 0);
			inv.SetSlot(Off, Cal); TickHud(0.01f);
			Check($"the Calendar in the offhand too: the Clock's line on top, the Calendar's under it (\"{nameLabel.Text.Replace("\n", " | ")}\")", nameLabel.Text == ClockW() + "\n" + CalW() && CalW().StartsWith("Day 3 of Spring, Year 1"));
			ShowNote("Your Stone Axe broke", 2); TickHud(0.01f);
			bool noteShown = nameLabel.Text == "Your Stone Axe broke";
			TickHud(2.1f); TickHud(0.01f);
			Check($"a passing note takes over for its time, then the words come back (\"{nameLabel.Text.Replace("\n", " | ")}\")", noteShown && nameLabel.Text == ClockW() + "\n" + CalW() && nameLabel.Modulate == Colors.White);
			string was = nameLabel.Text; sky.Clock += 61; TickHud(0.5f);
			bool stale = nameLabel.Text == was; TickHud(0.6f);
			Check($"refreshed once a second: the minutes count down (\"{was.Split('\n')[0]}\" then \"{nameLabel.Text.Split('\n')[0]}\")", stale && nameLabel.Text == ClockW() + "\n" + CalW() && nameLabel.Text != was);
			sky.Clock = sky.Day * Sky.DayLength + Sky.SecondsAt(0.75, sky.Day, sky.PlaceK, sky.PlaceSouth); TickHud(1.1f);
			Check($"by night: \"... minutes to sunrise\" (\"{nameLabel.Text.Split('\n')[0]}\")", nameLabel.Text.StartsWith("Midnight: ") && nameLabel.Text.Split('\n')[0].EndsWith(" to sunrise"));
			inv.SetSlot(0, 0); TickHud(0.01f);
			Check($"the Clock put away: only the offhand Calendar's words (\"{nameLabel.Text}\")", nameLabel.Text == CalW());
			inv.SetSlot(Off, 0); TickHud(0.01f);
			Check($"neither held: the words go at once (\"{nameLabel.Text}\")", nameLabel.Text == "");
			inv.SetSlot(Off, Clk); inv.OffSel = (inv.OffSel + 1) % Math.Max(1, inv.OffCount); TickHud(0.01f);
			Check($"a Clock in an offhand slot that isn't the selected one shows nothing (offhand slots {inv.OffCount})", inv.OffCount == 1 ? nameLabel.Text == ClockW() : nameLabel.Text == "");
			for (int i = 0; i < Inventory.Armor0; i++) inv.SetSlot(i, 0);
			if (args.Contains("--keepitems"))
			{   // (for the picture: the Clock in hand, the Calendar in the offhand, in the morning of a summer day)
				sky.Clock = 8 * Sky.DayLength + 500; inv.OffSel = 0;
				inv.SetSlot(0, Clk); inv.SetSlot(1, Cal); inv.SetSlot(Inventory.Off0, Cal); DrawHotbar(); SelectSlot(0);
			}
			GD.Print("CLOCKITEMS done");
		}
		if (args.Contains("--bagtest") && !bagTested && !menuOpen && inv.Survival && shotTimer > 0.3f)
		{   // (test: the Linen Bag, the user's (2026-11-25), with the rules agreed with the web version)
			bagTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int K(string key) => DataKeys.IdOf(key);
			int Bag = K("linen_bag"), Dirt = Blocks.Dirt, Sand = Blocks.Sand, Pick = K("stone_pickaxe"), Pack = K("leather_backpack");
			int P = Inventory.Pocket0;
			string In(Inventory.PackData d) => d?.Id == null ? "empty" : string.Join(", ", Enumerable.Range(0, d.Id.Length).Select(k => $"{d.N[k]} {Blocks.B[d.Id[k]].Name}{(d.W?[k] > 0 ? $" (worn {d.W[k]})" : "")}"));
			void Hold(int id, int n, int w = 0, Inventory.PackData d = null) { (heldId, heldN, heldWear, heldData) = (id, n, w, d); ShowHeld(); }
			void Click(int i, MouseButton b) { lastPick = -1; SurvivalSlotPress(i, b); UpdateHeld(); }
			for (int i = 0; i < Inventory.Armor0; i++) inv.SetSlot(i, 0);
			ClearHeld();
			// the data
			Check("the Linen Bag is 724 (key linen_bag), one to a slot; 725 is gone, and no key is linen_backpack or linen_utility_belt",
				Bag == 724 && Blocks.IsBag(Bag) && Blocks.StackMaxOf(Bag) == 1 && Blocks.Get(725) == null && Blocks.B[Bag].Name == "Linen Bag"
				&& !Enumerable.Range(1, Blocks.MaxId - 1).Any(i => DataKeys.KeyOf(i) is "linen_backpack" or "linen_utility_belt"));
			var made = Inventory.Recipes.Where(r => r.Out == Bag).ToList();
			Check("it's made at the Bench from 3 Linen and 1 String", made.Count == 1 && made[0].At == "bench" && made[0].N == 1
				&& made[0].Needs.OrderBy(n => n.id).SequenceEqual(new[] { (K("linen"), 3), (K("string"), 1) }.OrderBy(n => n.Item1)));
			Check("Linen no longer makes any pack or belt (they're leather-based, as before)", Inventory.Recipes.Where(r => Blocks.B[r.Out].Pack > 0 || Blocks.B[r.Out].Belt > 0).All(r => r.Needs.All(n => n.id != K("linen"))) && Inventory.Recipes.Count(r => Blocks.B[r.Out].Pack > 0 || Blocks.B[r.Out].Belt > 0) == 12);
			Check("it's last in the creative Tools & Armor tab", CreativeTabs.First(t => t.name == "Tools & Armor").ids.Last() == Bag);
			// putting things in: left-click puts in as much as fits, right-click one
			inv.SetSlot(P, Bag);
			Hold(Dirt, 40); Click(P, MouseButton.Left);
			Check($"40 Dirt held, the bag clicked: all 40 go in ({In(inv.Data[P])}; held {heldN})", In(inv.Data[P]) == "40 Dirt" && heldId == 0);
			Hold(Sand, 30); Click(P, MouseButton.Left);
			Check($"then 30 Sand: 24 go in (room for 64 in all), 6 stay held ({In(inv.Data[P])}; held {heldN})", In(inv.Data[P]) == "40 Dirt, 24 Sand" && heldId == Sand && heldN == 6);
			Click(P, MouseButton.Left);
			Check($"then nothing more goes in: \"The bag is full\" ({invName.Text}), and nothing swaps", invName.Text == "The bag is full" && heldN == 6 && inv.Id[P] == Bag);
			// taking out: the last thing put in, all of it
			ClearHeld(); Click(P, MouseButton.Right);
			Check($"right-click it with nothing held: the last thing put in comes out, all of it (held {heldN} {Blocks.B[heldId].Name}; left {In(inv.Data[P])})", heldId == Sand && heldN == 24 && In(inv.Data[P]) == "40 Dirt");
			Click(P, MouseButton.Right);
			Check($"right-click it holding Sand: one goes in ({In(inv.Data[P])}; held {heldN})", In(inv.Data[P]) == "40 Dirt, 1 Sand" && heldN == 23);
			ClearHeld(); Click(P, MouseButton.Right); ClearHeld(); Click(P, MouseButton.Right);
			Check($"and out again, newest first, till it's empty (held {heldN} {Blocks.B[heldId].Name}; bag {In(inv.Data[P])})", heldId == Dirt && heldN == 40 && inv.Data[P] == null && inv.Id[P] == Bag);
			ClearHeld(); Click(P, MouseButton.Right);
			Check("right-click an empty bag with nothing held: it's picked up", heldId == Bag && inv.Id[P] == 0);
			Click(P, MouseButton.Left);
			Check("and put back down", inv.Id[P] == Bag && heldId == 0);
			// the same thing joins its own entry; the tooltip
			Hold(Dirt, 10); Click(P, MouseButton.Left); Hold(Sand, 5); Click(P, MouseButton.Left); Hold(Dirt, 10); Click(P, MouseButton.Left);
			Check($"10 Dirt, 5 Sand, then 10 Dirt more: the Dirt joins its own entry ({In(inv.Data[P])})", In(inv.Data[P]) == "20 Dirt, 5 Sand");
			string tip = TipFor(Bag, -1, inv.Data[P]);
			Check($"its tooltip: what it holds, newest first, and its room ({tip.Replace("\n", " / ")})", tip == "Linen Bag\n5 Sand\n20 Dirt\nRoom for 39 more");
			Check("an empty bag's tooltip says Empty", TipFor(Bag, -1, null) == "Linen Bag\nEmpty");
			var many = new Inventory.PackData { Id = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 12, 13 }, N = Enumerable.Repeat(1, 10).ToArray(), W = new int[10] };
			string tip10 = TipFor(Bag, -1, many);
			Check($"with 10 things in it: 8 lines, then \"and 2 more\" ({tip10.Replace("\n", " / ")})", tip10.Split('\n').Length == 11 && tip10.Split('\n')[9] == "and 2 more" && tip10.EndsWith("Room for 54 more"));
			Hold(Dirt, 50); Click(P, MouseButton.Left);
			Check($"filled up: \"Full\" ({TipFor(Bag, -1, inv.Data[P]).Replace("\n", " / ")}; held {heldN})", TipFor(Bag, -1, inv.Data[P]).EndsWith("\nFull") && heldN == 11 && Inventory.BagUsed(inv.Data[P]) == 64);
			ClearHeld();
			// a tool fills a bag on its own, and keeps its wear
			inv.SetSlot(P + 1, Bag);
			Hold(Pick, 1, 5); Click(P + 1, MouseButton.Left);
			Check($"a worn Stone Pickaxe goes in an empty bag and fills it ({In(inv.Data[P + 1])}; {TipFor(Bag, -1, inv.Data[P + 1]).Replace("\n", " / ")})", In(inv.Data[P + 1]) == "1 Stone Pickaxe (worn 5)" && TipFor(Bag, -1, inv.Data[P + 1]) == "Linen Bag\nStone Pickaxe\nFull");
			Hold(Dirt, 1); Click(P + 1, MouseButton.Left);
			Check("nothing else fits with it", invName.Text == "The bag is full" && heldN == 1);
			ClearHeld(); Click(P + 1, MouseButton.Right);
			Check("it comes back out with its wear", heldId == Pick && heldN == 1 && heldWear == 5 && inv.Data[P + 1] == null);
			ClearHeld();
			Hold(Pick, 1); Click(P + 1, MouseButton.Left); ClearHeld();
			// what doesn't go in
			var packFull = new Inventory.PackData { Id = new[] { Dirt }, N = new[] { 3 }, W = new int[1] };
			inv.SetSlot(P + 2, Bag);
			Hold(Pack, 1, 0, packFull); Click(P + 2, MouseButton.Left);
			Check($"a backpack with things in it doesn't go in: \"That can't go in a bag\" ({invName.Text})", invName.Text == "That can't go in a bag" && heldId == Pack && inv.Data[P + 2] == null);
			Hold(Pack, 1); Click(P + 2, MouseButton.Left);
			Check($"an empty backpack does, and fills it ({In(inv.Data[P + 2])})", In(inv.Data[P + 2]) == "1 Leather Backpack" && heldId == 0);
			// a bag on a bag: they swap, each keeping what it holds
			var dirtBag = inv.Data[P]; var pickBag = inv.Data[P + 1];
			PickUp(P + 1, 1); Click(P, MouseButton.Left);
			Check($"a held bag clicked on a bag: they swap, each keeping what it holds (slot: {In(inv.Data[P])}; held: {In(heldData)})", inv.Data[P] == pickBag && heldId == Bag && heldData == dirtBag);
			Click(P + 1, MouseButton.Left);
			// the pictures: empty, holding anything; the fill bar
			bool Same(Image a, string png)
			{
				var b = Image.LoadFromFile(ProjectSettings.GlobalizePath(png)); if (a == null || b == null || a.GetSize() != b.GetSize()) return false;
				for (int y = 0; y < a.GetHeight(); y++) for (int x = 0; x < a.GetWidth(); x++) { Color c = a.GetPixel(x, y), e = b.GetPixel(x, y); if ((c.A > 0 || e.A > 0) && !c.IsEqualApprox(e)) return false; }
				return true;
			}
			Check("its two pictures show as drawn: linen_bag (empty) and linen_bag_full (holding anything)", Same(Atlas.IconTexture(Bag).GetImage(), "res://textures/items/linen_bag.png") && Same(Atlas.BagIcon(Bag, true).GetImage(), "res://textures/items/linen_bag_full.png") && Atlas.BagIcon(Bag, false) == Atlas.IconTexture(Bag));
			inv.SetSlot(P + 3, Bag); DrawHotbar();
			var s0 = gearSlots[P]; var s3 = gearSlots[P + 3];
			Check($"in its slot: the full picture and a fill bar of how full it is (full: {s0.BarShown:0.00}, in bright blue 3FA9FF), the empty picture and no bar when empty ({s3.BarShown})",
				s0.Pic.Texture == Atlas.BagIcon(Bag, true) && Math.Abs(s0.BarShown - 1) < 0.01f && s0.BarColor.IsEqualApprox(Color.FromHtml("3FA9FF")) && s3.Pic.Texture == Atlas.IconTexture(Bag) && s3.BarShown < 0);
			var half = new Inventory.PackData { Id = new[] { Dirt }, N = new[] { 16 }, W = new int[1] }; inv.Data[P + 3] = half; DrawHotbar();
			Check($"a quarter full: a quarter of the bar ({s3.BarShown:0.00})", Math.Abs(s3.BarShown - 0.25f) < 0.05f);
			Hold(Bag, 1, 0, half);
			Check("held on the mouse: the full picture while it holds anything", heldPic.Texture == Atlas.BagIcon(Bag, true));
			Hold(Bag, 1);
			Check("and its own picture when it's empty", heldPic.Texture == Atlas.IconTexture(Bag));
			ClearHeld();
			inv.SetSlot(0, Bag, 1, 0, half); SelectSlot(0); UpdateHand(0.016f); UpdateHand(0.016f);
			bool handFullPic = handBody?.HeldMesh?.MaterialOverride is ShaderMaterial hm && (Texture2D)hm.GetShaderParameter("tex") == Atlas.BagIcon(Bag, true);
			inv.Data[0] = null; UpdateHand(0.016f);
			bool handEmptyPic = handBody?.HeldMesh?.MaterialOverride is ShaderMaterial hm2 && (Texture2D)hm2.GetShaderParameter("tex") == Atlas.IconTexture(Bag);
			Check("in your hand: the full picture while it holds anything, its own when empty", handFullPic && handEmptyPic);
			var other = new Mob { Type = "player", T = new MobType { Scale = 0.9f } }; BuildMob(other);
			Texture2D OtherPic() => other.HeldMesh?.MaterialOverride is ShaderMaterial om ? (Texture2D)om.GetShaderParameter("tex") : null;
			SetHeld(other, Bag, true); var p1 = OtherPic(); SetHeld(other, Bag, false); var p2 = OtherPic(); SetHeld(other, Bag, true); var p3 = OtherPic();
			Check("in another player's hand: full, then emptied, then filled again, the picture follows", p1 == Atlas.BagIcon(Bag, true) && p2 == Atlas.IconTexture(Bag) && p3 == Atlas.BagIcon(Bag, true));
			other.Root.QueueFree();
			// saves: what it holds, and a bag in a backpack taken off
			inv.SetSlot(Inventory.BackSlot, Pack); inv.SetSlot(Inventory.Pack0, Bag, 1, 0, new Inventory.PackData { Id = new[] { Sand, Pick }, N = new[] { 3, 1 }, W = new[] { 0, 9 } });
			inv.PackBack();
			var packD = inv.Data[Inventory.BackSlot];
			Check($"a backpack taken off keeps a bag in it with what the bag holds ({In(packD?.D?.GetValueOrDefault(0))})", inv.Id[Inventory.Pack0] == 0 && In(packD?.D?.GetValueOrDefault(0)) == "3 Sand, 1 Stone Pickaxe (worn 9)");
			var so = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
			var back = System.Text.Json.JsonSerializer.Deserialize<Worlds.Save>(System.Text.Json.JsonSerializer.Serialize(new Worlds.Save { Data = new() { [P] = inv.Data[P], [Inventory.BackSlot] = packD } }, so), so);
			Check($"saved and loaded: a bag's things, and the bag in the backpack's ({In(back.Data[P])}; {In(back.Data[Inventory.BackSlot].D?.GetValueOrDefault(0))})", In(back.Data[P]) == In(inv.Data[P]) && In(back.Data[Inventory.BackSlot].D?.GetValueOrDefault(0)) == "3 Sand, 1 Stone Pickaxe (worn 9)");
			inv.Data[Inventory.BackSlot] = back.Data[Inventory.BackSlot]; inv.UnpackBack();
			Check("put back on, the bag is in its slot with what it holds", inv.Id[Inventory.Pack0] == Bag && In(inv.Data[Inventory.Pack0]) == "3 Sand, 1 Stone Pickaxe (worn 9)");
			// a dropped bag: its picture, and spilling what it holds when its 5 minutes are up
			var spot = player.Pos + new Vector3(30, 0, 30); spot.Y = world.Heights[(int)spot.Z * World.WX + (int)spot.X] + 1.5f;
			foreach (var dd in drops.All.ToArray()) drops.Remove(dd);
			var dr = drops.Add(Bag, spot.X, spot.Y, spot.Z); dr.V = Vector3.Zero;
			dr.Data = new Inventory.PackData { Id = new[] { Dirt, Pick }, N = new[] { 3, 1 }, W = new[] { 0, 7 } };
			Check("a dropped bag holding things shows its full picture", (Texture2D)dr.Mat.GetShaderParameter("tex") == Atlas.BagIcon(Bag, true));
			dr.Age = 300.5f; drops.Update(0.016f);
			var left = drops.All.ToList();
			Check($"when its 5 minutes are up it spills what it holds there, each thing a drop of its own ({string.Join(", ", left.Select(d => Blocks.B[d.Id].Name + (d.Wear > 0 ? $" (worn {d.Wear})" : "")))})",
				!left.Contains(dr) && left.Count(d => d.Id == Dirt) == 3 && left.Count(d => d.Id == Pick && d.Wear == 7) == 1 && left.Count == 4 && left.All(d => d.P.DistanceTo(spot) < 1 && d.Age < 1));
			foreach (var dd in drops.All.ToArray()) drops.Remove(dd);
			// multiplayer: a friend picking up a drop gets its wear and what a bag holds, checked
			var data = new Inventory.PackData { Id = new[] { Sand, Dirt }, N = new[] { 5, 3 }, W = new int[2] };
			Dictionary<string, object> Wire(Dictionary<string, object> m) => Cubeland.Net.BinaryPack.Unpack(Cubeland.Net.BinaryPack.Pack(m)) as Dictionary<string, object>;
			int Got(Dictionary<string, object> m) { for (int i = 0; i < Inventory.Armor0; i++) inv.SetSlot(i, 0); GotGive(Wire(m)); return Enumerable.Range(0, Inventory.Armor0).FirstOrDefault(i => inv.Id[i] != 0, -1); }
			var gm = GiveMsg(Bag, 0, data);
			int g = Got(gm);
			Check($"\"give\" sends a bag's things as data {{id, n, w}}, and the friend gets them ({In(g >= 0 ? inv.Data[g] : null)})", gm.ContainsKey("data") && !gm.ContainsKey("w") && g >= 0 && inv.Id[g] == Bag && In(inv.Data[g]) == "5 Sand, 3 Dirt");
			g = Got(GiveMsg(Bag, 0, new Inventory.PackData { Id = new[] { Pick }, N = new[] { 1 }, W = new[] { 3 } }));
			Check($"a bag holding a worn tool: the tool keeps its wear ({In(g >= 0 ? inv.Data[g] : null)})", g >= 0 && In(inv.Data[g]) == "1 Stone Pickaxe (worn 3)");
			g = Got(GiveMsg(Pick, 12, null));
			Check("and a tool's wear (w)", g >= 0 && inv.Id[g] == Pick && inv.Wear[g] == 12);
			g = Got(GiveMsg(Pick, Inventory.MaxWear(Pick) + 5, null)); bool over = g >= 0 && inv.Id[g] == Pick && inv.Wear[g] == 0;
			int g2 = Got(GiveMsg(Dirt, 4, null));
			Check("a wear past wearing it out, or on what doesn't wear, comes as none", over && g2 >= 0 && inv.Id[g2] == Dirt && inv.Wear[g2] == 0);
			g = Got(GiveMsg(Bag, 0, null));
			Check("an empty bag sends no data", !GiveMsg(Bag, 0, null).ContainsKey("data") && g >= 0 && inv.Id[g] == Bag && inv.Data[g] == null);
			Dictionary<string, object> Bad(List<object> ids, List<object> ns, List<object> ws) => new() { ["t"] = "give", ["id"] = Bag, ["data"] = new Dictionary<string, object> { ["id"] = ids, ["n"] = ns, ["w"] = ws } };
			List<object> L(params object[] v) => v.ToList();
			var bads = new (string what, Dictionary<string, object> m)[]
			{
				("more than one stack's worth", Bad(L(Dirt, Sand), L(40, 30), L(0, 0))),
				("a bag inside", Bad(L(Bag), L(1), L(0))),
				("a thing that isn't one", Bad(L(Enumerable.Range(1000, Blocks.MaxId - 1000).First(i => Blocks.Get(i) == null)), L(1), L(0))),   // (the first id from 1000 that's nothing: 4000 is Mahogany Log since Beta 0.1.5.2)
				("lists that don't match", Bad(L(Dirt, Sand), L(4), L(0, 0))),
				("none of a thing", Bad(L(Dirt), L(0), L(0))),
				("a number that isn't whole", Bad(L(Dirt), L(2.5), L(0))),
				("wear on what doesn't wear", Bad(L(Dirt), L(2), L(3))),
				("a tool worn out", Bad(L(Pick), L(1), L(Inventory.MaxWear(Pick)))),
				("more than 64 entries", Bad(Enumerable.Repeat((object)Dirt, 65).ToList(), Enumerable.Repeat((object)1, 65).ToList(), Enumerable.Repeat((object)0, 65).ToList())),
			};
			var wrong = new List<string>();
			foreach (var (what, m) in bads) { g = Got(m); if (g < 0 || inv.Id[g] != Bag || inv.Data[g] != null) wrong.Add(what); }
			Check($"the friend's game checks the data first: anything wrong gives an empty bag ({string.Join(", ", bads.Select(b => b.what))}){(wrong.Count > 0 ? "; got through: " + string.Join(", ", wrong) : "")}", wrong.Count == 0);
			// dying: the bag falls with what it holds
			for (int i = 0; i < Inventory.NS; i++) inv.SetSlot(i, 0);
			inv.SetSlot(P, Bag, 1, 0, new Inventory.PackData { Id = new[] { Dirt }, N = new[] { 7 }, W = new int[1] });
			Die();
			var fell = drops.All.FirstOrDefault(d => d.Id == Bag);
			Check($"dying: the bag falls with what it holds ({In(fell?.Data)})", fell != null && In(fell.Data) == "7 Dirt" && (Texture2D)fell.Mat.GetShaderParameter("tex") == Atlas.BagIcon(Bag, true));
			Respawn();
		}
		if (args.Contains("--croptest") && !cropTested && !menuOpen && inv.Survival && shotTimer > 0.3f)
		{   // (test, survival: the crops besides Grain: a row of wet tilled soil by water, each seed planted the way you would, grown,
			//  harvested; Corn's halves; a Cantaloupe Vine's melons; a Potato planted, not eaten, at bare soil; a Jug of Milk's jug back)
			cropTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int x0 = (int)player.Pos.X + 3, z0 = (int)player.Pos.Z, y = world.Heights[z0 * World.WX + x0];
			for (int dx = -2; dx < 12; dx++) for (int dz = -2; dz <= 4; dz++)
			{
				for (int u = -3; u <= 0; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, u == 0 ? Blocks.Stone : Blocks.Dirt);
				for (int u = 1; u < 6; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0);
			}
			int K(string key) => DataKeys.IdOf(key);
			string[] seeds = { "grain_seeds", "potato", "turnip", "flax_seeds", "corn", "cantaloupe_seeds" };
			string[] crops = { "grain", "potato", "turnip", "flax", "corn", "cantaloupe_vine" };
			world.SetBlock(x0, y, z0, Blocks.Water); world.SetBlock(x0 + 7, y, z0, Blocks.Dirt);   // (water at one end; bare dirt beyond the last, for its melon)
			world.SetBlock(x0, y, z0 + 2, Blocks.Water); world.SetBlock(x0 + 1, y + 2, z0 + 2, Blocks.Stone);   // (a Corn with a stone over it)
			int hoe = Blocks.Tools.First(t => Blocks.Get(t)?.Tool == "hoe");
			void Plant(int x, int z, string seed) { world.SetBlock(x, y, z, Blocks.Grass); inv.SetSlot(0, hoe); SelectSlot(0); FarmUse(x, y, z, Blocks.Grass); inv.SetSlot(0, K(seed)); FarmUse(x, y, z, world.GetBlock(x, y, z)); }
			for (int k = 0; k < 6; k++) Plant(x0 + 1 + k, z0, seeds[k]);
			Plant(x0 + 1, z0 + 2, "corn");
			Check("each seed plants its crop's first stage (Grain Seeds too)", Enumerable.Range(0, 6).All(k => world.GetBlock(x0 + 1 + k, y + 1, z0) == K(crops[k] + "#stage=0")));
			Check("the crops' keys: potato#stage=3, corn#stage=3,top", DataKeys.KeyOf(Blocks.CropBlock("potato", 3)) == "potato#stage=3" && DataKeys.KeyOf(Blocks.CropBlock("corn", 3, true)) == "corn#stage=3,top");
			Check("a Turnip grows as fast on dry soil as on wet; a Potato half as fast", World.GrowRate(Blocks.Tilled, K("turnip#stage=0")) == 1 && World.GrowRate(Blocks.Tilled, K("potato#stage=0")) == World.DryRate);
			// a Potato aimed at bare tilled soil is planted, not eaten; aimed anywhere else (hurt), it's eaten
			void Aim(int x, int yy, int z) { player.Pos = new Vector3(x + 0.5f, yy + 1.2f, z + 0.5f); player.Vel = Vector3.Zero; player.Flying = true; player.Pitch = -MathF.PI / 2 + 0.001f; player.Yaw = 0; }
			world.SetBlock(x0 + 9, y, z0, Blocks.Tilled); health = 4; inv.SetSlot(0, K("potato"), 2); SelectSlot(0);
			Aim(x0 + 9, y, z0); bool ate = Eat(); if (!ate && player.Raycast() is { } ph) FarmUse(ph.X, ph.Y, ph.Z, ph.Id);
			Check("a Potato aimed at bare tilled soil is planted, not eaten", !ate && health == 4 && world.GetBlock(x0 + 9, y + 1, z0) == K("potato#stage=0"));
			Aim(x0 + 9, y, z0 + 4); Check("aimed at anything else, a hurt player eats it (1 quarter heart)", Eat() && health == 5);
			world.SetBlock(x0 + 9, y + 1, z0, 0); world.SetBlock(x0 + 9, y, z0, Blocks.Stone);
			inv.SetSlot(0, K("jug_of_milk")); health = 4; Check("a Jug of Milk heals 6 and leaves its Clay Jug in your hand", Eat() && health == 10 && inv.Held == K("clay_jug"));
			health = MaxHealth;
			// fertilize the potato's soil, then let it all grow
			inv.SetSlot(0, Blocks.BoneAsh); FarmUse(x0 + 2, y + 1, z0, world.GetBlock(x0 + 2, y + 1, z0));
			for (int s = 0; s < 1500; s++) world.TickTimers(1);
			string Row() => string.Join(" ", Enumerable.Range(1, 7).Select(k => DataKeys.KeyOf(world.GetBlock(x0 + k, y + 1, z0)) + (k == 5 ? "/" + DataKeys.KeyOf(world.GetBlock(x0 + k, y + 2, z0)) : "")));
			GD.Print("crops after 1500s: " + Row());
			Check("after 25 minutes on wet soil, every crop is ripe (Grain stage 7, the others 3)", world.GetBlock(x0 + 1, y + 1, z0) == Blocks.GrainRipe && Enumerable.Range(1, 5).All(k => Blocks.B[world.GetBlock(x0 + 1 + k, y + 1, z0)] is { Stage: 3 }));
			Check("ripe Corn is two blocks: its top above", world.GetBlock(x0 + 5, y + 2, z0) == Blocks.CropBlock("corn", 3, true));
			Check("a Corn with no room above stays one block (stage 1)", world.GetBlock(x0 + 1, y + 1, z0 + 2) == Blocks.CropBlock("corn", 1) && world.GetBlock(x0 + 1, y + 2, z0 + 2) == Blocks.Stone);
			Check("the grown Cantaloupe Vine has grown a melon on its free side", world.GetBlock(x0 + 7, y + 1, z0) == Blocks.Cantaloupe);
			// harvests (survival): what breaking each gives
			int Dropped(int id) => drops.All.Count(d => d.Id == id);
			void Clear() { foreach (var dr in drops.All.ToArray()) drops.Remove(dr); }
			int BreakAt(int x, int yy, int z, string item) { Clear(); inv.SetSlot(0, 0); Aim(x, yy, z); Break(); return Dropped(K(item)); }
			int fertBefore = world.FertLeft.GetValueOrDefault(World.Idx(x0 + 2, y, z0));
			int potatoes = BreakAt(x0 + 2, y + 1, z0, "potato");
			Check($"a ripe Potato gives 2-4 Potatoes ({potatoes}), and uses one of its soil's fertilizer harvests", potatoes is >= 2 and <= 4 && world.FertLeft.GetValueOrDefault(World.Idx(x0 + 2, y, z0)) == fertBefore - 1);
			int turnips = BreakAt(x0 + 3, y + 1, z0, "turnip"); Check($"a ripe Turnip gives 1-3 Turnips ({turnips})", turnips is >= 1 and <= 3);
			Clear(); inv.SetSlot(0, 0); Aim(x0 + 4, y + 1, z0); Break(); int fl = Dropped(K("flax")), fs = Dropped(K("flax_seeds"));
			Check($"ripe Flax gives 1-2 Flax ({fl}) and 1-2 Flax Seeds ({fs})", fl is >= 1 and <= 2 && fs is >= 1 and <= 2);
			int corn = BreakAt(x0 + 5, y + 2, z0, "corn");
			Check($"breaking ripe Corn's top takes both halves and gives 2-3 Corn once ({corn})", corn is >= 2 and <= 3 && world.GetBlock(x0 + 5, y + 1, z0) == 0 && world.GetBlock(x0 + 5, y + 2, z0) == 0);
			int slices = BreakAt(x0 + 7, y + 1, z0, "cantaloupe_slice");
			Check($"a Cantaloupe gives 3-5 slices ({slices}); its vine stays", slices is >= 3 and <= 5 && world.GetBlock(x0 + 6, y + 1, z0) == Blocks.CropBlock("cantaloupe", 3));
			for (int s = 0; s < 1500; s++) world.TickTimers(1);
			// (the melon grows on a free side, picked at random: the bare dirt, or now the harvested Corn's soil too)
			Check("and grows another melon, on a free side", (world.GetBlock(x0 + 7, y + 1, z0) == Blocks.Cantaloupe) != (world.GetBlock(x0 + 5, y + 1, z0) == Blocks.Cantaloupe));
			world.SetBlock(x0 + 1, y + 2, z0 + 2, 0); int unripe = BreakAt(x0 + 1, y + 1, z0 + 2, "corn"); Check($"unripe Corn gives its seed back: 1 Corn ({unripe})", unripe == 1);
			int vineSeeds = BreakAt(x0 + 6, y + 1, z0, "cantaloupe_seeds"); Check($"a vine broken gives 1 Cantaloupe Seeds ({vineSeeds})", vineSeeds == 1);
			Check("Baked Potato, Roasted Corn, Cooked Egg and the Clay Jug (3 Clay Balls) in the Kiln", Kilns.Smelts.Any(s => s.Out == K("baked_potato") && s.From == K("potato")) && Kilns.Smelts.Any(s => s.Out == K("roasted_corn"))
				&& Kilns.Smelts.Any(s => s.Out == K("cooked_egg")) && Kilns.Smelts.Any(s => s.Out == K("clay_jug") && s.Needs.Length == 1 && s.Needs[0] == (Blocks.ClayBall, 3)));
			Check("Linen from Flax, cloth from Linen, Cantaloupe Seeds from a slice, String from Flax", new[] { "linen", "linen_cloth", "cantaloupe_seeds", "string", "red_linen_cloth", "linen_bag", "oak_fence" }.All(o => Inventory.Recipes.Any(r => r.Out == K(o))));
			// the 13 cloths cut into the 7 shapes at the Cutter (3800 + 7 x the cloth's place + the form), in the Shapes tab too
			var cloths = Enumerable.Range(0, 13).Select(k => K(k == 0 ? "linen_cloth" : new[] { "red", "orange", "yellow", "green", "aqua", "blue", "indigo", "purple", "magenta", "pink", "white", "black" }[k - 1] + "_linen_cloth")).ToArray();
			var shapes = cloths.SelectMany((c, k) => Blocks.Forms.Select((f, n) => (got: Blocks.FormItem(f, c), want: 3800 + 7 * k + n))).ToArray();
			var shapesTab = CreativeTabs.FirstOrDefault(t => t.name == "Shapes").ids ?? Array.Empty<int>();
			Check($"the 13 cloths cut into piece, slab, vslab, stairs, quarter, vstairs, vquarter: 3800-3890 ({shapes.Count(s => s.got == s.want)} of 91), each in the Shapes tab",
				shapes.All(s => s.got == s.want) && cloths.All(c => Blocks.Splittable.Contains(c)) && shapes.All(s => shapesTab.Contains(s.got))
				&& Blocks.B[3806].Name == "Linen Cloth Vertical Quarter" && Blocks.B[3887].Name == "Black Linen Cloth Stairs" && Blocks.B[3887].T[0] == Blocks.B[cloths[12]].T[0]);
			// the farming pictures put in so far (items 707-725, blocks 3700-3752): each one whose file is there shows as drawn
			var files = new Dictionary<string, string>();
			foreach (var folder in new[] { "res://textures/items", "res://textures/blocks" }) foreach (var (n, p) in Atlas.Pictures(folder)) files.TryAdd(n, p);
			var farmPics = Enumerable.Range(707, 19).Concat(Enumerable.Range(3700, 53)).Where(i => Blocks.Get(i) != null)
				.SelectMany(i => Blocks.B[i].T.Distinct()).Distinct().Where(t => files.ContainsKey(Atlas.Names[t])).ToArray();
			bool Same(Image img, int t)
			{
				if (img == null) return false;   // (a picture that can't be loaded, e.g. with no .import file yet)
				img.Convert(Image.Format.Rgba8);
				if (img.GetWidth() != Atlas.TILE) img.Resize(Atlas.TILE, Atlas.TILE, Image.Interpolation.Nearest);
				for (int py = 0; py < Atlas.TILE; py++) for (int px = 0; px < Atlas.TILE; px++)
				{
					Color a = img.GetPixel(px, py), b = Atlas.Image.GetPixel(t * Atlas.TILE + px, py);
					if (a != b && !(a.A8 == 0 && b.A8 == 0)) return false;   // (Godot's import recolours see-through pixels: those don't count)
				}
				return true;
			}
			// (the PNG file itself, not Godot's imported copy, so a picture changed since its last import shows up)
			Image Raw(string path) => FileAccess.FileExists(path) ? Image.LoadFromFile(ProjectSettings.GlobalizePath(path)) : null;
			bool AsDrawn(int t) => Same(Raw(files[Atlas.Names[t]]), t);
			var notDrawn = farmPics.Where(t => !AsDrawn(t)).Select(t => Atlas.Names[t]).ToArray();   // (a picture changed since its last import shows here)
			Check($"the farming pictures in so far show as drawn ({farmPics.Length}: {string.Join(", ", farmPics.Select(t => Atlas.Names[t]))}){(notDrawn.Length > 0 ? "; not as drawn: " + string.Join(", ", notDrawn) : "")}", notDrawn.Length == 0);
			// the cut-shape sheets (textures/blocks/sheets, 3 x 3 cells laid out like the face): each cell is its picture in the
			// game, and the pictures pieced together from them (no_top_left..., the diagonals, slab_side) are there too
			var sheets = new Dictionary<string, string>();
			foreach (var (n, p) in Atlas.Pictures("res://textures/blocks/sheets")) sheets.TryAdd(n, p);
			int TileNamed(string n) { for (int i = 0; i < Atlas.NT; i++) if (Atlas.Names[i] == n) return i; return -1; }
			string[,] cellName = { { "_top_left", "_left_half", "_bottom_left" }, { "_top_half", "", "_bottom_half" }, { "_top_right", "_right_half", "_bottom_right" } };
			string[] pieced = { "_no_top_left", "_no_top_right", "_no_bottom_left", "_no_bottom_right", "_diagonal_a", "_diagonal_b", "_slab_side" };
			var farmSheets = Enumerable.Range(3700, 53).Where(i => Blocks.Get(i) != null).SelectMany(i => Blocks.B[i].T.Distinct()).Distinct()
				.Select(t => Atlas.Names[t]).Where(sheets.ContainsKey).ToArray();
			var sheetBad = new List<string>();
			foreach (var n in farmSheets)
			{
				var sheet = Raw(sheets[n]);
				if (sheet == null) { sheetBad.Add(n + " (can't be loaded)"); continue; }
				int cs = sheet.GetWidth() / 3;
				for (int col = 0; col < 3; col++) for (int row = 0; row < 3; row++)
				{
					int t = TileNamed(n + cellName[col, row]);
					if (t < 0 || !Same(sheet.GetRegion(new Rect2I(col * cs, row * cs, cs, cs)), t)) sheetBad.Add(n + cellName[col, row]);
				}
				foreach (var suffix in pieced) if (TileNamed(n + suffix) < 0) sheetBad.Add(n + suffix);
			}
			Check($"the farming sheets in so far show as drawn, all 9 cells and the 7 pieced-together pictures ({farmSheets.Length} sheets{(sheetBad.Count > 0 ? "; wrong or missing: " + string.Join(", ", sheetBad.Take(8)) : "")})", sheetBad.Count == 0);
			player.Pos = new Vector3(x0 + 3.5f, y + 3.5f, z0 + 5.5f); player.Pitch = -0.6f; player.Yaw = 0;
		}
		if (args.Contains("--animaltest") && !animalTested && !menuOpen && inv.Survival && shotTimer > 0.3f)
		{   // (test, survival: the six animals of Farming & animals, as agreed with the web version)
			animalTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int K(string key) => DataKeys.IdOf(key);
			int x0 = (int)player.Pos.X + 4, z0 = (int)player.Pos.Z, y = world.Heights[z0 * World.WX + x0];
			for (int dx = -6; dx < 40; dx++) for (int dz = -8; dz <= 8; dz++)
			{
				world.SetBlock(x0 + dx, y, z0 + dz, Blocks.Grass); world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Dirt);
				for (int u = 1; u < 10; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0);
			}
			int Y = y + 1;
			foreach (var m in mobList.ToArray()) RemoveMob(m);
			void Clear() { foreach (var dr in drops.All.ToArray()) drops.Remove(dr); }
			int Dropped(int id) => drops.All.Count(d => d.Id == id);
			string[] six = { "chicken", "duck", "deer", "goat", "boar", "bear" };
			Check("six animals, each with its numbers, wearing <type> and <type>baby", six.All(t => MobTypes.TryGetValue(t, out var mt) && mt.Animal && mt.A != null)
				&& ModelOf(new Mob { Type = "deer", T = MobTypes["deer"], Variant = 1 }) == "deerbaby" && ModelOf(new Mob { Type = "deer", T = MobTypes["deer"] }) == "deer");
			// ---- chickens: fed, an egg later; fed full and happy, it won't eat; scratching fertilizes; pecking sets crops back ----
			var hen = SpawnAnimal("chicken", x0 + 0.5f, Y, z0 + 0.5f); hen.Vx = hen.Vz = 0;
			Check("a chicken eats Corn: happy, and an Egg on its way (60-120 s)", FeedAnimal(hen) && hen.Happy > 0 && hen.EggIn >= 60 && hen.EggIn <= 120 && hen.Fed == 1);
			Clear(); hen.EggIn = 0.05f; AnimalThink(hen, 0.1f);
			Check("then it lays an Egg where it stands", Dropped(Egg) == 1);
			hen.Fed = hen.T.A.fedMax; hen.Happy = hen.T.A.happyMax;
			Check("fed fedMax times and more than half happy, it doesn't want more", !FeedAnimal(hen));
			world.SetBlock(x0, y, z0, Blocks.Tilled);
			bool fert = false; for (int k = 0; k < 60 && !fert; k++) { hen.ScratchIn = 0; ChickenWork(hen, 0.01f, x0, Y, z0); fert = world.GetBlock(x0, y, z0) == Blocks.Fertilized; }
			Check("scratching empty tilled soil fertilizes it now and then", fert && world.FertLeft.ContainsKey(World.Idx(x0, y, z0)));
			int pot2 = Blocks.CropBlock("potato", 2), pot3 = Blocks.CropBlock("potato", 3);
			world.SetBlock(x0, y, z0 + 1, Blocks.WetTilled); world.SetBlock(x0, Y, z0 + 1, pot2);
			bool Pecked(int x, int z, int was) { for (int k = 0; k < 60; k++) { hen.PeckIn = 0; ChickenWork(hen, 0.01f, x0, Y, z0); if (world.GetBlock(x, Y, z) != was) return true; } return false; }
			Check("pecking puts a young crop beside it back a stage", Pecked(x0, z0 + 1, pot2) && world.GetBlock(x0, Y, z0 + 1) == Blocks.CropBlock("potato", 1));
			world.SetBlock(x0, Y, z0 + 1, Blocks.CropBlock("potato", 0));
			Check("a first stage is eaten", Pecked(x0, z0 + 1, Blocks.CropBlock("potato", 0)) && world.GetBlock(x0, Y, z0 + 1) == 0);
			world.SetBlock(x0, Y, z0 + 1, pot3);
			Check("a ripe crop is safe", !Pecked(x0, z0 + 1, pot3));
			world.SetBlock(x0 + 1, y, z0, Blocks.WetTilled); world.SetBlock(x0 + 1, Y, z0, Blocks.CropBlock("corn", 2)); world.SetBlock(x0 + 1, Y + 1, z0, Blocks.CropBlock("corn", 2, true));
			world.SetBlock(x0, Y, z0 + 1, 0);
			Check("Corn pecked from stage 2 loses its top", Pecked(x0 + 1, z0, Blocks.CropBlock("corn", 2)) && world.GetBlock(x0 + 1, Y, z0) == Blocks.CropBlock("corn", 1) && world.GetBlock(x0 + 1, Y + 1, z0) == 0);
			var chick = SpawnAnimal("chicken", x0 + 2.5f, Y, z0 + 0.5f, 0.5f);
			Check("a chick has babyHealth and grows up after babyTime", chick.Health == chick.T.A.babyHealth && chick.Variant == 1);
			AnimalThink(chick, 1); Check("(grown: full health, the grown model)", chick.Baby == 0 && chick.Health == chick.T.Health && chick.Variant == 0);
			var chick2 = SpawnAnimal("chicken", x0 + 3.5f, Y, z0 + 0.5f, 100); Clear(); MobDies(chick2);
			Check("a chick dies leaving nothing", drops.All.Count == 0);
			foreach (var m in mobList.ToArray()) RemoveMob(m);
			// ---- breeding: two happy chickens with room have a chick; a crowded pair doesn't ----
			var ha = SpawnAnimal("chicken", x0 + 6.5f, Y, z0 + 0.5f); var hb = SpawnAnimal("chicken", x0 + 7.3f, Y, z0 + 0.5f);
			foreach (var h in new[] { ha, hb }) { h.Happy = 100; h.MateCool = 0; h.MateCheck = 0; h.Vx = h.Vz = 0; }
			for (int f = 0; f < 400 && !mobList.Any(o => o.Type == "chicken" && o.Baby > 0); f++) { ha.MateCheck = Math.Min(ha.MateCheck, 0.05f); AnimalThink(ha, 0.1f); AnimalThink(hb, 0.1f); }
			Check($"two happy chickens with room have a chick ({mobList.Count(o => o.Type == "chicken" && o.Baby > 0)})", mobList.Count(o => o.Type == "chicken" && o.Baby > 0) == 1 && ha.MateCool > 0);
			foreach (var m in mobList.ToArray()) RemoveMob(m);
			// ---- ducks: a grown duck in water near wet soil speeds its crops; ashore, it heads for water ----
			for (int dx = 10; dx <= 12; dx++) for (int dz = -1; dz <= 1; dz++) world.SetBlock(x0 + dx, y, z0 + dz, Blocks.Water);
			world.SetBlock(x0 + 14, y, z0, Blocks.WetTilled); world.SetBlock(x0 + 20, y, z0, Blocks.WetTilled);
			var duck = SpawnAnimal("duck", x0 + 11.5f, y + 0.2f, z0 + 0.5f);   // (floating: half a block under the surface) duck.Vx = duck.Vz = 0;
			Check("a grown duck in the water helps wet soil within 4 (x1.25), not 9 away", DuckNear(World.Idx(x0 + 14, y, z0)) && !DuckNear(World.Idx(x0 + 20, y, z0)) && World.DuckRate == 1.25f);
			duck.Baby = 10; Check("a duckling doesn't", !DuckNear(World.Idx(x0 + 14, y, z0))); duck.Baby = 0;
			duck.X = x0 + 16.5f; duck.Y = Y; duck.WaterCheck = 0; duck.WaterGoal = null; inv.SetSlot(0, Blocks.Stone); SelectSlot(0);
			player.Pos = new Vector3(x0 - 20, Y, z0);
			Check("ashore, a duck heads for the nearest water", DuckWater(duck, 0.1f) && duck.WaterGoal is { } wg && wg.X < x0 + 13);
			foreach (var m in mobList.ToArray()) RemoveMob(m);
			// ---- deer: shy unless you hold its food; sows the sapling of the leaves it passed; nibbles saplings ----
			var deer = SpawnAnimal("deer", x0 + 25.5f, Y, z0 + 0.5f); deer.Vx = deer.Vz = 0;
			player.Pos = new Vector3(x0 + 20.5f, Y, z0 + 0.5f);
			inv.SetSlot(0, Blocks.Stone); deer.Flee = 0; deer.AlertIn = 0; AnimalAlert(deer, 0.1f); bool ran = deer.Flee > 0;
			inv.SetSlot(0, K("cherries")); deer.Flee = 0; deer.AlertIn = 0; AnimalAlert(deer, 0.1f);
			Check("a deer runs from you within 8, unless you hold its food", ran && deer.Flee <= 0);
			world.SetBlock(x0 + 25, Y + 4, z0 + 3, K("birch_leaves")); world.Grown.Add(World.Idx(x0 + 25, Y + 4, z0 + 3));
			deer.LookIn = 0; DeerWork(deer, 0.01f, x0 + 25, Y, z0);
			Check("it remembers the sapling of the leaves it passed", deer.Sow == K("birch_sapling"));
			world.SetBlock(x0 + 25, Y + 4, z0 + 3, 0);
			bool sown = false; for (int k = 0; k < 600 && !sown; k++) { deer.SowIn = 0; deer.NibbleIn = 99; DeerWork(deer, 0.01f, x0 + 25, Y, z0); sown = world.GetBlock(x0 + 25, Y, z0) == K("birch_sapling"); }
			Check("and now and then plants it where it stands (natural)", sown && world.Grown.Contains(World.Idx(x0 + 25, Y, z0)));
			bool nibbled = false; for (int k = 0; k < 200 && !nibbled; k++) { deer.NibbleIn = 0; deer.SowIn = 99; DeerWork(deer, 0.01f, x0 + 25, Y, z0); nibbled = world.GetBlock(x0 + 25, Y, z0) == 0; }
			Check("it nibbles a sapling close by", nibbled);
			foreach (var m in mobList.ToArray()) RemoveMob(m);
			// ---- goats: a Dead Bush or a natural low leaf block, never a hedge; milky after eating; a Clay Jug gives milk ----
			var goat = SpawnAnimal("goat", x0 + 30.5f, Y, z0 + 0.5f); goat.Vx = goat.Vz = 0;
			world.SetBlock(x0 + 32, Y, z0, K("oak_leaves"));   // (a hedge: put there)
			Check("a goat leaves a hedge alone", GoatTarget(x0 + 30, Y, z0) == null);
			world.Grown.Add(World.Idx(x0 + 32, Y, z0));
			Check("but eats a low natural leaf block", GoatTarget(x0 + 30, Y, z0) is { } gt && gt.x == x0 + 32);
			world.SetBlock(x0 + 32, Y, z0, 0); world.SetBlock(x0 + 29, Y, z0 + 1, Blocks.DeadBush);
			float before = goat.Happy; ForageDone(goat, x0 + 30, Y, z0);
			Check("and a Dead Bush (foraging: happier)", world.GetBlock(x0 + 29, Y, z0 + 1) == 0 && goat.Happy > before);
			Check("fed, it's milky for milkTime", FeedAnimal(goat) && goat.Milky == goat.T.A.milkTime);
			Check("a Clay Jug used on it: a Jug of Milk, once", MilkGoat(goat) && !MilkGoat(goat));
			foreach (var m in mobList.ToArray()) RemoveMob(m);
			// ---- boar: roots up Grass to Dirt near an Oak or Maple; hit, it turns on you (survival) ----
			var boar = SpawnAnimal("boar", x0 + 35.5f, Y, z0 + 0.5f); boar.Vx = boar.Vz = 0;
			Check("a boar roots only near an Oak or Maple log", !CanForage(boar, x0 + 35, Y, z0));
			world.SetBlock(x0 + 37, Y, z0 + 2, Blocks.Log);
			Check("near one, it does", CanForage(boar, x0 + 35, Y, z0));
			ForageDone(boar, x0 + 35, Y, z0);
			Check("the Grass turns to Dirt", world.GetBlock(x0 + 35, y, z0) == Blocks.Dirt);
			player.Pos = new Vector3(x0 + 33.5f, Y, z0 + 0.5f); inv.SetSlot(0, 0); HitMob(boar);
			Check("hit (survival), it's angry at you for angerTime", boar.Angry == boar.T.A.angerTime && boar.Flee <= 0);
			foreach (var m in mobList.ToArray()) RemoveMob(m);
			// ---- bears: prey run from a grown bear; a hungry bear catches one, which leaves only Feathers; its cubs ----
			var bear = SpawnAnimal("bear", x0 + 5.5f, Y, z0 + 5.5f); var prey = SpawnAnimal("chicken", x0 + 6.5f, Y, z0 + 5.5f);
			bear.Vx = bear.Vz = prey.Vx = prey.Vz = 0;
			prey.AlertIn = 0; AnimalAlert(prey, 0.1f);
			Check("a chicken runs from a grown bear", prey.Flee > 0 && prey.FleeFrom == bear);
			Check("a bear isn't hungry at first (huntCool)", bear.HungerIn == bear.T.A.huntCool && !BearHunt(bear, 0.1f));
			bear.HungerIn = 0; bear.HuntRetry = 0; bear.PreyCheck = 0; Clear();
			bool chased = BearHunt(bear, 0.1f);
			Check($"hungry, it catches the nearest prey, which leaves only Feathers ({Dropped(K("raw_meat"))} meat)", chased && prey.Dead > 0 && prey.Eaten && Dropped(K("raw_meat")) == 0);
			BearHunt(bear, 0.1f);
			Check("a meal: not hungry again for huntCool, and happier", bear.HungerIn == bear.T.A.huntCool && bear.Happy >= bear.T.A.mealHappy);
			var cub = SpawnAnimal("bear", x0 + 8.5f, Y, z0 + 5.5f, 100);
			player.Pos = new Vector3(x0 + 15.5f, Y, z0 + 5.5f); bear.AlertIn = 0; AnimalAlert(bear, 0.1f); bool calm = bear.Angry <= 0;
			player.Pos = new Vector3(x0 + 11.5f, Y, z0 + 5.5f); bear.AlertIn = 0; AnimalAlert(bear, 0.1f);
			Check("a bear leaves you alone, unless you go near its cub (survival)", calm && bear.Angry == bear.T.A.angerTime);
			// ---- what other games see ----
			var look = AnimalLook(cub) | 16; var copy = SpawnAnimal("bear", x0 + 9.5f, Y, z0 + 9.5f); ApplyAnimalLook(copy, look);
			Check("other games see an animal young, happy, eating or angry", copy.Variant == 1 && copy.Angry > 0 && (look & 1) == 1);
			foreach (var m in mobList.ToArray()) RemoveMob(m);
			// ---- where a group can start ----
			world.SetBlock(x0 + 2, y, z0 - 5, Blocks.Grass);
			Check("chickens can start on Grass; ducks on water with land near", AnimalSpot("chicken", x0 + 2, z0 - 5) is { } cs && cs.Y == Y && AnimalSpot("duck", x0 + 11, z0) is { } ds && ds.Y > y && AnimalSpot("duck", x0 + 2, z0 - 5) == null);
			Check("deer need a log within 6", AnimalSpot("deer", x0 + 2, z0 - 5) == null && AnimalSpot("deer", x0 + 36, z0 + 2) != null);
			player.Pos = new Vector3(x0 + 0.5f, Y + 3, z0 - 6.5f); player.Flying = true;
		}
		if (args.Contains("--fencetest") && !fenceTested && !menuOpen && shotTimer > 0.3f)
		{   // (test: fences, as agreed with the web version)
			fenceTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int K(string key) => DataKeys.IdOf(key);
			int x0 = (int)player.Pos.X + 3, z0 = (int)player.Pos.Z, y = world.Heights[z0 * World.WX + x0];
			for (int dx = -4; dx < 16; dx++) for (int dz = -6; dz <= 6; dz++)
			{
				world.SetBlock(x0 + dx, y, z0 + dz, Blocks.Stone);
				for (int u = 1; u < 7; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0);
			}
			int Y = y + 1, oak = K("oak_fence"), cherry = K("cherry_fence");
			var fences = Enumerable.Range(1, Blocks.B.Length - 1).Where(Blocks.IsFence).ToArray();
			int earthFences = fences.Count(f => Blocks.B[f].Earthlike);   // (Earthlike's 4 woods' fences, hidden until it's ready)
			Check($"16 fences and Earthlike's 4, each showing its own wood's planks, and a 3D icon (a post with rails each way): {fences.Length - earthFences} and {earthFences}", fences.Length - earthFences == 16 && earthFences == 4 && fences.All(f => Blocks.B[f].Of > 0 && Blocks.B[f].T[0] == Blocks.B[Blocks.B[f].Of].T[0])
				&& Atlas.Is3DIcon(oak) && Atlas.IconBoxes(oak).Length == 5 && Blocks.StackMaxOf(oak) > 1);
			// a line: glass, oak, oak (a door beside it), cherry, oak, stone
			int door = Enumerable.Range(1, Blocks.B.Length - 1).First(i => Blocks.B[i]?.Shape == "door");
			world.SetBlock(x0 - 1, Y, z0, Blocks.Glass);
			world.SetBlock(x0, Y, z0, oak); world.SetBlock(x0 + 1, Y, z0, oak); world.SetBlock(x0 + 2, Y, z0, cherry); world.SetBlock(x0 + 3, Y, z0, oak);
			world.SetBlock(x0 + 4, Y, z0, Blocks.Stone); world.SetBlock(x0 + 1, Y, z0 + 1, door);
			int M(int x) => Blocks.FenceMask(world.GetBlock, x, Y, z0);
			Check($"they join each other (any wood), a door and a whole block, not glass: {M(x0)} {M(x0 + 1)} {M(x0 + 2)} {M(x0 + 3)}", M(x0) == 1 && M(x0 + 1) == 7 && M(x0 + 2) == 3 && M(x0 + 3) == 3);
			void Run(float secs, bool fwd, bool jump) { for (int s = 0; s < secs * 60; s++) player.Update(1 / 60f, new Player.Keys { F = fwd, Jump = jump }); }
			void Stand(float x, float yy, float z, float yaw) { player.Pos = new Vector3(x, yy, z); player.Vel = Vector3.Zero; player.Flying = false; player.Yaw = yaw; player.Pitch = 0; Run(0.5f, false, false); }
			Stand(x0 + 2.5f, Y, z0 - 2.5f, MathF.PI); Run(2, true, false);
			Check($"you can't walk through a fence line (stopped at z {player.Pos.Z - z0:0.00})", player.Pos.Z < z0 + 6 / 16f - Player.Half + 0.01f && player.Pos.Z > z0 - 1);
			Stand(x0 + 2.5f, Y, z0 - 2.5f, MathF.PI); Run(3, true, true);
			Check($"nor jump over it (z {player.Pos.Z - z0:0.00})", player.Pos.Z < z0 + 0.5f);
			Stand(x0 + 2.5f, Y + 3, z0 + 0.5f, 0); Run(1, false, false);
			Check($"you can stand on top of it, 1.5 up ({player.Pos.Y - Y:0.00})", MathF.Abs(player.Pos.Y - (Y + Blocks.FenceTall)) < 0.01f && player.OnGround);
			// a post on its own: beside it you get past; into it you don't
			world.SetBlock(x0 + 7, Y, z0, oak);
			Stand(x0 + 6.65f, Y, z0 - 2.5f, MathF.PI); Run(2, true, false);
			Check($"a post on its own: you walk past it, close beside ({player.Pos.Z - z0:0.00})", player.Pos.Z > z0 + 2);
			Stand(x0 + 7.5f, Y, z0 - 2.5f, MathF.PI); Run(2, true, false);
			Check($"but not through it ({player.Pos.Z - z0:0.00})", player.Pos.Z < z0 + 6 / 16f - Player.Half + 0.01f);
			// aiming (from the -z side, level): the post, the rails, not the gap between them
			Player.Hit? AimAt(float x, float eyeY) { player.Pos = new Vector3(x, eyeY - Player.Eye, z0 - 2.5f); player.Flying = true; player.Yaw = MathF.PI; player.Pitch = 0; return player.Raycast(); }
			bool HitsAt(Player.Hit? h, int x) => h is { } hh && hh.X == x && hh.Y == Y && hh.Z == z0;
			Check("aiming: the post", HitsAt(AimAt(x0 + 2.5f, Y + 0.9f), x0 + 2));
			Check("a rail (low and high)", HitsAt(AimAt(x0 + 2.85f, Y + 7.5f / 16), x0 + 2) && HitsAt(AimAt(x0 + 2.85f, Y + 13.5f / 16), x0 + 2));
			Check("not the gap between the rails, nor above them", !HitsAt(AimAt(x0 + 2.85f, Y + 10.5f / 16), x0 + 2) && !HitsAt(AimAt(x0 + 2.85f, Y + 15.5f / 16), x0 + 2));
			// putting one down: not into you (it's 1.5 tall), nor into a layer of snow; no snow on top of one
			void Down(int x, float feet, int id) { player.Pos = new Vector3(x + 0.5f, feet, z0 + 4.5f); player.Vel = Vector3.Zero; player.Flying = true; player.Pitch = -MathF.PI / 2 + 0.001f; player.Yaw = 0; inv.SetSlot(0, id); SelectSlot(0); Place(); }
			Down(x0, Y + 1.2f, oak);
			Check("a fence isn't put down where its top half would be in you", world.GetBlock(x0, Y, z0 + 4) == 0);
			Down(x0, Y + 1.6f, oak);
			Check("a little higher, it is", world.GetBlock(x0, Y, z0 + 4) == oak);
			Down(x0, Y + 2.6f, Blocks.Snow);
			Check("no snow goes on top of a fence", world.GetBlock(x0, Y + 1, z0 + 4) == 0);
			world.SetBlock(x0 + 2, Y, z0 + 4, Blocks.Snow); Down(x0 + 2, Y + 2.6f, oak);
			Check("nor a fence into a layer of snow", world.GetBlock(x0 + 2, Y, z0 + 4) == Blocks.Snow);
			// a cow in a pen (3 x 3 inside) stays in it, never hopping out
			int px = x0 + 9, pz = z0 - 2;
			for (int a = 0; a < 5; a++) for (int c = 0; c < 5; c++) if (a == 0 || a == 4 || c == 0 || c == 4) world.SetBlock(px + a, Y, pz + c, oak);
			var cow = SpawnMob("cow", px + 2.5f, Y, pz + 2.5f);
			for (int s = 0; s < 1200; s++) { cow.Timer = Math.Min(cow.Timer, 0.5f); UpdateMobs(0.05f); }
			Check($"a cow in a pen walks about in it for a minute and stays in ({cow.X - px:0.0}, {cow.Y - Y:0.0}, {cow.Z - pz:0.0})", cow.X > px + 1 && cow.X < px + 4 && cow.Z > pz + 1 && cow.Z < pz + 4 && cow.Y < Y + 0.5f);
			RemoveMob(cow);
			player.Pos = new Vector3(x0 + 2.5f, Y + 2.5f, z0 - 4.5f); player.Flying = true; player.Yaw = MathF.PI; player.Pitch = -0.5f;
		}
		if (args.Contains("--wildtest") && !wildTested && !menuOpen && shotTimer > 0.3f)
		{   // (test, a Biomes world at land 5: what grew wild is where it should be; a Dead Bush put down; the linen gear)
			wildTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			var wild = world.Gen.Late.FindAll(l => l.stand == WorldGen.WildStand || l.stand == WorldGen.MelonStand);
			(int x, int y, int z) At(int i) => (i % World.WX, i / (World.WX * World.WZ), i / World.WX % World.WZ);
			var kept = wild.FindAll(l => { var (x, y, z) = At(l.i); return world.GetBlock(x, y, z) == l.id; });
			Check($"in land {current.Land}, crops, melons and Dead Bushes grew wild ({wild.Count}; {kept.Count} still there after the structures)", World.WildCrops(current.Land) ? wild.Count > 100 && kept.Count >= wild.Count * 0.95 : wild.Count == 0);
			int Under(int x, int y, int z) => world.GetBlock(x, y - 1, z);
			var rows = Nums.Worldgen.WILD_CROPS.ToDictionary(r => r.crop, r => r.ground.Select(DataKeys.IdOf).ToHashSet());
			var bushGround = Nums.Worldgen.DEAD_BUSHES.ground.Select(DataKeys.IdOf).ToHashSet();
			int bad = 0, pairs = 0, melons = 0, snowed = 0; var seen = new HashSet<string>(); var firsts = new List<string>();
			foreach (var l in kept)
			{
				var (x, y, z) = At(l.i); var d = Blocks.B[l.id];
				string kind = l.id == Blocks.Cantaloupe ? "melon" : l.id == Blocks.DeadBush ? "dead_bush" : d.Crop;
				if (seen.Add(kind)) firsts.Add($"{kind} {x},{y},{z}");
				if (l.id == Blocks.DeadBush) { if (!bushGround.Contains(Under(x, y, z))) bad++; }
				else if (l.id == Blocks.Cantaloupe) { melons++; if (!rows["cantaloupe"].Contains(Under(x, y, z)) || !new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Any(s => world.GetBlock(x + s.Item1, y, z + s.Item2) == Blocks.CropBlock("cantaloupe", 3))) bad++; }
				else if (d.Top) { pairs++; if (world.GetBlock(x, y - 1, z) != Blocks.CropBlock("corn", 3)) bad++; }
				else
				{
					if (d.Stage != 3 || !rows[d.Crop].Contains(Under(x, y, z))) bad++;
					if (d.Crop == "corn" && world.GetBlock(x, y + 1, z) != Blocks.CropBlock("corn", 3, true)) bad++;
				}
				int above = world.GetBlock(x, y + 1, z);
				if (l.id != Blocks.Cantaloupe && (above == Blocks.Snow || Blocks.Plain(l.id) != l.id)) snowed++;
			}
			Check($"each stands ripe on its ground (Corn as both halves, {pairs}; each melon beside a grown vine, {melons}): {bad} wrong", bad == 0 && (!World.WildCrops(current.Land) || pairs > 0 && melons > 0));
			Check($"no snow on or in any of them ({snowed})", snowed == 0);
			GD.Print("wild, the first of each: " + string.Join("; ", firsts));
			// a Dead Bush put down: on Sand, Grass, Dirt and any Terracotta; not on Stone or Chiseled Terracotta
			int x0 = (int)player.Pos.X + 3, z0 = (int)player.Pos.Z, y0 = world.Heights[z0 * World.WX + x0];
			int[] grounds = { Blocks.Sand, Blocks.Grass, Blocks.Dirt, Blocks.Terracotta, Blocks.ColourTerracotta[3], Blocks.FireclayTerracotta, Blocks.Stone, 67 };
			for (int k = 0; k < grounds.Length; k++) { world.SetBlock(x0 + k, y0, z0, grounds[k]); for (int u = 1; u < 4; u++) world.SetBlock(x0 + k, y0 + u, z0, 0); }
			for (int k = 0; k < grounds.Length; k++)
			{
				player.Pos = new Vector3(x0 + k + 0.5f, y0 + 2.2f, z0 + 0.5f); player.Vel = Vector3.Zero; player.Flying = true; player.Pitch = -MathF.PI / 2 + 0.001f; player.Yaw = 0;
				inv.SetSlot(0, Blocks.DeadBush); SelectSlot(0); Place();
			}
			var put = Enumerable.Range(0, grounds.Length).Select(k => world.GetBlock(x0 + k, y0 + 1, z0) == Blocks.DeadBush).ToArray();
			Check($"a Dead Bush goes down on Sand, Grass, Dirt and any Terracotta, not on Stone or Chiseled Terracotta ({string.Join("", put.Select(p => p ? 'y' : 'n'))})", put.SequenceEqual(new[] { true, true, true, true, true, true, false, false }));
			int bag = DataKeys.IdOf("linen_bag");
			Check("the Linen Bag is one to a slot, and not gear (see --bagtest)", Blocks.IsBag(bag) && Blocks.StackMaxOf(bag) == 1 && !Blocks.GearItems.Contains(bag));
		}
		if (args.Contains("--snowlog") && !snowTested && !menuOpen)
		{   // (test: snow round torches and flowers, put down and taken away the way you would, looking straight down at each spot)
			snowTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int x0 = (int)player.Pos.X + 3, z0 = (int)player.Pos.Z, y = world.Heights[z0 * World.WX + x0] + 1;
			for (int dx = -1; dx < 14; dx++) for (int dz = -2; dz <= 2; dz++)
			{
				world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Grass);
				for (int u = 0; u < 5; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0);
			}
			void Aim(int x, int yy, int z) { player.Pos = new Vector3(x + 0.5f, yy + 1.2f, z + 0.5f); player.Flying = true; player.Pitch = -MathF.PI / 2 + 0.001f; player.Yaw = 0; }
			void Hold(int id) { inv.SetSlot(0, id); SelectSlot(0); }
			// a flower planted in a layer of snow keeps it; more Snow on it does nothing (one layer only)
			int f = Blocks.Flowers[0];
			world.SetBlock(x0, y, z0, Blocks.Snow);
			Aim(x0, y, z0); Hold(f); Place();
			Check("a flower planted in snow keeps the snow", world.GetBlock(x0, y, z0) == Blocks.WithSnow(f, 1));
			Hold(Blocks.Snow); Aim(x0, y, z0); Place();
			Check("more snow on it does nothing", world.GetBlock(x0, y, z0) == Blocks.WithSnow(f, 1));
			// snow put on a plain flower; breaking takes the flower and leaves the snow
			world.SetBlock(x0 + 2, y, z0, f); Aim(x0 + 2, y, z0); Hold(Blocks.Snow); Place();
			Check("snow on a flower", world.GetBlock(x0 + 2, y, z0) == Blocks.WithSnow(f, 1));
			Aim(x0 + 2, y, z0); Hold(0); Break();
			Check("breaking it takes the flower, the snow stays", world.GetBlock(x0 + 2, y, z0) == Blocks.Snow);
			// a torch in snow; another added; it burns down and keeps its snow
			world.SetBlock(x0 + 4, y - 1, z0, Blocks.Stone);
			world.SetBlock(x0 + 4, y, z0, Blocks.WithSnow(Blocks.Torch, 1));
			Aim(x0 + 4, y, z0); Hold(Blocks.Torch); Place();
			Check("a second torch joins it in the snow", world.GetBlock(x0 + 4, y, z0) == Blocks.WithSnow(Blocks.TorchVariant(0, 2, 0), 1));
			Check("it still gives light", world.Torches.Contains(World.Idx(x0 + 4, y, z0)));
			for (int s = 0; s < 3000; s++) world.TickTimers(1);
			Check("burnt down, the snow is still round it", Blocks.SnowIn(world.GetBlock(x0 + 4, y, z0)) == 1 && Blocks.B[world.GetBlock(x0 + 4, y, z0)].Burn == 2);
			world.SetBlock(x0 + 6, y, z0, Blocks.Snow); Aim(x0 + 6, y, z0); Hold(Blocks.Torch); Place();
			Check("a torch put on a layer of snow stands in it", world.GetBlock(x0 + 6, y, z0) == Blocks.WithSnow(Blocks.Torch, 1));
			// a wall torch in snow: its wall goes, the snow stays; the ground goes, everything falls
			world.SetBlock(x0 + 8, y, z0, Blocks.Stone);
			world.SetBlock(x0 + 9, y, z0, Blocks.WithSnow(Blocks.TorchVariant(Blocks.WallIndex(-1, 0), 1, 0), 1));
			world.SetBlock(x0 + 8, y, z0, 0); world.DropTorchesAround(x0 + 8, y, z0, DropFor);
			Check("wall gone: the torch falls, the snow stays", world.GetBlock(x0 + 9, y, z0) == Blocks.Snow);
			world.SetBlock(x0 + 10, y, z0, Blocks.WithSnow(f, 1));
			world.SetBlock(x0 + 10, y - 1, z0, 0); world.DropTorchesAround(x0 + 10, y - 1, z0, DropFor);
			Check("ground gone: the flower and its snow fall", world.GetBlock(x0 + 10, y, z0) == 0);
			// stairs, slabs and pieces: put into snow, snow put on them, and the last piece taken away leaves the snow
			world.SetBlock(x0 + 12, y, z0, Blocks.Snow);
			world.SetSpots(x0 + 12, y, z0, 0x33 | 0x88, Blocks.Planks);   // (stairs)
			Check("stairs put into snow keep it", world.GetBlock(x0 + 12, y, z0) == Blocks.SnowyPieces);
			world.SetSpots(x0 + 12, y, z0, 0x33 | 0x88, 0);
			Check("the stairs taken away leave the snow", world.GetBlock(x0 + 12, y, z0) == Blocks.Snow);
			world.SetSpots(x0 + 11, y, z0, 0x33, Blocks.Planks);   // (a slab)
			Aim(x0 + 11, y, z0); Hold(Blocks.Snow); Place();
			Check("snow put on a slab lies round it", world.GetBlock(x0 + 11, y, z0) == Blocks.SnowyPieces && world.PieceMap.ContainsKey(World.Idx(x0 + 11, y, z0)));
			// a rock on snow, and the Cutter
			world.SetBlock(x0 + 13, y, z0, Blocks.Snow); Aim(x0 + 13, y, z0); Hold(Blocks.Rock); Place();
			Check("a rock put on a layer of snow lies on it", world.GetBlock(x0 + 13, y, z0) == Blocks.WithSnow(Blocks.RockBlock, 1));
			int rolls = Enumerable.Range(0, 2000).Select(_ => Blocks.SnowballsFor(3, rnd)).Distinct().Count();
			Check("snow breaks into 2-4 Snowballs a layer (3 layers: 6-12)", Enumerable.Range(0, 2000).All(_ => Blocks.SnowballsFor(3, rnd) is >= 6 and <= 12) && rolls == 7);
			Check("4 Snowballs make a Snow Block, which makes 8 Snow", Inventory.Recipes.Any(r => r.Out == Blocks.SnowBlock && r.Needs.Contains((Blocks.Snowball, 4))) && Inventory.Recipes.Any(r => r.Out == Blocks.Snow && r.N == 8 && r.Needs.Contains((Blocks.SnowBlock, 1))));
			Check("numbers match the web version (torches then flowers from 400; 388-392 pieces, Cutter, rock, clay ball, brick; 706 Snowball)",
				Blocks.WithSnow(Blocks.Torch, 1) == 400 && Blocks.WithSnow(Blocks.Flowers[4], 1) == 460 && Blocks.WithSnow(Blocks.Pieces, 1) == 388 && Blocks.WithSnow(Blocks.Cutter, 1) == 389
				&& Blocks.WithSnow(Blocks.RockBlock, 1) == 390 && Blocks.WithSnow(Blocks.ClayBallBlock, 1) == 391 && Blocks.WithSnow(Blocks.BrickBlock, 1) == 392 && Blocks.B[706]?.Name == "Snowball");
			// a row to look at: each kind standing in snow
			for (int dx = 0; dx < 14; dx++) world.SetBlock(x0 + dx, y, z0 + 2, 0);
			world.SetBlock(x0, y, z0 + 2, Blocks.WithSnow(Blocks.Flowers[2], 1));
			world.SetBlock(x0 + 1, y, z0 + 2, Blocks.WithSnow(Blocks.Torch, 1));
			world.SetBlock(x0 + 2, y, z0 + 2, Blocks.WithSnow(Blocks.RockBlock, 1));
			world.SetBlock(x0 + 3, y, z0 + 2, Blocks.WithSnow(Blocks.Cutter, 1));
			world.SetBlock(x0 + 4, y, z0 + 2, Blocks.Snow); world.SetSpots(x0 + 4, y, z0 + 2, 0x33 | 0x88, Blocks.Planks);
			world.SetBlock(x0 + 5, y, z0 + 2, Blocks.Snow); world.SetPiece(x0 + 5, y, z0 + 2, 0, 3);
			world.SetBlock(x0 + 6, y, z0 + 2, Blocks.Snow); world.SetSpots(x0 + 6, y, z0 + 2, 0x0C | 0xC0, Blocks.Planks);   // (a top slab)
			world.SetBlock(x0 + 7, y, z0 + 2, Blocks.Snow);
			player.Pos = new Vector3(x0 + 3.5f, y + 2.6f, z0 + 4.5f); player.Yaw = 0; player.Pitch = -0.75f; Hold(Blocks.Snowball);
		}
		if (args.Contains("--throwtest") && !throwTested && !menuOpen)
		{   // (test: throw Snowballs at a wall and at a cow, and check the new sounds are there)
			throwTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int x0 = (int)player.Pos.X, z0 = (int)player.Pos.Z, y = world.Heights[z0 * World.WX + x0] + 1;
			for (int dx = -2; dx <= 2; dx++) for (int dz = -12; dz <= 2; dz++) { world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Stone); for (int u = 0; u < 5; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0); }
			for (int dx = -2; dx <= 2; dx++) for (int u = 0; u < 5; u++) world.SetBlock(x0 + dx, y + u, z0 - 12, Blocks.Stone);
			player.Pos = new Vector3(x0 + 0.5f, y, z0 + 0.5f); player.Vel = Vector3.Zero; player.Yaw = 0; player.Pitch = 0; player.Flying = false;
			inv.SetSlot(0, Blocks.Snowball, 5); SelectSlot(0); throwCool = 0;
			int before = rocks.Count;
			Check("right-click with a Snowball throws it", ThrowSnowball() && rocks.Count == before + 1 && rocks[^1].Item == Blocks.Snowball);
			for (int i = 0; i < 100 && rocks.Any(r => r.Item == Blocks.Snowball); i++) UpdateRocks(0.02f);
			Check("it bursts on the wall, with a puff", !rocks.Any(r => r.Item == Blocks.Snowball) && bits.Count == 8);
			var cow = SpawnMob("cow", x0 + 0.5f, y, z0 - 5.5f); int hp = cow.Health; cow.Vx = cow.Vz = 0;
			throwCool = 0; player.Pitch = -0.05f; ThrowSnowball();
			for (int i = 0; i < 100 && rocks.Any(r => r.Item == Blocks.Snowball); i++) UpdateRocks(0.02f);
			Check("a cow hit by a Snowball is pushed back, unharmed", cow.Vz < -1 && cow.Health == hp);
			Check("the thrower isn't hit by their own Snowball", player.Vel.Length() < 0.01f);
			var rk = MakeRock(player.EyePos + new Vector3(0, 0, -1)); rk.V = new Vector3(0, 0, 0);
			Check("a thrown Rock is a flat picture", rk.Mesh.Mesh is QuadMesh); RemoveRock(rk);
			foreach (var k in new[] { "snow:step", "snow:dig", "snow:place", "throw:say", "splat:say" }) Check("sound " + k + " is made", Sound.Loudness(k) > 0.1f);
			world.SetBlock(x0, y, z0, Blocks.Snow);
			Check("walking in snow sounds like snow", player.BlockUnder() == Blocks.Snow && Sound.MatOf(Blocks.Snow) == "snow" && Sound.MatOf(Blocks.SnowBlock) == "snow");
			Check("a torch in snow breaks like a torch", Sound.MatOf(Blocks.WithSnow(Blocks.Torch, 1)) == "wood");
			Check("lying logs sound like wood", Enumerable.Range(0, 16).All(k => Sound.MatOf(Blocks.TurnedLogs + k) == "wood"));
			world.SetBlock(x0, y, z0, 0); RemoveMob(cow);
		}
		if (throwTested && !throwShown && shotTimer > 1.2f) { throwShown = true; var e = player.EyePos; MakeRock(e + new Vector3(-0.35f, 0, -1.4f)).V = new Vector3(0, 3, 0); MakeRock(e + new Vector3(0.35f, 0, -1.4f), Blocks.Snowball).V = new Vector3(0, 3, 0); }   // (thrown things to look at)
		if (args.Contains("--hudtest") && !hudTested && !menuOpen && inv.Survival && shotTimer > 0.3f)
		{   // (test, survival: the Fireclay recipe, the tool row beside the hotbar and its highlight, cows' hearts in the dark)
			hudTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			var fc = new Inventory(true); fc.Give(63); fc.Give(Blocks.Sand); fc.Give(11);
			Check("Clay + Sand + Gravel make Fireclay at the bench", Inventory.Recipes.Any(r => r.Out == Blocks.Fireclay && r.At == "bench" && fc.CanCraft(r)));
			inv.SetSlot(Inventory.BeltSlot, Blocks.Belts[1]);
			inv.SetSlot(Inventory.Tool0, Blocks.Tools[1]); inv.SetSlot(Inventory.Tool0 + 2, Blocks.CrudeAxe, 1, 10); inv.SetSlot(Inventory.Tool0 + 4, 649 + 1, 1, 200);
			SelectSlot(0);
			Check("the tool row shows a slot for each tool slot", toolSlots.Count(s => s.Visible) == inv.ToolCount && inv.ToolCount > 5);
			UsedToolSlot(Inventory.Tool0 + 2, Blocks.CrudeAxe);
			Check("the tool picked for you is highlighted", toolSlots[2].Selected && toolSlots.Count(s => s.Selected) == 1);
			UsedToolSlot(inv.Selected, inv.Held);
			Check("using what's in your hand leaves the highlight", toolSlots[2].Selected);
			inv.SetSlot(Inventory.Tool0 + 2, 0);
			Check("it goes when the tool leaves its slot", toolSlots.All(s => !s.Selected));
			inv.SetSlot(Inventory.Tool0 + 2, Blocks.CrudeAxe, 1, 10); UsedToolSlot(Inventory.Tool0 + 4, 649 + 1);
			// a cow's heart deep underground at night is dark, one in the open at noon is bright
			int x = (int)player.Pos.X, z = (int)player.Pos.Z, top = world.Heights[z * World.WX + x];
			sky.TimeOfDay = 0.0f; AddHeart(new Vector3(x + 0.5f, top - 12.5f, z + 0.5f));
			var dark = (Vector3)hearts[^1].Mat.GetShaderParameter("light");
			sky.TimeOfDay = 0.5f; AddHeart(new Vector3(x + 0.5f, top + 3.5f, z + 0.5f));
			var lit = (Vector3)hearts[^1].Mat.GetShaderParameter("light");
			Check($"hearts take the light where they are (cave {dark.X:0.00}, open {lit.X:0.00})", dark.X < 0.3f && lit.X > 0.7f);
			sky.TimeOfDay = 0.3f;
			// right-click with something in the offhand: what's in your hand goes down if it can; otherwise the offhand is used
			{
				int off = Inventory.Off0 + inv.OffSel, keep0 = inv.Id[0], keepOff = inv.Id[off];
				var (pos, yaw, pitch, fly) = (player.Pos, player.Yaw, player.Pitch, player.Flying);
				inv.SetSlot(off, Blocks.Torch, 4); SelectSlot(0);
				bool Uses(int held, bool main) { inv.SetSlot(0, held); return ActSlot(MouseButton.Right) == (main ? 0 : off); }
				Check("right-click: a block in your hand goes down, not the offhand's torch", Uses(Blocks.Stone, true));
				Check("so do slabs and stairs, pieces, doors, torches, Rocks and Clay Balls", Uses(Blocks.FormItem("slab", Blocks.Stone), true) && Uses(Blocks.FormItem("stairs", Blocks.Planks), true)
					&& Uses(Blocks.FormItem("piece", Blocks.Planks), true) && Uses(DataKeys.IdOf("cedar_door"), true) && Uses(Blocks.SmolderingTorch, true) && Uses(Blocks.Rock, true)
					&& Uses(Blocks.ClayBall, true));
				Check("an empty hand, a tool, food, a Snowball or a Stick: the offhand", Uses(0, false) && Uses(Blocks.CrudeAxe, false) && Uses(Blocks.Dates, false) && Uses(Blocks.Snowball, false) && Uses(Blocks.Stick, false));
				inv.SetSlot(off, 0);
				Check("nothing in the offhand: your hand, whatever it holds", Uses(Blocks.Stick, true) && Uses(0, true));
				inv.SetSlot(off, Blocks.Torch, 4);
				int hx = (int)pos.X + 2, hz = (int)pos.Z + 2, ht = world.Heights[hz * World.WX + hx];
				world.SetBlock(hx, ht + 1, hz, 0); world.SetBlock(hx, ht + 2, hz, 0);
				player.Pos = new Vector3(hx + 0.5f, ht + 2.2f, hz + 0.5f); player.Flying = true; player.Yaw = 0; player.Pitch = -MathF.PI / 2 + 0.001f;
				inv.SetSlot(0, Blocks.Stone, 5); inv.Act = ActSlot(MouseButton.Right); try { Place(); } finally { inv.Act = -1; }
				Check("and it really goes down: Stone placed, the offhand's torches all there", world.GetBlock(hx, ht + 1, hz) == Blocks.Stone && inv.Count[0] == 4 && inv.Id[off] == Blocks.Torch && inv.Count[off] == 4);
				world.SetBlock(hx, ht + 1, hz, 0);
				inv.SetSlot(0, keep0); inv.SetSlot(off, keepOff); SelectSlot(0);
				(player.Pos, player.Yaw, player.Pitch, player.Flying) = (pos, yaw, pitch, fly);
			}
			for (int k = 0; k < 6; k++) AddHeart(player.EyePos + new Vector3(-0.6f + k * 0.25f, -0.2f, -1.6f), k * 0.15f);   // (to look at)
		}
		if (args.Contains("--heldtest") && !heldTested && !menuOpen && inv.Survival && shotTimer > 0.5f)
		{   // (test, survival: the held stack, shift-click, Sort, the creative tabs, How to make, the Cutter's search, the pause
			//  screen and settings, the armor bar and the tool row's place)
			heldTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			void Empty() { for (int k = 0; k < Inventory.NS; k++) if (k != Inventory.BackSlot && k != Inventory.BeltSlot) inv.SetSlot(k, 0); ClearHeld(); }
			int P = Blocks.Planks, St = Blocks.Stone, max = Blocks.StackMaxOf(P), p0 = Inventory.Pocket0;
			Empty();
			inv.SetSlot(0, P, 10); PickUp(0, inv.Count[0]);
			Check("click picks up the whole stack", heldId == P && heldN == 10 && inv.Id[0] == 0);
			PutDown(p0);
			Check("click again puts it down", heldId == 0 && inv.Id[p0] == P && inv.Count[p0] == 10);
			PickUp(p0, (inv.Count[p0] + 1) / 2);
			Check("right-click picks up half (the bigger half)", heldN == 5 && inv.Count[p0] == 5);
			PutOne(p0 + 1);
			Check("right-click with a stack held puts down one", heldN == 4 && inv.Id[p0 + 1] == P && inv.Count[p0 + 1] == 1);
			spreading = true; spreadBtn = MouseButton.Left; spreadSlots.Clear(); spreadSlots.AddRange(new[] { p0 + 2, p0 + 3 }); EndSpread();
			Check("a left drag shares the held stack out evenly", heldId == 0 && inv.Count[p0 + 2] == 2 && inv.Count[p0 + 3] == 2);
			PickUp(p0, inv.Count[p0]);   // (5 held)
			spreading = true; spreadBtn = MouseButton.Right; spreadSlots.Clear(); spreadSlots.AddRange(new[] { p0 + 4, p0 + 5, p0 + 6 }); EndSpread();
			Check("a right drag puts one in each", heldN == 2 && inv.Count[p0 + 4] == 1 && inv.Count[p0 + 5] == 1 && inv.Count[p0 + 6] == 1);
			inv.SetSlot(1, St, 3); PutDown(1);
			Check("putting down on a different item swaps them", heldId == St && heldN == 3 && inv.Id[1] == P && inv.Count[1] == 2);
			ReturnHeld(false);
			Check("closing puts the held stack back", heldId == 0 && inv.CountOf(St) == 3);
			PickUp(p0 + 1, 1); Gather();
			Check("double-click gathers the same item", heldN == 10 && inv.CountOf(P) == 0);
			Empty();
			int pick = Blocks.Tools.First(t => Blocks.B[t].Tool == "pickaxe"), helmet = Blocks.GearItems.First(g => Blocks.B[g].Armor == 0);
			inv.SetSlot(p0, pick); QuickMove(p0);
			Check("shift-click from pockets: a tool goes to a free tool slot first", inv.Id[Inventory.Tool0] == pick && inv.Id[p0] == 0);
			inv.SetSlot(3, helmet); QuickMove(3);
			Check("shift-click from the hotbar: armor goes on", inv.Id[Inventory.Armor0] == helmet && inv.Id[3] == 0);
			QuickMove(Inventory.Armor0);
			Check("shift-click from a gear slot: to the hotbar", inv.Id[0] == helmet);
			inv.SetSlot(5, P, 5); inv.SetSlot(p0 + 3, P, 10); QuickMove(p0 + 3);
			Check("shift-click from pockets tops up the hotbar's stack first", inv.Count[5] == 15 && inv.Id[p0 + 3] == 0);
			QuickMove(5);
			Check("shift-click from the hotbar goes to the pockets", inv.Id[5] == 0 && inv.Id[p0] == P && inv.Count[p0] == 15);
			Empty();
			inv.SetSlot(p0 + 5, Blocks.Stick, 3); inv.SetSlot(p0 + 1, P, 7); inv.SetSlot(p0 + 8, Blocks.Cobblestone, 4); inv.SetSlot(p0 + 2, P, max - 2);
			SortPockets();
			Check("Sort: same items merged, in the tabs' order (Building, Wood, ..., Materials), fullest first",
				inv.Id[p0] == Blocks.Cobblestone && inv.Id[p0 + 1] == P && inv.Count[p0 + 1] == max && inv.Id[p0 + 2] == P && inv.Count[p0 + 2] == 5 && inv.Id[p0 + 3] == Blocks.Stick && inv.Id[p0 + 4] == 0);
			Empty();
			Check("Hearts in a tooltip: 2 -> 0.5, 4 -> 1, 6 -> 1.5", Hearts(2) == "0.5" && Hearts(4) == "1" && Hearts(6) == "1.5");
			Check("every creative block and item is in a tab", AllCreative().All(i => TabPlace(i).at != int.MaxValue) && CreativeTabs.Length == 9);
			ShowHowTo(Blocks.FormItem("stairs", Blocks.Stone));
			Check("How to make Andesite Stairs (Stone Stairs before Beta 0.1.5.2): the Cutter, 3 Andesite make 4", howPanel.Visible && howTitle.Text == "How to make Andesite Stairs" && howList.GetChildren().Count(c => !c.IsQueuedForDeletion()) == 1);
			ShowHowTo(Blocks.Grass);
			Check("How to make Grass: can't be made", howList.GetChildren().Where(c => !c.IsQueuedForDeletion()).OfType<Label>().Any(l => l.Text.StartsWith("Can't be made")));
			CloseInventory();
			Check("closing How to make closes the screen", !howPanel.Visible && !invOpen);
			cutSearch.Text = "oak"; FilterCutter();
			Check("the Cutter's search finds only what matches", cutSlots.Where(c => c.s.Visible).All(c => Blocks.B[c.id].Name.ToLowerInvariant().Contains("oak")) && cutSlots.Any(c => c.s.Visible));
			cutSearch.Text = ""; cutCan.ButtonPressed = true; FilterCutter();
			Check("the Cutter's \"Can make now\" shows nothing when you have no blocks", cutSlots.All(c => !c.s.Visible));
			cutCan.ButtonPressed = false; FilterCutter();
			Check("the pause screen has all its buttons (Save & quit was missing)", quitBtn.IsInsideTree() && discardBtn.IsInsideTree() && saveBtn.IsInsideTree());
			Check("Settings in four tabs: Video, Sound, Controls, Game", settingsPages.Select(p => p.tab.Text).SequenceEqual(new[] { "Video", "Sound", "Controls", "Game" }));
			int Piece(int k) => Blocks.GearItems.First(g => Blocks.B[g].Armor == k && Blocks.B[g].Material == "iron");
			int Wear(params int[] ks) { for (int k = 0; k < 4; k++) inv.SetSlot(Inventory.Armor0 + k, ks.Contains(k) ? Piece(k) : 0); DrawArmorBar(); return ArmorQuarters(inv.ArmorBlock()); }
			string Icons() => string.Join(",", Enumerable.Range(0, HEARTS).Select(j => ArmorIconQuarters(j)));   // (quarters in each icon, left to right)
			Check("armor bar quarters: helmet 4, chest 8, legs 5, boots 3, full set 20, chest + legs 13, none 0",
				Wear(0) == 4 && Wear(1) == 8 && Wear(2) == 5 && Wear(3) == 3 && Wear(0, 1, 2, 3) == 20 && Wear(1, 2) == 13 && Wear() == 0);
			Check("the armor bar mirrors the hearts (as many), and with no armor on it's hidden: no empty pictures", !armorShown && armorIcons.Length == HEARTS && armorIcons.All(t => t.Texture == armorPics[0]));
			Wear(0);
			Check($"a helmet: the armor bar shows, the rightmost icon full, the rest empty ({Icons()})", armorShown && Icons() == "0,0,0,0,4" && armorIcons[HEARTS - 1].Texture == armorPics[4] && armorIcons.All(t => !t.FlipH));
			Wear(2);
			Check($"leggings: one and a quarter from the right, drawn as they are ({Icons()})", Icons() == "0,0,0,1,4" && armorIcons[HEARTS - 2].Texture == armorPics[1] && !armorIcons[HEARTS - 2].FlipH);
			Wear(3);
			Check($"boots: three quarters ({Icons()})", Icons() == "0,0,0,0,3" && armorIcons[HEARTS - 1].Texture == armorPics[3]);
			Wear(1, 2);   // (left on, to see where the bar goes)
			if (args.Contains("--tools")) { var ts = Blocks.Tools.Where(t => Blocks.B[t].Material == "iron").ToArray(); for (int k = 0; k < Math.Min(ts.Length, inv.ToolCount); k++) inv.SetSlot(Inventory.Tool0 + k, ts[k]); }   // (to look at)
			Check($"chest + legs: three and a quarter ({Icons()})", Icons() == "0,1,4,4,4" && armorIcons[1].Texture == armorPics[1]);
			if (args.Contains("--noarmor")) { Wear(); Check("all armor off: the armor bar is hidden again", !armorShown); }   // (to see the HUD with no armor bar)
		}
		if (args.Contains("--heldtest") && heldTested && !heldPlaced && shotTimer > 1.5f)
		{   // (a frame later: where the tool row and armor bar went)
			heldPlaced = true;
			CheckHudLayout();
			// --longrow: a long tool row (a steel belt: 12 tool slots), too long for the gap between the hearts and the armor bar
			if (args.Contains("--longrow")) { inv.SetSlot(Inventory.BeltSlot, Blocks.Belts[^1]); DrawHotbar(); }
		}
		if (args.Contains("--longrow") && heldPlaced && !longChecked && shotTimer > 2.2f)
		{
			longChecked = true;
			CheckHudLayout();
		}
		if (args.Contains("--saplingtest") && !saplingTested && !menuOpen && shotTimer > 0.3f)
		{   // (test: saplings: the 16 and the leaves that drop them; planted only on their ground; growing (lucky ticks, Bone Ash) into
			//  the tree the world grows there, only with room for its trunk; a Redwood from four; an Oak in the plains; what grows is
			//  natural; the save. Best with --survival --biomes)
			saplingTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int Id(string key) => DataKeys.IdOf(key);
			int oak = Id("oak_sapling"), palm = Id("palm_sapling"), cedar = Id("cedar_sapling"), redwood = Id("redwood_sapling");
			var saplings = Enumerable.Range(1, Blocks.MaxId - 1).Where(i => Blocks.Get(i)?.Grows != null).ToArray();
			var dropping = Enumerable.Range(1, Blocks.MaxId - 1).Where(i => Blocks.Get(i)?.Sapling > 0).ToArray();
			Check($"20 saplings ({saplings.Length}); the 20 trees' leaves drop their own ({dropping.Length}; Dead has none)", saplings.Length == 20 && dropping.Length == 20
				&& dropping.All(i => Blocks.IsLeaves(i) && Blocks.B[Blocks.B[i].Sapling].Grows != null) && Blocks.B[Blocks.Leaves].Sapling == oak
				&& Blocks.B[Blocks.PalmLeaves].Sapling == palm && Blocks.B[Id("cedar_leaves")].Sapling == cedar && Blocks.B[Id("redwood_leaves")].Sapling == redwood);
			Check("only Palm, Orange and Palo Verde grow on Sand too", string.Join(",", saplings.Where(i => Blocks.B[i].Sand).Select(DataKeys.KeyOf)) == "orange_sapling,palo_verde_sapling,palm_sapling");
			Check("saplings are blocks (the main hand puts them down), in the creative inventory, plants you walk through",
				saplings.All(i => CanPutDown(i) && AllCreative().Contains(i) && Blocks.B[i].Shape == "plant" && Blocks.B[i].NoCollide));
			// a flat field of Grass, clear high above
			int x0 = (int)player.Pos.X - 10, z0 = (int)player.Pos.Z - 4, y = world.Heights[z0 * World.WX + x0] + 1;
			void Field(int ax, int az, int w, int d, int yy, int up) { for (int dx = 0; dx < w; dx++) for (int dz = 0; dz < d; dz++) { world.SetBlock(ax + dx, yy - 1, az + dz, Blocks.Grass); for (int u = 0; u < up; u++) if (world.GetBlock(ax + dx, yy + u, az + dz) != 0) world.SetBlock(ax + dx, yy + u, az + dz, 0); } }
			Field(x0 - 6, z0 - 6, 46, 26, y, 44);
			bool Plant(int id, int x, int z)
			{   // (standing above the spot, looking straight down, holding it)
				inv.SetSlot(inv.Selected, id); player.Pos = new Vector3(x + 0.5f, y + 1.2f, z + 0.5f); player.Vel = Vector3.Zero; player.Flying = true;
				player.Pitch = -MathF.PI / 2 + 0.001f; player.Yaw = 0; Place(); return world.GetBlock(x, y, z) == id;
			}
			int pz = z0 + 14;
			Check("an Oak sapling plants on Grass (and starts counting its lucky ticks)", Plant(oak, x0, pz) && world.Saplings.ContainsKey(World.Idx(x0, y, pz)));
			world.SetBlock(x0 + 2, y - 1, pz, Blocks.Sand); world.SetBlock(x0 + 4, y - 1, pz, Blocks.Stone); world.SetBlock(x0 + 6, y - 1, pz, Blocks.Tilled); world.SetBlock(x0 + 8, y, pz, Blocks.Snow);
			Check("but not on Sand, Stone or tilled soil, nor into a layer of snow", !Plant(oak, x0 + 2, pz) && !Plant(oak, x0 + 4, pz) && !Plant(oak, x0 + 6, pz) && !Plant(oak, x0 + 8, pz) && world.GetBlock(x0 + 8, y, pz) == Blocks.Snow);
			int[] grounds = { Blocks.Dirt, Blocks.SnowyGrass, Blocks.SwampGrass, Blocks.Mud, Blocks.Peat };
			for (int k = 0; k < grounds.Length; k++) world.SetBlock(x0 + 10 + 2 * k, y - 1, pz, grounds[k]);
			Check("a Palm sapling plants on Sand too; any on Dirt, Snowy Grass, Swamp Grass, Mud or Peat", Plant(palm, x0 + 2, pz) && Enumerable.Range(0, grounds.Length).All(k => Plant(cedar, x0 + 10 + 2 * k, pz)));
			world.SetBlock(x0 + 2, y - 1, pz, Blocks.Dirt);
			Check("it pops off when the ground under it goes", world.GetBlock(x0 + 2, y, pz) == palm);
			for (int x = x0; x < x0 + 20; x++) { world.SetBlock(x, y, pz, 0); world.SetBlock(x, y - 1, pz, Blocks.Grass); }
			Check("(and stops counting when it's gone)", !world.Saplings.ContainsKey(World.Idx(x0, y, pz)));
			// growing: an Oak sapling grows by itself into the tree the world grows there; another under a block waits for room
			long gi = World.Idx(x0, y, z0); int nx = x0 + 10; long ni = World.Idx(nx, y, z0);
			string want = world.Gen.BiomeOf(x0, z0) == WorldGen.Plains ? "prairie_oak" : "oak";
			var plan = world.Gen.PlanTree(want, x0, y - 1, z0, (a, b, c) => World.Idx(a, b, c) == gi ? 0 : world.GetBlock(a, b, c), (a, c) => y - 1);
			var roomy = world.Gen.PlanTree("oak", nx, y - 1, z0, (a, b, c) => World.Idx(a, b, c) == ni ? 0 : world.GetBlock(a, b, c), (a, c) => y - 1);
			var leaf = roomy.First(p => Blocks.IsLeaves(p.id) && p.y > y + 1);
			Plant(oak, x0, z0); Plant(oak, nx, z0);
			world.SetBlock(nx, y + 2, z0, Blocks.Stone); world.SetBlock(leaf.x, leaf.y, leaf.z, Blocks.Glass);
			Check("a sapling under a block has no room to grow", world.GrowSapling(nx, y, z0, justLook: true) == World.Growth.NoRoom);
			int secs = 0;
			while ((world.GetBlock(x0, y, z0) == oak || world.Saplings.GetValueOrDefault(ni) < World.SaplingHits - 1) && secs < 40000) { world.TickTimers(1); secs++; }
			Check($"a sapling grows by itself after a while ({secs} s here; {World.SaplingTime} s on average)", world.GetBlock(x0, y, z0) != oak && secs > 3);
			Check($"into the {want} the world grows there, every block of it ({plan?.Count} blocks)", plan != null && plan.All(p => world.GetBlock(p.x, p.y, p.z) == p.id) && world.GetBlock(x0, y, z0) == Blocks.Log);
			Check("what grew is natural (felling and stage breaking treat it as a tree), and saved", plan != null && plan.All(p => Natural(p.x, p.y, p.z) && world.Edits.ContainsKey(World.Idx(p.x, p.y, p.z))) && !world.Saplings.ContainsKey(gi));
			Check("with no room for its trunk, it waits (at its last lucky tick)", world.GetBlock(nx, y, z0) == oak && world.Saplings.GetValueOrDefault(ni) == World.SaplingHits - 1 && world.GetBlock(nx, y + 2, z0) == Blocks.Stone);
			world.SetBlock(nx, y + 2, z0, 0); secs = 0;
			while (world.GetBlock(nx, y, z0) == oak && secs < 40000) { world.TickTimers(1); secs++; }
			Check($"with room again, it grows at its next lucky tick ({secs} s)", world.GetBlock(nx, y, z0) == Blocks.Log);
			Check("its leaves never break anything (the Glass in their way is still there)", world.GetBlock(leaf.x, leaf.y, leaf.z) == Blocks.Glass);
			world.SetBlock(x0, y + 1, z0, 0);
			Check("a grown block changed is yours (not natural any more)", !world.Grown.Contains(World.Idx(x0, y + 1, z0)) && !Natural(x0, y + 1, z0));
			{   // (Beta 0.1.5.5) the four new woods' saplings grow into their own trees
				string[] news = { "mahogany", "acacia", "stiltroot", "teak" }; bool allGrew = true; var sizes = new List<string>();
				for (int k = 0; k < news.Length; k++)
				{
					int sx = x0 + 10 * k, sz = z0 + 8, sid = Id(news[k] + "_sapling"); long si = World.Idx(sx, y, sz);
					world.SetBlock(sx, y, sz, sid);
					var tp = world.Gen.PlanTree(news[k], sx, y - 1, sz, (a, b, c) => World.Idx(a, b, c) == si ? 0 : world.GetBlock(a, b, c), (a, c) => y - 1);
					bool grew = tp != null && world.GrowSapling(sx, y, sz) == World.Growth.Grew && tp.All(q => world.GetBlock(q.x, q.y, q.z) == q.id) && world.GetBlock(sx, y + 2, sz) == Id(news[k] + "_log");
					sizes.Add($"{news[k]} {tp?.Count}"); allGrew &= grew;
				}
				Check($"the 4 new woods' saplings grow into their own trees ({string.Join(", ", sizes)} blocks)", allGrew);
			}
			// a Redwood: only from four saplings in a square
			int rx = x0 + 20, rlog = Id("redwood_log");
			world.SetBlock(rx, y, z0, redwood);
			Check("a lone Redwood sapling never grows", world.GrowSapling(rx, y, z0, justLook: true) == World.Growth.NeedsFour);
			world.SetBlock(rx + 1, y, z0, redwood); world.SetBlock(rx, y, z0 + 1, redwood);
			Check("nor three of them", world.GrowSapling(rx + 1, y, z0, justLook: true) == World.Growth.NeedsFour);
			world.SetBlock(rx + 1, y, z0 + 1, redwood);
			Check("four in a square grow one Redwood (from any of them), its 2 x 2 trunk where they stood", world.GrowSapling(rx + 1, y, z0 + 1) == World.Growth.Grew
				&& new[] { (0, 0), (1, 0), (0, 1), (1, 1) }.All(c => world.GetBlock(rx + c.Item1, y, z0 + c.Item2) == rlog && world.GetBlock(rx + c.Item1, y + 20, z0 + c.Item2) == rlog));
			// Bone Ash (survival: it's used up)
			if (inv.Survival)
			{
				int bx = x0 + 30;
				world.SetBlock(bx, y, z0, cedar); world.SetBlock(bx, y + 3, z0, Blocks.Stone); world.SetBlock(bx + 4, y, z0, redwood);
				inv.SetSlot(inv.Selected, Blocks.BoneAsh, 64); int Ash() => inv.Count[inv.Selected];
				FarmUse(bx, y, z0, cedar); FarmUse(bx + 4, y, z0, redwood);
				Check("Bone Ash on a sapling with no room, or a lone Redwood: not used", Ash() == 64 && world.GetBlock(bx, y, z0) == cedar && world.GetBlock(bx + 4, y, z0) == redwood);
				world.SetBlock(bx, y + 3, z0, 0);
				int used = 0; while (world.GetBlock(bx, y, z0) == cedar && used < 60) { FarmUse(bx, y, z0, cedar); used++; }
				Check($"with room, each Bone Ash is used, and now and then the sapling grows at once ({used} used)", world.GetBlock(bx, y, z0) == Id("cedar_log") && Ash() == 64 - used);
				world.SetBlock(bx + 4, y, z0, 0);
				// leaves broken without a hoe now and then drop their sapling; with a hoe, never
				int cl = Id("cedar_leaves");
				int Picked(int held)
				{
					foreach (var dr in drops.All.ToArray()) drops.Remove(dr);
					// (a fresh one each time: a hoe wears out)
					for (int k = 0; k < 200; k++) { inv.SetSlot(inv.Selected, held); world.SetBlock(x0 + 12, y, pz, cl); player.Pos = new Vector3(x0 + 12.5f, y + 1.2f, pz + 0.5f); player.Pitch = -MathF.PI / 2 + 0.001f; Break(); }
					return drops.All.Count(d => d.Id == cedar);
				}
				int byHand = Picked(0), byHoe = Picked(Blocks.Tools.First(t => Blocks.B[t].Tool == "hoe"));
				Check($"Cedar Leaves drop Cedar Saplings by hand now and then ({byHand} in 200), never with a hoe ({byHoe})", byHand is > 4 and < 45 && byHoe == 0);
				world.SetBlock(x0 + 12, y, pz, 0);
			}
			// an Oak sapling in the plains grows a prairie oak (anywhere else, the woods' oak)
			int qx = -1, qz = -1;
			for (int z = 12; z < World.WZ - 12 && qx < 0; z += 5) for (int x = 12; x < World.WX - 12; x += 5)
				if (world.Gen.BiomeOf(x, z) == WorldGen.Plains && Math.Abs(x - x0) + Math.Abs(z - z0) > 60) { qx = x; qz = z; break; }
			if (qx < 0) Check("a plains spot to try an Oak sapling in (a Biomes world: --biomes)", false);
			else
			{
				int qy = world.Heights[qz * World.WX + qx] + 1; long qi = World.Idx(qx, qy, qz);
				Field(qx - 5, qz - 5, 11, 11, qy, 16); world.SetBlock(qx, qy, qz, oak);
				var prairie = world.Gen.PlanTree("prairie_oak", qx, qy - 1, qz, (a, b, c) => World.Idx(a, b, c) == qi ? 0 : world.GetBlock(a, b, c), (a, c) => qy - 1);
				Check($"an Oak sapling in the plains grows a prairie oak ({prairie?.Count} blocks)", world.GrowSapling(qx, qy, qz) == World.Growth.Grew && prairie != null && prairie.All(p => world.GetBlock(p.x, p.y, p.z) == p.id));
			}
			// the save: lucky ticks so far and what grew
			Plant(oak, x0 + 4, pz); world.Saplings[World.Idx(x0 + 4, y, pz)] = 2;
			var so = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
			var back = System.Text.Json.JsonSerializer.Deserialize<Worlds.Save>(System.Text.Json.JsonSerializer.Serialize(new Worlds.Save { Saplings = new(world.Saplings), Grown = world.Grown.ToArray() }, so), so);
			Check($"saplings' lucky ticks ({world.Saplings.Count}) and what grew ({world.Grown.Count}) go in the save", world.Saplings.Count > 0 && world.Grown.Count > 0
				&& back.Saplings.Count == world.Saplings.Count && back.Saplings.All(kv => world.Saplings[kv.Key] == kv.Value) && back.Grown.ToHashSet().SetEquals(world.Grown));
			player.Pos = new Vector3(x0 + 14.5f, y + 6, z0 - 14); player.Pitch = -0.25f; player.Yaw = 0;   // (to look at: the trees that grew)
		}
		if (args.Contains("--doortest") && !doorTested && !menuOpen && shotTimer > 0.3f)
		{   // (test: doors: put down, opened and shut from either side, double doors, broken, walked through; their recipes and
			//  keys; fruit from fruit trees' leaves; then a row of every door, some open, to look at)
			doorTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int x0 = (int)player.Pos.X - 8, z0 = (int)player.Pos.Z - 8, y = world.Heights[z0 * World.WX + x0] + 1;
			for (int dx = -2; dx < 20; dx++) for (int dz = -4; dz < 14; dz++) { world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Stone); for (int u = 0; u < 8; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0); }
			int cedar = DataKeys.IdOf("cedar_door");
			int D(int f, int g, bool o, int half) => Blocks.DoorState(cedar, f, g, o, half);
			void Stand(float x, float z) { player.Pos = new Vector3(x, y, z); player.Vel = Vector3.Zero; player.Flying = true; }
			void Put(int x, float hitX) { inv.SetSlot(inv.Selected, cedar); PlaceDoor(new Player.Hit { X = x, Y = y - 1, Z = z0, Ny = 1, Sub = -1, Id = Blocks.Stone, Pt = new Vector3(hitX, y, z0 + 0.5f) }, cedar); }
			bool Is(int x, int f, int g, bool o) => world.GetBlock(x, y, z0) == D(f, g, o, 0) && world.GetBlock(x, y + 1, z0) == D(f, g, o, 1);
			Check($"{(WorldGen.EarthlikeOn ? 20 : 16)} doors (16, and Earthlike's 4 since Beta 0.1.5.3), each with an item and 32 states", Blocks.ItemRows.Count(i => Blocks.DoorFirst(i) > 0) == (WorldGen.EarthlikeOn ? 20 : 16) && Blocks.ItemRows.Where(i => Blocks.DoorFirst(i) > 0).All(i => Enumerable.Range(0, 32).All(k => Blocks.IsDoor(Blocks.DoorFirst(i) + k))) && Blocks.DoorFirst(cedar) == cedar + 1 && Blocks.IsDoor(cedar + 32) && !Blocks.IsDoor(cedar + 33));
			Check("a door's key names its facing, half, hinge and open", DataKeys.KeyOf(D(1, 0, true, 1)) == "cedar_door#facing=1,half=1,hinge=0,open=1" && DataKeys.KeyOf(cedar) == "cedar_door");
			// one door, put down from the south (+z), clicked left of the middle: it faces you, hinge on the left
			Stand(x0 + 0.5f, z0 + 3.5f); Put(x0, x0 + 0.3f);
			Check("a door goes down shut, 2 tall, facing you, its hinge on the side you clicked", Is(x0, 0, 0, false));
			Check("shut, it's a wall", world.Solid(x0, y, z0) && world.Solid(x0, y + 1, z0));
			Put(x0 + 3, x0 + 3.8f);
			Check("clicked right of the middle: the hinge is on the right", Is(x0 + 3, 0, 1, false));
			ToggleDoor(x0, y + 1, z0);
			Check("right-clicked from in front: it opens away from you (both halves)", Is(x0, 0, 0, true));
			Check("open, you can walk through", !world.Solid(x0, y, z0) && !world.Solid(x0, y + 1, z0));
			Check("it swings for a moment (the chunk leaves it out meanwhile)", ChunkMesher.Swinging.ContainsKey(World.Idx(x0, y, z0)) && swings.Count > 0);
			UpdateDoorSwings(0.2f);
			Check("then stands in its new place", !ChunkMesher.Swinging.ContainsKey(World.Idx(x0, y, z0)) && swings.Count == 0);
			// open, it's turned a quarter about its hinge and reaches 10/16 out of its block's back (the boxes agreed with the web version)
			float[][][] openBoxes =
			{
				new[] { new[] { 0, 0, -0.625f, 0.25f, 1, 0.375f }, new[] { 0.75f, 0, -0.625f, 1, 1, 0.375f } },
				new[] { new[] { -0.625f, 0, 0.75f, 0.375f, 1, 1 }, new[] { -0.625f, 0, 0, 0.375f, 1, 0.25f } },
				new[] { new[] { 0.75f, 0, 0.625f, 1, 1, 1.625f }, new[] { 0, 0, 0.625f, 0.25f, 1, 1.625f } },
				new[] { new[] { 0.625f, 0, 0, 1.625f, 1, 0.25f }, new[] { 0.625f, 0, 0.75f, 1.625f, 1, 1 } },
			};
			bool boxesOk = true;
			for (int f = 0; f < 4; f++) for (int g = 0; g < 2; g++) for (int half = 0; half < 2; half++)
			{
				var bx = Blocks.B[D(f, g, true, half)].Box; var sb = Blocks.B[D(f, g, false, half)].Box;
				for (int k = 0; k < 6; k++) boxesOk &= MathF.Abs(bx[k] - openBoxes[f][g][k]) < 1e-6f;
				boxesOk &= f % 2 == 0 ? sb[2] == 0.375f && sb[5] == 0.625f && sb[0] == 0 && sb[3] == 1 : sb[0] == 0.375f && sb[3] == 0.625f && sb[2] == 0 && sb[5] == 1;
			}
			Check("open, a door reaches 10/16 out of its block's back, against its hinge side (all 8 ways); shut, across the middle", boxesOk);
			// (the door at x0 is open: facing 0, hinge 0, so its panel is x0 to x0 + 0.25 across, z0 - 0.625 to z0 + 0.375)
			Stand(x0 + 4.5f, z0 - 4.5f);
			Check("you can't walk into the part sticking out of its block", !player.Fits(new Vector3(x0 + 0.25f + Player.Half - 0.05f, y, z0 - 0.5f)) && player.Fits(new Vector3(x0 + 0.25f + Player.Half + 0.05f, y, z0 - 0.5f)));
			Stand(x0 + 0.2f, z0 - 0.5f);
			Check("but a door swung open into you doesn't hold you: you can walk out of it", player.Fits(player.Pos + new Vector3(0.3f, 0, 0)) && player.Fits(player.Pos + new Vector3(0, 0, -0.6f)));
			Stand(x0 + 0.125f, z0 - 3.5f); player.Yaw = MathF.PI; player.Pitch = 0;
			var aimed = player.Raycast();
			Check("aimed at from behind, the part sticking out is the door", aimed is { } ah && ah.X == x0 && ah.Z == z0 && ah.Y == y + 1 && MathF.Abs(ah.Pt.Z - (z0 - 0.625f)) < 1e-3f);
			Stand(x0 + 0.725f, z0 - 3.5f); player.Yaw = MathF.PI; player.Pitch = 0;
			Check("and a little to the side, it isn't", player.Raycast() is not { X: var ax, Z: var az } || ax != x0 || az != z0);
			ToggleDoor(x0, y, z0); world.SetBlock(x0, y, z0 - 1, Blocks.Stone); world.SetBlock(x0, y + 1, z0 - 1, Blocks.Stone);
			Stand(x0 + 0.5f, z0 + 3.5f); ToggleDoor(x0, y, z0); UpdateDoorSwings(0.2f);
			Check("it opens into a block behind it all the same (the block stays)", Is(x0, 0, 0, true) && world.GetBlock(x0, y, z0 - 1) == Blocks.Stone);
			Stand(x0 + 0.125f, z0 - 3.5f); player.Yaw = MathF.PI; player.Pitch = 0;
			Check("aimed at, the block in front of the hidden part is the block", player.Raycast() is { } bh && bh.Id == Blocks.Stone && bh.Z == z0 - 1);
			world.SetBlock(x0, y, z0 - 1, 0); world.SetBlock(x0, y + 1, z0 - 1, 0);
			ToggleDoor(x0, y, z0);
			Check("right-clicked again: shut", Is(x0, 0, 0, false));
			Stand(x0 + 0.5f, z0 - 2.5f); ToggleDoor(x0, y, z0);
			Check("opened from behind: turned round first, so it swings away from you", Is(x0, 2, 1, true));
			ToggleDoor(x0, y, z0);
			Check("and shuts again the same way", Is(x0, 2, 1, false));
			// a double door: hinges on the outsides
			Stand(x0 + 7f, z0 + 3.5f); Put(x0 + 6, x0 + 6.2f); Put(x0 + 7, x0 + 7.8f);
			Check("two doors side by side, hinges outside", Is(x0 + 6, 0, 0, false) && Is(x0 + 7, 0, 1, false));
			ToggleDoor(x0 + 6, y, z0);
			Check("a double door opens together", Is(x0 + 6, 0, 0, true) && Is(x0 + 7, 0, 1, true));
			ToggleDoor(x0 + 7, y + 1, z0);
			Check("and shuts together", Is(x0 + 6, 0, 0, false) && Is(x0 + 7, 0, 1, false));
			Stand(x0 + 7f, z0 - 2.5f); ToggleDoor(x0 + 7, y, z0);
			Check("a double door opened from behind: both turn round and open away from you", Is(x0 + 6, 2, 1, true) && Is(x0 + 7, 2, 0, true));
			ToggleDoor(x0 + 6, y, z0);
			Check("and shut together", Is(x0 + 6, 2, 1, false) && Is(x0 + 7, 2, 0, false));
			UpdateDoorSwings(0.2f);
			// picking it (creative) gives the door; breaking either half takes both, survival: 1 door
			player.Pos = new Vector3(x0 + 3.5f, y + 2.2f, z0 + 0.5f); player.Pitch = -MathF.PI / 2 + 0.001f; player.Yaw = 0;
			if (!inv.Survival) { inv.SetSlot(inv.Selected, 0); PickBlock(); Check("picking a door gives the door", inv.Held == cedar); }
			int Dropped(int id) => drops.All.Count(d => d.Id == id);
			foreach (var dr in drops.All.ToArray()) drops.Remove(dr);
			inv.SetSlot(inv.Selected, Blocks.Tools.First(t => Blocks.B[t].Tool == "axe")); Break();
			Check("breaking its top half takes the whole door" + (inv.Survival ? ", and gives 1 door" : ""), world.GetBlock(x0 + 3, y, z0) == 0 && world.GetBlock(x0 + 3, y + 1, z0) == 0 && (!inv.Survival || Dropped(cedar) == 1));
			Check("doors sound like wood", Sound.MatOf(D(0, 0, false, 0)) == "wood" && Sound.MatOf(D(3, 1, true, 1)) == "wood");
			int planks = DataKeys.IdOf("cedar_planks");
			Check("6 Cedar Planks make 2 Cedar Doors at the bench", Inventory.Recipes.Any(r => r.Out == cedar && r.N == 2 && r.At == "bench" && r.Needs.Length == 1 && r.Needs[0] == (planks, 6)));
			Check("doors burn (2), in the Wood tab, not standing ones", Kilns.Fuel.GetValueOrDefault(cedar) == 2 && AllCreative().Contains(cedar) && !AllCreative().Any(Blocks.IsDoor));
			// fruit: cherry leaves broken without a hoe now and then give Cherries; with a hoe, never
			int cl = DataKeys.IdOf("cherry_leaves"), cherries = DataKeys.IdOf("cherries");
			Check("Cherry, Orange and Juniper leaves bear their fruit; Palm, Dates", Blocks.B[cl].Fruit == cherries && Blocks.B[DataKeys.IdOf("orange_leaves")].Fruit == DataKeys.IdOf("oranges")
				&& Blocks.B[DataKeys.IdOf("juniper_leaves")].Fruit == DataKeys.IdOf("juniper_berries") && Blocks.B[Blocks.PalmLeaves].Fruit == Blocks.Dates && Blocks.B[Blocks.Leaves].Fruit == 0);
			if (inv.Survival)
			{
				int Pick(int held) { foreach (var dr in drops.All.ToArray()) drops.Remove(dr); inv.SetSlot(inv.Selected, held); for (int k = 0; k < 80; k++) { world.SetBlock(x0 + 12, y, z0, cl); player.Pos = new Vector3(x0 + 12.5f, y + 1.2f, z0 + 0.5f); Break(); } return Dropped(cherries); }
				int byHand = Pick(0), byHoe = Pick(Blocks.Tools.First(t => Blocks.B[t].Tool == "hoe"));
				Check($"Cherry Leaves: Cherries by hand now and then ({byHand} in 80), never with a hoe ({byHoe})", byHand is > 5 and < 40 && byHoe == 0);
				world.SetBlock(x0 + 12, y, z0, 0);
			}
			Check("the woods' logs are logs (felling, the axe), lying ones too", Blocks.IsLogBlock(DataKeys.IdOf("redwood_log")) && Blocks.IsLogBlock(DataKeys.IdOf("redwood_log#axis=2")) && Blocks.IsLogBlock(DataKeys.IdOf("dead_log"))
				&& Blocks.LogOf(DataKeys.IdOf("dead_log#axis=1")) == DataKeys.IdOf("dead_log") && Blocks.Turned(DataKeys.IdOf("stripped_holly_log"), 2) == DataKeys.IdOf("stripped_holly_log#axis=2"));
			// to look at: every door in a row, every other one open, and the double door open
			for (int dx = -2; dx < 20; dx++) for (int u = 0; u < 3; u++) world.SetBlock(x0 + dx, y + u, z0, 0);
			var doorItems = Blocks.ItemRows.Where(i => Blocks.DoorFirst(i) > 0).ToArray();
			for (int k = 0; k < doorItems.Length; k++)
			{
				world.SetBlock(x0 + k, y, z0 - 2, Blocks.DoorState(doorItems[k], 0, 0, k % 2 == 1, 0));
				world.SetBlock(x0 + k, y + 1, z0 - 2, Blocks.DoorState(doorItems[k], 0, 0, k % 2 == 1, 1));
			}
			world.SetBlock(x0 + 6, y, z0 + 3, D(0, 0, true, 0)); world.SetBlock(x0 + 6, y + 1, z0 + 3, D(0, 0, true, 1));
			world.SetBlock(x0 + 7, y, z0 + 3, D(0, 1, true, 0)); world.SetBlock(x0 + 7, y + 1, z0 + 3, D(0, 1, true, 1));
			inv.SetSlot(inv.Selected, cedar);
			player.Pos = new Vector3(x0 + 8f, y + 0.6f, z0 + 11f); player.Flying = true; player.Yaw = 0; player.Pitch = -0.12f;
			if (args.FirstOrDefault(a => a.StartsWith("--doorview=")) is { } dv)   // (another view: dx,dy,dz,yaw,pitch from the first door's spot)
			{
				var v = dv[11..].Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
				player.Pos = new Vector3(x0 + v[0], y + v[1], z0 + v[2]); player.Yaw = v[3]; player.Pitch = v[4];
			}
		}
		if (args.Contains("--stagetest") && !stageTested && !StageBreakingOn && !menuOpen && inv.Survival && shotTimer > 0.3f)
		{   // (test, survival, stage breaking held back: everything breaks as in Beta 0.1.2.0, and new worlds are land 1)
			stageTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int Tool(string kind, string mat) => Blocks.Tools.First(t => Blocks.B[t].Tool == kind && Blocks.B[t].Material == mat);
			int x0 = (int)player.Pos.X + 3, z0 = (int)player.Pos.Z + 3, y = world.Heights[z0 * World.WX + x0] + 1;
			for (int dx = -1; dx < 14; dx++) for (int dz = -1; dz < 8; dz++) { world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Stone); for (int u = 0; u < 9; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0); }
			void Grow(int x, int yy, int z, int b) { world.SetBlock(x, yy, z, b); world.Edits.Remove(World.Idx(x, yy, z)); }   // (as if the world grew it)
			void Aim(int x, int yy, int z) { player.Pos = new Vector3(x + 0.5f, yy + 1.2f, z + 0.5f); player.Flying = true; player.Pitch = -MathF.PI / 2 + 0.001f; player.Yaw = 0; }
			void Hold(int id) { inv.SetSlot(0, id); SelectSlot(0); for (int k = Inventory.Tool0; k < Inventory.Off0; k++) inv.SetSlot(k, 0); }
			void AimSide(int x, int yy, int z) { player.Pos = new Vector3(x + 0.5f, yy - 1, z + 1.8f); player.Flying = true; player.Pitch = 0; player.Yaw = 0; }   // (from the side, level with it)
			int Swings(int x, int yy, int z, int max = 40, bool side = false) { int b = world.GetBlock(x, yy, z), n = 0; if (side) AimSide(x, yy, z); else Aim(x, yy, z); while (world.GetBlock(x, yy, z) == b && n < max) { inv.Act = -1; Break(); n++; } return n; }
			int Dropped(int id) => drops.All.Where(d => d.Id == id).Count();
			void Clear() { foreach (var d in drops.All.ToArray()) drops.Remove(d); }
			Grow(x0, y, z0, Blocks.Stone); Hold(0); Aim(x0, y, z0); Break();
			Check("OFF: Stone by hand needs a pickaxe", world.GetBlock(x0, y, z0) == Blocks.Stone);
			Clear(); Hold(Tool("pickaxe", "stone"));
			Check("OFF: Stone: 1 click with a stone pickaxe, 1 Cobblestone", Swings(x0, y, z0) == 1 && Dropped(8) == 1);
			Grow(x0 + 1, y, z0, Blocks.Strata + 6);
			Check("OFF: layer-8 Stone is too deep for a stone pickaxe", Swings(x0 + 1, y, z0, 3) == 3 && world.GetBlock(x0 + 1, y, z0) == Blocks.Strata + 6);
			Grow(x0 + 3, y, z0, Blocks.IronOre); Hold(Tool("pickaxe", "iron")); Clear();
			Check("OFF: Iron Ore: 1 click, 1 Chunk", Swings(x0 + 3, y, z0) == 1 && Dropped(Blocks.Chunk["iron"]) == 1);
			int deep = Blocks.DeepOreOf(1, 8); Grow(x0 + 4, y, z0, deep);
			Check("OFF: layer-8 Iron Ore is too deep for an iron pickaxe", Swings(x0 + 4, y, z0, 3) == 3 && world.GetBlock(x0 + 4, y, z0) == deep);
			Hold(Tool("pickaxe", "steel")); Clear();
			Check("OFF: layer-8 Iron Ore with a steel pickaxe: 1 click, 1 Chunk", Swings(x0 + 4, y, z0) == 1 && Dropped(Blocks.Chunk["iron"]) == 1);
			Grow(x0 + 9, y, z0, Blocks.Sandstone); Hold(0); Clear(); Aim(x0 + 9, y, z0); Break();
			Check("OFF: Sandstone by hand needs a pickaxe (as in desktop's Beta 0.1.2.0)", world.GetBlock(x0 + 9, y, z0) == Blocks.Sandstone);
			Hold(Tool("pickaxe", "stone"));
			Check("OFF: Sandstone: 1 click with a pickaxe, gives itself (the drop fix stays)", Swings(x0 + 9, y, z0) == 1 && Dropped(Blocks.Sandstone) == 1);
			Grow(x0, y, z0 + 2, Blocks.Log); Hold(0);
			Check("OFF: a log by hand needs an axe", Swings(x0, y, z0 + 2, 3) == 3 && world.GetBlock(x0, y, z0 + 2) == Blocks.Log);
			void Tree(int tx, int tz) { for (int u = 0; u < 4; u++) Grow(tx, y + u, tz, Blocks.Log); for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++) for (int u = 3; u <= 4; u++) if (world.GetBlock(tx + dx, y + u, tz + dz) == 0) Grow(tx + dx, y + u, tz + dz, Blocks.Leaves); }
			Tree(x0 + 5, z0 + 5); Hold(Tool("axe", "steel")); Clear();
			int clicks = Swings(x0 + 5, y, z0 + 5, side: true);
			if (!TreeFellingOn) Check("OFF: a tree's bottom log: 1 click, and the tree stays up", clicks == 1 && world.GetBlock(x0 + 5, y + 1, z0 + 5) == Blocks.Log && Dropped(Blocks.Log) == 1);
			else
			{   // felling on its own (stage breaking off): an axe breaks the bottom log in one click, as always, and the tree comes down
				Check($"FELLING ALONE: a tree's bottom log, 1 click with an axe, fells the tree ({Dropped(Blocks.Log)} logs)", clicks == 1 && Enumerable.Range(0, 6).All(u => world.GetBlock(x0 + 5, y + u, z0 + 5) == 0) && world.GetBlock(x0 + 4, y + 3, z0 + 5) == 0 && Dropped(Blocks.Log) == 4);
				Tree(x0 + 9, z0 + 5); Swings(x0 + 9, y + 1, z0 + 5, side: true);
				Check("FELLING ALONE: breaking a middle log fells nothing", world.GetBlock(x0 + 9, y + 2, z0 + 5) == Blocks.Log && world.GetBlock(x0 + 9, y, z0 + 5) == Blocks.Log);
				Tree(x0 + 1, z0 + 5); world.SetBlock(x0 + 1, y + 4, z0 + 5, Blocks.Log);   // (a log put down on top)
				Swings(x0 + 1, y, z0 + 5, side: true);
				Check("FELLING ALONE: a put-down log touching a tree stays up", world.GetBlock(x0 + 1, y + 4, z0 + 5) == Blocks.Log && world.GetBlock(x0 + 1, y + 2, z0 + 5) == 0);
			}
			Check("OFF: nothing half broken, no cracks", stages.Count == 0);
			Check($"new worlds are land {World.NewLand} (this one: {current.Land}); staircases only in lands 5 to 8 ({world.Gen.StairSpots.Count} spots)", World.NewLand == (World.RockBlobsOn && WorldGen.EerieOn ? (World.OreSpreadOn ? 8 : 7) : (World.OreSpreadOn ? 4 : 3) + (WorldGen.EerieOn ? 2 : 0)) && current.Land == World.NewLand
				&& (world.Gen.StairSpots.Count > 0) == (world.Gen.HasStairs && current.Gen == 3));
		}
		if (args.Contains("--stagetest") && !stageTested && !menuOpen && inv.Survival && shotTimer > 0.3f)
		{   // (test, survival: stage breaking of Stone, ores and logs, natural or put down, and tree felling)
			stageTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int Tool(string kind, string mat) => Blocks.Tools.First(t => Blocks.B[t].Tool == kind && Blocks.B[t].Material == mat);
			int x0 = (int)player.Pos.X + 3, z0 = (int)player.Pos.Z + 3, y = world.Heights[z0 * World.WX + x0] + 1;
			for (int dx = -1; dx < 14; dx++) for (int dz = -1; dz < 8; dz++) { world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Stone); for (int u = 0; u < 9; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0); }
			void Grow(int x, int yy, int z, int b) { world.SetBlock(x, yy, z, b); world.Edits.Remove(World.Idx(x, yy, z)); }   // (as if the world grew it)
			void Aim(int x, int yy, int z) { player.Pos = new Vector3(x + 0.5f, yy + 1.2f, z + 0.5f); player.Flying = true; player.Pitch = -MathF.PI / 2 + 0.001f; player.Yaw = 0; }
			void Hold(int id) { inv.SetSlot(0, id); SelectSlot(0); for (int k = Inventory.Tool0; k < Inventory.Off0; k++) inv.SetSlot(k, 0); }
			void AimSide(int x, int yy, int z) { player.Pos = new Vector3(x + 0.5f, yy - 1, z + 1.8f); player.Flying = true; player.Pitch = 0; player.Yaw = 0; }   // (from the side, level with it)
			int Swings(int x, int yy, int z, int max = 40, bool side = false) { int b = world.GetBlock(x, yy, z), n = 0; if (side) AimSide(x, yy, z); else Aim(x, yy, z); while (world.GetBlock(x, yy, z) == b && n < max) { inv.Act = -1; Break(); n++; } return n; }
			int Dropped(int id) => drops.All.Where(d => d.Id == id).Count();
			void Clear() { foreach (var d in drops.All.ToArray()) drops.Remove(d); }
			// Stone
			Grow(x0, y, z0, Blocks.Stone); Hold(0); Aim(x0, y, z0); Break(); Break();
			Check("natural Stone by hand: nothing happens", world.GetBlock(x0, y, z0) == Blocks.Stone);
			Clear(); Hold(Tool("pickaxe", "stone"));
			Check("natural Stone: 4 swings with a stone pickaxe, 1 Cobblestone", Swings(x0, y, z0) == 4 && Dropped(8) == 1);
			Grow(x0 + 1, y, z0, Blocks.Strata + 6); Clear();
			Check("natural layer-8 Stone: 32 swings with a stone pickaxe (no longer too deep)", Swings(x0 + 1, y, z0) == 32 && Dropped(Blocks.DeepCobble + 6) == 1);
			Grow(x0 + 2, y, z0, Blocks.Strata + 6); Hold(Tool("pickaxe", "steel"));
			Check("layer-8 Stone: 1 swing with a steel pickaxe", Swings(x0 + 2, y, z0) == 1);
			// ores
			Grow(x0 + 3, y, z0, Blocks.IronOre); Hold(Tool("pickaxe", "iron")); Clear();
			Check("Iron Ore: 1 swing with an iron pickaxe, 1 Chunk", Swings(x0 + 3, y, z0) == 1 && Dropped(Blocks.Chunk["iron"]) == 1);
			int deep = Blocks.DeepOreOf(1, 8); Grow(x0 + 4, y, z0, deep); Clear();
			Check($"layer-8 Iron Ore ({deep}): 8 swings with an iron pickaxe, 8 Chunks", deep == 483 && Swings(x0 + 4, y, z0) == 8 && Dropped(Blocks.Chunk["iron"]) == 8);
			Grow(x0 + 5, y, z0, deep); Hold(Tool("pickaxe", "stone"));
			Check("layer-8 Iron Ore: a stone pickaxe can't", Swings(x0 + 5, y, z0, 5) == 5 && world.GetBlock(x0 + 5, y, z0) == deep);
			// put down: the old way
			world.SetBlock(x0 + 6, y, z0, Blocks.Stone); Clear();
			Check("put-down Stone: 1 click", Swings(x0 + 6, y, z0) == 1 && Dropped(8) == 1);
			world.SetBlock(x0 + 7, y, z0, Blocks.Strata + 6);
			Check("put-down layer-8 Stone: too deep for a stone pickaxe", Swings(x0 + 7, y, z0, 3) == 3 && world.GetBlock(x0 + 7, y, z0) == Blocks.Strata + 6);
			world.SetBlock(x0 + 8, y, z0, deep); Hold(Tool("pickaxe", "steel")); Clear();
			Check("put-down deep ore: 1 click, 1 Chunk", Swings(x0 + 8, y, z0) == 1 && Dropped(Blocks.Chunk["iron"]) == 1);
			Grow(x0 + 9, y, z0, Blocks.Sandstone); Hold(Tool("pickaxe", "stone")); Clear();
			Check("natural Sandstone: 4 swings, gives itself", Swings(x0 + 9, y, z0) == 4 && Dropped(Blocks.Sandstone) == 1);
			Grow(x0 + 10, y, z0, 65);
			Check("natural Terracotta: 4 swings", Swings(x0 + 10, y, z0) == 4);
			// logs
			Grow(x0, y, z0 + 2, Blocks.Log); Hold(0); Clear();
			Check("a natural log: 8 swings by hand, gives the log", Swings(x0, y, z0 + 2) == 8 && Dropped(Blocks.Log) == 1);
			Grow(x0 + 1, y, z0 + 2, Blocks.Log); Hold(Tool("axe", "stone"));
			Check("a natural log: 2 swings with a stone axe", Swings(x0 + 1, y, z0 + 2) == 2);
			world.SetBlock(x0 + 2, y, z0 + 2, Blocks.Log); Hold(0);
			Check("a put-down log by hand: needs an axe", Swings(x0 + 2, y, z0 + 2, 3) == 3 && world.GetBlock(x0 + 2, y, z0 + 2) == Blocks.Log);
			// a tree: trunk of 4 with a crown of leaves; breaking its bottom log fells it all
			void Tree(int tx, int tz) { for (int u = 0; u < 4; u++) Grow(tx, y + u, tz, Blocks.Log); for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++) for (int u = 3; u <= 4; u++) if (world.GetBlock(tx + dx, y + u, tz + dz) == 0) Grow(tx + dx, y + u, tz + dz, Blocks.Leaves); }
			Tree(x0 + 5, z0 + 5); Hold(Tool("axe", "steel")); Clear(); felled = 0;
			Swings(x0 + 5, y, z0 + 5, side: true);
			bool gone = Enumerable.Range(0, 6).All(u => world.GetBlock(x0 + 5, y + u, z0 + 5) == 0 && world.GetBlock(x0 + 4, y + 3, z0 + 5) == 0);
			if (!TreeFellingOn) Check("STAGES ALONE: a tree's bottom log comes out, the tree stays up", world.GetBlock(x0 + 5, y, z0 + 5) == 0 && world.GetBlock(x0 + 5, y + 1, z0 + 5) == Blocks.Log);
			else {
			Check($"breaking a tree's bottom log fells the tree ({felled} more blocks, {Dropped(Blocks.Log)} logs)", gone && Dropped(Blocks.Log) == 4);
			Tree(x0 + 9, z0 + 5); Swings(x0 + 9, y + 1, z0 + 5, side: true);
			Check("breaking a middle log fells nothing", world.GetBlock(x0 + 9, y + 2, z0 + 5) == Blocks.Log && world.GetBlock(x0 + 9, y, z0 + 5) == Blocks.Log);
			for (int u = 0; u < 3; u++) Grow(x0 + 12, y + u, z0 + 5, Blocks.Log);
			Swings(x0 + 12, y, z0 + 5, side: true);
			Check("a pile of logs with no leaves stays up", world.GetBlock(x0 + 12, y + 1, z0 + 5) == Blocks.Log);
			Tree(x0 + 1, z0 + 5); world.SetBlock(x0 + 1, y + 4, z0 + 5, Blocks.Log);   // (a log put down on top)
			Swings(x0 + 1, y, z0 + 5, side: true);
			Check("a put-down log touching a tree stays up", world.GetBlock(x0 + 1, y + 4, z0 + 5) == Blocks.Log && world.GetBlock(x0 + 1, y + 2, z0 + 5) == 0);
			}   // (felling on)
			Check($"new worlds are land {World.NewLand} (this one: {current.Land}); staircases only in lands 5 to 8 ({world.Gen.StairSpots.Count} spots)", World.NewLand == (World.RockBlobsOn && WorldGen.EerieOn ? (World.OreSpreadOn ? 8 : 7) : (World.OreSpreadOn ? 4 : 3) + (WorldGen.EerieOn ? 2 : 0)) && current.Land == World.NewLand
				&& (world.Gen.StairSpots.Count > 0) == (world.Gen.HasStairs && current.Gen == 3));
			// half broken, to look at
			Grow(x0 + 3, y, z0 + 4, Blocks.Stone); Hold(Tool("pickaxe", "stone")); Aim(x0 + 3, y, z0 + 4); Break(); Break();
			Grow(x0 + 4, y, z0 + 4, Blocks.Log); Hold(0); Aim(x0 + 4, y, z0 + 4); for (int k = 0; k < 7; k++) Break();
			Check("half-broken blocks stay, with their progress", stages.Count == 2);
			player.Pos = new Vector3(x0 + 3.5f, y + 1, z0 + 1.2f); player.Pitch = -0.45f; player.Yaw = MathF.PI;
		}
		if (args.Contains("--necktest") && !neckTested && !menuOpen && shotTimer > 0.3f)
		{   // (test: a head turns from its "head" group's pivot point, or from the middle of its bottom if the group has none, as agreed with the web version)
			neckTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			var keep = models["cow"]; var P = player.Pos;
			// textures' longer-legged cow and calf (their body, neck, head and snout), with the neck in the head group (in a group inside
			// it), and the head group's pivot at the neck's top back edge
			string Model(string neckName, bool calf, bool origin = true) => (calf ? """
				{ "elements": [ { "name": "body", "from": [5, 5, 3], "to": [11, 10, 13] }, { "name": "NECK", "from": [6, 8, 0], "to": [10, 12, 4] },
				{ "name": "head", "from": [5, 9, -4], "to": [11, 14, 0] }, { "name": "snout", "from": [6, 9, -5], "to": [10, 11, -4] },
				{ "name": "leg_a", "from": [5, 0, 4], "to": [7, 5, 6] }, { "name": "leg_b", "from": [9, 0, 4], "to": [11, 5, 6] },
				{ "name": "leg_c", "from": [5, 0, 10], "to": [7, 5, 12] }, { "name": "leg_d", "from": [9, 0, 10], "to": [11, 5, 12] } ],
				"groups": [ { "name": "body", "origin": [8, 5, 8], "children": [0] },
				{ "name": "Head", "origin": [8, 12, 4], "children": [ { "name": "throat", "origin": [8, 12, 4], "children": [1] }, 2, 3 ] },
				{ "name": "legs", "origin": [8, 5, 8], "children": [4, 5, 6, 7] } ] }
				""" : """
				{ "elements": [ { "name": "body", "from": [3, 6, 1], "to": [13, 14, 14] }, { "name": "NECK", "from": [6, 9, -3], "to": [10, 14, 2] },
				{ "name": "head", "from": [5, 9, -8], "to": [11, 15, -3] }, { "name": "snout", "from": [6, 9, -10], "to": [10, 12, -8] },
				{ "name": "leg_a", "from": [3, 0, 2], "to": [6, 6, 5] }, { "name": "leg_b", "from": [10, 0, 2], "to": [13, 6, 5] },
				{ "name": "leg_c", "from": [3, 0, 10], "to": [6, 6, 13] }, { "name": "leg_d", "from": [10, 0, 10], "to": [13, 6, 13] } ],
				"groups": [ { "name": "body", "origin": [8, 6, 8], "children": [0] },
				{ "name": "Head", "origin": [8, 14, 2], "children": [ { "name": "throat", "origin": [8, 14, 2], "children": [1] }, 2, 3 ] },
				{ "name": "legs", "origin": [8, 6, 8], "children": [4, 5, 6, 7] } ] }
				""").Replace("NECK", neckName).Replace(origin ? "\u0000" : "\"name\": \"Head\", \"origin\": [8, 14, 2],", "\"name\": \"Head\",").Replace(origin ? "\u0000" : "\"name\": \"Head\", \"origin\": [8, 12, 4],", "\"name\": \"Head\",");
			Mob Build(string json) { models["cow"] = (System.Text.Json.JsonDocument.Parse(json).RootElement.Clone(), "Cow"); return SpawnMob("cow", P.X, P.Y, P.Z + 6); }
			// each head-group part with the head turned by `a` (grazing is -0.85, wobbling to -0.90): its middle, lowest and highest point (model px)
			(Vector3 mid, float low, float high)[] Parts(Mob m, float a)
			{
				m.Head.Rotation = new Vector3(a, 0, 0);
				var outp = m.Head.GetChildren().OfType<MeshInstance3D>().Select(mi =>
				{
					var bb = mi.Mesh.GetAabb(); float low = 1e9f, high = -1e9f;
					for (int k = 0; k < 8; k++) { float yy = (m.Head.Transform * (mi.Position + bb.GetEndpoint(k))).Y * 16; low = Math.Min(low, yy); high = Math.Max(high, yy); }
					return (m.Head.Transform * mi.Position * 16, low, high);
				}).ToArray();
				m.Head.Rotation = Vector3.Zero;
				return outp;
			}
			foreach (var (calf, o, nose85, nose90, neckTop) in new[] { (false, new Vector3(8, 14, 2), 1.68f, 1.49f, 14f), (true, new Vector3(8, 12, 4), 3.26f, 3.09f, 12f) })
			{
				string who = calf ? "calf" : "cow";
				var m = Build(Model("Neck", calf));
				Check($"{who}: with a neck in its head group, the head turns from the group's pivot ({m.Head.Position * 16 + new Vector3(m.Cx, 0, m.Cz)})", (m.Head.Position * 16 + new Vector3(m.Cx, 0, m.Cz) - o).Length() < 1e-3f);
				var rest = Parts(m, 0); var g85 = Parts(m, -0.85f); var g90 = Parts(m, -0.90f);
				var restMids = rest.Select(r => r.mid + new Vector3(m.Cx, 0, m.Cz)).ToArray();
				var want = calf ? new[] { new Vector3(8, 11.5f, -2), new Vector3(8, 10, 2), new Vector3(8, 10, -4.5f) } : new[] { new Vector3(8, 12, -5.5f), new Vector3(8, 11.5f, -0.5f), new Vector3(8, 10.5f, -9) };
				Check($"{who}: at rest, the head, neck and snout are where the model puts them", rest.Length == 3 && want.All(w => restMids.Any(r => (r - w).Length() < 1e-3f)));
				int ni = Array.FindIndex(restMids, r => (r - want[1]).Length() < 1e-3f), si = Array.FindIndex(restMids, r => (r - want[2]).Length() < 1e-3f);
				Check($"{who}: grazing, the nose comes down to {g85[si].low:0.00} px up ({g90[si].low:0.00} at the wobble's -0.90; web {nose85:0.00} / {nose90:0.00}), and the neck's top stays at {g85[ni].high:0.00} (was {rest[ni].high:0.00})",
					Math.Abs(g85[si].low - nose85) < 0.01f && Math.Abs(g90[si].low - nose90) < 0.01f && g85[ni].high <= neckTop + 0.01f && Math.Abs(rest[ni].high - neckTop) < 0.01f);
				RemoveMob(m);
				var plain = Build(Model("dewlap", calf));   // (the same, with that part not called neck: still the group's pivot point)
				Check($"{who}: with no neck, the head turns from its group's pivot point too", (plain.Head.Position * 16 + new Vector3(plain.Cx, 0, plain.Cz) - o).Length() < 1e-3f);
				RemoveMob(plain);
				var old = Build(Model("Neck", calf, false));   // (the head group with no pivot point: the middle of the head's bottom, as before)
				var hf = calf ? new Vector3(8, 9, -2) : new Vector3(8, 9, -5.5f);
				Check($"{who}: a head group with no pivot point: the head turns from the middle of its bottom", (old.Head.Position * 16 + new Vector3(old.Cx, 0, old.Cz) - hf).Length() < 1e-3f);
				var o85 = Parts(old, -0.85f);
				GD.Print($"  ({who} without the rule: the nose stops {o85.Min(q => q.low):0.00} px up and the neck's top swings up to {o85.Max(q => q.high):0.00})");
				RemoveMob(old);
			}
			// every installed model: its head turns from its head group's origin (or, with none, the middle of its bottom). Which ones
			// that moves from the middle of the head's bottom (the old rule), and the gap behind a grazing head (its back top corner
			// going forward at -0.85, model px; web's numbers)
			var same = new List<string>(); var moved = new List<string>(); var necks = new List<string>(); var gaps = new List<string>(); var leaned = new List<string>();
			float[] NeckOrigin(System.Text.Json.JsonElement json)
			{
				float[] Find(System.Text.Json.JsonElement list)
				{
					if (list.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
					foreach (var g in list.EnumerateArray().Where(c => c.ValueKind == System.Text.Json.JsonValueKind.Object))
					{
						if (g.TryGetProperty("name", out var n) && string.Equals(n.GetString(), "head", StringComparison.OrdinalIgnoreCase) && g.TryGetProperty("origin", out var o)) return Vec(o);
						if (g.TryGetProperty("children", out var ch) && Find(ch) is { } f) return f;
					}
					return null;
				}
				return json.TryGetProperty("groups", out var gr) ? Find(gr) : null;
			}
			foreach (var (name, md) in models.Where(kv => kv.Key != "cow").Append(new KeyValuePair<string, (System.Text.Json.JsonElement json, string folder)>("cow", keep)).ToArray())
			{
				var hd = md.json.GetProperty("elements").EnumerateArray().FirstOrDefault(e => e.TryGetProperty("name", out var n) && n.GetString() == "head");
				if (hd.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
				models["cow"] = md; var mob = SpawnMob("cow", P.X, P.Y, P.Z + 6);
				if (mob.Head != null)
				{
					var f = Vec(hd.GetProperty("from")); var t = Vec(hd.GetProperty("to"));
					var no = NeckOrigin(md.json);
					var bottom = new Vector3(((f[0] + t[0]) / 2 - mob.Cx) / 16, f[1] / 16, ((f[2] + t[2]) / 2 - mob.Cz) / 16);
					var w = no != null ? new Vector3((no[0] - mob.Cx) / 16, no[1] / 16, (no[2] - mob.Cz) / 16) : bottom;
					((mob.Head.Position - w).Length() < 1e-4f ? same : moved).Add(name);
					if ((w - bottom).Length() > 1e-4f) necks.Add(name);
					// the gap: the head's back top corner (in the body's frame, model px) before and after turning down by 0.85
					var corner = new Vector3((f[0] + t[0]) / 2 - mob.Cx, t[1], t[2] - mob.Cz) / 16 - mob.Head.Position;
					float gap = (corner.Z - (Basis.FromEuler(new Vector3(-0.85f, 0, 0)) * corner).Z) * 16;
					if (MobTypes.ContainsKey(name) || name.EndsWith("baby")) gaps.Add($"{name} {gap:0.00}");
					// grazing in full: only the head goes down, the body stays as it stands (no lean: the user's call, 2026-11-25)
					mob.Happy = 0; mob.Grazing = 1000; mob.GrazeAnim = 1; CowShow(mob, 0);
					if (mob.Figure.Position.Length() > 1e-6f || mob.Figure.Rotation.Length() > 1e-6f || mob.Head.Rotation.X > -0.8f) leaned.Add(name);
				}
				RemoveMob(mob);
			}
			models["cow"] = keep;
			Check($"every installed model's head turns from its group's pivot point, or its bottom middle ({same.Count}: {string.Join(", ", same)}){(moved.Count > 0 ? "; wrong: " + string.Join(", ", moved) : "")}", moved.Count == 0 && same.Contains("cow") && same.Contains("zombie"));
			Check($"people and monsters turn their heads as before: their pivot points are the heads' bottom middles (moved from there: {string.Join(", ", necks)})", !necks.Intersect(new[] { "player", "skeleton", "zombie", "gloomling" }).Any());
			GD.Print($"  (the gap behind a grazing head, model px: {string.Join(", ", gaps)})");
			Check($"grazing, only the head goes down: no body leans, as in the web version{(leaned.Count > 0 ? " (wrong: " + string.Join(", ", leaned) + ")" : "")}", leaned.Count == 0);
		}
		if (args.Contains("--animalcheck") && !animalChecked && !menuOpen && shotTimer > 0.3f)
		{   // (test: the six animals' models in game: their parts, size and the neck rule; each walks; spawning across a Biomes world)
			animalChecked = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			string[] kinds = { "chicken", "duck", "deer", "goat", "boar", "bear" };
			var P = player.Pos;
			int x0 = (int)P.X - 12, z0 = (int)P.Z + 6, y = world.Heights[z0 * World.WX + x0] + 1;
			for (int dx = -2; dx < 30; dx++) for (int dz = -2; dz < 30; dz++) { world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Stone); for (int u = 0; u < 8; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0); }
			var looks = new List<string>(); bool allBuilt = true;
			foreach (var kind in kinds) foreach (bool young in new[] { false, true })
			{
				var m = SpawnAnimal(kind, x0 + 4, y, z0 + 4, young ? 100 : 0);
				int legs = m.Legs.Count; bool ok = m.Head != null && legs == (kind is "chicken" or "duck" ? 2 : 4) && m.Height > 0.2f && m.Half > 0.05f;
				if (!ok) allBuilt = false;
				looks.Add($"{(young ? kind + " young" : kind)} {legs} legs {m.Height:0.00} tall{(m.Head == null ? " NO HEAD" : "")}");
				RemoveMob(m);
			}
			Check($"each animal and its young is built from its model: a head, legs that swing (2 for birds, 4 for the rest) ({string.Join("; ", looks)})", allBuilt);
			// walking: each grown one let loose on open ground for 30 seconds goes somewhere, its legs swinging, never into a block
			var walked = new List<string>(); bool allWalk = true;
			foreach (var kind in kinds)
			{
				var m = SpawnAnimal(kind, x0 + 14.5f, y, z0 + 14.5f);
				float start = m.X, startZ = m.Z, swing = 0, far = 0; bool inBlock = false;
				player.Pos = new Vector3(x0 + 14.5f, y + 40, z0 + 14.5f); player.Flying = true;   // (well away: a deer would run off)
				for (int s = 0; s < 600; s++)
				{
					m.Timer = Math.Min(m.Timer, 0.5f); UpdateMobs(0.05f);
					foreach (var l in m.Legs) swing = Math.Max(swing, Math.Abs(l.Pivot.Rotation.X));
					far = Math.Max(far, MathF.Sqrt((m.X - start) * (m.X - start) + (m.Z - startZ) * (m.Z - startZ)));
					if (MobBlocked(m, m.X, m.Y + 0.01f, m.Z)) inBlock = true;
				}
				bool ok = far > 1.5f && swing > 0.15f && !inBlock;
				if (!ok) allWalk = false;
				walked.Add($"{kind} {far:0.0} blocks, legs {swing:0.00}{(inBlock ? " IN A BLOCK" : "")}");
				RemoveMob(m);
			}
			Check($"each grown animal walks about, legs swinging, never into a block ({string.Join("; ", walked)})", allWalk);
			// the Deer and the Duck turn their heads from where the neck meets the body (the neck rule); the others from the head's bottom
			var necked = kinds.Concat(kinds.Select(k => k + "baby")).Where(k => models.TryGetValue(k, out var md) && md.json.GetRawText().Contains("\"neck\"")).ToArray();
			Check($"the grown Deer and Duck have necks in their heads' groups ({string.Join(", ", necked)})", necked.OrderBy(k => k).SequenceEqual(new[] { "deer", "duck" }));
			// spawning: from spots all over this Biomes world, each kind starts somewhere, only in its own biomes
			if (current?.Gen == 3)
			{
				foreach (var m in mobList.ToArray()) RemoveMob(m);
				var seen = kinds.ToDictionary(k => k, k => 0); var wrong = new List<string>(); int overBorder = 0;
				var r = new Random(7);
				for (int n = 0; n < 1500; n++)
				{
					int before = mobList.Count;
					player.Pos = new Vector3(40 + r.Next(World.WX - 80), 80, 40 + r.Next(World.WZ - 80));
					animalSpawnIn = 0; SpawnAnimals();
					foreach (var m in mobList.Skip(before))
					{
						seen[m.Type]++;
						// (a group starts on one spot in the kind's biome and spreads up to 2.5 blocks each way, so it may stand just over a border)
						int mx = (int)MathF.Floor(m.X), mz = (int)MathF.Floor(m.Z), bi = world.Gen.BiomeOf(mx, mz);
						bool near = false;
						for (int dz = -4; dz <= 4 && !near; dz++) for (int dx = -4; dx <= 4 && !near; dx++) near = AnimalBiomes[m.Type].Contains(world.Gen.BiomeOf(mx + dx, mz + dz));
						if (!AnimalBiomes[m.Type].Contains(bi)) { if (near) overBorder++; else wrong.Add($"{m.Type} in {WorldGen.BiomeNames[bi]}"); }
					}
					if (mobList.Count > 300) foreach (var m in mobList.ToArray()) RemoveMob(m);
				}
				Check($"spawning all over a Biomes world: every kind starts somewhere ({string.Join(", ", seen.Select(kv => $"{kv.Key} {kv.Value}"))})", seen.Values.All(v => v > 0));
				Check($"each only in its own biomes ({overBorder} of {seen.Values.Sum()} just over a border, within 4){(wrong.Count > 0 ? "; elsewhere: " + string.Join(", ", wrong.Distinct().Take(6)) : "")}", wrong.Count == 0);
				foreach (var m in mobList.ToArray()) RemoveMob(m);
			}
			else
			{
				animalSpawnIn = 0; SpawnAnimals();
				Check("not a Biomes world: no animals start", !mobList.Any(m => m.T.Animal));
			}
			player.Pos = P;
		}
		// (pictures: animals held still where --animalpic put them, standing or with heads down)
		if (picHold != null) foreach (var (m, at, yaw, graze) in picHold)
		{
			m.X = at.X; m.Y = at.Y; m.Z = at.Z; m.Yaw = yaw; m.Vx = m.Vz = 0; m.Walking = false; m.Flee = 0; m.Angry = 0; m.Chase = false; m.Timer = 1000;
			m.Grazing = graze ? 1000 : 0; m.Mate = null;
		}
		if (args.Contains("--animalpic") && picHold == null && !menuOpen && shotTimer > 0.3f)
		{   // (to look at: the six animals side on in a line, each grown one with its young in front; --graze: heads down)
			string[] kinds = { "chicken", "duck", "deer", "goat", "boar", "bear" };
			bool graze = args.Contains("--graze");
			int x0 = (int)player.Pos.X - 12, z0 = (int)player.Pos.Z - 8, y = world.Heights[z0 * World.WX + x0] + 1;
			for (int dx = -3; dx < 28; dx++) for (int dz = -3; dz < 16; dz++) { world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Grass); for (int u = 0; u < 8; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0); }
			picHold = new();
			for (int k = 0; k < 6; k++)
			{
				var at = new Vector3(x0 + 3.0f + k * 3.5f, y, z0 + 2.5f); var atYoung = new Vector3(x0 + 2.6f + k * 3.5f, y, z0 + 4.8f);
				picHold.Add((SpawnAnimal(kinds[k], at.X, at.Y, at.Z), at, MathF.PI / 2, graze));
				picHold.Add((SpawnAnimal(kinds[k], atYoung.X, atYoung.Y, atYoung.Z, 1000), atYoung, MathF.PI / 2, graze));
			}
			player.Pos = new Vector3(x0 + 11.6f, y + 1.9f, z0 + 13.8f); player.Flying = true; player.Yaw = 0; player.Pitch = -0.19f;
			inv.SetSlot(9, 0); SelectSlot(9); panoCapturing = true;   // (no hand in the way)
		}
		if (args.Contains("--cowpic") && !cowShown && !menuOpen && shotTimer > 0.3f && models.ContainsKey("cow"))
		{   // (to look at: a cow, a fat cow and a calf side on, in a line, each standing then grazing)
			cowShown = true;
			int x0 = (int)player.Pos.X - 10, z0 = (int)player.Pos.Z - 8, y = world.Heights[z0 * World.WX + x0] + 1;
			for (int dx = -3; dx < 24; dx++) for (int dz = -3; dz < 14; dz++) { world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Stone); for (int u = 0; u < 6; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0); }
			int[] variants = { 0, 0, 2, 2, 1, 1 };
			for (int k = 0; k < 6; k++)
			{
				var c = SpawnMob("cow", x0 + 2f + k * 3.5f, y, z0 + 2.5f, variants[k]);
				c.Yaw = MathF.PI / 2; c.Walking = false; c.Timer = 1000; c.Happy = 0; if (variants[k] == 1) c.Baby = 1000;
				if (k % 2 == 1) c.Grazing = 1000;
			}
			player.Pos = new Vector3(x0 + 10.75f, y + 2.6f, z0 + 11.5f); player.Flying = true; player.Yaw = 0; player.Pitch = -0.2f;
			inv.SetSlot(9, 0); SelectSlot(9); panoCapturing = true;   // (no hand in the way)
		}
		// (test, an older world given the staircases, in two runs of the pumpkin 1280 land-3 world in a test folder (--worlds=):
		// --oldstairtest=1 opens it for the first time in this version, its save holding a little hut near the spot at 1175, 299, then
		// builds beside the staircase at 647, 840 and saves; --oldstairtest=2 opens it again: nothing was decided again)
		if (args.FirstOrDefault(a => a.StartsWith("--oldstairtest=")) is { } ot && !osTested && ready && !menuOpen && shotTimer > 0.3f)
		{
			osTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			string run = ot[15..], dir = args.FirstOrDefault(a => a.StartsWith("--worlds="))?[9..] ?? "user://worlds";
			int n = World.WX / World.CS;
			var hut = new HashSet<int> { 18 * n + 73, 19 * n + 73 };   // (the hut: x 1179 to 1183, z 303 to 307)
			string Placed() => string.Join(" ", world.Gen.StairSpots.Where(o => o.Placed).Select(o => $"{o.X},{o.Y},{o.Z},{o.Dir},{o.N},{o.Mat}"));
			GD.Print($"OLDSTAIRS run {run}: land {current.Land}, os {current.Os}, skip [{string.Join(",", current.OsSkip ?? Array.Empty<int>())}], placed {Placed()}");
			Check($"an older world (land {current.Land}) grows the staircases now", current.Land == 3 && current.Os && world.Gen.Os && world.Gen.HasStairs);
			Check("the chunks that held changes when it was first opened are noted: just the hut's two", current.OsSkip != null && current.OsSkip.SequenceEqual(hut.OrderBy(c => c)));
			Check("no staircase near the hut: the spot at 1175, 299 was skipped", !world.Gen.StairSpots.Any(o => WorldGen.NearChunks(hut, o.X, o.Z)) && !world.Gen.StairSpots.Any(o => o.X == 1175 && o.Z == 299));
			var keep = world.Gen.StairSpots.FirstOrDefault(o => o.Placed && o.X == 647 && o.Z == 840);
			Check($"the other staircases stand, as in the world grown with nothing skipped ({Placed()})", keep != null && StairStanding(keep) && Placed() == "647,80,840,3,7,8 438,71,345,0,8,253 940,70,914,1,5,7" && world.Gen.StairSpots.Where(o => o.Placed).All(StairStanding));
			Check("the hut is still there", world.GetBlock(1179, 88, 303) == Blocks.Cobblestone && world.GetBlock(1181, 89, 305) == DataKeys.IdOf("oak_planks"));
			string list = FileAccess.GetFileAsString(dir + "/worlds.json");
			Check("the world's list keeps it", list.Contains("\"Os\":true") && list.Contains("\"OsSkip\":[" + string.Join(",", hut.OrderBy(c => c)) + "]"));
			if (run == "1")
			{   // build right beside the staircase at 647, 840, and save
				for (int y = keep.Y; y < keep.Y + 4; y++) world.SetBlock(650, y, 838, Blocks.Cobblestone);
				Check("built beside a staircase, then saved, the save keeping it too", SaveWorld(true) && FileAccess.GetFileAsString($"{dir}/{current.Id}.json") is { } sv && sv.Contains("\"Os\":true") && sv.Contains("\"OsSkip\":[" + string.Join(",", hut.OrderBy(c => c)) + "]"));
			}
			else
				Check("opened again after building beside it: the staircase at 647, 840 still stands, the new wall beside it, nothing decided again", world.GetBlock(650, keep.Y + 1, 838) == Blocks.Cobblestone && StairStanding(keep));
		}
		// (test, two games or two editions: --osnet prints which staircases this game grew and whether they stand, as the host and as
		// the friend (once welcomed, with the host's changed blocks in), so the two can be compared; the friend checks it was told)
		if (args.Contains("--osnet") && !osNetDone && ready && !menuOpen && (hosting || netClient && welcomed) && (bagNetT += dt) > 3)
		{
			osNetDone = true;
			string Placed() => string.Join(" ", world.Gen.StairSpots.Where(o => o.Placed).Select(o => $"{o.X},{o.Y},{o.Z},{o.Dir},{o.N},{o.Mat}{(StairStanding(o) ? "" : " (gone)")}"));
			GD.Print($"OSNET {(hosting ? "host" : "friend")}: land {current.Land}, os {current.Os}, skip [{string.Join(",", current.OsSkip ?? Array.Empty<int>())}], {world.Gen.StairSpots.Count} spots, placed {Placed()}");
			if (netClient) GD.Print((current.Os && world.Gen.Os && world.Gen.StairSpots.Any(o => o.Placed && StairStanding(o)) ? "PASS " : "FAIL ") + "friend: the host's older world grows its staircases here too, told in \"info\" before the world grew");
		}
		// (test, mixed editions: --mixtest, as the host or the friend. From the friend's arrival (the host: a friend joined; the
		// friend: welcomed): at 5 s prints LIVE (every block's full id, in index order, as the world stands: an FNV-1a fingerprint)
		// and the changed-block count, then makes this side's changes (the host: 30 at y 150, z 600, x 600-629, 5 and 2200 in turn,
		// and 7 at the far corner WX-1, WY-2, WZ-1; the friend: 30 at y 152, z 602, x 600-629, 3 and 3760 in turn); at 25 s prints
		// them again, so both editions' worlds can be compared. --mixlook: only the first reading, no changes: a friend joining late,
		// to see the whole welcome come in)
		if (args.Contains("--mixtest") && hosting && ready && !menuOpen && !mixClockGiven)
		{   // (the host holds a Clock from the start)
			mixClockGiven = true; inv.SetSlot(inv.Selected, Blocks.Clock); DrawHotbar();
		}
		if ((args.Contains("--mixtest") || args.Contains("--mixlook")) && ready && !menuOpen && (hosting && friends.Count > 0 || netClient && welcomed))
		{
			mixT += (float)dt;
			if (mixStep == 0 && mixT > 5 && args.Contains("--mixlook")) { mixStep = 2; MixLive(1); }
			else if (mixStep == 0 && mixT > 5)
			{
				mixStep = 1; MixLive(1); Mix8("A");
				// (gen 4: in the air over the start instead, EarthSpawn's block (sx, sz): the host's at y 400, z sz, x sx - 15 + k; the
				// friend's at y 402, z sz + 2)
				var (ex, ez) = world.Lazy ? EarthSpawn() : (615, 600);
				for (int k = 0; k < 30; k++)
					if (world.Lazy) { if (hosting) world.SetBlock(ex - 15 + k, 400, ez, k % 2 == 0 ? 5 : 2200); else world.SetBlock(ex - 15 + k, 402, ez + 2, k % 2 == 0 ? 3 : 3760); }
					else if (hosting) world.SetBlock(600 + k, 150, 600, k % 2 == 0 ? 5 : 2200); else world.SetBlock(600 + k, 152, 602, k % 2 == 0 ? 3 : 3760);
				if (hosting) world.SetBlock(World.WX - 1, World.WY - 2, World.WZ - 1, 7);
				GD.Print($"MIXTEST {(hosting ? "host" : "friend")}: changes made");
			}
			else if (mixStep == 1 && mixT > 25)
			{
				mixStep = 2;
				if (hosting) { sky.Clock += Nums.N.CLOCK.skip; GD.Print("MIX8 host: T (+300 to the clock)"); }   // (as the T key: the friend should follow)
				MixLive(2);
			}
			// (Beta 0.1.5.4, in a river: an Earthlike host's refill, then the third and fourth readings on both sides)
			else if (mixStep == 2 && hosting && world.Lazy && mixT > 26)
			{
				mixStep = 3; var (sx, sz) = EarthSpawn(); long best = long.MaxValue;
				for (int z = Math.Max(1, sz - 56); z <= Math.Min(World.WZ - 2, sz + 56); z++)
					for (int x = Math.Max(1, sx - 56); x <= Math.Min(World.WX - 2, sx + 56); x++)
						for (int y = World.WY - 2; y > 1; y--)
						{
							int b = world.GetBlock(x, y, z);
							if (!Blocks.IsRunningRiver(b)) continue;
							if (world.GetBlock(x + 1, y, z) != b || world.GetBlock(x - 1, y, z) != b || world.GetBlock(x, y, z + 1) != b || world.GetBlock(x, y, z - 1) != b || world.GetBlock(x, y - 1, z) != b) continue;
							long d = (long)(x - sx) * (x - sx) + (long)(z - sz) * (z - sz);
							if (d < best) { best = d; mixRefill = (x, y, z, b); }
							break;
						}
				if (mixRefill.id > 0) { world.SetBlock(mixRefill.x, mixRefill.y, mixRefill.z, Blocks.Stone); GD.Print($"MIXTEST host: Andesite put in the river at {mixRefill.x},{mixRefill.y},{mixRefill.z} (its water {mixRefill.id})"); }
				else GD.Print("MIXTEST host: no river spot for the refill");
			}
			else if (mixStep == 3 && mixT > 27)
			{
				mixStep = 4; if (mixRefill.id > 0) world.SetBlock(mixRefill.x, mixRefill.y, mixRefill.z, 0);
				GD.Print("MIXTEST host: the Andesite taken out");
			}
			else if (mixStep is 2 or 4 && (!hosting || !world.Lazy || mixStep == 4) && mixT > 40) { mixStep = 5; MixLive(3); if (mixRefill.id > 0) GD.Print($"MIXTEST host: the refilled spot holds {world.GetBlock(mixRefill.x, mixRefill.y, mixRefill.z)}"); }
			else if (mixStep == 5 && mixT > 55) { mixStep = 6; MixLive(4); Mix8("B"); }
		}
		// (test, two games: --bagnet --host holds a full Linen Bag and drops an empty and a full one; --bagnet --join=CODE holds a
		// full one too, checks the host's drops and hand show the right pictures, and is the picture; the host checks the friend's hand)
		if (args.Contains("--bagnet"))
		{
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int Bag = DataKeys.IdOf("linen_bag"); int sx = (int)spawnPoint.X, sz = (int)spawnPoint.Z;
			float Gy(int x, int z) => world.Heights[z * World.WX + x] + 1;
			var some = new Inventory.PackData { Id = new[] { Blocks.Dirt }, N = new[] { 10 }, W = new int[1] };
			foreach (var d in drops.All) d.Spin = 0;   // (held facing the friend, for the picture)
			if (hosting && !bagNetSet)
			{
				bagNetSet = true;
				player.Pos = new Vector3(sx + 0.5f, Gy(sx, sz - 4), sz - 3.5f); player.Vel = Vector3.Zero; player.Flying = true; player.Yaw = MathF.PI; player.Pitch = 0;
				inv.SetSlot(0, Bag, 1, 0, some); SelectSlot(0);
				var d1 = drops.Add(Bag, sx - 1.2f, Gy(sx - 2, sz - 2), sz - 1.5f); d1.V = Vector3.Zero; d1.Keep = true;
				var d2 = drops.Add(Bag, sx + 0.1f, Gy(sx, sz - 2), sz - 1.5f); d2.V = Vector3.Zero; d2.Keep = true; d2.Data = some;
				GD.Print("bagnet: host ready");
			}
			if (hosting && bagNetSet && !bagNetDone && others.Values.FirstOrDefault(o => o.Av != null && o.Held == Bag) is { } fr && (bagNetT += (float)dt) > 3)
			{
				bagNetDone = true;
				Check("host: the friend's hand shows a bag holding something with its full picture (from their \"hf\")", fr.HeldFull && fr.Av.HeldMesh?.MaterialOverride is ShaderMaterial m && (Texture2D)m.GetShaderParameter("tex") == Atlas.BagIcon(Bag, true));
			}
			if (netClient && welcomed && !bagNetSet)
			{
				bagNetSet = true;
				player.Pos = new Vector3(sx + 0.5f, Gy(sx, sz + 1), sz + 1.5f); player.Vel = Vector3.Zero; player.Flying = true; player.Yaw = 0; player.Pitch = -0.28f;
				inv.SetSlot(0, Bag, 1, 0, some); SelectSlot(0);
			}
			if (netClient && bagNetSet && !bagNetDone && others.TryGetValue("host", out var host) && host.Av != null && drops.All.Count(d => d.Id == Bag) >= 2 && (bagNetT += (float)dt) > 3)
			{
				bagNetDone = true;
				var bags = drops.All.Where(d => d.Id == Bag).OrderBy(d => d.P.X).ToList();
				Check($"friend: the host's two dropped bags, the empty one with its own picture and the one holding something with its full picture ({string.Join(", ", bags.Select(d => d.Full ? "full" : "empty"))})",
					bags.Count == 2 && !bags[0].Full && bags[1].Full && (Texture2D)bags[1].Mat.GetShaderParameter("tex") == Atlas.BagIcon(Bag, true) && (Texture2D)bags[0].Mat.GetShaderParameter("tex") == Atlas.IconTexture(Bag));
				Check("friend: the host's hand shows their bag holding something with its full picture", host.HeldFull && host.Av.HeldMesh?.MaterialOverride is ShaderMaterial m && (Texture2D)m.GetShaderParameter("tex") == Atlas.BagIcon(Bag, true));
				Check("friend: their own bag in the hotbar and in their hand shows full too", hotSlots[0].Pic.Texture == Atlas.BagIcon(Bag, true) && handBody?.HeldMesh?.MaterialOverride is ShaderMaterial hm && (Texture2D)hm.GetShaderParameter("tex") == Atlas.BagIcon(Bag, true));
				// (pictures, --mixdir: this view (the dropped bags, and the host's hand) now; then the --shot is a close-up of the host's hand,
				// the camera narrowed and turned to it, your own hand out of the way; you still hold your bag, for the host's check)
				string dir = args.FirstOrDefault(a => a.StartsWith("--mixdir="))?[9..];
				if (dir != null && GetViewport().GetTexture()?.GetImage() is { } im) im.SavePng(dir + "/case_drops.png");
				if (dir != null && host.Av.HeldMesh is { } hand)
				{
					var to = hand.GlobalPosition - cam.GlobalPosition;
					player.Yaw = MathF.Atan2(-to.X, -to.Z); player.Pitch = MathF.Atan2(to.Y, new Vector2(to.X, to.Z).Length());
					cam.Fov = 18; panoCapturing = true;
				}
			}
		}
		// (test, mixed editions: --bagmix --join=CODE joins another edition's host that holds a full Linen Bag and has dropped an empty
		// and a full one; it stands 5 in front of the host looking back, checks those pictures, then holds its own bag full, empty,
		// then nothing, printing the time of each change so the host's log can be checked against it; pictures go to --mixdir)
		if (args.Contains("--bagmix") && netClient && welcomed && others.TryGetValue("host", out var mh) && mh.Av != null)
		{
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			string Now() => System.DateTime.UtcNow.ToString("HH:mm:ss") + " UTC";
			int Bag = DataKeys.IdOf("linen_bag"); string dir = args.FirstOrDefault(a => a.StartsWith("--mixdir="))?[9..];
			var some = new Inventory.PackData { Id = new[] { Blocks.Dirt }, N = new[] { 10 }, W = new int[1] };
			float fyaw = mh.Yaw + MathF.PI;
			foreach (var d in drops.All) d.Spin = fyaw;   // (held facing you, for the picture)
			if (!bagNetSet)
			{
				bagNetSet = true;
				float fx = mh.X - 5 * MathF.Sin(mh.Yaw), fz = mh.Z - 5 * MathF.Cos(mh.Yaw);
				float gy = world.Heights[(int)fz * World.WX + (int)fx] + 1;
				player.Pos = new Vector3(fx, MathF.Max(mh.Y, gy), fz); player.Vel = Vector3.Zero; player.Flying = true; player.Yaw = fyaw; player.Pitch = -0.2f;
				inv.SetSlot(0, Bag, 1, 0, some); inv.SetSlot(1, 0); SelectSlot(0);
				GD.Print($"bagmix: host at {mh.X:0.0} {mh.Y:0.0} {mh.Z:0.0} yaw {mh.Yaw:0.00}; me at {fx:0.0} {player.Pos.Y:0.0} {fz:0.0}; holding a FULL bag from {Now()}");
			}
			float was = bagNetT; bagNetT += dt;
			bool At(float t) => was < t && bagNetT >= t;
			if (At(8))
			{
				var bags = drops.All.Where(d => d.Id == Bag).ToList();
				GD.Print("bagmix: dropped bags " + string.Join(", ", bags.Select(d => $"{(d.Full ? "full" : "empty")} at {d.P.X:0.0} {d.P.Y:0.0} {d.P.Z:0.0}")));
				Check("mixed friend: the host's two dropped bags, one empty with its own picture and one holding something with its full picture",
					bags.Count == 2 && bags.Count(d => d.Full) == 1 && bags.All(d => (Texture2D)d.Mat.GetShaderParameter("tex") == (d.Full ? Atlas.BagIcon(Bag, true) : Atlas.IconTexture(Bag))));
				Check("mixed friend: the host's hand shows their bag holding something with its full picture", mh.Held == Bag && mh.HeldFull && mh.Av.HeldMesh?.MaterialOverride is ShaderMaterial m && (Texture2D)m.GetShaderParameter("tex") == Atlas.BagIcon(Bag, true));
				if (dir != null && GetViewport().GetTexture()?.GetImage() is { } im) im.SavePng(dir + "/mix_full.png");
			}
			if (At(75)) { inv.SetSlot(0, Bag); SelectSlot(0); GD.Print($"bagmix: holding an EMPTY bag from {Now()}"); }
			if (At(85))
			{
				Check("mixed friend: my own emptied bag shows its empty picture in the hotbar and in my hand", hotSlots[0].Pic.Texture == Atlas.IconTexture(Bag) && handBody?.HeldMesh?.MaterialOverride is ShaderMaterial hm && (Texture2D)hm.GetShaderParameter("tex") == Atlas.IconTexture(Bag));
				if (dir != null && GetViewport().GetTexture()?.GetImage() is { } im) im.SavePng(dir + "/mix_empty.png");
			}
			if (At(140)) { SelectSlot(1); GD.Print($"bagmix: holding NOTHING from {Now()}"); }
		}
		if (bagShown && args.Contains("--hand")) foreach (var d in drops.All) d.Spin = 0.25f;   // (pictures: the dropped bags held facing you)
		if (args.Contains("--bagpic") && !bagShown && !menuOpen && inv.Survival && shotTimer > 0.3f)
		{   // (to look at: Linen Bags empty, part full and full in the inventory, one's tooltip showing; --hand: one in your hand, two dropped)
			bagShown = true;
			int Bag = DataKeys.IdOf("linen_bag"), Dirt = Blocks.Dirt, Sand = Blocks.Sand;
			for (int i = 0; i < Inventory.Armor0; i++) inv.SetSlot(i, 0);
			var part = new Inventory.PackData { Id = new[] { Dirt, Sand, Blocks.Cobblestone }, N = new[] { 12, 6, 8 }, W = new int[3] };
			var full = new Inventory.PackData { Id = new[] { DataKeys.IdOf("stone_pickaxe") }, N = new[] { 1 }, W = new[] { 20 } };
			inv.SetSlot(0, Bag); inv.SetSlot(1, Bag, 1, 0, part); inv.SetSlot(2, Bag, 1, 0, full);
			inv.SetSlot(3, Dirt, 30); inv.SetSlot(4, Sand, 12); inv.SetSlot(5, DataKeys.IdOf("linen"), 9); inv.SetSlot(6, DataKeys.IdOf("string"), 4);
			inv.SetSlot(Inventory.Pocket0, Bag, 1, 0, new Inventory.PackData { Id = new[] { Blocks.Cobblestone }, N = new[] { 48 }, W = new int[1] });
			if (args.Contains("--hand"))
			{
				SelectSlot(2);
				int x0 = (int)player.Pos.X, z0 = (int)player.Pos.Z, y = world.Heights[z0 * World.WX + x0] + 1;
				for (int dx = -4; dx <= 4; dx++) for (int dz = -8; dz <= 2; dz++) { world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Grass); for (int u = 0; u < 6; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0); }
				player.Pos = new Vector3(x0 + 0.5f, y, z0 + 0.5f); player.Vel = Vector3.Zero; player.Flying = true; player.Yaw = 0; player.Pitch = -0.55f;
				var d1 = drops.Add(Bag, x0 - 0.9f, y, z0 - 1.4f); d1.V = Vector3.Zero; d1.Keep = true;
				var d2 = drops.Add(Bag, x0 + 0.2f, y, z0 - 1.4f); d2.V = Vector3.Zero; d2.Keep = true; d2.Data = part;
			}
			else { SelectSlot(1); OpenPanel(invPanel); tipPin = hotSlots[1]; Slot.TipShow?.Invoke(hotSlots[1]); }
		}
		if (args.Contains("--clothpic") && !clothShown && !menuOpen && shotTimer > 0.3f)
		{   // (to look at: the 13 Linen Cloths in rows, front to back: a vertical slab, a slab, stairs and a whole block of each)
			clothShown = true;
			int x0 = (int)player.Pos.X - 6, z0 = (int)player.Pos.Z - 8, y = world.Heights[z0 * World.WX + x0] + 1;
			for (int dx = -2; dx < 15; dx++) for (int dz = -2; dz < 12; dz++) { world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Stone); for (int u = 0; u < 6; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0); }
			string[] dyes = { "", "red_", "orange_", "yellow_", "green_", "aqua_", "blue_", "indigo_", "purple_", "magenta_", "pink_", "white_", "black_" };
			for (int k = 0; k < 13; k++)
			{
				int c = DataKeys.IdOf(dyes[k] + "linen_cloth");
				world.SetBlock(x0 + k, y, z0, c);
				world.SetSpots(x0 + k, y, z0 + 2, 0x33 | 0x88, c);
				world.SetSpots(x0 + k, y, z0 + 4, 0x33, c);
				world.SetSpots(x0 + k, y, z0 + 6, 0x55, c);
			}
			// the hotbar: cloth shapes' icons, and an empty hand
			int Cl(int k) => DataKeys.IdOf(dyes[k] + "linen_cloth");
			int[] bar = { Cl(0), Blocks.FormItem("slab", Cl(0)), Blocks.FormItem("stairs", Cl(1)), Blocks.FormItem("vslab", Cl(2)), Blocks.FormItem("quarter", Cl(4)),
				Blocks.FormItem("piece", Cl(6)), Blocks.FormItem("vstairs", Cl(8)), Blocks.FormItem("vquarter", Cl(10)), Blocks.FormItem("stairs", Cl(12)), 0 };
			for (int k = 0; k < bar.Length; k++) inv.SetSlot(k, bar[k]);
			SelectSlot(9);
			player.Pos = new Vector3(x0 + 6.5f, y + 2.2f, z0 + 10.5f); player.Flying = true; player.Yaw = 0; player.Pitch = -0.38f;
		}
		if (args.Contains("--cutshapes") && !cutTested && !menuOpen)
		{   // (test: slabs, stairs, quarters and pieces of stone, grass and terracotta to look at, and their icons in the hotbar)
			cutTested = true;
			int x0 = (int)player.Pos.X + 2, z0 = (int)player.Pos.Z - 4, y = world.Heights[z0 * World.WX + x0] + 1;
			for (int dx = -1; dx < 16; dx++) for (int dz = -1; dz < 8; dz++) { world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Stone); for (int u = 0; u < 4; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0); }
			int[] masks = { 0x33, 0xCC, 0x33 | 0x88, 0x55, 0x30, 0x01, 0xFF ^ 0xA0, 0x33 | 0x44 };
			int[] mats = { Blocks.Stone, 1, 65 };
			for (int r = 0; r < mats.Length; r++) for (int k = 0; k < masks.Length; k++) world.SetSpots(x0 + k * 2, y, z0 + r * 2, masks[k], mats[r]);
			// double slabs and loose pieces: nothing joins together any more
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int r4 = z0 + 6;
			world.SetSpots(x0, y, r4, 0x33, Blocks.Stone); world.SetSpots(x0, y, r4, 0xCC, Blocks.Stone);
			Check("two slabs stay a double slab", Blocks.IsPieces(world.GetBlock(x0, y, r4)));
			var ds = world.PieceMap[World.Idx(x0, y, r4)];
			Check("its top slab is a slab on its own", World.ShapeAt(ds, 2) is { shape: "slab", mask: 0xCC });
			world.SetSpots(x0, y, r4, World.ShapeAt(ds, 2).Value.mask, 0);
			Check("taking the top slab leaves the bottom one", Blocks.IsPieces(world.GetBlock(x0, y, r4)) && World.ShapeAt(world.PieceMap[World.Idx(x0, y, r4)], 0) is { shape: "slab" });
			world.SetSpots(x0, y, r4, 0xCC, Blocks.Stone);   // (and back, to look at)
			for (int s = 0; s < 8; s++) world.SetPiece(x0 + 2, y, r4, s, 65);
			Check("8 pieces stay 8 pieces", Blocks.IsPieces(world.GetBlock(x0 + 2, y, r4)) && Enumerable.Range(0, 8).All(s => World.ShapeAt(world.PieceMap[World.Idx(x0 + 2, y, r4)], s) == null));
			for (int s = 0; s < 4; s++) world.SetPiece(x0 + 4, y, r4, new[] { 0, 1, 4, 5 }[s], Blocks.Stone);
			Check("4 pieces side by side aren't a slab", World.ShapeAt(world.PieceMap[World.Idx(x0 + 4, y, r4)], 0) == null);
			world.SetSpots(x0 + 6, y, r4, 0x33, 1); world.SetSpots(x0 + 6, y, r4, 0xCC, 1);   // (a grass double slab)
			world.SetSpots(x0 + 8, y, r4, 0x33, 65); world.SetSpots(x0 + 8, y, r4, 0xCC, 65);
			// snow on the stone row's slabs and stairs: a layer on every top
			for (int k = 0; k < masks.Length; k++) world.SetBlock(x0 + k * 2, y, z0, Blocks.SnowyPieces);
			world.SetSpots(x0 + 12, y, r4, 0x33, Blocks.Stone); world.SetBlock(x0 + 12, y, r4, Blocks.SnowyPieces);
			world.SetSpots(x0 + 12, y, r4, 0x33, 0);
			Check("the last slab gone from snow on stone leaves the Snow", world.GetBlock(x0 + 12, y, r4) == Blocks.Snow);
			world.SetBlock(x0 + 12, y - 1, r4, 0); world.SetBlock(x0 + 12, y, r4, 0);
			world.SetSpots(x0 + 12, y, r4, 0xCC, Blocks.Stone); world.SetBlock(x0 + 12, y, r4, Blocks.SnowyPieces);
			world.SetSpots(x0 + 12, y, r4, 0xCC, 0);
			Check("the last slab gone from snow over a hole leaves air", world.GetBlock(x0 + 12, y, r4) == 0);
			world.SetBlock(x0 + 12, y - 1, r4, Blocks.Stone);
			string[] forms = { "slab", "stairs", "vslab", "quarter", "vstairs", "piece" };
			for (int k = 0; k < forms.Length; k++) inv.SetSlot(k, Blocks.FormItem(forms[k], Blocks.Stone));
			inv.SetSlot(6, Blocks.FormItem("slab", 1)); inv.SetSlot(7, Blocks.FormItem("stairs", 65));
			SelectSlot(1);
			player.Pos = new Vector3(x0 + 4.5f, y + 4.5f, z0 + 10f); player.Flying = true; player.Yaw = 0; player.Pitch = -0.95f;
		}
		if (args.Contains("--face") && others.Count > 0)
		{   // multiplayer test: step back from the first other player and look at them, menus closed
			var o = others.Values.First();
			var to = new Vector3(o.X, o.Y, o.Z);
			if (player.Pos.DistanceTo(to) < 3) player.Pos = to + new Vector3(0, 0, 4);
			var d = to - player.Pos;
			player.Yaw = MathF.Atan2(-d.X, -d.Z); player.Pitch = -0.1f;
			if (menuOpen) ShowMainMenu(false);
			pauseMenu.Visible = false;
		}
		if (!testGear && inv.Survival && (args.Contains("--wear") || args.Contains("--tools")))
		{   // (test: armor on, and tools in the tool slots, to see the armor bar and the tool row with)
			testGear = true;
			if (args.Contains("--wear"))
				foreach (int id in Blocks.GearItems.Where(i => Blocks.Get(i)?.Armor >= 0 && Blocks.B[i].Material == "iron")) inv.SetSlot(Inventory.Armor0 + Blocks.B[id].Armor, id);
			if (args.Contains("--tools"))
			{
				var tools = Blocks.Tools.Where(t => Blocks.B[t].Material == "iron").ToArray();
				for (int k = 0; k < Math.Min(tools.Length, inv.ToolCount); k++) inv.SetSlot(Inventory.Tool0 + k, tools[k]);
			}
		}
		string open = args.FirstOrDefault(a => a.StartsWith("--open="));
		if (open is "--open=worlds" or "--open=settings" or "--open=pause" or "--open=menu" or "--open=new")
		{
			if (open == "--open=worlds" && !worldsPanel.Visible) ShowWorlds(true);
			if (open == "--open=settings") { ShowSettings(true); if (ArgInt("--tab=", -1) >= 0) ShowSettingsTab(ArgInt("--tab=", 0)); }
			if (open == "--open=new" && !whatsNewPanel.Visible) whatsNewPanel.Visible = true;
			if (open == "--open=pause" && !pauseMenu.Visible) { if (menuOpen) ShowMainMenu(false); pauseMenu.Visible = true; UpdateSaveText(); Unlock(); }
		}
		else if (open != null && !invOpen)
		{
			if (args.Contains("--stuff") && inv.Survival)   // (test: a few things in your slots, to see the recipe book with)
			{
				foreach (var (id, n) in new[] { (Blocks.Planks, 12), (Blocks.Stick, 5), (Blocks.Cobblestone, 9), (Blocks.Stone, 1), (Blocks.Rock, 3), (Blocks.Log, 4) })
					for (int k = 0; k < n; k++) inv.Give(id);
			}
			if (open == "--open=howto") { OpenPanel(invPanel); ShowHowTo(Blocks.FormItem("stairs", Blocks.Stone)); }
			else if (open == "--open=kiln") { int x = (int)player.Pos.X, y = (int)player.Pos.Y + 3, z = (int)player.Pos.Z; world.SetBlock(x, y, z, Blocks.Furnace); kilnAt = World.Idx(x, y, z); var f = kilns.Of(kilnAt); f.Queue.Add(Blocks.Brick); f.Queue.Add(Blocks.Stone); f.Done[Blocks.Terracotta] = 3; OpenPanel(kilnPanel); }
			else if (open == "--open=bench") { benchBlock = Blocks.Planks; benchMode = 3; OpenBench(true); }
			else if (open == "--open=craftbench") OpenBench(false);
			else OpenPanel(invPanel);
		}
		lock (dirty) if (dirty.Count > 0) return;
		shotTimer += dt;
		var stay = args.FirstOrDefault(a => a.StartsWith("--stay="));
		if (shotTimer < (stay != null ? float.Parse(stay[7..]) : sceneBuilt ? 4f : 1.5f)) return;
		if (args.FirstOrDefault(a => a.StartsWith("--panorama=")) is { } panoArg) { if (!panoCapturing) CapturePanorama(panoArg[11..]); return; }
		if (GetViewport().GetTexture()?.GetImage() is { } shotImage) shotImage.SavePng(arg[7..]);   // (headless: no picture, the rest goes on)
		else GD.Print("no picture (headless)");
		GD.Print($"shot saved, {chunkParts.Count} chunks in {groupNodes.Count} groups");
		foreach (var m in mobList) GD.Print($"mob {m.Type}: layer {m.Layer}, health {m.Health}{(m.Burning ? ", burning" : "")}{(m.Fade > 0 ? ", fading" : "")} at y {m.Y:0.0}");
		GD.Print($"your health {health}, rocks flying {rocks.Count}");
		if (fallAt is var (tx, tg, tz)) GD.Print($"falltest: {falling.All.Count} still falling; ground {world.GetBlock(tx, tg, tz)} at y {tg}, then " + string.Join(", ", Enumerable.Range(1, 5).Select(u => world.GetBlock(tx, tg + u, tz))) + $"; you at {player.Pos} yaw {player.Yaw} pitch {player.Pitch}; falling at " + string.Join(" ", falling.All.Select(b => $"{b.Mesh.GlobalPosition}{(b.Mesh.IsVisibleInTree() ? "" : " hidden")}")));
		GetTree().Quit();
	}

	// (test) where the HUD went: the tool row, hearts and armor bar on one line 4 above the hotbar, or a tool row too long for the
	// gap on that line and the hearts and armor bar 4 above it; the armor bar's place kept whether it shows or not (so the tool
	// row never moves when armor goes on or off); the offhand slot 4 from the hotbar; the notes 4 above whatever is highest
	void CheckHudLayout()
	{
		void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
		var bar = hotBarBox.GetGlobalRect(); var off = offBox.GetGlobalRect();
		var tr = toolRow.GetGlobalRect(); var hr = heartsRect.GetGlobalRect(); var ar = armorBar.GetGlobalRect(); bool armor = armorBar.Visible;
		float line = bar.Position.Y - 4, gapL = bar.Position.X + hr.Size.X + 4, gapR = bar.End.X - hr.Size.X - 4;   // (the armor bar's place: the hearts' size, at the hotbar's right end)
		bool fits = tr.Size.X <= gapR - gapL;
		float iconFoot = fits ? line : tr.Position.Y - 4;
		Check($"the tool row's bottom on the line 4 above the hotbar (tool row {tr}, hotbar {bar})", Math.Abs(tr.End.Y - line) < 0.5f);
		if (fits) Check($"a {inv.ToolCount}-slot tool row fits between the hearts and the armor bar's place: centred in the gap",
			Math.Abs(tr.Position.X - (gapL + MathF.Round((gapR - gapL - tr.Size.X) / 2))) < 0.5f && tr.Position.X >= hr.End.X + 4 - 0.5f && tr.End.X <= gapR + 0.5f);
		else Check($"a {inv.ToolCount}-slot tool row too long for the gap: centred over the hotbar", Math.Abs(tr.Position.X - (bar.Position.X + MathF.Round((bar.Size.X - tr.Size.X) / 2))) < 0.5f);
		Check($"the hearts at the hotbar's left end, bottom {(fits ? "on the line" : "4 above the tool row")} (hearts {hr})", Math.Abs(hr.Position.X - bar.Position.X) < 0.5f && Math.Abs(hr.End.Y - iconFoot) < 0.5f);
		int q = ArmorQuarters(inv.ArmorBlock());
		if (q >= 1) Check($"armor on ({q} quarters): the armor bar shows, the hearts' size, at the hotbar's right end, level with the hearts (armor {ar})",
			armor && ar.Size == hr.Size && Math.Abs(ar.End.X - bar.End.X) < 0.5f && Math.Abs(ar.End.Y - hr.End.Y) < 0.5f);
		else Check("no armor: the armor bar is hidden, its place kept", !armor);
		Check("the offhand slot sits 4 from the hotbar", Math.Abs(bar.Position.X - off.End.X - 4) < 0.5f);
		float highest = Math.Min(tr.Position.Y, Math.Min(hr.Position.Y, off.Position.Y)); if (armor) highest = Math.Min(highest, ar.Position.Y);
		Check($"the notes sit 4 above whatever is highest (notes bottom {nameLabel.GetGlobalRect().End.Y}, highest {highest})", Math.Abs(nameLabel.GetGlobalRect().End.Y + 4 - highest) < 0.5f);
	}

	// scripted checks of the hotbar, crafting, bench, kiln and drops (prints PASS / FAIL lines)
	void SelfTest()
	{
		void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
		Check($"bedrock along the bottom of the world ({World.WY} tall)", world.GetBlock(100, 0, 100) == Blocks.Bedrock && world.GetBlock(500, 0, 37) == Blocks.Bedrock
			&& Blocks.Items.Contains(Blocks.Bedrock) && Blocks.Items.Contains(Blocks.Strata) == World.TallWorlds);
		// the chunk palette: block ids past 255, and a chunk holding more than 256 kinds of block (2-byte places)
		{
			var bs = new BlockStore(32, 32, 32);
			bs.Set(1, 2, 3, 70000); bs.Set(17, 2, 3, 300);
			for (int n = 0; n < 600; n++) bs.Set(n % 16, n / 256, (n / 16) % 16, 1000 + n);
			bool ok = bs.Get(17, 2, 3) == 300 && bs.Get(0, 0, 0) == 1000 && bs.Get(599 % 16, 599 / 256, (599 / 16) % 16) == 1599 && bs.Get(20, 20, 20) == 0;
			for (int n = 0; n < 600 && ok; n++) ok = bs[World.Idx(0, 0, 0) + 0] == 1000 && bs.Get(n % 16, n / 256, (n / 16) % 16) == 1000 + n;
			bs.Set(17, 2, 3, 0);
			Check("chunk palette: ids past 255 and chunks with more than 256 kinds of block", ok && bs.Get(17, 2, 3) == 0 && bs.Usage().bytes > 8192);
		}
		// Pack & Plate: ores in the ground (gold only deep, or at every depth from Land 2), and the counts to compare with the web version
		var oreCount = new int[5]; int goldShallow = 0;
		for (int q = 0; q < world.Blocks.Length; q++)
		{
			int ob = world.Blocks[q]; if (ob < Blocks.CopperOre || ob > Blocks.Fireclay) continue;
			oreCount[ob - Blocks.CopperOre]++;
			if (ob == Blocks.GoldOre) { var (ox, oy, oz) = World.FromIdx(q); if (world.Heights[oz * World.WX + ox] - oy < 20) goldShallow++; }
		}
		GD.Print("ores 142-146: " + string.Join(", ", oreCount));
		// loading a save: a block built above the land is a roof for the sky light (as the web version)
		{
			int rx = (int)player.Pos.X + 40, rz = (int)player.Pos.Z, ry = world.Heights[rz * World.WX + rx] + 6, rc = rz * World.WX + rx;
			world.ApplyEdits(new[] { World.Idx(rx, ry, rz) }, new[] { Blocks.Stone });
			bool roofed = world.SkyTop[rc] == ry && world.SkyExposure(rx, ry - 3, rz, null) < 1;
			world.SetBlock(rx, ry, rz, 0);
			Check($"a roof built above the land shades what's under it after loading (sky top {ry})", roofed && world.SkyTop[rc] < ry);
		}
		if (World.OreSpread(current.Land)) Check("ores and fireclay are in the ground, gold at every depth (lands 2 and 4)", oreCount.All(n => n > 0) && goldShallow > 0);
		else Check("ores and fireclay are in the ground, gold only deep", oreCount.All(n => n > 0) && goldShallow == 0);
		Check("ores need the right pickaxe", Blocks.B[Blocks.CopperOre].Pick == Blocks.PickTier["stone"] && Blocks.B[Blocks.IronOre].Pick == Blocks.PickTier["copper"]
			&& Blocks.B[Blocks.TinOre].Pick == Blocks.PickTier["iron"] && Blocks.B[Blocks.GoldOre].Pick == Blocks.PickTier["iron"] && Blocks.B[Blocks.IronOre].Drop == Blocks.Chunk["iron"]);
		// the Foundry: only works when it's all built
		{
			int fx = (int)player.Pos.X, fy = (int)player.Pos.Y + 12, fz = (int)player.Pos.Z;
			for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
			{
				world.SetBlock(fx + dx, fy - 1, fz + dz, Blocks.FireclayTerracotta); world.SetBlock(fx + dx, fy + 1, fz + dz, Blocks.FireclayBricks);
				world.SetBlock(fx + dx, fy, fz + dz, dx != 0 || dz != 0 ? Blocks.FireclayCasing : 0);
			}
			world.SetBlock(fx, fy, fz + 1, Blocks.Foundry);   // facing +z: the middle is behind it
			var fk = new Kilns(world) { Survival = false }; long fi = World.Idx(fx, fy, fz + 1);
			bool built = fk.FoundryBuilt(fi);
			world.SetBlock(fx - 1, fy, fz, 0);
			fk.Of(fi).Queue.Add(Blocks.MetalBrick["bronite"]);
			for (int t = 0; t < 80; t++) fk.Tick(0.1f, null);
			bool waited = fk.Of(fi).Done.Count == 0 && !Blocks.B[world.Blocks[fi]].Lit;
			world.SetBlock(fx - 1, fy, fz, Blocks.FireclayCasing);
			for (int t = 0; t < 80; t++) fk.Tick(0.1f, null);
			Check("the Foundry waits until it's all built, then makes Bronite", built && waited && fk.Of(fi).Done.GetValueOrDefault(Blocks.MetalBrick["bronite"]) == 1);
			Check("Foundry recipes: chunks, alloys, steel; the Kiln fires Fireclay", Kilns.Smelts.Count(m => m.At == "foundry") == 8
				&& Kilns.Smelts.Any(m => m.Out == Blocks.FireclayBrick && m.At == "kiln") && Inventory.Recipes.Any(r => r.Out == Blocks.Foundry) && Inventory.Recipes.Any(r => r.Out == 646));
		}
		// tool durability: a stone pickaxe lasts 160 uses, its wear moves with it, and it breaks at the end
		{
			var w = new Inventory(true); w.Give(Blocks.Tools[1]); w.Selected = 0;
			bool none = w.WearTool(159) == null && w.Id[0] == Blocks.Tools[1];
			w.Move(0, 3); bool moved = w.Wear[3] == 159 && w.Wear[0] == 0;
			w.Selected = 3; bool broke = w.WearTool(1) == "Stone Pickaxe" && w.Id[3] == 0;
			w.Give(Blocks.Tools[1], 40); bool kept = w.Wear[Array.IndexOf(w.Id, Blocks.Tools[1])] == 40;
			var cw = new Inventory(false); cw.SetSlot(0, Blocks.Tools[1]); cw.Selected = 0; cw.WearTool(500);
			Check("tools wear out and break; the wear moves and drops with them; creative tools never wear", none && moved && broke && kept && cw.Id[0] == Blocks.Tools[1]
				&& Inventory.MaxWear(649 + 1) == 480 && Inventory.MaxWear(Blocks.CrudeAxe) == 32 && Inventory.MaxWear(Blocks.Stick) == 0);
		}
		// gear: Pockets fill after the hotbar; a backpack opens its slots and keeps what's in it when taken off; slots only take
		// what fits; a belt's offhand slots empty out when it comes off; a full set of armor takes half; armor wears and breaks;
		// the tool slots give the longest-lasting tool of a kind
		{
			const int P = Inventory.Pocket0, B0 = Inventory.Pack0;
			var g = new Inventory(true);
			for (int k = 0; k < 10; k++) g.SetSlot(k, Blocks.Dirt, 64);
			g.Give(Blocks.Stick); bool pocket = g.Id[P] == Blocks.Stick && !g.SlotOpen(B0);
			g.SetSlot(P + 1, Blocks.Backpacks[2]);
			bool worn = g.Move(P + 1, Inventory.BackSlot) && g.PackSize == 15 && g.SlotOpen(B0 + 14) && !g.SlotOpen(B0 + 15);
			g.SetSlot(B0, Blocks.Rock, 5);
			g.Move(Inventory.BackSlot, P + 1); bool kept = g.Data[P + 1] != null && g.Id[B0] == 0 && g.PackSize == 0;
			g.Move(P + 1, Inventory.BackSlot); bool back = g.Id[B0] == Blocks.Rock && g.Count[B0] == 5;
			bool fits = !g.Fits(Inventory.Tool0, Blocks.Dirt) && g.Fits(Inventory.Tool0, Blocks.Tools[1]) && g.Fits(Inventory.Armor0 + 1, Blocks.ArmorItem(2, 1))
				&& !g.Fits(Inventory.Armor0, Blocks.ArmorItem(2, 1)) && !g.Fits(B0, Blocks.Backpacks[0]) && !g.Move(P + 2 - 2, Inventory.Armor0);
			int thrown = 0; g.ThrowOut = k => { thrown++; g.SetSlot(k, 0); return true; };
			g.SetSlot(P + 2, Blocks.Belts[3]); bool six = g.Move(P + 2, Inventory.BeltSlot) && g.OffCount == 6;
			g.SetSlot(Inventory.Off0 + 4, Blocks.Torch, 3); g.Move(Inventory.BeltSlot, P + 2); bool out1 = thrown == 1 && g.OffCount == 1;
			g.Move(P + 2, Inventory.BeltSlot); bool tools = g.ToolCount == 10 && g.SlotOpen(Inventory.Tool0 + 9) && !g.SlotOpen(Inventory.Tool0 + 10);
			g.SetSlot(Inventory.Tool0 + 9, Blocks.Tools[0]); g.Move(Inventory.BeltSlot, P + 2); out1 &= tools && thrown == 2 && g.ToolCount == 5;
			for (int k = 0; k < 4; k++) g.SetSlot(Inventory.Armor0 + k, Blocks.ArmorItem(0, k));
			bool half = Math.Abs(g.ArmorBlock() - 0.5f) < 0.001f;
			g.Wear[Inventory.Armor0] = 99; var broke = g.WearArmor();
			bool brk = broke.Count == 1 && broke[0] == "Leather Helmet" && g.Id[Inventory.Armor0] == 0 && g.Wear[Inventory.Armor0 + 1] == 1 && Inventory.MaxWear(Blocks.ArmorItem(5, 0)) == 900;
			g.SetSlot(Inventory.Tool0, Blocks.Tools[1]); g.SetSlot(Inventory.Tool0 + 1, 649 + 1); bool best = g.BestTool("pickaxe") == Inventory.Tool0 + 1 && g.BestTool("hoe") == -1;
			Check($"gear: pockets {pocket}, backpack {worn}/{kept}/{back}, fits {fits}, belt {six}/{out1}, armor {half}/{brk}, tool slots {best}", pocket && worn && kept && back && fits && six && out1 && half && brk && best
				&& Blocks.StackMaxOf(Blocks.Belts[0]) == 1 && Inventory.Recipes.Any(r => r.Out == Blocks.ArmorItem(5, 1) && r.Needs[0] == (Blocks.SteelBrick, 5)));
		}
		var s = new Inventory(true);
		for (int k = 0; k < 2; k++) s.Give(Blocks.Rock); s.Give(Blocks.Stick);
		var axe = Inventory.Recipes.First(r => r.Out == Blocks.CrudeAxe);
		Check("survival: 2 rocks + stick can make a Crude Axe", s.CanCraft(axe));
		// the Cutter: made at the bench from 3 Planks + a Copper Brick; cutting moved off the Crafting Bench onto it
		Check("the Cutter: a block made from 3 Planks + a Copper Brick at the bench", Blocks.B[Blocks.Cutter]?.Name == "Cutter" && Blocks.Items.Contains(Blocks.Cutter)
			&& Inventory.Recipes.Any(r => r.Out == Blocks.Cutter && r.At == "bench" && r.Needs.Contains((Blocks.Planks, 3)) && r.Needs.Contains((Blocks.MetalBrick["copper"], 1))));
		Check("the Cutter is half a block tall and its saw blade spins (frame 1, then frame 2, then frame 1)", Blocks.B[Blocks.Cutter].Shape == "half" && Atlas.SawSpins());
		// block sounds (data/blocks.json's sound): what breaking, placing and walking on a block ask the sound engine for
		{
			var heard = new List<string>();
			string Sounds(int b) { heard.Clear(); Sound.Heard = heard.Add; Sound.I?.Break(b); Sound.I?.Place(b); Sound.I?.Step(b); Sound.Heard = null; return string.Join(" ", heard); }
			string ice = Sounds(Blocks.Ice), cutter = Sounds(Blocks.Cutter);
			Check($"block sounds: Snowy Grass and Swamp Grass sound like Grass, Ice like Glass (Ice: {ice}), the Cutter (snowy too) like Planks (Cutter: {cutter}), Sandstone still the plain stone sound",
				Sound.I != null && Sounds(Blocks.Grass) == "grass:dig grass:dig grass:step" && Sounds(Blocks.SnowyGrass) == Sounds(Blocks.Grass) && Sounds(Blocks.SwampGrass) == Sounds(Blocks.Grass)
				&& ice == "shatter:dig glass:dig glass:step" && ice == Sounds(Blocks.Glass) && cutter == "wood:dig wood:dig wood:step" && cutter == Sounds(Blocks.Planks)
				&& Blocks.WithSnow(Blocks.Cutter, 1) != Blocks.Cutter && Sounds(Blocks.WithSnow(Blocks.Cutter, 1)) == cutter
				&& Sounds(Blocks.Sandstone) == "stone:dig stone:dig stone:step");
		}
		Check("crafting it works", s.Craft(axe) == true && s.CountOf(Blocks.CrudeAxe) == 1 && s.CountOf(Blocks.Rock) == 0 && s.CountOf(Blocks.Stick) == 0);
		s.Give(Blocks.Log);
		var planks = Inventory.Recipes.First(r => r.Out == Blocks.Planks && r.Needs[0].id == Blocks.Log);
		Check("log -> 4 planks with an axe", s.Craft(planks) == true && s.CountOf(Blocks.Planks) == 4);
		Check("bench: planks need 3 for stairs", s.BenchCut(Blocks.Planks, 3).StartsWith("Made 4") && s.CountOf(1200 + Blocks.Planks) == 4 && s.CountOf(Blocks.Planks) == 1);
		Check("bench: 1 plank -> 8 pieces", s.BenchCut(Blocks.Planks, 0).StartsWith("Took 8") && s.CountOf(Blocks.PieceItem + Blocks.Planks) == 8);
		s.Give(Blocks.FirLog);
		var firPlanks = Inventory.Recipes.First(r => r.Out == Blocks.FirPlanks && r.Needs[0].id == Blocks.FirLog);
		Check("Fir: the original wood is now Oak, and a Fir Log -> 4 Fir Planks (which cut into Fir Planks Stairs)", Blocks.B[Blocks.Log].Name == "Oak Log" && s.Craft(firPlanks) == true
			&& s.CountOf(Blocks.FirPlanks) == 4 && s.BenchCut(Blocks.FirPlanks, 3).StartsWith("Made 4") && Blocks.Get(1200 + Blocks.FirPlanks)?.Name == "Fir Planks Stairs");
		s.Give(Blocks.PalmLog);
		var palmPlanks = Inventory.Recipes.First(r => r.Out == Blocks.PalmPlanks && r.Needs[0].id == Blocks.PalmLog);
		Check("desert and swamp: a Palm Log -> 4 Palm Planks (which cut into stairs); Mud fires into Mud Bricks; Peat is fuel; Dates are food",
			s.Craft(palmPlanks) == true && s.CountOf(Blocks.PalmPlanks) == 4 && s.BenchCut(Blocks.PalmPlanks, 3).StartsWith("Made 4")
			&& s.CountOf(Blocks.FormItem("stairs", Blocks.PalmPlanks)) == 4 && Blocks.Get(Blocks.FormItem("stairs", Blocks.PalmPlanks))?.Name == "Palm Planks Stairs"
			&& Kilns.Smelts.Any(m => m.Out == Blocks.MudBricks && m.From == Blocks.Mud) && Kilns.Fuel[Blocks.Peat] == 8 && Blocks.B[Blocks.Dates].Food > 0
			&& Blocks.B[Blocks.WillowLeaves].T[0] != Blocks.B[Blocks.Leaves].T[0] && Blocks.TakesBiomeTint(Blocks.PalmLeaves));
		// deep stone sets: every piece / slab / stairs item has its own number, and each layer's stone gives its own cobblestone
		var forms = Blocks.Splittable.SelectMany(b => Blocks.Forms.Select(f => (b, f, id: Blocks.FormItem(f, b)))).ToArray();
		Check("deep sets: all piece and shape items are different", forms.Select(x => x.id).Distinct().Count() == forms.Length
			&& forms.All(x => x.f == "piece" ? Blocks.B[x.id].Piece == x.b : Blocks.B[x.id].Of == x.b && Blocks.B[x.id].Form == x.f));
		Check("deep sets: layer 5 stone gives layer 5 cobblestone (which smelts back while tall worlds are on)", Blocks.B[Blocks.Strata + 3].Drop == Blocks.DeepCobble + 3 && Blocks.B[Blocks.DeepCobble + 3].Layer == 5
			&& Kilns.Smelts.Any(m => m.Out == Blocks.Strata + 3 && m.From == Blocks.DeepCobble + 3) == World.TallWorlds && Blocks.B[Blocks.FormItem("slab", Blocks.DeepBricks + 3)].Of == Blocks.DeepBricks + 3);
		for (int k = 0; k < 70; k++) s.Give(Blocks.Dirt);
		Check("stacks stop at 64", s.Count.Max() == 64 && s.CountOf(Blocks.Dirt) == 70);
		var c = new Inventory(false);
		Check("creative: default hotbar", c.Id[0] == Blocks.Grass && c.Id[8] == Blocks.Torch);
		c.UseOne(); Check("creative: blocks never run out", c.Id[0] == Blocks.Grass);
		// kiln (creative: no fuel needed)
		int x = (int)player.Pos.X, y = (int)player.Pos.Y + 5, z = (int)player.Pos.Z;
		world.SetBlock(x, y, z, Blocks.Furnace);
		var kl = new Kilns(world) { Survival = false }; long i = World.Idx(x, y, z);
		kl.Of(i).Queue.Add(Blocks.Brick);
		kl.Tick(0.1f, null);
		Check("kiln lights up while smelting", Blocks.B[world.Blocks[i]].Lit);
		for (int t = 0; t < 60; t++) kl.Tick(0.1f, null);
		Check("kiln made a Brick and stored it", kl.Of(i).Done.GetValueOrDefault(Blocks.Brick) == 1 && !Blocks.B[world.Blocks[i]].Lit);
		// survival kiln with fuel
		var ks = new Kilns(world) { Survival = true }; var f = ks.Of(i); f.Fuel = Blocks.Stick; f.FuelN = 1; f.Queue.Add(Blocks.Stone); f.Queue.Add(Blocks.Stone);
		for (int t = 0; t < 120; t++) ks.Tick(0.1f, null);
		Check("survival kiln: 1 stick smelts 1 thing", f.Done.GetValueOrDefault(Blocks.Stone) == 1 && f.Queue.Count == 1 && f.FuelN == 0);
		world.SetBlock(x, y, z, 0);
		// drops: in survival, a dropped item falls and you pick it up
		var dp = new Drops(); AddChild(dp); dp.Init(world, player, s);
		{   // stand on the ground first (the world may have been saved with you flying high up)
			int gx = (int)MathF.Floor(player.Pos.X), gz = (int)MathF.Floor(player.Pos.Z);
			player.Pos = new Vector3(player.Pos.X, world.Heights[gz * World.WX + gx] + 1, player.Pos.Z); player.Flying = false;
		}
		int before = s.CountOf(Blocks.Stick);
		var d = dp.Add(Blocks.Stick, player.Pos.X, player.Pos.Y + 0.5f, player.Pos.Z); d.V = Vector3.Zero;
		for (int t = 0; t < 60; t++) dp.Update(1 / 60f);
		Check("walking into a dropped item picks it up", s.CountOf(Blocks.Stick) == before + 1 && dp.All.Count == 0);
		if (dp.All.Count > 0) GD.Print($"  (stick count {before} -> {s.CountOf(Blocks.Stick)}, drop at {dp.All[0].P}, you at {player.Pos}, slots: {string.Join(",", s.Id)})");
		dp.QueueFree();
		GD.Print("selftest done");
		GetTree().Quit();
	}

	bool sceneBuilt, mobsShown, diedTest, savetestDone, netTried, riverTested, riverPlayed;
	float riverScan;
	// (Beta 0.1.5.4) how much river you hear: the open river water near you (every 2nd column within 8, heights within 8 of your
	// eyes; each river water block with air above it adds 1 / (1 + d^2 / 25), d to the middle of its top), calm for gentle and
	// quick water, rapid for rapids and falling water. Only in Earthlike worlds. The same in the web version
	(float calm, float rapid) RiverHeard()
	{
		float calm = 0, rapid = 0;
		if (world?.Plan == null || player == null) return (0, 0);
		float hx = player.Pos.X, hy = player.Pos.Y + Player.Eye, hz = player.Pos.Z;
		int x0 = (int)MathF.Floor(hx), y0 = (int)MathF.Floor(hy), z0 = (int)MathF.Floor(hz);
		for (int dz = -8; dz <= 8; dz += 2) for (int dx = -8; dx <= 8; dx += 2) for (int dy = -8; dy <= 8; dy++)
		{
			int x = x0 + dx, y = y0 + dy, z = z0 + dz;
			if (!World.Inside(x, y, z) || !World.Inside(x, y + 1, z)) continue;
			int b = world.GetBlock(x, y, z);
			if (!Blocks.IsRiverWater(b) || world.GetBlock(x, y + 1, z) != 0) continue;
			float ex = x + 0.5f - hx, ey = y + 1 - hy, ez = z + 0.5f - hz, w = 1 / (1 + (ex * ex + ey * ey + ez * ez) / 25);
			if (b >= Blocks.RiverWater + 16) rapid += w; else calm += w;
		}
		return (calm, rapid);
	}
	// (test: --piecescene: whole blocks beside a piece, slab, stairs and quarter of the same block, against a stone wall and out
	//  on grass, to compare how they're lit; --torch adds torches, for a picture at night)
	bool pieceSceneBuilt;
	void BuildPieceScene()
	{
		pieceSceneBuilt = true;
		int cx = (int)player.Pos.X, cz = (int)player.Pos.Z - 10, y = world.Heights[cz * World.WX + cx] + 1;
		for (int x = -9; x <= 9; x++) for (int z = -4; z <= 12; z++)
		{
			for (int u = -3; u < 0; u++) world.SetBlock(cx + x, y + u, cz + z, u == -1 ? Blocks.Grass : Blocks.Dirt);
			for (int u = 0; u < 10; u++) if (world.GetBlock(cx + x, y + u, cz + z) != 0) world.SetBlock(cx + x, y + u, cz + z, 0);
		}
		for (int x = -8; x <= 8; x++) for (int u = 0; u < 3; u++) world.SetBlock(cx + x, y + u, cz - 2, Blocks.Stone);   // the wall
		// against the wall: Stone whole, a slab, stairs, one piece (in the corner by the wall), a quarter, Stone whole
		world.SetBlock(cx - 6, y, cz - 1, Blocks.Stone);
		world.SetSpots(cx - 4, y, cz - 1, 0x33, Blocks.Stone);
		world.SetSpots(cx - 2, y, cz - 1, Blocks.ShapeMasks["stairs"][2], Blocks.Stone);
		world.SetPiece(cx, y, cz - 1, 0, Blocks.Stone);
		world.SetSpots(cx + 2, y, cz - 1, 0x03, Blocks.Stone);
		world.SetBlock(cx + 4, y, cz - 1, Blocks.Stone);
		world.SetPiece(cx + 6, y, cz - 1, 0, Blocks.Stone); world.SetPiece(cx + 6, y, cz - 1, 1, Blocks.Stone);
		// out on the grass: Cobblestone whole, a piece, a slab; Grass whole (a step up) and a Grass slab; Oak Leaves whole and a slab
		world.SetBlock(cx - 6, y, cz + 2, Blocks.Cobblestone);
		world.SetPiece(cx - 4, y, cz + 2, 0, Blocks.Cobblestone);
		world.SetSpots(cx - 2, y, cz + 2, 0x33, Blocks.Cobblestone);
		world.SetBlock(cx, y, cz + 2, Blocks.Grass);
		world.SetSpots(cx + 2, y, cz + 2, 0x33, Blocks.Grass);
		world.SetBlock(cx + 4, y, cz + 2, Blocks.Leaves);
		world.SetSpots(cx + 6, y, cz + 2, 0x33, Blocks.Leaves);
		// a few loose pieces scattered on the grass
		world.SetPiece(cx - 5, y, cz + 5, 0, Blocks.Cobblestone); world.SetPiece(cx - 1, y, cz + 5, 5, Blocks.Stone); world.SetPiece(cx + 3, y, cz + 5, 4, Blocks.Planks);
		if (HasArg("--torch")) { world.SetBlock(cx - 3, y, cz + 4, Blocks.TorchVariant(0, 1, 0)); world.SetBlock(cx + 5, y, cz + 4, Blocks.TorchVariant(0, 1, 0)); world.SetBlock(cx + 1, y, cz, Blocks.TorchVariant(0, 1, 0)); }
		player.Pos = new Vector3(cx + 0.5f, y + 3.2f, cz + 9.5f); player.Vel = Vector3.Zero; player.Flying = true; player.Yaw = 0; player.Pitch = -0.42f;
		if (HasArg("--near")) { player.Pos = new Vector3(cx - 0.5f, y + 1.6f, cz + 5.2f); player.Pitch = -0.5f; }   // (closer: both rows, left half)
	}
	// (test: --holescene: holes 1 x 1, 2 x 2 and 3 x 3, 3 deep, in flat grass: the back row empty, the front row with pieces, slabs,
	//  stairs and quarters inside (on the floor, on the walls partway down, by the top edge), to compare how their floors are lit)
	bool holeSceneBuilt;
	void BuildHoleScene()
	{
		holeSceneBuilt = true;
		int cx = (int)player.Pos.X, cz = (int)player.Pos.Z - 10, y = world.Heights[cz * World.WX + cx] + 1;
		for (int x = -9; x <= 9; x++) for (int z = -6; z <= 12; z++)
		{
			for (int u = -9; u < 0; u++) world.SetBlock(cx + x, y + u, cz + z, u == -1 ? Blocks.Grass : Blocks.Dirt);
			for (int u = 0; u < 10; u++) if (world.GetBlock(cx + x, y + u, cz + z) != 0) world.SetBlock(cx + x, y + u, cz + z, 0);
		}
		void Hole(int hx, int hz, int n) { for (int dx = 0; dx < n; dx++) for (int dz = 0; dz < n; dz++) for (int u = 1; u <= 3; u++) world.SetBlock(hx + dx, y - u, hz + dz, 0); }
		int[] xs = { cx - 7, cx - 3, cx + 2 }; int[] ns = { 1, 2, 3 };
		for (int k = 0; k < 3; k++) { Hole(xs[k], cz - 3, ns[k]); Hole(xs[k], cz + 3, ns[k]); }
		int fz = cz + 3, fl = y - 3;   // (the front row, and its holes' bottom layer of air)
		bool probe = HasArg("--holeprobe");
		// (--holeprobe: each floor's light from the meshes: shading and sky at every corner of the floor's faces)
		string FloorLight(int hx, int hz, int n)
		{
			var mesher = new ChunkMesher(world); var seen = new HashSet<(int, int, int)>();
			float shade = 0, skyv = 0, lo = 9; int cnt = 0;
			for (int dx = 0; dx < n; dx++) for (int dz = 0; dz < n; dz++)
			{
				int bx = hx + dx, bz = hz + dz;
				if (!seen.Add((bx / World.CS, fl / World.CS, bz / World.CS))) continue;
				var (solid, _) = mesher.Build(bx / World.CS, fl / World.CS, bz / World.CS);
				for (int v = 0; v + 3 < solid.Pos.Count; v += 4)
				{
					bool flat = true; for (int k = 0; k < 4; k++) flat &= solid.Pos[v + k].Y == fl;
					if (!flat) continue;
					for (int k = 0; k < 4; k++)
					{
						var p = solid.Pos[v + k];
						if (p.X < hx || p.X > hx + n || p.Z < hz || p.Z > hz + n) continue;
						float b = solid.Col[v + k].A * solid.Uv2[v + k].Y;
						shade += solid.Col[v + k].A; skyv += solid.Uv2[v + k].Y; lo = Math.Min(lo, b); cnt++;
					}
				}
			}
			return cnt == 0 ? "none" : $"corners {cnt} shade {shade / cnt:0.000} sky {skyv / cnt:0.000} darkest {lo:0.000}";
		}
		void Probe(string when) { for (int k = 0; k < 3; k++) GD.Print($"HOLEPROBE {when} {ns[k]}x{ns[k]} empty: {FloorLight(xs[k], cz - 3, ns[k])} | pieces row: {FloorLight(xs[k], fz, ns[k])}"); }
		if (probe) Probe("before");
		// 1 x 1: a piece on the floor, and a top-half piece stuck to the back wall partway down
		world.SetPiece(xs[0], fl, fz, 0, Blocks.Stone); world.SetPiece(xs[0], fl + 1, fz, 3, Blocks.Stone);
		// 2 x 2: stairs on the floor, a quarter on the wall by the top edge, a slab partway down
		world.SetSpots(xs[1] + 1, fl, fz + 1, Blocks.ShapeMasks["stairs"][2], Blocks.Cobblestone);
		world.SetSpots(xs[1], fl + 2, fz, 0x0C, Blocks.Planks);
		world.SetSpots(xs[1] + 1, fl + 1, fz, 0xCC, Blocks.Stone);
		// 3 x 3: a slab on the floor in the middle, pieces on the walls at each height, a quarter by the top edge
		world.SetSpots(xs[2] + 1, fl, fz + 1, 0x33, Blocks.Cobblestone);
		world.SetPiece(xs[2], fl + 1, fz + 1, 2, Blocks.Stone); world.SetPiece(xs[2] + 2, fl, fz + 2, 1, Blocks.Stone);
		world.SetSpots(xs[2] + 1, fl + 2, fz, 0x0C, Blocks.Planks);
		if (probe) { Probe("after"); GetTree().Quit(); }
		player.Pos = new Vector3(cx - 1.5f, y + 11.5f, cz + 1.5f); player.Vel = Vector3.Zero; player.Flying = true; player.Yaw = MathF.PI; player.Pitch = -1.5f;   // (looking down: the front row at the top)
		// (--holeview=front or back: straight down over one row, closer)
		if (HasArg("--holeview=front")) player.Pos = new Vector3(cx - 1f, y + 7f, fz + 1.5f);
		if (HasArg("--holeview=back")) player.Pos = new Vector3(cx - 1f, y + 7f, cz - 1.5f);
		// (--holeview=one: close up, straight down into the 1 x 1 hole with the piece on its floor)
		if (HasArg("--holeview=one")) player.Pos = new Vector3(xs[0] + 0.5f, y + 0.1f, fz + 0.25f);
	}
	// (test: --casingscene: Fireclay Bricks, Fireclay Casing and Fireclay Terracotta in a row in the air, close up, from above;
	//  --casingview=below looks up at them from underneath. The hotbar holds the three, the casing in your hand)
	bool casingSceneBuilt;
	void BuildCasingScene()
	{
		casingSceneBuilt = true;
		int cx = (int)player.Pos.X, cz = (int)player.Pos.Z - 8, y = world.Heights[cz * World.WX + cx] + 8;
		for (int x = -12; x <= 12; x++) for (int z = -8; z <= 12; z++) for (int u = -8; u < 12; u++) if (world.GetBlock(cx + x, y + u, cz + z) != 0) world.SetBlock(cx + x, y + u, cz + z, 0);
		world.SetBlock(cx - 2, y, cz, Blocks.FireclayBricks);
		world.SetBlock(cx, y, cz, Blocks.FireclayCasing);
		world.SetBlock(cx + 2, y, cz, Blocks.FireclayTerracotta);
		int[] hot = { Blocks.FireclayCasing, Blocks.FireclayBricks, Blocks.FireclayTerracotta };
		for (int k = 0; k < 9; k++) inv.SetSlot(k, k < hot.Length ? hot[k] : 0);
		SelectSlot(0);
		bool below = HasArg("--casingview=below");
		player.Pos = new Vector3(cx + 0.5f, below ? y - 3.4f : y + 2.6f, cz + 2.6f); player.Vel = Vector3.Zero; player.Flying = true; player.Yaw = 0;
		player.Pitch = below ? 1.0f : -0.9f;
	}
	// (test: --aoscene: corner shadows to compare (with --aodarkest=N): holes 1 x 1, 2 x 2 and 3 x 3, 3 deep, in flat grass, seen from
	//  above; --aoview=corner: the inside corner of two stone walls and a 1-wide tunnel through a block of stone, from the side)
	bool aoSceneBuilt;
	void BuildAoScene()
	{
		aoSceneBuilt = true;
		int cx = (int)player.Pos.X, cz = (int)player.Pos.Z - 6, y = world.Heights[cz * World.WX + cx] + 1;
		for (int x = -12; x <= 12; x++) for (int z = -20; z <= 10; z++)
		{
			for (int u = -9; u < 0; u++) world.SetBlock(cx + x, y + u, cz + z, u == -1 ? Blocks.Grass : Blocks.Dirt);
			for (int u = 0; u < 14; u++) if (world.GetBlock(cx + x, y + u, cz + z) != 0) world.SetBlock(cx + x, y + u, cz + z, 0);
		}
		void Hole(int hx, int hz, int n) { for (int dx = 0; dx < n; dx++) for (int dz = 0; dz < n; dz++) for (int u = 1; u <= 3; u++) world.SetBlock(hx + dx, y - u, hz + dz, 0); }
		Hole(cx - 5, cz + 1, 1); Hole(cx - 2, cz, 2); Hole(cx + 2, cz, 3);
		// the inside corner: two stone walls 3 tall meeting at a right angle, open towards you
		int wz = cz - 14;
		for (int k = 0; k < 5; k++) for (int u = 0; u < 3; u++) { world.SetBlock(cx - 7 + k, y + u, wz, Blocks.Stone); world.SetBlock(cx - 7, y + u, wz + k, Blocks.Stone); }
		// the tunnel: a block of stone 5 wide, 4 tall, 7 deep, with a tunnel 1 wide and 2 tall through it, towards you
		for (int x = 1; x <= 5; x++) for (int z = -6; z <= 0; z++) for (int u = 0; u < 4; u++) world.SetBlock(cx + x, y + u, wz + z + 4, x == 3 && u < 2 ? 0 : Blocks.Stone);
		bool corner = HasArg("--aoview=corner");
		player.Vel = Vector3.Zero; player.Flying = true; player.Yaw = 0;
		if (corner) { player.Pos = new Vector3(cx + 3.5f, y + 1.6f, wz + 10.5f); player.Pitch = -0.12f; }
		else { player.Pos = new Vector3(cx + 0.5f, y + 5.5f, cz + 5f); player.Pitch = -1.05f; }
	}
	// (test: --icescene: Ice whole, as a slab, a top slab, stairs, a quarter, a vertical slab and a piece on a stone floor in the air,
	//  the shape items in the hotbar; prints each shape item's id and name)
	bool iceSceneBuilt;
	void BuildIceScene()
	{
		iceSceneBuilt = true;
		int cx = (int)player.Pos.X, cz = (int)player.Pos.Z - 8, y = world.Heights[cz * World.WX + cx] + 8, I = Blocks.Ice;
		for (int x = -12; x <= 12; x++) for (int z = -8; z <= 12; z++) for (int u = -8; u < 12; u++) if (world.GetBlock(cx + x, y + u, cz + z) != 0) world.SetBlock(cx + x, y + u, cz + z, 0);
		for (int x = -8; x <= 8; x++) for (int z = -2; z <= 2; z++) world.SetBlock(cx + x, y - 1, cz + z, Blocks.Stone);
		var st = Blocks.ShapeMasks["stairs"];
		world.SetBlock(cx - 6, y, cz, I);
		world.SetSpots(cx - 4, y, cz, 0x33, I);          // a slab
		world.SetSpots(cx - 2, y, cz, 0xCC, I);          // a top slab
		world.SetSpots(cx, y, cz, st[2], I);             // stairs
		world.SetSpots(cx + 2, y, cz, 0x03, I);          // a quarter
		world.SetSpots(cx + 4, y, cz, 0x0F, I);          // a vertical slab
		world.SetPiece(cx + 6, y, cz, 0, I);             // a piece
		var hot = new List<int> { I };
		foreach (var f in Blocks.Forms) hot.Add(Blocks.FormItem(f, I));
		for (int k = 0; k < 9; k++) inv.SetSlot(k, k < hot.Count ? hot[k] : 0);
		SelectSlot(0);
		foreach (int h in hot) GD.Print($"ICESHAPE {h} {Blocks.Get(h)?.Name}");
		player.Pos = new Vector3(cx + 0.5f, y + 2.6f, cz + 5.5f); player.Vel = Vector3.Zero; player.Flying = true; player.Yaw = 0; player.Pitch = -0.45f;
		// (--icemix: on grass instead, the shapes touching each other and whole Ice in two rows, a slab on top of whole Ice, an
		//  upside-down stairs over a gap; --iceview=low: seen from just above the ground)
		if (HasArg("--icemix"))
		{
			int g = world.Heights[(cz + 6) * World.WX + cx] + 1;
			for (int x = -6; x <= 6; x++) for (int z = -4; z <= 8; z++)
			{
				world.SetBlock(cx + x, g - 1, cz + 6 + z, Blocks.Grass);
				for (int u = 0; u < 8; u++) if (world.GetBlock(cx + x, g + u, cz + 6 + z) != 0) world.SetBlock(cx + x, g + u, cz + 6 + z, 0);
			}
			int z0 = cz + 6, z1 = cz + 5;
			world.SetBlock(cx - 3, g, z0, I); world.SetSpots(cx - 2, g, z0, 0x33, I); world.SetSpots(cx - 1, g, z0, st[2], I);
			world.SetBlock(cx, g, z0, I); world.SetSpots(cx + 1, g, z0, 0x55, I); world.SetSpots(cx + 2, g, z0, 0x03, I); world.SetBlock(cx + 3, g, z0, I);
			world.SetSpots(cx - 3, g, z1, 0x33, I); world.SetBlock(cx - 2, g, z1, I); world.SetSpots(cx - 1, g, z1, 0xCC, I);
			world.SetSpots(cx, g, z1, st[3], I); world.SetBlock(cx + 1, g, z1, I); world.SetPiece(cx + 2, g, z1, 1, I); world.SetSpots(cx + 3, g, z1, 0x33, I);
			world.SetSpots(cx, g + 1, z0, 0x33, I);                 // a slab on top of whole Ice
			world.SetSpots(cx + 2, g + 1, z0, st[6], I);            // upside-down stairs over the quarter
			bool low = HasArg("--iceview=low");
			player.Pos = new Vector3(cx + 2.5f, low ? g + 1.2f : g + 3.2f, z0 + (low ? 4.5f : 4f)); player.Pitch = low ? -0.15f : -0.6f;
		}
	}
	// (test: --doorscene: a wall of Stone Bricks on flat stone with doors in it, their fronts to +z: an Oak door shut, an Oak door
	//  open, an Oak door open with a Stone block behind it, and a Cherry double door open; seen from behind the wall, the side
	//  they open to; --doorsceneview=front: from in front)
	bool doorSceneBuilt;
	void BuildDoorScene()
	{
		doorSceneBuilt = true;
		int cx = (int)player.Pos.X, cz = (int)player.Pos.Z - 4, y = world.Heights[cz * World.WX + cx] + 1;
		for (int x = -12; x <= 12; x++) for (int z = -12; z <= 12; z++)
		{
			for (int u = -4; u < 0; u++) world.SetBlock(cx + x, y + u, cz + z, Blocks.Stone);
			for (int u = 0; u < 14; u++) if (world.GetBlock(cx + x, y + u, cz + z) != 0) world.SetBlock(cx + x, y + u, cz + z, 0);
		}
		int bricks = DataKeys.IdOf("stone_bricks"), oak = DataKeys.IdOf("oak_door"), cherry = DataKeys.IdOf("cherry_door");
		int[] gaps = { -6, -3, 0, 4, 5 };
		for (int x = -9; x <= 9; x++) for (int u = 0; u < 3; u++) if (u == 2 || Array.IndexOf(gaps, x) < 0) world.SetBlock(cx + x, y + u, cz, bricks);
		void Door(int x, int item, int hinge, bool open) { world.SetBlock(cx + x, y, cz, Blocks.DoorState(item, 0, hinge, open, 0)); world.SetBlock(cx + x, y + 1, cz, Blocks.DoorState(item, 0, hinge, open, 1)); }
		Door(-6, oak, 0, false); Door(-3, oak, 0, true); Door(0, oak, 0, true); Door(4, cherry, 0, true); Door(5, cherry, 1, true);
		world.SetBlock(cx, y, cz - 1, Blocks.Stone);   // (where the open door at 0 swings to)
		inv.SetSlot(inv.Selected, oak);
		player.Vel = Vector3.Zero; player.Flying = true;
		if (HasArg("--doorsceneview=front")) { player.Pos = new Vector3(cx + 0.5f, y + 2.6f, cz + 7.5f); player.Yaw = 0; player.Pitch = -0.32f; }
		else { player.Pos = new Vector3(cx - 0.5f, y + 4.2f, cz - 8.5f); player.Yaw = MathF.PI; player.Pitch = -0.5f; }
	}
	void BuildTestScene()
	{
		sceneBuilt = true;
		// a stone floor in the air, with one of everything on it, seen from the side at dusk-free daylight
		int cx = (int)player.Pos.X, cy = 120, cz = (int)player.Pos.Z - 8;
		for (int x = -6; x <= 6; x++) for (int z = -3; z <= 3; z++) world.SetBlock(cx + x, cy, cz + z, Blocks.Stone);
		world.SetBlock(cx - 6, cy + 1, cz - 3, Blocks.Water);                                   // water that should spread over the floor
		world.SetSpots(cx - 4, cy + 1, cz, 0x33, Blocks.StoneBricks);                           // a slab
		world.SetSpots(cx - 3, cy + 1, cz, Blocks.ShapeMasks["stairs"][0], Blocks.Planks);       // stairs
		world.SetPiece(cx - 2, cy + 1, cz, 0, Blocks.Cobblestone);                               // a piece
		world.SetBlock(cx - 1, cy + 1, cz, Blocks.TorchVariant(0, 1, 0));                        // lit
		world.SetBlock(cx, cy + 1, cz, Blocks.TorchVariant(0, 4, 1));                            // 4 smoldering
		world.SetBlock(cx + 1, cy + 1, cz, Blocks.TorchVariant(0, 1, 2));                        // burnt
		world.SetBlock(cx + 2, cy + 1, cz, Blocks.TorchVariant(0, 2, 3));                        // 2 ever
		world.SetBlock(cx + 4, cy + 1, cz, Blocks.Furnace);                                      // kiln
		world.SetBlock(cx + 3, cy + 1, cz + 1, Blocks.Cutter);                                   // the Cutter (half a block tall, its saw blade standing up)
		for (int k = 0; k < 12; k++)
		{   // along the back: every colour's bricks, terracotta and chiseled terracotta, bottom to top
			world.SetBlock(cx - 6 + k, cy + 1, cz - 3, Blocks.ColourBricks[k]); world.SetBlock(cx - 6 + k, cy + 2, cz - 3, Blocks.ColourTerracotta[k]);
			world.SetBlock(cx - 6 + k, cy + 3, cz - 3, Blocks.ColourChiseled[k]);
		}
		for (int k = 0; k < 5; k++) { world.SetBlock(cx - 2 + k, cy, cz + 2, Blocks.Grass); world.SetBlock(cx - 2 + k, cy + 1, cz + 2, Blocks.Flowers[k]); }   // the flowers, on grass
		player.Pos = new Vector3(cx + 0.5f, cy + 1.5f, cz + 7.5f); player.Flying = true; player.Yaw = 0; player.Pitch = -0.35f;
		GD.Print($"burn timers: {world.TorchBurn.Count}  (expect 2: lit + smoldering)");
	}

	// ---- render distance (also how far out mobs are simulated) ----
	const int MinRender = 4, MaxRender = 32;
	const float ForgetMobs = 128;   // (mobs other than cows are forgotten past this, or the render distance if it's nearer)
	float RenderBlocks => (settings?.RenderDist ?? 10) * World.CS;   // (in blocks)
	float FogThickness => 0.045f * 10 / (settings?.RenderDist ?? 10);   // (the haze thins out as you see further, so 10 chunks looks like it always did)
	void ApplyRenderDist()
	{
		RenderingServer.GlobalShaderParameterSet("fog_edge", RenderBlocks);
		if (!wasUnder) RenderingServer.GlobalShaderParameterSet("fog_thickness", FogThickness);
		lastGroupAt = -1;   // (show and hide groups, and build the newly near ones, straight away)
	}
	// how far (sideways) the camera is from the nearest edge of group g
	static float GroupDist(int g, float px, float pz)
	{
		const int S = World.CS * World.GS;
		var (gx, _, gz) = World.FromGroupKey(g);
		float dx = Math.Max(0, Math.Max(gx * S - px, px - (gx + 1) * S)), dz = Math.Max(0, Math.Max(gz * S - pz, pz - (gz + 1) * S));
		return MathF.Sqrt(dx * dx + dz * dz);
	}
	// how far up or down the camera is from group g (tall worlds: deep caves and the sky far above aren't built or drawn)
	static float GroupDistY(int g, float py)
	{
		const int S = World.CS * World.GS;
		var (_, gy, _) = World.FromGroupKey(g);
		return Math.Max(0, Math.Max(gy * S - py, py - (gy + 1) * S));
	}
	const int ViewUpDown = 64;
	bool InRange(int g) => player == null || GroupDist(g, player.Pos.X, player.Pos.Z) < RenderBlocks && GroupDistY(g, player.Pos.Y) < RenderBlocks + ViewUpDown;
	int lastGroupAt = -1;
	// whenever you move into another group: hide the groups past the render distance, show the ones within it, and
	// let the chunks that were too far to build get built if they're near enough now
	void CullGroups()
	{
		const int S = World.CS * World.GS;
		int at = ((int)(player.Pos.X / S) * 1000 + (int)(player.Pos.Z / S)) * 100 + (int)Math.Floor(player.Pos.Y / S);
		if (at == lastGroupAt) return;
		lastGroupAt = at;
		foreach (var (g, n) in groupNodes) { bool v = InRange(g); n.solid.Visible = v; n.water.Visible = v; }
		lock (dirty) { foreach (int c in far) dirty.Add(c); far.Clear(); }
	}

	// rebuild changed chunks, nearest first, for up to a few milliseconds a frame (several at once, on all CPU cores)
	void BuildChunks()
	{
		CullGroups();
		var start = Time.GetTicksUsec();
		// (nearest GROUP first, and a group's chunks together, so each group gets glued together once)
		const int S = World.CS * World.GS;
		int pgx = (int)player.Pos.X / S, pgy = (int)player.Pos.Y / S, pgz = (int)player.Pos.Z / S;
		int Dist(int c) { int g = World.GroupOf(c); var (gx, gy, gz) = World.FromGroupKey(g); int dx = gx - pgx, dy = gy - pgy, dz = gz - pgz; return (dx * dx + dy * dy + dz * dz) * World.NGroups + g; }
		var touched = new HashSet<int>();
		try { BuildSome(start, Dist, touched); }
		finally { foreach (int g in touched) MergeGroup(g); }
	}
	void BuildSome(ulong start, Func<int, int> Dist, HashSet<int> touched)
	{
		int want = Math.Max(2, System.Environment.ProcessorCount);
		var best = new int[want]; var bestD = new int[want];
		while (Time.GetTicksUsec() - start < 8000)
		{
			int[] batch;
			lock (dirty)
			{
				if (dirty.Count == 0 && soon.Count > 0) { foreach (int c in soon) dirty.Add(c); soon.Clear(); }
				if (dirty.Count == 0) return;
				// the nearest few (one pass, keeping a short sorted list, rather than sorting thousands of chunks every time)
				int n = 0; List<int> tooFar = null;
				foreach (int c in dirty)
				{
					if (!InRange(World.GroupOf(c))) { (tooFar ??= new()).Add(c); continue; }
					int d = Dist(c);
					if (n == want && d >= bestD[n - 1]) continue;
					int j = n < want ? n++ : n - 1;
					while (j > 0 && bestD[j - 1] > d) { best[j] = best[j - 1]; bestD[j] = bestD[j - 1]; j--; }
					best[j] = c; bestD[j] = d;
				}
				if (tooFar != null) foreach (int c in tooFar) { dirty.Remove(c); soon.Remove(c); far.Add(c); }
				if (n == 0) return;
				batch = best[..n];
				foreach (int c in batch) { dirty.Remove(c); soon.Remove(c); }
			}
			var parts = new (ChunkMesher.Part, ChunkMesher.Part)[batch.Length];
			Parallel.For(0, batch.Length, i => { var (cx, cy, cz) = World.FromChunkKey(batch[i]); parts[i] = mesher.Build(cx, cy, cz); });
			for (int i = 0; i < batch.Length; i++)
			{
				var (s, w) = parts[i];
				if (s.Ind.Count == 0 && w.Ind.Count == 0) chunkParts.Remove(batch[i]);
				else chunkParts[batch[i]] = (s.Ind.Count > 0 ? s : null, w.Ind.Count > 0 ? w : null);
				touched.Add(World.GroupOf(batch[i]));
			}
		}
	}
	// glue a group's chunks into one mesh for the solid blocks and one for the water
	void MergeGroup(int g)
	{
		var (gx, gy, gz) = World.FromGroupKey(g);
		var solids = new List<ChunkMesher.Part>(); var waters = new List<ChunkMesher.Part>();
		for (int dy = 0; dy < World.GS; dy++) for (int dz = 0; dz < World.GS; dz++) for (int dx = 0; dx < World.GS; dx++)
			if (chunkParts.TryGetValue(World.ChunkKey(gx * World.GS + dx, gy * World.GS + dy, gz * World.GS + dz), out var p))
			{ if (p.solid != null) solids.Add(p.solid); if (p.water != null) waters.Add(p.water); }
		var solid = solids.Count == 0 ? null : solids.Count == 1 ? solids[0].ToMesh() : ChunkMesher.Part.Join(solids).ToMesh();
		var water = waters.Count == 0 ? null : waters.Count == 1 ? waters[0].ToMesh() : ChunkMesher.Part.Join(waters).ToMesh();
		if (!groupNodes.TryGetValue(g, out var n))
		{
			if (solid == null && water == null) return;   // (an empty group, all air or buried rock: no nodes for it)
			n = (new MeshInstance3D { MaterialOverride = blockMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off },
				 new MeshInstance3D { MaterialOverride = waterMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
			AddChild(n.solid); AddChild(n.water);
			groupNodes[g] = n;
			n.solid.Visible = n.water.Visible = InRange(g);
		}
		n.solid.Mesh = solid; n.water.Mesh = water;
	}

	// ---- the outline around the block you're looking at ----
	static MeshInstance3D MakeOutline()
	{
		var im = new ImmediateMesh();
		im.SurfaceBegin(Mesh.PrimitiveType.Lines);
		float a = -0.002f, b = 1.002f;
		var c = new[] { new Vector3(a, a, a), new Vector3(b, a, a), new Vector3(b, a, b), new Vector3(a, a, b), new Vector3(a, b, a), new Vector3(b, b, a), new Vector3(b, b, b), new Vector3(a, b, b) };
		int[] e = { 0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
		foreach (int i in e) im.SurfaceAddVertex(c[i]);
		im.SurfaceEnd();
		return new MeshInstance3D { Mesh = im, MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0, 0, 0, 0.7f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha }, Visible = false };
	}
	void UpdateOutline()
	{
		var h = Locked ? player.Raycast() : null;
		outline.Visible = h != null;
		if (h is not { } hit) return;
		Vector3 pos = new(hit.X, hit.Y, hit.Z), size = Vector3.One;
		if (hit.Sub >= 0) { pos += new Vector3((hit.Sub & 1) * 0.5f, ((hit.Sub >> 1) & 1) * 0.5f, (hit.Sub >> 2) * 0.5f); size = new Vector3(0.5f, 0.5f, 0.5f); }
		else if (Blocks.IsTorch(hit.Id)) { var bx = ChunkMesher.TorchGeo(hit.Id).Box; pos += new Vector3(bx[0], bx[1], bx[2]); size = new Vector3(bx[3] - bx[0], bx[4] - bx[1], bx[5] - bx[2]); }
		else if (Blocks.B[hit.Id].Box is { } bb) { pos += new Vector3(bb[0], bb[1], bb[2]); size = new Vector3(bb[3] - bb[0], bb[4] - bb[1], bb[5] - bb[2]); }
		outline.Transform = new Transform3D(Basis.Identity.Scaled(size), pos);
	}


	// ---- Ever Torches give off little fire essence sparks (your fire_essence picture) that drift up and fade ----
	const float SparkLife = 1.6f, SparkEvery = 0.35f, SparkSize = 0.14f, SparkRange = 40; const int SparkMax = 300;
	readonly List<(long i, int x, int y, int z, float[] t)> everNear = new();
	readonly List<(Sprite3D s, float age, Vector3 v)> sparks = new();
	float everScan; Texture2D sparkTex;
	void TickEverSparks(float dt)
	{
		if ((everScan -= dt) <= 0)
		{
			everScan = 1; everNear.Clear();
			foreach (long i in world.Torches)
			{
				if (Blocks.B[world.Blocks[i]].Burn != 3) continue;
				var (x, y, z) = World.FromIdx(i);
				if (MathF.Abs(x - player.Pos.X) < SparkRange && MathF.Abs(z - player.Pos.Z) < SparkRange && MathF.Abs(y - player.Pos.Y) < SparkRange)
					everNear.Add((i, x, y, z, new[] { (float)rnd.NextDouble() * SparkEvery }));
			}
		}
		sparkTex ??= ImageTexture.CreateFromImage(Atlas.Icon(Blocks.FireEssence));
		foreach (var e in everNear)
		{
			int id = world.Blocks[e.i];
			if (id == 0 || Blocks.B[id].Burn != 3 || !Blocks.IsTorch(id)) continue;   // (broken since the last look)
			if ((e.t[0] -= dt) > 0 || sparks.Count >= SparkMax) continue;
			e.t[0] += SparkEvery * (0.6f + (float)rnd.NextDouble() * 0.8f);
			var box = ChunkMesher.TorchGeo(id).Box;   // spark from the top of the torch(es)
			var s = new Sprite3D
			{
				Texture = sparkTex, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
				PixelSize = SparkSize / Atlas.TILE, Shaded = false, AlphaCut = SpriteBase3D.AlphaCutMode.Disabled, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				Position = new Vector3(e.x + (box[0] + box[3]) / 2 + ((float)rnd.NextDouble() - 0.5f) * 0.2f, e.y + box[4], e.z + (box[2] + box[5]) / 2 + ((float)rnd.NextDouble() - 0.5f) * 0.2f),
			};
			AddChild(s);
			sparks.Add((s, 0, new Vector3(((float)rnd.NextDouble() - 0.5f) * 0.2f, 0.5f + (float)rnd.NextDouble() * 0.3f, ((float)rnd.NextDouble() - 0.5f) * 0.2f)));
		}
		for (int k = sparks.Count - 1; k >= 0; k--)
		{
			var (s, age, v) = sparks[k];
			age += dt;
			if (age >= SparkLife) { s.QueueFree(); sparks.RemoveAt(k); continue; }
			sparks[k] = (s, age, v);
			s.Position += v * dt;
			float f = age / SparkLife;
			s.Modulate = new Color(1, 1, 1, 1 - f);
			s.PixelSize = SparkSize * (1 - 0.5f * f) / Atlas.TILE;
		}
	}

	double lastSpace;
	string lastMouseNote; int clickWait = 60, clickStep;
	// Looking around with the mouse. Godot's own "captured" mouse gets its movements a special way (raw input) that
	// doesn't reach the game on some PCs, so instead the pointer is hidden and kept inside the window, and each frame
	// the game sees how far it moved from the middle of the window, turns you by that much, and puts it back.
	public const Input.MouseModeEnum LockMode = Input.MouseModeEnum.ConfinedHidden;
	// Playing (Locked): looking around, clicks break and place, the pause screen away. Lock() / Unlock() go in and out of it.
	// The real pointer is only held (hidden, kept in the window, put back in the middle) while you're playing AND the window
	// has the focus: losing the focus lets it go (see _Notification), and a click back in the game takes it again.
	// A test run (the game started with any arguments: normal play has none) never holds, hides, clips or moves the real
	// pointer at all, and its window doesn't take the focus: "playing" is just this flag, and a test that needs the mouse
	// feeds the game made-up mouse events (Input.ParseInputEvent: --clicks, --mousemove).
	public static readonly bool TestRun = OS.GetCmdlineUserArgs().Length > 0;
	bool playLock, mouseHeld; Vector2 testMoved;
	public bool Locked => playLock;
	void Lock() { playLock = true; HoldMouse(); }
	void Unlock() { playLock = false; ReleaseMouse(); }
	void HoldMouse() { if (TestRun || !playLock || !GetWindow().HasFocus()) return; Input.MouseMode = LockMode; mouseHeld = true; }
	void ReleaseMouse() { if (TestRun && Input.MouseMode == Input.MouseModeEnum.Visible) return; Input.MouseMode = Input.MouseModeEnum.Visible; mouseHeld = false; lookPrimed = false; }
	// (test runs: the made-up mouse events carry this device number; the real mouse's don't count, so moving or clicking it
	//  over a test window does nothing to the test)
	public const int TestDevice = 7357;
	public override void _Input(InputEvent e) { if (TestRun && playLock && e is InputEventMouseMotion { Device: TestDevice } mm) testMoved += mm.Relative; }
	// the left button held down (to keep breaking): the real one, never in a test run
	bool LeftDown => !TestRun && Input.IsMouseButtonPressed(MouseButton.Left);
	bool lookPrimed;
	void MouseLook()
	{
		string note = !playLock ? "free" : TestRun ? "playing (test run: the real pointer is left alone)" : mouseHeld ? "playing, held" : "playing, not held (no focus)";
		if (note != lastMouseNote) { lastMouseNote = note; GD.Print($"mouse: {note}"); }   // (a note in the game's log)
		if (TestRun)
		{
			if (playLock) { player.Yaw -= testMoved.X * MouseSens; player.Pitch = Math.Clamp(player.Pitch - testMoved.Y * MouseSens, -1.55f, 1.55f); }
			testMoved = Vector2.Zero;
			return;
		}
		if (mouseHeld && !GetWindow().HasFocus()) ReleaseMouse();   // (in case the focus went without a notification)
		if (!playLock || !mouseHeld) { lookPrimed = false; return; }
		var middle = DisplayServer.WindowGetSize() / 2;
		var at = DisplayServer.MouseGetPosition() - DisplayServer.WindowGetPosition();
		if (lookPrimed)
		{
			var moved = at - middle;
			player.Yaw -= moved.X * MouseSens;
			player.Pitch = Math.Clamp(player.Pitch - moved.Y * MouseSens, -1.55f, 1.55f);
		}
		if (at != middle) DisplayServer.WarpMouse(middle);
		lookPrimed = true;
	}
	bool gearShown;   // (test: --gear)
	public override void _UnhandledInput(InputEvent e)
	{
		if (skinPanel?.Visible == true) { SkinKey(e); return; }   // (the skin editor has keys of its own)
		if (!ready) return;
		if (TestRun && e is InputEventMouse && e.Device != TestDevice) return;   // (a test run: the real mouse does nothing)
		bool locked = Locked;
		if (e is InputEventMouseMotion && locked) { }   // (looking around is done in MouseLook, every frame)
		else if (e is InputEventMouseButton mb && mb.Pressed)
		{
			if (invOpen || menuOpen || pauseMenu.Visible || dead) return;
			if (!locked) { Lock(); return; }
			if (!TestRun && !mouseHeld) { HoldMouse(); return; }   // (back from another window: this click just takes the pointer again)
			if (mb.ButtonIndex == MouseButton.Left) { LeftClick(); holdSwing = SwingTime; }
			else if (mb.ButtonIndex == MouseButton.Right) { inv.Act = ActSlot(mb.ButtonIndex); SwingHand(); try { Place(); } finally { inv.Act = -1; } }
			else if (mb.ButtonIndex == MouseButton.WheelDown) SelectSlot(inv.Selected + 1);
			else if (mb.ButtonIndex == MouseButton.WheelUp) SelectSlot(inv.Selected - 1);
			else if (mb.ButtonIndex == MouseButton.Middle) PickBlock();
		}
		else if (e is InputEventKey k && k.Pressed && !k.Echo)
		{
			if (k.PhysicalKeycode == Key.Escape)
			{   // Esc: closes whatever's open; while playing, pauses
				if (confirm.Visible) return;
				if (settingsPanel.Visible || worldsPanel.Visible || whatsNewPanel.Visible || multiPanel.Visible) { settingsPanel.Visible = whatsNewPanel.Visible = multiPanel.Visible = false; ShowWorlds(false); }
				else if (howPanel?.Visible == true) howPanel.Visible = false;
				else if (invOpen) CloseInventory();
				else if (pauseMenu.Visible) Resume();
				else if (!menuOpen) Unlock();
				return;
			}
			if (menuOpen || pauseMenu.Visible || AnyPanel || dead) return;
			if (k.PhysicalKeycode == Key.M) { settings.Muted = !settings.Muted; ApplySettings(); ShowNote(settings.Muted ? "Sound off" : "Sound on"); }
			else if (k.PhysicalKeycode == Key.T && locked) sky.Clock += Nums.N.CLOCK.skip;   // skip ahead 1/8 of a day (5 minutes)
			else if (k.PhysicalKeycode == Key.F && locked && inv.Survival) SwapOffhand();   // survival: swap the offhand item with your hand
			else if (k.PhysicalKeycode == Key.F && locked) { player.Flying = !player.Flying; player.Vel.Y = 0; }
			else if (k.PhysicalKeycode == Key.R && invOpen) { if (tipSlot != null && GodotObject.IsInstanceValid(tipSlot) && tipSlot.IsVisibleInTree() && tipSlot.ShownId != 0) ShowHowTo(tipSlot.ShownId); }   // R over an item: how to make it
			else if (k.PhysicalKeycode == Key.R && inv.Survival) CycleOffhand();             // the next offhand slot
			else if (k.PhysicalKeycode == Key.Q && inv.Survival) DropFromSlot(invOpen ? HoveredSlot() : (locked ? inv.Selected : -1), k.CtrlPressed || k.ShiftPressed);   // Q drops one, Ctrl+Q (or Shift+Q) the stack
			else if (k.PhysicalKeycode == Key.E) { if (invOpen) CloseInventory(); else OpenPanel(invPanel); }
			else if (k.PhysicalKeycode >= Key.Key1 && k.PhysicalKeycode <= Key.Key9) SelectSlot((int)(k.PhysicalKeycode - Key.Key1));
			else if (k.PhysicalKeycode == Key.Key0) SelectSlot(9);
			else if (k.PhysicalKeycode == Key.Space && locked && !inv.Survival)
			{
				// double-tap space to fly
				double now = Time.GetTicksMsec() / 1000.0;
				if (now - lastSpace < 0.3) { player.Flying = !player.Flying; player.Vel.Y = 0; lastSpace = 0; }
				else lastSpace = now;
			}
		}
	}
}
