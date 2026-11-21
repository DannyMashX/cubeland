using System;
using System.Collections.Generic;
using Godot;

namespace Cubeland;

// The texture atlas: every 16x16 block/item picture side by side in one strip.
// Your pictures in textures/blocks and textures/items fill the slots below (by file name).
public static class Atlas
{
	public const int TILE = 16, NT = 246;   // 246 pictures (241-245 are the Cutter and its spinning saw blade; 240 is spare) (204+ are armor, backpacks and belts) side by side (153+ are Pack & Plate's ores, Fireclay, Foundry, metals and metal tools, 52+ are the colours, 102+ Fear & Foes, 105+ Till & Tend, 121+ cows, 124+ the deep stone layers and bedrock, 132+ the deep cobblestone, stone bricks and chiseled stone)
	public const int PW = TILE + 2;        // the GPU copy pads each picture with a 1px copy of its edge (no colour bleeding)

	// Which atlas slot each texture file fills (a file can fill more than one)
	static readonly Dictionary<string, int[]> Slots = new()
	{
		["grass"] = new[] { 0 }, ["grass_top"] = new[] { 0 }, ["grass_side"] = new[] { 1 }, ["dirt"] = new[] { 2 }, ["stone"] = new[] { 3 },
		["log_side"] = new[] { 4 }, ["log_top"] = new[] { 5 }, ["leaves"] = new[] { 6 }, ["sand"] = new[] { 7 }, ["planks"] = new[] { 8 },
		["cobblestone"] = new[] { 9 }, ["stone_bricks"] = new[] { 10 }, ["glass"] = new[] { 11 }, ["gravel"] = new[] { 12 },
		["clay"] = new[] { 17 }, ["bricks"] = new[] { 18 }, ["terracotta"] = new[] { 19 }, ["red_terracotta"] = new[] { 20 },
		["chiseled_terracotta"] = new[] { 21 }, ["chisled_terracota"] = new[] { 21 }, ["chiseled_red_terracotta"] = new[] { 22 },
		["red_chiseled_terracotta"] = new[] { 22 }, ["chiseled_stone"] = new[] { 35 },
		["kiln_front"] = new[] { 36 }, ["kiln_front_on"] = new[] { 37 }, ["kiln_side"] = new[] { 43 }, ["kiln_bottom"] = new[] { 44 },
		["clay_ball"] = new[] { 45 }, ["brick"] = new[] { 46 },
		["red_bricks"] = new[] { 13, 14, 15, 16 }, ["bricks_a"] = new[] { 13 }, ["bricks_b"] = new[] { 14 }, ["bricks_c"] = new[] { 15 }, ["bricks_d"] = new[] { 16 },
		["torch_side"] = new[] { 23 }, ["torch_top"] = new[] { 24 }, ["torch_bottom"] = new[] { 25 },
		["tortch_side"] = new[] { 23 }, ["tortch_top"] = new[] { 24 }, ["tortch_bottom"] = new[] { 25 },
		["smoldering_torch_side"] = new[] { 48 }, ["smoldering_torch_top"] = new[] { 49 }, ["burnt_torch_side"] = new[] { 50 }, ["burnt_torch_top"] = new[] { 51 },
		["chocolate"] = new[] { 26 }, ["water"] = new[] { 27 },
		["crafting_bench_side"] = new[] { 28 }, ["crafting_bench_top"] = new[] { 29 }, ["stripped_log_side"] = new[] { 30 }, ["stripped_log_top"] = new[] { 31 },
		["stick"] = new[] { 32 }, ["rock"] = new[] { 33 }, ["crude_axe"] = new[] { 34 }, ["fire_essence"] = new[] { 47 },
		["stone_axe"] = new[] { 38 }, ["stone_pickaxe"] = new[] { 39 }, ["stone_shovel"] = new[] { 40 }, ["stone_sword"] = new[] { 41 }, ["stone_hoe"] = new[] { 42 },
		["bone"] = new[] { 102 }, ["bone_ash"] = new[] { 103 }, ["gloom_wisp"] = new[] { 104 },
		["grain_seeds"] = new[] { 105 }, ["grain"] = new[] { 106 }, ["flour"] = new[] { 107 }, ["bread"] = new[] { 108 },
		["tilled_dirt"] = new[] { 109 }, ["wet_tilled_dirt"] = new[] { 110 }, ["fertilized_dirt"] = new[] { 111 }, ["wet_fertilized_dirt"] = new[] { 112 },
		["leather"] = new[] { 121 }, ["raw_meat"] = new[] { 122 }, ["cooked_meat"] = new[] { 123 },
	};

