using System;
using System.Collections.Generic;
using Godot;

namespace Cubeland;

// The texture atlas: every 16x16 block/item picture side by side in one strip.
// Pictures are known by NAME: the file's name in textures/blocks or textures/items, without .png (e.g. "log_side").
// Blocks and items say which pictures they use by name (see Blocks.Def), and each name gets its own place in the
// atlas the first time it's asked for, so a new picture never needs a number: just use its file name.
public static class Atlas
{
	public const int TILE = 16;
	public const int PW = TILE + 2;        // the GPU copy pads each picture with a 1px copy of its edge (no colour bleeding)

	static readonly Dictionary<string, int> slotOf = new();
	static readonly List<string> names = new();
	// the place in the atlas of picture `name` (it gets one the first time it's asked for)
	public static int Tile(string name)
	{
		if (slotOf.TryGetValue(name, out int t)) return t;
		if (Image != null) throw new InvalidOperationException($"the picture \"{name}\" was asked for after the atlas was made");
		slotOf[name] = t = names.Count; names.Add(name);
		return t;
	}
	public static int[] Tiles(params string[] n) => Array.ConvertAll(n, Tile);
	public static int NT => names.Count;   // how many pictures the atlas holds

	// other spellings of a picture's file name
	static readonly Dictionary<string, string> Spellings = new()
	{
		["grass_top"] = "grass", ["tortch_side"] = "torch_side", ["tortch_top"] = "torch_top", ["tortch_bottom"] = "torch_bottom",
		["chisled_terracota"] = "chiseled_terracotta", ["red_chiseled_terracotta"] = "chiseled_red_terracotta",
	};
	// a picture whose file you haven't made (yet) borrows another one
	static readonly Dictionary<string, string> Borrows = new()
	{
		// the 4 looks of Red Bricks
		["bricks_a"] = "red_bricks", ["bricks_b"] = "red_bricks", ["bricks_c"] = "red_bricks", ["bricks_d"] = "red_bricks",
		// the Cutter: the Crafting Bench's side, planks underneath; with no second saw frame the blade just stands still
		["cutter_base"] = "crafting_bench_side", ["cutter_bottom"] = "planks", ["cutter_saw_frame_0"] = "cutter_saw", ["cutter_saw_1"] = "cutter_saw",
		// biome blocks: White Terracotta (snow), the Grass pictures, or Glass (ice)
		["snow"] = "white_terracotta", ["snowy_grass_side"] = "grass_side", ["ice"] = "glass", ["swamp_grass"] = "grass", ["swamp_grass_side"] = "grass_side",
	};

	// the Cutter's saw blade spins: its two frames (cutter_saw, cutter_saw_1) take turns being copied into the picture it's drawn with
	public static readonly int SawTile = Tile("cutter_saw");
	static readonly int[] SawFrames = { Tile("cutter_saw_frame_0"), Tile("cutter_saw_1") };
	public static Image Image;          // TILE*NT x TILE, the plain strip (used to draw icons)
	public static ImageTexture Texture; // the padded GPU copy, used by the world's blocks

	// the u, v (0-1) in the GPU atlas of a point (u, v in 0-1, v = 0 at the picture's bottom like the web version) inside picture `tile`
	public static Vector2 UV(int tile, float u, float v) => new((tile * PW + 1 + u * TILE) / (PW * NT), (1 + (1 - v) * TILE) / PW);

	public static void Build()
	{
		System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Blocks).TypeHandle);   // (every block has asked for its pictures)
		// the picture files, by name
		var files = new Dictionary<string, Image>();
		foreach (string folder in new[] { "res://textures/blocks", "res://textures/items" })
			foreach (var (file, path) in Pictures(folder))
			{
				string name = Spellings.TryGetValue(file, out var s) ? s : file;
				if (files.ContainsKey(name)) continue;
				var img = LoadImage(path);
				if (img == null) continue;
				if (img.GetWidth() != TILE || img.GetHeight() != TILE) img.Resize(TILE, TILE, Image.Interpolation.Nearest);
				img.Convert(Image.Format.Rgba8);
				files[name] = img;
			}
		Image = Image.CreateEmpty(TILE * NT, TILE, false, Image.Format.Rgba8);
		for (int t = 0; t < NT; t++)
		{
			// its own file, or the one it borrows until you make it
			Image img = null;
			for (string n = names[t]; n != null && img == null; n = Borrows.TryGetValue(n, out var b) ? b : null) files.TryGetValue(n, out img);
			if (img != null) { Image.BlitRect(img, new Rect2I(0, 0, TILE, TILE), new Vector2I(t * TILE, 0)); continue; }
			// no picture: a plain grey-pink check, so a missing file is easy to spot
			for (int y = 0; y < TILE; y++) for (int x = 0; x < TILE; x++)
				Image.SetPixel(t * TILE + x, y, ((x / 4 + y / 4) & 1) == 0 ? new Color(0.8f, 0.4f, 0.7f) : new Color(0.3f, 0.3f, 0.3f));
		}
		gpu = Image.CreateEmpty(PW * NT, PW, false, Image.Format.Rgba8);
		for (int t = 0; t < NT; t++) for (int y = 0; y < PW; y++) for (int x = 0; x < PW; x++)
		{
			int sx = t * TILE + Mathf.Clamp(x - 1, 0, TILE - 1), sy = Mathf.Clamp(y - 1, 0, TILE - 1);
			gpu.SetPixel(t * PW + x, y, Image.GetPixel(sx, sy));
		}
		Texture = ImageTexture.CreateFromImage(gpu);
	}
	static Image gpu;
	const int SawFps = 10;   // (the saw blade's frames: see SawTile)
	static double sawTime; static int sawFrame;
	public static void SpinSaw(double dt)
	{
		if (gpu == null || Texture == null) return;
		sawTime += dt;
		int f = (int)(sawTime * SawFps) % SawFrames.Length;
		if (f == sawFrame) return;
		sawFrame = f;
		gpu.BlitRect(gpu, new Rect2I(SawFrames[f] * PW, 0, PW, PW), new Vector2I(SawTile * PW, 0));
		Texture.Update(gpu);
	}
	// (self-test: stepping on by one frame's time shows the other frame, then the first again)
	public static bool SawSpins()
	{
		byte[] Px(int t) => gpu.GetRegion(new Rect2I(t * PW, 0, PW, PW)).GetData();
		bool Shows(int t) => Px(SawTile).AsSpan().SequenceEqual(Px(t));
		if (gpu == null || Px(SawFrames[0]).AsSpan().SequenceEqual(Px(SawFrames[1]))) return false;   // (two different pictures needed)
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
		return LeafTint(id, img);
	}
	// Fir, Palm and Willow Leaves (and their pieces, slabs and stairs) share the Oak Leaves picture: their icons and drops get its green in their own colour
	static Image LeafTint(int id, Image img)
	{
		var b = Blocks.Get(id);
		var t = b == null ? null : Blocks.LeafTint(id) ?? Blocks.LeafTint(b.Piece) ?? Blocks.LeafTint(b.Of);
		if (t == null) return img;
		for (int y = 0; y < img.GetHeight(); y++) for (int x = 0; x < img.GetWidth(); x++)
		{
			var c = img.GetPixel(x, y);
			if (c.G > c.R && c.G > c.B) img.SetPixel(x, y, new Color(c.R * t[0], c.G * t[1], Mathf.Min(1, c.B * t[2]), c.A));
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
		strip = LeafTint(id, strip);
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
		return LeafTint(id, icon);
	}
}
