using System;
using System.Collections.Generic;
using System.Linq;

namespace Cubeland;

// One kind of block or item. T = [side tile, top tile, bottom tile, (front tile)] in the texture atlas.
public class BlockDef
{
	public string Name;
	public int[] T;
	public bool Transparent, NoCollide, Liquid, Item, Lit;
	public string Shape;            // null (a cube), "torch", "flat", "plant", "pieces", "half" (the bottom half of the block space)
	public float[] Box;             // the part you can aim at, inside the block space: [x0, y0, z0, x1, y1, z1]
	public int Drop;                // what breaking it gives you (0 = itself)
	public int Places;              // an item that puts down a block (a Rock puts down a rock lying on the ground)
	public int Icon = -1;           // its hotbar picture, if not T[0]
	public int[] Front;             // [x, z]: the side that shows T[3] (the kiln's front)
	public int Facing;
	public int[] Wall;              // torch: [x, z] direction of the wall it leans off (null = standing)
	public int Wi, Count, Burn;     // torch: which wall (0 = standing), how many in the spot, lit/smoldering/burnt/ever
	public int Level;               // water: 0 = still, 1-7 = flowing (weaker further out)
	public int Food;                // eating it heals this many quarter hearts
	public string Tool, Material;   // tools: "axe", "pickaxe"...; "stone", "crude"
	public int Damage;              // how much a hit with it takes off a mob
	public int Piece;               // a piece item: the block it's a piece of
	public string Form;             // a shape item: "slab", "stairs"...
	public int Of;                  // a shape item: the block it's made of
	public int Dye;                 // a flower: the dye it makes
	public int Layer;               // deep stone sets: which layer of the strata (2-8; the normal stone set is layer 1)
	public int Pick;                // ores: how good a pickaxe it needs (Blocks.PickTier)
	public int Armor = -1, Pack, Belt, BeltTools;   // gear: which piece of armor (0 helmet .. 3 boots), backpack slots, belt offhand slots and tool slots
	public int[] HandDrop;          // dug without a shovel it gives [item, fewest, most] of that instead (Fireclay -> Fireclay Balls)
}

// Every block and item in the game (the same numbers as the web version, so saved worlds line up).
public static class Blocks
{
	public const int MaxId = 2000;
	// Strata (640-tall worlds): from sea level down, a new layer of stone every 64 blocks, down to bedrock at the very bottom.
	// Layer 1 is the normal Stone (and everything above sea level); layers 2-8 are blocks 113-119, each darker and
	// harder: past a few layers, only a better pickaxe can mine it (see StrataPickaxe). All of them are called Stone.
	// Bedrock (120) can't be broken in survival (and nothing can break the bottom layer of the world).
	// Each deep layer has its own stone set, in that layer's shade: Stone (113-119), Cobblestone (121-127), Stone Bricks
	// (128-134) and Chiseled Stone (135-141). Deep Stone gives its layer's Cobblestone; the Kiln turns that back into its Stone.
	public const int StrataLayers = 8, StrataThick = 64, Strata = 113, Bedrock = 120;
	public const int DeepCobble = 121, DeepBricks = 128, DeepChiseled = 135, DeepLast = DeepChiseled + StrataLayers - 2;
	// a normal stone-set block (Stone, Cobblestone, Stone Bricks, Chiseled Stone) -> its versions in layers 2, 3, ...
	public static int[] DeepRow(int b) => Enumerable.Range(b == 3 ? Strata : b == 8 ? DeepCobble : b == 9 ? DeepBricks : DeepChiseled, StrataLayers - 1).ToArray();
	public static int[] DeepShown(int b) => World.TallWorlds ? DeepRow(b) : Array.Empty<int>();   // the same, but only while tall worlds are on
	public static readonly int[] DeepBlocks = new[] { 3, 8, 9, 69 }.SelectMany(DeepRow).ToArray();
	// what a piece, slab or stairs of this block gives when broken (Stone, deep or not, gives its Cobblestone)
	public static int StoneDropOf(int b) => b == 3 || (b >= Strata && b < Bedrock) ? B[b].Drop : b;
	// The deep stone sets' piece, slab, stairs... items can't use 100 + b and 1000 + b (those numbers clash with other blocks'),
	// so theirs are 1800 + 7 x (the block's place in DeepBlocks) + (piece 0, slab 1, vslab 2, stairs 3, quarter 4, vstairs 5, vquarter 6).
	public static readonly string[] Forms = { "piece", "slab", "vslab", "stairs", "quarter", "vstairs", "vquarter" };
	public const int DeepFormItem = 1800;
	// the item for a piece / slab / stairs... of block b
	public static int BenchOut(string mode, int b) => FormItem(mode == "pieces" ? "piece" : mode, b);   // what a bench mode makes from block b
	public static int FormItem(string form, int b)
	{
		int i = Array.IndexOf(DeepBlocks, b);
		return i >= 0 ? DeepFormItem + 7 * i + Array.IndexOf(Forms, form) : (form == "piece" ? PieceItem : ShapeItem[form]) + b;
	}
	public static int StoneLayer(int b) => b == 3 ? 1 : Get(b)?.Layer ?? 0;
	// the deepest layer each pickaxe can mine, by what it's made of (copper and up are still to come)
	public static readonly Dictionary<string, int> StrataPickaxe = new() { ["stone"] = 2, ["copper"] = 3, ["iron"] = 4, ["bronite"] = 6, ["rosium"] = 7, ["steel"] = 8 };
	public static readonly BlockDef[] B = new BlockDef[MaxId];
	public static BlockDef Get(int id) => id > 0 && id < MaxId ? B[id] : null;

