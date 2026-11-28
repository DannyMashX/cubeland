using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Godot;

namespace Cubeland;

// Moving the game's tables into the shared data files (data/*.json), one table at a time:
//  --keydump=PATH: every block and item id with its key (the name the data files use for it), one "id<TAB>key" a line,
//    to compare with the web version's (they have to match before any file uses keys).
//  --atlasdump=PATH: every atlas picture by name with a fingerprint of its pixels (to compare before and after a change), and PATH.order: their atlas order.
//  --blockdump=PATH: every block and item with every field (pictures by name), to compare before and after a change.
//  --datacheck: each table built from its data file, compared field by field with the one today's code builds.
//    Any field a file has that the game doesn't read counts as a difference too (fields starting with _ are notes).
//    "files" flags a data/*.json the game doesn't read.
//    (--datashow also prints the smelting, recipe, creative and mob tables the game uses; --dataplant puts mistakes in
//    smelting.json, recipes.json, creative.json, mobs.json, texture-names.json, blocks.json and worldgen.json as they're read, not on disk
//    (2, 4, 5, 5, 5, 67 and 14; blocks: 6 in BASE, 49 in the family tables, 3 in DOORS, 8 in the fields that were lists and one in the tables' order), and pretends there's a
//    data/stray.json, all of which the check has to find)
public partial class Main
{
	// runs at startup when asked; true if the game should stop there
	bool DataTools()
	{
		var args = OS.GetCmdlineUserArgs();
		bool any = false;
		if (args.FirstOrDefault(a => a.StartsWith("--keydump=")) is { } kd) { KeyDump(kd[10..]); any = true; }
		if (args.FirstOrDefault(a => a.StartsWith("--atlasdump=")) is { } ad) { AtlasDump(ad[12..]); any = true; }
		if (args.FirstOrDefault(a => a.StartsWith("--blockdump=")) is { } bd) { BlockDump(bd[12..]); any = true; }
		if (args.FirstOrDefault(a => a.StartsWith("--listdump=")) is { } ld) { ListDump(ld[11..]); any = true; }
		if (args.Contains("--datacheck")) { DataCheck(); any = true; }
		if (args.FirstOrDefault(a => a.StartsWith("--resave=")) is { } rs) { Resave(rs[9..]); any = true; }
		if (args.Contains("--packtest")) { PackTest(); any = true; }
		if (args.Contains("--clocktest")) { Sky.ClockTest(); any = true; }
		return any;
	}
	// (Beta 0.1.6.1) the place the sky is drawn for: yours (in an Earthlike world the seasons reach it by its distance from the equator)
	bool clockArgDone;
	void SetSkyPlace() { var (k, south) = Sky.PlaceOf(world != null && world.Lazy, player?.Pos.Z ?? 0, World.WZ); sky.PlaceK = k; sky.PlaceSouth = south; sky.Seed = world != null ? unchecked((int)world.Gen.Seed) : 0; }

