using System;
using System.Collections.Generic;
using System.Linq;

namespace Cubeland;

// What you carry: the 10-slot hotbar (keys 1-9, 0), and the crafting recipes that use it.
// Survival also has (all in the same arrays): Pockets 10-19, the worn backpack's slots 20-59, tool slots 60-71 (5 + what your
// belt gives; the right tool is picked from them for you), offhand 72-81 (1 + what your belt gives), armor 82-85, the worn
// backpack 86 and belt 87.
// Creative: blocks are unlimited (no count). Survival: everything is counted, and you only have what you collected.
public class Inventory
{
	public const int Slots = 10, Pocket0 = 10, Pack0 = 20, Tool0 = 60, Off0 = 72, Armor0 = 82, BackSlot = 86, BeltSlot = 87, NS = 88;
	// what a backpack that's been taken off carries (its slots, in order)
	public class PackData { public int[] Id, N, W; }
	public static readonly int[] Default = { 1, 2, 3, 8, 7, 4, 10, 12, 22, 6 };
	public readonly int[] Id = new int[NS], Count = new int[NS];
	public readonly PackData[] Data = new PackData[NS];
	// ---- Tool durability (Pack & Plate) ----
	// Each tool can be used Durability[its material] times. Breaking a block uses 1 (2 with the wrong tool for that block),
	// a hit on a mob uses 1 for a sword and 2 for any other tool, tilling uses 1. At 0 the tool breaks and is gone.
	// Wear: how much each slot's tool has been used (0 = new). A tool dropped on the ground keeps its wear. Survival only.
	public static readonly Dictionary<string, int> Durability = new() { ["crude"] = 32, ["stone"] = 160, ["copper"] = 320, ["iron"] = 480, ["bronite"] = 720, ["rosium"] = 960, ["steel"] = 1440 };
	public readonly int[] Wear = new int[NS];
	// the most a tool can be used (0: not a tool that wears out)
	public static int MaxWear(int id) => Blocks.Get(id) is { } d && (d.Tool != null ? Durability : d.Armor >= 0 ? ArmorDurability : null) is { } t && t.TryGetValue(d.Material ?? "", out int m) ? m : 0;
	// use up some of the selected tool (survival); returns its name if that broke it
	public string WearTool(int cost)
	{
		int id = Held, max = MaxWear(id);
		if (!Survival || max == 0) return null;
		int i = ActSlot;
		Wear[i] += cost;
		if (Wear[i] < max) { Changed?.Invoke(); return null; }
		SetSlot(i, 0);
		return Blocks.B[id].Name;
	}
	public int Selected;
	// the slot a click is using (the offhand on a right-click, a tool slot's tool when breaking...); -1 = the selected one
	public int Act = -1, OffSel;
	public int ActSlot => Act >= 0 ? Act : Selected;
	public Func<int, bool> ThrowOut;   // a belt came off: what to do with an offhand slot's things that no longer has room
	public readonly bool Survival;
	public Action Changed;   // redraw the hotbar (and any open recipe list)

	public Inventory(bool survival)
	{
		Survival = survival;
		if (!survival) for (int i = 0; i < Slots; i++) { Id[i] = Default[i]; Count[i] = Blocks.IsItem(Default[i]) ? 1 : 0; }
	}
	public int Held => Id[ActSlot];
	public bool Counted(int id) => id != 0 && (Survival || Blocks.IsItem(id));
	// in creative mode pieces, slabs, stairs and quarters are unlimited, like whole blocks (no count, never used up)
	public bool InfinitePiece(int id) => (Blocks.IsPiece(id) || Blocks.FormOf(id) != null) && !Survival;
	public bool Holding(string tool) => Blocks.Get(Held)?.Tool == tool;
	public int CountOf(int id) { int n = 0; for (int i = 0; i < Armor0; i++) if (Id[i] == id) n += Count[i]; return n; }

