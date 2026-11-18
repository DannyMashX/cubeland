using System;
using System.Collections.Generic;
using Godot;

namespace Cubeland;

// Health (survival only): 5 hearts, each split in quarters (0-20). Eating heals; falling and mobs hurt.
// Gloom: staying in the dark too long blackens your hearts, a quarter at a time, from the left of the bar.
// Blackened quarters count as empty: if the gloom reaches all the health you have, you die. In the light it fades again.
public partial class Main
{
	const int HEARTS = 5, MaxHealth = HEARTS * 4;
	const float GloomStart = 60, GloomStep = 90, GloomFade = 120, GloomDark = 0.15f;
	int health = MaxHealth, gloom; float darkTime, lightTime, gloomCheck; bool inDark, dead, gloomDeath;
	float hurtFlash;
	Vector3 spawnPoint;
	// for a moment after your health changes, the part that changed is a different colour: red = lost, green = came back
	int changeFrom; string changeColor = "red"; float changeTime;
	TextureRect heartsRect; Image heartImg, heartsCanvas; ImageTexture heartsTex; int HP = 10, HS = 9;
	Image whiteFillOnly, gloomFillOnly;
	ColorRect hurtOverlay, waterOverlay; Control deathScreen; Label deathNote;

	void ShowChange(int from, string color)
	{
		// hit again while still red: keep the red reaching back to where it started
		if (changeTime > 0 && changeColor == color && color == "red") from = Math.Max(from, changeFrom);
		if (changeTime > 0 && changeColor == color && color == "green") from = Math.Min(from, changeFrom);
		changeFrom = from; changeColor = color; changeTime = 0.8f;
	}
	// amount is in quarter hearts. from = what hit you (knocks you away from it)
	public void HurtPlayer(int amount, Vector3? from = null)
	{
		if (!inv.Survival || dead) return;
		if (Input.MouseMode != LockMode && !invOpen) return;   // nothing hurts you while paused
		if (from is { } f)
		{
			var d = new Vector2(player.Pos.X - f.X, player.Pos.Z - f.Z); float l = d.Length(); if (l == 0) l = 1;
			player.Vel.X += d.X / l * 7; player.Vel.Z += d.Y / l * 7; player.Vel.Y = Math.Max(player.Vel.Y, 5);
		}
		ShowChange(health, "red");
		health = Math.Max(0, health - Math.Max(1, amount));
		hurtFlash = 0.35f;
		DrawHearts();
		if (health <= gloom) Die();   // (blackened quarters count as empty)
	}
	// Health never comes back on its own: you eat food (right-click while holding it) to heal
	bool Eat()
	{
		int food = Blocks.Get(inv.Held)?.Food ?? 0;
		if (!inv.Survival || food == 0 || dead || health >= MaxHealth) return false;
		ShowChange(health, "green");
		health = Math.Min(MaxHealth, health + food);
		inv.UseOne(); DrawHearts();
		return true;
	}
	// Fire Essence (a rare Grr drop): using one clears all your gloom at once
	bool UseFireEssence()
	{
		if (!inv.Survival || dead || inv.Held != Blocks.FireEssence || gloom == 0) return false;
		gloom = 0; lightTime = 0;
		inv.UseOne(); DrawHearts();
		ShowNote("The gloom lifts");
		return true;
	}
	// how bright it is where your head is: sky light x time of day, or torch light, whichever is brighter
	float LightHere()
	{
		int x = (int)MathF.Floor(player.Pos.X), y = (int)MathF.Floor(player.Pos.Y + 1.5f), z = (int)MathF.Floor(player.Pos.Z);
		return Math.Max(sky.SkyDay * world.SkyExposure(x, y, z, null), World.LightCurve(world.LightAt(x, y, z)));
	}
	void UpdateHealth(float dt)
	{
		if (!inv.Survival || dead) return;
		if (changeTime > 0 && (changeTime -= dt) <= 0) DrawHearts();   // the red/green part turns back to normal
		if (player.Pos.Y < -20) HurtPlayer(99);   // fell out of the world
		if (Input.MouseMode != LockMode && !invOpen) return;   // (not while paused)
		if ((gloomCheck -= dt) <= 0) { gloomCheck = 0.5f; inDark = LightHere() < GloomDark; }
		if (inDark)
		{
			lightTime = 0;
			if ((darkTime += dt) >= GloomStart)
			{
				darkTime -= GloomStep;   // (the first quarter at GloomStart, then one every GloomStep)
				gloom = Math.Min(MaxHealth, gloom + 1);
				DrawHearts();
				if (gloom >= health) { gloomDeath = true; Die(); }
			}
		}
		else
		{
			darkTime = 0;
			if (gloom == 0) { lightTime = 0; return; }
			if ((lightTime += dt) >= GloomFade) { lightTime -= GloomFade; gloom--; DrawHearts(); }
		}
	}
	void Die()
	{
		dead = true;
		// everything you carried falls where you died (in a friend's world items live in their game, so you keep yours)
		if (!netClient) for (int i = 0; i < Inventory.Slots; i++) { for (int n = inv.Count[i]; n > 0; n--) drops.Add(inv.Id[i], player.Pos.X, player.Pos.Y + 1, player.Pos.Z); inv.Id[i] = 0; inv.Count[i] = 0; }
		inv.Changed?.Invoke(); SelectSlot(inv.Selected);
		deathNote.Text = (gloomDeath ? "The gloom took you. " : "") + (netClient ? "You kept your things." : "Your things are where you fell.");
		gloomDeath = false;
		if (invOpen) CloseInventory();
		deathScreen.Visible = true;
		Input.MouseMode = Input.MouseModeEnum.Visible;
	}
	void Respawn()
	{
		// back at the world's starting spot (moved up if something was built there since)
		int x = (int)MathF.Floor(spawnPoint.X), z = (int)MathF.Floor(spawnPoint.Z), y = (int)MathF.Ceiling(spawnPoint.Y);
		while (y < World.WY - 2 && (world.Solid(x, y, z) || world.Solid(x, y + 1, z))) y++;
		player.Pos = new Vector3(spawnPoint.X, y, spawnPoint.Z); player.Vel = Vector3.Zero;
		health = MaxHealth; dead = false; gloom = 0; darkTime = 0; lightTime = 0;
		deathScreen.Visible = false;
		DrawHearts();
		Input.MouseMode = LockMode;
	}

