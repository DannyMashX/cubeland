using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Cubeland;

// The creative inventory: every block and item in nine tabs, a search box that looks through all of them (shapes too),
// and a way to clear the hotbar. The tab order is also the order Sort puts things in (survival).
public partial class Main
{
	// the tabs: (name, the item whose picture is on the tab, what's in it). Anything not listed goes in Building.
	static (string name, int icon, int[] ids)[] creativeTabs;
	public static (string name, int icon, int[] ids)[] CreativeTabs => creativeTabs ??= MakeTabs();   // (made when first needed, once every block is in)
	static (string, int, int[])[] MakeTabs()
	{
		var woods = new[] { (Blocks.Log, Blocks.StrippedLog, Blocks.Planks, Blocks.Leaves), (Blocks.FirLog, Blocks.StrippedFirLog, Blocks.FirPlanks, Blocks.FirLeaves),
			(Blocks.PalmLog, Blocks.StrippedPalmLog, Blocks.PalmPlanks, Blocks.PalmLeaves), (Blocks.WillowLog, Blocks.StrippedWillowLog, Blocks.WillowPlanks, Blocks.WillowLeaves) };
		int[] building = new[] { Blocks.Cobblestone }.Concat(Blocks.DeepShown(8)).Append(Blocks.StoneBricks).Concat(Blocks.DeepShown(9)).Append(69).Concat(Blocks.DeepShown(69))
			.Concat(Blocks.ColourBricks.Prepend(64)).Append(65).Concat(Blocks.ColourTerracotta).Append(67).Concat(Blocks.ColourChiseled).Append(Blocks.Glass)
			.Concat(new[] { Blocks.SandstoneBricks, Blocks.ChiseledSandstone, Blocks.SmoothSandstone, Blocks.MudBricks }).Concat(Blocks.FireclayBlocks).Distinct().ToArray();
		int[] natural = new[] { Blocks.Grass, Blocks.Dirt }.Concat(Blocks.Soils).Append(Blocks.Stone).Concat(Blocks.DeepShown(3)).Concat(Blocks.Ores)
			.Concat(World.TallWorlds ? Blocks.DeepOres : Array.Empty<int>())
			.Concat(new[] { Blocks.Sand, Blocks.Sandstone, Blocks.SnowyGrass, Blocks.SwampGrass, Blocks.Mud, Blocks.Peat, Blocks.Ice, Blocks.Gravel, Blocks.Clay, Blocks.Fireclay,
				Blocks.Snow, Blocks.SnowBlock, Blocks.Water, Blocks.Bedrock }).Distinct().ToArray();
		int[] wood = woods.SelectMany(w => new[] { w.Item1, w.Item2, w.Item3, w.Item4 }).ToArray();
		int[] shapes = Blocks.Splittable.Where(b => World.TallWorlds || Array.IndexOf(Blocks.DeepBlocks, b) < 0).SelectMany(b => ShapeForms.Select(f => Blocks.FormItem(f, b))).Where(i => Blocks.Get(i) != null).ToArray();
		int[] light = new[] { Blocks.Torch, Blocks.SmolderingTorch, Blocks.BurntTorch, Blocks.EverTorch }.Concat(Blocks.Flowers).ToArray();
		int[] tools = new[] { Blocks.CrudeAxe }.Concat(Blocks.Tools).Concat(Blocks.GearItems).ToArray();
		int[] materials = new[] { Blocks.Stick, Blocks.Rock, Blocks.ClayBall, Blocks.Brick, Blocks.FireclayBall, Blocks.FireclayBrick }.Concat(Blocks.PackItems.Except(new[] { Blocks.FireclayBall, Blocks.FireclayBrick }))
			.Concat(new[] { Blocks.Bone, Blocks.BoneAsh, Blocks.GloomWisp, Blocks.FireEssence, Blocks.Leather }).Concat(Blocks.Dye).Concat(new[] { Blocks.Grain, Blocks.GrainSeeds, Blocks.Flour, Blocks.Snowball }).ToArray();
		int[] machines = { Blocks.CraftingBench, Blocks.Cutter, Blocks.Furnace, Blocks.Foundry };
		// food: everything you can eat (that isn't somewhere else already)
		var listed = new HashSet<int>(building.Concat(natural).Concat(wood).Concat(shapes).Concat(light).Concat(tools).Concat(materials).Concat(machines));
		int[] food = AllCreative().Where(i => Blocks.Get(i)?.Food > 0 && !listed.Contains(i)).ToArray();
		listed.UnionWith(food);
		building = building.Concat(AllCreative().Where(i => !listed.Contains(i))).ToArray();   // (anything not listed)
		return new (string, int, int[])[]
		{
			("Building", 64, building), ("Natural", Blocks.Grass, natural), ("Wood", Blocks.Log, wood), ("Shapes", Blocks.FormItem("stairs", Blocks.Stone), shapes),
			("Light & Decoration", Blocks.Torch, light), ("Tools & Armor", Blocks.Tools.FirstOrDefault(t => Blocks.B[t].Tool == "pickaxe" && Blocks.B[t].Material == "iron", Blocks.CrudeAxe), tools),
			("Food", Blocks.Bread, food), ("Materials", Blocks.Stick, materials), ("Machines", Blocks.CraftingBench, machines),
		};
	}
	static readonly string[] ShapeForms = { "piece", "slab", "stairs", "quarter", "vslab", "vstairs", "vquarter" };
	// every block and item the creative inventory has (before the shapes)
	static IEnumerable<int> AllCreative() => Blocks.Items.Concat(new[] { Blocks.Stick, Blocks.Rock, Blocks.CrudeAxe, Blocks.ClayBall, Blocks.Brick, Blocks.FireEssence, Blocks.Bone, Blocks.BoneAsh,
		Blocks.GloomWisp, Blocks.GrainSeeds, Blocks.Grain, Blocks.Flour, Blocks.Bread, Blocks.Dates, Blocks.Chocolate, Blocks.Snowball, Blocks.Leather, Blocks.RawMeat, Blocks.CookedMeat,
		Blocks.SmolderingTorch, Blocks.BurntTorch, Blocks.EverTorch }).Concat(Blocks.Dye).Concat(Blocks.Tools).Concat(Blocks.PackItems).Concat(Blocks.GearItems).Where(i => Blocks.Get(i) != null).Distinct();
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
			int kk = k; var (name, icon, _) = CreativeTabs[k];
			var b = new Button { Text = name, ToggleMode = true, MouseFilter = Control.MouseFilterEnum.Pass };
			b.Pressed += () => { creativeTab = kk; creativeSearch.Text = ""; ShowCreative(); };
			flow.AddChild(b); creativeTabButtons.Add(b);
		}
		var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(CreativeCols * 48 + 12, 6 * 48), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		var gridZone = new DropZone(); gridZone.AddChild(scroll); body.AddChild(gridZone);
		creativeGrid = new GridContainer { Columns = CreativeCols }; creativeGrid.AddThemeConstantOverride("h_separation", 4); creativeGrid.AddThemeConstantOverride("v_separation", 4);
		scroll.AddChild(creativeGrid);
		foreach (var (_, _, ids) in CreativeTabs) foreach (int id in ids) if (!creativeSlots.ContainsKey(id)) { var s = InvSlot(id); creativeSlots[id] = s; creativeGrid.AddChild(s); }
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
		else { b = Blocks.Plain(b); b = Blocks.IsTorch(b) ? Blocks.TorchDrop(b) : Blocks.IsLogBlock(b) ? Blocks.LogOf(b) : Blocks.IsSnow(b) ? Blocks.Snow : b; }
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