	// the colours: <colour>_terracotta 52-62, <colour>_chiseled_terracotta 63-73, <colour>_bricks 74-84 (orange..black),
	// <colour>_flower 85-89, <colour>_dye 90-101 (red..black)
	static Atlas()
	{
		for (int k = 0; k < Blocks.Colours.Length; k++)
		{
			string c = Blocks.Colours[k];
			Slots[c + "_dye"] = new[] { 90 + k };
			if (k == 0) continue;
			Slots[c + "_terracotta"] = new[] { 51 + k }; Slots[c + "_chiseled_terracotta"] = new[] { 62 + k }; Slots[c + "_bricks"] = new[] { 73 + k };
		}
		for (int k = 0; k < Blocks.FlowerColours.Length; k++) Slots[Blocks.FlowerColours[k] + "_flower"] = new[] { 85 + k };
		for (int k = 0; k < 8; k++) Slots["grain_stage_" + k] = new[] { 113 + k };
		for (int n = 2; n <= Blocks.StrataLayers; n++) Slots["stone_layer_" + n] = new[] { 122 + n };   // stone_layer_2 .. _8: pictures 124-130
		Slots["bedrock"] = new[] { 131 };
		for (int n = 2; n <= Blocks.StrataLayers; n++)   // the deep stone sets: pictures 132-138, 139-145, 146-152
		{
			Slots["cobblestone_layer_" + n] = new[] { 130 + n };
			Slots["stone_bricks_layer_" + n] = new[] { 137 + n };
			Slots["chiseled_stone_layer_" + n] = new[] { 144 + n };
		}
		// Pack & Plate: ores 153-156, Fireclay blocks 157-161, the Foundry's Furnace 162-164, chunks 165-168, bricks 169-176, fireclay items 177-178,
		// metal tools 179-203
		string[] pack = { "copper_ore", "iron_ore", "gold_ore", "tin_ore", "fireclay", "fireclay_terracotta", "fireclay_bricks", "fireclay_casing", "chiseled_fireclay",
			"furnace_front", "furnace_front_on", "furnace_bottom", "copper_chunk", "iron_chunk", "gold_chunk", "tin_chunk",
			"copper_brick", "iron_brick", "gold_brick", "tin_brick", "bronite_brick", "rosium_brick", "steel_scrap", "steel_brick", "fireclay_ball", "fireclay_brick" };
		for (int k = 0; k < pack.Length; k++) Slots[pack[k]] = new[] { 153 + k };
		string[] metals = { "copper", "iron", "bronite", "rosium", "steel" }, kinds = { "axe", "pickaxe", "shovel", "sword", "hoe" };
		for (int j = 0; j < 5; j++) for (int k = 0; k < 5; k++) Slots[$"{metals[j]}_{kinds[k]}"] = new[] { 179 + j * 5 + k };
		// gear: armor 204-227, backpacks 228-233, utility belts 234-239
		string[] tiers = { "leather", "copper", "iron", "bronite", "rosium", "steel" }, pieces = { "helmet", "chestplate", "leggings", "boots" };
		for (int t = 0; t < 6; t++)
		{
			for (int k = 0; k < 4; k++) Slots[$"{tiers[t]}_{pieces[k]}"] = new[] { 204 + t * 4 + k };
			Slots[tiers[t] + "_backpack"] = new[] { 228 + t };
			Slots[tiers[t] + "_utility_belt"] = new[] { 234 + t };
		}
		// the Cutter: 241-243 (its top is the stripped log top; until you make its pictures it borrows the Crafting Bench side, with planks underneath: see Build)
		// its saw blade spins: 243 is the one drawn, and the frames (244: cutter_saw, 245: cutter_saw_1) take turns being copied into it (see SpinSaw)
		Slots["cutter_saw"] = new[] { 243, 244 }; Slots["cutter_saw_1"] = new[] { 245 }; Slots["cutter_base"] = new[] { 241 }; Slots["cutter_bottom"] = new[] { 242 };   // (cutter_base: its sides)
	}