	public void SetSlot(int i, int id, int n = 1, int wear = 0, PackData data = null) { Id[i] = id; Count[i] = Counted(id) ? n : 0; Wear[i] = wear; Data[i] = data; Changed?.Invoke(); }
	public void UseOne() { UseOneAt(ActSlot); }
	public void UseOneAt(int i)
	{
		if (!Survival || Id[i] == 0) return;   // creative: nothing gets used up
		if (--Count[i] <= 0) { Id[i] = 0; Count[i] = 0; }
		Changed?.Invoke();
	}
	// picking up an item: top up a stack of it, otherwise the first empty slot. Returns false if there's no room.
	public bool Give(int id, int wear = 0, PackData data = null)
	{
		if (InfinitePiece(id) && Id.Take(Slots).Contains(id)) return true;   // creative: a piece already in the hotbar never runs out
		var store = StoreSlots().ToList();   // (hotbar, then Pockets, then the backpack)
		foreach (int k in store) if (Id[k] == id && Counted(id) && Count[k] < Blocks.StackMaxOf(id)) { Count[k]++; Changed?.Invoke(); return true; }
		if (!Counted(id) && Id.Take(Slots).Contains(id)) return true;          // creative block you already have
		int e = store.FirstOrDefault(k => Id[k] == 0 && Fits(k, id), -1);
		// no empty slot: take over the right-most block slot (blocks can always be picked again). Not in survival.
		if (e < 0 && !Survival) for (int k = Slots - 1; k >= 0; k--) if (!Blocks.IsItem(Id[k])) { e = k; break; }
		if (e < 0) return false;
		SetSlot(e, id, 1, wear, data);
		return true;
	}
	// ---- gear ----
	public int PackSize => Survival && Id[BackSlot] != 0 ? Blocks.B[Id[BackSlot]].Pack : 0;
	public int OffCount => 1 + (Survival && Id[BeltSlot] != 0 ? Blocks.B[Id[BeltSlot]].Belt : 0);
	// tool slots: 5, plus what your belt gives (2 for leather up to 7 for steel)
	public int ToolCount => 5 + (Survival && Id[BeltSlot] != 0 ? Blocks.B[Id[BeltSlot]].BeltTools : 0);
	// is slot i there right now?
	public bool SlotOpen(int i) => i < Slots || Survival && (i < Pack0 || (i < Tool0 ? i < Pack0 + PackSize : (i < Off0 ? i < Tool0 + ToolCount : i < Armor0 ? i < Off0 + OffCount : i < NS)));
	// where picked-up things go, in order: hotbar, Pockets, backpack
	public IEnumerable<int> StoreSlots() { int n = Survival ? Pack0 + PackSize : Slots; for (int i = 0; i < n; i++) yield return i; }
	// can item id go in slot i? (tool slots take tools, armor slots their piece, and so on; backpacks don't go in a backpack)
	public bool Fits(int i, int id)
	{
		if (id == 0) return true;
		if (!SlotOpen(i)) return false;
		var d = Blocks.Get(id); if (d == null) return false;
		if (i >= Tool0 && i < Off0) return d.Tool != null;
		if (i >= Armor0 && i < BackSlot) return d.Armor == i - Armor0;
		if (i == BackSlot) return d.Pack > 0;
		if (i == BeltSlot) return d.Belt > 0;
		if (i >= Pack0 && i < Tool0) return d.Pack == 0;
		return true;
	}
	public void Swap(int i, int j)
	{
		(Id[i], Id[j]) = (Id[j], Id[i]); (Count[i], Count[j]) = (Count[j], Count[i]); (Wear[i], Wear[j]) = (Wear[j], Wear[i]); (Data[i], Data[j]) = (Data[j], Data[i]);
	}
	// the worn backpack takes its slots' contents with it when it comes off...
	public void PackBack()
	{
		if (Id[BackSlot] == 0) return;
		var d = new PackData { Id = new int[Tool0 - Pack0], N = new int[Tool0 - Pack0], W = new int[Tool0 - Pack0] };
		for (int k = Pack0; k < Tool0; k++) { d.Id[k - Pack0] = Id[k]; d.N[k - Pack0] = Count[k]; d.W[k - Pack0] = Wear[k]; Id[k] = Count[k] = Wear[k] = 0; Data[k] = null; }
		Data[BackSlot] = d.Id.Any(x => x != 0) ? d : null;
	}
	// ...and a backpack put on spreads what it carries back into them
	public void UnpackBack()
	{
		var d = Data[BackSlot]; Data[BackSlot] = null;
		if (d?.Id == null) return;
		for (int k = 0; k < d.Id.Length && k < Tool0 - Pack0; k++) { Id[Pack0 + k] = d.Id[k]; Count[Pack0 + k] = d.N?.ElementAtOrDefault(k) ?? 0; Wear[Pack0 + k] = d.W?.ElementAtOrDefault(k) ?? 0; }
	}
	// a smaller belt (or none): what was in the offhand and tool slots it gave comes out
	public void FitBelt()
	{
		for (int k = Tool0; k < Armor0; k++) if (Id[k] != 0 && !SlotOpen(k)) { if (ThrowOut?.Invoke(k) != true) SetSlot(k, 0); }
		if (OffSel >= OffCount) OffSel = 0;
	}
	// the best tool of a kind in your tool slots (the one that lasts longest), or -1
	public int BestTool(string kind)
	{
		int best = -1, bestDur = -1;
		for (int k = Tool0; k < Off0; k++)
			if (Blocks.Get(Id[k]) is { } t && t.Tool == kind && Durability.GetValueOrDefault(t.Material ?? "") > bestDur) { best = k; bestDur = Durability.GetValueOrDefault(t.Material ?? ""); }
		return best;
	}
	// ---- Armor: a full set takes half of all damage (helmet 10%, chestplate 20%, leggings 12.5%, boots 7.5%). A piece
	// protects less as it wears, but never less than half until it breaks. Each piece you wear loses 1 durability per hit.
	public static readonly Dictionary<string, int> ArmorDurability = new() { ["leather"] = 100, ["copper"] = 200, ["iron"] = 300, ["bronite"] = 450, ["rosium"] = 600, ["steel"] = 900 };
	static readonly float[] ArmorShare = { 0.10f, 0.20f, 0.125f, 0.075f };
	public float ArmorBlock()
	{
		if (!Survival) return 0;
		float p = 0;
		for (int k = 0; k < 4; k++) { int i = Armor0 + k, max = MaxWear(Id[i]); if (max > 0) p += ArmorShare[k] * (0.5f + 0.5f * (1 - (float)Wear[i] / max)); }
		return p;
	}
	// a hit: each piece you wear wears by 1. Returns the names of any that broke.
	public List<string> WearArmor()
	{
		var broke = new List<string>();
		for (int k = 0; k < 4; k++)
		{
			int i = Armor0 + k, id = Id[i], max = MaxWear(id);
			if (max == 0 || ++Wear[i] < max) continue;
			broke.Add(Blocks.B[id].Name); SetSlot(i, 0);
		}
		Changed?.Invoke();
		return broke;
	}
	// what you wear, for other players to see: helmet, chestplate, leggings, boots, backpack, belt
	public int[] GearIds() => Survival ? Enumerable.Range(Armor0, 6).Select(i => Id[i]).ToArray() : Array.Empty<int>();
	// take k of an item out of the hotbar (right-most first); false if there aren't enough
	public bool Take(int id, int k)
	{
		if (CountOf(id) < k) return false;
		for (int s = Armor0 - 1; s >= 0 && k > 0; s--) if (Id[s] == id)
		{
			int t = Math.Min(k, Count[s]); Count[s] -= t; k -= t;
			if (Count[s] == 0) Id[s] = 0;
		}
		Changed?.Invoke();
		return true;
	}
	// drag one slot onto another: the same item stacks together, anything else swaps if each fits where it's going
	// (false: it doesn't go there)
	public bool Move(int from, int to)
	{
		if (from == to || Id[from] == 0) return true;
		if (Id[from] == Id[to] && Counted(Id[to]) && Blocks.StackMaxOf(Id[to]) > 1)
		{
			int moved = Math.Min(Count[from], Blocks.StackMaxOf(Id[to]) - Count[to]);
			Count[to] += moved; Count[from] -= moved;
			if (Count[from] == 0) Id[from] = 0;
		}
		else
		{
			if (!Fits(to, Id[from]) || !Fits(from, Id[to])) return false;
			bool back = from == BackSlot || to == BackSlot;
			if (back) PackBack();
			Swap(from, to);
			if (back) UnpackBack();
			if (from == BeltSlot || to == BeltSlot) FitBelt();
		}
		Changed?.Invoke();
		return true;
	}
	public (int[] id, int[] n, int[] w) Snapshot() => ((int[])Id.Clone(), (int[])Count.Clone(), (int[])Wear.Clone());
	public void Restore((int[] id, int[] n, int[] w) s) { Array.Copy(s.id, Id, NS); Array.Copy(s.n, Count, NS); Array.Copy(s.w, Wear, NS); Changed?.Invoke(); }

