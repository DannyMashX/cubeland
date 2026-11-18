using System.Collections.Generic;
using Godot;

namespace Cubeland;

// The texture atlas: every 16x16 block/item picture side by side in one strip.
// Your pictures in textures/blocks and textures/items fill the slots below (by file name).
public static class Atlas
{
	public const int TILE = 16, NT = 102;   // 102 pictures side by side (52+ are the colours)
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
		foreach (string folder in new[] { "res://textures/blocks", "res://textures/items" })
			foreach (var (name, path) in Pictures(folder))
			{
				if (!Slots.TryGetValue(name, out var slots)) continue;
				var img = LoadImage(path);
				if (img == null) continue;
				if (img.GetWidth() != TILE || img.GetHeight() != TILE) img.Resize(TILE, TILE, Image.Interpolation.Nearest);
				foreach (int t in slots) Image.BlitRect(img, new Rect2I(0, 0, TILE, TILE), new Vector2I(t * TILE, 0));
			}
		var gpu = Image.CreateEmpty(PW * NT, PW, false, Image.Format.Rgba8);
		for (int t = 0; t < NT; t++) for (int y = 0; y < PW; y++) for (int x = 0; x < PW; x++)
		{
			int sx = t * TILE + Mathf.Clamp(x - 1, 0, TILE - 1), sy = Mathf.Clamp(y - 1, 0, TILE - 1);
			gpu.SetPixel(t * PW + x, y, Image.GetPixel(sx, sy));
		}
		Texture = ImageTexture.CreateFromImage(gpu);
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
		if (!IsCubeIcon(id)) return IconTexture(id);
		if (!slotCache.TryGetValue(id, out var t)) slotCache[id] = t = ImageTexture.CreateFromImage(CubeIcon(id));
		return t;
	}
	// ordinary whole blocks (not items, shapes, liquids, pieces, slabs or stairs, nor ones with a picture of their own);
	// a block with a front face (the kiln) is a cube too, and a torch is a little standing stick
	public static bool IsCubeIcon(int id)
	{
		var b = Blocks.Get(id);
		return id != 0 && b != null && b.T != null && b.T.Length >= 3 && (b.Icon < 0 || b.T.Length > 3) && !b.Item
			&& (b.Shape == null || b.Shape == "torch") && !b.Liquid && b.Piece <= 0 && b.Form == null;
	}
	// a block as a little cube, seen from above at the front-left corner: top, front (left) and side (right), in a
	// 4 x TILE square (each texture pixel is 2 icon pixels across). Each pixel takes its colour straight from the face
	// picture under it, so it stays crisp. A torch is the same drawing cut down to its stick (texture pixels 6-10).
	// (The same drawing as the web edition's drawCubeIcon.)
	public static Image CubeIcon(int id)
	{
		int R = TILE * 4, s = TILE * 2, T = TILE; var b = Blocks.Get(id);
		int a = b.Shape == "torch" ? 6 : 0, e = b.Shape == "torch" ? 10 : TILE;
		float k = (float)s / TILE;
		int[] tiles = { b.T[1], b.T.Length > 3 ? b.T[3] : b.T[0], b.T[0] };   // top, front, side
		var img = Image.CreateEmpty(R, R, false, Image.Format.Rgba8);
		// per face: origin (a point on the top, A and B texture pixels in), where one texture pixel across (U) and one
		// down (V) moves you, its shade, and the solid u and v
		float AtX(float A, float B) => s + (A - B) * k; float AtY(float A, float B) => (A + B) * k / 2;
		float[][] faces = {
			new[] { AtX(0, 0), AtY(0, 0), 1, 0.5f, -1, 0.5f, 1, a, e, a, e },
			new[] { AtX(0, e), AtY(0, e), 1, 0.5f, 0, 1, 0.8f, a, e, 0, T },
			new[] { AtX(e, T), AtY(e, T), 1, -0.5f, 0, 1, 0.62f, T - e, T - a, 0, T } };
		for (int y = 0; y < R; y++)
			for (int x = 0; x < R; x++)
				for (int fi = 0; fi < 3; fi++)
				{
					var f = faces[fi];
					float dx = (x + 0.5f - f[0]) / k, dy = (y + 0.5f - f[1]) / k, det = f[2] * f[5] - f[3] * f[4];
					float u = (dx * f[5] - dy * f[4]) / det, v = (f[2] * dy - f[3] * dx) / det;
					if (u < f[7] || u >= f[8] || v < f[9] || v >= f[10]) continue;
					var c = Image.GetPixel(tiles[fi] * TILE + (int)u, (int)v);
					if (c.A >= 0.5f) img.SetPixel(x, y, new Color(c.R * f[6], c.G * f[6], c.B * f[6], 1));
					break;
				}
		return img;
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
