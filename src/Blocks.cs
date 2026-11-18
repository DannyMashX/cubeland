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
	public string Shape;            // null (a cube), "torch", "flat", "plant", "pieces"
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
}

// Every block and item in the game (the same numbers as the web version, so saved worlds line up).
public static class Blocks
{
	public const int MaxId = 1800;
	public static readonly BlockDef[] B = new BlockDef[MaxId];
	public static BlockDef Get(int id) => id > 0 && id < MaxId ? B[id] : null;

	public const int Grass = 1, Dirt = 2, Stone = 3, Log = 4, Leaves = 5, Sand = 6, Planks = 7, Cobblestone = 8,
		StoneBricks = 9, Glass = 10, Gravel = 11, CraftingBench = 60, StrippedLog = 61, RockBlock = 62, Clay = 63,
		BricksBlock = 64, Terracotta = 65, Pieces = 70, ClayBallBlock = 71, BrickBlock = 72, Furnace = 73;
	public const int Torch = 22, SmolderingTorch = 81, BurntTorch = 170, EverTorch = 182, Water = 50;
	public const int Chocolate = 200, Stick = 201, Rock = 212, CrudeAxe = 213, ClayBall = 215, Brick = 216, FireEssence = 217;
	public const int Bone = 620, BoneAsh = 621, GloomWisp = 622;   // Fear & Foes (items 620+; 600-611 are the dyes)
	public const int PieceItem = 100;
	public static readonly int[] BrickVariants = { 12, 13, 14, 15 };
	public static readonly int[][] FurnaceFronts = { new[] { 0, 1 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { -1, 0 } };
	public static readonly int[][] TorchWalls = { null, new[] { -1, 0 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { 0, 1 } };
	static readonly Dictionary<string, int> torchIds = new();
	public static readonly List<int> Tools = new();
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
	public static readonly int[] Splittable = new[] { 1, 2, 3, 8, 7, 4, 61, 5, 6, 9, 12, 10, 11, 63, 64, 65, 66, 67, 68, 69 }.Concat(NewColourBlocks).ToArray();
	public static readonly int[] NeedsAxe = { 4, 7, 61 }, Diggable = { 1, 2, 6, 11, 63 };
	public static readonly int[] NeedsPickaxe = new[] { 3, 8, 9, 12, 13, 14, 15, 64, 65, 66, 67, 68, 69, 73, 74, 75, 76, 77, 78, 79, 80 }.Concat(NewColourBlocks).ToArray();
	// Every block you can pick from the creative inventory
	public static readonly int[] Items = new[] { 1, 2, 3, 8, 7, 60, 4, 61, 5, 6, 9, 12, 10, 11, 63, 64, 65, 66, 67, 68, 69 }.Concat(NewColourBlocks).Concat(Flowers)
		.Concat(new[] { Furnace, 22, Water }).ToArray();
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
		for (int k = 0; k < 5; k++)
		{
			int id = 207 + k, tile = 38 + k, sword = 3;
			B[id] = new BlockDef { Name = "Stone " + kinds[k], T = new[] { tile, tile, tile }, Item = true, Tool = kinds[k].ToLower(), Material = "stone",
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
		foreach (int b in Splittable) B[PieceItem + b] = new BlockDef { Name = B[b].Name + " Piece", T = B[b].T, Item = true, Piece = b };
		foreach (int b in Splittable)
			foreach (var (k, baseId) in ShapeItem)
				B[baseId + b] = new BlockDef { Name = $"{B[b].Name} {shapeName[k]}", T = B[b].T, Item = true, Form = k, Of = b };
	}

	public static bool IsTorch(int id) => Get(id)?.Shape == "torch";
	public static bool IsWater(int id) => id >= Water && id <= Water + 7;
	public static bool IsFurnace(int id) => id >= Furnace && id < Furnace + 8;
	public static bool IsItem(int id) => Get(id)?.Item == true;
	public static bool IsTool(int id) => Get(id)?.Tool != null;
	public static bool IsPiece(int id) => Get(id)?.Piece > 0;
	public static string FormOf(int id) => Get(id)?.Form;
	public static int TorchVariant(int wi, int n, int burn = 0) => torchIds.TryGetValue($"{burn}:{wi}:{n}", out var id) ? id : 0;
	public static int TorchMax(int wi) => wi > 0 ? 2 : 4;
	public static int WallIndex(int dx, int dz) { for (int i = 1; i < 5; i++) if (TorchWalls[i][0] == dx && TorchWalls[i][1] == dz) return i; return -1; }
	public static int TorchDrop(int id) => B[id].Burn == 3 ? EverTorch : BurntTorch;
	public static int StackMaxOf(int id) => IsTool(id) ? 1 : StackMax;
	// how high the water's surface sits in its block (water with more water on top fills the whole block)
	public static float WaterHeight(int id, int above) => IsWater(above) ? 1 : id == Water ? 0.875f : Math.Max(0.15f, 0.875f * (8 - (id - Water)) / 8f);
	public static string ShapeOfMask(int m) => ShapeMasks.FirstOrDefault(kv => kv.Value.Contains(m)).Key;
	// the block id to actually place for a hotbar block (bricks pick a random variant)
	public static int PlaceId(int id, Random rnd) =>
		Get(id)?.Places > 0 ? B[id].Places : BrickVariants.Contains(id) ? BrickVariants[rnd.Next(4)] : id;
}
