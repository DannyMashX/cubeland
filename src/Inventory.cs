using System;
using System.Collections.Generic;
using System.Linq;

namespace Cubeland;

// What you carry: the 10-slot hotbar (keys 1-9, 0), and the crafting recipes that use it.
// Creative: blocks are unlimited (no count). Survival: everything is counted, and you only have what you collected.
public class Inventory
{
	public const int Slots = 10;
	public static readonly int[] Default = { 1, 2, 3, 8, 7, 4, 10, 12, 22, 6 };
	public readonly int[] Id = new int[Slots], Count = new int[Slots];
	public int Selected;
	public readonly bool Survival;
	public Action Changed;   // redraw the hotbar (and any open recipe list)

	public Inventory(bool survival)
	{
		Survival = survival;
		if (!survival) for (int i = 0; i < Slots; i++) { Id[i] = Default[i]; Count[i] = Blocks.IsItem(Default[i]) ? 1 : 0; }
	}
	public int Held => Id[Selected];
	public bool Counted(int id) => id != 0 && (Survival || Blocks.IsItem(id));
	// in creative mode pieces, slabs, stairs and quarters are unlimited, like whole blocks (no count, never used up)
	public bool InfinitePiece(int id) => (Blocks.IsPiece(id) || Blocks.FormOf(id) != null) && !Survival;
	public bool Holding(string tool) => Blocks.Get(Held)?.Tool == tool;
	public int CountOf(int id) { int n = 0; for (int i = 0; i < Slots; i++) if (Id[i] == id) n += Count[i]; return n; }

