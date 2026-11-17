using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Cubeland;

// The game's look, from your own pictures (like the web version):
//  - the font: every letter picture in textures/font/<folder>/ (8x8, "upper" = capitals; names like q_mark.png for "?")
//  - buttons, text boxes and menu boxes: textures/gui/gui_atlas.png (top left = button, top right = button under
//    the mouse, bottom left = menu box), each cut into corners, sides and a middle that stretch to any size.
// Text sizes are whole multiples of 12 (12 = each letter pixel is 1 screen pixel), so your letters stay sharp.
public static class Skin
{
	public const int Px = 2;   // screen pixels per pixel of the button pictures
	public static FontFile Font { get; private set; }
	static StyleBox btn, btnHover, panel, sel;
	static readonly Dictionary<string, char> Names = new()
	{
		["asterisk"] = '*', ["dash"] = '-', ["hyphen"] = '-', ["minus"] = '-', ["period"] = '.', ["dot"] = '.', ["full_stop"] = '.', ["plus"] = '+',
		["q_mark"] = '?', ["question"] = '?', ["question_mark"] = '?', ["exclamation"] = '!', ["exclamation_mark"] = '!', ["ampersand"] = '&', ["and"] = '&',
		["caret"] = '^', ["comma"] = ',', ["colon"] = ':', ["semicolon"] = ';', ["slash"] = '/', ["forward_slash"] = '/', ["backslash"] = '\\',
		["apostrophe"] = '\'', ["quote"] = '"', ["quotes"] = '"', ["lparen"] = '(', ["left_paren"] = '(', ["open_paren"] = '(', ["rparen"] = ')', ["right_paren"] = ')',
		["close_paren"] = ')', ["lbracket"] = '[', ["rbracket"] = ']', ["equals"] = '=', ["underscore"] = '_', ["percent"] = '%', ["hash"] = '#', ["at"] = '@',
		["dollar"] = '$', ["less_than"] = '<', ["greater_than"] = '>', ["tilde"] = '~', ["space"] = ' ',
		["times"] = '·', ["middot"] = '·', ["middle_dot"] = '·',
		["zero"] = '0', ["one"] = '1', ["two"] = '2', ["three"] = '3', ["four"] = '4', ["five"] = '5', ["six"] = '6', ["seven"] = '7', ["eight"] = '8', ["nine"] = '9',
	};

	// a requested text size -> a whole multiple of 12, in the web version's tiers: small text (16 or less) 12,
	// headings 24, big titles 36 or 60
	public static int Size(int want) => Math.Max(want > 16 ? 2 : 1, (int)MathF.Round(want / 12f)) * 12;

	public static void Load()
	{
		if (Font != null) return;
		Font = BuildFont();
		LoadGui();
		// make them the look of every menu, button and label
		var t = ThemeDB.GetDefaultTheme();
		t.DefaultFont = Font;
		t.DefaultFontSize = 12;
		if (btn == null) return;
		var off = new StyleBoxEmpty();
		var dis = (StyleBox)btn.Duplicate(); ((StyleBoxTexture)dis).ModulateColor = new Color(1, 1, 1, 0.45f);
		foreach (var type in new[] { "Button", "OptionButton", "MenuButton" })
		{
			t.SetStylebox("normal", type, btn); t.SetStylebox("hover", type, btnHover); t.SetStylebox("pressed", type, btnHover);
			t.SetStylebox("hover_pressed", type, btnHover); t.SetStylebox("focus", type, off); t.SetStylebox("disabled", type, dis);
		}
		var field = (StyleBoxTexture)btn.Duplicate(); field.ContentMarginLeft = field.ContentMarginRight = 8;
		t.SetStylebox("normal", "LineEdit", field); t.SetStylebox("focus", "LineEdit", btnHover); t.SetStylebox("read_only", "LineEdit", dis);
		t.SetStylebox("panel", "PanelContainer", panel);
		t.SetStylebox("panel", "PopupMenu", panel);
		t.SetStylebox("panel", "PopupPanel", panel);
		t.SetStylebox("panel", "AcceptDialog", panel);
		t.SetStylebox("embedded_border", "Window", panel);
	}

	public static StyleBox Panel(int padX = 16, int padY = 14)
	{
		if (panel == null) { var f = new StyleBoxFlat { BgColor = new Color(30 / 255f, 30 / 255f, 34 / 255f, 0.92f) }; f.SetContentMarginAll(padY); return f; }
		var p = (StyleBox)panel.Duplicate();
		p.ContentMarginLeft = p.ContentMarginRight = padX; p.ContentMarginTop = p.ContentMarginBottom = padY;
		return p;
	}
	public static StyleBox Button(bool lit, int pad = 0)
	{
		var b = (StyleBox)(lit ? btnHover : btn)?.Duplicate();
		if (b == null) { var f = new StyleBoxFlat { BgColor = new Color(1, 1, 1, lit ? 0.25f : 0.12f), BorderColor = new Color(1, 1, 1, 0.5f) }; f.SetBorderWidthAll(2); b = f; }
		b.SetContentMarginAll(pad);
		return b;
	}

	// the PNGs in a folder: (lower-case name without .png, path). Works in the editor and in exported games.
	public static IEnumerable<(string name, string path)> Pictures(string folder)
	{
		var seen = new HashSet<string>();
		foreach (string f in DirAccess.GetFilesAt(folder))
		{
			string file = f.Replace(".import", "").Replace(".remap", "");
			if (!file.ToLower().EndsWith(".png") || !seen.Add(file)) continue;
			yield return (file[..^4].ToLower(), folder + "/" + file);
		}
	}
	public static Image LoadImage(string path)
	{
		var tex = ResourceLoader.Load<Texture2D>(path);
		var img = tex?.GetImage();
		if (img == null) return null;
		if (img.IsCompressed()) img.Decompress();
		img.Convert(Image.Format.Rgba8);
		return img;
	}

