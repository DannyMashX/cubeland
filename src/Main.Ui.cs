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
				if (d.StartsWith("slot:")) inv.Move(int.Parse(d[5..]), k);
				else inv.SetSlot(k, int.Parse(d[5..]));
				SelectSlot(k);
			};
			hotSlots[i] = s; bar.AddChild(s);
		}
		inv.Changed = () => { DrawHotbar(); RefreshPanels(); };

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
		("Building Blocks", new[] { 7, 61, 8, 9, 64 }.Concat(Blocks.ColourBricks).Append(65).Concat(Blocks.ColourTerracotta).Append(67).Concat(Blocks.ColourChiseled)
			.Concat(new[] { 69, 10, 22, Blocks.CraftingBench, Blocks.Furnace }).ToArray()),
		("Natural Blocks", new[] { 1, 2, 3, 4, 5, 6, 11, 63 }.Concat(Blocks.Flowers).Append(Blocks.Water).Concat(Blocks.Soils).ToArray()),
		("Items", new[] { Blocks.Stick, Blocks.Rock, Blocks.ClayBall, Blocks.Brick, Blocks.FireEssence, Blocks.Bone, Blocks.BoneAsh, Blocks.GloomWisp, Blocks.GrainSeeds, Blocks.Grain, Blocks.Flour, Blocks.Bread }.Concat(Blocks.Dye).Append(Blocks.CrudeAxe).Concat(Blocks.Tools).ToArray()),
	};
	static int TabOf(int id) => Math.Max(0, Array.FindIndex(InvTabs, t => t.ids.Contains(id)));
	static int[] VariantsOf(int b) => Array.IndexOf(Blocks.Splittable, b) >= 0
		? new[] { Blocks.PieceItem + b, 1000 + b, 1200 + b, 1300 + b, 1100 + b, 1400 + b, 1500 + b }
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
			var list = Blocks.Items.Concat(new[] { Blocks.Stick, Blocks.Rock, Blocks.CrudeAxe, Blocks.ClayBall, Blocks.Brick, Blocks.FireEssence, Blocks.Bone, Blocks.BoneAsh, Blocks.GloomWisp, Blocks.GrainSeeds, Blocks.Grain, Blocks.Flour, Blocks.Bread }).Concat(Blocks.Dye).Concat(Blocks.Tools)
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
		else body.AddChild(RecipeList(out invRecipes));
		invName = Text(""); invName.CustomMinimumSize = new Vector2(0, 18); body.AddChild(invName);
		body.AddChild(Text(inv.Survival
			? "Survival: break blocks to collect them · click a recipe to craft it (tools need a Crafting Bench)\ndrag hotbar slots to move or stack them · right-click a hotbar slot to throw it out · E to close"
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
	Label benchName, benchCount; Slot benchIn, benchOut; VBoxContainer benchRecipes; int benchBlock, benchMode;
	readonly List<Button> benchModeButtons = new();
	Control BuildBench()
	{
		var scr = Screen("Crafting Bench", out var body);
		var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 18); body.AddChild(row);
		var grid = new GridContainer { Columns = 7 }; grid.AddThemeConstantOverride("h_separation", 4); grid.AddThemeConstantOverride("v_separation", 4);
		row.AddChild(grid);
		foreach (int id in Blocks.Splittable)
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
			benchName.Text = m.key == "pieces" ? (inv.InfinitePiece(Blocks.PieceItem + benchBlock) ? $"{Blocks.B[benchBlock].Name} Piece (click to take)" : $"{Blocks.B[benchBlock].Name} Piece x8 (click to take)")
				: $"{Blocks.B[m.baseId + benchBlock].Name} x{m.n} (click to take)";
		};
		var modes = new HFlowContainer(); body.AddChild(modes);
		for (int k = 0; k < Inventory.BenchModes.Length; k++)
		{
			int kk = k; var b = new Button { Text = Inventory.BenchModes[k].name, ToggleMode = true };
			b.Pressed += () => { benchMode = kk; DrawBench(); };
			modes.AddChild(b); benchModeButtons.Add(b);
		}
		if (inv.Survival) { body.AddChild(Text("Crafting", 15)); body.AddChild(RecipeList(out benchRecipes)); }
		benchName = Text(""); benchName.CustomMinimumSize = new Vector2(0, 18); body.AddChild(benchName);
		body.AddChild(Text("Pick a block, pick what to cut it into, then click what it makes to take it · Shift + right-click to build against the bench · E to close", 12, 0.65f));
		return scr;
	}
	void DrawBench()
	{
		var m = Inventory.BenchModes[benchMode];
		benchIn.Show(benchBlock);
		benchOut.Show(benchBlock != 0 ? m.baseId + benchBlock : 0);
		benchCount.Text = benchBlock != 0 && !(m.key == "pieces" && inv.InfinitePiece(Blocks.PieceItem + benchBlock)) ? m.n.ToString() : "";
		for (int k = 0; k < benchModeButtons.Count; k++) benchModeButtons[k].ButtonPressed = k == benchMode;
	}

	// ===================== Kiln =====================
	Label kilnName, kilnStatus; Slot kilnFuel; VBoxContainer kilnRecipes; HBoxContainer kilnOut;
	Control BuildKiln()
	{
		var scr = Screen("Kiln", out var body);
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
		kilnFuel.Show(inv.Survival ? f.Fuel : 0, inv.Survival ? f.FuelN : 0);
		DrawKilnStatus();
		foreach (var c in kilnRecipes.GetChildren()) c.QueueFree();
		foreach (var (outId, from) in Kilns.Smelts)
		{
			var b = new Button { Text = $"{Blocks.B[outId].Name}\n1 {Blocks.B[from].Name} · {Kilns.SmeltTime} seconds", Icon = Atlas.SlotIcon(outId),
				Alignment = HorizontalAlignment.Left, Disabled = inv.Survival && inv.CountOf(from) < 1, TextureFilter = Atlas.Is3DIcon(outId) ? CanvasItem.TextureFilterEnum.Linear : CanvasItem.TextureFilterEnum.Nearest, ExpandIcon = true };
			b.AddThemeConstantOverride("icon_max_width", 32);
			b.Pressed += () =>
			{
				var g = kilns.Of(kilnAt);
				if (g.Queue.Count >= 64) { kilnName.Text = "The kiln is full up"; return; }
				if (inv.Survival && !inv.Take(from, 1)) return;
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
		if (benchPanel.Visible) { DrawBench(); if (inv.Survival) DrawRecipes(benchRecipes, true, benchName); }
		if (kilnPanel.Visible) DrawKiln();
	}

	// ===================== the hotbar =====================
	void DrawHotbar()
	{
		for (int i = 0; i < Inventory.Slots; i++)
			hotSlots[i].Show(inv.Id[i], inv.Counted(inv.Id[i]) && !inv.InfinitePiece(inv.Id[i]) ? inv.Count[i] : 0);
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
		var at = player.Pos + player.Forward * 1.2f;
		for (int n = inv.Count[i]; n > 0; n--) drops.Add(inv.Id[i], at.X, player.Pos.Y + 1.2f, at.Z).Age = -1;   // (can't be picked straight back up)
		inv.SetSlot(i, 0);
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