	// ===================== Crafting (survival) =====================
	// at: "hand" = can be made anywhere (in the inventory, E), "bench" = only at a Crafting Bench (which can make everything).
	// Needs: [id, how many]. Axe: you also need an axe somewhere in your hotbar (it isn't used up). Tool: the same for another tool.
	// Colour: only listed while you carry at least one of its ingredients (there are a lot of them)
	public class Recipe { public int Out, N; public (int id, int n)[] Needs; public string At = "hand"; public bool Axe; public string Tool; public bool Colour; }
	public static readonly List<Recipe> Recipes = new()
	{
		new() { Out = Blocks.CrudeAxe, N = 1, Needs = new[] { (Blocks.Rock, 2), (Blocks.Stick, 1) } },
		new() { Out = Blocks.Cobblestone, N = 1, Needs = new[] { (Blocks.Rock, 4) } },
		new() { Out = Blocks.Planks, N = 4, Needs = new[] { (Blocks.Log, 1) }, Axe = true },
		new() { Out = Blocks.Planks, N = 4, Needs = new[] { (Blocks.StrippedLog, 1) }, Axe = true },
		new() { Out = Blocks.Stick, N = 4, Needs = new[] { (Blocks.Planks, 2) }, At = "bench", Axe = true },
		new() { Out = Blocks.CraftingBench, N = 1, Needs = new[] { (Blocks.Planks, 4), (Blocks.Log, 1) } },
		new() { Out = Blocks.Cutter, N = 1, Needs = new[] { (Blocks.Planks, 3), (Blocks.MetalBrick["copper"], 1) }, At = "bench" },   // 3 Planks + a Copper Brick (its blade)
		// the same from Fir wood
		new() { Out = Blocks.FirPlanks, N = 4, Needs = new[] { (Blocks.FirLog, 1) }, Axe = true },
		new() { Out = Blocks.FirPlanks, N = 4, Needs = new[] { (Blocks.StrippedFirLog, 1) }, Axe = true },
		new() { Out = Blocks.Stick, N = 4, Needs = new[] { (Blocks.FirPlanks, 2) }, At = "bench", Axe = true },
		new() { Out = Blocks.CraftingBench, N = 1, Needs = new[] { (Blocks.FirPlanks, 4), (Blocks.FirLog, 1) } },
		new() { Out = Blocks.Cutter, N = 1, Needs = new[] { (Blocks.FirPlanks, 3), (Blocks.MetalBrick["copper"], 1) }, At = "bench" },
		// ... and from Palm and Willow wood
		new() { Out = Blocks.PalmPlanks, N = 4, Needs = new[] { (Blocks.PalmLog, 1) }, Axe = true },
		new() { Out = Blocks.PalmPlanks, N = 4, Needs = new[] { (Blocks.StrippedPalmLog, 1) }, Axe = true },
		new() { Out = Blocks.Stick, N = 4, Needs = new[] { (Blocks.PalmPlanks, 2) }, At = "bench", Axe = true },
		new() { Out = Blocks.CraftingBench, N = 1, Needs = new[] { (Blocks.PalmPlanks, 4), (Blocks.PalmLog, 1) } },
		new() { Out = Blocks.Cutter, N = 1, Needs = new[] { (Blocks.PalmPlanks, 3), (Blocks.MetalBrick["copper"], 1) }, At = "bench" },
		new() { Out = Blocks.WillowPlanks, N = 4, Needs = new[] { (Blocks.WillowLog, 1) }, Axe = true },
		new() { Out = Blocks.WillowPlanks, N = 4, Needs = new[] { (Blocks.StrippedWillowLog, 1) }, Axe = true },
		new() { Out = Blocks.Stick, N = 4, Needs = new[] { (Blocks.WillowPlanks, 2) }, At = "bench", Axe = true },
		new() { Out = Blocks.CraftingBench, N = 1, Needs = new[] { (Blocks.WillowPlanks, 4), (Blocks.WillowLog, 1) } },
		new() { Out = Blocks.Cutter, N = 1, Needs = new[] { (Blocks.WillowPlanks, 3), (Blocks.MetalBrick["copper"], 1) }, At = "bench" },
		new() { Out = Blocks.Torch, N = 2, Needs = new[] { (Blocks.Stick, 1), (Blocks.Torch, 1) } },
		new() { Out = Blocks.EverTorch, N = 1, Needs = new[] { (Blocks.Torch, 1), (Blocks.FireEssence, 1) } },
		new() { Out = Blocks.ClayBall, N = 4, Needs = new[] { (Blocks.Clay, 1) } },
		new() { Out = Blocks.ClayBall, N = 1, Needs = new[] { (Blocks.PieceItem + Blocks.Clay, 2) } },
		new() { Out = Blocks.Clay, N = 1, Needs = new[] { (Blocks.ClayBall, 4) } },   // 4 Clay Balls packed back into Clay
		new() { Out = Blocks.SnowBlock, N = 1, Needs = new[] { (Blocks.Snowball, 4) } },   // 4 Snowballs packed into a Snow Block
		new() { Out = Blocks.Snow, N = 8, Needs = new[] { (Blocks.SnowBlock, 1) } },     // ... which makes 8 layers of Snow
		new() { Out = Blocks.BricksBlock, N = 1, Needs = new[] { (Blocks.Brick, 4) }, At = "bench" },
		new() { Out = Blocks.Furnace, N = 1, Needs = new[] { (Blocks.Brick, 8) }, At = "bench" },
		new() { Out = 67, N = 1, Needs = new[] { (65, 1) }, At = "bench", Tool = "pickaxe" },
		new() { Out = 68, N = 1, Needs = new[] { (66, 1) }, At = "bench", Tool = "pickaxe" },
		new() { Out = 69, N = 1, Needs = new[] { (3, 1) }, At = "bench", Tool = "pickaxe" },
		new() { Out = Blocks.SandstoneBricks, N = 1, Needs = new[] { (Blocks.Sandstone, 1) }, At = "bench", Tool = "pickaxe" },     // Sandstone, cut into bricks
		new() { Out = Blocks.ChiseledSandstone, N = 1, Needs = new[] { (Blocks.Sandstone, 1) }, At = "bench", Tool = "pickaxe" },   // Sandstone, carved
		new() { Out = Blocks.Flour, N = 1, Needs = new[] { (Blocks.Grain, 1) } },   // Grain, mashed by hand -> Flour
		// Fireclay, like Clay; and the Foundry's blocks
		new() { Out = Blocks.FireclayBall, N = 4, Needs = new[] { (Blocks.Fireclay, 1) } },                     // Fireclay -> 4 Fireclay Balls
		new() { Out = Blocks.Fireclay, N = 1, Needs = new[] { (Blocks.FireclayBall, 4) } },                     // 4 Fireclay Balls packed back into Fireclay
		new() { Out = Blocks.Fireclay, N = 1, Needs = new[] { (63, 1), (Blocks.Sand, 1), (11, 1) }, At = "bench" },        // Clay + Sand + Gravel mixed into Fireclay
		new() { Out = Blocks.FireclayBricks, N = 1, Needs = new[] { (Blocks.FireclayBrick, 4) }, At = "bench" },   // 4 Fireclay Bricks -> Fireclay Bricks (block)
		new() { Out = Blocks.FireclayCasing, N = 1, Needs = new[] { (Blocks.FireclayBrick, 4), (Blocks.FireclayTerracotta, 1) }, At = "bench" },   // the Foundry's walls
		new() { Out = Blocks.ChiseledFireclay, N = 1, Needs = new[] { (Blocks.FireclayTerracotta, 1) }, At = "bench", Tool = "pickaxe" },   // carved
		new() { Out = Blocks.Foundry, N = 1, Needs = new[] { (Blocks.FireclayBrick, 8) }, At = "bench" },    // 8 Fireclay Bricks -> the Foundry's Furnace
	};
	static Inventory()
	{
		// dyes and colours
		foreach (int f in Blocks.Flowers) Recipes.Add(new() { Out = Blocks.B[f].Dye, N = 1, Needs = new[] { (f, 1) }, Colour = true });   // a Flower -> its Dye
		Recipes.Add(new() { Out = Blocks.DyeOf("black"), N = 1, Needs = new[] { (Blocks.GloomWisp, 1) }, Colour = true });   // a Gloom Wisp -> Black Dye
		Recipes.Add(new() { Out = Blocks.DyeOf("white"), N = 1, Needs = new[] { (Blocks.BoneAsh, 1) }, Colour = true });     // Bone Ash -> White Dye
		// deep Stone -> that layer's Chiseled Stone (at the bench, with a pickaxe)
		if (World.TallWorlds) for (int k = 0; k < Blocks.StrataLayers - 1; k++) Recipes.Add(new() { Out = Blocks.DeepChiseled + k, N = 1, Needs = new[] { (Blocks.Strata + k, 1) }, At = "bench", Tool = "pickaxe", Colour = true });
		foreach (var (o, a, b) in new[] { ("orange", "red", "yellow"), ("green", "yellow", "aqua"), ("blue", "aqua", "indigo"), ("purple", "indigo", "magenta"), ("pink", "red", "white") })
			Recipes.Add(new() { Out = Blocks.DyeOf(o), N = 2, Needs = new[] { (Blocks.DyeOf(a), 1), (Blocks.DyeOf(b), 1) }, Colour = true });   // two dyes mixed
		for (int k = 0; k < Blocks.Colours.Length; k++)
		{
			Recipes.Add(new() { Out = Blocks.ColourTerracotta[k], N = 8, Needs = new[] { (Blocks.Terracotta, 8), (Blocks.Dye[k], 1) }, At = "bench", Colour = true });
			if (k > 0) Recipes.Add(new() { Out = Blocks.ColourChiseled[k], N = 1, Needs = new[] { (Blocks.ColourTerracotta[k], 1) }, At = "bench", Tool = "pickaxe", Colour = true });
			Recipes.Add(new() { Out = Blocks.ColourBricks[k], N = 1, Needs = new[] { (Blocks.ColourTerracotta[k], 1) }, At = "bench", Tool = "pickaxe", Colour = true });
		}
		// tools: made of Cobblestone (stone tools) or a metal's Bricks, with sticks for the handle. [material, sticks]
		var toolRecipe = new Dictionary<string, (int m, int s)> { ["Pickaxe"] = (3, 2), ["Axe"] = (3, 2), ["Shovel"] = (1, 2), ["Sword"] = (2, 1), ["Hoe"] = (2, 2) };
		foreach (int id in Blocks.Tools)
		{
			var (m, s) = toolRecipe[Blocks.B[id].Name.Split(' ').Last()];
			string mat = Blocks.B[id].Material;
			Recipes.Add(new() { Out = id, N = 1, Needs = new[] { (mat == "stone" ? Blocks.Cobblestone : Blocks.MetalBrick[mat], m), (Blocks.Stick, s) }, At = "bench" });
		}
		// gear, at the bench: armor from Leather or a metal's Bricks (helmet 3, chestplate 5, leggings 4, boots 3);
		// backpacks and belts are Leather, with a Brick of the metal for the metal ones
		for (int t = 0; t < Blocks.GearTiers.Length; t++)
		{
			string m = Blocks.GearTiers[t]; int mat = t == 0 ? Blocks.Leather : Blocks.MetalBrick[m];
			int[] n = { 3, 5, 4, 3 };
			for (int k = 0; k < 4; k++) Recipes.Add(new() { Out = Blocks.ArmorItem(t, k), N = 1, Needs = new[] { (mat, n[k]) }, At = "bench" });
			Recipes.Add(new() { Out = Blocks.Backpacks[t], N = 1, Needs = t == 0 ? new[] { (Blocks.Leather, 5) } : new[] { (Blocks.Leather, 4), (mat, 1) }, At = "bench" });
			Recipes.Add(new() { Out = Blocks.Belts[t], N = 1, Needs = t == 0 ? new[] { (Blocks.Leather, 3) } : new[] { (Blocks.Leather, 2), (mat, 1) }, At = "bench" });
		}
	}
	public bool CanCraft(Recipe r) =>
		r.Needs.All(n => CountOf(n.id) >= n.n) && (!r.Axe || Id.Any(i => Blocks.Get(i)?.Tool == "axe")) && (r.Tool == null || Id.Any(i => Blocks.Get(i)?.Tool == r.Tool));
	// true = made, false = can't, null = no room for what it makes (nothing changes)
	public bool? Craft(Recipe r)
	{
		if (!CanCraft(r)) return false;
		var was = Snapshot();
		foreach (var (id, n) in r.Needs) Take(id, n);
		for (int k = 0; k < r.N; k++) if (!Give(r.Out)) { Restore(was); return null; }
		return true;
	}
	public static string NeedsText(Recipe r) =>
		string.Join(" + ", r.Needs.Select(n => $"{n.n} {Blocks.B[n.id].Name}")) + (r.Axe ? ", with an axe" : "") + (r.Tool != null ? $", with a {r.Tool}" : "");

