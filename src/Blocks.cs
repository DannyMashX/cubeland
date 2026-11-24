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
	public string Needs, Sound;     // the tool it can't be broken without in survival ("axe", "pickaxe"); its dig, step and place sound (null: "stone")
	public bool Shovel, Leaves, Inventory;   // a shovel is its tool; leaves; you can have it in your inventory (see Items)
	public int Look;                // another look of the block it drops (Red Bricks: 1-3; see LooksOf)
	public int Axis;               // logs: which way it lies (0 upright, 1 along x, 2 along z)
	public int Layers;             // snow: how many layers deep (1-7; a full block is the Snow Block)
	public int Inner, Snowed;      // snow-logged: the torch or flower it holds, and how many layers of snow are round it
	public BlockDef Copy() => (BlockDef)MemberwiseClone();
}

// Every block and item in the game (the same numbers as the web version, so saved worlds line up).
public static partial class Blocks
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
	public static readonly Dictionary<string, int> StrataPickaxe = Nums.N.STRATA_PICKAXE;   // (data/numbers.json)
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
	// Logs lie the way you put them down: on the top or bottom of something they stand upright; on a side they lie pointing
	// out from it. Each kind of log has two lying blocks (360-375): 360 + 2k along x, 361 + 2k along z, k its place in Logs.
	// (They're numbered over 255, so the world's growing puts them in after it's packed: see WorldGen.Late.)
	public static readonly int[] Logs = { Log, StrippedLog, FirLog, StrippedFirLog, PalmLog, StrippedPalmLog, WillowLog, StrippedWillowLog };
	public const int TurnedLogs = 360;
	public static int Turned(int log, int axis) { int k = Array.IndexOf(Logs, log); return k < 0 || axis == 0 ? log : TurnedLogs + 2 * k + axis - 1; }
	public static int LogOf(int b) => b >= TurnedLogs && b < TurnedLogs + 16 ? Logs[(b - TurnedLogs) / 2] : b;   // (the upright log of a lying one)
	// Snow: Snow (380-386) lies in layers, 1 to 7 deep (each an eighth of a block); putting Snow on Snow adds a layer, and an
	// 8th layer makes it a Snow Block (387). Only the 2nd layer up is solid (you sink into the first). It falls off whatever
	// it was lying on if that goes. Breaking it gives you a Snow for each layer. Snowy lands are covered in one layer.
	public const int Snow = 380, SnowBlock = 387;
	public static bool IsSnow(int b) => b >= Snow && b < SnowBlock;
	// Snow-logged blocks: a block that doesn't fill its space can stand in one layer of snow (never more). Each is its own
	// block, the same numbers as the web version: torches (any kind, wall and count) then flowers from 400, one each; 388 a
	// pieces space (slabs, stairs, quarters, pieces: the same 8 spots as 70), 389 the Cutter, 390 a Rock, 391 a Clay Ball,
	// 392 a Brick. Each works like the block it holds (Inner), with a layer of snow drawn round it (Snowed = 1). Breaking it
	// takes the block (or the piece) and leaves the Snow. More Snow on it does nothing.
	public const int SnowLogged1 = 400, SnowyPieces = 388;
	public static readonly int[] SnowLoggedOthers = { Pieces, Cutter, 62, 71, 72 };   // (-> 388-392)
	public static int[] SnowLoggable = Array.Empty<int>();
	static readonly Dictionary<int, int> snowLoggedIds = new();
	public static int SnowIn(int b) => Get(b)?.Snowed ?? 0;                                   // how many layers of snow are round it (0 or 1)
	public static int Plain(int b) => Get(b) is { Inner: > 0 } d ? d.Inner : b;             // the block without its snow
	public static bool CanSnowLog(int b) => snowLoggedIds.ContainsKey(Plain(b));
	public static bool IsPieces(int b) => b == Pieces || b == SnowyPieces;                    // a space holding pieces (in snow or not)
	// b (or what b holds) in a layer of snow (n >= 1), or without it (n = 0). Anything that can't be snow-logged stays as it is.
	public static int WithSnow(int b, int n) { int p = Plain(b); return n <= 0 ? p : snowLoggedIds.TryGetValue(p, out var id) ? id : b; }
	// Snow breaks into Snowballs: 2-4 for each layer (a Snow Block: 4)
	public const int Snowball = 706;
	public static int SnowballsFor(int layers, Random r) { int n = 0; for (int k = 0; k < layers; k++) n += 2 + r.Next(3); return n; }
	public static bool IsLeaves(int b) => Get(b)?.Leaves == true;
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
	// Deep ores (worlds from Land 2): each ore in stone layers 2-8 (470-497: 470 + 7 x its place in Ores + layer - 2), in that
	// layer's stone colours. Same pickaxe needed as the ore itself; it gives as many Chunks as its layer.
	public const int DeepOre = 470;
	public static int DeepOreOf(int k, int layer) => layer <= 1 ? Ores[k] : DeepOre + 7 * k + layer - 2;
	public static int OreIndex(int b) { int k = Array.IndexOf(Ores, b); return k >= 0 ? k : b >= DeepOre && b < DeepOre + 28 ? (b - DeepOre) / 7 : -1; }
	public static readonly int[] DeepOres = Enumerable.Range(DeepOre, 28).ToArray();
	// ---- Stage breaking (survival): Stone, ores and logs break a few stages with each swing, and only come out at the last ----
	public static bool IsLogBlock(int b) => Array.IndexOf(Logs, b) >= 0 || (b >= TurnedLogs && b < TurnedLogs + 16);
	// how many stages block b has as the world grew it (0: it breaks at once, as always): Stone and ores 4 x their layer,
	// Sandstone and Terracotta 4, logs 8. Blocks a player put down (or changed) always break at once, the old way.
	public static int Stages(int b)
	{
		if (b == 3 || (b >= Strata && b < Bedrock)) return Nums.N.STONE_STAGES * StoneLayer(b);
		if (OreIndex(b) >= 0) return Nums.N.STONE_STAGES * Math.Max(1, B[b].Layer);
		if (b == Sandstone || b == 65 || Array.IndexOf(ColourTerracotta, b) >= 0) return Nums.N.STONE_STAGES;   // (Sandstone and every Terracotta)
		return IsLogBlock(b) ? Nums.N.LOG_STAGES : 0;
	}
	// stages broken by one swing of tool t (0 = your hand) at block b
	public static int StagesPerSwing(int b, int t)
	{
		var d = Get(t);
		var ax = Nums.N.AXE_STAGES;   // (data/numbers.json: by the axe's material; "none" by hand or with anything else)
		if (IsLogBlock(b)) return d?.Tool != "axe" || !ax.TryGetValue(d.Material ?? "", out int s) ? ax["none"] : s;
		if (d?.Tool != "pickaxe") return 0;
		return Nums.N.PICK_STAGES.TryGetValue(d.Material ?? "", out int p) ? p : 1;
	}
	public static readonly Dictionary<string, int> MetalBrick = new() { ["copper"] = 634, ["iron"] = 635, ["gold"] = 636, ["tin"] = 637, ["bronite"] = 638, ["rosium"] = 639, ["steel"] = SteelBrick };
	public static readonly Dictionary<string, int> PickTier = Nums.N.PICK_TIER;   // how good each pickaxe is
	public static readonly int[] FireclayBlocks = { FireclayTerracotta, FireclayBricks, FireclayCasing, ChiseledFireclay };
	public static readonly int[] FoundryIds = Enumerable.Range(Foundry, 8).ToArray();
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
	public static readonly int[][] FurnaceFronts = { new[] { 0, 1 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { -1, 0 } };
	public static readonly int[][] TorchWalls = { null, new[] { -1, 0 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { 0, 1 } };
	static readonly Dictionary<string, int> torchIds = new();
	public static readonly List<int> Tools = new();
	// ---- gear (Pack & Plate): armor, backpacks and utility belts, in six tiers ----
	public static readonly string[] GearTiers = { "leather", "copper", "iron", "bronite", "rosium", "steel" };
	public static readonly string[] ArmorPieces = { "helmet", "chestplate", "leggings", "boots" };
	public static int ArmorItem(int t, int k) => 669 + t * 4 + k;   // 669-692
	public static readonly int[] Backpacks = { 693, 694, 695, 696, 697, 698 }, Belts = { 699, 700, 701, 702, 703, 704 };
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
	// set from data/blocks.json in the constructor: every block you can cut (FORMS' blocks, in order), and every block you can have
	// in your inventory (the blocks with inventory, not snow-logged, and in layer 1 unless tall worlds are on; by id). Which tool a
	// block needs, its sound, leaves and the shovel's blocks are its fields (Needs, Sound, Leaves, Shovel).
	public static readonly int[] Splittable, Items;
	// every item row (the creative inventory has them all, with Items and each kind of torch)
	public static readonly int[] ItemRows;
	public static readonly int StackMax = Nums.N.STACK_MAX;   // (data/numbers.json)

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
		// data/blocks.json's rows, first (the ones made below use them): BASE, written out one by one, then the families whose
		// every member is written out (the Kiln, torches, water, lying logs, snow, tools, the deep stone sets, soils, Grain, the
		// colours, metals, the deep ores, the Foundry, gear, flowers)
		if (Nums.Blocks.BASE == null) throw new InvalidOperationException("data/blocks.json has no BASE");
		var rows = RowBlocks(AllRows(Nums.Blocks), P, out var keys);
		foreach (var (r, b) in rows)
		{
			if (r.id < 1 || r.id >= MaxId) throw new InvalidOperationException($"data/blocks.json: block {r.id} ({r.name}) isn't 1-{MaxId - 1}");
			if (B[r.id] != null) throw new InvalidOperationException($"data/blocks.json: block {r.id} ({r.name}) is there twice");
			B[r.id] = b;
		}
		// Torches (rows: lit = 22-41, smoldering = 81-92, burnt = 170-181, ever = 182-193), found by (burn, wall, count)
		foreach (var r in Nums.Blocks.TORCHES ?? Array.Empty<BlockRow>()) torchIds[$"{B[r.id].Burn}:{B[r.id].Wi}:{B[r.id].Count}"] = r.id;
		// the tools (rows: stone 207-211; copper, iron, bronite, rosium, steel from 644), in the rows' order
		Tools.AddRange((Nums.Blocks.TOOLS ?? Array.Empty<BlockRow>()).Select(r => r.id));
		// the gear (rows): the armor in the rows' order, then the backpacks and the belts
		GearItems.AddRange((Nums.Blocks.GEAR ?? Array.Empty<BlockRow>()).Where(r => r.armor != null).Select(r => r.id));
		GearItems.AddRange(Backpacks); GearItems.AddRange(Belts);
		// piece and shape items: data/blocks.json's FORMS (each block you can cut, with its items' ids), named with FORM_NAMES
		// (Pieces, block 70, is a row: which block fills each of its 8 spots is kept separately)
		var formNames = Nums.Blocks.FORM_NAMES ?? throw new InvalidOperationException("data/blocks.json has no FORM_NAMES");
		var forms = Nums.Blocks.FORMS ?? Array.Empty<FormRow>();
		Splittable = forms.Select(f => KeyRef(keys, f.of, "a FORMS row")).ToArray();
		for (int k = 0; k < forms.Length; k++)
		{
			var f = forms[k]; int of = Splittable[k];
			foreach (var (id, d) in FormItems(f, of, B[of], formNames))
			{
				if (id < 1 || id >= MaxId) throw new InvalidOperationException($"data/blocks.json: {d.Name} is {id}, which isn't 1-{MaxId - 1}");
				if (B[id] != null) throw new InvalidOperationException($"data/blocks.json: {d.Name} is {id}, which is {B[id].Name} too");
				B[id] = d;
			}
		}
		// snow-logged: a torch, flower or other small thing with snow lying round it (data/blocks.json's SNOWY; see SnowLogged): its
		// inner block, then the row's own drop and box, with Inner and Snowed 1
		var snowy = Nums.Blocks.SNOWY ?? Array.Empty<SnowyRow>();
		SnowLoggable = new int[snowy.Length];
		for (int k = 0; k < snowy.Length; k++)
		{
			var s = snowy[k];
			int inner = KeyRef(keys, s.inner, $"snow-logged block {s.id}");
			if (s.id < 1 || s.id >= MaxId) throw new InvalidOperationException($"data/blocks.json: snow-logged block {s.id} isn't 1-{MaxId - 1}");
			if (B[s.id] != null) throw new InvalidOperationException($"data/blocks.json: snow-logged block {s.id} is {B[s.id].Name} too");
			if (B[inner] == null) throw new InvalidOperationException($"data/blocks.json: snow-logged block {s.id} holds \"{s.inner}\", which isn't made yet");
			B[s.id] = SnowyBlock(s, inner, B[inner], keys);
			SnowLoggable[k] = inner;
			snowLoggedIds[inner] = s.id;
		}
		// the lists the blocks' fields make: the item rows, Items, and each block's looks (itself, then the rows with a look that
		// drop it, by look)
		ItemRows = rows.Where(x => x.r.item).Select(x => x.r.id).ToArray();
		Items = Enumerable.Range(1, MaxId - 1).Where(i => B[i] is { Inventory: true, Snowed: 0 } d && (d.Layer <= 1 || World.TallWorlds)).ToArray();
		foreach (var g in rows.Where(x => x.b.Look > 0).GroupBy(x => x.b.Drop))
			looks[g.Key] = g.OrderBy(x => x.b.Look).Select(x => x.r.id).Prepend(g.Key).ToArray();
	}
	static readonly Dictionary<int, int[]> looks = new();

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
	// a block's looks: what putting it down picks from at random (Red Bricks: 12-15, from 12 or any of its looks; anything else:
	// just itself)
	public static int[] LooksOf(int id) => looks.TryGetValue(Get(id) is { Look: > 0 } d ? d.Drop : id, out var l) ? l : new[] { id };
	// the block id to actually place for a hotbar block (bricks pick a random look)
	public static int PlaceId(int id, Random rnd) =>
		Get(id)?.Places > 0 ? B[id].Places : LooksOf(id) is { Length: > 1 } looks ? looks[rnd.Next(looks.Length)] : id;
}
