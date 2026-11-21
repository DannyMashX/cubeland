using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Cubeland;

// The hotbar, and the screens that open over the game: the inventory (E), the Crafting Bench and the Kiln.
public partial class Main
{
	CanvasLayer uiLayer;
	readonly Slot[] hotSlots = new Slot[Inventory.Slots];
	readonly Slot[] gearSlots = new Slot[Inventory.NS];   // survival: the rest of your slots, in the inventory
	Slot offSlot; Control offBox; Label packTitle; Control packWrap;
	// the faint word in an empty gear slot
	static string SlotWord(int i) => i >= Inventory.Armor0 && i < Inventory.BackSlot ? new[] { "Head", "Chest", "Legs", "Feet" }[i - Inventory.Armor0]
		: i == Inventory.BackSlot ? "Pack" : i == Inventory.BeltSlot ? "Belt" : i >= Inventory.Tool0 && i < Inventory.Off0 ? "Tool" : "";
	static string SlotName(int i) => i >= Inventory.Armor0 && i < Inventory.BackSlot ? Blocks.ArmorPieces[i - Inventory.Armor0] switch { var s => char.ToUpper(s[0]) + s[1..] }
		: i == Inventory.BackSlot ? "Backpack" : i == Inventory.BeltSlot ? "Utility Belt" : i >= Inventory.Tool0 && i < Inventory.Off0 ? "Tool" : "Empty";
	// drag slot `from` onto slot `to`
	void MoveSlot(int from, int to) { if (!inv.Move(from, to)) { if (invName != null) invName.Text = "That doesn't go there"; ShowNote("That doesn't go there"); } }
	// one of the inventory's gear slots: drag to move, right-click to throw out
	Slot GearSlot(int i)
	{
		var s = new Slot();
		s.Clicked = b => { if (invOpen && b == MouseButton.Right) ThrowSlot(i); };
		s.DragData = () => invOpen && inv.Id[i] != 0 ? "slot:" + i : null;
		s.CanDrop = d => invOpen && d.StartsWith("slot:");
		s.Dropped = d => MoveSlot(int.Parse(d[5..]), i);
		s.Hover = () => invName.Text = inv.Id[i] != 0 ? Blocks.B[inv.Id[i]].Name : SlotName(i);
		s.Leave = () => invName.Text = "";
		gearSlots[i] = s;
		return s;
	}
	Label nameLabel; float nameTimer;
	bool invOpen;
	Control dim, invPanel, benchPanel, kilnPanel;
	int kilnAt = -1;

	static StyleBox PanelStyle() => Skin.Panel();   // (the menu box from gui_atlas.png)
	static Label Text(string t, int size = 14, float alpha = 1)
	{
		var l = new Label { Text = t, Modulate = new Color(1, 1, 1, alpha) };
		l.AddThemeFontSizeOverride("font_size", Skin.Size(size));   // (whole multiples of 12 keep your letters sharp)
		return l;
	}

