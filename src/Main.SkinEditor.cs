using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace Cubeland;

// The skin editor (Skin, on the main menu and the pause screen), the same as the web version's:
// left, the whole 64x64 picture, big, with each part's faces outlined (the name of the one under the mouse shows below
// it); right, your figure in 3D: drag around it to turn it, or paint straight onto it. Tools: pencil, eraser, fill (fills
// one face), colour picker, and mirror (paints the other side too: left arm <-> right arm, and so on).
// Everything is drawn with your gui_atlas.png: the menu box, the buttons, the button under the mouse, and the bottom right
// picture for whichever tool and colour are picked.
public partial class Main
{
	const int SkS = PlayerSkin.Size;
	Control skinPanel;
	Image skEdit; ImageTexture skEditTex, skChecker;
	Control skSheet; Label skWhere, skHex, skNote; SubViewport skView; Camera3D skCam; Mob skFig;
	ColorRect skSwatch; GridContainer skPalette; Button skMirrorBtn;
	readonly Dictionary<string, Button> skToolBtns = new();
	string skTool = "pencil"; bool skMirror; Color skColour = new(0x3a6ea5ff);
	readonly List<Image> skUndo = new(), skRedo = new(); readonly List<Color> skRecent = new();
	(int x, int y, SkFace f)? skHover; float skTurn = MathF.PI + 0.5f, skTilt = 0.12f;
	bool skPainting; (int x, int y)? skLast; Vector2? skDrag;
	record SkFace(string Part, string Face, int X0, int Y0, int X1, int Y1);
	readonly List<SkFace> skFaces = new();
	static readonly string[] SkPalette = { "000000", "3f3f3f", "7f7f7f", "bfbfbf", "ffffff", "5a3a22", "8b5a2b", "c68642", "f1c27d", "ffdbac",
		"b3261e", "e8743b", "f2c12e", "4caf50", "1f7a4a", "3a6ea5", "2b3fae", "7b3fb0", "d04f9a", "1c1c2e" };
	static readonly Dictionary<string, string> SkPartNames = new() { ["head"] = "Head", ["body"] = "Body", ["arm_left"] = "Left upper arm", ["arm_left_lower"] = "Left forearm",
		["arm_right"] = "Right upper arm", ["arm_right_lower"] = "Right forearm", ["leg_left"] = "Left thigh", ["leg_left_lower"] = "Left shin", ["leg_right"] = "Right thigh", ["leg_right_lower"] = "Right shin" };
	static readonly Dictionary<string, string> SkFaceNames = new() { ["north"] = "front", ["south"] = "back", ["east"] = "right side", ["west"] = "left side", ["up"] = "top", ["down"] = "bottom" };

