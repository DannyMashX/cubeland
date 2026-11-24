using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace Cubeland;

// The creative inventory: every block and item in nine tabs, a search box that looks through all of them (shapes too),
// and a way to clear the hotbar. The tab order is also the order Sort puts things in (survival).
public partial class Main
{
	// the tabs, in button order: (name, what's in it): data/creative.json (the deep layers' only while tall worlds are on)
	static (string name, int[] ids)[] creativeTabs;
	public static (string name, int[] ids)[] CreativeTabs => creativeTabs ??=   // (made when first needed, once every block is in)
		TabsFrom(GameData.Read<CreativeData>("creative") ?? throw new InvalidOperationException("data/creative.json is missing or can't be read"), World.TallWorlds);
	// data/creative.json as the tabs (tall: with the deep layers' blocks and ores). Anything the creative inventory has that
	// no tab lists goes at the end of Building.
	public static (string name, int[] ids)[] TabsFrom(CreativeData d, bool tall)
	{
		var tabs = d.TABS.Select(t => (t.name, ids: t.forms != null ? ShapesIn(t.forms, tall) : t.items.SelectMany(e => TabEntry(e, tall)).ToArray())).ToArray();
		var listed = new HashSet<int>(tabs.SelectMany(t => t.ids));
		int b = Math.Max(0, Array.FindIndex(tabs, t => t.name == "Building"));
		tabs[b].ids = tabs[b].ids.Concat(AllCreative().Where(i => !listed.Contains(i))).ToArray();
		return tabs;
	}
	// one of a tab's entries: a key, or a { "tall": [keys] } group (its keys are checked even while tall worlds are off)
	public static int[] TabEntry(JsonElement e, bool tall)
	{
		if (e.ValueKind == JsonValueKind.String) return new[] { DataKeys.IdOf(e.GetString()) };
		if (e.ValueKind == JsonValueKind.Object && e.EnumerateObject().Count() == 1 && e.TryGetProperty("tall", out var g) && g.ValueKind == JsonValueKind.Array)
		{
			var ids = g.EnumerateArray().Select(k => DataKeys.IdOf(k.ValueKind == JsonValueKind.String ? k.GetString() : null)).ToArray();
			return tall ? ids : Array.Empty<int>();
		}
		throw new InvalidOperationException($"data/creative.json: a tab has {e.GetRawText()}, which isn't a key or {{ \"tall\": [keys] }}");
	}
	// the Shapes tab: every block the Cutter can cut, block by block, in each form (the deep layers' only while tall worlds are on)
	public static int[] ShapesIn(string[] forms, bool tall)
	{
		foreach (var f in forms) if (Array.IndexOf(Blocks.Forms, f) < 0) throw new InvalidOperationException($"data/creative.json: the Shapes tab has \"{f}\", which isn't a shape");
		return Blocks.Splittable.Where(b => tall || Array.IndexOf(Blocks.DeepBlocks, b) < 0).SelectMany(b => forms.Select(f => Blocks.FormItem(f, b))).Where(i => Blocks.Get(i) != null).ToArray();
	}
	// every block and item the creative inventory has (before the shapes): Items, every item row, and each kind of torch (lit,
	// smoldering, burnt, ever: a standing one)
	static IEnumerable<int> AllCreative() => Blocks.Items.Concat(Blocks.ItemRows).Concat(Enumerable.Range(0, 4).Select(burn => Blocks.TorchVariant(0, 1, burn)))
		.Where(i => Blocks.Get(i) != null).Distinct();
	// which tab an item is in (its place in the tab order, for sorting), and where in that tab
	static Dictionary<int, (int tab, int at)> tabPlace;
	public static (int tab, int at) TabPlace(int id)
	{
		if (tabPlace == null)
		{
			tabPlace = new();
			for (int t = 0; t < CreativeTabs.Length; t++) for (int k = 0; k < CreativeTabs[t].ids.Length; k++) tabPlace.TryAdd(CreativeTabs[t].ids[k], (t, k));
		}
		return tabPlace.TryGetValue(id, out var p) ? p : (0, int.MaxValue);
	}