	public static Image Image;          // TILE*NT x TILE, the plain strip (used to draw icons)
	public static ImageTexture Texture; // the padded GPU copy, used by the world's blocks

	// the u, v (0-1) in the GPU atlas of a point (u, v in 0-1, v = 0 at the picture's bottom like the web version) inside picture `tile`
	public static Vector2 UV(int tile, float u, float v) => new((tile * PW + 1 + u * TILE) / (PW * NT), (1 + (1 - v) * TILE) / PW);

	public static void Build()
	{
		Image = Image.CreateEmpty(TILE * NT, TILE, false, Image.Format.Rgba8);
		// placeholder for any slot with no picture: a plain grey-pink check, so a missing file is easy to spot
		for (int t = 0; t < NT; t++) for (int y = 0; y < TILE; y++) for (int x = 0; x < TILE; x++)
			Image.SetPixel(t * TILE + x, y, ((x / 4 + y / 4) & 1) == 0 ? new Color(0.8f, 0.4f, 0.7f) : new Color(0.3f, 0.3f, 0.3f));
		var loaded = new HashSet<string>();
		foreach (string folder in new[] { "res://textures/blocks", "res://textures/items" })
			foreach (var (name, path) in Pictures(folder))
			{
				if (!Slots.TryGetValue(name, out var slots)) continue;
				var img = LoadImage(path);
				if (img == null) continue;
				loaded.Add(name);
				if (img.GetWidth() != TILE || img.GetHeight() != TILE) img.Resize(TILE, TILE, Image.Interpolation.Nearest);
				foreach (int t in slots) Image.BlitRect(img, new Rect2I(0, 0, TILE, TILE), new Vector2I(t * TILE, 0));
			}
		// a Cutter picture you haven't made yet: the Crafting Bench's side or planks (bottom) for now
		// (and no second saw frame: the blade just stands still)
		foreach (var (mine, borrow) in new[] { ("cutter_base", 28), ("cutter_bottom", 8), ("cutter_saw_1", 244) })
			if (!loaded.Contains(mine)) Image.BlitRect(Image.GetRegion(new Rect2I(borrow * TILE, 0, TILE, TILE)), new Rect2I(0, 0, TILE, TILE), new Vector2I(Slots[mine][0] * TILE, 0));
		gpu = Image.CreateEmpty(PW * NT, PW, false, Image.Format.Rgba8);
		for (int t = 0; t < NT; t++) for (int y = 0; y < PW; y++) for (int x = 0; x < PW; x++)
		{
			int sx = t * TILE + Mathf.Clamp(x - 1, 0, TILE - 1), sy = Mathf.Clamp(y - 1, 0, TILE - 1);
			gpu.SetPixel(t * PW + x, y, Image.GetPixel(sx, sy));
		}
		Texture = ImageTexture.CreateFromImage(gpu);
	}
	static Image gpu;
	// the Cutter's saw blade spins: its two frames (pictures 244 and 245) take turns being copied into the picture it's drawn with (243)
	const int SawFps = 10;
	static readonly int[] SawFrames = { 244, 245 };
	static double sawTime; static int sawFrame;
	public static void SpinSaw(double dt)
	{
		if (gpu == null || Texture == null) return;
		sawTime += dt;
		int f = (int)(sawTime * SawFps) % SawFrames.Length;
		if (f == sawFrame) return;
		sawFrame = f;
		gpu.BlitRect(gpu, new Rect2I(SawFrames[f] * PW, 0, PW, PW), new Vector2I(243 * PW, 0));
		Texture.Update(gpu);
	}
	// (self-test: stepping on by one frame's time shows the other frame, then the first again)
	public static bool SawSpins()
	{
		byte[] Tile(int t) => gpu.GetRegion(new Rect2I(t * PW, 0, PW, PW)).GetData();
		bool Shows(int t) => Tile(243).AsSpan().SequenceEqual(Tile(t));
		if (gpu == null || Tile(244).AsSpan().SequenceEqual(Tile(245))) return false;   // (two different pictures needed)
		int a = SawFrames[sawFrame], b = SawFrames[1 - sawFrame];
		bool ok = Shows(a) || sawTime == 0;
		SpinSaw(1.0 / SawFps); ok &= Shows(b);
		SpinSaw(1.0 / SawFps); ok &= Shows(a);
		return ok;
	}