	void BuildHud()
	{
		// ---- hotbar ----
		var bottom = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = Control.MouseFilterEnum.Ignore };
		bottom.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		bottom.AddThemeConstantOverride("separation", 8);
		uiLayer.AddChild(bottom);
		nameLabel = Text("", 15); nameLabel.HorizontalAlignment = HorizontalAlignment.Center;
		nameLabel.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
		bottom.AddChild(nameLabel);
		var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		bottom.AddChild(center);
		var barBg = new PanelContainer();
		barBg.AddThemeStyleboxOverride("panel", Skin.Panel(4, 4));   // (the hotbar is a menu box, like the web version)
		var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 4);
		center.AddChild(col);
		heartsHolder = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };   // (survival: your hearts, above the left end of the hotbar)
		col.AddChild(heartsHolder);
		// survival: the offhand slot to the left of the hotbar (R picks the next one, F swaps it with your hand)
		var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 8);
		center.RemoveChild(col); center.AddChild(row);
		var offPanel = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkEnd, Visible = inv.Survival };
		offPanel.AddThemeStyleboxOverride("panel", Skin.Panel(4, 4));
		offSlot = new Slot { Interactive = false }; offPanel.AddChild(offSlot); offBox = offPanel;
		row.AddChild(offPanel); row.AddChild(col);
		col.AddChild(barBg);
		var bar = new HBoxContainer(); bar.AddThemeConstantOverride("separation", 4);
		barBg.AddChild(bar);
		bottom.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4), MouseFilter = Control.MouseFilterEnum.Ignore });
		for (int i = 0; i < Inventory.Slots; i++)
		{
			int k = i;
			var s = new Slot(); s.Num.Text = ((i + 1) % 10).ToString();
			s.Clicked = b =>
			{
				if (!invOpen) return;
				if (b == MouseButton.Right) { if (inv.Survival) ThrowSlot(k); else inv.SetSlot(k, 0); return; }
				SelectSlot(k);
			};
			s.DragData = () => invOpen && inv.Id[k] != 0 ? "slot:" + k : null;
			s.CanDrop = d => invOpen && (d.StartsWith("slot:") || (d.StartsWith("item:") && !inv.Survival));
			s.Dropped = d =>
			{
				if (d.StartsWith("slot:")) MoveSlot(int.Parse(d[5..]), k);
				else inv.SetSlot(k, int.Parse(d[5..]));
				SelectSlot(k);
			};
			hotSlots[i] = s; bar.AddChild(s);
		}
		inv.Changed = () => { DrawHotbar(); RefreshPanels(); };
		// a belt came off: what was in the offhand slots it gave drops in front of you (in a friend's world, into your other slots)
		inv.ThrowOut = k => { if (netClient) { if (inv.Give(inv.Id[k], inv.Wear[k], inv.Data[k])) inv.SetSlot(k, 0); } else ThrowSlot(k); return true; };

		// ---- the screens (one at a time), over a dark backdrop ----
		dim = new ColorRect { Color = new Color(0, 0, 0, 0.45f), Visible = false };
		dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		uiLayer.AddChild(dim);
		uiLayer.MoveChild(dim, bottom.GetIndex());   // (behind the hotbar, which you use while a screen is open)
		invPanel = BuildInventory(); benchPanel = BuildBench(); kilnPanel = BuildKiln();
		DrawHotbar(); SelectSlot(0);
	}
	Control Screen(string title, out VBoxContainer body)
	{
		var wrap = new CenterContainer { Visible = false };
		wrap.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		wrap.OffsetBottom = -70;   // (clear of the hotbar)
		dim.AddChild(wrap);
		var panel = new PanelContainer(); panel.AddThemeStyleboxOverride("panel", PanelStyle());
		wrap.AddChild(panel);
		body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 8);
		panel.AddChild(body);
		body.AddChild(Text(title, 17));
		return wrap;
	}

	// ===================== Inventory (E) =====================
	// Creative: each block once, in tabs, then the items. A block with other forms (pieces, slabs, stairs...) has
	// a + on it: click the + to see them all in a row underneath. Survival: just the recipes you can make by hand.
	Label invName; VBoxContainer invRecipes; GridContainer invGrid; HBoxContainer invVariants; int openVariants; Button openPlus;
	readonly List<Button> invTabButtons = new();
	static readonly (string name, int[] ids)[] InvTabs =
	{
		("Building Blocks", new[] { 7, 61, Blocks.FirPlanks, Blocks.StrippedFirLog, Blocks.PalmPlanks, Blocks.StrippedPalmLog, Blocks.WillowPlanks, Blocks.StrippedWillowLog, Blocks.SandstoneBricks, Blocks.ChiseledSandstone, Blocks.SmoothSandstone, Blocks.MudBricks, 8 }.Concat(Blocks.DeepShown(8)).Append(9).Concat(Blocks.DeepShown(9)).Append(64).Concat(Blocks.ColourBricks).Append(65).Concat(Blocks.ColourTerracotta).Append(67).Concat(Blocks.ColourChiseled)
			.Append(69).Concat(Blocks.DeepShown(69)).Concat(new[] { 10, 22, Blocks.CraftingBench, Blocks.Cutter, Blocks.Furnace }).Concat(Blocks.FireclayBlocks).Append(Blocks.Foundry).ToArray()),
		("Natural Blocks", new[] { 1, 2, 3 }.Concat(Blocks.DeepShown(3)).Concat(Blocks.Ores).Concat(new[] { 4, 5, Blocks.FirLog, Blocks.FirLeaves, Blocks.PalmLog, Blocks.PalmLeaves, Blocks.WillowLog, Blocks.WillowLeaves, 6, Blocks.Sandstone, Blocks.SnowyGrass, Blocks.SwampGrass, Blocks.Mud, Blocks.Peat, Blocks.Ice, 11, 63, Blocks.Fireclay }).Concat(Blocks.Flowers).Append(Blocks.Water).Concat(Blocks.Soils).ToArray()),
		("Items", new[] { Blocks.Stick, Blocks.Rock, Blocks.ClayBall, Blocks.Brick, Blocks.FireclayBall, Blocks.FireclayBrick, 630, 631, 632, 633, 634, 635, 636, 637, 638, 639, Blocks.SteelScrap, Blocks.SteelBrick, Blocks.FireEssence, Blocks.Bone, Blocks.BoneAsh, Blocks.GloomWisp, Blocks.GrainSeeds, Blocks.Grain, Blocks.Flour, Blocks.Bread, Blocks.Dates, Blocks.Leather, Blocks.RawMeat, Blocks.CookedMeat }.Concat(Blocks.Dye).Append(Blocks.CrudeAxe).Concat(Blocks.Tools).Concat(Blocks.GearItems).ToArray()),
	};
	static int TabOf(int id) => Math.Max(0, Array.FindIndex(InvTabs, t => t.ids.Contains(id)));
	static int[] VariantsOf(int b) => Array.IndexOf(Blocks.Splittable, b) >= 0
		? new[] { "piece", "slab", "stairs", "quarter", "vslab", "vstairs", "vquarter" }.Select(f => Blocks.FormItem(f, b)).ToArray()
		: b == Blocks.Torch ? new[] { Blocks.SmolderingTorch, Blocks.BurntTorch, Blocks.EverTorch } : Array.Empty<int>();
	Control BuildInventory()
	{
		var scr = Screen("Inventory", out var body);
		if (!inv.Survival)
		{
			var tabs = new HBoxContainer(); body.AddChild(tabs);
			for (int k = 0; k < InvTabs.Length; k++) { int kk = k; var b = new Button { Text = InvTabs[k].name, ToggleMode = true }; b.Pressed += () => ShowInvTab(kk); tabs.AddChild(b); invTabButtons.Add(b); }
			invGrid = new GridContainer { Columns = 9 }; invGrid.AddThemeConstantOverride("h_separation", 4); invGrid.AddThemeConstantOverride("v_separation", 4);
			body.AddChild(invGrid);
			var list = Blocks.Items.Concat(new[] { Blocks.Stick, Blocks.Rock, Blocks.CrudeAxe, Blocks.ClayBall, Blocks.Brick, Blocks.FireEssence, Blocks.Bone, Blocks.BoneAsh, Blocks.GloomWisp, Blocks.GrainSeeds, Blocks.Grain, Blocks.Flour, Blocks.Bread, Blocks.Dates, Blocks.Leather, Blocks.RawMeat, Blocks.CookedMeat }).Concat(Blocks.Dye).Concat(Blocks.Tools).Concat(Blocks.PackItems).Concat(Blocks.GearItems)
				.OrderBy(TabOf).ThenBy(id => Array.IndexOf(InvTabs[TabOf(id)].ids, id)).ToList();
			foreach (int id in list)
			{
				var s = InvSlot(id); s.SetMeta("tab", TabOf(id)); invGrid.AddChild(s);
				if (VariantsOf(id).Length == 0) continue;
				// a small + tucked inside the slot's bottom-right corner, clear of the block's picture
				var plus = new Button { Position = new Vector2(32, 32), Size = new Vector2(10, 10), TooltipText = "Show the other forms" };
				// the + (or - once open) is two little bars, so it's exactly in the middle
				plus.AddChild(new ColorRect { Position = new Vector2(2, 4), Size = new Vector2(6, 2), MouseFilter = Control.MouseFilterEnum.Ignore });
				plus.AddChild(new ColorRect { Position = new Vector2(4, 2), Size = new Vector2(2, 6), MouseFilter = Control.MouseFilterEnum.Ignore });
				foreach (var st in new[] { "normal", "hover", "pressed" }) plus.AddThemeStyleboxOverride(st, Skin.Button(st != "normal"));
				plus.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
				plus.Pressed += () => ShowVariants(id, plus);
				plus.SetDeferred(Control.PropertyName.Size, new Vector2(10, 10));   // (something widens it on its way into the slot)
				s.AddChild(plus);
			}
			invVariants = new HBoxContainer { Visible = false }; invVariants.AddThemeConstantOverride("separation", 4);
			body.AddChild(invVariants);
		}
		else
		{   // survival: your slots on the left, crafting on the right
			var split = new HBoxContainer(); split.AddThemeConstantOverride("separation", 20);
			body.AddChild(split);
			var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(10 * 48 + 12, 460), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
			split.AddChild(scroll);
			var left = new VBoxContainer(); left.AddThemeConstantOverride("separation", 4); scroll.AddChild(left);
			HBoxContainer Row(Control into) { var r = new HBoxContainer(); r.AddThemeConstantOverride("separation", 4); into.AddChild(r); return r; }
			GridContainer Grid(Control into) { var g = new GridContainer { Columns = 10 }; g.AddThemeConstantOverride("h_separation", 4); g.AddThemeConstantOverride("v_separation", 4); into.AddChild(g); return g; }
			void Head(Control into, string t) => into.AddChild(Text(t, 12, 0.85f));
			var top = Row(left); top.AddThemeConstantOverride("separation", 16);
			var armor = new VBoxContainer(); top.AddChild(armor); Head(armor, "Armor"); var ar = Row(armor);
			for (int k = 0; k < 4; k++) ar.AddChild(GearSlot(Inventory.Armor0 + k));
			var equip = new VBoxContainer(); top.AddChild(equip); Head(equip, "Backpack & Belt"); var eq = Row(equip);
			eq.AddChild(GearSlot(Inventory.BackSlot)); eq.AddChild(GearSlot(Inventory.BeltSlot));
			Head(left, "Tools (the right one is picked for you)"); var tl = Grid(left);
			for (int i = Inventory.Tool0; i < Inventory.Off0; i++) tl.AddChild(GearSlot(i));
			Head(left, "Offhand (R picks one · F swaps it with your hand · right-click uses it)"); var off = Grid(left);
			for (int i = Inventory.Off0; i < Inventory.Armor0; i++) off.AddChild(GearSlot(i));
			Head(left, "Pockets"); var pk = Grid(left);
			for (int i = Inventory.Pocket0; i < Inventory.Pack0; i++) pk.AddChild(GearSlot(i));
			var pw = new VBoxContainer(); pw.AddThemeConstantOverride("separation", 4); left.AddChild(pw); packWrap = pw;
			packTitle = Text("Backpack", 12, 0.85f); pw.AddChild(packTitle); var pg = Grid(pw);
			for (int i = Inventory.Pack0; i < Inventory.Tool0; i++) pg.AddChild(GearSlot(i));
			var craft = new VBoxContainer(); craft.AddThemeConstantOverride("separation", 4); split.AddChild(craft);
			craft.AddChild(Text("Crafting", 12, 0.85f));
			var rl = RecipeList(out invRecipes); rl.CustomMinimumSize = new Vector2(440, 440); craft.AddChild(rl);
		}
		invName = Text(""); invName.CustomMinimumSize = new Vector2(0, 18); body.AddChild(invName);
		body.AddChild(Text(inv.Survival
			? "Survival: break blocks to collect them · click a recipe to craft it (tools, armor and gear need a Crafting Bench)\ndrag slots to move or stack them · right-click a slot to throw it out · E to close"
			: "Click a block to put it in the selected hotbar slot, or drag it onto a slot · right-click a hotbar slot to empty it · E to close", 12, 0.65f));
		if (!inv.Survival) ShowInvTab(0);
		return scr;
	}
	Slot InvSlot(int id)
	{
		var s = new Slot(); s.Show(id);
		s.Clicked = b => { if (b == MouseButton.Left) inv.SetSlot(inv.Selected, id); };
		s.DragData = () => "item:" + id;
		s.Hover = () => invName.Text = Blocks.B[id].Name; s.Leave = () => invName.Text = "";
		return s;
	}
	void ShowInvTab(int k)
	{
		for (int j = 0; j < invTabButtons.Count; j++) invTabButtons[j].ButtonPressed = j == k;
		foreach (var c in invGrid.GetChildren()) if (c is Slot s) s.Visible = (int)s.GetMeta("tab") == k;
		ShowVariants(0, null);
	}
	void ShowVariants(int b, Button plus)
	{
		if (openPlus != null) openPlus.GetChild<ColorRect>(1).Visible = true;
		foreach (var c in invVariants.GetChildren()) c.QueueFree();
		if (b == 0 || openVariants == b) { openVariants = 0; openPlus = null; invVariants.Visible = false; return; }
		openVariants = b; openPlus = plus; if (plus != null) plus.GetChild<ColorRect>(1).Visible = false;
		invVariants.AddChild(Text(Blocks.B[b].Name, 13, 0.8f));
		foreach (int id in new[] { b }.Concat(VariantsOf(b))) invVariants.AddChild(InvSlot(id));
		invVariants.Visible = true;
	}
	Control RecipeList(out VBoxContainer list)
	{
		var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(440, 230), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; list.AddThemeConstantOverride("separation", 4);
		scroll.AddChild(list);
		return scroll;
	}
	// fill a recipe list: the ones you can make right now first
	void DrawRecipes(VBoxContainer list, bool atBench, Label name)
	{
		foreach (var c in list.GetChildren()) c.QueueFree();
		foreach (var r in Inventory.Recipes.Where(r => (atBench || r.At == "hand") && (!r.Colour || r.Needs.Any(n => inv.CountOf(n.id) > 0))).OrderBy(r => inv.CanCraft(r) ? 0 : 1))
		{
			string title = (r.N > 1 ? r.N + " " : "") + Blocks.B[r.Out].Name;
			var b = new Button { Text = $"{title}\n{Inventory.NeedsText(r)}", Icon = Atlas.SlotIcon(r.Out), Alignment = HorizontalAlignment.Left,
				Disabled = !inv.CanCraft(r), TextureFilter = Atlas.Is3DIcon(r.Out) ? CanvasItem.TextureFilterEnum.Linear : CanvasItem.TextureFilterEnum.Nearest, ExpandIcon = true };
			b.AddThemeConstantOverride("icon_max_width", 32);
			b.Pressed += () =>
			{
				var ok = inv.Craft(r);
				name.Text = ok == true ? $"Made {title}" : ok == null ? "No room in your hotbar" : "";
			};
			list.AddChild(b);
		}
	}

	// ===================== Crafting Bench =====================
	// Pick a block to put on the bench and what to cut it into; click what comes out to take it into your hotbar.
	Label benchName, benchCount, benchTitle; Slot benchIn, benchOut; VBoxContainer benchRecipes; int benchBlock, benchMode;
	// the same screen is the Crafting Bench (its recipes) or the Cutter (cutting blocks into pieces, slabs and stairs)
	bool benchIsCutter; Control[] cutterParts, benchParts;
	readonly List<Button> benchModeButtons = new();
	Control BuildBench()
	{
		var scr = Screen("Crafting Bench", out var body);
		benchTitle = FindTitle(body);
		var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 18); body.AddChild(row);
		var grid = new GridContainer { Columns = 7 }; grid.AddThemeConstantOverride("h_separation", 4); grid.AddThemeConstantOverride("v_separation", 4);
		row.AddChild(grid);
		foreach (int id in Blocks.Splittable.Where(b => World.TallWorlds || Array.IndexOf(Blocks.DeepBlocks, b) < 0))   // (the deep layers' stone only while tall worlds are on)
		{
			var s = new Slot(); s.Show(id);
			s.Clicked = _ => { benchBlock = id; DrawBench(); };
			s.Hover = () => benchName.Text = Blocks.B[id].Name; s.Leave = () => benchName.Text = "";
			grid.AddChild(s);
		}
		var craft = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; craft.AddThemeConstantOverride("separation", 10);
		row.AddChild(craft);
		benchIn = new Slot(56) { Interactive = false, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter }; craft.AddChild(benchIn);
		craft.AddChild(Text("→", 22, 0.8f));
		benchOut = new Slot(56) { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter }; craft.AddChild(benchOut);
		benchCount = benchOut.Count;
		benchOut.Clicked = _ => { if (benchBlock != 0) benchName.Text = inv.BenchCut(benchBlock, benchMode); };
		benchOut.Hover = () =>
		{
			if (benchBlock == 0) return;
			var m = Inventory.BenchModes[benchMode];
			benchName.Text = m.key == "pieces" ? (inv.InfinitePiece(Blocks.FormItem("piece", benchBlock)) ? $"{Blocks.B[benchBlock].Name} Piece (click to take)" : $"{Blocks.B[benchBlock].Name} Piece x8 (click to take)")
				: $"{Blocks.B[Blocks.BenchOut(m.key, benchBlock)].Name} x{m.n} (click to take)";
		};
		var modes = new HFlowContainer(); body.AddChild(modes);
		for (int k = 0; k < Inventory.BenchModes.Length; k++)
		{
			int kk = k; var b = new Button { Text = Inventory.BenchModes[k].name, ToggleMode = true };
			b.Pressed += () => { benchMode = kk; DrawBench(); };
			modes.AddChild(b); benchModeButtons.Add(b);
		}
		var parts = new List<Control>();
		if (inv.Survival) { var h = Text("Crafting", 15); body.AddChild(h); var list = RecipeList(out benchRecipes); body.AddChild(list); parts.Add(h); parts.Add(list); }
		benchName = Text(""); benchName.CustomMinimumSize = new Vector2(0, 18); body.AddChild(benchName);
		var cutHint = Text("Pick a block, pick what to cut it into, then click what it makes to take it · Shift + right-click to build against the Cutter · E to close", 12, 0.65f);
		var craftHint = Text("Click a recipe to craft it · Shift + right-click to build against the bench · E to close", 12, 0.65f);
		body.AddChild(cutHint); body.AddChild(craftHint); parts.Add(craftHint);
		cutterParts = new Control[] { row, modes, cutHint }; benchParts = parts.ToArray();
		return scr;
	}
	void OpenBench(bool cutter)
	{
		benchIsCutter = cutter;
		if (benchTitle != null) benchTitle.Text = cutter ? "Cutter" : "Crafting Bench";
		foreach (var c in cutterParts) c.Visible = cutter;
		foreach (var c in benchParts) c.Visible = !cutter;
		OpenPanel(benchPanel);
	}
	// a screen's title (the first label in it)
	static Label FindTitle(Node body) { foreach (var c in body.GetChildren()) if (c is Label l) return l; return null; }
	void DrawBench()
	{
		var m = Inventory.BenchModes[benchMode];
		benchIn.Show(benchBlock);
		benchOut.Show(benchBlock != 0 ? Blocks.BenchOut(m.key, benchBlock) : 0);
		benchCount.Text = benchBlock != 0 && !(m.key == "pieces" && inv.InfinitePiece(Blocks.FormItem("piece", benchBlock))) ? m.n.ToString() : "";
		for (int k = 0; k < benchModeButtons.Count; k++) benchModeButtons[k].ButtonPressed = k == benchMode;
	}

	// ===================== Kiln =====================
	Label kilnName, kilnStatus, kilnTitle; Slot kilnFuel; VBoxContainer kilnRecipes; HBoxContainer kilnOut;
	Control BuildKiln()
	{
		var scr = Screen("Kiln", out var body);
		kilnTitle = body.GetChild<Label>(0);   // Kiln or Foundry
		var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 12); body.AddChild(row);
		kilnFuel = new Slot(56) { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter }; row.AddChild(kilnFuel);
		kilnFuel.Clicked = b => { if (b == MouseButton.Right) TakeFuel(); else AddFuel(); };
		var col = new VBoxContainer(); row.AddChild(col);
		col.AddChild(Text("Fuel", 13, 0.8f));
		kilnStatus = Text("", 14); col.AddChild(kilnStatus);
		kilnRecipes = new VBoxContainer(); kilnRecipes.AddThemeConstantOverride("separation", 4); body.AddChild(kilnRecipes);
		body.AddChild(Text("Made (click to take)", 13, 0.8f));
		kilnOut = new HBoxContainer { CustomMinimumSize = new Vector2(0, 44) }; kilnOut.AddThemeConstantOverride("separation", 4); body.AddChild(kilnOut);
		kilnName = Text(""); kilnName.CustomMinimumSize = new Vector2(0, 18); body.AddChild(kilnName);
		body.AddChild(Text("Click the fuel slot to put in Logs, Planks or Sticks (right-click to take it out) · click a recipe to smelt it · E to close", 12, 0.65f));
		return scr;
	}
	void DrawKiln()
	{
		if (kilnAt < 0) return;
		var f = kilns.Of(kilnAt);
		bool foundry = Blocks.IsFoundry(world.Blocks[kilnAt]);
		kilnTitle.Text = foundry ? "Foundry" : "Kiln";
		kilnFuel.Show(inv.Survival ? f.Fuel : 0, inv.Survival ? f.FuelN : 0);
		DrawKilnStatus();
		foreach (var c in kilnRecipes.GetChildren()) c.QueueFree();
		foreach (var r in Kilns.Smelts)
		{
			if (r.At != (foundry ? "foundry" : "kiln")) continue;
			int outId = r.Out; var needs = r.Needs;
			var b = new Button { Text = $"{Blocks.B[outId].Name}\n{string.Join(" + ", needs.Select(n => $"{n.N} {Blocks.B[n.Id].Name}"))} · {Kilns.SmeltTime} seconds", Icon = Atlas.SlotIcon(outId),
				Alignment = HorizontalAlignment.Left, Disabled = inv.Survival && needs.Any(n => inv.CountOf(n.Id) < n.N), TextureFilter = Atlas.Is3DIcon(outId) ? CanvasItem.TextureFilterEnum.Linear : CanvasItem.TextureFilterEnum.Nearest, ExpandIcon = true };
			b.AddThemeConstantOverride("icon_max_width", 32);
			b.Pressed += () =>
			{
				var g = kilns.Of(kilnAt);
				if (g.Queue.Count >= 64) { kilnName.Text = foundry ? "The foundry is full up" : "The kiln is full up"; return; }
				if (inv.Survival && needs.Any(n => inv.CountOf(n.Id) < n.N)) return;
				if (inv.Survival) foreach (var (id, n) in needs) inv.Take(id, n);
				g.Queue.Add(outId);
				kilnName.Text = $"Smelting {Blocks.B[outId].Name}";
				DrawKiln();
			};
			kilnRecipes.AddChild(b);
		}
		// what it has made waits inside it until you take it
		foreach (var c in kilnOut.GetChildren()) c.QueueFree();
		foreach (var (id, n) in f.Done)
		{
			var s = new Slot(); s.Show(id, n); s.TooltipText = $"{Blocks.B[id].Name} (click to take)";
			s.Clicked = _ =>
			{
				var g = kilns.Of(kilnAt); int took = 0;
				while (g.Done.GetValueOrDefault(id) > 0 && inv.Give(id)) { g.Done[id]--; took++; }
				if (g.Done.GetValueOrDefault(id) == 0) g.Done.Remove(id);
				kilnName.Text = took > 0 ? $"Took {took} {Blocks.B[id].Name}" : "No room in your hotbar";
				DrawKiln();
			};
			kilnOut.AddChild(s);
		}
	}
	void DrawKilnStatus()
	{
		if (kilnAt >= 0 && Blocks.IsFoundry(world.Blocks[kilnAt]) && !kilns.FoundryBuilt(kilnAt)) { kilnStatus.Text = "Build the rest of the Foundry around it"; return; }
		if (kilnAt < 0 || !kilns.All.TryGetValue(kilnAt, out var f) || f.Queue.Count == 0) { kilnStatus.Text = "Pick something to smelt"; return; }
		string more = f.Queue.Count > 1 ? $" ({f.Queue.Count - 1} more waiting)" : "";
		kilnStatus.Text = f.Burning ? $"Making {Blocks.B[f.Queue[0]].Name}: {Math.Max(1, (int)Math.Ceiling(Kilns.SmeltTime - f.T))} s{more}" : $"Needs fuel{more}";
	}
	// fuel slot: click to put in fuel from your hotbar (the selected slot if it's fuel, otherwise the first fuel)
	void AddFuel()
	{
		if (!inv.Survival) { kilnName.Text = "Creative: no fuel needed"; return; }
		var f = kilns.Of(kilnAt);
		bool Ok(int id) => Kilns.Fuel.ContainsKey(id) && (f.Fuel == 0 || f.Fuel == id);
		int s = Ok(inv.Held) ? inv.Selected : Array.FindIndex(inv.Id, Ok);
		if (s < 0) { kilnName.Text = f.Fuel != 0 ? $"Add more {Blocks.B[f.Fuel].Name}, or right-click to take it out" : "Fuel: Logs, Planks or Sticks from your hotbar"; return; }
		int n = Math.Min(inv.Count[s], Blocks.StackMax - f.FuelN);
		if (n <= 0) { kilnName.Text = "The fuel slot is full"; return; }
		f.Fuel = inv.Id[s]; f.FuelN += n;
		inv.Count[s] -= n; if (inv.Count[s] == 0) inv.Id[s] = 0;
		inv.Changed?.Invoke();
		kilnName.Text = $"Put in {n} {Blocks.B[f.Fuel].Name}";
		DrawKiln();
	}
	void TakeFuel()
	{
		if (!kilns.All.TryGetValue(kilnAt, out var f) || f.FuelN == 0) return;
		while (f.FuelN > 0 && inv.Give(f.Fuel)) f.FuelN--;
		if (f.FuelN == 0) f.Fuel = 0;
		kilnName.Text = f.FuelN > 0 ? "No room in your hotbar" : "Took the fuel out";
		DrawKiln();
	}

	// ===================== opening and closing the screens =====================
	void OpenPanel(Control panel)
	{
		invOpen = true;
		dim.Visible = true;
		foreach (var p in new[] { invPanel, benchPanel, kilnPanel }) p.Visible = p == panel;
		if (panel != kilnPanel) kilnAt = -1;
		invName.Text = ""; benchName.Text = ""; kilnName.Text = "";
		RefreshPanels();
		Input.MouseMode = Input.MouseModeEnum.Visible;
	}
	void CloseInventory()
	{
		invOpen = false; kilnAt = -1;
		dim.Visible = false;
		Input.MouseMode = LockMode;
	}
	// redraw whichever screen is open (after crafting, or when your hotbar changes)
	void RefreshPanels()
	{
		if (!invOpen) return;
		if (invPanel.Visible && inv.Survival) DrawRecipes(invRecipes, false, invName);
		if (benchPanel.Visible) { DrawBench(); if (inv.Survival && !benchIsCutter) DrawRecipes(benchRecipes, true, benchName); }
		if (kilnPanel.Visible) DrawKiln();
	}

	// ===================== the hotbar =====================
	void DrawHotbar()
	{
		for (int i = 0; i < Inventory.NS; i++)
		{
			var sl = i < Inventory.Slots ? hotSlots[i] : gearSlots[i];
			if (sl == null) continue;
			sl.Show(inv.Id[i], inv.Counted(inv.Id[i]) && !inv.InfinitePiece(inv.Id[i]) ? inv.Count[i] : 0);
			int max = Inventory.MaxWear(inv.Id[i]);   // a worn tool or piece of armor: a bar of what's left
			sl.ShowWear(inv.Survival && max > 0 && inv.Wear[i] > 0 ? 1 - (float)inv.Wear[i] / max : -1);
			if (i < Inventory.Slots) continue;
			sl.Visible = inv.SlotOpen(i);
			sl.Num.Text = inv.Id[i] == 0 ? SlotWord(i) : ""; sl.Num.Modulate = new Color(1, 1, 1, 0.4f);
		}
		if (packWrap != null)
		{
			packWrap.Visible = inv.PackSize > 0;
			if (inv.PackSize > 0) packTitle.Text = $"{Blocks.B[inv.Id[Inventory.BackSlot]].Name} ({inv.PackSize} slots)";
		}
		if (offSlot != null && inv.Survival)
		{
			int o = Inventory.Off0 + inv.OffSel;
			offSlot.Show(inv.Id[o], inv.Counted(inv.Id[o]) ? inv.Count[o] : 0);
			offSlot.Num.Text = inv.OffCount > 1 ? $"R{inv.OffSel + 1}" : "R";
		}
	}
	void SelectSlot(int i)
	{
		inv.Selected = ((i % Inventory.Slots) + Inventory.Slots) % Inventory.Slots;
		for (int k = 0; k < Inventory.Slots; k++) hotSlots[k].Selected = k == inv.Selected;
		ShowNote(inv.Held != 0 ? Blocks.B[inv.Held].Name : "Empty");
	}
	// survival: right-click a hotbar slot (inventory open) to throw that stack out in front of you
	void ThrowSlot(int i)
	{
		if (inv.Id[i] == 0) return;
		if (netClient) { ShowNote("You can't throw things out in a friend's world yet", 2); return; }
		if (i == Inventory.BackSlot) inv.PackBack();   // (a backpack keeps what was in it)
		var at = player.Pos + player.Forward * 1.2f;
		for (int n = inv.Count[i]; n > 0; n--) { var d = drops.Add(inv.Id[i], at.X, player.Pos.Y + 1.2f, at.Z); d.Age = -1; d.Wear = inv.Wear[i]; d.Data = inv.Data[i]; }   // (can't be picked straight back up)
		inv.SetSlot(i, 0);
		if (i == Inventory.BeltSlot) inv.FitBelt();
	}
	// a short note above the hotbar (the selected block's name, "Lit the torch again"...)
	void ShowNote(string text, float secs = 1.5f) { nameLabel.Text = text; nameLabel.Modulate = Colors.White; nameTimer = secs; }
	float kilnRedraw;
	void TickHud(float dt)
	{
		if (nameTimer > 0) { nameTimer -= dt; if (nameTimer < 0.5f) nameLabel.Modulate = new Color(1, 1, 1, Math.Max(0, nameTimer / 0.5f)); }
		if (kilnAt >= 0 && invOpen && (kilnRedraw -= dt) <= 0) { kilnRedraw = 0.25f; DrawKilnStatus(); }
	}
}