	const int CreativeCols = 15;
	LineEdit creativeSearch; GridContainer creativeGrid; int creativeTab; readonly List<Button> creativeTabButtons = new();
	readonly Dictionary<int, Slot> creativeSlots = new();
	void BuildCreative(VBoxContainer body)
	{
		creativeSearch = new LineEdit { PlaceholderText = "Search", CustomMinimumSize = new Vector2(260, 0), ClearButtonEnabled = true };
		creativeSearch.TextChanged += _ => ShowCreative();
		body.AddChild(creativeSearch);
		var tabs = new DropZone(); body.AddChild(tabs);
		var flow = new HFlowContainer { CustomMinimumSize = new Vector2(CreativeCols * 48, 0) }; flow.AddThemeConstantOverride("h_separation", 4); flow.AddThemeConstantOverride("v_separation", 4);
		tabs.AddChild(flow);
		for (int k = 0; k < CreativeTabs.Length; k++)
		{
			int kk = k; string name = CreativeTabs[k].name;
			var b = new Button { Text = name, ToggleMode = true, MouseFilter = Control.MouseFilterEnum.Pass };
			b.Pressed += () => { creativeTab = kk; creativeSearch.Text = ""; ShowCreative(); };
			flow.AddChild(b); creativeTabButtons.Add(b);
		}
		var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(CreativeCols * 48 + 12, 6 * 48), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		var gridZone = new DropZone(); gridZone.AddChild(scroll); body.AddChild(gridZone);
		creativeGrid = new GridContainer { Columns = CreativeCols }; creativeGrid.AddThemeConstantOverride("h_separation", 4); creativeGrid.AddThemeConstantOverride("v_separation", 4);
		scroll.AddChild(creativeGrid);
		foreach (var (_, ids) in CreativeTabs) foreach (int id in ids) if (!creativeSlots.ContainsKey(id)) { var s = InvSlot(id); creativeSlots[id] = s; creativeGrid.AddChild(s); }
		// a hotbar slot dragged onto the tabs or the grid: it's emptied
		foreach (var z in new[] { tabs, gridZone })
		{
			z.CanDrop = d => d.StartsWith("slot:");
			z.Dropped = d => { int k = int.Parse(d[5..]); if (k < Inventory.Slots) inv.SetSlot(k, 0); };
		}
		var clear = new Button { Text = "Clear hotbar", SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
		clear.Pressed += () => { for (int k = 0; k < Inventory.Slots; k++) inv.SetSlot(k, 0); };
		body.AddChild(clear);
		ShowCreative();
	}
	// show the chosen tab, or what the search finds in every tab
	void ShowCreative()
	{
		string q = creativeSearch.Text.Trim().ToLowerInvariant();
		for (int j = 0; j < creativeTabButtons.Count; j++) creativeTabButtons[j].ButtonPressed = q == "" && j == creativeTab;
		var show = q == "" ? CreativeTabs[creativeTab].ids : CreativeTabs.SelectMany(t => t.ids).Where(i => Blocks.B[i].Name.ToLowerInvariant().Contains(q)).ToArray();
		var on = new HashSet<int>(show);
		foreach (var (id, s) in creativeSlots) s.Visible = on.Contains(id);
		for (int k = 0; k < show.Length; k++) if (creativeSlots.TryGetValue(show[k], out var s)) creativeGrid.MoveChild(s, k);
	}

	// middle-click (creative): the block you're looking at goes into your hotbar; survival: picks its hotbar slot if you have it there
	void PickBlock()
	{
		if (player.Raycast() is not { } h) return;
		int b = h.Id;
		if (h.Sub >= 0 && world.PieceMap.TryGetValue(World.Idx(h.X, h.Y, h.Z), out var a) && a != null) b = Blocks.FormItem("piece", World.SpotBlock(a[h.Sub]));
		else { b = Blocks.Plain(b); b = Blocks.IsTorch(b) ? Blocks.TorchDrop(b) : Blocks.IsDoor(b) ? Blocks.B[b].Drop : Blocks.IsLogBlock(b) ? Blocks.LogOf(b) : Blocks.IsSnow(b) ? Blocks.Snow : b; }
		if (Blocks.Get(b) == null || b == 0) return;
		int have = Array.IndexOf(inv.Id, b, 0, Inventory.Slots);
		if (have >= 0) { SelectSlot(have); return; }
		if (inv.Survival) return;
		inv.SetSlot(inv.Selected, b); SelectSlot(inv.Selected);
	}
}

// a box that takes things dropped on it (anything inside it that doesn't take the drop itself passes it here)
public partial class DropZone : MarginContainer
{
	public Func<string, bool> CanDrop; public Action<string> Dropped;
	public override bool _CanDropData(Vector2 at, Variant data) => data.VariantType == Variant.Type.String && CanDrop?.Invoke((string)data) == true;
	public override void _DropData(Vector2 at, Variant data) => Dropped?.Invoke((string)data);
}
