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
	// what a backpack that's been taken off carries (its slots, in order; D: the slots' own contents, such as a bag's, by
	// slot), or what's in a bag (its things in the order they went in)
	public class PackData { public int[] Id, N, W; public Dictionary<int, PackData> D; }
	public static readonly int[] Default = { 1, 2, 3, 8, 7, 4, 10, 12, 22, 6 };
	public readonly int[] Id = new int[NS], Count = new int[NS];
	public readonly PackData[] Data = new PackData[NS];
	// ---- Tool durability (Pack & Plate) ----
	// Each tool can be used Durability[its material] times. Breaking a block uses 1 (2 with the wrong tool for that block),
	// a hit on a mob uses 1 for a sword and 2 for any other tool, tilling uses 1. At 0 the tool breaks and is gone.
	// Wear: how much each slot's tool has been used (0 = new). A tool dropped on the ground keeps its wear. Survival only.
	public static readonly Dictionary<string, int> Durability = Nums.N.DURABILITY;   // (data/numbers.json)
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
	// ---- the Linen Bag (the user's, 2026-11-25; the same rules as the web version) ----
	// One slot holding a mix of things, up to one full stack (STACK_MAX) in all: each thing takes STACK_MAX / its own stack
	// size, so 1 for anything that stacks and the whole bag for a tool, armor, a pack, a belt (one to a slot). Not another bag,
	// nor anything holding things (a backpack with things in it). Its things are its slot's Data, in the order they went in
	// (an unworn thing that stacks joins its own entry); an empty bag has no Data.
	public const string BagFullNote = "The bag is full", BagNoNote = "That can't go in a bag";
	public static int BagRoom => Blocks.StackMax;
	public static int BagSize(int id) => Blocks.StackMax / Blocks.StackMaxOf(id);
	public static int BagUsed(PackData d) { int u = 0; if (d?.Id != null) for (int k = 0; k < d.Id.Length; k++) u += d.N[k] * BagSize(d.Id[k]); return u; }
	public static bool BagTakes(int id, PackData data) => id != 0 && Blocks.Get(id) != null && !Blocks.IsBag(id) && data == null;
	// put up to n of (id, wear) in a bag's things: how many went in (bag: its Data, made or changed)
	public static int BagPut(ref PackData bag, int id, int n, int wear)
	{
		int fit = Math.Min(n, (BagRoom - BagUsed(bag)) / BagSize(id));
		if (fit <= 0) return 0;
		var ids = bag?.Id?.ToList() ?? new(); var ns = bag?.N?.ToList() ?? new(); var ws = bag?.W?.ToList() ?? new();
		int at = wear == 0 && Blocks.StackMaxOf(id) > 1 ? Enumerable.Range(0, ids.Count).FirstOrDefault(k => ids[k] == id && ws[k] == 0, -1) : -1;
		if (at >= 0) ns[at] += fit; else { ids.Add(id); ns.Add(fit); ws.Add(wear); }
		bag = new PackData { Id = ids.ToArray(), N = ns.ToArray(), W = ws.ToArray() };
		return fit;
	}
	// the last thing put in, all of it, out of a bag's things (null if there's nothing)
	public static (int id, int n, int w)? BagTakeLast(ref PackData bag)
	{
		if (bag?.Id == null || bag.Id.Length == 0) { bag = null; return null; }
		int k = bag.Id.Length - 1; var got = (bag.Id[k], bag.N[k], bag.W?.ElementAtOrDefault(k) ?? 0);
		bag = k == 0 ? null : new PackData { Id = bag.Id[..k], N = bag.N[..k], W = (bag.W ?? new int[k + 1])[..k] };
		return got;
	}
	// a bag's things as checked from outside (a friend's game): whole numbers, real things, at least 1 of each, no bag nor
	// anything holding things, wear only on what wears (and less than wears it out), 1-64 entries and the room it has;
	// anything else: null (an empty bag)
	public static bool WearOk(int id, int w) => w == 0 || (w > 0 && w < MaxWear(id));
	public static PackData BagChecked(int[] ids, int[] ns, int[] ws)
	{
		if (ids == null || ns == null || ids.Length == 0 || ids.Length > BagRoom || ns.Length != ids.Length || (ws != null && ws.Length != ids.Length)) return null;
		for (int k = 0; k < ids.Length; k++) if (Blocks.Get(ids[k]) == null || Blocks.IsBag(ids[k]) || ns[k] < 1 || ns[k] > BagRoom || (ws != null && !WearOk(ids[k], ws[k]))) return null;
		var d = new PackData { Id = ids, N = ns, W = ws ?? new int[ids.Length] };
		return BagUsed(d) <= BagRoom ? d : null;
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
		for (int k = Pack0; k < Tool0; k++)
		{
			d.Id[k - Pack0] = Id[k]; d.N[k - Pack0] = Count[k]; d.W[k - Pack0] = Wear[k];
			if (Data[k] != null) (d.D ??= new())[k - Pack0] = Data[k];   // (a bag in it keeps what it holds)
			Id[k] = Count[k] = Wear[k] = 0; Data[k] = null;
		}
		Data[BackSlot] = d.Id.Any(x => x != 0) ? d : null;
	}
	// ...and a backpack put on spreads what it carries back into them
	public void UnpackBack()
	{
		var d = Data[BackSlot]; Data[BackSlot] = null;
		if (d?.Id == null) return;
		for (int k = 0; k < d.Id.Length && k < Tool0 - Pack0; k++) { Id[Pack0 + k] = d.Id[k]; Count[Pack0 + k] = d.N?.ElementAtOrDefault(k) ?? 0; Wear[Pack0 + k] = d.W?.ElementAtOrDefault(k) ?? 0; Data[Pack0 + k] = d.D?.GetValueOrDefault(k); }
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
	public static readonly Dictionary<string, int> ArmorDurability = Nums.N.ARMOR_DURABILITY;   // (data/numbers.json)
	public static readonly float[] ArmorShare = Nums.N.ARMOR_SHARE;   // (data/numbers.json: helmet, chestplate, leggings, boots)
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
	// every recipe, in the order the recipe lists show them, and the Cutter's shapes: data/recipes.json
	// (the deep layers' Chiseled Stone only while tall worlds are on)
	static readonly RecipesData RecipeFile = GameData.Read<RecipesData>("recipes") ?? throw new InvalidOperationException("data/recipes.json is missing or can't be read");
	public static readonly List<Recipe> Recipes = RecipesFrom(RecipeFile, World.TallWorlds);
	// data/recipes.json as the game's tables (tall: with the deep layers' Chiseled Stone)
	public static List<Recipe> RecipesFrom(RecipesData d, bool tall) => d.RECIPES.Where(r => tall || !r.tall).Select(r => new Recipe
	{
		Out = DataKeys.IdOf(r.@out), N = r.n, Needs = r.needs.Select(n => (DataKeys.IdOf(n[0].GetString()), n[1].GetInt32())).ToArray(),
		At = r.at is "hand" or "bench" ? r.at : throw new InvalidOperationException($"data/recipes.json: a recipe for {r.@out} is made at \"{r.at}\" (hand or bench)"),
		Axe = r.axe, Tool = r.tool, Colour = r.colour,
	}).ToList();
	public static (string key, string name, int baseId, int use, int n)[] CutterFrom(RecipesData d) => d.CUTTER.Select(c => (c.mode, c.label,
		c.mode == "pieces" ? Blocks.PieceItem : Blocks.ShapeItem.TryGetValue(c.mode ?? "", out int b) ? b : throw new InvalidOperationException($"data/recipes.json: the Cutter can't make \"{c.mode}\""),
		c.uses, c.makes)).ToArray();
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
	// its buttons, in order: [shape, label, item id base, blocks used, how many you get] (data/recipes.json's CUTTER;
	// pieces always give a block's 8 spots)
	public static readonly (string key, string name, int baseId, int use, int n)[] BenchModes = CutterFrom(RecipeFile);
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
