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
}
