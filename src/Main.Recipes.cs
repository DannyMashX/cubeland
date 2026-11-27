using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Cubeland;

// The recipe book: the crafting lists (in your inventory, and at the Crafting Bench) with a search, a "Can make now"
// switch, and each recipe's ingredients as pictures with how many you have; and "How to make", every way to get an item
// (R over any item, or click an ingredient's picture).
public partial class Main
{
	readonly Dictionary<VBoxContainer, (LineEdit search, CheckBox can)> recipeFilters = new();
	Control RecipeList(out VBoxContainer list)
	{
		var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 4);
		var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 8); box.AddChild(head);
		var search = new LineEdit { PlaceholderText = "Search", ClearButtonEnabled = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		var can = new CheckBox { Text = "Can make now" };
		head.AddChild(search); head.AddChild(can);
		search.TextChanged += _ => RefreshPanels(); can.Toggled += _ => RefreshPanels();
		var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(440, 230), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; list.AddThemeConstantOverride("separation", 4);
		scroll.AddChild(list); box.AddChild(scroll);
		recipeFilters[list] = (search, can);
		return box;
	}
	// does a search find this recipe? (what it makes, or any of its ingredients)
	static bool Finds(string q, int outId, IEnumerable<int> needs) => q == "" || Blocks.B[outId].Name.ToLowerInvariant().Contains(q) || needs.Any(n => Blocks.B[n].Name.ToLowerInvariant().Contains(q));
	// fill a recipe list: the ones you can make right now first
	void DrawRecipes(VBoxContainer list, bool atBench, Label name)
	{
		foreach (var c in list.GetChildren()) c.QueueFree();
		var (search, can) = recipeFilters[list];
		string q = search.Text.Trim().ToLowerInvariant();
		foreach (var r in Inventory.Recipes.Where(r => (atBench || r.At == "hand") && (!r.Colour || r.Needs.Any(n => inv.CountOf(n.id) > 0)))
			.Where(r => Finds(q, r.Out, r.Needs.Select(n => n.id)) && (!can.ButtonPressed || inv.CanCraft(r))).OrderBy(r => inv.CanCraft(r) ? 0 : 1))
		{
			string title = (r.N > 1 ? r.N + " " : "") + Blocks.B[r.Out].Name;
			var row = RecipeRow(r.Out, r.N, r.Needs, inv.CanCraft(r), MissingTools(r));
			row.GuiInput += e =>
			{
				if (e is not InputEventMouseButton mb || !mb.Pressed || mb.ButtonIndex != MouseButton.Left) return;
				var ok = inv.Craft(r);
				name.Text = ok == true ? $"Made {title}" : ok == null ? "No room in your hotbar" : "You're missing something for that";
			};
			list.AddChild(row);
		}
	}
	// the tools a recipe needs that you haven't got ("an axe", "a pickaxe")
	List<string> MissingTools(Inventory.Recipe r)
	{
		var m = new List<string>();
		if (r.Axe && !inv.Id.Any(i => Blocks.Get(i)?.Tool == "axe")) m.Add("an axe");
		if (r.Tool != null && !inv.Id.Any(i => Blocks.Get(i)?.Tool == r.Tool)) m.Add((r.Tool[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "an " : "a ") + r.Tool);
		return m;
	}
	// one recipe: what it makes, then its ingredients' pictures with have/need (red when short), and what's missing
	Control RecipeRow(int outId, int n, (int id, int n)[] needs, bool ok, List<string> missingTools = null, bool showHave = true)
	{
		var row = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
		void Style(bool lit) => row.AddThemeStyleboxOverride("panel", Skin.Button(lit));
		Style(false); row.MouseEntered += () => Style(true); row.MouseExited += () => Style(false);
		row.Modulate = ok || !showHave ? Colors.White : new Color(1, 1, 1, 0.75f);
		var h = new HBoxContainer(); h.AddThemeConstantOverride("separation", 8); h.MouseFilter = Control.MouseFilterEnum.Ignore; row.AddChild(h);
		var outSlot = new Slot(44) { Interactive = false, MouseFilter = Control.MouseFilterEnum.Pass }; outSlot.Show(outId, n); h.AddChild(outSlot);
		var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 2); h.AddChild(col);
		var t = Text((n > 1 ? n + " " : "") + Blocks.B[outId].Name, 13); t.MouseFilter = Control.MouseFilterEnum.Ignore; col.AddChild(t);
		var ing = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }; ing.AddThemeConstantOverride("separation", 6); col.AddChild(ing);
		var missing = new List<string>();
		foreach (var (id, need) in needs)
		{
			int have = inv.CountOf(id);
			var s = new Slot(36, 2, 24) { Interactive = false }; s.Show(id); s.Clicked = _ => ShowHowTo(id);   // (click an ingredient: how to make it)
			ing.AddChild(s);
			var c = Text(showHave ? $"{Math.Min(have, 999)}/{need}" : $"x{need}", 12); c.MouseFilter = Control.MouseFilterEnum.Ignore;
			if (showHave && have < need) { c.AddThemeColorOverride("font_color", new Color(1, 0.45f, 0.4f)); missing.Add($"{need - have} {Blocks.B[id].Name}"); }
			ing.AddChild(c);
		}
		if (missingTools != null) missing.AddRange(missingTools);
		if (showHave && missing.Count > 0)
		{
			var m = Text("Missing: " + string.Join(", ", missing), 12); m.AddThemeColorOverride("font_color", new Color(1, 0.45f, 0.4f)); m.MouseFilter = Control.MouseFilterEnum.Ignore;
			col.AddChild(m);
		}
		return row;
	}

	// ===================== How to make =====================
	Control howPanel; VBoxContainer howList; Label howTitle; Control[] howHid = Array.Empty<Control>();
	void BuildHowTo()
	{
		howPanel = Screen("How to make", out var body);
		howTitle = FindTitle(body);
		var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(520, 360), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		howList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; howList.AddThemeConstantOverride("separation", 6);
		scroll.AddChild(howList); body.AddChild(scroll);
		var back = new Button { Text = "Back", SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
		back.Pressed += CloseHowTo; body.AddChild(back);
		body.AddChild(Text("Click an ingredient to see how to make that · Back or Esc to close", 12, 0.65f));
		howPanel.VisibilityChanged += () => { if (!howPanel.Visible) foreach (var c in howHid) c.Visible = true; };
	}
	void CloseHowTo() => howPanel.Visible = false;
	// every way to get item id: crafting recipes (by hand / at the Crafting Bench), the Cutter, the Kiln and the Foundry
	void ShowHowTo(int id)
	{
		if (howPanel == null || Blocks.Get(id) == null) return;
		if (!howPanel.Visible)
		{   // (it takes the place of the screen that's open, which comes back when it closes)
			howHid = new[] { invPanel, benchPanel, kilnPanel }.Where(p => p.Visible).ToArray();
			foreach (var p in howHid) p.Visible = false;
		}
		howTitle.Text = "How to make " + Blocks.B[id].Name;
		foreach (var c in howList.GetChildren()) c.QueueFree();
		int ways = 0;
		// one way: its title, then the ingredients' pictures each with how many it takes, then "makes N" (as the web version)
		void Way(string where, (int id, int n)[] needs, int makes)
		{
			var box = new PanelContainer(); box.AddThemeStyleboxOverride("panel", Skin.Button(false));
			var pad = new MarginContainer(); foreach (var side in new[] { "left", "top", "right", "bottom" }) pad.AddThemeConstantOverride("margin_" + side, 6); box.AddChild(pad);
			var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 4); pad.AddChild(col);
			col.AddChild(Text(where, 13, 0.85f));
			var line = new HBoxContainer(); line.AddThemeConstantOverride("separation", 6); col.AddChild(line);
			foreach (var (nid, need) in needs)
			{
				var s = new Slot(36, 2, 24) { Interactive = false }; s.Show(nid); s.Clicked = _ => ShowHowTo(nid);   // (click an ingredient: how to make it)
				s.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
				line.AddChild(s);
				var c = Text(need.ToString(), 13); c.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; line.AddChild(c);
			}
			var mk = Text($"makes {makes}", 13, 0.85f); mk.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; line.AddChild(mk);
			howList.AddChild(box); ways++;
		}
		foreach (var r in Inventory.Recipes.Where(r => r.Out == id))
			Way(r.At == "hand" ? "By hand" : "Crafting Bench" + (r.Axe ? " (with an axe)" : r.Tool != null ? $" (with a {r.Tool})" : ""), r.Needs, r.N);
		if (Blocks.Get(id) is { Form: { } form, Of: > 0 } fd)
		{
			var m = Inventory.BenchModes.FirstOrDefault(b => b.key == (form == "piece" ? "pieces" : form));
			if (m.key != null) Way("Cutter", new[] { (fd.Of, m.use) }, m.n);
		}
		foreach (var s in Kilns.Smelts.Where(s => s.Out == id))
			Way(s.At == "foundry" ? "Foundry" : "Kiln", s.Needs.Select(x => (x.Id, x.N)).ToArray(), 1);
		if (ways == 0) howList.AddChild(Text("Can't be made: found in the world", 14, 0.85f));
		howPanel.Visible = true;
		if (!invOpen) { invOpen = true; dim.Visible = true; Unlock(); }
	}

	// ===================== the Cutter's block list: search and "Can make now" =====================
	LineEdit cutSearch; CheckBox cutCan; Label cutNeed; readonly List<(int id, Slot s)> cutSlots = new();
	void FilterCutter()
	{
		if (cutSearch == null) return;
		string q = cutSearch.Text.Trim().ToLowerInvariant();
		int use = Inventory.BenchModes[benchMode].use;
		foreach (var (id, s) in cutSlots) s.Visible = (q == "" || Blocks.B[id].Name.ToLowerInvariant().Contains(q)) && (!cutCan.ButtonPressed || !inv.Survival || inv.CountOf(id) >= use);
	}

	// (test, Beta 0.1.6.0: --earthrecipes, in a survival Earthlike world: the Earthlike recipes are there, and a start goes from
	// bare hands to a stone pickaxe and a dried brick using only what lies near the start: Earthlike gravel dug by hand gives
	// Rocks, leaves a Stick, the Rocks a Crude Axe, the axe a log, then planks, a Crafting Bench, Sticks, Cobbled Andesite and a
	// Stone Pickaxe, which mines the rock there (its cobbled block goes back to rock in the Kiln); Clay Soil or Silt gives Clay Balls,
	// and one put down dries into a brick)
	bool earthRecipesTested;
	void EarthRecipeTest()
	{
		earthRecipesTested = true;
		void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
		int K(string k) => DataKeys.IdOf(k);
		Inventory.Recipe R(string o, string need) => Inventory.Recipes.FirstOrDefault(r => r.Out == K(o) && r.Needs.Any(n => n.id == K(need)));
		bool Is(Inventory.Recipe r, int n, string at, bool axe = false, string tool = null) => r != null && r.N == n && r.At == at && r.Axe == axe && r.Tool == tool;
		string[] woods = { "mahogany", "acacia", "stiltroot", "teak" };
		string[] rocks = { "limestone", "shale", "chalk", "granite", "basalt", "gabbro", "slate", "schist", "gneiss", "marble", "quartzite",
			"conglomerate", "siltstone", "dolomite", "travertine", "diorite", "rhyolite", "peridotite", "phyllite", "serpentinite", "hornfels" };
		var gravels = Enumerable.Range(5030, 22).Select(DataKeys.KeyOf).ToArray();
		Check("an Earthlike world, in survival", Earthlike && Survival);
		// 1. the rows
		Check("Rocks: 2 from 1 of each of the 22 Earthlike gravels, by hand (and not from plain Gravel)", gravels.All(g => Is(R("rock", g), 2, "hand")) && R("rock", "gravel") == null);
		Check("the 4 woods as the old woods: planks (log and stripped, axe), sticks, bench, Cutter, doors, fences, and their fuel",
			woods.All(w => Is(R($"{w}_planks", $"{w}_log"), 4, "hand", true) && Is(R($"{w}_planks", $"stripped_{w}_log"), 4, "hand", true) && Is(R("stick", $"{w}_planks"), 4, "bench", true)
				&& Is(R("crafting_bench", $"{w}_log"), 1, "hand") && Is(R("cutter", $"{w}_planks"), 1, "bench") && Is(R($"{w}_door", $"{w}_planks"), 2, "bench") && Is(R($"{w}_fence", $"{w}_planks"), 4, "bench")
				&& Kilns.Fuel.GetValueOrDefault(K($"{w}_log")) == 4 && Kilns.Fuel.GetValueOrDefault(K($"stripped_{w}_log")) == 4 && Kilns.Fuel.GetValueOrDefault(K($"{w}_planks")) == 2
				&& Kilns.Fuel.GetValueOrDefault(K($"{w}_door")) == 2 && Kilns.Fuel.GetValueOrDefault(K($"{w}_fence")) == 1));
		Check("the 21 rocks come back from their cobbled block in the Kiln", rocks.All(r => Kilns.Smelts.Any(s => s.Out == K(r) && s.Needs.Length == 1 && s.From == K("cobbled_" + r) && s.At == "kiln")));
		var bricks = rocks.Where(r => DataKeys.TryIdOf(r + "_bricks", out _)).ToArray(); var polished = rocks.Append("andesite").Where(r => DataKeys.TryIdOf("polished_" + r, out _)).ToArray();
		Check($"{bricks.Length} bricks and {polished.Length} polished rocks: 1 from 1 of the rock, at the bench, with a pickaxe",
			bricks.Length == 6 && polished.Length == 14 && bricks.All(r => Is(R(r + "_bricks", r), 1, "bench", tool: "pickaxe")) && polished.All(r => Is(R("polished_" + r, r), 1, "bench", tool: "pickaxe")));
		Check("Pink, Blue and White Dye from Fireweed, Gentian and Edelweiss (colour, by hand)",
			new[] { ("pink_dye", "fireweed"), ("blue_dye", "gentian"), ("white_dye", "edelweiss") }.All(p => Is(R(p.Item1, p.Item2), 1, "hand") && R(p.Item1, p.Item2).Colour));
		Check("Clay Balls: 2 from Clay Soil, 2 from Cracked Clay, 1 from 2 Silt (by hand)",
			Is(R("clay_ball", "clay_soil"), 2, "hand") && Is(R("clay_ball", "cracked_clay"), 2, "hand") && Is(R("clay_ball", "silt"), 1, "hand") && R("clay_ball", "silt").Needs[0].n == 2);
		Check("Fireclay from Clay + Sand + each Earthlike gravel, at the bench", gravels.All(g => Is(R("fireclay", g), 1, "bench")));
		Check("a Burnt Log burns (4) and splits into 6 Sticks with an axe", Kilns.Fuel.GetValueOrDefault(K("burnt_log")) == 4 && Is(R("stick", "burnt_log"), 6, "hand", true));
		Check("no new stone-tool rows (T1), nothing from Flint (F1) or Laterite (L1)", Inventory.Recipes.Count(r => r.Out == K("stone_pickaxe")) == 1
			&& !Inventory.Recipes.Any(r => r.Needs.Any(n => n.id == K("flint") || n.id == K("laterite"))));
		// 2. a start, from bare hands
		for (int i = 0; i < inv.Id.Length; i++) inv.SetSlot(i, 0);
		int sx = (int)MathF.Floor(player.Pos.X), sz = (int)MathF.Floor(player.Pos.Z);
		(int x, int y, int z)? Find(Func<int, bool> want, int r = 40)
		{
			(int, int, int)? best = null; int bd = int.MaxValue;
			for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++)
			{
				if (dx * dx + dz * dz >= bd) continue;
				int x = sx + dx, z = sz + dz;
				for (int y = World.WY - 1; y > 0; y--)
				{
					int v = world.GetBlock(x, y, z); if (v == 0) continue;
					if (want(v)) { best = (x, y, z); bd = dx * dx + dz * dz; break; }
					var b = Blocks.Get(v); if (b == null) break;
					if (Blocks.IsWater(v) || b.NoCollide || b.Leaves || b.Log) continue;
					break;
				}
			}
			return best;
		}
		void Hold(int slot) { SelectSlot(slot); }
		int Dig((int x, int y, int z) p, int block, int want)   // (put the block back if it's gone, clear above it, break it once; drops go into your bag)
		{
			foreach (var dr in drops.All.ToArray()) drops.Remove(dr);
			for (int u = 1; u <= 3; u++) if (world.GetBlock(p.x, p.y + u, p.z) != 0) world.SetBlock(p.x, p.y + u, p.z, 0);
			if (world.GetBlock(p.x, p.y, p.z) != block) world.SetBlock(p.x, p.y, p.z, block);
			player.Pos = new Vector3(p.x + 0.5f, p.y + 1.2f, p.z + 0.5f); player.Vel = Vector3.Zero; player.Flying = true; player.Pitch = -MathF.PI / 2 + 0.001f; player.Yaw = 0;
			Break();
			int got = 0; foreach (var dr in drops.All.ToArray()) { if (dr.Id == want) got++; inv.Give(dr.Id); drops.Remove(dr); }
			return got;
		}
		int Make(Inventory.Recipe r, int times) { int n = 0; for (int k = 0; k < times; k++) if (r != null && inv.Craft(r) == true) n++; return n; }
		Hold(8);
		var logAt = Find(v => Blocks.Get(v)?.Log == true && Blocks.LogOf(v) == v);
		var gravelAt = Find(v => v >= 5030 && v <= 5051);
		var leafAt = Find(v => Blocks.IsLeaves(v));
		Check($"near the start: a tree ({(logAt is { } l0 ? Blocks.B[world.GetBlock(l0.x, l0.y, l0.z)].Name : "none")}), Earthlike gravel ({(gravelAt is { } g0 ? Blocks.B[world.GetBlock(g0.x, g0.y, g0.z)].Name : "none")}) and leaves",
			logAt != null && gravelAt != null && leafAt != null);
		if (logAt is not { } lp || gravelAt is not { } gp || leafAt is not { } fp) return;
		int log = world.GetBlock(lp.x, lp.y, lp.z), gravel = world.GetBlock(gp.x, gp.y, gp.z), leaf = world.GetBlock(fp.x, fp.y, fp.z);
		Check("bare hands can't cut the log", Dig(lp, log, log) == 0 && world.GetBlock(lp.x, lp.y, lp.z) == log);
		int gravels7 = 0; for (int k = 0; k < 7; k++) gravels7 += Dig(gp, gravel, gravel);
		Check($"dug by hand, the gravel comes up whole ({gravels7} of 7)", gravels7 == 7 && inv.CountOf(gravel) == 7);
		int sticks = 0; for (int k = 0; k < 300 && sticks == 0; k++) sticks += Dig(fp, leaf, Blocks.Stick);
		Check("leaves broken by hand give a Stick now and then", sticks >= 1);
		Check($"7 gravel make 14 Rocks ({Make(R("rock", DataKeys.KeyOf(gravel)), 7)} made, {inv.CountOf(Blocks.Rock)} Rocks)", inv.CountOf(Blocks.Rock) == 14);
		Check("2 Rocks and a Stick make a Crude Axe", Make(R("crude_axe", "rock"), 1) == 1 && inv.CountOf(Blocks.CrudeAxe) == 1);
		int ai = Array.IndexOf(inv.Id, Blocks.CrudeAxe); inv.SetSlot(ai, 0); inv.SetSlot(8, Blocks.CrudeAxe); Hold(8);
		int logs = 0; for (int k = 0; k < 3; k++) logs += Dig(lp, log, log);
		Check($"with the Crude Axe the log comes out (3 of 3: {logs})", logs == 3);
		var planksR = Inventory.Recipes.First(r => r.Needs.Length == 1 && r.Needs[0].id == log && r.Axe && Blocks.B[r.Out].Name.EndsWith("Planks"));
		int planks = planksR.Out;
		Check($"logs make {Blocks.B[planks].Name}, a Crafting Bench and Sticks",
			Make(planksR, 2) == 2 && Make(Inventory.Recipes.First(r => r.Out == Blocks.CraftingBench && r.Needs.Any(n => n.id == log)), 1) == 1
			&& Make(Inventory.Recipes.First(r => r.Out == Blocks.Stick && r.Needs.Any(n => n.id == planks)), 1) == 1 && inv.CountOf(Blocks.Stick) >= 4);
		Check("12 Rocks make 3 Cobbled Andesite, and those a Stone Pickaxe", Make(R("cobbled_andesite", "rock"), 3) == 3 && Make(R("stone_pickaxe", "cobbled_andesite"), 1) == 1);
		int pi = Array.IndexOf(inv.Id, K("stone_pickaxe")); inv.SetSlot(pi, 0); inv.SetSlot(8, K("stone_pickaxe")); Hold(8);
		var rockAt = Find(v => Blocks.Get(v)?.Needs == "pickaxe" && Kilns.Smelts.Any(s => s.Out == v && s.From == DropFor(v)));
		if (rockAt is { } rp)
		{
			int rock = world.GetBlock(rp.x, rp.y, rp.z), cob = DropFor(rock);
			Check($"the Stone Pickaxe mines the {Blocks.B[rock].Name} there into {Blocks.B[cob].Name}, which the Kiln turns back", Dig(rp, rock, cob) == 1);
		}
		else Check("bare rock near the start", false);
		inv.SetSlot(7, 0); Hold(7);
		var clayAt = Find(v => v is 4805 or 4815 or 3928 or 3930);
		if (clayAt is { } cp)
		{
			int cb = world.GetBlock(cp.x, cp.y, cp.z), got = DropFor(cb); int n = got == K("silt") ? 2 : 1, dug = 0;
			for (int k = 0; k < n; k++) dug += Dig(cp, cb, got);
			Check($"{Blocks.B[cb].Name} dug by hand gives {Blocks.B[got].Name}, which makes Clay Balls", dug == n && Make(R("clay_ball", DataKeys.KeyOf(got)), 1) == 1 && inv.CountOf(Blocks.ClayBall) >= 1);
			int bx = cp.x, by = cp.y + 1, bz = cp.z;
			world.SetBlock(bx, by, bz, Blocks.ClayBallBlock);
			for (int s = 0; s < (int)World.DryTime + 5; s++) world.TickTimers(1);
			Check("a Clay Ball put down dries into a brick", Blocks.Plain(world.GetBlock(bx, by, bz)) == Blocks.BrickBlock);
		}
		else Check("Clay Soil, Cracked Clay or Silt near the start", false);
		player.Pos = new Vector3(sx + 0.5f, player.Pos.Y + 6, sz + 0.5f);
	}
}