	// ---- gui_atlas.png ----
	static void LoadGui()
	{
		var img = LoadImage("res://textures/gui/gui_atlas.png");
		if (img == null) return;
		btn = Cut(img, 0, 0); btnHover = Cut(img, 8, 0); panel = Cut(img, 0, 8); sel = Cut(img, 8, 8);
	}
	// the bottom right picture: what's picked (the tool or colour you're using)
	public static StyleBox Selected(int pad = 0)
	{
		var b = (StyleBox)(sel ?? btnHover)?.Duplicate();
		if (b == null) { var f = new StyleBoxFlat { BgColor = new Color(0.55f, 0.8f, 1, 0.5f) }; b = f; }
		b.SetContentMarginAll(pad);
		return b;
	}
	// a button that shows when it's the picked one (tools, swatches): its picture becomes the "picked" one
	public static void Pickable(Button b, bool on)
	{
		foreach (var s in new[] { "normal", "hover", "pressed", "hover_pressed" })
		{
			if (!on) { b.RemoveThemeStyleboxOverride(s); continue; }
			var x = Selected(0); x.ContentMarginLeft = x.ContentMarginRight = 12; x.ContentMarginTop = x.ContentMarginBottom = 6;
			b.AddThemeStyleboxOverride(s, x);
		}
	}
	// the 7x7 picture at (left, top): columns/rows 0,1 = edge, 3 = middle, 5,6 = other edge (2 and 4 are gaps)
	static StyleBox Cut(Image img, int left, int top)
	{
		int[] bands = { 0, 1, 3, 5, 6 };
		var five = Image.CreateEmpty(5 * Px, 5 * Px, false, Image.Format.Rgba8);
		for (int y = 0; y < 5; y++)
			for (int x = 0; x < 5; x++)
			{
				var c = img.GetPixel(left + bands[x], top + bands[y]);
				five.FillRect(new Rect2I(x * Px, y * Px, Px, Px), c);
			}
		var s = new StyleBoxTexture { Texture = ImageTexture.CreateFromImage(five) };
		s.TextureMarginLeft = s.TextureMarginRight = s.TextureMarginTop = s.TextureMarginBottom = 2 * Px;
		s.ContentMarginLeft = s.ContentMarginRight = 12; s.ContentMarginTop = s.ContentMarginBottom = 6;
		return s;
	}

	// ---- the font: one picture per letter, packed into one texture of a bitmap font ----
	static FontFile BuildFont()
	{
		var glyphs = new List<(char ch, Image img, int x0, int w)>();
		const string root = "res://textures/font";
		if (DirAccess.DirExistsAbsolute(root))
			foreach (var folder in DirAccess.GetDirectoriesAt(root))
				foreach (var (name, path) in Pictures(root + "/" + folder))
				{
					char? ch = name.Length == 1 ? (folder == "upper" ? char.ToUpperInvariant(name[0]) : name[0]) : Names.TryGetValue(name, out var c) ? c : null;
					if (ch == null) continue;
					var img = LoadImage(path); if (img == null) continue;
					int x0 = 99, x1 = -1;
					for (int y = 0; y < img.GetHeight(); y++) for (int x = 0; x < img.GetWidth(); x++) if (img.GetPixel(x, y).A > 0.5f) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); }
					glyphs.Add((ch.Value, img, x1 < 0 ? 0 : x0, x1 < 0 ? 0 : x1 - x0 + 1));
				}
		var font = new FontFile
		{
			FixedSize = 12, FixedSizeScaleMode = TextServer.FixedSizeScaleMode.IntegerOnly,
			Antialiasing = TextServer.FontAntialiasing.None, Hinting = TextServer.Hinting.None, SubpixelPositioning = TextServer.SubpixelPositioning.Disabled,
			Fallbacks = new Godot.Collections.Array<Font> { ThemeDB.FallbackFont },   // (letters you haven't drawn)
		};
		if (glyphs.Count == 0) return font;
		var sz = new Vector2I(12, 0);
		font.SetCacheAscent(0, 12, 10); font.SetCacheDescent(0, 12, 2);
		int cell = glyphs.Max(g => Math.Max(g.img.GetWidth(), g.img.GetHeight())) + 1, cols = 16, rows = (glyphs.Count + cols - 1) / cols;
		var sheet = Image.CreateEmpty(cols * cell, rows * cell, false, Image.Format.Rgba8);
		if (!glyphs.Any(g => g.ch == ' ')) font.SetGlyphAdvance(0, 12, ' ', new Vector2(4, 0));
		for (int i = 0; i < glyphs.Count; i++)
		{
			var (ch, img, x0, w) = glyphs[i];
			int h = img.GetHeight(), cx = i % cols * cell, cy = i / cols * cell;
			if (w == 0) { font.SetGlyphAdvance(0, 12, ch, new Vector2(4, 0)); continue; }
			for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
				if (img.GetPixel(x0 + x, y).A > 0.5f) sheet.SetPixel(cx + x, cy + y, Colors.White);
			font.SetGlyphAdvance(0, 12, ch, new Vector2(w + 1, 0));   // (+1 pixel gap after each letter)
			font.SetGlyphOffset(0, sz, ch, new Vector2(0, -h));          // the bottom row stands on the line
			font.SetGlyphSize(0, sz, ch, new Vector2(w, h));
			font.SetGlyphUVRect(0, sz, ch, new Rect2(cx, cy, w, h));
			font.SetGlyphTextureIdx(0, sz, ch, 0);
		}
		font.SetTextureImage(0, sz, 0, sheet);
		return font;
	}
}