	// ---- the hearts: your pictures in textures/gui/heart_atlas.png ----
	// The atlas is a 10x10 grid of hearts with a gap between them. Each heart picture is [column, row] in it.
	// White (normal health): 2nd row, columns 4-7 = full, 3/4, 1/2, 1/4. Red: 1st row and green: 4th row, columns 0-3.
	// Gloom: 1st row, columns 4-7. Part gloom, part white: columns 8-9 of the first 3 rows (GloomMix). Empty: bottom-right.
	static (int, int) HeartCell(string kind, int q) => kind switch
	{
		"white" => (8 - q, 1), "red" => (4 - q, 0), "green" => (4 - q, 3), "gloom" => (8 - q, 0), _ => (9, 9),
	};
	static readonly Dictionary<(int, int), (int, int)> GloomMix = new()
	{ [(1, 3)] = (8, 1), [(1, 2)] = (8, 2), [(1, 1)] = (9, 1), [(2, 2)] = (9, 0), [(2, 1)] = (9, 2), [(3, 1)] = (8, 0) };
	readonly Dictionary<(int, int), Vector2I> heartSpots = new();
	// where heart [column, row] really is: a heart drawn a pixel or two off the grid is found by following its pixels
	Vector2I HeartAt(int cx, int cy)
	{
		if (heartSpots.TryGetValue((cx, cy), out var have)) return have;
		var spot = new Vector2I(cx * HP, cy * HP);
		int W = heartImg.GetWidth(), H = heartImg.GetHeight();
		bool Op(int x, int y) => x >= 0 && y >= 0 && x < W && y < H && heartImg.GetPixel(x, y).A > 0;
		int sx = -1, sy = -1; float best = 1e9f, mx = cx * HP + HS / 2f, my = cy * HP + HS / 2f;
		for (int y = cy * HP - 2; y < cy * HP + HS + 2; y++) for (int x = cx * HP - 2; x < cx * HP + HS + 2; x++)
		{
			float d = (x - mx) * (x - mx) + (y - my) * (y - my);
			if (d < best && Op(x, y)) { best = d; sx = x; sy = y; }
		}
		if (sx >= 0)
		{
			int x0 = sx, y0 = sy, x1 = sx, y1 = sy;
			var seen = new HashSet<int> { sy * W + sx }; var todo = new Stack<(int, int)>(); todo.Push((sx, sy));
			while (todo.Count > 0)
			{
				var (x, y) = todo.Pop();
				x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
				foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
					if (Op(nx, ny) && seen.Add(ny * W + nx)) todo.Push((nx, ny));
			}
			if (x1 - x0 < HS + 2 && y1 - y0 < HS + 2) spot = new Vector2I(x0, y0);   // (if it ran into a neighbour, stick to the grid)
		}
		return heartSpots[(cx, cy)] = spot;
	}
	void DrawHeart(Image g, (int, int) cell, int x) { var s = HeartAt(cell.Item1, cell.Item2); g.BlendRect(heartImg, new Rect2I(s, new Vector2I(HS, HS)), new Vector2I(x, 0)); }
	void BuildHearts()
	{
		var tex = ResourceLoader.Load<Texture2D>("res://textures/gui/heart_atlas.png");
		heartsRect = new TextureRect { TextureFilter = CanvasItem.TextureFilterEnum.Nearest, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
		heartsHolder.AddChild(heartsRect);
		if (tex == null || !inv.Survival) return;
		heartImg = tex.GetImage(); if (heartImg.IsCompressed()) heartImg.Decompress(); heartImg.Convert(Image.Format.Rgba8);
		float hp = heartImg.GetWidth() / 9.9f; HS = (int)Math.Round(hp * 0.9f); HP = (int)Math.Round(hp);
		heartsCanvas = Image.CreateEmpty(HEARTS * HP - (HP - HS), HS, false, Image.Format.Rgba8);
		Image FillOnly(string kind)
		{
			var c = Image.CreateEmpty(4 * HP, HS, false, Image.Format.Rgba8);
			for (int q = 1; q <= 4; q++) DrawHeart(c, HeartCell(kind, q), (q - 1) * HP);
			for (int y = 0; y < HS; y++) for (int x = 0; x < c.GetWidth(); x++) { var p = c.GetPixel(x, y); if (p.R8 < 20 && p.G8 < 20 && p.B8 < 20) c.SetPixel(x, y, new Color(0, 0, 0, 0)); }
			return c;
		}
		whiteFillOnly = FillOnly("white"); gloomFillOnly = FillOnly("gloom");
		heartsTex = ImageTexture.CreateFromImage(heartsCanvas);
		heartsRect.Texture = heartsTex; heartsRect.Visible = true;
		// sized as if the atlas were the first 99-pixel one, a whole number of screen pixels for each of its pixels
		float scale = Math.Max(1, (int)Math.Round(18.0 / HS));
		heartsRect.CustomMinimumSize = new Vector2(heartsCanvas.GetWidth(), HS) * scale;
		DrawHearts();
	}
	void DrawHearts()
	{
		if (heartsCanvas == null) return;
		var g = heartsCanvas; g.Fill(new Color(0, 0, 0, 0));
		bool changing = changeTime > 0;
		int low = changing ? Math.Min(health, changeFrom) : health, high = changing ? Math.Max(health, changeFrom) : health;
		void Fill(Image img, int q, int i) => g.BlendRect(img, new Rect2I((q - 1) * HP, 0, HS, HS), new Vector2I(i * HP, 0));
		for (int i = 0; i < HEARTS; i++)
		{
			int w = Math.Clamp(low - i * 4, 0, 4), all = Math.Clamp(high - i * 4, 0, 4);
			int gq = Math.Min(all, Math.Clamp(gloom - i * 4, 0, 4));
			if (all == 0) DrawHeart(g, HeartCell("empty", 0), i * HP);
			else if (all == w && gq == 0) DrawHeart(g, HeartCell("white", w), i * HP);
			else if (all == w && gq == w) DrawHeart(g, HeartCell("gloom", gq), i * HP);
			else if (all == w && GloomMix.TryGetValue((gq, w - gq), out var mix)) DrawHeart(g, mix, i * HP);
			else
			{
				DrawHeart(g, HeartCell(all == w ? "white" : changeColor, all), i * HP);   // the whole filled part in red/green...
				if (w > 0 && all != w) Fill(whiteFillOnly, w, i);                         // ...with the part you still have in white on top
				if (gq > 0) Fill(gloomFillOnly, gq, i);                                    // ...and the blackened part on top of that
			}
		}
		heartsTex.Update(g);
	}
	Control heartsHolder;
	void BuildLifeUi()
	{
		hurtOverlay = new ColorRect { Color = new Color(200 / 255f, 0, 0, 0.35f), MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) };
		hurtOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		waterOverlay = new ColorRect { Color = new Color(20 / 255f, 70 / 255f, 190 / 255f, 0.28f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
		waterOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		uiLayer.AddChild(waterOverlay); uiLayer.MoveChild(waterOverlay, 0);
		uiLayer.AddChild(hurtOverlay); uiLayer.MoveChild(hurtOverlay, 1);
		// death screen
		var ds = new ColorRect { Color = new Color(120 / 255f, 0, 0, 0.5f), Visible = false };
		ds.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		var center = new CenterContainer(); center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); ds.AddChild(center);
		var box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; box.AddThemeConstantOverride("separation", 14); center.AddChild(box);
		var title = Text("You died!", 40); title.HorizontalAlignment = HorizontalAlignment.Center; box.AddChild(title);
		deathNote = Text("", 16); deathNote.HorizontalAlignment = HorizontalAlignment.Center; box.AddChild(deathNote);
		var b = new Button { Text = "Respawn", CustomMinimumSize = new Vector2(220, 44) }; b.Pressed += Respawn; box.AddChild(b);
		uiLayer.AddChild(ds); deathScreen = ds;
		BuildHearts();
	}
	void TickLifeUi(float dt)
	{
		if (hurtFlash > 0 || hurtOverlay.Modulate.A > 0) { hurtFlash = Math.Max(0, hurtFlash - dt); hurtOverlay.Modulate = new Color(1, 1, 1, Math.Min(1, hurtFlash / 0.2f)); }
	}
}