	// ===================== Crafting Bench: cut a block into pieces, slabs, stairs... =====================
	// [name, item id base, blocks used, how many you get]
	public static readonly (string key, string name, int baseId, int use, int n)[] BenchModes =
	{
		("pieces", "8 Pieces", Blocks.PieceItem, 1, 8),
		("slab", "2 Slabs", 1000, 1, 2),
		("vslab", "2 Vertical Slabs", 1100, 1, 2),
		("stairs", "4 Stairs (from 3 blocks)", 1200, 3, 4),
		("quarter", "4 Quarters", 1300, 1, 4),
		("vstairs", "4 Vertical Stairs (from 3 blocks)", 1400, 3, 4),
		("vquarter", "4 Vertical Quarters", 1500, 1, 4),
	};
	// returns what to show under the bench
	public string BenchCut(int block, int mode)
	{
		var (key, _, _, use, n) = BenchModes[mode];
		string bname = Blocks.B[block].Name;
		if (key != "pieces")
		{
			int outId = Blocks.BenchOut(key, block);
			if (Survival)
			{
				if (CountOf(block) < use) return $"You need {use} {bname} block{(use > 1 ? "s" : "")} in your hotbar";
				var was = Snapshot();
				Take(block, use);
				for (int k = 0; k < n; k++) if (!Give(outId)) { Restore(was); return "No room in your hotbar"; }
			}
			else for (int k = 0; k < n; k++) Give(outId);
			return $"Made {n} {Blocks.B[outId].Name}";
		}
		int piece = Blocks.FormItem("piece", block), got = 0;
		if (Survival)
		{
			// uses up one of that block from your hotbar, and needs room for all 8 pieces
			int s = Array.IndexOf(Id, block);
			if (s < 0) return $"You need a {bname} block in your hotbar";
			int room = 0;
			for (int k = 0; k < Slots; k++) room += Id[k] == 0 || (k == s && Count[k] == 1) ? Blocks.StackMax : Id[k] == piece ? Blocks.StackMax - Count[k] : 0;
			if (room < 8) return "No room in your hotbar for 8 pieces";
			if (--Count[s] <= 0) { Id[s] = 0; Count[s] = 0; }
		}
		if (InfinitePiece(piece)) got = Give(piece) ? 1 : 0;
		else for (int k = 0; k < 8; k++) if (Give(piece)) got++;
		return got == 0 ? "No room in your hotbar" : InfinitePiece(piece) ? $"{bname} Pieces are in your hotbar" : $"Took {got} {bname} Pieces";
	}
}