	// the PNGs in a folder: (lower-case name without .png, path). Works in the editor and in exported games.
	public static IEnumerable<(string name, string path)> Pictures(string folder) => Skin.Pictures(folder);
	public static Image LoadImage(string path) => Skin.LoadImage(path);

	static readonly Dictionary<int, ImageTexture> iconCache = new();
	public static ImageTexture IconTexture(int id)
	{
		if (!iconCache.TryGetValue(id, out var t)) iconCache[id] = t = ImageTexture.CreateFromImage(Icon(id));
		return t;
	}
	// what inventory slots, the hotbar and recipe lists show: an ordinary whole block as a little cube, anything else
	// its flat picture (dropped and held things keep using the flat IconTexture)
	static readonly Dictionary<int, ImageTexture> slotCache = new();
	public static ImageTexture SlotIcon(int id)
	{
		if (!Is3DIcon(id)) return IconTexture(id);
		if (!slotCache.TryGetValue(id, out var t)) slotCache[id] = t = ImageTexture.CreateFromImage(BoxIcon(id));
		return t;
	}
	// What a block, slab, stairs, piece or torch is made of, for its 3D icon and its 3D drop: boxes {x0, x1, z0, z1, y0, y1}
	// in texture pixels (0-16 = a whole block), or null for things shown as a flat picture. Slabs and stairs are shown
	// in one set way (made of the 8 half-size spots, spot = x + y*2 + z*4): a bottom slab, stairs with the step at the
	// back, a quarter lying at the front... A loose piece is a small cube in the middle. (Same as the web edition's iconBoxes.)
	static readonly Dictionary<string, int> IconMasks = new()
		{ ["slab"] = 0x33, ["vslab"] = 0x55, ["stairs"] = 0x33 | 0x44, ["quarter"] = 0x30, ["vquarter"] = 0xA0, ["vstairs"] = 0xFF ^ 0xA0 };
	public static int[][] IconBoxes(int id)
	{
		var b = Blocks.Get(id);
		if (id == 0 || b == null || b.T == null || b.T.Length < 3) return null;
		if (b.Piece > 0) return new[] { new[] { 4, 12, 4, 12, 4, 12 } };
		if (b.Form != null && IconMasks.TryGetValue(b.Form, out int mask))
		{
			var boxes = new List<int[]>();
			for (int s = 0; s < 8; s++) if ((mask & (1 << s)) != 0)
			{
				int x = (s & 1) * 8, y = (s >> 1 & 1) * 8, z = (s >> 2 & 1) * 8;
				boxes.Add(new[] { x, x + 8, z, z + 8, y, y + 8 });
			}
			return boxes.ToArray();
		}
		if (b.Shape == "half") return new[] { new[] { 0, 16, 0, 16, 0, 8 } };   // the Cutter: half a block tall
		if (!IsCubeIcon(id)) return null;
		return b.Shape == "torch" ? new[] { new[] { 6, 10, 6, 10, 0, 16 } } : new[] { new[] { 0, 16, 0, 16, 0, 16 } };
	}
	public static bool Is3DIcon(int id) => IconBoxes(id) != null;
	// a thing's face pictures: top, front, side, bottom (the kiln's front is its opening)
	static int[] IconTiles(BlockDef b) => new[] { b.T[1], b.T.Length > 3 ? b.T[3] : b.T[0], b.T[0], b.T[2] };
	// The 3D icon: seen from above at the front-left corner, in a 4 x TILE square (each texture pixel is 2 icon pixels
	// across). Each icon pixel takes its colour straight from the nearest face under it, so it stays crisp.
	// (The same drawing as the web edition's drawBoxIcon.)
	public static Image BoxIcon(int id)
	{
		int R = TILE * 4, T = TILE; float k = 2;
		var boxes = IconBoxes(id); var tiles = IconTiles(Blocks.Get(id));
		float[] shades = { 1, 0.8f, 0.62f };
		var img = Image.CreateEmpty(R, R, false, Image.Format.Rgba8);
		for (int y = 0; y < R; y++)
			for (int x = 0; x < R; x++)
			{
				// A (x across), B (z, towards you) and D (down from the top) of the point on each face under this pixel;
				// the face nearest you (the biggest A + B - D) that isn't see-through wins
				float X = (x + 0.5f - T * k) / k, Y = (y + 0.5f) / k, best = -1e9f; Color? col = null;
				foreach (var bx in boxes)
				{
					float a0 = bx[0], a1 = bx[1], b0 = bx[2], b1 = bx[3], d0 = T - bx[5], d1 = T - bx[4];
					void Try(int f, float A, float B, float D, int u, int v)
					{
						float depth = A + B - D;
						if (depth <= best) return;
						var c = Image.GetPixel(tiles[f] * T + u, v);
						if (c.A < 0.5f) return;
						best = depth; col = new Color(c.R * shades[f], c.G * shades[f], c.B * shades[f], 1);
					}
					{ float A = Y - d0 + X / 2, B = Y - d0 - X / 2;                    // top
					  if (A >= a0 && A < a1 && B >= b0 && B < b1) Try(0, A, B, d0, (int)MathF.Floor(A), (int)MathF.Floor(B)); }
					{ float A = X + b1, D = Y - (A + b1) / 2;                           // front
					  if (A >= a0 && A < a1 && D >= d0 && D < d1) Try(1, A, b1, D, (int)MathF.Floor(A), (int)MathF.Floor(D)); }
					{ float B = a1 - X, D = Y - (a1 + B) / 2;                           // side
					  if (B >= b0 && B < b1 && D >= d0 && D < d1) Try(2, a1, B, D, T - 1 - (int)MathF.Floor(B), (int)MathF.Floor(D)); }
				}
				if (col != null) img.SetPixel(x, y, col.Value);
			}
		return img;
	}
	// the same boxes as a little 3D model, 1 unit for a whole block, centred, with each face's picture (for drops).
	// Its texture is the 4 face pictures side by side; the faces are shaded like the icon (the item shader multiplies
	// by the vertex colour).
	static readonly Dictionary<int, (ArrayMesh mesh, ImageTexture tex)> boxModels = new();
	public static (ArrayMesh mesh, ImageTexture tex) BoxModel(int id)
	{
		if (boxModels.TryGetValue(id, out var m)) return m;
		int T = TILE; var tiles = IconTiles(Blocks.Get(id));
		var strip = Image.CreateEmpty(T * 4, T, false, Image.Format.Rgba8);
		for (int f = 0; f < 4; f++) strip.BlitRect(Image, new Rect2I(tiles[f] * T, 0, T, T), new Vector2I(f * T, 0));
		var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
		// one face: corners p ({A, h, B}), their texture pixels q ({u, v}, v down from the picture's top), tile f, shade
		void Face(float[][] p, float[][] q, int f, float s)
		{
			foreach (int i in new[] { 0, 1, 2, 0, 2, 3 })
			{
				st.SetColor(new Color(s, s, s));
				st.SetUV(new Vector2((f * T + q[i][0]) / (T * 4f), q[i][1] / T));
				st.AddVertex(new Vector3(p[i][0] / T - 0.5f, p[i][1] / T - 0.5f, p[i][2] / T - 0.5f));
			}
		}
		float[] P(float a, float h, float b) => new[] { a, h, b };
		float[] Q(float u, float v) => new[] { u, v };
		foreach (var bx in IconBoxes(id))
		{
			float a0 = bx[0], a1 = bx[1], b0 = bx[2], b1 = bx[3], h0 = bx[4], h1 = bx[5], d0 = T - h1, d1 = T - h0;
			Face(new[] { P(a0, h1, b0), P(a0, h1, b1), P(a1, h1, b1), P(a1, h1, b0) }, new[] { Q(a0, b0), Q(a0, b1), Q(a1, b1), Q(a1, b0) }, 0, 1);       // top
			Face(new[] { P(a0, h0, b1), P(a0, h0, b0), P(a1, h0, b0), P(a1, h0, b1) }, new[] { Q(a0, b1), Q(a0, b0), Q(a1, b0), Q(a1, b1) }, 3, 0.5f);    // bottom
			Face(new[] { P(a0, h1, b1), P(a0, h0, b1), P(a1, h0, b1), P(a1, h1, b1) }, new[] { Q(a0, d0), Q(a0, d1), Q(a1, d1), Q(a1, d0) }, 1, 0.8f);    // front
			Face(new[] { P(a1, h1, b0), P(a1, h0, b0), P(a0, h0, b0), P(a0, h1, b0) }, new[] { Q(T - a1, d0), Q(T - a1, d1), Q(T - a0, d1), Q(T - a0, d0) }, 2, 0.8f);   // back
			Face(new[] { P(a1, h1, b1), P(a1, h0, b1), P(a1, h0, b0), P(a1, h1, b0) }, new[] { Q(T - b1, d0), Q(T - b1, d1), Q(T - b0, d1), Q(T - b0, d0) }, 2, 0.62f);  // right side
			Face(new[] { P(a0, h1, b0), P(a0, h0, b0), P(a0, h0, b1), P(a0, h1, b1) }, new[] { Q(b0, d0), Q(b0, d1), Q(b1, d1), Q(b1, d0) }, 2, 0.62f);   // left side
		}
		return boxModels[id] = (st.Commit(), ImageTexture.CreateFromImage(strip));
	}
	// ordinary whole blocks (not items, shapes, liquids, pieces, slabs or stairs, nor ones with a picture of their own);
	// a block with a front face (the kiln) is a cube too, and a torch is a little standing stick
	public static bool IsCubeIcon(int id)
	{
		var b = Blocks.Get(id);
		return id != 0 && b != null && b.T != null && b.T.Length >= 3 && (b.Icon < 0 || b.T.Length > 3) && !b.Item
			&& (b.Shape == null || b.Shape == "torch") && !b.Liquid && b.Piece <= 0 && b.Form == null;
	}
	// draw a block's or item's picture into a TILE x TILE image (its hotbar icon)
	public static Image Icon(int id)
	{
		var icon = Image.CreateEmpty(TILE, TILE, false, Image.Format.Rgba8);
		var b = Blocks.Get(id);
		if (b == null) return icon;
		int sx = b.T[0] * TILE, h = TILE / 2;
		void Copy(int x, int y, int w, int hh, int dx, int dy) => icon.BlitRect(Image, new Rect2I(sx + x, y, w, hh), new Vector2I(dx, dy));
		// a piece: a half-size square (one quarter of the block's side picture)
		if (b.Piece > 0) Copy(0, h, h, h, TILE / 4, TILE / 4);
		// slabs and stairs: the part of the block's side picture that they fill
		else if (b.Form != null)
		{
			switch (b.Form)
			{
				case "vslab": Copy(0, 0, h, TILE, 0, 0); break;
				case "quarter": Copy(0, h, TILE, h / 2 + 2, 0, h - 2); break;              // a thin bar
				case "vquarter": Copy(h - 3, 0, h / 2 + 2, TILE, h - 3, 0); break;         // a standing bar
				case "vstairs": Copy(0, 0, h, TILE, 0, 0); Copy(h, h, h, h, h, h); break;  // tall half + a corner
				default: Copy(0, h, TILE, h, 0, h); if (b.Form == "stairs") Copy(h, 0, h, h, h, 0); break;   // the bottom half (+ the step)
			}
		}
		else
		{
			int tile = b.Icon >= 0 ? b.Icon : b.T[0];
			icon.BlitRect(Image, new Rect2I(tile * TILE, 0, TILE, TILE), Vector2I.Zero);
		}
		return icon;
	}
}