	public const int Grass = 1, Dirt = 2, Stone = 3, Log = 4, Leaves = 5, Sand = 6, Planks = 7, Cobblestone = 8,
		StoneBricks = 9, Glass = 10, Gravel = 11, CraftingBench = 60, Cutter = 159, StrippedLog = 61, RockBlock = 62, Clay = 63,
		BricksBlock = 64, Terracotta = 65, Pieces = 70, ClayBallBlock = 71, BrickBlock = 72, Furnace = 73;
	public const int Torch = 22, SmolderingTorch = 81, BurntTorch = 170, EverTorch = 182, Water = 50;
	public const int Chocolate = 200, Stick = 201, Rock = 212, CrudeAxe = 213, ClayBall = 215, Brick = 216, FireEssence = 217;
	public const int Bone = 620, BoneAsh = 621, GloomWisp = 622;   // Fear & Foes (items 620+; 600-611 are the dyes)
	// Till & Tend (farming Grain; see Farm.cs): items 623-626, soil blocks 93-96, Grain growing 42-49 (49 = ripe)
	public const int GrainSeeds = 623, Grain = 624, Flour = 625, Bread = 626;
	public const int Leather = 627, RawMeat = 628, CookedMeat = 629;   // cows (items 627-629)
	// ---- Pack & Plate: ores, Fireclay and the Foundry ----
	// Ores (142-145) lie in veins in the stone (WorldGen.PlaceOres). Mining one gives its Chunk, and needs a good enough pickaxe
	// (Pick: the PickTier it needs): Copper Ore any pickaxe, Iron Ore copper or better, Tin and Gold Ore iron or better.
	// The Foundry melts Chunks into Bricks (items 630-643), mixes two metals' Bricks into an alloy, and makes Steel.
	// Fireclay (146) is found in patches a little below the surface; it works like Clay: by hand it comes up as Fireclay Balls,
	// which the Kiln fires into Fireclay Bricks (and the block into Fireclay Terracotta). These blocks (146-150) build the Foundry.
	// (They can't be cut into pieces: 246-250 are already the pieces' item numbers of other blocks.)
	public const int CopperOre = 142, IronOre = 143, GoldOre = 144, TinOre = 145;
	public const int Fireclay = 146, FireclayTerracotta = 147, FireclayBricks = 148, FireclayCasing = 149, ChiseledFireclay = 150;
	public const int Foundry = 151;   // the Foundry's Furnace: 151-154 unlit, 155-158 lit; it faces the way you put it down, like the Kiln
	public const int SteelScrap = 640, SteelBrick = 641, FireclayBall = 642, FireclayBrick = 643;
	public static readonly int[] Ores = { CopperOre, IronOre, GoldOre, TinOre };
	public static readonly string[] Metals = { "copper", "iron", "gold", "tin" };
	public static readonly Dictionary<string, int> Chunk = new() { ["copper"] = 630, ["iron"] = 631, ["gold"] = 632, ["tin"] = 633 };
	public static readonly Dictionary<string, int> MetalBrick = new() { ["copper"] = 634, ["iron"] = 635, ["gold"] = 636, ["tin"] = 637, ["bronite"] = 638, ["rosium"] = 639, ["steel"] = SteelBrick };
	public static readonly Dictionary<string, int> PickTier = new() { ["stone"] = 1, ["copper"] = 2, ["iron"] = 3, ["bronite"] = 4, ["rosium"] = 5, ["steel"] = 6 };   // how good each pickaxe is
	public static readonly int[] FireclayBlocks = { FireclayTerracotta, FireclayBricks, FireclayCasing, ChiseledFireclay };
	public static readonly int[] FoundryIds = Enumerable.Range(Foundry, 8).ToArray();
	public static readonly int[] PackItems = { 630, 631, 632, 633, 634, 635, 636, 637, 638, 639, SteelScrap, SteelBrick, FireclayBall, FireclayBrick };
	public static bool IsFoundry(int id) => id >= Foundry && id < Foundry + 8;
	public const int Tilled = 93, WetTilled = 94, Fertilized = 95, WetFertilized = 96, GrainRipe = 49;
	public static readonly int[] GrainStages = { 42, 43, 44, 45, 46, 47, 48, 49 };
	public static readonly int[] Soils = { Tilled, WetTilled, Fertilized, WetFertilized };
	public static bool IsSoil(int id) => id >= Tilled && id <= WetFertilized;
	public static bool IsCrop(int id) => id >= 42 && id <= GrainRipe;
	public static int SoilId(bool wet, bool fert) => Tilled + (wet ? 1 : 0) + (fert ? 2 : 0);
	public static bool SoilWet(int id) => id == WetTilled || id == WetFertilized;
	public static bool SoilFert(int id) => id == Fertilized || id == WetFertilized;
	public const int PieceItem = 100;
	public static readonly int[] BrickVariants = { 12, 13, 14, 15 };
	public static readonly int[][] FurnaceFronts = { new[] { 0, 1 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { -1, 0 } };
	public static readonly int[][] TorchWalls = { null, new[] { -1, 0 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { 0, 1 } };
	static readonly Dictionary<string, int> torchIds = new();
	public static readonly List<int> Tools = new();
	// ---- gear (Pack & Plate): armor, backpacks and utility belts, in six tiers ----
	public static readonly string[] GearTiers = { "leather", "copper", "iron", "bronite", "rosium", "steel" };
	public static readonly string[] ArmorPieces = { "helmet", "chestplate", "leggings", "boots" };
	public static int ArmorItem(int t, int k) => 669 + t * 4 + k;   // 669-692
	public static readonly int[] Backpacks = { 693, 694, 695, 696, 697, 698 }, Belts = { 699, 700, 701, 702, 703, 704 };
	public static readonly int[] PackSlots = { 5, 10, 15, 20, 30, 40 }, BeltSlots = { 1, 2, 3, 5, 7, 9 };
	public static readonly List<int> GearItems = new();
	public static bool IsGear(int id) => Get(id) is { } d && (d.Armor >= 0 || d.Pack > 0 || d.Belt > 0);
	// ---- Colours (Brick & Bloom): 12 colours, each with a Terracotta, a Chiseled Terracotta, Bricks and a Dye ----
	// Red's blocks came first (Red Terracotta 66, Chiseled Red Terracotta 68, Red Bricks 12); the other 11 colours use 218-250.
	// (Placed blocks must be numbered under 256, and a block's slab/stairs items are 1000/1100/.../1500 + its number, so new
	// blocks that can be cut into pieces sit at 218-250, where those item numbers can't clash with any other block's.)
	// Dyes are items 600-611. Flowers are 194-198. The same numbers as the web version.
	public static readonly string[] Colours = { "red", "orange", "yellow", "green", "aqua", "blue", "indigo", "purple", "magenta", "pink", "white", "black" };
	public static readonly int[] ColourTerracotta = Enumerable.Range(0, 12).Select(k => k > 0 ? 217 + k : 66).ToArray();
	public static readonly int[] ColourChiseled = Enumerable.Range(0, 12).Select(k => k > 0 ? 228 + k : 68).ToArray();
	public static readonly int[] ColourBricks = Enumerable.Range(0, 12).Select(k => k > 0 ? 239 + k : 12).ToArray();
	public static readonly int[] Dye = Enumerable.Range(0, 12).Select(k => 600 + k).ToArray();
	public static int DyeOf(string c) => Dye[Array.IndexOf(Colours, c)];
	public static readonly int[] NewColourBlocks = ColourTerracotta.Skip(1).Concat(ColourChiseled.Skip(1)).Concat(ColourBricks.Skip(1)).ToArray();
	// Flowers: grow in small patches on grass; you walk through them, pick them by hand, and can plant them again on grass or dirt
	public static readonly string[] FlowerColours = { "red", "yellow", "aqua", "indigo", "magenta" };
	public static readonly int[] Flowers = { 194, 195, 196, 197, 198 };
	public static bool IsFlower(int id) => id >= 194 && id <= 198;
	public static readonly int[] Splittable = new[] { 1, 2, 3, 8, 7, 4, 61, 5, 6, 9, 12, 10, 11, 63, 64, 65, 66, 67, 68, 69 }.Concat(NewColourBlocks).Concat(DeepBlocks).ToArray();
	public static readonly int[] NeedsAxe = { 4, 7, 61 }, Diggable = { 1, 2, 6, 11, 63, Fireclay, 93, 94, 95, 96 };
	public static readonly int[] NeedsPickaxe = new[] { 3, 8, 9, 12, 13, 14, 15, 64, 65, 66, 67, 68, 69, 73, 74, 75, 76, 77, 78, 79, 80 }.Concat(NewColourBlocks).Concat(DeepBlocks)
		.Concat(Ores).Concat(FireclayBlocks).Concat(FoundryIds).ToArray();
	// Every block you can pick from the creative inventory
	public static readonly int[] Items = new[] { new[] { 1, 2, 3 }, DeepShown(3), new[] { 8 }, DeepShown(8), new[] { 7, 60, Cutter, 4, 61, 5, 6, 9 }, DeepShown(9),
		new[] { 12, 10, 11, 63, 64, 65, 66, 67, 68, 69 }, DeepShown(69) }.SelectMany(a => a).Concat(NewColourBlocks).Concat(Flowers)
		.Concat(new[] { Furnace, 22, Water, Bedrock }).Concat(Ores).Append(Fireclay).Concat(FireclayBlocks).Append(Foundry).ToArray();
	public const int StackMax = 64;

	// shapes made of pieces: bit masks of the 8 spots (spot number = x + y*2 + z*4)
	public static readonly Dictionary<string, int[]> ShapeMasks = new()
	{
		["slab"] = new[] { 0x33, 0xCC },
		["vslab"] = new[] { 0x55, 0xAA, 0x0F, 0xF0 },
		["stairs"] = new[] { 0x33 | 0x44, 0x33 | 0x88, 0x33 | 0x0C, 0x33 | 0xC0, 0xCC | 0x11, 0xCC | 0x22, 0xCC | 0x03, 0xCC | 0x30 },
		["vstairs"] = new[] { 0xFF ^ 0x05, 0xFF ^ 0x0A, 0xFF ^ 0x50, 0xFF ^ 0xA0 },
		["quarter"] = new[] { 0x03, 0x0C, 0x30, 0xC0, 0x11, 0x22, 0x44, 0x88 },
		["vquarter"] = new[] { 0x05, 0x0A, 0x50, 0xA0 },
	};
	public static readonly Dictionary<string, int> ShapeItem = new()
	{ ["slab"] = 1000, ["vslab"] = 1100, ["stairs"] = 1200, ["quarter"] = 1300, ["vstairs"] = 1400, ["vquarter"] = 1500 };
	static readonly Dictionary<string, string> shapeName = new()
	{ ["slab"] = "Slab", ["vslab"] = "Vertical Slab", ["stairs"] = "Stairs", ["quarter"] = "Quarter", ["vstairs"] = "Vertical Stairs", ["vquarter"] = "Vertical Quarter" };

	static BlockDef Def(int id, string name, int side, int top, int bottom)
	{
		var b = new BlockDef { Name = name, T = new[] { side, top, bottom } };
		B[id] = b;
		return b;
	}

	static Blocks()
	{
		Def(1, "Grass", 1, 0, 2).Drop = 2;   // digging up Grass gives Dirt
		Def(2, "Dirt", 2, 2, 2);
		Def(3, "Stone", 3, 3, 3).Drop = 8;   // mining Stone gives Cobblestone
		for (int n = 2; n <= StrataLayers; n++)   // the deep stone sets: pictures 124-130 (stone), 132-138, 139-145, 146-152
		{
			int k = n - 2;
			var d = Def(Strata + k, "Stone", 122 + n, 122 + n, 122 + n); d.Drop = DeepCobble + k; d.Layer = n;
			Def(DeepCobble + k, "Cobblestone", 130 + n, 130 + n, 130 + n).Layer = n;
			Def(DeepBricks + k, "Stone Bricks", 137 + n, 137 + n, 137 + n).Layer = n;
			Def(DeepChiseled + k, "Chiseled Stone", 144 + n, 144 + n, 144 + n).Layer = n;
		}
		Def(Bedrock, "Bedrock", 131, 131, 131);
		Def(4, "Log", 4, 5, 5);
		Def(5, "Leaves", 6, 6, 6).Transparent = true;
		Def(6, "Sand", 7, 7, 7);
		Def(7, "Planks", 8, 8, 8);
		Def(8, "Cobblestone", 9, 9, 9);
		Def(9, "Stone Bricks", 10, 10, 10);
		Def(10, "Glass", 11, 11, 11).Transparent = true;
		Def(11, "Gravel", 12, 12, 12);
		for (int k = 0; k < 4; k++) Def(12 + k, "Red Bricks", 13 + k, 13 + k, 13 + k);   // 4 looks, picked at random when placed
		Def(60, "Crafting Bench", 28, 29, 8);
		// the Cutter: cuts blocks into pieces, slabs and stairs. Half a block tall, with a saw blade standing up out of it
		// (pictures: cutter_base on the sides, the stripped log top on top, cutter_bottom underneath, cutter_saw for the blade)
		var cutter = Def(Cutter, "Cutter", 241, 31, 242); cutter.Shape = "half"; cutter.Transparent = true; cutter.Box = new[] { 0f, 0, 0, 1, 0.5f, 1 };
		Def(61, "Stripped Log", 30, 31, 31);
		var rock = Def(62, "Rock", 33, 33, 33); rock.Transparent = true; rock.NoCollide = true; rock.Shape = "flat"; rock.Box = new[] { 0.1f, 0, 0.1f, 0.9f, 0.12f, 0.9f };
		Def(63, "Clay", 17, 17, 17);
		Def(64, "Bricks", 18, 18, 18);
		Def(65, "Terracotta", 19, 19, 19);
		Def(66, "Red Terracotta", 20, 20, 20);
		Def(67, "Chiseled Terracotta", 21, 21, 21);
		Def(68, "Chiseled Red Terracotta", 22, 22, 22);
		Def(69, "Chiseled Stone", 35, 35, 35);
		var cb = Def(71, "Clay Ball", 45, 45, 45); cb.Transparent = true; cb.NoCollide = true; cb.Shape = "flat"; cb.Box = new[] { 0.15f, 0, 0.15f, 0.85f, 0.12f, 0.85f };
		var br = Def(72, "Brick", 46, 46, 46); br.Transparent = true; br.NoCollide = true; br.Shape = "flat"; br.Box = new[] { 0.1f, 0, 0.1f, 0.9f, 0.12f, 0.9f };
		// Kiln: 73-76 unlit, 77-80 lit; each faces one way. t = [side, top (Bricks), bottom, front]
		for (int k = 0; k < 4; k++)
		{
			B[Furnace + k] = new BlockDef { Name = "Kiln", T = new[] { 43, 18, 44, 36 }, Front = FurnaceFronts[k], Facing = k, Drop = Furnace };
			B[Furnace + 4 + k] = new BlockDef { Name = "Kiln", T = new[] { 43, 18, 44, 37 }, Front = FurnaceFronts[k], Facing = k, Lit = true, Drop = Furnace };
		}
		B[Furnace].Icon = 36;
		// Torches: (wall, count) combos. Lit = 22-41, smoldering = 81-92, burnt = 170-181, ever = 182-193
		string[] tName = { "Torch", "Smoldering Torch", "Burnt Torch", "Ever Torch" };
		int[][] tTiles = { new[] { 23, 24, 25 }, new[] { 48, 49, 25 }, new[] { 50, 51, 25 }, new[] { 23, 24, 25 } };
		int next = 27;
		for (int wi = 0; wi < 5; wi++)
			for (int n = 1; n <= 4; n++)
			{
				int id = n == 1 ? 22 + wi : next++;
				B[id] = new BlockDef { Name = "Torch", T = tTiles[0], Transparent = true, Shape = "torch", NoCollide = true, Wall = TorchWalls[wi], Wi = wi, Count = n, Burn = 0 };
				torchIds[$"0:{wi}:{n}"] = id;
			}
		int[] firsts = { 81, 170, 182 };
		for (int k = 0; k < 3; k++)
		{
			int burn = k + 1, id = firsts[k];
			for (int wi = 0; wi < 5; wi++)
				for (int n = 1; n <= (wi > 0 ? 2 : 4); n++)
				{
					B[id] = new BlockDef { Name = tName[burn], T = tTiles[burn], Transparent = true, Shape = "torch", NoCollide = true, Wall = TorchWalls[wi], Wi = wi, Count = n, Burn = burn };
					torchIds[$"{burn}:{wi}:{n}"] = id++;
				}
		}
		// Water: 50 = still, 51-57 = flowing
		for (int l = 0; l <= 7; l++) B[Water + l] = new BlockDef { Name = "Water", T = new[] { 27, 27, 27 }, Transparent = true, NoCollide = true, Liquid = true, Level = l };
		// Items (they share the block list so icons and names work the same way)
		B[Chocolate] = new BlockDef { Name = "Chocolate", T = new[] { 26, 26, 26 }, Item = true, Food = 4 };
		B[Stick] = new BlockDef { Name = "Stick", T = new[] { 32, 32, 32 }, Item = true };
		string[] kinds = { "Axe", "Pickaxe", "Shovel", "Sword", "Hoe" };
		// stone tools 207-211; metal tools (Pack & Plate) 644-668: copper, iron, bronite, rosium, steel, 5 each in the same order (pictures 179-203)
		foreach (var (key, label, firstTile, sword, firstId) in new[] { ("stone", "Stone", 38, 3, 207), ("copper", "Copper", 179, 4, 644), ("iron", "Iron", 184, 5, 649),
			("bronite", "Bronite", 189, 6, 654), ("rosium", "Rosium", 194, 7, 659), ("steel", "Steel", 199, 8, 664) })
			for (int k = 0; k < 5; k++)
			{
				int id = firstId + k, tile = firstTile + k;
				B[id] = new BlockDef { Name = label + " " + kinds[k], T = new[] { tile, tile, tile }, Item = true, Tool = kinds[k].ToLower(), Material = key,
					Damage = kinds[k] == "Sword" ? sword : kinds[k] == "Axe" ? sword - 1 : 1 };
				Tools.Add(id);
			}
		B[Rock] = new BlockDef { Name = "Rock", T = new[] { 33, 33, 33 }, Item = true, Places = RockBlock };
		B[RockBlock].Drop = Rock;
		B[CrudeAxe] = new BlockDef { Name = "Crude Axe", T = new[] { 34, 34, 34 }, Item = true, Tool = "axe", Material = "crude", Damage = 1 };
		B[ClayBall] = new BlockDef { Name = "Clay Ball", T = new[] { 45, 45, 45 }, Item = true, Places = ClayBallBlock };
		B[Brick] = new BlockDef { Name = "Brick", T = new[] { 46, 46, 46 }, Item = true };
		B[FireEssence] = new BlockDef { Name = "Fire Essence", T = new[] { 47, 47, 47 }, Item = true };
		B[ClayBallBlock].Drop = ClayBall;
		B[BrickBlock].Drop = Brick;
		// Fear & Foes: Skeletons drop Bones (Kiln -> Bone Ash -> White Dye); a Gloomling beaten in the dark leaves a Gloom Wisp (-> Black Dye)
		B[Bone] = new BlockDef { Name = "Bone", T = new[] { 102, 102, 102 }, Item = true };
		B[BoneAsh] = new BlockDef { Name = "Bone Ash", T = new[] { 103, 103, 103 }, Item = true };
		B[GloomWisp] = new BlockDef { Name = "Gloom Wisp", T = new[] { 104, 104, 104 }, Item = true };
		// Till & Tend: Grain Seeds, Grain (-> Flour by hand -> Bread in the Kiln), tilled soil, and Grain growing in 8 stages
		B[GrainSeeds] = new BlockDef { Name = "Grain Seeds", T = new[] { 105, 105, 105 }, Item = true };
		B[Grain] = new BlockDef { Name = "Grain", T = new[] { 106, 106, 106 }, Item = true };
		B[Flour] = new BlockDef { Name = "Flour", T = new[] { 107, 107, 107 }, Item = true };
		B[Bread] = new BlockDef { Name = "Bread", T = new[] { 108, 108, 108 }, Item = true, Food = 6 };
		// cows: Leather (more from a cow with room to roam), Raw Meat (more from a well-fed cow) -> Cooked Meat in the Kiln
		B[Leather] = new BlockDef { Name = "Leather", T = new[] { 121, 121, 121 }, Item = true };
		B[RawMeat] = new BlockDef { Name = "Raw Meat", T = new[] { 122, 122, 122 }, Item = true, Food = 2 };
		B[CookedMeat] = new BlockDef { Name = "Cooked Meat", T = new[] { 123, 123, 123 }, Item = true, Food = 8 };
		// Pack & Plate: ores 153-156, Fireclay blocks 157-161, the Foundry's Furnace 162-164, chunks 165-168, bricks 169-176, fireclay items 177-178
		for (int k = 0; k < 4; k++)
		{
			string m = Metals[k], M = char.ToUpper(m[0]) + m[1..];
			Def(Ores[k], M + " Ore", 153 + k, 153 + k, 153 + k).Drop = Chunk[m];
			B[Ores[k]].Pick = new[] { 1, 2, 3, 3 }[k];
			B[Chunk[m]] = new BlockDef { Name = M + " Chunk", T = new[] { 165 + k, 165 + k, 165 + k }, Item = true };
			B[MetalBrick[m]] = new BlockDef { Name = M + " Brick", T = new[] { 169 + k, 169 + k, 169 + k }, Item = true };
		}
		B[MetalBrick["bronite"]] = new BlockDef { Name = "Bronite Brick", T = new[] { 173, 173, 173 }, Item = true };
		B[MetalBrick["rosium"]] = new BlockDef { Name = "Rosium Brick", T = new[] { 174, 174, 174 }, Item = true };
		B[SteelScrap] = new BlockDef { Name = "Steel Scrap", T = new[] { 175, 175, 175 }, Item = true };
		B[SteelBrick] = new BlockDef { Name = "Steel Brick", T = new[] { 176, 176, 176 }, Item = true };
		B[FireclayBall] = new BlockDef { Name = "Fireclay Ball", T = new[] { 177, 177, 177 }, Item = true };
		B[FireclayBrick] = new BlockDef { Name = "Fireclay Brick", T = new[] { 178, 178, 178 }, Item = true };
		// gear: armor pictures 204-227, backpacks 228-233, belts 234-239
		for (int t = 0; t < GearTiers.Length; t++)
		{
			string m = GearTiers[t], M = char.ToUpper(m[0]) + m[1..];
			for (int k = 0; k < 4; k++)
			{
				int tile = 204 + t * 4 + k;
				B[ArmorItem(t, k)] = new BlockDef { Name = $"{M} {char.ToUpper(ArmorPieces[k][0]) + ArmorPieces[k][1..]}", T = new[] { tile, tile, tile }, Item = true, Armor = k, Material = m };
				GearItems.Add(ArmorItem(t, k));
			}
		}
		for (int t = 0; t < GearTiers.Length; t++)
		{
			string m = GearTiers[t], M = char.ToUpper(m[0]) + m[1..];
			B[Backpacks[t]] = new BlockDef { Name = M + " Backpack", T = new[] { 228 + t, 228 + t, 228 + t }, Item = true, Pack = PackSlots[t], Material = m };
			B[Belts[t]] = new BlockDef { Name = M + " Utility Belt", T = new[] { 234 + t, 234 + t, 234 + t }, Item = true, Belt = BeltSlots[t], BeltTools = t + 2, Material = m };
		}
		GearItems.AddRange(Backpacks); GearItems.AddRange(Belts);
		Def(Fireclay, "Fireclay", 157, 157, 157).HandDrop = new[] { FireclayBall, 1, 4 };
		Def(FireclayTerracotta, "Fireclay Terracotta", 158, 158, 158);
		Def(FireclayBricks, "Fireclay Bricks", 159, 159, 159);
		Def(FireclayCasing, "Fireclay Casing", 160, 160, 160);
		Def(ChiseledFireclay, "Chiseled Fireclay", 161, 161, 161);
		for (int k = 0; k < 4; k++)
		{
			B[Foundry + k] = new BlockDef { Name = "Furnace", T = new[] { 160, 159, 164, 162 }, Front = FurnaceFronts[k], Facing = k, Drop = Foundry };
			B[Foundry + 4 + k] = new BlockDef { Name = "Furnace", T = new[] { 160, 159, 164, 163 }, Front = FurnaceFronts[k], Facing = k, Lit = true, Drop = Foundry };
		}
		B[Foundry].Icon = 162;
		string[] soilNames = { "Tilled Dirt", "Wet Tilled Dirt", "Fertilized Dirt", "Wet Fertilized Dirt" };
		for (int k = 0; k < 4; k++) Def(Tilled + k, soilNames[k], 2, 109 + k, 2).Drop = Dirt;
		for (int k = 0; k < 8; k++)
		{
			var g = Def(GrainStages[k], "Grain", 113 + k, 113 + k, 113 + k);
			g.Transparent = true; g.NoCollide = true; g.Shape = "plant"; g.Box = new[] { 0.15f, 0, 0.15f, 0.85f, 0.15f + k * 0.12f, 0.85f };
			g.Drop = k == 7 ? Grain : GrainSeeds;
		}
		// the colours (red's blocks are above). Pictures: terracotta 52-62, chiseled 63-73, bricks 74-84, flowers 85-89, dyes 90-101
		for (int k = 0; k < Colours.Length; k++)
		{
			string C = char.ToUpper(Colours[k][0]) + Colours[k][1..];
			B[Dye[k]] = new BlockDef { Name = $"{C} Dye", T = new[] { 90 + k, 90 + k, 90 + k }, Item = true };
			if (k == 0) continue;
			Def(ColourTerracotta[k], $"{C} Terracotta", 51 + k, 51 + k, 51 + k);
			Def(ColourChiseled[k], $"Chiseled {C} Terracotta", 62 + k, 62 + k, 62 + k);
			Def(ColourBricks[k], $"{C} Bricks", 73 + k, 73 + k, 73 + k);
		}
		// flowers: shape "plant" = the picture twice, standing up and crossed like an X
		for (int k = 0; k < Flowers.Length; k++)
		{
			string c = FlowerColours[k];
			var f = Def(Flowers[k], char.ToUpper(c[0]) + c[1..] + " Flower", 85 + k, 85 + k, 85 + k);
			f.Transparent = true; f.NoCollide = true; f.Shape = "plant"; f.Box = new[] { 0.25f, 0, 0.25f, 0.75f, 0.7f, 0.75f }; f.Dye = DyeOf(c);
		}
		// Pieces: a space holding pieces is block 70; which block fills each of its 8 spots is kept separately
		B[Pieces] = new BlockDef { Name = "Pieces", T = new[] { 3, 3, 3 }, Transparent = true, Shape = "pieces" };
		foreach (int b in Splittable) B[FormItem("piece", b)] = new BlockDef { Name = B[b].Name + " Piece", T = B[b].T, Item = true, Piece = b };
		foreach (int b in Splittable)
			foreach (var k in ShapeItem.Keys)
				B[FormItem(k, b)] = new BlockDef { Name = $"{B[b].Name} {shapeName[k]}", T = B[b].T, Item = true, Form = k, Of = b };
	}

	public static bool IsTorch(int id) => Get(id)?.Shape == "torch";
	public static bool IsWater(int id) => id >= Water && id <= Water + 7;
	public static bool IsFurnace(int id) => (id >= Furnace && id < Furnace + 8) || IsFoundry(id);   // a Kiln or the Foundry's Furnace (they work the same way)
	public static int FurnaceBase(int id) => IsFoundry(id) ? Foundry : Furnace;   // its first (unlit, facing 0) id
	public static bool IsItem(int id) => Get(id)?.Item == true;
	public static bool IsTool(int id) => Get(id)?.Tool != null;
	public static bool IsPiece(int id) => Get(id)?.Piece > 0;
	public static string FormOf(int id) => Get(id)?.Form;
	public static int TorchVariant(int wi, int n, int burn = 0) => torchIds.TryGetValue($"{burn}:{wi}:{n}", out var id) ? id : 0;
	public static int TorchMax(int wi) => wi > 0 ? 2 : 4;
	public static int WallIndex(int dx, int dz) { for (int i = 1; i < 5; i++) if (TorchWalls[i][0] == dx && TorchWalls[i][1] == dz) return i; return -1; }
	public static int TorchDrop(int id) => B[id].Burn == 3 ? EverTorch : BurntTorch;
	public static int StackMaxOf(int id) => IsTool(id) || IsGear(id) ? 1 : StackMax;
	// how high the water's surface sits in its block (water with more water on top fills the whole block)
	public static float WaterHeight(int id, int above) => IsWater(above) ? 1 : id == Water ? 0.875f : Math.Max(0.15f, 0.875f * (8 - (id - Water)) / 8f);
	public static string ShapeOfMask(int m) => ShapeMasks.FirstOrDefault(kv => kv.Value.Contains(m)).Key;
	// the block id to actually place for a hotbar block (bricks pick a random variant)
	public static int PlaceId(int id, Random rnd) =>
		Get(id)?.Places > 0 ? B[id].Places : BrickVariants.Contains(id) ? BrickVariants[rnd.Next(4)] : id;
}