	// (testing) pieces packed as block + 8192 x group, and saves' old packing (block + 4096 x group, or "Pg": 8192 only when a
	// piece's block is 4096 or more): see World.GroupMul
	static void PackTest()
	{
		static void Check(string what, bool ok) => GD.Print($"{(ok ? "PASS" : "FAIL")} {what}");
		int v1 = 3 + World.GroupMul * 5, v2 = 4403 + World.GroupMul * 2;
		Check("an old block's spot packs the old way and back", World.SpotOut(v1) == 3 + 4096 * 5 && World.SpotIn(World.SpotOut(v1), World.OldGroupMul) == v1);
		Check("a 0.1.5.0 spot (block + 4096 x group) reads as block + 8192 x group", World.SpotIn(12 + 4096 * 15, 4096) == 12 + 8192 * 15 && World.SpotIn(0, 4096) == 0 && World.SpotIn(7, 4096) == 7);
		Check("a spot packed with pg 8192 reads as it is", World.SpotIn(v2, 8192) == v2 && World.SpotBlock(v2) == 4403 && v2 / World.GroupMul == 2);
		Check("pieces of today's blocks don't need pg", !World.PiecesNeedWide(new[] { new[] { v1, 0, 0, 0, 0, 0, 0, 3 } }));
		Check("a piece of Granite (4403) needs pg 8192", World.PiecesNeedWide(new[] { new[] { 0, v2, 0, 0, 0, 0, 0, 0 } }));
		var opts = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };   // (as Worlds saves them)
		string plain = System.Text.Json.JsonSerializer.Serialize(new Worlds.Save(), opts), wide = System.Text.Json.JsonSerializer.Serialize(new Worlds.Save { Pg = 8192 }, opts);
		Check("a save without wide pieces has no Pg (byte for byte as before)", !plain.Contains("\"Pg\"") && plain == wide.Replace(",\"Pg\":8192", ""));
		Check("a save with wide pieces says Pg 8192", wide.Contains("\"Pg\":8192") && System.Text.Json.JsonSerializer.Deserialize<Worlds.Save>(wide, opts).Pg == 8192 && System.Text.Json.JsonSerializer.Deserialize<Worlds.Save>(plain, opts).Pg == 0);
		Check("Earthlike's blocks are hidden until it's ready, then all show (but the new rocks and gravels show already, since Beta 0.1.5.2)", Blocks.Shown(3935) == WorldGen.EarthlikeOn && Blocks.Shown(4480) == WorldGen.EarthlikeOn && Blocks.Shown(3) && Blocks.Items.Contains(3935) == WorldGen.EarthlikeOn && Blocks.Items.Contains(3) && Blocks.Items.Contains(4800) == WorldGen.EarthlikeOn && Blocks.ItemRows.Contains(726) == WorldGen.EarthlikeOn && Blocks.ItemRows.Contains(4050) == WorldGen.EarthlikeOn && Blocks.Shown(4010) == WorldGen.EarthlikeOn && Blocks.Shown(4403) && Blocks.Items.Contains(4403) && Blocks.Items.Contains(4766) && Blocks.Items.Contains(5030) && Blocks.Shown(4521) && Blocks.Shown(4471));
		Check("Earthlike's soils for planting (Beta 0.1.5.3): 4800-4809 bare (like Dirt), 4810-4819 their grassy tops (like Grass), 4814 unused", Enumerable.Range(4800, 20).All(i => i == 4814 ? Blocks.B[i] == null && !Blocks.PlantsLikeGrass(i) && !Blocks.PlantsLikeDirt(i) : i < 4810 ? Blocks.B[i] is { Earthlike: true } bd && !bd.Name.StartsWith("Grassy") && Blocks.PlantsLikeDirt(i) && !Blocks.PlantsLikeGrass(i) : Blocks.B[i] is { } gd && Blocks.B[i - 10] is { } pd && gd.Name == "Grassy " + pd.Name && Blocks.PlantsLikeGrass(i) && !Blocks.PlantsLikeDirt(i)) && Blocks.B[4815].Name == "Grassy Clay Soil" && Blocks.B[4805].Name == "Clay Soil");
		Check("Earthlike's keys", DataKeys.KeyOf(4480) == "copper_ore#in=granite" && Blocks.B[4480].Name == "Copper Ore in Granite" && DataKeys.KeyOf(3947) == "tall_grass#top" && DataKeys.KeyOf(3891) == "water#flow=0,pace=0" && DataKeys.KeyOf(3915) == "water#flow=8,pace=2" && DataKeys.KeyOf(3716) == "corn#stage=2,top");
		Check("the new gravels fall, and take the tint where they should", Falling.Falls.Contains(5030) && Falling.Falls.Contains(Blocks.Gravel) && !Falling.Falls.Contains(4403) && Blocks.TakesBiomeTint(4810) && Blocks.TakesBiomeTint(3945) && !Blocks.TakesBiomeTint(4800));
	}

	// (testing) every saved world in a folder (a copy: never the real worlds folder) read and written back into its out/ folder,
	// with a fingerprint of each, to compare before and after a change to the save's types
	static void Resave(string dir)
	{
		var outDir = System.IO.Path.Combine(dir, "out"); System.IO.Directory.CreateDirectory(outDir);
		foreach (var f in System.IO.Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
		{
			var name = System.IO.Path.GetFileName(f);
			if (name == "worlds.json") continue;
			string text = System.IO.File.ReadAllText(f), again = Worlds.Reserialize(text);
			System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, name), again);
			uint h = 2166136261; foreach (char c in again) h = (h ^ c) * 16777619;
			GD.Print($"RESAVE {name}: {again.Length} letters, {h:x8}, {(again == text ? "the same as the file" : "differs from the file (fields added since it was saved)")}");
		}
	}

	// every picture the atlas holds, by name (sorted), with a fingerprint of its pixels: the same before and after a change
	// means every picture is still there and looks the same, whatever order the atlas asks for them in
	static void AtlasDump(string path)
	{
		if (Atlas.Image == null) Atlas.Build();
		var lines = new List<string>();
		for (int t = 0; t < Atlas.NT; t++)
		{
			uint h = 2166136261; foreach (byte b in Atlas.Image.GetRegion(new Rect2I(t * Atlas.TILE, 0, Atlas.TILE, Atlas.TILE)).GetData()) h = (h ^ b) * 16777619;
			lines.Add($"{Atlas.Names[t]}\t{h:x8}");
		}
		lines.Sort(StringComparer.Ordinal);
		using (var f = FileAccess.Open(path, FileAccess.ModeFlags.Write)) f.StoreString(string.Join("\n", lines) + "\n");
		// (and PATH.order: the names in atlas order, which only changes how far-off blocks blend, not what they look like up close)
		using (var o = FileAccess.Open(path + ".order", FileAccess.ModeFlags.Write)) o.StoreString(string.Join("\n", Atlas.Names) + "\n");
		GD.Print($"ATLASDUMP {lines.Count} pictures to {path} (and their order to {path}.order)");
	}

	void KeyDump(string path)
	{
		var sb = new StringBuilder();
		int n = 0;
		sb.Append("0\tair\n");
		for (int id = 1; id < Blocks.MaxId; id++)
		{
			if (Blocks.Get(id) == null) continue;
			sb.Append(id).Append('\t').Append(DataKeys.KeyOf(id)).Append('\n'); n++;
		}
		using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
		f.StoreString(sb.ToString());
		GD.Print($"KEYDUMP {n} ids to {path}");
	}

	void DataCheck()
	{
		int tables = 0, diffs = 0;
		void Table(string name, List<string> found)
		{
			tables++; diffs += found.Count;
			GD.Print($"DATACHECK {name}: {found.Count} difference{(found.Count == 1 ? "" : "s")}");
			foreach (var d in found.Take(1000)) GD.Print("  " + d);
		}
		// (each table, as it's moved, adds its check here)
		Table("keys", DataKeys.Check());
		Table("numbers", NumbersCheck());
		Table("smelting", SmeltingCheck());
		Table("recipes", RecipesCheck());
		Table("creative", CreativeCheck());
		Table("mobs", MobsCheck());
		Table("texture-names", TextureNamesCheck());
		Table("blocks", BlocksCheck());
		Table("worldgen", WorldgenCheck());
		Table("files", FilesCheck("numbers", "smelting", "recipes", "creative", "mobs", "texture-names", "blocks", "worldgen"));
		GD.Print($"DATACHECK done: {tables} table{(tables == 1 ? "" : "s")}, {diffs} difference{(diffs == 1 ? "" : "s")}");
	}
	// numbers.json against what the game does: the tables themselves, and measured through the real functions where there are some
	List<string> NumbersCheck()
	{
		var n = GameData.Read<NumbersData>("numbers"); var found = new List<string>();
		if (GameData.File("numbers") is { } nroot) UnreadFields(nroot, typeof(NumbersData), "numbers", found);
		if (n == null) { found.Add("data/numbers.json is missing"); return found; }
		void Same<T>(string what, T file, T game) { if (!Equals(file, game)) found.Add($"{what}: file {file}, game {game}"); }
		void SameDict(string what, Dictionary<string, int> file, IReadOnlyDictionary<string, int> game)
		{
			foreach (var (k, v) in game) Same($"{what}.{k}", file != null && file.TryGetValue(k, out int f) ? f : (int?)null, (int?)v);
			if (file != null) foreach (var k in file.Keys.Where(k => !game.ContainsKey(k))) found.Add($"{what}.{k}: only in the file");
		}
		void SameList<T>(string what, T[] file, T[] game) { if (file == null || !file.SequenceEqual(game)) found.Add($"{what}: file [{(file == null ? "" : string.Join(", ", file))}], game [{string.Join(", ", game)}]"); }
		Same("STACK_MAX", n.STACK_MAX, Blocks.StackMaxOf(Blocks.Stone));
		Same("SMELT_TIME", n.SMELT_TIME, Kilns.SmeltTime);
		for (int k = 0; k < 2; k++) SameList($"BURN_TIMES[{k}]", n.BURN_TIMES?.ElementAtOrDefault(k), World.BurnTimes[k]);
		Same("FARM.stageTime", n.FARM?.stageTime, World.StageTime); Same("FARM.stageHits", n.FARM?.stageHits, World.StageHits);
		Same("FARM.dryRate", n.FARM?.dryRate, World.DryRate); Same("FARM.fertRate", n.FARM?.fertRate, World.FertRate);
		Same("FARM.wetReach", n.FARM?.wetReach, World.WetReach); Same("FARM.fertHarvests", n.FARM?.fertHarvests, World.FertHarvests);
		Same("FARM.revertTime", n.FARM?.revertTime, World.RevertTime); Same("FARM.seedChance", n.FARM?.seedChance, SeedChance);
		Same("SAPLING.dropChance", n.SAPLING?.dropChance, SaplingDrop); Same("SAPLING.boneAshChance", n.SAPLING?.boneAshChance, BoneAshGrows);
		Same("SAPLING.growTime", n.SAPLING?.growTime, World.SaplingTime); Same("SAPLING.growHits", n.SAPLING?.growHits, World.SaplingHits);
		// wear: each tool and piece of armor, by what it's made of
		var dur = new Dictionary<string, int>(); var adur = new Dictionary<string, int>();
		foreach (int t in Blocks.Tools.Append(Blocks.CrudeAxe)) dur[Blocks.B[t].Material] = Inventory.MaxWear(t);
		foreach (int g in Blocks.GearItems.Where(g => Blocks.B[g].Armor >= 0)) adur[Blocks.B[g].Material] = Inventory.MaxWear(g);
		SameDict("DURABILITY", n.DURABILITY, dur); SameDict("ARMOR_DURABILITY", n.ARMOR_DURABILITY, adur);
		SameList("ARMOR_SHARE", n.ARMOR_SHARE, Inventory.ArmorShare);
		SameDict("PICK_TIER", n.PICK_TIER, Blocks.PickTier); SameDict("STRATA_PICKAXE", n.STRATA_PICKAXE, Blocks.StrataPickaxe);
		// stage breaking: a swing of each pickaxe at Stone, of each axe (and of a hand, and a pickaxe) at a log
		var ps = new Dictionary<string, int>(); var axs = new Dictionary<string, int> { ["none"] = Blocks.StagesPerSwing(Blocks.Log, 0) };
		foreach (int t in Blocks.Tools.Append(Blocks.CrudeAxe))
		{
			var d = Blocks.B[t];
			if (d.Tool == "pickaxe") { ps[d.Material] = Blocks.StagesPerSwing(Blocks.Stone, t); Same($"AXE_STAGES.none (a {d.Name} at a log)", n.AXE_STAGES?.GetValueOrDefault("none"), Blocks.StagesPerSwing(Blocks.Log, t)); }
			if (d.Tool == "axe") axs[d.Material] = Blocks.StagesPerSwing(Blocks.Log, t);
		}
		SameDict("PICK_STAGES", n.PICK_STAGES, ps); SameDict("AXE_STAGES", n.AXE_STAGES, axs);
		Same("LOG_STAGES", n.LOG_STAGES, Blocks.Stages(Blocks.Log));
		for (int layer = 1; layer <= 8; layer++) Same($"STONE_STAGES (layer {layer} Stone)", n.STONE_STAGES * layer, Blocks.Stages(layer == 1 ? Blocks.Stone : Blocks.Strata + layer - 2));
		Same("STONE_STAGES (Iron Ore)", n.STONE_STAGES, Blocks.Stages(Blocks.IronOre));
		Same("HEARTS", n.HEARTS, HEARTS);
		return found;
	}
	// smelting.json against the Kiln's and Foundry's tables: every smelt in order (with tall worlds as they are, and with the
	// deep layers' smelts in), and what burns; --datashow also prints the whole table the game uses
	List<string> SmeltingCheck()
	{
		var found = new List<string>();
		var d = GameData.Read<SmeltingData>("smelting");
		if (d == null) { found.Add("data/smelting.json is missing"); return found; }
		UnreadFields(GameData.File("smelting").Value, typeof(SmeltingData), "smelting", found);
		if (OS.GetCmdlineUserArgs().Contains("--dataplant")) { d.FUEL["peat"] = 7; d.SMELTS.First(s => s.tall).from = "cobbled_andesite#layer=3"; }   // (two mistakes the check has to find)
		string Name(int id) => $"{DataKeys.KeyOf(id)} ({id})";
		string Show(Kilns.Smelt s) => $"{Name(s.Out)} <- {string.Join(" + ", s.Needs.Select(n => $"{n.N} {Name(n.Id)}"))} at {s.At}";
		void SameSmelts(string what, Kilns.Smelt[] file, Kilns.Smelt[] game)
		{
			for (int k = 0; k < Math.Max(file.Length, game.Length); k++)
			{
				string f = k < file.Length ? Show(file[k]) : "nothing", g = k < game.Length ? Show(game[k]) : "nothing";
				if (f != g) found.Add($"{what} #{k}: file {f}, game {g}");
			}
		}
		try
		{
			SameSmelts("SMELTS", Kilns.SmeltsFrom(d, World.TallWorlds), Kilns.Smelts);
			// with tall worlds on: the same, with each deep layer's Cobblestone -> its Stone (layers 2-8) straight after Cooked Meat
			var shown = Kilns.SmeltsFrom(d, false).ToList(); int after = shown.FindIndex(s => s.Out == Blocks.CookedMeat) + 1;
			shown.InsertRange(after, Enumerable.Range(0, Blocks.StrataLayers - 1).Select(k => new Kilns.Smelt(Blocks.Strata + k, new[] { (Blocks.DeepCobble + k, 1) })));
			SameSmelts("SMELTS with the deep layers", Kilns.SmeltsFrom(d, true), shown.ToArray());
			var fuel = Kilns.FuelFrom(d);
			foreach (var (id, n) in Kilns.Fuel) if (!fuel.TryGetValue(id, out int f) || f != n) found.Add($"FUEL {Name(id)}: file {(fuel.TryGetValue(id, out int g) ? g.ToString() : "none")}, game {n}");
			foreach (int id in fuel.Keys.Where(k => !Kilns.Fuel.ContainsKey(k))) found.Add($"FUEL {Name(id)}: only in the file");
		}
		catch (InvalidOperationException x) { found.Add(x.Message); }
		if (OS.GetCmdlineUserArgs().Contains("--datashow"))
		{
			foreach (var s in Kilns.Smelts) GD.Print("  SMELT " + Show(s));
			foreach (var (id, n) in Kilns.Fuel.OrderBy(kv => kv.Key)) GD.Print($"  FUEL {Name(id)} {n}");
		}
		return found;
	}
	// recipes.json against crafting and the Cutter: every recipe, in order (with tall worlds as they are, and with the deep
	// layers' Chiseled Stone in), and the Cutter's buttons; --datashow also prints the recipes the game uses
	List<string> RecipesCheck()
	{
		var found = new List<string>();
		const string path = "res://data/recipes.json";
		if (!FileAccess.FileExists(path)) { found.Add("data/recipes.json is missing"); return found; }
		bool plant = OS.GetCmdlineUserArgs().Contains("--dataplant");
		string text = FileAccess.GetFileAsString(path);
		if (plant) { int a = text.IndexOf("\"axe\""); text = text.Remove(a, 5).Insert(a, "\"axes\""); }   // (a misspelt field)
		using var doc = JsonDocument.Parse(text);
		UnreadFields(doc.RootElement, typeof(RecipesData), "recipes", found);
		var d = doc.RootElement.Deserialize<RecipesData>(GameData.Opts);
		if (plant)
		{   // (three more mistakes the check has to find: a wrong count, a wrong deep-layer ingredient, a wrong Cutter button)
			d.RECIPES[0].n = 2;
			using var wrong = JsonDocument.Parse("\"stone#layer=3\""); d.RECIPES.First(r => r.tall).needs[0][0] = wrong.RootElement.Clone();
			d.CUTTER[3].uses = 2;
		}
		string Name(int id) => $"{DataKeys.KeyOf(id)} ({id})";
		string Show(Inventory.Recipe r) => $"{r.N} {Name(r.Out)} <- {string.Join(" + ", r.Needs.Select(n => $"{n.n} {Name(n.id)}"))} at {r.At}"
			+ (r.Axe ? ", axe" : "") + (r.Tool != null ? $", {r.Tool}" : "") + (r.Colour ? ", colour" : "");
		void SameRecipes(string what, List<Inventory.Recipe> file, List<Inventory.Recipe> game)
		{
			var fs = file.Select(Show).ToList(); var gs = game.Select(Show).ToList();
			foreach (var g in gs.GroupBy(x => x)) { int nf = fs.Count(x => x == g.Key); if (nf != g.Count()) found.Add($"{what}: [{g.Key}] the game has {g.Count()}, the file {nf}"); }
			foreach (var x in fs.Distinct().Where(x => !gs.Contains(x))) found.Add($"{what}: [{x}] only in the file");
			if (fs.Count != gs.Count || fs.SequenceEqual(gs) || !fs.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(gs.OrderBy(x => x, StringComparer.Ordinal))) return;
			// the same recipes in another order: the ones that moved (those not in the longest run kept in the same order)
			var used = new bool[gs.Count]; var at = fs.Select(x => { int k = Enumerable.Range(0, gs.Count).First(i => !used[i] && gs[i] == x); used[k] = true; return k; }).ToArray();
			var len = new int[at.Length]; var prev = new int[at.Length];
			for (int i = 0; i < at.Length; i++) { len[i] = 1; prev[i] = -1; for (int j = 0; j < i; j++) if (at[j] < at[i] && len[j] + 1 > len[i]) { len[i] = len[j] + 1; prev[i] = j; } }
			var keep = new HashSet<int>(); for (int i = Array.IndexOf(len, len.Max()); i >= 0; i = prev[i]) keep.Add(i);
			for (int i = 0; i < at.Length; i++) if (!keep.Contains(i)) found.Add($"{what} order: [{fs[i]}] is #{i} in the file, #{at[i]} in the game");
		}
		try
		{
			SameRecipes("RECIPES", Inventory.RecipesFrom(d, World.TallWorlds), Inventory.Recipes);
			// with tall worlds on: the same, with each deep layer's Stone -> its Chiseled Stone (layers 2-8, at the bench, with a
			// pickaxe, listed only while carrying it) straight after the dye mixes, before the coloured Terracotta
			var shown = Inventory.RecipesFrom(d, false); int before = shown.FindIndex(r => r.Out == Blocks.ColourTerracotta[0]);
			shown.InsertRange(before, Enumerable.Range(0, Blocks.StrataLayers - 1).Select(k => new Inventory.Recipe { Out = Blocks.DeepChiseled + k, N = 1, Needs = new[] { (Blocks.Strata + k, 1) }, At = "bench", Tool = "pickaxe", Colour = true }));
			SameRecipes("RECIPES with the deep layers", Inventory.RecipesFrom(d, true), shown);
			var tools = Blocks.B.Where(b => b?.Tool != null).Select(b => b.Tool).ToHashSet();
			foreach (var r in d.RECIPES.Where(r => r.tool != null && !tools.Contains(r.tool))) found.Add($"RECIPES: {r.@out} needs a \"{r.tool}\", which isn't a tool");
			var cut = Inventory.CutterFrom(d);
			for (int k = 0; k < Math.Max(cut.Length, Inventory.BenchModes.Length); k++)
			{
				string f = k < cut.Length ? $"{cut[k].key} \"{cut[k].name}\" {cut[k].use} -> {cut[k].n} (items from {cut[k].baseId})" : "nothing";
				var m = k < Inventory.BenchModes.Length ? Inventory.BenchModes[k] : default;
				string g = k < Inventory.BenchModes.Length ? $"{m.key} \"{m.name}\" {m.use} -> {m.n} (items from {m.baseId})" : "nothing";
				if (f != g) found.Add($"CUTTER #{k}: file {f}, game {g}");
			}
			foreach (var c in cut.Where(c => c.key == "pieces" && c.n != 8)) found.Add($"CUTTER pieces makes {c.n}: it's always 8 (a block's 8 spots)");
		}
		catch (InvalidOperationException x) { found.Add(x.Message); }
		if (OS.GetCmdlineUserArgs().Contains("--datashow")) foreach (var r in Inventory.Recipes) GD.Print("  RECIPE " + Show(r));
		return found;
	}

	// creative.json against the creative inventory: every tab and what's in it, in order (with tall worlds as they are, and
	// with the deep layers in); the tall groups hold every deep layer's block and ore once and nothing else; nothing is in
	// two places; and everything the creative inventory has, and every item, is in a tab (not just left over for Building)
	List<string> CreativeCheck()
	{
		var found = new List<string>();
		if (GameData.File("creative") is not { } root) { found.Add("data/creative.json is missing"); return found; }
		UnreadFields(root, typeof(CreativeData), "creative", found);
		var d = root.Deserialize<CreativeData>(GameData.Opts);
		JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
		CreativeTab Tab(string name) => d.TABS.First(t => t.name == name);
		if (OS.GetCmdlineUserArgs().Contains("--dataplant"))
		{   // (five mistakes the check has to find)
			var w = Tab("Wood").items; (w[0], w[1]) = (w[1], w[0]);                                   // two Wood items swapped
			Tab("Natural").items = Tab("Natural").items.Where(e => e.ValueKind != JsonValueKind.String || e.GetString() != "bedrock").ToArray();   // bedrock left out
			Tab("Machines").items = Tab("Machines").items.Append(Json("\"glass\"")).ToArray();           // glass in Machines too
			var nat = Tab("Natural").items; int g = Array.FindIndex(nat, e => e.ValueKind == JsonValueKind.Object);
			nat[g] = Json(nat[g].GetRawText().TrimEnd().TrimEnd('}').TrimEnd().TrimEnd(']') + ", \"sand\"] }");   // sand in a tall group
			Tab("Food").items = Tab("Food").items.Where(e => e.GetString() != "chocolate").ToArray();     // chocolate left out
		}
		string Name(int id) => $"{DataKeys.KeyOf(id)} ({id})";
		void SameTabs(string what, (string name, int[] ids)[] file, (string name, int[] ids)[] game)
		{
			for (int k = 0; k < Math.Max(file.Length, game.Length); k++)
			{
				if (k >= file.Length || k >= game.Length) { found.Add($"{what} #{k}: file {(k < file.Length ? file[k].name : "nothing")}, game {(k < game.Length ? game[k].name : "nothing")}"); continue; }
				var (f, g) = (file[k], game[k]);
				if (f.name != g.name) found.Add($"{what} #{k}: file \"{f.name}\", game \"{g.name}\"");
				if (f.ids.SequenceEqual(g.ids)) continue;
				int at = Enumerable.Range(0, Math.Min(f.ids.Length, g.ids.Length)).FirstOrDefault(i => f.ids[i] != g.ids[i], Math.Min(f.ids.Length, g.ids.Length));
				found.Add($"{what} {g.name}: file {f.ids.Length}, game {g.ids.Length}; first different at #{at}: file {(at < f.ids.Length ? Name(f.ids[at]) : "nothing")}, game {(at < g.ids.Length ? Name(g.ids[at]) : "nothing")}");
				foreach (int id in f.ids.Except(g.ids)) found.Add($"{what} {g.name}: {Name(id)} only in the file");
				foreach (int id in g.ids.Except(f.ids)) found.Add($"{what} {g.name}: {Name(id)} only in the game");
			}
		}
		try
		{
			SameTabs("TABS", Main.TabsFrom(d, World.TallWorlds), CreativeTabs.Select(t => (t.name, t.ids)).ToArray());
			// with tall worlds on: the same, with each deep layer's Stone, Cobblestone, Stone Bricks and Chiseled Stone straight after
			// its own, and every deep ore (in order) straight after Tin Ore
			var deepAfter = new Dictionary<int, int[]> { [Blocks.Stone] = Blocks.DeepRow(3), [Blocks.Cobblestone] = Blocks.DeepRow(8), [Blocks.StoneBricks] = Blocks.DeepRow(9), [69] = Blocks.DeepRow(69), [Blocks.Ores[^1]] = Blocks.DeepOres };
			SameTabs("TABS with the deep layers", Main.TabsFrom(d, true), Main.TabsFrom(d, false).Select(t => (t.name, t.name == "Shapes" ? Main.ShapesIn(Tab("Shapes").forms, true)
				: t.ids.SelectMany(i => deepAfter.TryGetValue(i, out var row) ? row.Prepend(i) : new[] { i }).ToArray())).ToArray());
			// the tall groups: every deep layer's block and ore, once; nothing else; no deep one outside them
			var deep = Blocks.DeepBlocks.Concat(Blocks.DeepOres).ToHashSet();
			var grouped = new List<int>(); var outside = new List<int>();
			foreach (var t in d.TABS.Where(t => t.items != null)) foreach (var e in t.items) (e.ValueKind == JsonValueKind.Object ? grouped : outside).AddRange(Main.TabEntry(e, true));
			foreach (int id in deep) { int c = grouped.Count(x => x == id); if (c != 1) found.Add($"{Name(id)} is in the tall groups {c} times (it should be once)"); }
			foreach (int id in grouped.Where(x => !deep.Contains(x)).Distinct()) found.Add($"{Name(id)} is in a tall group, but it isn't a deep layer's block or ore");
			foreach (int id in outside.Where(deep.Contains).Distinct()) found.Add($"{Name(id)} is a deep layer's, but it isn't in a tall group");
			// each thing in one place
			var listed = d.TABS.SelectMany(t => t.forms != null ? Main.ShapesIn(t.forms, true) : t.items.SelectMany(e => Main.TabEntry(e, true))).ToList();
			foreach (var g in listed.GroupBy(x => x).Where(g => g.Count() > 1)) found.Add($"{Name(g.Key)} is in {g.Count()} places");
			// everything in a tab
			var inTabs = listed.ToHashSet();
			foreach (int id in AllCreative().Where(i => !inTabs.Contains(i))) found.Add($"{Name(id)} is in the creative inventory but in no tab");
			for (int id = 1; id < Blocks.MaxId; id++)
				if (Blocks.Get(id) is { Item: true } b && b.Piece == 0 && b.Form == null && !inTabs.Contains(id) && Blocks.Shown(id)) found.Add($"{Name(id)} is an item, but in no tab (creative wouldn't have it)");
		}
		catch (InvalidOperationException x) { found.Add(x.Message); }
		if (OS.GetCmdlineUserArgs().Contains("--datashow"))
			foreach (var (name, ids) in CreativeTabs) GD.Print($"  TAB {name} ({ids.Length}): " + string.Join(" ", ids.Select(i => DataKeys.KeyOf(i))));
		return found;
	}

	// mobs.json against the mobs: every mob, field by field, and the spawning order (with the Zombie, and with Grr Angry);
	// every drop is a real thing (Grr Angry's too); both night mobs are there; and the layer boost, Gloomling fade and cow numbers
	List<string> MobsCheck()
	{
		var found = new List<string>();
		const string path = "res://data/mobs.json";
		if (!FileAccess.FileExists(path)) { found.Add("data/mobs.json is missing"); return found; }
		bool plant = OS.GetCmdlineUserArgs().Contains("--dataplant");
		string text = FileAccess.GetFileAsString(path);
		if (plant) text = text.Replace("\"snuffer\": true", "\"snuffer\": true, \"glows\": true").Replace("\"LAYER_BOOST\"", "\"SPAWN\": 1, \"LAYER_BOOST\"");   // (a misspelt field, an unknown table)
		using var doc = JsonDocument.Parse(text);
		UnreadFields(doc.RootElement, typeof(MobsData), "mobs", found);
		var d = doc.RootElement.Deserialize<MobsData>(GameData.Opts);
		if (plant)
		{   // (three more mistakes the check has to find)
			d.MOBS.First(m => m.type == "zombie").damage = 4; d.COW.calfTime = 500;
			using var wrong = JsonDocument.Parse("\"fire_esence\""); d.MOBS.First(m => m.type == "grrangry").drops[0][0] = wrong.RootElement.Clone();
		}
		string Val(object v) => v is (int, int, int, float)[] drops ? "[" + string.Join(" ", drops.Select(x => $"{DataKeys.KeyOf(x.Item1)} {x.Item2}-{x.Item3} {x.Item4.ToString(System.Globalization.CultureInfo.InvariantCulture)}")) + "]"
			: Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
		void SameMobs(string what, Dictionary<string, MobType> file, Dictionary<string, MobType> game)
		{
			foreach (var (k, g) in game)
			{
				if (!file.TryGetValue(k, out var m)) { found.Add($"{what}: {k} isn't in the file"); continue; }
				foreach (var fi in typeof(MobType).GetFields()) if (Val(fi.GetValue(m)) != Val(fi.GetValue(g))) found.Add($"{what} {k}.{fi.Name}: file {Val(fi.GetValue(m))}, game {Val(fi.GetValue(g))}");
			}
			foreach (var k in file.Keys.Where(k => !game.ContainsKey(k))) found.Add($"{what}: {k} is only in the file");
			if (!file.Keys.SequenceEqual(game.Keys)) found.Add($"{what} spawning order: file {string.Join(", ", file.Keys)}; game {string.Join(", ", game.Keys)}");
		}
		void Same<T>(string what, T file, T game) { if (!Equals(file, game)) found.Add($"{what}: file {file}, game {game}"); }
		// every drop is a real thing, Grr Angry's too (nothing else looks at his while the Zombie is in the game)
		foreach (var m in d.MOBS) foreach (var dr in m.drops ?? Array.Empty<JsonElement[]>()) try { Drop(dr); } catch (InvalidOperationException x) { found.Add($"{m.type}'s drops: {x.Message}"); }
		foreach (var t in new[] { "zombie", "grrangry" }) if (!d.MOBS.Any(m => m.type == t)) found.Add($"MOBS: no {t} (both night mobs have to be there; ZombieReady picks one)");
		foreach (var g in d.MOBS.GroupBy(m => m.type).Where(g => g.Count() > 1)) found.Add($"MOBS: {g.Key} is there {g.Count()} times");
		try
		{
			SameMobs("MOBS", MobTypesFrom(d, ZombieReady), MobTypes);
			// the other night mob instead: the same mobs in the same order, with it in place of this one
			string now = ZombieReady ? "zombie" : "grrangry", other = ZombieReady ? "grrangry" : "zombie";
			var swapped = MobTypesFrom(d, !ZombieReady).Keys.ToList(); var want = MobTypes.Keys.Select(k => k == now ? other : k).ToList();
			if (!swapped.SequenceEqual(want)) found.Add($"MOBS spawning order with {other}: file {string.Join(", ", swapped)}; it should be {string.Join(", ", want)}");
		}
		catch (InvalidOperationException x) { found.Add(x.Message); }
		Same("LAYER_BOOST", d.LAYER_BOOST, MobLayerBoost);
		Same("GLOOM_FADE.range", d.GLOOM_FADE?.range, GloomFadeRange); Same("GLOOM_FADE.time", d.GLOOM_FADE?.time, GloomFadeTime);
		var c = d.COW ?? new CowNumbers();
		Same("COW.perChunk", c.perChunk, CowsPerChunk); Same("COW.crowdedAt", c.crowdedAt, CrowdedAt); Same("COW.fatAt", c.fatAt, FatAt); Same("COW.fedMax", c.fedMax, FedMax);
		Same("COW.happyMax", c.happyMax, CowHappyMax); Same("COW.grazeHappy", c.grazeHappy, GrazeHappy); Same("COW.grainHappy", c.grainHappy, GrainHappy);
		Same("COW.grazeBelow", c.grazeBelow, GrazeBelow); Same("COW.grazeTime", c.grazeTime, GrazeTime); Same("COW.mateCooldown", c.mateCooldown, MateCooldown);
		Same("COW.mateCost", c.mateCost, MateCost); Same("COW.calfTime", c.calfTime, CalfTime); Same("COW.calfGrainBoost", c.calfGrainBoost, CalfGrainBoost);
		Same("COW.nearRange", c.nearRange, NearRange); Same("COW.calfHealth", c.calfHealth, CalfHealth);
		Same("COW.regrowMin", c.regrowMin, World.RegrowMin); Same("COW.regrowMax", c.regrowMax, World.RegrowMax);
		if (OS.GetCmdlineUserArgs().Contains("--datashow"))   // (through the game's own layer boost: what a mob from layer 1 and from layer 3 has)
			foreach (var (k, t) in MobTypes) GD.Print($"  MOB {k}: health {t.Health}, layer 3 {(int)MathF.Round(t.Health * LayerBoost(3), MidpointRounding.AwayFromZero)}; damage {MobDamage(new Mob { T = t })}, layer 3 {MobDamage(new Mob { T = t, Layer = 3 })}");
		return found;
	}
	// a data/*.json the game doesn't read
	// worldgen.json against the world generator: the ore veins (in order: a row's place is in its veins' seeds) and the
	// badlands' stripes, each the same as the tables WorldGen grows with; and values that make sense: an allDepths ore has
	// deep versions (one of Blocks.Ores), per and size are whole numbers from 1, top and deep from 0, top no deeper than deep,
	// the flags true or left out. (The world fingerprints check what they grow.)
	static List<string> WorldgenCheck()
	{
		var found = new List<string>();
		const string path = "res://data/worldgen.json";
		if (!FileAccess.FileExists(path)) { found.Add("data/worldgen.json is missing"); return found; }
		var node = System.Text.Json.Nodes.JsonNode.Parse(FileAccess.GetFileAsString(path));
		if (OS.GetCmdlineUserArgs().Contains("--dataplant") && node?["ORE_VEINS"] is System.Text.Json.Nodes.JsonArray v && v.Count >= 5
			&& node["BADLANDS_BANDS"] is System.Text.Json.Nodes.JsonArray pb && pb.Count >= 4)
		{   // (ten mistakes the check has to find: two rows swapped, a number, an unknown field, allDepths on an ore with no deep
			// versions, top deeper than deep, a flag that isn't true, size 0, a band misspelt, a band missing, an unknown table)
			var (a, b) = (v[0].DeepClone(), v[1].DeepClone()); v[0] = b; v[1] = a;
			v[2]["per"] = 71; v[0]["rich"] = true; v[4]["allDepths"] = true; v[3]["top"] = 1000; v[4]["takesDirt"] = 1; v[1]["size"] = 0;
			pb[3] = "orange_terracota"; pb.RemoveAt(pb.Count - 1); node["CAVES"] = 1;
		}
		if (OS.GetCmdlineUserArgs().Contains("--dataplant") && node?["TREE_SHAPES"] is System.Text.Json.Nodes.JsonArray tsp && tsp.Count > 0
			&& node["TREE_EXTRA"] is System.Text.Json.Nodes.JsonArray tep && tep.Count > 1 && node["TREE_MIX"]?["woods"] is System.Text.Json.Nodes.JsonArray wm && wm.Count > 1)
		{   // (four more, in the trees: a log that isn't a log, a tree the code can't grow, a biome misspelt, shares over 1)
			tsp[0]["log"] = "maple_planks"; tep[0]["tree"] = "baobab"; tep[1]["biome"] = "meadows"; wm[1][1] = 0.6;
		}
		using var doc = JsonDocument.Parse(node?.ToJsonString() ?? "null");
		var root = doc.RootElement;
		if (root.ValueKind != JsonValueKind.Object) { found.Add("data/worldgen.json isn't { ORE_VEINS, BADLANDS_BANDS }"); return found; }
		UnreadFields(root, typeof(WorldgenData), "worldgen", found);
		// the values as written (one of the wrong kind couldn't be read into the game's tables at all)
		var veins = new List<(int ore, int per, int size, int top, int deep, bool takesDirt, bool allDepths)>();
		var rows = root.TryGetProperty("ORE_VEINS", out var ov) && ov.ValueKind == JsonValueKind.Array ? ov.EnumerateArray().ToArray() : null;
		if (rows == null || rows.Length == 0) found.Add("worldgen.ORE_VEINS: should be a list of veins");
		for (int k = 0; k < (rows?.Length ?? 0); k++)
		{
			var r = rows[k];
			string ore = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("ore", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null, at = $"worldgen.ORE_VEINS.{k} ({ore})";
			if (r.ValueKind != JsonValueKind.Object) { found.Add($"{at}: isn't {{ ore, per, size, top, deep }}"); continue; }
			int id = -1;
			try { id = DataKeys.IdOf(ore); } catch (InvalidOperationException) { found.Add($"{at}: isn't a block"); }
			int Num(string f, int least)
			{
				bool has = r.TryGetProperty(f, out var e);
				if (has && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int n) && n >= least) return n;
				found.Add($"{at}: {f} can't be {(has ? e.GetRawText() : "left out")} (a whole number from {least})"); return -1;
			}
			bool Flag(string f)
			{
				if (!r.TryGetProperty(f, out var e)) return false;
				if (e.ValueKind == JsonValueKind.True) return true;
				found.Add($"{at}: {f} should be true or left out, not {e.GetRawText()}"); return false;
			}
			int per = Num("per", 1), size = Num("size", 1), top = Num("top", 0), deep = Num("deep", 0);
			bool takesDirt = Flag("takesDirt"), allDepths = Flag("allDepths");
			if (top >= 0 && deep >= 0 && top > deep) found.Add($"{at}: top ({top}) is deeper than deep ({deep})");
			if (allDepths && id > 0 && Array.IndexOf(Blocks.Ores, id) < 0) found.Add($"{at}: allDepths, but it has no deep versions (it isn't one of the ores)");
			veins.Add((id, per, size, top, deep, takesDirt, allDepths));
		}
		var bands = new List<int>();
		var bandRows = root.TryGetProperty("BADLANDS_BANDS", out var bb) && bb.ValueKind == JsonValueKind.Array ? bb.EnumerateArray().ToArray() : null;
		if (bandRows == null || bandRows.Length == 0) found.Add("worldgen.BADLANDS_BANDS: should be a list of blocks");
		for (int k = 0; k < (bandRows?.Length ?? 0); k++)
		{
			string key = bandRows[k].ValueKind == JsonValueKind.String ? bandRows[k].GetString() : bandRows[k].GetRawText();
			try { bands.Add(DataKeys.IdOf(key)); } catch (InvalidOperationException) { found.Add($"worldgen.BADLANDS_BANDS.{k}: \"{key}\" isn't a block"); bands.Add(-1); }
		}
		// the same as the tables the game grows with, row by row and band by band
		string Key(int id) => id >= 0 ? DataKeys.KeyOf(id) : "?";
		string Vein((int ore, int per, int size, int top, int deep, bool takesDirt, bool allDepths) x) =>
			$"{Key(x.ore)} per {x.per} size {x.size} top {x.top} deep {x.deep}{(x.takesDirt ? " takesDirt" : "")}{(x.allDepths ? " allDepths" : "")}";
		var game = WorldGen.OreVeins;
		for (int k = 0; k < Math.Max(game.Length, veins.Count); k++)
		{
			string f = k < veins.Count ? Vein(veins[k]) : "(none)", g = k < game.Length ? Vein(game[k]) : "(none)";
			if (f != g) found.Add($"ORE_VEINS {k}: file {f}, game {g}");
		}
		if (!bands.SequenceEqual(WorldGen.BadlandsBands)) found.Add($"BADLANDS_BANDS: file [{string.Join(" ", bands.Select(Key))}], game [{string.Join(" ", WorldGen.BadlandsBands.Select(Key))}]");
		// the trees (land 3 and up): values that make sense (the game grows straight from these; the world fingerprints check
		// what they grow): each shape a tree the code grows, once, with an upright log and leaves (if any) for its wood, a trunk
		// [shortest, tallest] and the numbers its shape needs; TREE_MIX's biomes and trees, the shares adding up to 1 at most;
		// TREE_EXTRA's biomes and trees, a chance from 0 to 1, a grove [size, above]
		try
		{
			T Part<T>(string table) => root.TryGetProperty(table, out var e) ? e.Deserialize<T>(GameData.Opts) : default;   // (each table on its own: a mistake elsewhere doesn't hide these)
			var wg = new WorldgenData { TREE_PATCH = Part<int>("TREE_PATCH"), TREE_MIX = Part<Dictionary<string, JsonElement[][]>>("TREE_MIX"), TREE_EXTRA = Part<TreeExtraRow[]>("TREE_EXTRA"), TREE_SHAPES = Part<TreeShapeRow[]>("TREE_SHAPES") };
			var shapes = wg.TREE_SHAPES ?? Array.Empty<TreeShapeRow>();
			if (wg.TREE_PATCH < 1) found.Add($"worldgen.TREE_PATCH: should be a whole number from 1, not {wg.TREE_PATCH}");
			var named = new HashSet<string>();
			for (int k = 0; k < shapes.Length; k++)
			{
				var t = shapes[k]; string at = $"worldgen.TREE_SHAPES.{k} ({t.tree})";
				if (t.tree == null || !WorldGen.GrownTrees.Contains(t.tree)) found.Add($"{at}: isn't a tree the code grows ({string.Join(", ", WorldGen.GrownTrees)})");
				if (t.tree != null && !named.Add(t.tree)) found.Add($"{at}: is there twice");
				int log = -1, lv = -1;
				try { log = DataKeys.IdOf(t.log); } catch (InvalidOperationException) { found.Add($"{at}: log \"{t.log}\" isn't a block"); }
				if (log > 0 && Blocks.Get(log)?.Log != true && t.tree != "burnt") found.Add($"{at}: log {t.log} isn't an upright log (\"log\")");   // (a burnt tree's Burnt Log isn't in the log group)
				if (t.leaves != null) { try { lv = DataKeys.IdOf(t.leaves); } catch (InvalidOperationException) { found.Add($"{at}: leaves \"{t.leaves}\" isn't a block"); } }
				if (lv > 0 && !Blocks.IsLeaves(lv)) found.Add($"{at}: leaves {t.leaves} aren't leaves");
				if (t.leaves == null && t.tree is not ("dead" or "burnt")) found.Add($"{at}: needs leaves");
				if (t.trunk != null && (t.trunk.Length != 2 || t.trunk[0] < 1 || t.trunk[1] < t.trunk[0])) found.Add($"{at}: trunk should be [shortest, tallest], from 1");
				if (t.trunk == null && t.tree != "shrub") found.Add($"{at}: needs a trunk");
				var c = WorldGen.CrownOf(t);
				int crowns = t.tree is "birch" or "cherry" or "orange" or "aspen" or "prairie_oak" or "shrub" or "mahogany" or "stiltroot" or "teak" ? 2 : t.tree is "dead" or "burnt" ? 0 : t.tree == "cedar" ? -1 : 1;
				if (crowns >= 0 && (c.Length != crowns || c.Any(n => n <= 0))) found.Add($"{at}: crown should be {(crowns == 2 ? "[across, up and down]" : crowns == 1 ? "a number" : "left out")}, more than 0");
				if (t.tree == "cedar" && (t.tiers == null || t.tiers.Length == 0 || t.gap < 1)) found.Add($"{at}: needs tiers and a gap");
				if (t.tree is "palo_verde" or "dead" or "burnt" or "acacia" && (t.branch is not { Length: 2 } || t.branch[1] < t.branch[0])) found.Add($"{at}: needs branch [shortest, longest]");
				if (t.tree is "dead" or "burnt" && (t.branches is not { Length: 2 } || t.branches[1] < t.branches[0])) found.Add($"{at}: needs branches [fewest, most]");
				if (t.sparse < 0 || t.sparse >= 1) found.Add($"{at}: sparse should be from 0 to under 1");
			}
			string[] biomes = { "woods", "plains", "desert", "snowy", "swamp", "taiga", "meadow", "badlands" }, own = { "oak", "palm", "fir", "willow" };
			foreach (var (b, mix) in wg.TREE_MIX ?? new())
			{
				string at = $"worldgen.TREE_MIX.{b}";
				if (Array.IndexOf(biomes, b) < 0) found.Add($"{at}: isn't a biome ({string.Join(", ", biomes)})");
				double sum = 0;
				foreach (var e in mix)
				{
					if (e.Length != 2 || e[0].ValueKind != JsonValueKind.String || e[1].ValueKind != JsonValueKind.Number) { found.Add($"{at}: each should be [tree, share]"); continue; }
					string tr = e[0].GetString(); double sh = e[1].GetDouble(); sum += sh;
					if (!named.Contains(tr) && Array.IndexOf(own, tr) < 0) found.Add($"{at}: {tr} isn't a tree in TREE_SHAPES (or a biome's own: {string.Join(", ", own)})");
					if (sh <= 0) found.Add($"{at}: {tr}'s share should be more than 0");
				}
				if (sum > 1 + 1e-9) found.Add($"{at}: the shares add up to {sum}, more than 1");
			}
			var extra = wg.TREE_EXTRA ?? Array.Empty<TreeExtraRow>();
			for (int k = 0; k < extra.Length; k++)
			{
				var r = extra[k]; string at = $"worldgen.TREE_EXTRA.{k} ({r.tree} in {r.biome})";
				if (Array.IndexOf(biomes, r.biome) < 0) found.Add($"{at}: {r.biome} isn't a biome");
				if (r.tree == null || !named.Contains(r.tree)) found.Add($"{at}: {r.tree} isn't a tree in TREE_SHAPES");
				if (!(r.chance > 0 && r.chance <= 1)) found.Add($"{at}: chance should be more than 0, at most 1");
				if (r.grove != null && (r.grove.Length != 2 || r.grove[0] <= 0)) found.Add($"{at}: grove should be [size, above]");
				if (r.spacing < 0 || r.edge < 0 || r.coast < 0) found.Add($"{at}: spacing, edge and coast are whole numbers from 1, or left out");
			}
		}
		catch (Exception x) when (x is JsonException or InvalidOperationException or FormatException) { found.Add($"the trees in data/worldgen.json can't be read: {x.Message}"); }
		// and the game can read it
		try { root.Deserialize<WorldgenData>(GameData.Opts); } catch (Exception x) { found.Add($"the game can't read data/worldgen.json: {x.Message}"); }
		return found;
	}

	static List<string> FilesCheck(params string[] read)
	{
		var found = new List<string>();
		var names = DirAccess.GetFilesAt("res://data").Where(n => n.EndsWith(".json")).Select(n => n[..^5]).ToList();
		if (OS.GetCmdlineUserArgs().Contains("--dataplant")) names.Add("stray");   // (as if there were a data/stray.json)
		foreach (var n in names.Where(n => !read.Contains(n))) found.Add($"data/{n}.json: the game doesn't read it");
		return found;
	}

	// texture-names.json against the atlas and the font: the three tables (key order too); spellings and borrows name pictures
	// the game uses; no borrow chain goes round in a circle; each font name is one character, isn't one itself, and every letter
	// file with a long name in textures/font/ has one (or the font would leave that letter out)
	List<string> TextureNamesCheck()
	{
		var found = new List<string>();
		if (GameData.File("texture-names") is not { } root) { found.Add("data/texture-names.json is missing"); return found; }
		UnreadFields(root, typeof(TextureNamesData), "texture-names", found);
		var d = root.Deserialize<TextureNamesData>(GameData.Opts);
		d.TEXTURE_SPELLINGS ??= new(); d.TEXTURE_BORROWS ??= new(); d.FONT_NAMES ??= new();
		if (OS.GetCmdlineUserArgs().Contains("--dataplant"))
		{   // (five mistakes the check has to find)
			d.TEXTURE_SPELLINGS["grass_top"] = "grasss"; d.TEXTURE_BORROWS["red_bricks"] = "bricks_a";
			d.FONT_NAMES["dash"] = "--"; d.FONT_NAMES["a"] = "a"; d.FONT_NAMES.Remove("q_mark");
		}
		void SameMap<T>(string what, Dictionary<string, string> file, Dictionary<string, T> game, Func<T, string> show)
		{
			foreach (var (k, v) in game)
				if (!file.TryGetValue(k, out var f)) found.Add($"{what}.{k}: not in the file (the game has \"{show(v)}\")");
				else if (f != show(v)) found.Add($"{what}.{k}: file \"{f}\", game \"{show(v)}\"");
			foreach (var k in file.Keys.Where(k => !game.ContainsKey(k))) found.Add($"{what}.{k}: only in the file");
			if (file.Count == game.Count && file.Keys.All(game.ContainsKey) && !file.Keys.SequenceEqual(game.Keys)) found.Add($"{what}: the same names in another order");
		}
		SameMap("TEXTURE_SPELLINGS", d.TEXTURE_SPELLINGS, Atlas.Spellings, s => s);
		SameMap("TEXTURE_BORROWS", d.TEXTURE_BORROWS, Atlas.Borrows, s => s);
		SameMap("FONT_NAMES", d.FONT_NAMES, Skin.Names, c => c.ToString());
		// spellings and borrows are for pictures the game uses
		System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Blocks).TypeHandle);   // (every block has asked for its pictures)
		foreach (var (k, v) in d.TEXTURE_SPELLINGS) if (!Atlas.Uses(v)) found.Add($"TEXTURE_SPELLINGS.{k}: \"{v}\" isn't a picture the game uses");
		foreach (var k in d.TEXTURE_BORROWS.Keys) if (!Atlas.Uses(k)) found.Add($"TEXTURE_BORROWS.{k}: isn't a picture the game uses");
		// no circles
		var circles = new HashSet<string>();
		foreach (var start in d.TEXTURE_BORROWS.Keys)
		{
			var chain = new List<string> { start };
			for (string n = start; d.TEXTURE_BORROWS.TryGetValue(n, out var b); n = b)
			{
				int at = chain.IndexOf(b);
				if (at >= 0)
				{
					var loop = chain.Skip(at).ToList();
					if (circles.Add(string.Join(",", loop.OrderBy(x => x, StringComparer.Ordinal)))) found.Add($"TEXTURE_BORROWS: a circle: {string.Join(" -> ", loop.Append(b))}");
					break;
				}
				chain.Add(b);
			}
		}
		// font names
		foreach (var (k, v) in d.FONT_NAMES)
		{
			if (v == null || v.Length != 1) found.Add($"FONT_NAMES.{k}: \"{v}\" isn't one character");
			if (k.Length == 1) found.Add($"FONT_NAMES.{k}: a file named with one character is already that character");
		}
		const string fontRoot = "res://textures/font";
		if (DirAccess.DirExistsAbsolute(fontRoot))
			foreach (var folder in DirAccess.GetDirectoriesAt(fontRoot))
				foreach (var (name, _) in Skin.Pictures(fontRoot + "/" + folder))
					if (name.Length > 1 && !d.FONT_NAMES.ContainsKey(name)) found.Add($"textures/font/{folder}/{name}.png: no FONT_NAMES entry, so the font leaves that letter out");
		return found;
	}

	// one field of a block, as text (pictures and the icon by name, so it doesn't matter where the atlas put them)
	static string BlockVal(System.Reflection.FieldInfo fi, BlockDef d)
	{
		object v = fi.GetValue(d);
		if (fi.Name == "T") return v is int[] t ? string.Join(" ", t.Select(x => x >= 0 && x < Atlas.NT ? Atlas.Names[x] : "?")) : "";
		if (fi.Name == "Icon") return d.Icon >= 0 ? (d.Icon < Atlas.NT ? Atlas.Names[d.Icon] : "?") : "-1";
		if (fi.Name == "Full") return d.Full >= 0 ? (d.Full < Atlas.NT ? Atlas.Names[d.Full] : "?") : "-1";
		if (fi.Name == "Frames") return v is int[] fr ? string.Join(" ", fr.Select(x => x >= 0 && x < Atlas.NT ? Atlas.Names[x] : "?")) : "";
		return v switch
		{
			null => "",
			int[] a => string.Join(" ", a),
			float[] a => string.Join(" ", a.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))),
			float x => x.ToString(System.Globalization.CultureInfo.InvariantCulture),
			_ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
		};
	}
	// every block and item, every field, one a line (fields in BlockDef's order)
	static void BlockDump(string path)
	{
		var sb = new StringBuilder(); int n = 0;
		var fields = typeof(BlockDef).GetFields();
		for (int id = 1; id < Blocks.MaxId; id++)
		{
			if (Blocks.Get(id) is not { } d) continue;
			sb.Append(id).Append('\t').Append(DataKeys.KeyOf(id));
			foreach (var fi in fields) sb.Append('\t').Append(fi.Name).Append('=').Append(BlockVal(fi, d));
			sb.Append('\n'); n++;
		}
		using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
		f.StoreString(sb.ToString());
		GD.Print($"BLOCKDUMP {n} blocks to {path}");
	}

	// what the block lists decide, through the game's own functions (so it reads the same before and after a list becomes a
	// field): for every block, the tool it needs, its right tool, whether it digs into pieces, leaves, its sound, whether it's
	// in Items, what breaking it gives, its looks; then Items, Splittable, SnowLoggable and the creative inventory's blocks
	static void ListDump(string path)
	{
		var sb = new StringBuilder(); int n = 0;
		for (int id = 1; id < Blocks.MaxId; id++)
		{
			if (Blocks.Get(id) == null) continue;
			sb.Append($"{id}\t{DataKeys.KeyOf(id)}\tneeds={NeededTool(id)}\tright={RightTool(id)}\tdig={Diggable(id)}\tleaves={Blocks.IsLeaves(id)}\tsound={Sound.MatOf(id)}");
			sb.Append($"\titem={Blocks.Items.Contains(id)}\tdrop={DropFor(id)}\tlooks={string.Join(" ", Blocks.LooksOf(id))}\n"); n++;
		}
		sb.Append("Items (sorted)\t").Append(string.Join(" ", Blocks.Items.Order())).Append('\n');
		sb.Append("Splittable\t").Append(string.Join(" ", Blocks.Splittable)).Append('\n');
		sb.Append("SnowLoggable\t").Append(string.Join(" ", Blocks.SnowLoggable)).Append('\n');
		sb.Append("AllCreative (sorted)\t").Append(string.Join(" ", AllCreative().Order())).Append('\n');
		using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
		f.StoreString(sb.ToString());
		GD.Print($"LISTDUMP {n} blocks to {path}");
	}

	// blocks.json against the blocks: each row, every field (pictures by name, the blocks it names by id) and its key; ids 1-8191
	// (and on desktop, under Blocks.MaxId), none twice; pictures 1, 3 or 4 names; the blocks a row names are rows, one each.
	// The families: the code still finds their blocks by number (Water + level, SoilId, GrainStages, the colours' lists...), so
	// each family's table must be just those blocks, each the one the code expects there.
	List<string> BlocksCheck()
	{
		var found = new List<string>();
		const string path = "res://data/blocks.json";
		if (!FileAccess.FileExists(path)) { found.Add("data/blocks.json is missing"); return found; }
		bool plant = OS.GetCmdlineUserArgs().Contains("--dataplant");
		string text = FileAccess.GetFileAsString(path);
		if (plant) { int a = text.IndexOf("\"transparent\": true"); text = text.Remove(a, 13).Insert(a, "\"transparant\""); }   // (a misspelt field)
		using var doc = JsonDocument.Parse(text);
		UnreadFields(doc.RootElement, typeof(BlocksData), "blocks", found);
		var d = doc.RootElement.Deserialize<BlocksData>(GameData.Opts);
		if (plant)
		{   // (more mistakes the check has to find: five in BASE, and one or two in each family)
			BlockRow Row(int id) => Blocks.AllRows(d).Concat(Blocks.EarthRows(d)).First(r => r.id == id);
			Row(2).name = "Dirtt"; Row(1).drop = "dirrt";
			using (var two = JsonDocument.Parse("[\"sand\", \"sand\"]")) Row(6).pictures = two.RootElement.Clone();
			Row(12).id = 11; Row(706).id = 5000;
			Row(74).facing = 2; Row(75).drop = "kilnn"; Row(53).level = 4; Row(361).drop = "oak_planks"; Row(383).box = new[] { 0f, 0, 0, 1, 0.6f, 1 };
			Row(95).wet = true; Row(44).stage = 3; Row(601).name = "Orange Dyee"; Row(250).soil = true; Row(143).drop = "copper_chunk";
			d.FOUNDRY = d.FOUNDRY.Where(r => r.id != 158).ToArray(); Row(196).dye = "red_dye";
			Row(115).layer = 5; Row(113).drop = "cobbled_andesite#layer=3"; Row(128).name = "Andesite Brick"; d.DEEP_STONE = d.DEEP_STONE.Where(r => r.id != 141).ToArray();
			Row(480).pick = 1; Row(490).drop = "iron_chunk";
			if (d.TORCHES != null)
			{
				Row(27).count = 3; Row(24).wall = new[] { 0, 1 }; Row(85).name = "Torch"; Row(171).shape = "plant";
				d.TORCHES = d.TORCHES.Where(r => r.id != 193).ToArray(); (Row(23).id, Row(182).id) = (182, 23);
			}
			if (d.TOOLS != null) { Row(650).tool = "axe"; Row(666).material = "rosium"; Row(210).damage = 4; }
			if (d.GEAR != null) { Row(674).armor = 2; Row(695).pack = 16; Row(702).tools = 0; Row(688).material = "steel"; d.GEAR = d.GEAR.Where(r => r.id != 704).ToArray(); }
			if (d.FORMS != null && d.FORM_NAMES != null)
			{
				d.FORM_NAMES["vslab"] = "Vertical Slabb"; d.FORMS[0].slab = 1002; d.FORMS[5].of = "stonee";
				(d.FORMS[2], d.FORMS[3]) = (d.FORMS[3], d.FORMS[2]); d.FORMS = d.FORMS[..^1];
			}
			if (d.SNOWY is { Length: > 6 } sn)
			{
				(sn[0].id, sn[1].id) = (sn[1].id, sn[0].id); sn[2].inner = "torchh"; sn[3].drop = "cutter";
				sn[^3].box = new[] { 0.1f, 0, 0.1f, 0.9f, 0.2f, 0.9f }; sn[^2].box = new[] { 0.15f, 0, 0.15f, 0.85f, 0.1f, 0.85f };
				if (sn.FirstOrDefault(s => s.inner == "red_flower") is { } rf) rf.drop = null;
				d.SNOWY = sn[..^1];
			}
			if (d.WOODS is { Length: > 0 } && d.DOORS is { Length: > 3 })
			{   // (step 2: the trees' logs and fruit, the doors)
				Row(2200).log = false; Row(2703).fruit = "stick";
				if (d.WOODS.Any(r => r.grows != null)) { Row(2303).sapling = "maple_log"; Row(2408).grows = "birchh"; Row(195).sand = true; }
				d.DOORS[1].first += 1; d.DOORS[2].of = "oak_log"; d.DOORS = d.DOORS[..^1];
			}
			if (d.EARTH_GROUND is { Length: > 0 } && d.RIVER_WATER is { Length: > 0 } && d.EARTH_FORMS is { Length: > 0 } && d.EARTH_DOORS is { Length: > 0 })
			{   // (Earthlike's rows: a row without earthlike, a river water out of its place, an ore's rock that isn't a key word, a fence
				// outside the fences' tables, a plant's top in a table not its own, earthlike off on a form row and on a door row)
				Row(3920).earthlike = false; Row(3893).pace = 1; Row(4481).@in = "Basalt Rock"; Row(3776).shape = null; Row(3995).top = true;
				d.EARTH_FORMS[0].earthlike = false; d.EARTH_DOORS[0].earthlike = false;
			}
			if (Blocks.AllRows(d).Any(r => r.sound != null))
			{   // (7c: the fields that were lists)
				Row(4).needs = "pickaxe"; Row(61).needs = "hoe"; Row(5).leaves = null; Row(11).shovel = null; Row(7).sound = "woood";
				Row(3).sound = "dirt"; Row(9).inventory = false; Row(14).look = 3;
			}
		}
		// the tables come in the file in the order the game reads them (a table names only blocks in the tables before it, on web)
		var inFile = doc.RootElement.EnumerateObject().Select(p => p.Name).Where(Blocks.FileTables.Contains).ToList();
		if (plant) inFile.Reverse();   // (a table out of its place)
		if (!inFile.SequenceEqual(Blocks.FileTables.Where(inFile.Contains))) found.Add($"blocks: the tables should come in the order {string.Join(", ", Blocks.FileTables)}, not {string.Join(", ", inFile)}");
		var tables = Blocks.RowTables.Concat(Blocks.EarthRowTables).Select(t => (t, rows: Blocks.RowTable(d, t))).ToList();
		var all = tables.SelectMany(x => x.rows.Select(r => (x.t, r))).ToList();
		foreach (var g in all.GroupBy(x => x.r.id).Where(g => g.Count() > 1)) found.Add($"blocks: id {g.Key} is used {g.Count()} times ({string.Join(", ", g.Select(x => $"{x.t} {x.r.name}"))})");
		foreach (var (t, r) in all.Where(x => x.r.id < 1 || x.r.id > 8191)) found.Add($"{t}: id {r.id} ({r.name}) isn't 1-8191");
		foreach (var (t, r) in all.Where(x => x.r.id >= Blocks.MaxId && x.r.id <= 8191)) found.Add($"{t}: id {r.id} ({r.name}) is past the end of desktop's block list (Blocks.MaxId {Blocks.MaxId})");
		// each row as a block (its pictures stand-ins: they're compared by name), then the blocks rows name
		var defs = new List<(string t, BlockRow r, BlockDef b)>();
		foreach (var (t, r) in all)
			try { defs.Add((t, r, Blocks.RowDef(r, n => new int[n.Length]))); }
			catch (Exception x) when (x is InvalidOperationException or FormatException) { found.Add($"{t}: {x.Message}"); }
		var keys = Blocks.RowKeys(defs.Select(x => (x.r, x.b)));
		var itemSlugs = defs.Where(x => x.r.item).Select(x => DataKeys.Snake(x.r.name ?? "")).ToHashSet();
		foreach (var (t, r, want) in defs)
		{
			if (r.id < 1 || r.id >= Blocks.MaxId) continue;
			try { Blocks.RowRefs(want, r, keys); }
			catch (Exception x) when (x is InvalidOperationException or FormatException) { found.Add($"{t}: {x.Message}"); continue; }
			if (Blocks.Get(r.id) is not { } game) { found.Add($"{t} {r.id} ({r.name}): the game has no block {r.id}"); continue; }
			string fp = string.Join(" ", Blocks.RowPictures(r)), gp = BlockVal(typeof(BlockDef).GetField("T"), game);
			if (fp != gp) found.Add($"{t} {r.id} ({r.name}) pictures: file {fp}; game {gp}");
			string ficon = r.icon ?? "-1", gi = BlockVal(typeof(BlockDef).GetField("Icon"), game);
			if (ficon != gi) found.Add($"{t} {r.id} ({r.name}) icon: file {ficon}; game {gi}");
			if ((r.full ?? "-1") is var ffull && BlockVal(typeof(BlockDef).GetField("Full"), game) is var gfull && ffull != gfull) found.Add($"{t} {r.id} ({r.name}) full: file {ffull}; game {gfull}");
			if (string.Join(" ", r.frames ?? Array.Empty<string>()) is var ffr && BlockVal(typeof(BlockDef).GetField("Frames"), game) is var gfr && ffr != gfr) found.Add($"{t} {r.id} ({r.name}) frames: file {ffr}; game {gfr}");
			if ((r.live == null) != (r.frames == null) || r.live is not (null or "clock" or "season") || r.live == "clock" && r.frames.Length != 8 || r.live == "season" && r.frames.Length != 4 || r.live != null && !r.item) found.Add($"{t} {r.id} ({r.name}): live is clock (8 frames) or season (4 frames), on an item, and only with frames");
			if (r.frames is { Length: > 0 } && Blocks.RowPictures(r).Any(p => p != r.frames[0])) found.Add($"{t} {r.id} ({r.name}): frames[0] should be its picture ({string.Join(" ", Blocks.RowPictures(r))})");
			if (r.stack is < 0 or > 63) found.Add($"{t} {r.id} ({r.name}): stack is a whole number 1-63 (left out: a full stack)");
			foreach (var fi in typeof(BlockDef).GetFields().Where(fi => fi.Name is not ("T" or "Icon" or "Full" or "Frames")))
				if (BlockVal(fi, want) is var f && BlockVal(fi, game) is var g && f != g) found.Add($"{t} {r.id} ({r.name}).{fi.Name}: file {f}, game {g}");
			if (Blocks.RowKey(r, want, itemSlugs) is var rk && DataKeys.KeyOf(r.id) is var gk && rk != gk) found.Add($"{t} {r.id} ({r.name}): its key is \"{rk}\", but the game's block {r.id} is \"{gk}\"");
			// (fields only the families have, which desktop finds by number: only on the blocks that are those)
			if (t != "SOILS" && (r.soil || r.wet || r.fert)) found.Add($"{t} {r.id} ({r.name}): soil, wet and fert are only for SOILS");
			if (t != "GRAIN" && t != "CROPS" && r.stage != 0) found.Add($"{t} {r.id} ({r.name}): stage is only for GRAIN and CROPS");
			if (t != "CROPS" && r.crop != null || r.top && t is not ("CROPS" or "EARTH_PLANTS")) found.Add($"{t} {r.id} ({r.name}): crop is only for CROPS, and top for CROPS and EARTH_PLANTS");
			// (Earthlike's: earthlike on every row of the EARTH_ tables and RIVER_WATER but the shown rocks and gravels, and only theirs; flow and pace only river
			// water's, each at 3891 + 8 x pace + flow (3915 falling: flow 8, pace 2); in only an ore's in EARTH_ROCKS, a key word;
			// water and top only a plant's in EARTH_PLANTS)
			if (r.earthlike != (Blocks.EarthRowTables.Contains(t) && !Blocks.ShownEarthRow(t, r.id))) found.Add($"{t} {r.id} ({r.name}): earthlike is for every row of the EARTH_ tables and RIVER_WATER but the rocks and gravels shown since Beta 0.1.5.2 (Blocks.ShownEarthRow), and only theirs");
			if ((t == "RIVER_WATER") != (r.flow != null) || (t != "RIVER_WATER" && r.pace != 0)) found.Add($"{t} {r.id} ({r.name}): flow and pace are river water's (RIVER_WATER), and every one of its rows has flow");
			if (t == "RIVER_WATER" && (r.flow is < 0 or > 8 || r.pace is < 0 or > 2 || r.id != (r.flow == 8 ? 3915 : 3891 + 8 * r.pace + r.flow) || (r.flow == 8 && r.pace != 2) || !r.liquid || r.level != 0))
				found.Add($"RIVER_WATER {r.id}: should be 3891 + 8 x pace + flow (0-7; 3915 falling, flow 8 pace 2), liquid, level 0 (it's flow {r.flow}, pace {r.pace})");
			if (r.@in != null && (t != "EARTH_ROCKS" || !System.Text.RegularExpressions.Regex.IsMatch(r.@in, "^[a-z0-9_]+$"))) found.Add($"{t} {r.id} ({r.name}): in (an ore's rock) is only for EARTH_ROCKS' ores, a key word like granite, not \"{r.@in}\"");
			if ((r.water || r.top && t == "EARTH_PLANTS") && (t != "EARTH_PLANTS" || r.shape != "plant")) found.Add($"{t} {r.id} ({r.name}): water and a plant's top are for EARTH_PLANTS' plants (shape plant)");
			if (t == "CROPS" && (r.crop == null || r.stage is < 0 or > 3 || (r.top && (r.crop != "corn" || r.stage < 2)))) found.Add($"CROPS {r.id} ({r.name}): a crop needs its crop and a stage 0-3 (top: Corn's, stages 2 and 3)");
			if ((t is "FENCES" or "EARTH_FENCES") != (r.shape == "fence") || (t is "FENCES" or "EARTH_FENCES" && r.of == null)) found.Add($"{t} {r.id} ({r.name}): fences (shape fence, with of) are FENCES' and EARTH_FENCES' rows, and only theirs");
		}
		// the families: just the blocks the code finds by number
		static int[] Span(int a, int n) => Enumerable.Range(a, n).ToArray();
		var families = new Dictionary<string, int[]>
		{
			["KILN"] = Span(Blocks.Furnace, 8), ["TORCHES"] = Span(Blocks.Torch, 20).Concat(Span(Blocks.SmolderingTorch, 12)).Concat(Span(Blocks.BurntTorch, 12)).Concat(Span(Blocks.EverTorch, 12)).ToArray(),
			["WATER"] = Span(Blocks.Water, 8), ["LYING_LOGS"] = Span(Blocks.TurnedLogs, 16),   // (the older woods': the newer ones lie in WOODS)
			["SNOW"] = Span(Blocks.Snow, Blocks.SnowBlock - Blocks.Snow), ["DEEP_STONE"] = Blocks.DeepBlocks, ["SOILS"] = Blocks.Soils, ["GRAIN"] = Blocks.GrainStages,
			["COLOURS"] = Blocks.Dye.Concat(Blocks.NewColourBlocks).ToArray(),
			["METALS"] = Blocks.Ores.Concat(Blocks.Metals.SelectMany(m => new[] { Blocks.Chunk[m], Blocks.MetalBrick[m] })).ToArray(),
			["DEEP_ORES"] = Blocks.DeepOres, ["FOUNDRY"] = Span(Blocks.Foundry, 8), ["FLOWERS"] = Blocks.Flowers,
			["TOOLS"] = Span(207, 5).Concat(Span(644, 25)).ToArray(), ["GEAR"] = Span(Blocks.ArmorItem(0, 0), 24).Concat(Blocks.Backpacks).Concat(Blocks.Belts).ToArray(),
		};
		foreach (var (t, ids) in families)
		{
			string has = string.Join(", ", Blocks.RowTable(d, t).Select(r => r.id).Order()), should = string.Join(", ", ids.Order());
			if (has != should) found.Add($"{t}: should be blocks {should} (the code finds them by number), not {has}");
		}
		var rowOf = new Dictionary<int, BlockRow>();
		foreach (var (_, r) in all) rowOf.TryAdd(r.id, r);
		var fromFile = new HashSet<int>(rowOf.Keys);   // (every id the file gives a block: the rows, then FORMS' and SNOWY's below)
		var fileDef = new Dictionary<int, BlockDef>();   // (each of those as the file makes it: the rows here, FORMS' and SNOWY's below)
		foreach (var (_, r, b) in defs) fileDef.TryAdd(r.id, b);
		string Show(object v) => v is JsonElement e ? e.GetRawText() : JsonSerializer.Serialize(v);
		void Want(int id, string why, params (string f, object v)[] fields)
		{
			rowOf.TryGetValue(id, out var r);
			foreach (var (f, v) in fields)
			{
				object has = r == null ? null : f == "pictures" ? (object)Blocks.RowPictures(r) : typeof(BlockRow).GetField(f).GetValue(r);
				if (Show(has) != Show(v)) found.Add($"blocks {id}: {f} should be {Show(v)}, not {Show(has)} ({why})");
			}
		}
		string Cap(string s) => char.ToUpper(s[0]) + s[1..];
		foreach (var (b, name) in new[] { (Blocks.Furnace, "the Kiln"), (Blocks.Foundry, "the Foundry") })
			for (int k = 0; k < 8; k++)
				Want(b + k, $"{name}: {b} + facing, + 4 when lit; breaking it gives {b}", ("facing", k % 4), ("front", Blocks.FurnaceFronts[k % 4]), ("lit", k >= 4), ("drop", DataKeys.KeyOf(b)));
		// torches: one row for each (burn, wall, count) there is (lit: 1-4 anywhere; the others 1-4 standing, 1-2 on a wall), found
		// by those (TorchVariant); the code knows the standing x1 of each burn by number, and a lit x1 on wall wi is 22 + wi
		var torchRows = Blocks.RowTable(d, "TORCHES");
		string[] torchNames = { "Torch", "Smoldering Torch", "Burnt Torch", "Ever Torch" };
		int[] torchFirst = { Blocks.Torch, Blocks.SmolderingTorch, Blocks.BurntTorch, Blocks.EverTorch };
		for (int burn = 0; burn < 4; burn++)
			for (int wi = 0; wi < Blocks.TorchWalls.Length; wi++)
				for (int n = 1; n <= (burn == 0 ? 4 : Blocks.TorchMax(wi)); n++)
				{
					var at = torchRows.Where(r => r.burn == burn && r.wi == wi && r.count == n).ToList();
					if (at.Count != 1) { found.Add($"TORCHES: {at.Count} rows for burn {burn}, wall {wi}, count {n} (each needs one: TorchVariant finds it by those)"); continue; }
					string why = $"a torch: burn {burn} ({torchNames[burn]}), wall {wi}, count {n}";
					Want(at[0].id, why, ("name", torchNames[burn]), ("shape", "torch"), ("wall", Blocks.TorchWalls[wi]));
					if (n == 1 && (wi == 0 || burn == 0) && at[0].id != (wi == 0 ? torchFirst[burn] : Blocks.Torch + wi))
						found.Add($"TORCHES: burn {burn}, wall {wi}, count 1 should be block {(wi == 0 ? torchFirst[burn] : Blocks.Torch + wi)}, not {at[0].id} (the code knows it by number)");
				}
		foreach (var r in torchRows.Where(r => r.burn is < 0 or > 3 || r.wi < 0 || r.wi >= Blocks.TorchWalls.Length || r.count < 1 || r.count > (r.burn == 0 ? 4 : Blocks.TorchMax(r.wi))))
			found.Add($"TORCHES {r.id}: burn {r.burn}, wall {r.wi}, count {r.count} isn't a torch there is");
		for (int l = 0; l <= 7; l++) Want(Blocks.Water + l, $"water: {Blocks.Water} + level", ("level", l), ("liquid", true));
		foreach (int log in Blocks.Logs)
			for (int axis = 1; axis <= 2; axis++)
			{
				rowOf.TryGetValue(log, out var lr);
				Want(Blocks.Turned(log, axis), $"a lying {DataKeys.KeyOf(log)}: see Turned", ("name", lr?.name), ("pictures", lr == null ? null : Blocks.RowPictures(lr)), ("axis", axis), ("drop", DataKeys.KeyOf(log)));
			}
		for (int n = 1; n < Blocks.SnowBlock - Blocks.Snow + 1; n++) Want(Blocks.Snow + n - 1, $"snow: {Blocks.Snow} + layers - 1", ("shape", "snow"), ("layers", n));
		foreach (int b in new[] { 3, 8, 9, 69 })
		{
			var row = Blocks.DeepRow(b);
			for (int k = 0; k < row.Length; k++)
				Want(row[k], $"the deep stone sets: DeepRow({b})[{k}] is {DataKeys.KeyOf(b)} in layer {k + 2}" + (b == 3 ? ", which breaks into that layer's Cobblestone" : ""),
					("name", rowOf.GetValueOrDefault(b)?.name), ("layer", k + 2), ("drop", b == 3 ? DataKeys.KeyOf(Blocks.DeepCobble + k) : null));
		}
		for (int k = 0; k < Blocks.Ores.Length; k++)
			for (int n = 2; n <= Blocks.StrataLayers; n++)
				Want(Blocks.DeepOreOf(k, n), $"the deep ores: DeepOreOf({k}, {n}) is {DataKeys.KeyOf(Blocks.Ores[k])} in layer {n}",
					("name", rowOf.GetValueOrDefault(Blocks.Ores[k])?.name), ("pick", rowOf.GetValueOrDefault(Blocks.Ores[k])?.pick), ("layer", n), ("drop", DataKeys.KeyOf(Blocks.Chunk[Blocks.Metals[k]])));
		foreach (bool wet in new[] { false, true })
			foreach (bool fert in new[] { false, true }) Want(Blocks.SoilId(wet, fert), "soil: see SoilId", ("soil", true), ("wet", wet), ("fert", fert));
		for (int k = 0; k < Blocks.GrainStages.Length; k++) Want(Blocks.GrainStages[k], "Grain: GrainStages in order", ("stage", k));
		for (int k = 0; k < Blocks.Colours.Length; k++)
		{
			string C = Cap(Blocks.Colours[k]), why = $"the colours' lists: {Blocks.Colours[k]} is colour {k}";
			Want(Blocks.Dye[k], why, ("name", $"{C} Dye"), ("item", true));
			Want(Blocks.ColourTerracotta[k], why, ("name", $"{C} Terracotta"));
			Want(Blocks.ColourChiseled[k], why, ("name", $"Chiseled {C} Terracotta"));
			Want(Blocks.ColourBricks[k], why, ("name", $"{C} Bricks"));
		}
		for (int k = 0; k < Blocks.Metals.Length; k++)
		{
			string m = Blocks.Metals[k], M = Cap(m), why = $"the metals' lists: {m} is metal {k}";
			Want(Blocks.Ores[k], why, ("name", $"{M} Ore"), ("drop", DataKeys.KeyOf(Blocks.Chunk[m])));
			Want(Blocks.Chunk[m], why, ("name", $"{M} Chunk"), ("item", true));
			Want(Blocks.MetalBrick[m], why, ("name", $"{M} Brick"), ("item", true));
		}
		for (int k = 0; k < Blocks.Flowers.Length; k++)
		{
			string c = Blocks.FlowerColours[k];
			Want(Blocks.Flowers[k], $"the flowers' list: {c} is flower {k}", ("name", $"{Cap(c)} Flower"), ("shape", "plant"), ("dye", DataKeys.KeyOf(Blocks.DyeOf(c))));
		}
		// tools: each material's 5, in the kinds' order (stone 207-211; copper, iron, bronite, rosium, steel from 644)
		if (d.TOOLS != null)
		{
			string[] kinds = { "axe", "pickaxe", "shovel", "sword", "hoe" };
			foreach (var (m, first) in new[] { ("stone", 207), ("copper", 644), ("iron", 649), ("bronite", 654), ("rosium", 659), ("steel", 664) })
				for (int k = 0; k < 5; k++) Want(first + k, $"the tools: {first} + kind is the {m} {kinds[k]}", ("tool", kinds[k]), ("material", m));
		}
		// gear: each tier's armor (ArmorItem), backpack and belt (their slots are only in the rows)
		if (d.GEAR != null)
		{
			for (int t = 0; t < Blocks.GearTiers.Length; t++)
			{
				string m = Blocks.GearTiers[t], why = $"the gear: {m} is tier {t}";
				for (int k = 0; k < Blocks.ArmorPieces.Length; k++) Want(Blocks.ArmorItem(t, k), why + $", {Blocks.ArmorPieces[k]} is armor {k}", ("armor", k), ("material", m));
				Want(Blocks.Backpacks[t], why, ("material", m)); Want(Blocks.Belts[t], why, ("material", m));
				if (rowOf.GetValueOrDefault(Blocks.Backpacks[t]) is { pack: <= 0 }) found.Add($"blocks {Blocks.Backpacks[t]}: a backpack needs its pack (slots)");
				if (rowOf.GetValueOrDefault(Blocks.Belts[t]) is { belt: <= 0 } or { tools: <= 0 }) found.Add($"blocks {Blocks.Belts[t]}: a belt needs its belt and tools (slots)");
			}
		}
		// pieces and shapes: FORM_NAMES is the code's forms (Blocks.Forms) in order; FORMS is each block you can cut, in
		// Blocks.Splittable's order, with its items where FormItem finds them; each item against the game's, field by field
		if (d.FORMS != null || d.FORM_NAMES != null)
		{
			var names = d.FORM_NAMES ?? new Dictionary<string, string>();
			if (!names.Keys.SequenceEqual(Blocks.Forms)) found.Add($"FORM_NAMES: should be the forms {string.Join(", ", Blocks.Forms)}, in that order, not {string.Join(", ", names.Keys)}");
			var forms = (d.FORMS ?? Array.Empty<FormRow>()).Concat(d.EARTH_FORMS ?? Array.Empty<FormRow>()).ToArray();   // (then EARTH_FORMS' rows: they're Blocks.Splittable's last ones)
			int earthFrom = d.FORMS?.Length ?? 0;
			var ofs = new List<int>(); var itemIds = new HashSet<int>();
			for (int k = 0; k < forms.Length; k++)
			{
				var f = forms[k]; int of;
				if (f.earthlike != (k >= earthFrom && !Blocks.ShownEarthForm(f.piece))) found.Add($"FORMS {k} ({f.of}): earthlike is for every EARTH_FORMS row but the rocks' (shown since Beta 0.1.5.2: Blocks.ShownEarthForm), and only theirs");
				try { of = Blocks.KeyRef(keys, f.of, $"FORMS {k}"); }
				catch (InvalidOperationException x) { found.Add(x.Message); continue; }
				ofs.Add(of);
				if (!rowOf.TryGetValue(of, out var ofRow)) { found.Add($"FORMS {k} ({f.of}): no block to cut"); continue; }
				string pics = string.Join(" ", Blocks.RowPictures(ofRow));
				try
				{
					foreach (var (id, item) in Blocks.FormItems(f, of, Blocks.RowDef(ofRow, n => new int[n.Length]), names))
					{
						string form = item.Piece > 0 ? "piece" : item.Form, at = $"FORMS {k} ({f.of}).{form} {id}";
						fileDef.TryAdd(id, item);
						int sum = (of >= 2200 && of < 3400 || of >= 4000 && of < 4400) && of % 100 < 4 ? of - of % 100 + 10 * (of % 100 + 1) + Array.IndexOf(Blocks.Forms, form) : Blocks.FormSum(form, of);
						if (id != sum) found.Add($"{at}: should be {sum} (the older blocks': 100 + b, 1000 + b..., or from 1800 or 2000; the cloths': from 3800; the newer woods': B + 10 to B + 46; Earthlike's rocks': from 4500 and 4830)");
						if (rowOf.ContainsKey(id) || !itemIds.Add(id)) found.Add($"{at}: id {id} is another block's or item's too");
						if (Blocks.Get(id) is not { } game) { found.Add($"{at}: the game has no block {id}"); continue; }
						if (BlockVal(typeof(BlockDef).GetField("T"), game) is var gp && gp != pics) found.Add($"{at} pictures: file {pics}; game {gp}");
						foreach (var fi in typeof(BlockDef).GetFields().Where(fi => fi.Name != "T"))
							if (BlockVal(fi, item) is var fv && BlockVal(fi, game) is var gv && fv != gv) found.Add($"{at}.{fi.Name}: file {fv}, game {gv}");
						if ($"{form}:{f.of}" is var ik && DataKeys.KeyOf(id) is var gk && ik != gk) found.Add($"{at}: its key is \"{ik}\", but the game's block {id} is \"{gk}\"");
					}
				}
				catch (InvalidOperationException x) { found.Add(x.Message); }
			}
			if (!ofs.SequenceEqual(Blocks.Splittable)) found.Add($"FORMS: the blocks should be Blocks.Splittable's, in order ({Blocks.Splittable.Length}: {string.Join(", ", Blocks.Splittable)}), not {string.Join(", ", ofs)}");
			foreach (int id in itemIds) fromFile.Add(id);
		}
		// snow-logged blocks: each row against the game's, field by field (its inner row's pictures by name); the ids where the
		// code puts them: SnowLogged1 + k for the torches and flowers (by number), then SnowyPieces + k for SnowLoggedOthers
		if (d.SNOWY != null)
		{
			var defOf = new Dictionary<int, BlockDef>();
			foreach (var (_, r, b) in defs) defOf.TryAdd(r.id, b);
			var torchesFlowers = Enumerable.Range(1, Blocks.SnowLogged1 - 1).Where(i => rowOf.TryGetValue(i, out var r) && (r.shape == "torch" || Blocks.IsFlower(i)));
			var should = torchesFlowers.Select((inner, k) => (id: Blocks.SnowLogged1 + k, inner)).Concat(Blocks.SnowLoggedOthers.Select((inner, k) => (id: Blocks.SnowyPieces + k, inner))).ToList();
			var has = new List<(int id, int inner)>(); var snowIds = new HashSet<int>();
			for (int k = 0; k < d.SNOWY.Length; k++)
			{
				var s = d.SNOWY[k]; string at = $"SNOWY {k} (id {s.id}, {s.inner})";
				int inner;
				try { inner = Blocks.KeyRef(keys, s.inner, at); }
				catch (InvalidOperationException x) { found.Add(x.Message); continue; }
				has.Add((s.id, inner));
				if (rowOf.ContainsKey(s.id) || !snowIds.Add(s.id)) found.Add($"{at}: id {s.id} is another block's too");
				if (!rowOf.TryGetValue(inner, out var innerRow) || !defOf.TryGetValue(inner, out var innerDef)) { found.Add($"{at}: its inner isn't a row"); continue; }
				BlockDef want;
				try { want = Blocks.SnowyBlock(s, inner, innerDef, keys); }
				catch (InvalidOperationException x) { found.Add(x.Message); continue; }
				fileDef.TryAdd(s.id, want);
				if (Blocks.Get(s.id) is not { } game) { found.Add($"{at}: the game has no block {s.id}"); continue; }
				string fp = string.Join(" ", Blocks.RowPictures(innerRow)), gp = BlockVal(typeof(BlockDef).GetField("T"), game);
				if (fp != gp) found.Add($"{at} pictures: file {fp}; game {gp}");
				string ficon = innerRow.icon ?? "-1", gi = BlockVal(typeof(BlockDef).GetField("Icon"), game);
				if (ficon != gi) found.Add($"{at} icon: file {ficon}; game {gi}");
				foreach (var fi in typeof(BlockDef).GetFields().Where(fi => fi.Name is not ("T" or "Icon" or "Full" or "Frames")))
					if (BlockVal(fi, want) is var fv && BlockVal(fi, game) is var gv && fv != gv) found.Add($"{at}.{fi.Name}: file {fv}, game {gv}");
				if ($"snowy:{s.inner}" is var sk && DataKeys.KeyOf(s.id) is var gk && sk != gk) found.Add($"{at}: its key is \"{sk}\", but the game's block {s.id} is \"{gk}\"");
				// (breaking it gives what it holds, not itself: only a torch or pieces drop nothing here, the code gives theirs)
				if (want.Drop == 0 && innerRow.shape != "torch" && inner != Blocks.Pieces) found.Add($"{at}: needs a drop (what breaking it gives)");
				if (want.Box != null && want.Box[4] < 1 / 8f) found.Add($"{at}: its box should be at least 1/8 high, over the snow");
			}
			for (int k = 0; k < Math.Max(has.Count, should.Count); k++)
				if (k >= has.Count || k >= should.Count || has[k] != should[k])
					found.Add($"SNOWY {k}: should be block {(k < should.Count ? $"{should[k].id} holding {DataKeys.KeyOf(should[k].inner)}" : "nothing")} (the torches and flowers by number from {Blocks.SnowLogged1}, then {string.Join(", ", Blocks.SnowLoggedOthers)} from {Blocks.SnowyPieces}), not {(k < has.Count ? $"{has[k].id} holding {DataKeys.KeyOf(has[k].inner)}" : "nothing")}");
			foreach (int id in snowIds) fromFile.Add(id);
		}
		// doors: each DOORS row's 32 blocks against the game's, field by field (pictures by name); a row's planks are a row, its
		// item an item row named "... Door", its states start just after the item (first = item + 1) and overlap nothing
		if (d.DOORS != null)
		{
			var defOf = new Dictionary<int, BlockDef>();
			foreach (var (_, r, b) in defs) defOf.TryAdd(r.id, b);
			var doorIds = new HashSet<int>(); var doorItems = new HashSet<int>();
			var doorRows = d.DOORS.Concat(d.EARTH_DOORS ?? Array.Empty<DoorRow>()).ToArray();   // (then EARTH_DOORS')
			for (int k = 0; k < doorRows.Length; k++)
			{
				var dr = doorRows[k]; string at = $"{(k < d.DOORS.Length ? "DOORS" : "EARTH_DOORS")} {(k < d.DOORS.Length ? k : k - d.DOORS.Length)} ({dr.of}, item {dr.item})";
				if (dr.earthlike != k >= d.DOORS.Length) found.Add($"{at}: earthlike is for every EARTH_DOORS row, and only theirs");
				int of;
				try { of = Blocks.KeyRef(keys, dr.of, at); }
				catch (InvalidOperationException x) { found.Add(x.Message); continue; }
				if (!rowOf.TryGetValue(of, out var ofRow) || !defOf.TryGetValue(of, out var ofDef)) { found.Add($"{at}: its planks aren't a row"); continue; }
				if (!rowOf.TryGetValue(dr.item, out var itemRow) || !itemRow.item || !defOf.TryGetValue(dr.item, out var itemDef)) { found.Add($"{at}: its item isn't an item row"); continue; }
				if (itemRow.name == null || !itemRow.name.EndsWith(" Door")) found.Add($"{at}: its item is {itemRow.name}, not a door");
				if (dr.first != dr.item + 1) found.Add($"{at}: first should be {dr.item + 1} (just after its item), not {dr.first}");
				if (!doorItems.Add(dr.item)) found.Add($"{at}: item {dr.item} has another DOORS row too");
				if (Blocks.DoorFirst(dr.item) != dr.first) found.Add($"{at}: the game's door {dr.item} stands from {Blocks.DoorFirst(dr.item)}, not {dr.first}");
				string itemKey = keys.First(kv => kv.Value == dr.item).Key, edge = Blocks.RowPictures(ofRow)[0];
				foreach (var (id, want) in Blocks.DoorBlocks(dr, of, ofDef, dr.item, itemDef, itemKey, n => new int[n.Length]))
				{
					string sat = $"{at} {id}";
					if (rowOf.ContainsKey(id) || fromFile.Contains(id) || !doorIds.Add(id)) { found.Add($"{sat}: id {id} is another block's or item's too"); continue; }
					fileDef.TryAdd(id, want);
					if (Blocks.Get(id) is not { } game) { found.Add($"{sat}: the game has no block {id}"); continue; }
					string fp = $"{edge} {edge} {edge} {itemKey}_{(want.Half == 0 ? "bottom" : "top")}", gp = BlockVal(typeof(BlockDef).GetField("T"), game);
					if (fp != gp) found.Add($"{sat} pictures: file {fp}; game {gp}");
					foreach (var fi in typeof(BlockDef).GetFields().Where(fi => fi.Name != "T"))
						if (BlockVal(fi, want) is var fv && BlockVal(fi, game) is var gv && fv != gv) found.Add($"{sat}.{fi.Name}: file {fv}, game {gv}");
					string sk = $"{itemKey}#facing={want.Facing},half={want.Half},hinge={want.Hinge},open={want.Open}";
					if (DataKeys.KeyOf(id) is var gk && sk != gk) found.Add($"{sat}: its key is \"{sk}\", but the game's block {id} is \"{gk}\"");
				}
			}
			for (int id = 1; id < Blocks.MaxId; id++)
				if (Blocks.Get(id) is { Shape: "door" } g && !doorItems.Contains(g.Drop)) { found.Add($"DOORS: the game's door {g.Drop} (standing from {Blocks.DoorFirst(g.Drop)}) has no row"); doorItems.Add(g.Drop); }
			foreach (int id in doorIds) fromFile.Add(id);
		}
		// the trees' logs (the rows with "log"): each upright, lying both ways (a row on each axis that drops it); fruit: food
		{
			string Ids(IEnumerable<int> ids) => string.Join(", ", ids.Order());
			var fileLogs = all.Where(x => x.r.log).Select(x => x.r.id).ToList();
			if (Ids(fileLogs) is var fl && Ids(Blocks.Logs) is var gl && fl != gl) found.Add($"blocks: the logs (\"log\") are {fl} in the file, {gl} in the game");
			foreach (var (t, r) in all.Where(x => x.r.log && x.r.axis != 0)) found.Add($"{t} {r.id} ({r.name}): a log (\"log\") stands upright (no axis); its lying rows drop it");
			foreach (var (t, r) in all.Where(x => x.r.fruit != null))
			{
				if (!keys.TryGetValue(r.fruit, out int fr) || !rowOf.TryGetValue(fr, out var frow)) { found.Add($"{t} {r.id} ({r.name}): its fruit \"{r.fruit}\" isn't a row"); continue; }
				if (!frow.item || frow.food <= 0) found.Add($"{t} {r.id} ({r.name}): its fruit {r.fruit} isn't food (an item row with food)");
				if (r.leaves != true) found.Add($"{t} {r.id} ({r.name}): fruit only falls from leaves");
			}
			// saplings: a leaves row's sapling is a sapling row; a sapling grows into a tree there's a shape for (the older four, or
			// worldgen.json TREE_SHAPES); only saplings have sand; and each is planted like a flower (a plant you walk through)
			foreach (var (t, r) in all.Where(x => x.r.sapling != null))
			{
				if (!keys.TryGetValue(r.sapling, out int sp) || !rowOf.TryGetValue(sp, out var srow)) { found.Add($"{t} {r.id} ({r.name}): its sapling \"{r.sapling}\" isn't a row"); continue; }
				if (srow.grows == null) found.Add($"{t} {r.id} ({r.name}): its sapling {r.sapling} isn't a sapling (a row with grows)");
				if (r.leaves != true) found.Add($"{t} {r.id} ({r.name}): saplings only fall from leaves");
			}
			var shapes = (Nums.Worldgen.TREE_SHAPES ?? Array.Empty<TreeShapeRow>()).Select(s => s.tree).Concat(WorldGen.OldTrees).ToHashSet();
			foreach (var (t, r) in all.Where(x => x.r.grows != null))
			{
				if (!shapes.Contains(r.grows)) found.Add($"{t} {r.id} ({r.name}): it grows into \"{r.grows}\", which isn't oak, fir, palm, willow or a worldgen.json TREE_SHAPES tree");
				if (r.shape != "plant" || !r.noCollide || r.item) found.Add($"{t} {r.id} ({r.name}): a sapling is a plant you walk through (shape plant, noCollide, not an item)");
			}
			foreach (var (t, r) in all.Where(x => x.r.sand && x.r.grows == null)) found.Add($"{t} {r.id} ({r.name}): sand is only for saplings");
		}
		// the block lists the game uses, against the blocks' fields: the tool a block needs, the shovel's blocks, leaves, sounds,
		// Items (inventory, not snow-logged, layer 1 unless tall worlds are on), looks, and the creative inventory (Items and
		// every item row); and the fields' values
		{
			string[] needsKinds = { "axe", "pickaxe" };
			foreach (var (_, r) in all)
			{
				if (r.needs != null && !needsKinds.Contains(r.needs)) found.Add($"blocks {r.id} ({r.name}): needs is \"{r.needs}\", not axe or pickaxe");
				if (r.sound != null && !Sound.Recipes.Contains(r.sound)) found.Add($"blocks {r.id} ({r.name}): sound \"{r.sound}\" isn't one the game has ({string.Join(", ", Sound.Recipes)})");
				if (r.shovel == false || r.leaves == false || r.inventory == false) found.Add($"blocks {r.id} ({r.name}): shovel, leaves and inventory are true or left out");
				if (r.look != 0 && !(r.look > 0 && fileDef.GetValueOrDefault(r.id) is { Drop: > 0 } ld && fileDef.GetValueOrDefault(ld.Drop) is { Look: 0 }))
					found.Add($"blocks {r.id} ({r.name}): a look (1 up) is only for a block that drops a block without one");
			}
			foreach (var g in all.Where(x => x.r.look > 0).GroupBy(x => x.r.drop))
				if (!g.Select(x => x.r.look).Order().SequenceEqual(Enumerable.Range(1, g.Count())))
					found.Add($"blocks: the looks of {g.Key} should be numbered 1-{g.Count()}, each once, not {string.Join(", ", g.Select(x => $"{x.r.id}: {x.r.look}"))}");
			int[] FileLooks(int id)
			{
				var d0 = fileDef.GetValueOrDefault(id); int b = d0 is { Look: > 0 } ? d0.Drop : id;
				var others = fileDef.Where(kv => kv.Value.Look > 0 && kv.Value.Drop == b).OrderBy(kv => kv.Value.Look).Select(kv => kv.Key).ToArray();
				return others.Length == 0 ? new[] { id } : others.Prepend(b).ToArray();
			}
			var fileItems = new HashSet<int>();
			for (int id = 1; id < Blocks.MaxId; id++)
			{
				if (Blocks.Get(id) == null || !fileDef.TryGetValue(id, out var fd)) continue;
				string at = $"blocks {id} ({fd.Name})";
				if (NeededTool(id) != fd.Needs) found.Add($"{at}: the game needs {NeededTool(id) ?? "nothing"} to break it, the file {fd.Needs ?? "nothing"}");
				if (Diggable(id) != fd.Shovel) found.Add($"{at}: the game {(Diggable(id) ? "has" : "hasn't")} it as a shovel's block, the file {(fd.Shovel ? "has" : "hasn't")}");
				if (Blocks.IsLeaves(id) != fd.Leaves) found.Add($"{at}: leaves in the game {Blocks.IsLeaves(id)}, in the file {fd.Leaves}");
				if (Sound.MatOf(id) != (fd.Sound ?? "stone")) found.Add($"{at}: sounds like {Sound.MatOf(id)} in the game, {fd.Sound ?? "stone (left out)"} in the file");
				if (string.Join(" ", Blocks.LooksOf(id)) is var gl && string.Join(" ", FileLooks(id)) is var fl && gl != fl) found.Add($"{at}: looks {gl} in the game, {fl} in the file");
				if (fd.Inventory && fd.Snowed == 0 && (fd.Layer <= 1 || World.TallWorlds) && (WorldGen.EarthlikeOn || !fd.Earthlike)) fileItems.Add(id);
			}
			string Ids(IEnumerable<int> ids) => string.Join(", ", ids.Order());
			if (Ids(Blocks.Items) is var gi && Ids(fileItems) is var fi && gi != fi)
				found.Add($"Items: the game's are [{Ids(Blocks.Items.Except(fileItems))}] beyond the file's, the file's [{Ids(fileItems.Except(Blocks.Items))}] beyond the game's");
			var fileCreative = fileItems.Concat(all.Where(x => (x.r.item && (WorldGen.EarthlikeOn || !x.r.earthlike)) || x.r.shape == "torch" && x.r.wi == 0 && x.r.count == 1).Select(x => x.r.id)).ToHashSet();
			if (Ids(AllCreative()) is var gc && Ids(fileCreative) is var fc && gc != fc)
				found.Add($"the creative inventory: the game's has [{Ids(AllCreative().Except(fileCreative))}] beyond Items, the item rows and each kind of torch, which have [{Ids(fileCreative.Except(AllCreative()))}] beyond it");
		}
		// every block the game has comes from data/blocks.json (a row, a FORMS item or a SNOWY block): nothing made in code
		for (int id = 1; id < Blocks.MaxId; id++)
			if (Blocks.Get(id) is { } g && !fromFile.Contains(id)) found.Add($"blocks: the game's block {id} ({g.Name}) isn't in data/blocks.json");
		return found;
	}

	// every field a data file has that the game doesn't read (fields starting with _ are notes), so a misspelt one can't be missed
	static void UnreadFields(JsonElement e, Type t, string path, List<string> found)
	{
		if (t.IsArray)
		{
			if (e.ValueKind == JsonValueKind.Array) { int k = 0; foreach (var x in e.EnumerateArray()) UnreadFields(x, t.GetElementType(), $"{path}[{k++}]", found); }
			return;
		}
		if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
		{
			if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) UnreadFields(p.Value, t.GetGenericArguments()[1], $"{path}.{p.Name}", found);
			return;
		}
		if (!t.IsClass || t == typeof(string) || e.ValueKind != JsonValueKind.Object) return;
		foreach (var p in e.EnumerateObject())
		{
			if (p.Name.StartsWith('_')) continue;
			var f = t.GetField(p.Name, BindingFlags.Public | BindingFlags.Instance);
			if (f == null) found.Add($"{path}.{p.Name}: the game doesn't read this");
			else UnreadFields(p.Value, f.FieldType, $"{path}.{p.Name}", found);
		}
	}
}