	// ---------------- building it ----------------
	void BuildSkinEditor()
	{
		skinPanel = Backdrop(0.55f);
		skinPanel.MouseFilter = Control.MouseFilterEnum.Stop;
		var c = new CenterContainer(); c.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); skinPanel.AddChild(c);
		var p = new PanelContainer(); p.AddThemeStyleboxOverride("panel", Skin.Panel(18, 14)); c.AddChild(p);
		var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 10); p.AddChild(col);
		col.AddChild(Text("Your skin", 20));
		var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 16); col.AddChild(row);

		// left: the picture
		var left = new VBoxContainer(); left.AddThemeConstantOverride("separation", 6); row.AddChild(left);
		int big = Math.Clamp((int)(GetViewport().GetVisibleRect().Size.Y - 250) / SkS * SkS, 256, 448);
		var frame = new PanelContainer(); frame.AddThemeStyleboxOverride("panel", Skin.Button(false, 4)); left.AddChild(frame);
		skSheet = new Control { CustomMinimumSize = new Vector2(big, big), MouseDefaultCursorShape = Control.CursorShape.Cross, TextureFilter = CanvasItem.TextureFilterEnum.Nearest };
		skSheet.Draw += DrawSkinSheet;
		skSheet.GuiInput += SheetInput;
		skSheet.MouseExited += () => { if (!skPainting) SkShowWhere(null); };
		frame.AddChild(skSheet);
		skWhere = Text("", 13, 0.85f); skWhere.CustomMinimumSize = new Vector2(0, 16); left.AddChild(skWhere);

		// right: the figure, then the tools
		var right = new VBoxContainer(); right.AddThemeConstantOverride("separation", 8); row.AddChild(right);
		var vframe = new PanelContainer(); vframe.AddThemeStyleboxOverride("panel", Skin.Button(false, 4)); vframe.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter; right.AddChild(vframe);
		int vh = Math.Min(320, big - 120 > 200 ? big - 120 : 200), vw = vh * 3 / 4;
		var vc = new SubViewportContainer { Stretch = true, CustomMinimumSize = new Vector2(vw, vh), MouseDefaultCursorShape = Control.CursorShape.Cross };
		vframe.AddChild(vc);
		skView = new SubViewport { OwnWorld3D = true, TransparentBg = true, Size = new Vector2I(vw, vh), RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible };
		vc.AddChild(skView);
		skCam = new Camera3D { Fov = 30, Near = 0.1f, Far = 50 }; skView.AddChild(skCam);
		vc.GuiInput += ViewInput;
		vc.MouseExited += () => { if (skDrag == null && !skPainting) SkShowWhere(null); };
		var help = Text("Drag around it to turn it · paint on it or on the picture", 12, 0.75f);
		help.AutowrapMode = TextServer.AutowrapMode.WordSmart; help.CustomMinimumSize = new Vector2(340, 0); help.HorizontalAlignment = HorizontalAlignment.Center;
		right.AddChild(help);
		Button Small(string text, Action click, int w = 96)
		{
			var b = new Button { Text = text, CustomMinimumSize = new Vector2(w, 32), FocusMode = Control.FocusModeEnum.None };
			b.Pressed += click; return b;
		}
		HBoxContainer Row(Control parent) { var r = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; r.AddThemeConstantOverride("separation", 6); parent.AddChild(r); return r; }
		var tools = Row(right);
		foreach (var (id, name) in new[] { ("pencil", "Pencil"), ("eraser", "Eraser"), ("fill", "Fill") }) tools.AddChild(skToolBtns[id] = Small(name, () => SkChoose(id)));
		tools = Row(right);
		tools.AddChild(skToolBtns["pick"] = Small("Pick", () => SkChoose("pick")));
		skMirrorBtn = Small("Mirror", () => { skMirror = !skMirror; Skin.Pickable(skMirrorBtn, skMirror); skSheet.QueueRedraw(); });
		tools.AddChild(skMirrorBtn);
		var colours = Row(right);
		var anyBtn = new Button { CustomMinimumSize = new Vector2(64, 32), FocusMode = Control.FocusModeEnum.None, TooltipText = "Any colour" };
		skSwatch = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore };
		skSwatch.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); skSwatch.OffsetLeft = skSwatch.OffsetTop = 6; skSwatch.OffsetRight = skSwatch.OffsetBottom = -6;
		anyBtn.AddChild(skSwatch);
		anyBtn.Pressed += () => OpenColourPicker(anyBtn);
		colours.AddChild(anyBtn);
		BuildColourPicker();
		skHex = Text("", 15); skHex.CustomMinimumSize = new Vector2(90, 0); colours.AddChild(skHex);
		skPalette = new GridContainer { Columns = 10 }; skPalette.AddThemeConstantOverride("h_separation", 3); skPalette.AddThemeConstantOverride("v_separation", 3);
		var pc = new CenterContainer(); pc.AddChild(skPalette); right.AddChild(pc);
		var undoRow = Row(right);
		undoRow.AddChild(Small("Undo", () => SkStep(skUndo, skRedo)));
		undoRow.AddChild(Small("Redo", () => SkStep(skRedo, skUndo)));

		// bottom: pictures in and out, and done
		var bottom = new HBoxContainer(); bottom.AddThemeConstantOverride("separation", 8); col.AddChild(bottom);
		bottom.AddChild(Small("Import picture", SkImport, 150));
		bottom.AddChild(Small("Save picture", SkExport, 140));
		bottom.AddChild(Small("Default skin", () => { var d = SkDefault(); if (d != null) { SkSnapshot(); SkLoad(d); SkNote("Back to the default skin."); } }, 140));
		skNote = Text("", 13, 0.85f); skNote.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; skNote.AutowrapMode = TextServer.AutowrapMode.WordSmart; skNote.CustomMinimumSize = new Vector2(120, 0);
		bottom.AddChild(skNote);
		bottom.AddChild(Small("Cancel", () => CloseSkinEditor(false), 100));
		bottom.AddChild(Small("Done", () => CloseSkinEditor(true), 100));
	}

	// ---------------- opening and closing ----------------
	void OpenSkinEditor()
	{
		if (skinPanel.Visible) return;
		LoadModels();
		if (mobRoot == null || !models.ContainsKey("player")) return;
		skEdit = Image.CreateEmpty(SkS, SkS, false, Image.Format.Rgba8);
		skEditTex = ImageTexture.CreateFromImage(skEdit);
		SkReadFaces();
		skUndo.Clear(); skRedo.Clear(); SkNote("");
		SkLoad(PlayerSkin.Image ?? SkDefault());
		// your figure, in your skin as it's being made
		skFig = new Mob { Type = "player", T = new MobType { Scale = 1 }, Skin = skEditTex };
		BuildMob(skFig);
		skFig.Root.GetParent().RemoveChild(skFig.Root); skView.AddChild(skFig.Root);
		for (int k = 0; k < skFig.Arms.Count; k++) skFig.Arms[k].Pivot.Rotation = new Vector3(0, 0, (k > 0 ? 1 : -1) * 0.18f);   // arms a little out, so their insides can be painted
		foreach (var m in skFig.Mats) m.SetShaderParameter("haze_amount", 0f);
		SkChoose(skTool); SkSetColour(skColour, false); Skin.Pickable(skMirrorBtn, skMirror);
		skinPanel.Visible = true;
		Input.MouseMode = Input.MouseModeEnum.Visible;
		SkPlaceCamera();
	}
	void CloseSkinEditor(bool keep)
	{
		if (!skinPanel.Visible) return;
		if (keep)
		{   // the default picture, unchanged, isn't saved as a skin of your own (so a new default still reaches you)
			var d = SkDefault();
			bool same = d != null && d.GetData().SequenceEqual(skEdit.GetData());
			PlayerSkin.Set(same ? null : skEdit);
		}
		skPop.Hide();
		skinPanel.Visible = false;
		skFig?.Root.QueueFree(); skFig = null;
	}
	static Image SkDefault()
	{
		var p = FindRes("res://textures/entity/mobs/Player/player.png");
		var img = p == null ? null : Skin.LoadImage(p);
		return img != null && img.GetWidth() == SkS && img.GetHeight() == SkS ? img : null;
	}
	void SkLoad(Image img)
	{
		skEdit.Fill(new Color(0, 0, 0, 0));
		if (img != null) { var i = (Image)img.Duplicate(); i.Convert(Image.Format.Rgba8); skEdit.BlitRect(i, new Rect2I(0, 0, SkS, SkS), Vector2I.Zero); }
		SkChanged();
	}
	void SkNote(string s) { if (skNote != null) skNote.Text = s ?? ""; }

	// ---------------- where each face of each part is on the picture (from the Player model) ----------------
	void SkReadFaces()
	{
		skFaces.Clear();
		foreach (var e in models["player"].json.GetProperty("elements").EnumerateArray())
		{
			string part = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
			if (!e.TryGetProperty("faces", out var faces) || faces.ValueKind != JsonValueKind.Object) continue;
			foreach (var f in faces.EnumerateObject())
			{
				if (!f.Value.TryGetProperty("uv", out var uv)) continue;
				var u = Vec(uv).Select(v => (int)MathF.Round(v * SkS / 16)).ToArray();
				skFaces.Add(new SkFace(part, f.Name, Math.Min(u[0], u[2]), Math.Min(u[1], u[3]), Math.Max(u[0], u[2]), Math.Max(u[1], u[3])));
			}
		}
		// the see-through background: a checkerboard, darker where no part uses the picture
		var ch = Image.CreateEmpty(SkS, SkS, false, Image.Format.Rgba8);
		for (int y = 0; y < SkS; y++) for (int x = 0; x < SkS; x++)
		{
			bool used = SkFaceAt(x, y) != null, odd = ((x + y) & 1) == 1;
			ch.SetPixel(x, y, new Color(odd ? (used ? 0x4a4a52ffu : 0x1c1c20ffu) : (used ? 0x3c3c44ffu : 0x151518ffu)));
		}
		skChecker = ImageTexture.CreateFromImage(ch);
	}
	SkFace SkFaceAt(int x, int y) => skFaces.FirstOrDefault(f => x >= f.X0 && x < f.X1 && y >= f.Y0 && y < f.Y1);
	// the same spot on the other side of the body: the matching part and face, flipped left to right
	(int, int)? SkMirrorOf(int x, int y)
	{
		var f = SkFaceAt(x, y); if (f == null) return null;
		string part = f.Part.Contains("left") ? f.Part.Replace("left", "right") : f.Part.Replace("right", "left");
		string face = f.Face == "east" ? "west" : f.Face == "west" ? "east" : f.Face;
		var g = skFaces.FirstOrDefault(o => o.Part == part && o.Face == face);
		if (g == null || g.X1 - g.X0 != f.X1 - f.X0 || g.Y1 - g.Y0 != f.Y1 - f.Y0) return null;
		return (g.X0 + (f.X1 - 1 - x), g.Y0 + (y - f.Y0));
	}

	// ---------------- painting ----------------
	void SkSetColour(Color c, bool remember)
	{
		skColour = new Color(c.R, c.G, c.B, 1);
		skSwatch.Color = skColour; skHex.Text = "#" + skColour.ToHtml(false).ToUpperInvariant();
		if (remember) { skRecent.Remove(skColour); skRecent.Insert(0, skColour); if (skRecent.Count > 10) skRecent.RemoveAt(10); }
		SkDrawPalette();
	}
	void SkDrawPalette()
	{
		foreach (var ch in skPalette.GetChildren()) ch.QueueFree();
		var all = SkPalette.Select(h => new Color(h + "ff")).ToList();
		all.AddRange(skRecent.Where(r => !all.Any(a => a.ToHtml(false) == r.ToHtml(false))));
		foreach (var c in all)
		{
			bool on = c.ToHtml(false) == skColour.ToHtml(false);
			var b = new Button { CustomMinimumSize = new Vector2(26, 26), FocusMode = Control.FocusModeEnum.None, TooltipText = "#" + c.ToHtml(false).ToUpperInvariant() };
			foreach (var s in new[] { "normal", "hover", "pressed", "hover_pressed" }) b.AddThemeStyleboxOverride(s, on ? Skin.Selected(3) : Skin.Button(s.StartsWith("hover"), 3));
			var dot = new ColorRect { Color = c, MouseFilter = Control.MouseFilterEnum.Ignore };
			dot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); dot.OffsetLeft = dot.OffsetTop = 5; dot.OffsetRight = dot.OffsetBottom = -5;
			b.AddChild(dot);
			var cc = c;
			b.Pressed += () => { SkSetColour(cc, false); if (skTool is "eraser" or "pick") SkChoose("pencil"); };
			skPalette.AddChild(b);
		}
	}
	void SkChoose(string t)
	{
		skTool = t;
		foreach (var (id, b) in skToolBtns) Skin.Pickable(b, id == t);
	}
	void SkPlot(int x, int y)
	{
		if (x < 0 || y < 0 || x >= SkS || y >= SkS) return;
		skEdit.SetPixel(x, y, skTool == "eraser" ? new Color(0, 0, 0, 0) : skColour);
	}
	void SkPaint(int x, int y)
	{
		if (x < 0 || y < 0 || x >= SkS || y >= SkS) return;
		if (skTool == "pick")
		{
			var px = skEdit.GetPixel(x, y);
			if (px.A > 0) SkSetColour(px, true);
			SkChoose("pencil");
			return;
		}
		var m = skMirror ? SkMirrorOf(x, y) : null;
		if (skTool == "fill") { SkFill(x, y); if (m is var (mx, my)) SkFill(mx, my); }
		else { SkPlot(x, y); if (m is var (mx, my)) SkPlot(mx, my); }
		SkChanged();
	}
	// fill: the spots joined to this one that are the same colour, without going past the edge of its face
	void SkFill(int x, int y)
	{
		var f = SkFaceAt(x, y) ?? new SkFace("", "", 0, 0, SkS, SkS);
		var was = skEdit.GetPixel(x, y); var now = skTool == "eraser" ? new Color(0, 0, 0, 0) : skColour;
		bool Same(Color a, Color b) => a.ToRgba32() == b.ToRgba32() || (a.A == 0 && b.A == 0);
		if (Same(was, now)) return;
		var todo = new Stack<(int, int)>(); todo.Push((x, y));
		while (todo.Count > 0)
		{
			var (cx, cy) = todo.Pop();
			if (cx < f.X0 || cy < f.Y0 || cx >= f.X1 || cy >= f.Y1 || !Same(skEdit.GetPixel(cx, cy), was)) continue;
			skEdit.SetPixel(cx, cy, now);
			todo.Push((cx + 1, cy)); todo.Push((cx - 1, cy)); todo.Push((cx, cy + 1)); todo.Push((cx, cy - 1));
		}
	}
	void SkSnapshot() { skUndo.Add((Image)skEdit.Duplicate()); if (skUndo.Count > 60) skUndo.RemoveAt(0); skRedo.Clear(); }
	void SkStep(List<Image> from, List<Image> to)
	{
		if (from.Count == 0) return;
		to.Add((Image)skEdit.Duplicate());
		var img = from[^1]; from.RemoveAt(from.Count - 1);
		skEdit.BlitRect(img, new Rect2I(0, 0, SkS, SkS), Vector2I.Zero);
		SkChanged();
	}
	void SkChanged() { skEditTex?.Update(skEdit); skSheet?.QueueRedraw(); }

	// ---------------- the big picture on the left ----------------
	void DrawSkinSheet()
	{
		if (skEdit == null) return;
		float k = skSheet.Size.X / SkS;
		var all = new Rect2(Vector2.Zero, skSheet.Size);
		if (skChecker != null) skSheet.DrawTextureRect(skChecker, all, false);
		skSheet.DrawTextureRect(skEditTex, all, false);
		foreach (var f in skFaces)
		{
			bool on = skHover?.f == f;
			skSheet.DrawRect(new Rect2(f.X0 * k + 0.5f, f.Y0 * k + 0.5f, (f.X1 - f.X0) * k - 1, (f.Y1 - f.Y0) * k - 1), new Color(1, 1, 1, on ? 0.9f : 0.22f), false, 1);
		}
		if (skHover is var (hx, hy, _))
		{
			skSheet.DrawRect(new Rect2(hx * k + 1, hy * k + 1, k - 2, k - 2), Colors.White, false, 2);
			if (skMirror && SkMirrorOf(hx, hy) is var (mx, my)) skSheet.DrawRect(new Rect2(mx * k + 1, my * k + 1, k - 2, k - 2), new Color(1, 1, 1, 0.5f), false, 2);
		}
	}
	void SkShowWhere((int x, int y)? at)
	{
		var f = at is var (x, y) ? SkFaceAt(x, y) : null;
		skHover = at is var (hx, hy) ? (hx, hy, f) : null;
		skWhere.Text = f != null ? $"{SkPartNames.GetValueOrDefault(f.Part, f.Part)} · {SkFaceNames.GetValueOrDefault(f.Face, f.Face)}" : at == null ? "" : "Not used";
		skSheet.QueueRedraw();
	}
	(int, int) SheetPos(Vector2 p) { float k = skSheet.Size.X / SkS; return ((int)MathF.Floor(p.X / k), (int)MathF.Floor(p.Y / k)); }
	void SheetInput(InputEvent e)
	{
		if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
		{
			if (mb.Pressed)
			{
				var (x, y) = SheetPos(mb.Position);
				if (skTool != "pick") SkSnapshot();
				skPainting = skTool is "pencil" or "eraser"; skLast = (x, y);
				SkPaint(x, y);
			}
			else { skPainting = false; skLast = null; }
		}
		else if (e is InputEventMouseMotion mm)
		{
			var (x, y) = SheetPos(mm.Position);
			if (skPainting && skLast is var (lx, ly))
			{   // a line from the last spot, so fast strokes don't leave gaps
				int n = Math.Max(Math.Abs(x - lx), Math.Abs(y - ly));
				for (int i = 1; i <= n; i++) SkPaint((int)MathF.Round(lx + (x - lx) * i / (float)n), (int)MathF.Round(ly + (y - ly) * i / (float)n));
				skLast = (x, y);
			}
			SkShowWhere(x >= 0 && y >= 0 && x < SkS && y < SkS ? (x, y) : null);
		}
	}

	// ---------------- your figure in 3D on the right ----------------
	void SkPlaceCamera()
	{
		if (skCam == null) return;
		float d = 4.6f, cy = (skFig?.Height ?? 2) / 2;
		skCam.Position = new Vector3(MathF.Sin(skTurn) * MathF.Cos(skTilt) * d, cy + MathF.Sin(skTilt) * d, MathF.Cos(skTurn) * MathF.Cos(skTilt) * d);
		skCam.LookAt(new Vector3(0, cy, 0));
	}
	// the picture's pixel under the mouse on the figure (or null)
	(int, int)? FigureSpot(Vector2 at)
	{
		if (skFig?.Root == null) return null;
		var from = skCam.ProjectRayOrigin(at); var dir = skCam.ProjectRayNormal(at);
		float best = float.MaxValue; (int, int)? hit = null;
		void Check(Node n)
		{
			if (n is MeshInstance3D mi && mi.Mesh is ArrayMesh am && mi.IsVisibleInTree())
			{
				var xf = mi.GlobalTransform;
				for (int s = 0; s < am.GetSurfaceCount(); s++)
				{
					var arr = am.SurfaceGetArrays(s);
					var v = (Vector3[])arr[(int)Mesh.ArrayType.Vertex]; var uv = (Vector2[])arr[(int)Mesh.ArrayType.TexUV]; var idx = (int[])arr[(int)Mesh.ArrayType.Index];
					if (uv == null || idx == null) continue;
					for (int t = 0; t + 2 < idx.Length; t += 3)
					{
						int a = idx[t], b = idx[t + 1], c = idx[t + 2];
						var r = Geometry3D.RayIntersectsTriangle(from, dir, xf * v[a], xf * v[b], xf * v[c]);
						if (r.VariantType == Variant.Type.Nil) continue;
						var p = (Vector3)r; float dist = from.DistanceTo(p);
						if (dist >= best) continue;
						best = dist;
						// where in the triangle: its picture spot, kept inside the face that was hit (its edge falls exactly between two pixels)
						var bc = Geometry3D.GetTriangleBarycentricCoords(p, xf * v[a], xf * v[b], xf * v[c]);
						var u = (uv[a] * bc.X + uv[b] * bc.Y + uv[c] * bc.Z) * SkS;
						float u0 = Math.Min(uv[a].X, Math.Min(uv[b].X, uv[c].X)) * SkS, u1 = Math.Max(uv[a].X, Math.Max(uv[b].X, uv[c].X)) * SkS;
						float v0 = Math.Min(uv[a].Y, Math.Min(uv[b].Y, uv[c].Y)) * SkS, v1 = Math.Max(uv[a].Y, Math.Max(uv[b].Y, uv[c].Y)) * SkS;
						int Clamp(float q, float lo, float hi) => Math.Min(Math.Max((int)MathF.Floor(q), (int)MathF.Round(lo)), (int)MathF.Round(hi) - 1);
						hit = (Clamp(u.X, u0, u1), Clamp(u.Y, v0, v1));
					}
				}
			}
			foreach (var ch in n.GetChildren()) Check(ch);
		}
		Check(skFig.Root);
		return hit;
	}
	void ViewInput(InputEvent e)
	{
		if (e is InputEventMouseButton mb && (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Right))
		{
			if (mb.Pressed)
			{
				var spot = mb.ButtonIndex == MouseButton.Left ? FigureSpot(mb.Position) : null;
				if (spot is var (x, y)) { if (skTool != "pick") SkSnapshot(); skPainting = true; SkPaint(x, y); }
				else skDrag = mb.Position;   // dragging around the figure turns it
			}
			else { skPainting = false; skDrag = null; }
		}
		else if (e is InputEventMouseMotion mm)
		{
			var spot = FigureSpot(mm.Position);
			if (skPainting && spot is var (x, y) && skTool is "pencil" or "eraser") SkPaint(x, y);
			else if (skDrag is Vector2 d)
			{
				skTurn -= (mm.Position.X - d.X) * 0.012f; skTilt = Math.Clamp(skTilt + (mm.Position.Y - d.Y) * 0.008f, -1.2f, 1.2f);
				skDrag = mm.Position; SkPlaceCamera();
			}
			if (spot != null) SkShowWhere(spot); else if (skHover != null) SkShowWhere(null);
		}
	}

	// ---------------- "any colour": the game's own picker ----------------
	// The square is how bright (up/down) and how strong (left/right) the colour is; the strip beside it picks the hue.
	// Drawn blocky, in your menu box and buttons, with your font (Godot's own colour picker can't be).
	PopupPanel skPop; Control skSv, skHue; LineEdit skHexIn; ImageTexture skSvTex, skHueTex;
	float skH, skSat, skVal; bool skHexTyping;
	const int SvN = 40;
	void BuildColourPicker()
	{
		skPop = new PopupPanel();
		skPop.AddThemeStyleboxOverride("panel", Skin.Panel(10, 10));
		skPop.PopupHide += () => SkSetColour(skColour, true);   // (remembered with your recent colours)
		var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 8); skPop.AddChild(box);
		var top = new HBoxContainer(); top.AddThemeConstantOverride("separation", 8); box.AddChild(top);
		Control Framed(Vector2 size, Action draw, Action<float, float> at)
		{
			var f = new PanelContainer(); f.AddThemeStyleboxOverride("panel", Skin.Button(false, 4)); top.AddChild(f);
			var c = new Control { CustomMinimumSize = size, MouseDefaultCursorShape = Control.CursorShape.Cross, TextureFilter = CanvasItem.TextureFilterEnum.Nearest };
			c.Draw += draw;
			c.GuiInput += e =>
			{
				Vector2? p = e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb ? mb.Position
					: e is InputEventMouseMotion mm && (mm.ButtonMask & MouseButtonMask.Left) != 0 ? mm.Position : null;
				if (p is Vector2 q) at(Math.Clamp(q.X / c.Size.X, 0, 1), Math.Clamp(q.Y / c.Size.Y, 0, 1));
			};
			f.AddChild(c);
			return c;
		}
		skSv = Framed(new Vector2(160, 160), DrawSv, (u, v) => { skSat = u; skVal = 1 - v; if (skTool is "eraser" or "pick") SkChoose("pencil"); SkFromHsv(); });
		skHue = Framed(new Vector2(20, 160), DrawHue, (u, v) => { skH = Math.Min(0.999f, v); SkMakeSv(); SkFromHsv(); });
		var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 6); box.AddChild(row);
		var hash = Text("#", 15); hash.VerticalAlignment = VerticalAlignment.Center; row.AddChild(hash);
		skHexIn = new LineEdit { MaxLength = 7, CustomMinimumSize = new Vector2(120, 32), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		skHexIn.AddThemeFontSizeOverride("font_size", Skin.Size(15));
		skHexIn.TextChanged += t =>
		{
			t = new string(t.Where(Uri.IsHexDigit).ToArray());
			if (t.Length == 3) t = string.Concat(t.Select(ch => $"{ch}{ch}"));
			if (t.Length != 6) return;
			skHexTyping = true; SkSetColour(new Color(t + "ff"), false); SkToHsv(); skHexTyping = false;
		};
		skHexIn.TextSubmitted += _ => skPop.Hide();
		row.AddChild(skHexIn);
		var ok = new Button { Text = "OK", CustomMinimumSize = new Vector2(56, 32), FocusMode = Control.FocusModeEnum.None };
		ok.Pressed += () => skPop.Hide();
		row.AddChild(ok);
		skinPanel.AddChild(skPop);
		// the hue strip never changes
		var hi = Image.CreateEmpty(1, SvN, false, Image.Format.Rgba8);
		for (int y = 0; y < SvN; y++) hi.SetPixel(0, y, Color.FromHsv(y / (float)SvN, 1, 1));
		skHueTex = ImageTexture.CreateFromImage(hi);
	}
	void OpenColourPicker(Control under)
	{
		SkToHsv();
		skHexIn.Text = skColour.ToHtml(false).ToUpperInvariant();
		var size = (Vector2I)skPop.GetContentsMinimumSize();
		var at = under.GetGlobalRect();   // just above the colour button (over the tools), so it never runs off the bottom
		skPop.Popup(new Rect2I((int)(at.GetCenter().X - size.X / 2f), (int)(at.Position.Y - size.Y - 6), size.X, size.Y));
	}
	void SkToHsv() { skH = skColour.H; skSat = skColour.S; skVal = skColour.V; SkMakeSv(); }
	void SkFromHsv()
	{
		SkSetColour(Color.FromHsv(skH, skSat, skVal), false);
		if (!skHexTyping) skHexIn.Text = skColour.ToHtml(false).ToUpperInvariant();
		skSv.QueueRedraw(); skHue.QueueRedraw();
	}
	void SkMakeSv()
	{
		var img = Image.CreateEmpty(SvN, SvN, false, Image.Format.Rgba8);
		for (int y = 0; y < SvN; y++) for (int x = 0; x < SvN; x++) img.SetPixel(x, y, Color.FromHsv(skH, x / (SvN - 1f), 1 - y / (SvN - 1f)));
		if (skSvTex == null) skSvTex = ImageTexture.CreateFromImage(img); else skSvTex.Update(img);
		skSv?.QueueRedraw(); skHue?.QueueRedraw();
	}
	void DrawSv()
	{
		if (skSvTex == null) return;
		skSv.DrawTextureRect(skSvTex, new Rect2(Vector2.Zero, skSv.Size), false);
		// where the colour is: a little ring of picture pixels
		float k = skSv.Size.X / SvN; int mx = (int)MathF.Round(skSat * (SvN - 1)), my = (int)MathF.Round((1 - skVal) * (SvN - 1));
		var c = skVal > 0.5f ? Colors.Black : Colors.White;
		foreach (var (dx, dy) in new[] { (-2, 0), (2, 0), (0, -2), (0, 2), (-1, -1), (1, -1), (-1, 1), (1, 1) })
			skSv.DrawRect(new Rect2((mx + dx) * k, (my + dy) * k, k, k), c);
	}
	void DrawHue()
	{
		skHue.DrawTextureRect(skHueTex, new Rect2(Vector2.Zero, skHue.Size), false);
		float k = skHue.Size.Y / SvN; int hy = Math.Min(SvN - 1, (int)(skH * SvN));
		skHue.DrawRect(new Rect2(0, hy * k, skHue.Size.X, k), Colors.White);
	}

	// ---------------- keys (while it's open) ----------------
	void SkinKey(InputEvent e)
	{
		if (e is not InputEventKey k || !k.Pressed || k.Echo) return;
		bool ctrl = k.CtrlPressed || k.MetaPressed;
		if (k.PhysicalKeycode == Key.Escape) { if (!confirm.Visible) CloseSkinEditor(false); }
		else if (ctrl && k.PhysicalKeycode == Key.Z) { if (k.ShiftPressed) SkStep(skRedo, skUndo); else SkStep(skUndo, skRedo); }
		else if (ctrl && k.PhysicalKeycode == Key.Y) SkStep(skRedo, skUndo);
		else if (!ctrl)
		{
			string t = k.PhysicalKeycode switch { Key.P or Key.B => "pencil", Key.E => "eraser", Key.F or Key.G => "fill", Key.I => "pick", _ => null };
			if (t != null) SkChoose(t);
			else if (k.PhysicalKeycode == Key.M) skMirrorBtn.EmitSignal(BaseButton.SignalName.Pressed);
		}
		GetViewport().SetInputAsHandled();
	}

	// ---------------- pictures in and out ----------------
	void SkImport()
	{
		var fd = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = "Pick a 64x64 skin picture" };
		fd.Filters = new[] { "*.png ; PNG pictures" };
		fd.FileSelected += path =>
		{
			var img = Image.LoadFromFile(path);
			string name = System.IO.Path.GetFileName(path);
			if (img == null) SkNote("That file isn't a picture the game can read.");
			else if (img.GetWidth() != SkS || img.GetHeight() != SkS) SkNote($"That picture is {img.GetWidth()}x{img.GetHeight()}. Skins are 64x64.");
			else { SkSnapshot(); SkLoad(img); SkNote($"Loaded {name}."); }
			fd.QueueFree();
		};
		fd.Canceled += fd.QueueFree;
		skinPanel.AddChild(fd); fd.PopupCentered(new Vector2I(800, 500));
	}
	void SkExport()
	{
		var fd = new FileDialog { FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = "Save your skin", CurrentFile = "cubeland-skin.png" };
		fd.Filters = new[] { "*.png ; PNG pictures" };
		fd.FileSelected += path =>
		{
			if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
			SkNote(skEdit.SavePng(path) == Error.Ok ? $"Saved {System.IO.Path.GetFileName(path)}." : "Couldn't save the picture there.");
			fd.QueueFree();
		};
		fd.Canceled += fd.QueueFree;
		skinPanel.AddChild(fd); fd.PopupCentered(new Vector2I(800, 500));
	}
}