	public void SetSlot(int i, int id, int n = 1) { Id[i] = id; Count[i] = Counted(id) ? n : 0; Changed?.Invoke(); }
	public void UseOne() { UseOneAt(Selected); }
	public void UseOneAt(int i)
	{
		if (!Survival || Id[i] == 0) return;   // creative: nothing gets used up
		if (--Count[i] <= 0) { Id[i] = 0; Count[i] = 0; }
		Changed?.Invoke();
	}
	// picking up an item: top up a stack of it, otherwise the first empty slot. Returns false if there's no room.
	public bool Give(int id)
	{
		if (InfinitePiece(id) && Id.Contains(id)) return true;   // creative: a piece already in the hotbar never runs out
		for (int k = 0; k < Slots; k++) if (Id[k] == id && Counted(id) && Count[k] < Blocks.StackMaxOf(id)) { Count[k]++; Changed?.Invoke(); return true; }
		if (!Counted(id) && Id.Contains(id)) return true;          // creative block you already have
		int e = Array.IndexOf(Id, 0);
		// no empty slot: take over the right-most block slot (blocks can always be picked again). Not in survival.
		if (e < 0 && !Survival) for (int k = Slots - 1; k >= 0; k--) if (!Blocks.IsItem(Id[k])) { e = k; break; }
		if (e < 0) return false;
		SetSlot(e, id, 1);
		return true;
	}
	// take k of an item out of the hotbar (right-most first); false if there aren't enough
	public bool Take(int id, int k)
	{
		if (CountOf(id) < k) return false;
		for (int s = Slots - 1; s >= 0 && k > 0; s--) if (Id[s] == id)
		{
			int t = Math.Min(k, Count[s]); Count[s] -= t; k -= t;
			if (Count[s] == 0) Id[s] = 0;
		}
		Changed?.Invoke();
		return true;
	}
	// drag one slot onto another: the same item stacks together, anything else swaps
	public void Move(int from, int to)
	{
		if (from == to) return;
		if (Id[from] == Id[to] && Counted(Id[to]))
		{
			int moved = Math.Min(Count[from], Blocks.StackMaxOf(Id[to]) - Count[to]);
			Count[to] += moved; Count[from] -= moved;
			if (Count[from] == 0) Id[from] = 0;
		}
		else { (Id[from], Id[to]) = (Id[to], Id[from]); (Count[from], Count[to]) = (Count[to], Count[from]); }
		Changed?.Invoke();
	}
	public (int[] id, int[] n) Snapshot() => ((int[])Id.Clone(), (int[])Count.Clone());
	public void Restore((int[] id, int[] n) s) { Array.Copy(s.id, Id, Slots); Array.Copy(s.n, Count, Slots); Changed?.Invoke(); }

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
		new() { Out = Blocks.Torch, N = 2, Needs = new[] { (Blocks.Stick, 1), (Blocks.Torch, 1) } },
		new() { Out = Blocks.EverTorch, N = 1, Needs = new[] { (Blocks.Torch, 1), (Blocks.FireEssence, 1) } },
		new() { Out = Blocks.ClayBall, N = 4, Needs = new[] { (Blocks.Clay, 1) } },
		new() { Out = Blocks.ClayBall, N = 1, Needs = new[] { (Blocks.PieceItem + Blocks.Clay, 2) } },
		new() { Out = Blocks.BricksBlock, N = 1, Needs = new[] { (Blocks.Brick, 4) }, At = "bench" },
		new() { Out = Blocks.Furnace, N = 1, Needs = new[] { (Blocks.Brick, 8) }, At = "bench" },
		new() { Out = 67, N = 1, Needs = new[] { (65, 1) }, At = "bench", Tool = "pickaxe" },
		new() { Out = 68, N = 1, Needs = new[] { (66, 1) }, At = "bench", Tool = "pickaxe" },
		new() { Out = 69, N = 1, Needs = new[] { (3, 1) }, At = "bench", Tool = "pickaxe" },
		new() { Out = Blocks.Flour, N = 1, Needs = new[] { (Blocks.Grain, 1) } },   // Grain, mashed by hand -> Flour
	};
	static Inventory()
	{
		// dyes and colours
		foreach (int f in Blocks.Flowers) Recipes.Add(new() { Out = Blocks.B[f].Dye, N = 1, Needs = new[] { (f, 1) }, Colour = true });   // a Flower -> its Dye
		Recipes.Add(new() { Out = Blocks.DyeOf("black"), N = 1, Needs = new[] { (Blocks.GloomWisp, 1) }, Colour = true });   // a Gloom Wisp -> Black Dye
		Recipes.Add(new() { Out = Blocks.DyeOf("white"), N = 1, Needs = new[] { (Blocks.BoneAsh, 1) }, Colour = true });     // Bone Ash -> White Dye
		foreach (var (o, a, b) in new[] { ("orange", "red", "yellow"), ("green", "yellow", "aqua"), ("blue", "aqua", "indigo"), ("purple", "indigo", "magenta"), ("pink", "red", "white") })
			Recipes.Add(new() { Out = Blocks.DyeOf(o), N = 2, Needs = new[] { (Blocks.DyeOf(a), 1), (Blocks.DyeOf(b), 1) }, Colour = true });   // two dyes mixed
		for (int k = 0; k < Blocks.Colours.Length; k++)
		{
			Recipes.Add(new() { Out = Blocks.ColourTerracotta[k], N = 8, Needs = new[] { (Blocks.Terracotta, 8), (Blocks.Dye[k], 1) }, At = "bench", Colour = true });
			if (k > 0) Recipes.Add(new() { Out = Blocks.ColourChiseled[k], N = 1, Needs = new[] { (Blocks.ColourTerracotta[k], 1) }, At = "bench", Tool = "pickaxe", Colour = true });
			Recipes.Add(new() { Out = Blocks.ColourBricks[k], N = 1, Needs = new[] { (Blocks.ColourTerracotta[k], 1) }, At = "bench", Tool = "pickaxe", Colour = true });
		}
		// tools: made of Cobblestone, with sticks for the handle. [material, sticks]
		var toolRecipe = new Dictionary<string, (int m, int s)> { ["Pickaxe"] = (3, 2), ["Axe"] = (3, 2), ["Shovel"] = (1, 2), ["Sword"] = (2, 1), ["Hoe"] = (2, 2) };
		foreach (int id in Blocks.Tools)
		{
			var (m, s) = toolRecipe[Blocks.B[id].Name.Split(' ').Last()];
			Recipes.Add(new() { Out = id, N = 1, Needs = new[] { (Blocks.Cobblestone, m), (Blocks.Stick, s) }, At = "bench" });
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
		var (key, _, baseId, use, n) = BenchModes[mode];
		string bname = Blocks.B[block].Name;
		if (key != "pieces")
		{
			int outId = baseId + block;
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
		int piece = Blocks.PieceItem + block, got = 0;
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
