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
		s.Clicked = b => { if (invOpen) SurvivalSlotPress(i, b); };   // (see Main.Held)
		s.Hover = () => { if (inv.Id[i] == 0) invName.Text = SlotName(i); SpreadOver(i); };
		s.Leave = () => invName.Text = "";
		slotIndex[s] = i;
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
		nameLabel.MouseFilter = Control.MouseFilterEnum.Ignore; uiLayer.AddChild(nameLabel);   // (placed each frame, 4 above whatever is highest: see PlaceToolRow)
		var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		bottom.AddChild(center); nameCenter = center;
		var barBg = new PanelContainer();
		barBg.AddThemeStyleboxOverride("panel", Skin.Panel(4, 4));   // (the hotbar is a menu box, like the web version)
		var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 4);
		center.AddChild(col);
		heartsHolder = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };   // (survival: your hearts, above the left end of the hotbar)
		col.AddChild(heartsHolder);
		heartsLift = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false }; col.AddChild(heartsLift);   // (lifts the hearts over a long tool row: see PlaceToolRow)
		// survival: the offhand slot to the left of the hotbar (R picks the next one, F swaps it with your hand)
		var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 4);
		center.RemoveChild(col); center.AddChild(row);
		var offPanel = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkEnd, Visible = inv.Survival };
		offPanel.AddThemeStyleboxOverride("panel", Skin.Panel(4, 4));
		offSlot = new Slot { Interactive = false }; offPanel.AddChild(offSlot); offBox = offPanel;
		row.AddChild(offPanel); row.AddChild(col);
		col.AddChild(barBg);
		hotBarBox = barBg;
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
				if (inv.Survival) { SurvivalSlotPress(k, b); return; }   // (survival: a stack held on the mouse, see Main.Held)
				if (b == MouseButton.Right) { inv.SetSlot(k, 0); return; }
				SelectSlot(k);
			};
			if (inv.Survival) { slotIndex[s] = k; s.Hover = () => SpreadOver(k); }
			s.DragData = () => invOpen && !inv.Survival && inv.Id[k] != 0 ? "slot:" + k : null;
			s.CanDrop = d => invOpen && !inv.Survival && (d.StartsWith("slot:") || d.StartsWith("item:"));
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
		BuildToolRow();
		invPanel = BuildInventory(); benchPanel = BuildBench(); kilnPanel = BuildKiln(); BuildHowTo();
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
	// Creative: every block and item in tabs, with a search (see Main.Creative). Survival: your slots, worked with a stack
	// held on the mouse (see Main.Held), and the recipes you can make by hand.
	Label invName; VBoxContainer invRecipes;
	Control BuildInventory()
	{
		var scr = Screen("Inventory", out var body);
		if (!inv.Survival) BuildCreative(body);
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
			var ph = Row(left); ph.AddChild(Text("Pockets", 12, 0.85f)); var sort = new Button { Text = "Sort" }; sort.AddThemeFontSizeOverride("font_size", Skin.Size(12)); sort.Pressed += SortPockets; ph.AddChild(sort); var pk = Grid(left);   // (Sort: pockets and backpack)
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
			? "Click a slot to pick up its stack, click again to put it down · right-click: half, or put down one · drag a held stack to share it out\nShift-click moves a stack · double-click gathers · Q drops one, Ctrl+Q the stack · R: how to make it · E to close"
			: "Click a block or item to put it in the selected hotbar slot, or drag it onto a slot · drag a hotbar slot back here to empty it · E to close", 12, 0.65f));
		return scr;
	}
	Slot InvSlot(int id)
	{
		var s = new Slot(); s.Show(id);
		s.Clicked = b => { if (b == MouseButton.Left) inv.SetSlot(inv.Selected, id); };
		s.DragData = () => "item:" + id;
		s.CanDrop = d => d.StartsWith("slot:");
		s.Dropped = d => { int k = int.Parse(d[5..]); if (k < Inventory.Slots) inv.SetSlot(k, 0); };   // (a hotbar slot dragged back: emptied)
		return s;
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
		// the blocks you can cut, with a search and (survival) "Can make now": only the ones you have enough of
		var pick = new VBoxContainer(); pick.AddThemeConstantOverride("separation", 4); row.AddChild(pick);
		var find = new HBoxContainer(); find.AddThemeConstantOverride("separation", 8); pick.AddChild(find);
		cutSearch = new LineEdit { PlaceholderText = "Search", ClearButtonEnabled = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		cutCan = new CheckBox { Text = "Can make now", Visible = inv.Survival };
		find.AddChild(cutSearch); find.AddChild(cutCan);
		cutSearch.TextChanged += _ => FilterCutter(); cutCan.Toggled += _ => FilterCutter();
		var gscroll = new ScrollContainer { CustomMinimumSize = new Vector2(7 * 48 + 12, 5 * 48), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		pick.AddChild(gscroll);
		var grid = new GridContainer { Columns = 7 }; grid.AddThemeConstantOverride("h_separation", 4); grid.AddThemeConstantOverride("v_separation", 4);
		gscroll.AddChild(grid);
		foreach (int id in Blocks.Splittable.Where(b => World.TallWorlds || Array.IndexOf(Blocks.DeepBlocks, b) < 0))   // (the deep layers' stone only while tall worlds are on)
		{
			var s = new Slot(); s.Show(id);
			s.Clicked = _ => { benchBlock = id; DrawBench(); };
			s.Hover = () => benchName.Text = Blocks.B[id].Name; s.Leave = () => benchName.Text = "";
			grid.AddChild(s); cutSlots.Add((id, s));
		}
		var craft = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; craft.AddThemeConstantOverride("separation", 10);
		row.AddChild(craft);
		benchIn = new Slot(56) { Interactive = false, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter }; craft.AddChild(benchIn);
		craft.AddChild(Text("→", 22, 0.8f));
		benchOut = new Slot(56) { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter }; craft.AddChild(benchOut);
		benchCount = benchOut.Count;
		cutNeed = Text("", 12); cutNeed.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; craft.AddChild(cutNeed);   // (survival: "needs 3, you have 1")
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
		if (cutNeed != null)
		{
			int have = benchBlock != 0 ? inv.CountOf(benchBlock) : 0;
			cutNeed.Text = inv.Survival && benchBlock != 0 ? $"needs {m.use}, you have {have}" : "";
			if (have < m.use) cutNeed.AddThemeColorOverride("font_color", new Color(1, 0.45f, 0.4f)); else cutNeed.RemoveThemeColorOverride("font_color");
		}
		FilterCutter();
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
		Unlock();
	}
	void CloseInventory()
	{
		if (heldId != 0 && !ReturnHeld(true)) { invName.Text = benchName.Text = kilnName.Text = "No room: put it down first"; return; }   // (a stack held on the mouse goes back into your slots)
		if (howPanel?.Visible == true) howPanel.Visible = false;
		invOpen = false; kilnAt = -1;
		dim.Visible = false;
		Lock();
	}
	// redraw whichever screen is open (after crafting, or when your hotbar changes)
	void RefreshPanels()
	{
		if (!invOpen) return;
		if (invPanel.Visible && inv.Survival) DrawRecipes(invRecipes, false, invName);
		if (benchPanel.Visible) { DrawBench(); if (inv.Survival && !benchIsCutter) DrawRecipes(benchRecipes, true, benchName); }
		if (kilnPanel.Visible) DrawKiln();
	}


	// ===================== the tool row (survival) =====================
	// The tools in your tool slots, shown above the hotbar between the hearts and the armor bar (see PlaceToolRow). Only to look at:
	// the one last picked for you (breaking a block or hitting a mob without the right tool in your hand) is highlighted
	// until a different one is picked, or it breaks or leaves its slot.
	Control hotBarBox; PanelContainer toolRow; readonly Slot[] toolSlots = new Slot[Inventory.Off0 - Inventory.Tool0];
	int autoTool = -1, autoToolId;
	void BuildToolRow()
	{
		toolRow = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
		toolRow.AddThemeStyleboxOverride("panel", Skin.Panel(4, 4));
		var r = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }; r.AddThemeConstantOverride("separation", 4);
		toolRow.AddChild(r);
		for (int k = 0; k < toolSlots.Length; k++) { var s = new Slot(36, 2, 24) { Interactive = false, MouseFilter = Control.MouseFilterEnum.Ignore }; toolSlots[k] = s; r.AddChild(s); }
		uiLayer.AddChild(toolRow);
	}
	void DrawToolRow()
	{
		if (toolRow == null) return;
		if (autoTool >= 0 && inv.Id[autoTool] != autoToolId) autoTool = -1;   // (it broke, or went somewhere else)
		for (int k = 0; k < toolSlots.Length; k++)
		{
			int i = Inventory.Tool0 + k; var s = toolSlots[k];
			s.Visible = k < inv.ToolCount;
			s.Show(inv.Id[i]);
			int max = Inventory.MaxWear(inv.Id[i]);
			s.ShowWear(max > 0 && inv.Wear[i] > 0 ? 1 - (float)inv.Wear[i] / max : -1);
			s.TipWear = inv.Wear[i];
			s.Selected = i == autoTool;
		}
		toolRow.ResetSize();
	}
	// a left-click used the tool in tool slot i for you
	void UsedToolSlot(int i, int id)
	{
		if (i < Inventory.Tool0 || i >= Inventory.Off0 || i == inv.Selected) return;
		autoTool = inv.Id[i] == id ? i : -1; autoToolId = id;
		DrawToolRow(); DrawArmorBar();
	}
	// every frame (survival; the same as the web version), one line 4 above the hotbar:
	//     hearts   [tool row]   armor
	//  [R] [1][2][3][4][5][6][7][8][9][0]
	//  - the hearts: left end at the hotbar's left end; the armor bar: right end at its right end, filling from the right;
	//  - the tool row: bottom on the line, centred in the gap between them (4 clear of each; the armor bar counts as the
	//    hearts' width even when it isn't showing, so nothing jumps). Too long for the gap: its bottom on the line, centred
	//    over the hotbar, and the hearts and armor bar go up, level, 4 above it;
	//  - notes (the block's name, "Saved" ...) 4 above whatever is highest.
	// Offsets are rounded to whole UI units from the hotbar's left edge, so everything stays on the hotbar's pixel grid.
	void PlaceToolRow()
	{
		if (hotBarBox == null || !hotBarBox.IsInsideTree()) return;   // (not while the HUD is out of the scene, joining a game)
		var bar = hotBarBox.GetGlobalRect(); float screen = hotBarBox.GetViewportRect().Size.X;
		float line = bar.Position.Y - 4, iconFoot = line, top = float.MaxValue;
		Rect2? hearts = heartsRect != null && heartsRect.IsVisibleInTree() ? heartsRect.GetGlobalRect() : null;
		float iconW = hearts?.Size.X ?? (armorBar != null ? armorBar.Size.X : 0);
		float gapL = bar.Position.X + iconW + 4, gapR = bar.End.X - iconW - 4;
		bool lift = false; float rowH = 0;
		if (toolRow != null)
		{
			toolRow.Visible = inv.Survival && hotBarBox.IsVisibleInTree();
			if (toolRow.Visible)
			{
				var size = toolRow.Size;
				if (size.X <= gapR - gapL) toolRow.Position = new Vector2(gapL + MathF.Round((gapR - gapL - size.X) / 2), line - size.Y);
				else
				{
					toolRow.Position = new Vector2(bar.Position.X + MathF.Round((bar.Size.X - size.X) / 2), line - size.Y);
					lift = true; rowH = size.Y; iconFoot = toolRow.Position.Y - 4;
				}
				top = toolRow.Position.Y;
			}
		}
		// the hearts go up over a long tool row: a gap its height between them and the hotbar (the column's 4 between rows does the rest)
		if (heartsLift != null && (heartsLift.Visible != lift || (lift && Math.Abs(heartsLift.CustomMinimumSize.Y - rowH) > 0.5f)))
		{ heartsLift.Visible = lift; heartsLift.CustomMinimumSize = new Vector2(0, rowH); }
		if (armorBar != null)
		{
			armorBar.Visible = armorShown && hotBarBox.IsVisibleInTree();
			if (armorBar.Visible) { armorBar.Position = new Vector2(bar.End.X - armorBar.Size.X, iconFoot - armorBar.Size.Y); top = Math.Min(top, armorBar.Position.Y); }
		}
		// the notes, across the screen, 4 above the highest thing (the hotbar's column holds the hearts and the offhand slot)
		if (nameLabel != null)
		{
			if (nameCenter != null) top = Math.Min(top, nameCenter.GetGlobalRect().Position.Y);
			float hgt = nameLabel.GetCombinedMinimumSize().Y;
			nameLabel.Size = new Vector2(screen, hgt);
			nameLabel.Position = new Vector2(0, MathF.Round(top - 4 - hgt));
		}
	}

	// ===================== the armor bar (survival) =====================
	// The hearts' mirror: as many pictures as hearts, their size and spacing, filling from its right end inward in quarters,
	// as the hearts do, with the share of each hit your armor takes (a full new set, half of every hit, fills it). Pictures:
	// textures/gui/armor_full / three_quarter / half / quarter / empty; the part-filled ones are drawn already mirrored, so they fill
	// from the outer (right) side, the mirror of the hearts. Shown in survival only while your armor gives at least a quarter
	// (nothing on, or all of it broken: hidden, no empty pictures), unless the pictures are missing. Its place stays kept
	// either way (see PlaceToolRow), so nothing moves when armor goes on or comes off. (The same as the web version.)
	Control armorBar; TextureRect[] armorIcons; Texture2D[] armorPics; bool armorShown;
	Control nameCenter, heartsLift;
	void BuildArmorBar()
	{
		Texture2D Pic(string name) => ResourceLoader.Exists($"res://textures/gui/{name}.png") ? ResourceLoader.Load<Texture2D>($"res://textures/gui/{name}.png") : null;
		armorPics = new[] { "armor_empty", "armor_quarter", "armor_half", "armor_three_quarter", "armor_full" }.Select(Pic).ToArray();   // (by quarters held: 0 to 4)
		if (armorPics.Any(p => p == null) || !inv.Survival) return;
		int scale = Math.Max(1, (int)Math.Round(18.0 / HS));   // (the hearts' scale, see BuildHearts)
		var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false }; row.AddThemeConstantOverride("separation", (HP - HS) * scale);
		armorIcons = new TextureRect[HEARTS];
		for (int k = 0; k < HEARTS; k++)
		{
			armorIcons[k] = new TextureRect { TextureFilter = CanvasItem.TextureFilterEnum.Nearest, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
				CustomMinimumSize = new Vector2(HS, HS) * scale, MouseFilter = Control.MouseFilterEnum.Ignore };
			row.AddChild(armorIcons[k]);
		}
		armorBar = row; uiLayer.AddChild(row);
	}
	// how many quarters the armor bar shows (0 to 4 per heart): a full new set (which takes half of every hit) fills it
	public static int ArmorQuarters(float block) => (int)Math.Floor(block * 8 * HEARTS + 0.5f);
	// how many quarters icon j (from the left) holds
	int ArmorIconQuarters(int j) => Math.Clamp(ArmorQuarters(inv.ArmorBlock()) - 4 * (HEARTS - 1 - j), 0, 4);   // (counted from the right end)
	void DrawArmorBar()
	{
		if (armorBar == null) return;
		armorShown = inv.Survival && ArmorQuarters(inv.ArmorBlock()) >= 1;
		for (int j = 0; j < HEARTS; j++)
		{
			int q = ArmorIconQuarters(j);
			armorIcons[j].Texture = armorPics[q];
			armorIcons[j].FlipH = false;   // (the pictures are drawn mirrored already: filled on the right)
		}
		armorBar.ResetSize();
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
			sl.TipWear = inv.Survival ? inv.Wear[i] : -1;
			ShowBag(sl, inv.Id[i], inv.Data[i]);
			if (i < Inventory.Slots) continue;
			sl.Visible = inv.SlotOpen(i);
			sl.Num.Text = inv.Id[i] == 0 ? SlotWord(i) : ""; sl.Num.Modulate = new Color(1, 1, 1, 0.4f);
		}
		DrawToolRow(); DrawArmorBar();
		if (packWrap != null)
		{
			packWrap.Visible = inv.PackSize > 0;
			if (inv.PackSize > 0) packTitle.Text = $"{Blocks.B[inv.Id[Inventory.BackSlot]].Name} ({inv.PackSize} slots)";
		}
		if (offSlot != null && inv.Survival)
		{
			int o = Inventory.Off0 + inv.OffSel;
			offSlot.Show(inv.Id[o], inv.Counted(inv.Id[o]) ? inv.Count[o] : 0);
			ShowBag(offSlot, inv.Id[o], inv.Data[o]);
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
