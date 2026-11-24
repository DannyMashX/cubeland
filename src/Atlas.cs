using System;
using System.Collections.Generic;
using System.Linq;
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
	public static bool Uses(string name) => slotOf.ContainsKey(name);   // (a picture the game asks for)
	public static IReadOnlyList<string> Names => names;                 // (every picture, in atlas order)

	// other spellings of a picture's file name (old or misspelt names that still work), and pictures whose file you haven't
	// made yet that borrow another's until you do (that one may borrow too): data/texture-names.json
	public static readonly Dictionary<string, string> Spellings = Nums.TextureNames.TEXTURE_SPELLINGS ?? new();
	public static readonly Dictionary<string, string> Borrows = Nums.TextureNames.TEXTURE_BORROWS ?? new();

	// the Cutter's saw blade spins: its two frames (cutter_saw, cutter_saw_1) take turns being copied into the picture it's drawn with
	public static readonly int SawTile = Tile("cutter_saw");
	static readonly int[] SawFrames = { Tile("cutter_saw_frame_0"), Tile("cutter_saw_1") };
	public static Image Image;          // TILE*NT x TILE, the plain strip (used to draw icons)
	public static ImageTexture Texture; // the padded GPU copy, used by the world's blocks

	// The GPU atlas is a grid, Cols pictures across (a single strip got too wide for graphics cards once the cut-shape
	// pictures came in). The plain Image used for icons is still one strip.
	const int Cols = 64;
	static int Rows => (NT + Cols - 1) / Cols;
	static Vector2I Cell(int t) => new(t % Cols * PW, t / Cols * PW);   // (a picture's top-left corner in the GPU atlas)
	// the u, v (0-1) in the GPU atlas of a point (u, v in 0-1, v = 0 at the picture's bottom like the web version) inside picture `tile`
	public static Vector2 UV(int tile, float u, float v) { var c = Cell(tile); return new((c.X + 1 + u * TILE) / (PW * Cols), (c.Y + 1 + (1 - v) * TILE) / (PW * Rows)); }

	// Cut-shape pictures (textures/blocks/shapes/<picture>_<shape>.png): the same picture with its frame drawn along the cut
	// edges too, for a slab's, stairs'... face that only fills some quarters of it. Quarters of a face, as seen in the
	// picture: 1 top-left, 2 top-right, 4 bottom-left, 8 bottom-right. Same rule as the web version (see ShapeTile).
	static readonly Dictionary<string, int> ShapeMasks = new()
	{
		["top_half"] = 3, ["bottom_half"] = 12, ["left_half"] = 5, ["right_half"] = 10, ["top_left"] = 1, ["top_right"] = 2, ["bottom_left"] = 4, ["bottom_right"] = 8,
		["no_top_left"] = 14, ["no_top_right"] = 13, ["no_bottom_left"] = 11, ["no_bottom_right"] = 7, ["diagonal_a"] = 9, ["diagonal_b"] = 6,
	};
	static readonly Dictionary<(int, int), int> shapeTiles = new();
	// the picture for a face showing quarters `mask` of picture `tile`: its cut-shape picture if there is one, else the picture itself
	// (a face filling just its top or bottom half, like a slab's or a double slab's side: <picture>_slab_side if there is one,
	// the two halves framed, which the user asked for by name; pixel for pixel the same as top_half / bottom_half)
	public static int ShapeTile(int tile, int mask) =>
		(mask == 3 || mask == 12) && slabSides.TryGetValue(tile, out int ss) ? ss
		: mask != 15 && mask != 0 && shapeTiles.TryGetValue((tile, mask), out int t) ? t : tile;
	static readonly Dictionary<int, int> slabSides = new();
	public static int Quarter(int col, int row) => row == 0 ? (col == 0 ? 1 : 2) : (col == 0 ? 4 : 8);   // (col 0 left, row 0 top)

	// ---- Texture sheets (textures/blocks/sheets/<picture>.png): one picture's cut-shape pictures together, 3 x 3 cells laid out
	// like the face (cell = width / 3). textures/sheet-layout.json (shared with the web version) says which cell is which
	// picture (<picture> + its suffix; "full" is the picture itself) and how the others are put together from quarters of
	// those. A cell that's all see-through is a picture that isn't there. Returns the pictures by name, each 16 x 16.
	static readonly HashSet<string> sheetBases = new();
	static Dictionary<string, Image> CutSheets()
	{
		var outp = new Dictionary<string, Image>();
		string json = FileAccess.FileExists("res://textures/sheet-layout.json") ? FileAccess.GetFileAsString("res://textures/sheet-layout.json") : null;
		if (json == null) return outp;
		using var doc = System.Text.Json.JsonDocument.Parse(json);
		var root = doc.RootElement; int grid = root.GetProperty("grid").GetInt32();
		var cells = root.GetProperty("cells").EnumerateObject().Select(p => (suffix: p.Name == "full" ? "" : p.Name, col: p.Value[0].GetInt32(), row: p.Value[1].GetInt32())).ToList();
		var compose = root.GetProperty("compose").EnumerateObject()
			.Select(p => (suffix: p.Name, parts: p.Value.EnumerateObject().Select(q => (corner: q.Name, from: q.Value.GetString() == "full" ? "" : q.Value.GetString())).ToList())).ToList();
		foreach (var (sheet, path) in Pictures("res://textures/blocks/sheets"))
		{
			var img = LoadImage(path);
			if (img == null) continue;
			sheetBases.Add(sheet);
			int cs = img.GetWidth() / grid;
			var got = new Dictionary<string, Image>();
			foreach (var (suffix, col, row) in cells)
			{
				var c = img.GetRegion(new Rect2I(col * cs, row * cs, cs, cs));
				if (c.IsInvisible()) continue;   // (no such picture)
				if (cs != TILE) c.Resize(TILE, TILE, Image.Interpolation.Nearest);
				got[suffix] = c;
			}
			foreach (var (suffix, parts) in compose)
			{
				var c = Image.CreateEmpty(TILE, TILE, false, Image.Format.Rgba8); bool any = false;
				foreach (var (corner, from) in parts)
				{
					if (!got.TryGetValue(from, out var src)) continue;
					int h = TILE / 2, qx = corner[1] == 'R' ? h : 0, qy = corner[0] == 'B' ? h : 0;
					c.BlitRect(src, new Rect2I(qx, qy, h, h), new Vector2I(qx, qy)); any = true;
				}
				if (any && !c.IsInvisible()) got[suffix] = c;
			}
			foreach (var (suffix, c) in got) outp[sheet + suffix] = c;
		}
		return outp;
	}

	public static void Build()
	{
		System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Blocks).TypeHandle);   // (every block has asked for its pictures)
		// the picture files, by name (the sheets' pictures first: a loose file with the same name takes their place)
		var files = CutSheets();
		var loose = new HashSet<string>();
		foreach (string folder in new[] { "res://textures/blocks", "res://textures/items", "res://textures/blocks/shapes" })
			foreach (var (file, _) in Pictures(folder)) loose.Add(Spellings.TryGetValue(file, out var s) ? s : file);
		foreach (var n in files.Keys.ToArray())
			if (loose.Contains(n)) { if (!sheetBases.Contains(n)) GD.Print($"textures: {n}.png is a loose file and on a sheet; the loose file wins"); files.Remove(n); }   // (the full pictures are kept loose for now, so no note for those)
		// the cut-shape pictures of the pictures blocks use (each gets a place in the atlas now, before it's made)
		foreach (var file in loose.Concat(files.Keys).Distinct().ToArray())
		{
			foreach (var (shape, mask) in ShapeMasks)
				if (file.EndsWith("_" + shape) && slotOf.TryGetValue(file[..^(shape.Length + 1)], out int baseTile)) { shapeTiles[(baseTile, mask)] = Tile(file); break; }
			if (file.EndsWith("_slab_side") && slotOf.TryGetValue(file[..^"_slab_side".Length], out int sideOf)) slabSides[sideOf] = Tile(file);
		}
		foreach (string folder in new[] { "res://textures/blocks", "res://textures/items", "res://textures/blocks/shapes" })
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
			int hops = 0;   // (a circle of borrows with no file in it just stops after 32)
			for (string n = names[t]; n != null && img == null && hops++ < 32; n = Borrows.TryGetValue(n, out var b) ? b : null) files.TryGetValue(n, out img);
			if (img != null) { Image.BlitRect(img, new Rect2I(0, 0, TILE, TILE), new Vector2I(t * TILE, 0)); continue; }
			// no picture: a plain grey-pink check, so a missing file is easy to spot
			for (int y = 0; y < TILE; y++) for (int x = 0; x < TILE; x++)
				Image.SetPixel(t * TILE + x, y, ((x / 4 + y / 4) & 1) == 0 ? new Color(0.8f, 0.4f, 0.7f) : new Color(0.3f, 0.3f, 0.3f));
		}
		gpu = Image.CreateEmpty(PW * Cols, PW * Rows, false, Image.Format.Rgba8);
		for (int t = 0; t < NT; t++) { var c = Cell(t); for (int y = 0; y < PW; y++) for (int x = 0; x < PW; x++)
		{
			int sx = t * TILE + Mathf.Clamp(x - 1, 0, TILE - 1), sy = Mathf.Clamp(y - 1, 0, TILE - 1);
			gpu.SetPixel(c.X + x, c.Y + y, Image.GetPixel(sx, sy));
		} }
		Texture = ImageTexture.CreateFromImage(gpu);
	}
	static Image gpu;
	// (testing: every picture in the atlas by name, with a fingerprint of its pixels, and which cut-shape picture each
	// face uses; to check that a change to how the pictures are stored leaves every picture exactly as it was)
	public static void Dump(string path)
	{
		var lines = new List<string>();
		for (int t = 0; t < NT; t++)
		{
			var px = Image.GetRegion(new Rect2I(t * TILE, 0, TILE, TILE)).GetData();
			ulong h = 1469598103934665603; foreach (byte b in px) h = (h ^ b) * 1099511628211;
			lines.Add($"{names[t]} {h:x16}");
		}
		foreach (var ((tile, mask), t) in shapeTiles) lines.Add($"shape {names[tile]} {mask} -> {names[t]}");
		foreach (var (tile, t) in slabSides) lines.Add($"slab_side {names[tile]} -> {names[t]}");
		lines.Sort(StringComparer.Ordinal);
		System.IO.File.WriteAllLines(path, lines);
	}
	// (testing: the old loose cut-shape files, from a backup folder, against what the sheets are cut into; pixels you can see only)
	public static void SheetCheck(string dir)
	{
		var cut = CutSheets(); int same = 0, missing = 0; var bad = new List<string>();
		foreach (string sub in new[] { dir, dir + "/shapes" })
		{
			if (!System.IO.Directory.Exists(sub)) continue;
			foreach (var f in System.IO.Directory.GetFiles(sub, "*.png"))
			{
				string name = System.IO.Path.GetFileNameWithoutExtension(f).ToLower();
				var old = Image.LoadFromFile(f); old.Convert(Image.Format.Rgba8);
				if (!cut.TryGetValue(name, out var img)) { missing++; bad.Add(name + " (not on a sheet)"); continue; }
				int diff = 0;
				for (int y = 0; y < TILE; y++) for (int x = 0; x < TILE; x++)
				{
					Color a = old.GetPixel(x, y), b = img.GetPixel(x, y);
					if ((a.A8 == 0) != (b.A8 == 0) || (a.A8 > 0 && a.ToRgba32() != b.ToRgba32())) diff++;
				}
				if (diff == 0) same++; else bad.Add($"{name} ({diff} pixels)");
			}
		}
		// and each sheet's centre against the loose full picture (while there still is one)
		foreach (var b in sheetBases)
		{
			string p = $"res://textures/blocks/{b}.png";
			if (!ResourceLoader.Exists(p) || LoadImage(p) is not { } full) continue;
			if (!cut.TryGetValue(b, out var c)) { bad.Add(b + " (no centre on its sheet)"); continue; }
			int diff = 0;
			for (int y = 0; y < TILE; y++) for (int x = 0; x < TILE; x++)
			{
				Color a = full.GetPixel(x, y), d = c.GetPixel(x, y);
				if ((a.A8 == 0) != (d.A8 == 0) || (a.A8 > 0 && a.ToRgba32() != d.ToRgba32())) diff++;
			}
			if (diff == 0) same++; else bad.Add($"{b} centre ({diff} pixels)");
		}
		GD.Print($"SHEETCHECK same {same}, different or missing {bad.Count}");
		foreach (var b in bad.Take(20)) GD.Print("  " + b);
	}
	const int SawFps = 10;   // (the saw blade's frames: see SawTile)
	static double sawTime; static int sawFrame;
	public static void SpinSaw(double dt)
	{
		if (gpu == null || Texture == null) return;
		sawTime += dt;
		int f = (int)(sawTime * SawFps) % SawFrames.Length;
		if (f == sawFrame) return;
		sawFrame = f;
		gpu.BlitRect(gpu, new Rect2I(Cell(SawFrames[f]), new Vector2I(PW, PW)), Cell(SawTile));
		Texture.Update(gpu);
	}
	// (self-test: stepping on by one frame's time shows the other frame, then the first again)
	public static bool SawSpins()
	{
		byte[] Px(int t) => gpu.GetRegion(new Rect2I(Cell(t), new Vector2I(PW, PW))).GetData();
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
		if (b.Shape == "snow") return new[] { new[] { 0, 16, 0, 16, 0, 2 * b.Layers } };   // snow: as deep as its layers
		if (!IsCubeIcon(id)) return null;
		return b.Shape == "torch" ? new[] { new[] { 6, 10, 6, 10, 0, 16 } } : new[] { new[] { 0, 16, 0, 16, 0, 16 } };
	}
	public static bool Is3DIcon(int id) => IconBoxes(id) != null;
	// a slab's, stairs'... face in its icon or model, facing (dx, dy, dz): its cut-shape picture. The quarters that show are
	// the spots in the same layer along that axis that are filled and not covered that way by another spot of the shape;
	// each is the quarter of the picture its face shows (col 0 left, row 0 top). Same rule as the world's pieces.
	static int CutTile(int id, int tile, int[] box, int dx, int dy, int dz)
	{
		var b = Blocks.Get(id);
		if (b?.Form == null || !IconMasks.TryGetValue(b.Form, out int mask)) return tile;
		bool Filled(int x, int y, int z) => x >= 0 && x < 2 && y >= 0 && y < 2 && z >= 0 && z < 2 && (mask & (1 << (x + y * 2 + z * 4))) != 0;
		int sx = box[0] / 8, sz = box[2] / 8, sy = box[4] / 8, q = 0;
		for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++)
		{
			int x = dx != 0 ? sx : i, y = dy != 0 ? sy : dx != 0 ? i : j, z = dz != 0 ? sz : j;
			if (!Filled(x, y, z) || Filled(x + dx, y + dy, z + dz)) continue;
			int col = dy != 0 ? x : dz > 0 ? x : dz < 0 ? 1 - x : dx > 0 ? 1 - z : z, row = dy != 0 ? z : 1 - y;
			q |= Quarter(col, row);
		}
		return ShapeTile(tile, q);
	}
	// a thing's face pictures: top, front, side, bottom (the kiln's front is its opening)
	static int[] IconTiles(BlockDef b) => new[] { b.T[1], b.T.Length > 3 ? b.T[3] : b.T[0], b.T[0], b.T[2] };
	// The 3D icon: seen from above at the front-left corner, in a 4 x TILE square (each texture pixel is 2 icon pixels
	// across). Each icon pixel takes its colour straight from the nearest face under it, so it stays crisp.
	// (The same drawing as the web edition's drawBoxIcon.)
	public static Image BoxIcon(int id)
	{
		int R = TILE * 4, T = TILE; float k = 2;
		var boxes = IconBoxes(id); var tiles = IconTiles(Blocks.Get(id));
		bool saw = Blocks.Plain(id) == Blocks.Cutter;
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
					int[] bt = { CutTile(id, tiles[0], bx, 0, 1, 0), CutTile(id, tiles[1], bx, 0, 0, 1), CutTile(id, tiles[2], bx, 1, 0, 0) };   // (top, front, side)
					void Try(int f, float A, float B, float D, int u, int v)
					{
						float depth = A + B - D;
						if (depth <= best) return;
						var c = Image.GetPixel(bt[f] * T + u, v);
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
				if (saw)
				{   // the Cutter's saw blade: the top half of its picture, standing up across the middle (x 8) above the base, as in the world
					float B = T / 2f - X, D = Y - (T / 2f + B) / 2, depth = T / 2f + B - D;
					if (B >= 0 && B < T && D >= 0 && D < T / 2f && depth > best)
					{
						var c = Image.GetPixel(SawTile * T + (int)MathF.Floor(B), (int)MathF.Floor(D));
						if (c.A >= 0.5f) { best = depth; col = new Color(c.R * 0.8f, c.G * 0.8f, c.B * 0.8f, 1); }
					}
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
		// its texture: the pictures its faces use (the 4 face pictures, cut-shape ones, the Cutter's saw blade) side by side
		const int Slots = 32;
		var strip = Image.CreateEmpty(T * Slots, T, false, Image.Format.Rgba8);
		var slotOfTile = new Dictionary<int, int>();
		int Slot(int tile)
		{
			if (slotOfTile.TryGetValue(tile, out int sl)) return sl;
			sl = slotOfTile.Count; slotOfTile[tile] = sl;
			strip.BlitRect(Image, new Rect2I(tile * T, 0, T, T), new Vector2I(sl * T, 0));
			return sl;
		}
		var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
		// one face: corners p ({A, h, B}), their texture pixels q ({u, v}, v down from the picture's top), picture `tile`, shade
		void Face(float[][] p, float[][] q, int tile, float s)
		{
			int f = Slot(tile);
			foreach (int i in new[] { 0, 1, 2, 0, 2, 3 })
			{
				st.SetColor(new Color(s, s, s));
				st.SetUV(new Vector2((f * T + q[i][0]) / (T * (float)Slots), q[i][1] / T));
				st.AddVertex(new Vector3(p[i][0] / T - 0.5f, p[i][1] / T - 0.5f, p[i][2] / T - 0.5f));
			}
		}
		float[] P(float a, float h, float b) => new[] { a, h, b };
		float[] Q(float u, float v) => new[] { u, v };
		foreach (var bx in IconBoxes(id))
		{
			float a0 = bx[0], a1 = bx[1], b0 = bx[2], b1 = bx[3], h0 = bx[4], h1 = bx[5], d0 = T - h1, d1 = T - h0;
			Face(new[] { P(a0, h1, b0), P(a0, h1, b1), P(a1, h1, b1), P(a1, h1, b0) }, new[] { Q(a0, b0), Q(a0, b1), Q(a1, b1), Q(a1, b0) }, CutTile(id, tiles[0], bx, 0, 1, 0), 1);       // top
			Face(new[] { P(a0, h0, b1), P(a0, h0, b0), P(a1, h0, b0), P(a1, h0, b1) }, new[] { Q(a0, b1), Q(a0, b0), Q(a1, b0), Q(a1, b1) }, CutTile(id, tiles[3], bx, 0, -1, 0), 0.5f);    // bottom
			Face(new[] { P(a0, h1, b1), P(a0, h0, b1), P(a1, h0, b1), P(a1, h1, b1) }, new[] { Q(a0, d0), Q(a0, d1), Q(a1, d1), Q(a1, d0) }, CutTile(id, tiles[1], bx, 0, 0, 1), 0.8f);    // front
			Face(new[] { P(a1, h1, b0), P(a1, h0, b0), P(a0, h0, b0), P(a0, h1, b0) }, new[] { Q(T - a1, d0), Q(T - a1, d1), Q(T - a0, d1), Q(T - a0, d0) }, CutTile(id, tiles[2], bx, 0, 0, -1), 0.8f);   // back
			Face(new[] { P(a1, h1, b1), P(a1, h0, b1), P(a1, h0, b0), P(a1, h1, b0) }, new[] { Q(T - b1, d0), Q(T - b1, d1), Q(T - b0, d1), Q(T - b0, d0) }, CutTile(id, tiles[2], bx, 1, 0, 0), 0.62f);  // right side
			Face(new[] { P(a0, h1, b0), P(a0, h0, b0), P(a0, h0, b1), P(a0, h1, b1) }, new[] { Q(b0, d0), Q(b0, d1), Q(b1, d1), Q(b1, d0) }, CutTile(id, tiles[2], bx, -1, 0, 0), 0.62f);   // left side
		}
		if (Blocks.Plain(id) == Blocks.Cutter)
		{   // the saw blade: the top half of its picture, standing up across the middle (x 8) above the base, seen from both sides
			float mid = T / 2f;
			Face(new[] { P(mid, T, 0), P(mid, mid, 0), P(mid, mid, T), P(mid, T, T) }, new[] { Q(0, 0), Q(0, mid), Q(T, mid), Q(T, 0) }, SawTile, 0.8f);
			Face(new[] { P(mid, T, T), P(mid, mid, T), P(mid, mid, 0), P(mid, T, 0) }, new[] { Q(T, 0), Q(T, mid), Q(0, mid), Q(0, 0) }, SawTile, 0.8f);
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
