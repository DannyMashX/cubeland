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
	public const int MaxId = 2200;
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
		int i = Array.IndexOf(DeepBlocks, b), e = Array.IndexOf(ExtraFormBlocks, b);
		return i >= 0 ? DeepFormItem + 7 * i + Array.IndexOf(Forms, form) : e >= 0 ? ExtraFormItem + 7 * e + Array.IndexOf(Forms, form)
			: (form == "piece" ? PieceItem : ShapeItem[form]) + b;
	}
	// Blocks numbered where 100 + b and 1000 + b would clash too (the desert and swamp blocks, and Sandstone): their piece, slab, stairs...
	// items are 2000 + 7 x (the block's place in this list) + the form, like the deep stone sets. (Only ever add to the end of this list.)
	public const int ExtraFormItem = 2000;
	public static readonly int[] ExtraFormBlocks = { Sandstone, PalmLog, PalmLeaves, PalmPlanks, StrippedPalmLog, WillowLog, WillowLeaves, WillowPlanks, StrippedWillowLog,
		SandstoneBricks, ChiseledSandstone, SmoothSandstone, Mud, MudBricks, Peat };
	public static int StoneLayer(int b) => b == 3 ? 1 : Get(b)?.Layer ?? 0;
	// the deepest layer each pickaxe can mine, by what it's made of (copper and up are still to come)
	public static readonly Dictionary<string, int> StrataPickaxe = new() { ["stone"] = 2, ["copper"] = 3, ["iron"] = 4, ["bronite"] = 6, ["rosium"] = 7, ["steel"] = 8 };
	public static readonly BlockDef[] B = new BlockDef[MaxId];
	public static BlockDef Get(int id) => id > 0 && id < MaxId ? B[id] : null;

	public const int Grass = 1, Dirt = 2, Stone = 3, Log = 4, Leaves = 5, Sand = 6, Planks = 7, Cobblestone = 8,
		StoneBricks = 9, Glass = 10, Gravel = 11, CraftingBench = 60, Cutter = 159, StrippedLog = 61, RockBlock = 62, Clay = 63,
		BricksBlock = 64, Terracotta = 65, Pieces = 70, ClayBallBlock = 71, BrickBlock = 72, Furnace = 73;
	public const int Torch = 22, SmolderingTorch = 81, BurntTorch = 170, EverTorch = 182, Water = 50;
	// Biomes (gen 3 worlds): Sandstone under desert sand, Snowy Grass in snowy lands, Ice on their frozen water, Swamp Grass in swamps
	public const int Sandstone = 97, SnowyGrass = 98, Ice = 99, SwampGrass = 58;
	public static bool IsGrassy(int b) => b == Grass || b == SnowyGrass || b == SwampGrass;   // grass of any biome (dirt underneath; flowers and hoes)
	public static bool TakesBiomeTint(int b) => b == Grass || b == SwampGrass || IsLeaves(b);   // coloured by the climate where they are (Biome worlds)
	// Fir: the snowy biome's wood (Oak is the original wood: Log, Leaves, Planks and Stripped Log above)
	public const int FirLog = 251, FirLeaves = 252, FirPlanks = 253, StrippedFirLog = 254;
	// Palm: the desert's trees (their leaves sometimes drop Dates). Willow: the swamp's trees
	public const int PalmLog = 16, PalmLeaves = 17, PalmPlanks = 18, StrippedPalmLog = 19;
	public const int WillowLog = 20, WillowLeaves = 21, WillowPlanks = 59, StrippedWillowLog = 160;
	// the desert's building stone (made from Sandstone), and the swamp's ground: Mud (fired into Mud Bricks) and Peat (a long-burning fuel)
	public const int SandstoneBricks = 162, ChiseledSandstone = 199, SmoothSandstone = 202, Mud = 203, MudBricks = 204, Peat = 205;
	public const int Dates = 705;   // food, from Palm Leaves
	public static bool IsLeaves(int b) => b == Leaves || b == FirLeaves || b == PalmLeaves || b == WillowLeaves;
	// Fir, Palm and Willow Leaves use the Oak Leaves picture, with their own colour on top of the climate colour
	// (Fir: darker blue-green; Palm: brighter yellow-green; Willow: a softer olive green). null: none (Oak Leaves)
	public static float[] LeafTint(int b) => b == FirLeaves ? FirLeafTint : b == PalmLeaves ? PalmLeafTint : b == WillowLeaves ? WillowLeafTint : null;
	static readonly float[] FirLeafTint = { 0.78f, 0.85f, 1.05f }, PalmLeafTint = { 1.06f, 1.1f, 0.72f }, WillowLeafTint = { 0.92f, 1f, 0.78f };
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
	public static readonly int[] Splittable = new[] { 1, 2, 3, 8, 7, 4, 61, 5, FirPlanks, FirLog, StrippedFirLog, FirLeaves, 6, 9, 12, 10, 11, 63, 64, 65, 66, 67, 68, 69 }.Concat(NewColourBlocks).Concat(DeepBlocks).Concat(ExtraFormBlocks).ToArray();
	public static readonly int[] NeedsAxe = { 4, 7, 61, FirLog, FirPlanks, StrippedFirLog, PalmLog, PalmPlanks, StrippedPalmLog, WillowLog, WillowPlanks, StrippedWillowLog },
		Diggable = { 1, 2, 6, 11, 63, Fireclay, 93, 94, 95, 96, Mud, Peat };
	public static readonly int[] NeedsPickaxe = new[] { 3, 8, 9, 12, 13, 14, 15, 64, 65, 66, 67, 68, 69, 73, 74, 75, 76, 77, 78, 79, 80 }.Concat(NewColourBlocks).Concat(DeepBlocks)
		.Concat(Ores).Concat(FireclayBlocks).Concat(FoundryIds).Concat(new[] { Sandstone, SandstoneBricks, ChiseledSandstone, SmoothSandstone, MudBricks }).ToArray();
	// Every block you can pick from the creative inventory
	public static readonly int[] Items = new[] { new[] { 1, 2, 3 }, DeepShown(3), new[] { 8 }, DeepShown(8), new[] { 7, 60, Cutter, 4, 61, 5, FirPlanks, FirLog, StrippedFirLog, FirLeaves, 6, 9 }, DeepShown(9),
		new[] { 12, 10, 11, 63, 64, 65, 66, 67, 68, 69 }, DeepShown(69) }.SelectMany(a => a).Concat(NewColourBlocks).Concat(Flowers)
		.Concat(new[] { Furnace, 22, Water, Bedrock }).Concat(Ores).Append(Fireclay).Concat(FireclayBlocks).Append(Foundry)
		.Concat(new[] { PalmPlanks, PalmLog, StrippedPalmLog, PalmLeaves, WillowPlanks, WillowLog, StrippedWillowLog, WillowLeaves, SandstoneBricks, ChiseledSandstone, SmoothSandstone, Mud, MudBricks, Peat }).ToArray();
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

	// pictures are named by their file (textures/blocks or textures/items, without .png): side, top, bottom
	static BlockDef Def(int id, string name, string side, string top, string bottom)
	{
		var b = new BlockDef { Name = name, T = P(side, top, bottom) };
		B[id] = b;
		return b;
	}

	// the atlas places of some pictures (named by their file)
	static int[] P(params string[] names) => Atlas.Tiles(names);
	static int[] P3(string name) => Atlas.Tiles(name, name, name);   // one picture on every side

	static Blocks()
	{
		Def(1, "Grass", "grass_side", "grass", "dirt").Drop = 2;   // digging up Grass gives Dirt
		Def(2, "Dirt", "dirt", "dirt", "dirt");
		Def(3, "Stone", "stone", "stone", "stone").Drop = 8;   // mining Stone gives Cobblestone
		for (int n = 2; n <= StrataLayers; n++)   // the deep stone sets (pictures stone_layer_2, cobblestone_layer_2, ... up to _8)
		{
			int k = n - 2;
			var d = Def(Strata + k, "Stone", $"stone_layer_{n}", $"stone_layer_{n}", $"stone_layer_{n}"); d.Drop = DeepCobble + k; d.Layer = n;
			Def(DeepCobble + k, "Cobblestone", $"cobblestone_layer_{n}", $"cobblestone_layer_{n}", $"cobblestone_layer_{n}").Layer = n;
			Def(DeepBricks + k, "Stone Bricks", $"stone_bricks_layer_{n}", $"stone_bricks_layer_{n}", $"stone_bricks_layer_{n}").Layer = n;
			Def(DeepChiseled + k, "Chiseled Stone", $"chiseled_stone_layer_{n}", $"chiseled_stone_layer_{n}", $"chiseled_stone_layer_{n}").Layer = n;
		}
		Def(Bedrock, "Bedrock", "bedrock", "bedrock", "bedrock");
		Def(4, "Oak Log", "log_side", "log_top", "log_top");
		Def(5, "Oak Leaves", "leaves", "leaves", "leaves").Transparent = true;
		Def(6, "Sand", "sand", "sand", "sand");
		Def(7, "Oak Planks", "planks", "planks", "planks");
		Def(8, "Cobblestone", "cobblestone", "cobblestone", "cobblestone");
		Def(9, "Stone Bricks", "stone_bricks", "stone_bricks", "stone_bricks");
		Def(10, "Glass", "glass", "glass", "glass").Transparent = true;
		Def(11, "Gravel", "gravel", "gravel", "gravel");
		for (int k = 0; k < 4; k++) { string look = "bricks_" + "abcd"[k]; Def(12 + k, "Red Bricks", look, look, look); }   // 4 looks, picked at random when placed (each is red_bricks until you make its own picture)
		Def(60, "Crafting Bench", "crafting_bench_side", "crafting_bench_top", "planks");
		// the Cutter: cuts blocks into pieces, slabs and stairs. Half a block tall, with a saw blade standing up out of it
		// (pictures: cutter_base on the sides, the stripped log top on top, cutter_bottom underneath, cutter_saw for the blade)
		var cutter = Def(Cutter, "Cutter", "cutter_base", "stripped_log_top", "cutter_bottom"); cutter.Shape = "half"; cutter.Transparent = true; cutter.Box = new[] { 0f, 0, 0, 1, 0.5f, 1 };
		Def(Sandstone, "Sandstone", "sandstone", "sandstone", "sandstone");
		Def(SnowyGrass, "Snowy Grass", "snowy_grass_side", "snow", "dirt").Drop = 2;   // (digging up any grass gives Dirt)
		Def(Ice, "Ice", "ice", "ice", "ice").Transparent = true;
		Def(SwampGrass, "Swamp Grass", "swamp_grass_side", "swamp_grass", "dirt").Drop = 2;
		Def(61, "Stripped Oak Log", "stripped_log_side", "stripped_log_top", "stripped_log_top");
		// Fir (the snowy biome's trees)
		Def(FirLog, "Fir Log", "fir_log_side", "fir_log_top", "fir_log_top");
		Def(FirLeaves, "Fir Leaves", "leaves", "leaves", "leaves").Transparent = true;   // (the Oak Leaves picture, tinted: see FirLeafTint)
		Def(FirPlanks, "Fir Planks", "fir_planks", "fir_planks", "fir_planks");
		Def(StrippedFirLog, "Stripped Fir Log", "stripped_fir_log_side", "stripped_fir_log_top", "stripped_fir_log_top");
		// Palm (the desert's trees) and Willow (the swamp's)
		Def(PalmLog, "Palm Log", "palm_log_side", "palm_log_top", "palm_log_top");
		Def(PalmLeaves, "Palm Leaves", "leaves", "leaves", "leaves").Transparent = true;   // (the Oak Leaves picture, tinted: see LeafTint)
		Def(PalmPlanks, "Palm Planks", "palm_planks", "palm_planks", "palm_planks");
		Def(StrippedPalmLog, "Stripped Palm Log", "stripped_palm_log_side", "stripped_palm_log_top", "stripped_palm_log_top");
		Def(WillowLog, "Willow Log", "willow_log_side", "willow_log_top", "willow_log_top");
		Def(WillowLeaves, "Willow Leaves", "leaves", "leaves", "leaves").Transparent = true;
		Def(WillowPlanks, "Willow Planks", "willow_planks", "willow_planks", "willow_planks");
		Def(StrippedWillowLog, "Stripped Willow Log", "stripped_willow_log_side", "stripped_willow_log_top", "stripped_willow_log_top");
		// the desert's building stone and the swamp's ground
		Def(SandstoneBricks, "Sandstone Bricks", "sandstone_bricks", "sandstone_bricks", "sandstone_bricks");
		Def(ChiseledSandstone, "Chiseled Sandstone", "chiseled_sandstone", "chiseled_sandstone", "chiseled_sandstone");
		Def(SmoothSandstone, "Smooth Sandstone", "smooth_sandstone", "smooth_sandstone", "smooth_sandstone");
		Def(Mud, "Mud", "mud", "mud", "mud");
		Def(MudBricks, "Mud Bricks", "mud_bricks", "mud_bricks", "mud_bricks");
		Def(Peat, "Peat", "peat", "peat", "peat");
		B[Dates] = new BlockDef { Name = "Dates", T = P3("dates"), Item = true, Food = 3 };
		var rock = Def(62, "Rock", "rock", "rock", "rock"); rock.Transparent = true; rock.NoCollide = true; rock.Shape = "flat"; rock.Box = new[] { 0.1f, 0, 0.1f, 0.9f, 0.12f, 0.9f };
		Def(63, "Clay", "clay", "clay", "clay");
		Def(64, "Bricks", "bricks", "bricks", "bricks");
		Def(65, "Terracotta", "terracotta", "terracotta", "terracotta");
		Def(66, "Red Terracotta", "red_terracotta", "red_terracotta", "red_terracotta");
		Def(67, "Chiseled Terracotta", "chiseled_terracotta", "chiseled_terracotta", "chiseled_terracotta");
		Def(68, "Chiseled Red Terracotta", "chiseled_red_terracotta", "chiseled_red_terracotta", "chiseled_red_terracotta");
		Def(69, "Chiseled Stone", "chiseled_stone", "chiseled_stone", "chiseled_stone");
		var cb = Def(71, "Clay Ball", "clay_ball", "clay_ball", "clay_ball"); cb.Transparent = true; cb.NoCollide = true; cb.Shape = "flat"; cb.Box = new[] { 0.15f, 0, 0.15f, 0.85f, 0.12f, 0.85f };
		var br = Def(72, "Brick", "brick", "brick", "brick"); br.Transparent = true; br.NoCollide = true; br.Shape = "flat"; br.Box = new[] { 0.1f, 0, 0.1f, 0.9f, 0.12f, 0.9f };
		// Kiln: 73-76 unlit, 77-80 lit; each faces one way. t = [side, top (Bricks), bottom, front]
		for (int k = 0; k < 4; k++)
		{
			B[Furnace + k] = new BlockDef { Name = "Kiln", T = P("kiln_side", "bricks", "kiln_bottom", "kiln_front"), Front = FurnaceFronts[k], Facing = k, Drop = Furnace };
			B[Furnace + 4 + k] = new BlockDef { Name = "Kiln", T = P("kiln_side", "bricks", "kiln_bottom", "kiln_front_on"), Front = FurnaceFronts[k], Facing = k, Lit = true, Drop = Furnace };
		}
		B[Furnace].Icon = Atlas.Tile("kiln_front");
		// Torches: (wall, count) combos. Lit = 22-41, smoldering = 81-92, burnt = 170-181, ever = 182-193
		string[] tName = { "Torch", "Smoldering Torch", "Burnt Torch", "Ever Torch" };
		int[][] tTiles = { P("torch_side", "torch_top", "torch_bottom"), P("smoldering_torch_side", "smoldering_torch_top", "torch_bottom"),
			P("burnt_torch_side", "burnt_torch_top", "torch_bottom"), P("torch_side", "torch_top", "torch_bottom") };
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
		for (int l = 0; l <= 7; l++) B[Water + l] = new BlockDef { Name = "Water", T = P3("water"), Transparent = true, NoCollide = true, Liquid = true, Level = l };
		// Items (they share the block list so icons and names work the same way)
		B[Chocolate] = new BlockDef { Name = "Chocolate", T = P3("chocolate"), Item = true, Food = 4 };
		B[Stick] = new BlockDef { Name = "Stick", T = P3("stick"), Item = true };
		string[] kinds = { "Axe", "Pickaxe", "Shovel", "Sword", "Hoe" };
		// stone tools 207-211; metal tools (Pack & Plate) 644-668: copper, iron, bronite, rosium, steel, 5 each in the same order (pictures <metal>_axe, ...)
		foreach (var (key, label, sword, firstId) in new[] { ("stone", "Stone", 3, 207), ("copper", "Copper", 4, 644), ("iron", "Iron", 5, 649),
			("bronite", "Bronite", 6, 654), ("rosium", "Rosium", 7, 659), ("steel", "Steel", 8, 664) })
			for (int k = 0; k < 5; k++)
			{
				int id = firstId + k;
				B[id] = new BlockDef { Name = label + " " + kinds[k], T = P3($"{key}_{kinds[k].ToLower()}"), Item = true, Tool = kinds[k].ToLower(), Material = key,
					Damage = kinds[k] == "Sword" ? sword : kinds[k] == "Axe" ? sword - 1 : 1 };
				Tools.Add(id);
			}
		B[Rock] = new BlockDef { Name = "Rock", T = P3("rock"), Item = true, Places = RockBlock };
		B[RockBlock].Drop = Rock;
		B[CrudeAxe] = new BlockDef { Name = "Crude Axe", T = P3("crude_axe"), Item = true, Tool = "axe", Material = "crude", Damage = 1 };
		B[ClayBall] = new BlockDef { Name = "Clay Ball", T = P3("clay_ball"), Item = true, Places = ClayBallBlock };
		B[Brick] = new BlockDef { Name = "Brick", T = P3("brick"), Item = true };
		B[FireEssence] = new BlockDef { Name = "Fire Essence", T = P3("fire_essence"), Item = true };
		B[ClayBallBlock].Drop = ClayBall;
		B[BrickBlock].Drop = Brick;
		// Fear & Foes: Skeletons drop Bones (Kiln -> Bone Ash -> White Dye); a Gloomling beaten in the dark leaves a Gloom Wisp (-> Black Dye)
		B[Bone] = new BlockDef { Name = "Bone", T = P3("bone"), Item = true };
		B[BoneAsh] = new BlockDef { Name = "Bone Ash", T = P3("bone_ash"), Item = true };
		B[GloomWisp] = new BlockDef { Name = "Gloom Wisp", T = P3("gloom_wisp"), Item = true };
		// Till & Tend: Grain Seeds, Grain (-> Flour by hand -> Bread in the Kiln), tilled soil, and Grain growing in 8 stages
		B[GrainSeeds] = new BlockDef { Name = "Grain Seeds", T = P3("grain_seeds"), Item = true };
		B[Grain] = new BlockDef { Name = "Grain", T = P3("grain"), Item = true };
		B[Flour] = new BlockDef { Name = "Flour", T = P3("flour"), Item = true };
		B[Bread] = new BlockDef { Name = "Bread", T = P3("bread"), Item = true, Food = 6 };
		// cows: Leather (more from a cow with room to roam), Raw Meat (more from a well-fed cow) -> Cooked Meat in the Kiln
		B[Leather] = new BlockDef { Name = "Leather", T = P3("leather"), Item = true };
		B[RawMeat] = new BlockDef { Name = "Raw Meat", T = P3("raw_meat"), Item = true, Food = 2 };
		B[CookedMeat] = new BlockDef { Name = "Cooked Meat", T = P3("cooked_meat"), Item = true, Food = 8 };
		for (int k = 0; k < 4; k++)
		{
			string m = Metals[k], M = char.ToUpper(m[0]) + m[1..];
			Def(Ores[k], M + " Ore", m + "_ore", m + "_ore", m + "_ore").Drop = Chunk[m];
			B[Ores[k]].Pick = new[] { 1, 2, 3, 3 }[k];
			B[Chunk[m]] = new BlockDef { Name = M + " Chunk", T = P3(m + "_chunk"), Item = true };
			B[MetalBrick[m]] = new BlockDef { Name = M + " Brick", T = P3(m + "_brick"), Item = true };
		}
		B[MetalBrick["bronite"]] = new BlockDef { Name = "Bronite Brick", T = P3("bronite_brick"), Item = true };
		B[MetalBrick["rosium"]] = new BlockDef { Name = "Rosium Brick", T = P3("rosium_brick"), Item = true };
		B[SteelScrap] = new BlockDef { Name = "Steel Scrap", T = P3("steel_scrap"), Item = true };
		B[SteelBrick] = new BlockDef { Name = "Steel Brick", T = P3("steel_brick"), Item = true };
		B[FireclayBall] = new BlockDef { Name = "Fireclay Ball", T = P3("fireclay_ball"), Item = true };
		B[FireclayBrick] = new BlockDef { Name = "Fireclay Brick", T = P3("fireclay_brick"), Item = true };
		// gear: armor pictures <tier>_helmet, _chestplate, _leggings, _boots; <tier>_backpack; <tier>_utility_belt
		for (int t = 0; t < GearTiers.Length; t++)
		{
			string m = GearTiers[t], M = char.ToUpper(m[0]) + m[1..];
			for (int k = 0; k < 4; k++)
			{
				B[ArmorItem(t, k)] = new BlockDef { Name = $"{M} {char.ToUpper(ArmorPieces[k][0]) + ArmorPieces[k][1..]}", T = P3($"{m}_{ArmorPieces[k]}"), Item = true, Armor = k, Material = m };
				GearItems.Add(ArmorItem(t, k));
			}
		}
		for (int t = 0; t < GearTiers.Length; t++)
		{
			string m = GearTiers[t], M = char.ToUpper(m[0]) + m[1..];
			B[Backpacks[t]] = new BlockDef { Name = M + " Backpack", T = P3(m + "_backpack"), Item = true, Pack = PackSlots[t], Material = m };
			B[Belts[t]] = new BlockDef { Name = M + " Utility Belt", T = P3(m + "_utility_belt"), Item = true, Belt = BeltSlots[t], BeltTools = t + 2, Material = m };
		}
		GearItems.AddRange(Backpacks); GearItems.AddRange(Belts);
		Def(Fireclay, "Fireclay", "fireclay", "fireclay", "fireclay").HandDrop = new[] { FireclayBall, 1, 4 };
		Def(FireclayTerracotta, "Fireclay Terracotta", "fireclay_terracotta", "fireclay_terracotta", "fireclay_terracotta");
		Def(FireclayBricks, "Fireclay Bricks", "fireclay_bricks", "fireclay_bricks", "fireclay_bricks");
		Def(FireclayCasing, "Fireclay Casing", "fireclay_casing", "fireclay_casing", "fireclay_casing");
		Def(ChiseledFireclay, "Chiseled Fireclay", "chiseled_fireclay", "chiseled_fireclay", "chiseled_fireclay");
		for (int k = 0; k < 4; k++)
		{
			B[Foundry + k] = new BlockDef { Name = "Furnace", T = P("fireclay_casing", "fireclay_bricks", "furnace_bottom", "furnace_front"), Front = FurnaceFronts[k], Facing = k, Drop = Foundry };
			B[Foundry + 4 + k] = new BlockDef { Name = "Furnace", T = P("fireclay_casing", "fireclay_bricks", "furnace_bottom", "furnace_front_on"), Front = FurnaceFronts[k], Facing = k, Lit = true, Drop = Foundry };
		}
		B[Foundry].Icon = Atlas.Tile("furnace_front");
		string[] soilNames = { "Tilled Dirt", "Wet Tilled Dirt", "Fertilized Dirt", "Wet Fertilized Dirt" };
		for (int k = 0; k < 4; k++) Def(Tilled + k, soilNames[k], "dirt", soilNames[k].ToLower().Replace(' ', '_'), "dirt").Drop = Dirt;
		for (int k = 0; k < 8; k++)
		{
			var g = Def(GrainStages[k], "Grain", $"grain_stage_{k}", $"grain_stage_{k}", $"grain_stage_{k}");
			g.Transparent = true; g.NoCollide = true; g.Shape = "plant"; g.Box = new[] { 0.15f, 0, 0.15f, 0.85f, 0.15f + k * 0.12f, 0.85f };
			g.Drop = k == 7 ? Grain : GrainSeeds;
		}
		// the colours (red's blocks are above). Pictures: <colour>_terracotta, <colour>_chiseled_terracotta, <colour>_bricks, <colour>_dye, <colour>_flower
		for (int k = 0; k < Colours.Length; k++)
		{
			string C = char.ToUpper(Colours[k][0]) + Colours[k][1..];
			B[Dye[k]] = new BlockDef { Name = $"{C} Dye", T = P3(Colours[k] + "_dye"), Item = true };
			if (k == 0) continue;
			Def(ColourTerracotta[k], $"{C} Terracotta", Colours[k] + "_terracotta", Colours[k] + "_terracotta", Colours[k] + "_terracotta");
			Def(ColourChiseled[k], $"Chiseled {C} Terracotta", Colours[k] + "_chiseled_terracotta", Colours[k] + "_chiseled_terracotta", Colours[k] + "_chiseled_terracotta");
			Def(ColourBricks[k], $"{C} Bricks", Colours[k] + "_bricks", Colours[k] + "_bricks", Colours[k] + "_bricks");
		}
		// flowers: shape "plant" = the picture twice, standing up and crossed like an X
		for (int k = 0; k < Flowers.Length; k++)
		{
			string c = FlowerColours[k];
			var f = Def(Flowers[k], char.ToUpper(c[0]) + c[1..] + " Flower", c + "_flower", c + "_flower", c + "_flower");
			f.Transparent = true; f.NoCollide = true; f.Shape = "plant"; f.Box = new[] { 0.25f, 0, 0.25f, 0.75f, 0.7f, 0.75f }; f.Dye = DyeOf(c);
		}
		// Pieces: a space holding pieces is block 70; which block fills each of its 8 spots is kept separately
		B[Pieces] = new BlockDef { Name = "Pieces", T = P3("stone"), Transparent = true, Shape = "pieces" };
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
