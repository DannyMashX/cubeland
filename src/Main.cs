using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Cubeland;

// The game: makes the world, keeps its chunks' meshes up to date, and runs you around in it.
public partial class Main : Node3D
{
	// The same version as the web game (project.godot config/version), so the two always match, multiplayer included.
	public static string Version => (string)ProjectSettings.GetSetting("application/config/version", "Alpha 0.0.5.1");
	// The name shown in the window and menus ("CubeLand" + "Alpha 0.0.5.1"). project.godot's config/name stays
	// "CubeLand Alpha", because that names the folder your worlds are saved in.
	public const string Title = "CubeLand";

	World world;
	Player player;
	ChunkMesher mesher;
	Camera3D cam;
	ShaderMaterial blockMat, waterMat;
	readonly Dictionary<int, (MeshInstance3D solid, MeshInstance3D water)> chunkNodes = new();
	readonly HashSet<int> dirty = new(), soon = new();
	Task genTask; volatile float genProgress; bool ready;
	Label info, loading;
	MeshInstance3D outline;
	Inventory inv; Drops drops; Kilns kilns;
	Sky sky; Godot.Environment env;
	float camSky = 1, camSkyCheck; bool nightMusic, wasUnder;
	readonly Random rnd = new();
	static bool HasArg(string a) => OS.GetCmdlineUserArgs().Contains(a);

	public override void _Ready()
	{
		DisplayServer.WindowSetTitle($"{Title} {Version}");
		// menus and the hotbar follow your Windows display scaling, like the web edition does in a browser, but always
		// by a whole number (150% and up makes them twice as big), so every pixel of the pixel art stays the same size.
		// --uiscale=N for testing.
		var uiArg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--uiscale="));
		float uiScale = uiArg != null ? float.Parse(uiArg[10..], System.Globalization.CultureInfo.InvariantCulture)
			: MathF.Floor(DisplayServer.ScreenGetDpi() / 96f + 0.5f);
		uiScale = Mathf.Max(1, MathF.Round(uiScale) - (uiArg != null ? 0 : 1));   // (one step smaller than your display scaling: 150% -> 1x)
		GetTree().Root.ContentScaleFactor = uiScale;
		if (uiScale > 1 && DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Windowed)
		{
			// the window grows to match, so there's as much room as at 100% (but never bigger than the screen)
			var usable = DisplayServer.ScreenGetUsableRect();
			var size = DisplayServer.WindowGetSize();
			var want = new Vector2I(Math.Min((int)(size.X * uiScale), usable.Size.X - 16), Math.Min((int)(size.Y * uiScale), usable.Size.Y - 48));
			DisplayServer.WindowSetSize(want);
			DisplayServer.WindowSetPosition(usable.Position + (usable.Size - want) / 2 + new Vector2I(0, 16));
		}
		Atlas.Build();
		Skin.Load();   // your font and gui_atlas.png, for every menu and label
		blockMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://src/blocks.gdshader") };
		waterMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://src/water.gdshader"), RenderPriority = 1 };
		blockMat.SetShaderParameter("atlas", Atlas.Texture);
		waterMat.SetShaderParameter("atlas", Atlas.Texture);

		env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0x8ec9ffff) };
		AddChild(new WorldEnvironment { Environment = env });
		cam = new Camera3D { Fov = 75, Near = 0.05f, Far = 1000 };
		AddChild(cam);
		AddChild(new Sound());
		sky = new Sky(); AddChild(sky);
		var at = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--time="));   // for testing: start at a time of day
		if (at != null) sky.TimeOfDay = float.Parse(at[7..], System.Globalization.CultureInfo.InvariantCulture);
		BuildUi();
		PickWorld();   // (the last world you played, or the one you just picked; --seed=N for testing: the same world every time)
		BuildMenus();
		ApplySettings();
		DisplayServer.WindowSetTitle($"{Title} {Version}");
		world = new World(current.Seed, current.Gen);
		genTask = Task.Run(() => { world.Generate(p => genProgress = p); LoadSaveBlocks(); });
	}

	// ---- the screen: info in the corner, the crosshair, the hotbar ----
	void BuildUi()
	{
		var ui = new CanvasLayer(); AddChild(ui);
		info = new Label { Position = new Vector2(8, 6) };
		info.AddThemeColorOverride("font_color", Colors.White);
		info.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.6f));
		ui.AddChild(info);
		loading = new Label { Text = "Making the world...", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		loading.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		loading.AddThemeFontSizeOverride("font_size", 24);
		ui.AddChild(loading);
		var cross = new Control { MouseFilter = Control.MouseFilterEnum.Ignore }; cross.SetAnchorsPreset(Control.LayoutPreset.Center); ui.AddChild(cross);
		cross.AddChild(new ColorRect { Color = new Color(1, 1, 1, 0.8f), Position = new Vector2(-9, -1), Size = new Vector2(18, 2), MouseFilter = Control.MouseFilterEnum.Ignore });   // (the hidden pointer sits under it: clicks pass through)
		cross.AddChild(new ColorRect { Color = new Color(1, 1, 1, 0.8f), Position = new Vector2(-1, -9), Size = new Vector2(2, 18), MouseFilter = Control.MouseFilterEnum.Ignore });
		uiLayer = ui;
	}

	// ---- once the world is made ----
	void StartPlaying()
	{
		ready = true;
		loading.Visible = false;
		mesher = new ChunkMesher(world);
		player = new Player(world);
		inv = new Inventory(current.Survival);
		drops = new Drops(); AddChild(drops); drops.Init(world, player, inv); drops.DayTint = () => sky.DayTint; drops.HeldLight = HeldLightAt;
		kilns = new Kilns(world) { Survival = inv.Survival };
		world.Drop = Collect;
		world.NoWater = netClient;   // (in a joined game the host's game runs the water)
		drops.Client = netClient; drops.AskPickup = d => hostConn?.Send(new Dictionary<string, object> { ["t"] = "pickup", ["k"] = d.K });
		world.BlockChanged = (i, old, now, _) => { NetBlockChanged(i, now); if (Blocks.IsFurnace(old) && !Blocks.IsFurnace(now)) { if (kilnAt == i) CloseInventory(); kilns.Empty(i, Collect); } };
		BuildHud();
		BuildLifeUi();
		InitMobs();
		world.ChunksChanged = set => { lock (dirty) foreach (int c in set) dirty.Add(c); };
		world.NearbyRebuild = (x, y, z) =>
		{
			const int R = 18;
			for (int cz = Math.Max(0, (z - R) / World.CS); cz <= Math.Min(World.NCZ - 1, (z + R) / World.CS); cz++)
				for (int cx = Math.Max(0, (x - R) / World.CS); cx <= Math.Min(World.NCX - 1, (x + R) / World.CS); cx++)
					soon.Add(cz * World.NCX + cx);
		};
		for (int c = 0; c < World.NCX * World.NCZ; c++) dirty.Add(c);
		player.Pos = spawnPoint = FindSpawn();
		outline = MakeOutline(); AddChild(outline);
		ApplyPlayerSave();
		if (netClient) ConnectToHost();
		// tests go straight in; otherwise the main menu (or the Ready screen) is showing
		Input.MouseMode = (tempWorld || HasArg("--play")) && !menuOpen ? LockMode : Input.MouseModeEnum.Visible;
	}
	Vector3 FindSpawn()
	{
		// spiral out from the middle of the world until there's dry land
		int cx = World.WX / 2, cz = World.WZ / 2;
		for (int r = 0; r < World.WX / 2; r++)
			for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++)
			{
				if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
				int x = cx + dx, z = cz + dz, h = world.Heights[z * World.WX + x];
				int top = world.GetBlock(x, h, z);
				if (top != 0 && !Blocks.IsWater(top) && !Blocks.IsTorch(top)) return new Vector3(x + 0.5f, h + 1.01f, z + 0.5f);
			}
		return new Vector3(cx + 0.5f, World.WY - 2, cz + 0.5f);
	}

	public override void _Process(double delta)
	{
		float dt = (float)Math.Min(delta, 0.1);
		NetUpdate(dt);
		if (!ready)
		{
			if (genTask.IsFaulted) { loading.Text = "Couldn't make the world:\n" + genTask.Exception?.InnerException?.Message; return; }
			if (genTask.IsCompleted) StartPlaying();
			else { loading.Text = $"Making the world... {(int)(genProgress * 100)}%"; loading.Visible = !menuOpen; return; }
		}
		bool moving = Input.MouseMode == LockMode && !invOpen && !dead && (!netClient || welcomed);   // (a joined game waits for the host's blocks)
		if (!dead && (moving || (inv.Survival && invOpen && (!netClient || welcomed))))   // (survival keeps falling while the inventory is open)
		{
			var ev = player.Update(dt, !moving ? default : new Player.Keys
			{
				F = Input.IsPhysicalKeyPressed(Key.W), B = Input.IsPhysicalKeyPressed(Key.S),
				L = Input.IsPhysicalKeyPressed(Key.A), R = Input.IsPhysicalKeyPressed(Key.D),
				Jump = Input.IsPhysicalKeyPressed(Key.Space), Down = Input.IsPhysicalKeyPressed(Key.Shift),   // (Shift: sprint, dive, fly down)
				Sprint = Input.IsPhysicalKeyPressed(Key.Shift),
			});
			PlayerSounds(ev);
		}
		MouseLook();
		if (moving)
		{   // the arrow keys look around too (for laptops, or when the mouse won't)
			float turn = 2.4f * dt;
			if (Input.IsPhysicalKeyPressed(Key.Left)) player.Yaw += turn;
			if (Input.IsPhysicalKeyPressed(Key.Right)) player.Yaw -= turn;
			if (Input.IsPhysicalKeyPressed(Key.Up)) player.Pitch = Math.Min(1.55f, player.Pitch + turn * 0.75f);
			if (Input.IsPhysicalKeyPressed(Key.Down)) player.Pitch = Math.Max(-1.55f, player.Pitch - turn * 0.75f);
		}
		cam.Position = player.EyePos;
		cam.Rotation = new Vector3(player.Pitch, player.Yaw, 0);
		if (!cam.IsInsideTree()) return;   // (the scene is being swapped for another world)
		sky.Tick(dt, cam);
		UpdateSkyAndWater(dt);
		UpdateHealth(dt);
		TickLifeUi(dt);
		UpdateMobs(dt);
		UpdateHand(dt);
		if ((moving || invOpen || hosting) && !netClient)
		{   // (torches burning down, clay drying and kilns all wait while you're paused)
			world.TickTimers(dt);
			kilns.Tick(dt, i => { if (i == kilnAt) DrawKiln(); });
		}
		world.UpdateWater(dt);
		UpdateScreens(dt);
		drops.Update(dt);
		TickHud(dt);
		TickEverSparks(dt);
		BuildChunks();
		UpdateOutline();
		TestShot(dt);
		var p = player.Pos;
		info.Text = $"{Title} {Version}\n{Engine.GetFramesPerSecond()} fps\nx {p.X:0.0}  y {p.Y:0.0}  z {p.Z:0.0}" + (player.Flying ? "\nflying" : "") + (settings.Muted ? "\nSound off (M)" : "") + NetInfoLine();
	}

	// footsteps, jumps, landings and splashes; a hard landing hurts (survival)
	void PlayerSounds(Player.Events ev)
	{
		var s = Sound.I; if (s == null) return;
		if (ev.Jumped) s.Jump(player.BlockUnder());
		if (ev.Splashed) s.Splash(ev.FallSpeed);
		if (ev.Stroke) s.Swim();
		if (ev.Step) s.Step(player.BlockUnder());
		if (ev.Landed)
		{
			if (ev.Impact > 5) s.Land(player.BlockUnder(), ev.Impact);
			float fell = ev.Impact * ev.Impact / 56;   // (about how many blocks you fell)
			if (fell > 3.5f) HurtPlayer((int)MathF.Floor(fell - 3));
		}
	}
	// the sky colour behind everything, the haze, and how it all looks with your head underwater
	void UpdateSkyAndWater(float dt)
	{
		var e = cam.GlobalPosition;
		int ex = (int)MathF.Floor(e.X), ey = (int)MathF.Floor(e.Y), ez = (int)MathF.Floor(e.Z);
		// in a cave the haze darkens (less sky reaches you)
		if ((camSkyCheck -= dt) <= 0) { camSkyCheck = 0.2f; target = world.SkyExposure(ex, ey, ez, null); }
		camSky += (target - camSky) * Math.Min(1, dt * 1.5f);
		bool under = Blocks.IsWater(world.GetBlock(ex, ey, ez));
		Vector3 fog;
		if (under)
		{
			float l = Math.Max(0.15f, sky.DayTint.Y * camSky);
			fog = new Vector3(0.07f, 0.2f, 0.45f) * l;
			env.BackgroundColor = new Color(fog.X, fog.Y, fog.Z);
		}
		else
		{
			var c = sky.Now; env.BackgroundColor = c;
			fog = new Vector3(c.R, c.G, c.B) * camSky;
		}
		RenderingServer.GlobalShaderParameterSet("fog_color", fog);
		if (under != wasUnder)
		{
			wasUnder = under;
			RenderingServer.GlobalShaderParameterSet("fog_thickness", under ? 0.22f : 0.045f);
			RenderingServer.GlobalShaderParameterSet("fog_max", under ? 0.97f : 0.82f);
			cam.Far = under ? 40 : 1000;
			waterOverlay.Visible = under;
			Sound.I?.Underwater(under);
		}
		// night music comes on once it's properly dark, and goes back to day music once it's light again
		nightMusic = nightMusic ? sky.SkyDay < 0.5f : sky.SkyDay < 0.12f;
		Sound.I?.Music(nightMusic ? "night" : "day");
	}
	float target = 1;

	// for testing: `-- --shot=file.png` saves a picture of the screen once the world has loaded, then quits
	float shotTimer; bool timeArgDone, skinTested, dropsTested, farmTested;
	void TestShot(float dt)
	{
		var args = OS.GetCmdlineUserArgs();
		string arg = args.FirstOrDefault(a => a.StartsWith("--shot="));
		if (arg == null) return;
		if (args.Contains("--scene") && !sceneBuilt) BuildTestScene();
		if (args.Contains("--selftest") && !sceneBuilt) { sceneBuilt = true; SelfTest(); }
		if (args.Contains("--die") && !diedTest && inv.Survival) { diedTest = true; gloomDeath = true; HurtPlayer(99); if (!dead) { gloomDeath = true; Die(); } }
		// multiplayer tests: --host (host this world), --join=CODE (join a game)
		if (args.Contains("--host") && peer == null && !hosting && !netTried) { netTried = true; Host(); }
		var jn = args.FirstOrDefault(a => a.StartsWith("--join="));
		if (jn != null && peer == null && !netClient && !netTried) { netTried = true; Join(jn[7..]); }
		if (args.Contains("--savetest") && !savetestDone)
		{   // run twice: the first time builds a little tower and saves; the second time checks it came back
			savetestDone = true;
			int x = (int)spawnPoint.X + 3, z = (int)spawnPoint.Z, y = world.Heights[z * World.WX + x] + 1;
			bool has = world.Edits.Count > 0;
			if (!has)
			{
				for (int k = 0; k < 4; k++) world.SetBlock(x, y + k, z, Blocks.Brick == 0 ? 12 : 12);
				world.SetPiece(x + 1, y, z, 0, Blocks.Cobblestone);
				inv.SetSlot(2, 12, 0); sky.TimeOfDay = 0.3f; player.Yaw = 1.0f;
				GD.Print("savetest: built and saved = " + SaveWorld());
			}
			else
			{
				int top = world.Heights[z * World.WX + x];
				GD.Print((world.GetBlock(x, top, z) == 12 ? "PASS" : "FAIL") + " tower came back");
				GD.Print((world.PieceMap.Count > 0 ? "PASS" : "FAIL") + " piece came back");
				GD.Print((MathF.Abs(sky.TimeOfDay - 0.3f) < 0.01f ? "PASS" : "FAIL") + " time of day came back");
				GD.Print((MathF.Abs(player.Yaw - 1.0f) < 0.01f ? "PASS" : "FAIL") + " where you look came back");
				GD.Print("worlds: " + string.Join(", ", Worlds.List().Select(w => w.Name + " " + w.Seed)));
			}
		}
		if (args.Contains("--play") && menuOpen) { ShowMainMenu(false); Input.MouseMode = LockMode; }   // menus closed, as if playing
		var timeArg = args.FirstOrDefault(a => a.StartsWith("--time="));   // (test: the time of day again, after the world loaded its own)
		if (timeArg != null && !timeArgDone) { timeArgDone = true; sky.TimeOfDay = float.Parse(timeArg[7..], System.Globalization.CultureInfo.InvariantCulture); }
		var holdArg = args.FirstOrDefault(a => a.StartsWith("--hold="));   // (test: put item ID in the first hotbar slot and hold it)
		if (holdArg != null && int.TryParse(holdArg[7..], out int holdId) && inv.Id[0] != holdId) { inv.SetSlot(0, holdId); SelectSlot(0); }
		if (args.Contains("--inventory") && !invOpen && !menuOpen) OpenPanel(invPanel);   // (test: the inventory open)
		var varArg = args.FirstOrDefault(a => a.StartsWith("--variants="));   // (test: a block's other forms shown under the grid)
		if (varArg != null && invOpen && openVariants == 0) ShowVariants(int.Parse(varArg[11..]), null);
		var pressArg = args.FirstOrDefault(a => a.StartsWith("--press="));   // (test: hold this key down, e.g. --press=Left)
		if (pressArg != null && !menuOpen && !Input.IsPhysicalKeyPressed(Enum.Parse<Key>(pressArg[8..])))
			Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Enum.Parse<Key>(pressArg[8..]), Pressed = true });
		var clicksArg = args.FirstOrDefault(a => a.StartsWith("--clicks="));   // (test: click these buttons in turn, by their words, e.g. --clicks=Singleplayer,Play)
		if (clicksArg != null && clickStep < clicksArg[9..].Split(',').Length && clickWait-- <= 0)
		{   // (test: click whichever "Play" button is showing, with real-looking mouse clicks, as a person would)
			clickWait = 30;
			Button FindPlay(Node n) { foreach (var ch in n.GetChildren()) { if (ch is Button b && b.IsVisibleInTree() && b.Text.StartsWith(clicksArg[9..].Split(',')[clickStep], StringComparison.OrdinalIgnoreCase)) return b; var f = FindPlay(ch); if (f != null) return f; } return null; }
			var pb = FindPlay(this);
			if (pb != null)
			{
				var at = pb.GetGlobalRect().GetCenter() * GetWindow().ContentScaleFactor;   // (the window's pixels)
				clickStep++;
				GD.Print($"clicks: clicking \"{pb.Text}\" ({pb.GetPath()}) at {at}");
				Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
				Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
				Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = false });
			}
		}
		if (args.Contains("--mousemove") && !menuOpen && Input.MouseMode == LockMode)   // (test: the mouse moving right, through the same path a real one takes)
		{
			DisplayServer.WarpMouse(DisplayServer.WindowGetSize() / 2 + new Vector2I(8, 0));   // (the real pointer, 8 pixels right of the middle)
			if ((int)(shotTimer * 2) != (int)((shotTimer - (float)GetProcessDeltaTime()) * 2))
				GD.Print($"mousemove: yaw {player.Yaw:F2}, mouse over: {GetViewport().GuiGetHoveredControl()?.GetPath()}");
		}
		var dropArg = args.FirstOrDefault(a => a.StartsWith("--drops="));     // (test: these items lying in a row in front of you)
		if (dropArg != null && !dropsTested && !menuOpen && shotTimer > 0.5f)
		{
			dropsTested = true;
			var ids = dropArg[8..].Split(',').Select(int.Parse).ToArray();
			var fwd = new Vector3(-MathF.Sin(player.Yaw), 0, -MathF.Cos(player.Yaw)); var side = new Vector3(fwd.Z, 0, -fwd.X);
			for (int i = 0; i < ids.Length; i++)
			{
				var p = player.Pos + fwd * 2.2f + side * ((i - (ids.Length - 1) / 2f) * 0.45f);
				var d = drops.Add(ids[i], p.X, player.Pos.Y, p.Z); d.V = Vector3.Zero;
			}
		}
		var swingArg = args.FirstOrDefault(a => a.StartsWith("--swing="));   // (test: your hand frozen part way through a swing, 0-1)
		if (swingArg != null) testSwing = float.Parse(swingArg[8..], System.Globalization.CultureInfo.InvariantCulture);
		var slotArg = args.FirstOrDefault(a => a.StartsWith("--slot="));   // (test: hold what's in hotbar slot N, 1-10)
		if (slotArg != null && int.TryParse(slotArg[7..], out int slotN) && inv.Selected != slotN - 1) SelectSlot(slotN - 1);
		if (args.Contains("--skineditor") && !skinTested)
		{   // (test: the skin editor open, with a mirrored stroke, a fill, and a spot painted through the 3D figure)
			skinTested = true; OpenSkinEditor();			skMirror = true; Skin.Pickable(skMirrorBtn, true); SkChoose("pencil");
			for (int y = 20; y < 24; y++) SkPaint(4, y);
			GD.Print((skEdit.GetPixel(28, 22).ToHtml(false) == skColour.ToHtml(false) ? "PASS" : "FAIL") + " mirror paints the other arm");
			SkChoose("fill"); SkSetColour(new Color(0xf2c12eff), true); SkPaint(40, 10);
			GD.Print((skEdit.GetPixel(44, 15).ToHtml(false) == "f2c12e" && skEdit.GetPixel(36, 10).ToHtml(false) != "f2c12e" ? "PASS" : "FAIL") + " fill stays on its face");
			SkChoose("pencil"); SkPlaceCamera();
			var mid = (Vector2)skView.Size / 2; var spot = FigureSpot(mid);
			GD.Print((spot != null && SkFaceAt(spot.Value.Item1, spot.Value.Item2) != null ? "PASS" : "FAIL") + $" the figure under the middle of the view is on the picture ({spot})");
			if (PlayerSkin.Image == null)
			{   // (only when you have no skin saved: Done saves this one, your hand is made in it, then it's removed again)
				CloseSkinEditor(true); UpdateHand(0.016f);
				bool ok = PlayerSkin.Image != null && handBody?.Mats.Any(m => (Texture2D)m.GetShaderParameter("tex") == PlayerSkin.Texture) == true;
				GD.Print((ok ? "PASS" : "FAIL") + " Done saves the skin and your hand wears it");
				GD.Print((PlayerSkin.ImageFrom(PlayerSkin.DataUrl) != null ? "PASS" : "FAIL") + " the skin goes over the network and back");
				PlayerSkin.Set(null); OpenSkinEditor();
			}
			if (args.Contains("--skinpicker"))
			{   // (test: the colour picker open, after dragging in its square and typing a colour)
				GetTree().CreateTimer(0.2).Timeout += () => OpenColourPicker(skSwatch.GetParent<Control>());
				skSat = 1; skVal = 1; SkFromHsv();
				GD.Print((skColour.ToHtml(false) == "ffc12e" || skColour.R > 0.99f ? "PASS" : "FAIL") + $" the picker's square sets the colour ({skColour.ToHtml(false)})");
				skHexIn.Text = "7b3fb0"; skHexIn.EmitSignal(LineEdit.SignalName.TextChanged, "7b3fb0");
				GD.Print((skColour.ToHtml(false) == "7b3fb0" ? "PASS" : "FAIL") + " typing a colour sets it");
			}
		}
		if (args.Contains("--mobs") && !mobsShown)
		{   // a Dan and the night mob (Grr Angry until the Zombie is switched on) just in front of you, facing you
			mobsShown = true;
			if (menuOpen) ShowMainMenu(false);
			Input.MouseMode = LockMode;   // playing, so the pause screen stays away
			var ya = args.FirstOrDefault(a => a.StartsWith("--yaw="));
			if (ya != null) player.Yaw = float.Parse(ya[6..], System.Globalization.CultureInfo.InvariantCulture);
			var fwd = new Vector3(-MathF.Sin(player.Yaw), 0, -MathF.Cos(player.Yaw)); var side = new Vector3(fwd.Z, 0, -fwd.X);
			// --figures: the Player and Zombie models instead (test only: shows their jointed animations, even with the Zombie off)
			var list = args.Contains("--figures") ? new[] { ("player", -1.0f), ("zombie", 1.0f) } : new[] { ("danthechocolateman", -1.2f), (ZombieReady ? "zombie" : "grrangry", 1.4f) };
			// --mobtypes=skeleton,gloomling: these mobs instead, side by side (test)
			var mtArg = args.FirstOrDefault(a => a.StartsWith("--mobtypes="));
			if (mtArg != null) { var ts = mtArg[11..].Split(','); list = ts.Select((ty, i) => (ty, (i - (ts.Length - 1) / 2f) * 2.2f)).ToArray(); }
			foreach (var (type, off) in list)
			{
				var at = player.Pos + fwd * 4.5f + side * off; int x = (int)MathF.Floor(at.X), z = (int)MathF.Floor(at.Z), y = world.Heights[z * World.WX + x] + 1;
				Mob m;
				if (MobTypes.ContainsKey(type)) m = SpawnMob(type, at.X, y, at.Z);
				else
				{
					m = new Mob { K = ++mobCount, Type = type, T = new MobType { Name = type, Health = 10, Scale = 0.9f, Shamble = type == "zombie" }, X = at.X, Y = y, Z = at.Z, Health = 10 };
					BuildMob(m); mobList.Add(m);
				}
				m.Yaw = player.Yaw + MathF.PI + off * 0.3f; m.Timer = 99; m.GoalCheck = 99;
				if (args.Contains("--figures")) m.Yaw = player.Yaw + MathF.PI / 2;   // side on, to see how the arms and legs move
				if (type == "player") SetHeld(m, holdArg != null && int.TryParse(holdArg[7..], out int hid) ? hid : Blocks.CrudeAxe);   // and holding something
			}
			player.Pitch = -0.15f;
			var pa = args.FirstOrDefault(a => a.StartsWith("--pitch="));
			if (pa != null) player.Pitch = float.Parse(pa[8..], System.Globalization.CultureInfo.InvariantCulture);
			if (inv.Survival) { health = 13; gloom = 3; changeTime = 0; DrawHearts(); }
		}
		if (args.Contains("--farm") && !farmTested && !menuOpen)
		{   // (test: a row of soil in front of you with water at one end, tilled and planted the way you would, then 200 seconds of growing)
			farmTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			player.Yaw = 0;   // (lined up with the blocks)
			var fwd = new Vector3(0, 0, -1); var side = new Vector3(-1, 0, 0);
			var c0 = player.Pos + fwd * 3.5f; int y = world.Heights[(int)c0.Z * World.WX + (int)c0.X];
			(int x, int z) At(int k) { var p = c0 + side * (k - 4); return ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z)); }
			for (int k = -1; k < 10; k++)
			{   // the row, with a stone kerb round it so the water stays put
				var (x, z) = At(k);
				for (int d = -1; d <= 1; d++) { world.SetBlock(x, y - 1, z + d, Blocks.Dirt); world.SetBlock(x, y, z + d, Blocks.Stone); for (int u = 1; u < 4; u++) world.SetBlock(x, y + u, z + d, 0); }
				if (k >= 0 && k < 9) world.SetBlock(x, y, z, k == 0 ? Blocks.Water : Blocks.Grass);
			}
			int hoe = Blocks.Tools.First(t => Blocks.Get(t)?.Tool == "hoe");
			for (int k = 1; k < 9; k++)
			{
				var (x, z) = At(k);
				inv.SetSlot(0, hoe); SelectSlot(0); FarmUse(x, y, z, Blocks.Grass);
				inv.SetSlot(0, Blocks.GrainSeeds); FarmUse(x, y, z, world.GetBlock(x, y, z));
				if (k == 1) { inv.SetSlot(0, Blocks.BoneAsh); FarmUse(x, y + 1, z, world.GetBlock(x, y + 1, z)); }   // (fertilized by clicking the crop)
			}
			Check("hoe tills grass and seeds go in", Enumerable.Range(1, 8).All(k => { var (x, z) = At(k); return Blocks.IsSoil(world.GetBlock(x, y, z)) && world.GetBlock(x, y + 1, z) == Blocks.GrainStages[0]; }));
			for (int s = 0; s < 3; s++) world.TickTimers(1);
			string Soil() => string.Join(" ", Enumerable.Range(1, 8).Select(k => { var (x, z) = At(k); return world.GetBlock(x, y, z) + "/" + world.GetBlock(x, y + 1, z); }));
			GD.Print("farm after 3s (soil/crop): " + Soil());
			Check("soil joined to water is wet up to 5 away, the next is dry", Enumerable.Range(1, 6).All(k => { var (x, z) = At(k); return Blocks.SoilWet(world.GetBlock(x, y, z)); }) && !Blocks.SoilWet(world.GetBlock(At(7).x, y, At(7).z)));
			for (int s = 0; s < 200; s++) world.TickTimers(1);
			GD.Print("farm after 203s (soil/crop): " + Soil());
			var (fx, fz) = At(1); var (wx, wz) = At(3); var (dx, dz) = At(8);
			Check("wet + fertilized is ripe", world.GetBlock(fx, y + 1, fz) == Blocks.GrainRipe);
			Check("wet grows faster than dry", world.GetBlock(wx, y + 1, wz) > world.GetBlock(dx, y + 1, dz));
			world.SetBlock(fx, y + 1, fz, 0);
			Check("a harvest uses up one of the fertilizer's 2", world.FertLeft.GetValueOrDefault(World.Idx(fx, y, fz)) == 1);
			Check("Flour by hand, Bread in the kiln, Bread is food", Inventory.Recipes.Any(r => r.Out == Blocks.Flour) && Kilns.Smelts.Any(s => s.Out == Blocks.Bread && s.From == Blocks.Flour) && Blocks.Get(Blocks.Bread)?.Food > 0);
			inv.SetSlot(0, Blocks.Bread); player.Pitch = -0.45f;
		}
		if (args.Contains("--face") && others.Count > 0)
		{   // multiplayer test: step back from the first other player and look at them, menus closed
			var o = others.Values.First();
			var to = new Vector3(o.X, o.Y, o.Z);
			if (player.Pos.DistanceTo(to) < 3) player.Pos = to + new Vector3(0, 0, 4);
			var d = to - player.Pos;
			player.Yaw = MathF.Atan2(-d.X, -d.Z); player.Pitch = -0.1f;
			if (menuOpen) ShowMainMenu(false);
			pauseMenu.Visible = false;
		}
		string open = args.FirstOrDefault(a => a.StartsWith("--open="));
		if (open is "--open=worlds" or "--open=settings" or "--open=pause" or "--open=menu" or "--open=new")
		{
			if (open == "--open=worlds" && !worldsPanel.Visible) ShowWorlds(true);
			if (open == "--open=settings") ShowSettings(true);
			if (open == "--open=new" && !whatsNewPanel.Visible) whatsNewPanel.Visible = true;
			if (open == "--open=pause" && menuOpen) { ShowMainMenu(false); Input.MouseMode = Input.MouseModeEnum.Visible; }
		}
		else if (open != null && !invOpen)
		{
			if (open == "--open=kiln") { int x = (int)player.Pos.X, y = (int)player.Pos.Y + 3, z = (int)player.Pos.Z; world.SetBlock(x, y, z, Blocks.Furnace); kilnAt = World.Idx(x, y, z); var f = kilns.Of(kilnAt); f.Queue.Add(Blocks.Brick); f.Queue.Add(Blocks.Stone); f.Done[Blocks.Terracotta] = 3; OpenPanel(kilnPanel); }
			else if (open == "--open=bench") { benchBlock = Blocks.Planks; benchMode = 3; OpenPanel(benchPanel); }
			else { if (!inv.Survival) ShowVariants(Blocks.Stone, null); OpenPanel(invPanel); }
		}
		lock (dirty) if (dirty.Count > 0) return;
		shotTimer += dt;
		var stay = args.FirstOrDefault(a => a.StartsWith("--stay="));
		if (shotTimer < (stay != null ? float.Parse(stay[7..]) : sceneBuilt ? 4f : 1.5f)) return;
		GetViewport().GetTexture().GetImage().SavePng(arg[7..]);
		GD.Print($"shot saved, {chunkNodes.Count} chunks");
		foreach (var m in mobList) GD.Print($"mob {m.Type}: health {m.Health}{(m.Burning ? ", burning" : "")}{(m.Fade > 0 ? ", fading" : "")} at y {m.Y:0.0}");
		GD.Print($"your health {health}, rocks flying {rocks.Count}");
		GetTree().Quit();
	}

	// scripted checks of the hotbar, crafting, bench, kiln and drops (prints PASS / FAIL lines)
	void SelfTest()
	{
		void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
		var s = new Inventory(true);
		for (int k = 0; k < 2; k++) s.Give(Blocks.Rock); s.Give(Blocks.Stick);
		var axe = Inventory.Recipes.First(r => r.Out == Blocks.CrudeAxe);
		Check("survival: 2 rocks + stick can make a Crude Axe", s.CanCraft(axe));
		Check("crafting it works", s.Craft(axe) == true && s.CountOf(Blocks.CrudeAxe) == 1 && s.CountOf(Blocks.Rock) == 0 && s.CountOf(Blocks.Stick) == 0);
		s.Give(Blocks.Log);
		var planks = Inventory.Recipes.First(r => r.Out == Blocks.Planks && r.Needs[0].id == Blocks.Log);
		Check("log -> 4 planks with an axe", s.Craft(planks) == true && s.CountOf(Blocks.Planks) == 4);
		Check("bench: planks need 3 for stairs", s.BenchCut(Blocks.Planks, 3).StartsWith("Made 4") && s.CountOf(1200 + Blocks.Planks) == 4 && s.CountOf(Blocks.Planks) == 1);
		Check("bench: 1 plank -> 8 pieces", s.BenchCut(Blocks.Planks, 0).StartsWith("Took 8") && s.CountOf(Blocks.PieceItem + Blocks.Planks) == 8);
		for (int k = 0; k < 70; k++) s.Give(Blocks.Dirt);
		Check("stacks stop at 64", s.Count.Max() == 64 && s.CountOf(Blocks.Dirt) == 70);
		var c = new Inventory(false);
		Check("creative: default hotbar", c.Id[0] == Blocks.Grass && c.Id[8] == Blocks.Torch);
		c.UseOne(); Check("creative: blocks never run out", c.Id[0] == Blocks.Grass);
		// kiln (creative: no fuel needed)
		int x = (int)player.Pos.X, y = (int)player.Pos.Y + 5, z = (int)player.Pos.Z;
		world.SetBlock(x, y, z, Blocks.Furnace);
		var kl = new Kilns(world) { Survival = false }; int i = World.Idx(x, y, z);
		kl.Of(i).Queue.Add(Blocks.Brick);
		kl.Tick(0.1f, null);
		Check("kiln lights up while smelting", Blocks.B[world.Blocks[i]].Lit);
		for (int t = 0; t < 60; t++) kl.Tick(0.1f, null);
		Check("kiln made a Brick and stored it", kl.Of(i).Done.GetValueOrDefault(Blocks.Brick) == 1 && !Blocks.B[world.Blocks[i]].Lit);
		// survival kiln with fuel
		var ks = new Kilns(world) { Survival = true }; var f = ks.Of(i); f.Fuel = Blocks.Stick; f.FuelN = 1; f.Queue.Add(Blocks.Stone); f.Queue.Add(Blocks.Stone);
		for (int t = 0; t < 120; t++) ks.Tick(0.1f, null);
		Check("survival kiln: 1 stick smelts 1 thing", f.Done.GetValueOrDefault(Blocks.Stone) == 1 && f.Queue.Count == 1 && f.FuelN == 0);
		world.SetBlock(x, y, z, 0);
		// drops: in survival, a dropped item falls and you pick it up
		var dp = new Drops(); AddChild(dp); dp.Init(world, player, s);
		{   // stand on the ground first (the world may have been saved with you flying high up)
			int gx = (int)MathF.Floor(player.Pos.X), gz = (int)MathF.Floor(player.Pos.Z);
			player.Pos = new Vector3(player.Pos.X, world.Heights[gz * World.WX + gx] + 1, player.Pos.Z); player.Flying = false;
		}
		int before = s.CountOf(Blocks.Stick);
		var d = dp.Add(Blocks.Stick, player.Pos.X, player.Pos.Y + 0.5f, player.Pos.Z); d.V = Vector3.Zero;
		for (int t = 0; t < 60; t++) dp.Update(1 / 60f);
		Check("walking into a dropped item picks it up", s.CountOf(Blocks.Stick) == before + 1 && dp.All.Count == 0);
		if (dp.All.Count > 0) GD.Print($"  (stick count {before} -> {s.CountOf(Blocks.Stick)}, drop at {dp.All[0].P}, you at {player.Pos}, slots: {string.Join(",", s.Id)})");
		dp.QueueFree();
		GD.Print("selftest done");
		GetTree().Quit();
	}

	bool sceneBuilt, mobsShown, diedTest, savetestDone, netTried;
	void BuildTestScene()
	{
		sceneBuilt = true;
		// a stone floor in the air, with one of everything on it, seen from the side at dusk-free daylight
		int cx = (int)player.Pos.X, cy = 120, cz = (int)player.Pos.Z - 8;
		for (int x = -6; x <= 6; x++) for (int z = -3; z <= 3; z++) world.SetBlock(cx + x, cy, cz + z, Blocks.Stone);
		world.SetBlock(cx - 6, cy + 1, cz - 3, Blocks.Water);                                   // water that should spread over the floor
		world.SetSpots(cx - 4, cy + 1, cz, 0x33, Blocks.StoneBricks);                           // a slab
		world.SetSpots(cx - 3, cy + 1, cz, Blocks.ShapeMasks["stairs"][0], Blocks.Planks);       // stairs
		world.SetPiece(cx - 2, cy + 1, cz, 0, Blocks.Cobblestone);                               // a piece
		world.SetBlock(cx - 1, cy + 1, cz, Blocks.TorchVariant(0, 1, 0));                        // lit
		world.SetBlock(cx, cy + 1, cz, Blocks.TorchVariant(0, 4, 1));                            // 4 smoldering
		world.SetBlock(cx + 1, cy + 1, cz, Blocks.TorchVariant(0, 1, 2));                        // burnt
		world.SetBlock(cx + 2, cy + 1, cz, Blocks.TorchVariant(0, 2, 3));                        // 2 ever
		world.SetBlock(cx + 4, cy + 1, cz, Blocks.Furnace);                                      // kiln
		for (int k = 0; k < 12; k++)
		{   // along the back: every colour's bricks, terracotta and chiseled terracotta, bottom to top
			world.SetBlock(cx - 6 + k, cy + 1, cz - 3, Blocks.ColourBricks[k]); world.SetBlock(cx - 6 + k, cy + 2, cz - 3, Blocks.ColourTerracotta[k]);
			world.SetBlock(cx - 6 + k, cy + 3, cz - 3, Blocks.ColourChiseled[k]);
		}
		for (int k = 0; k < 5; k++) { world.SetBlock(cx - 2 + k, cy, cz + 2, Blocks.Grass); world.SetBlock(cx - 2 + k, cy + 1, cz + 2, Blocks.Flowers[k]); }   // the flowers, on grass
		player.Pos = new Vector3(cx + 0.5f, cy + 1.5f, cz + 7.5f); player.Flying = true; player.Yaw = 0; player.Pitch = -0.35f;
		GD.Print($"burn timers: {world.TorchBurn.Count}  (expect 2: lit + smoldering)");
	}

	// rebuild changed chunks, nearest first, for up to a few milliseconds a frame (several at once, on all CPU cores)
	void BuildChunks()
	{
		var start = Time.GetTicksUsec();
		int pcx = (int)player.Pos.X / World.CS, pcz = (int)player.Pos.Z / World.CS;
		int Dist(int c) { int dx = c % World.NCX - pcx, dz = c / World.NCX - pcz; return dx * dx + dz * dz; }
		while (Time.GetTicksUsec() - start < 8000)
		{
			int[] batch;
			lock (dirty)
			{
				if (dirty.Count == 0 && soon.Count > 0) { foreach (int c in soon) dirty.Add(c); soon.Clear(); }
				if (dirty.Count == 0) return;
				batch = dirty.OrderBy(Dist).Take(Math.Max(2, System.Environment.ProcessorCount)).ToArray();
				foreach (int c in batch) { dirty.Remove(c); soon.Remove(c); }
			}
			var parts = new (ChunkMesher.Part, ChunkMesher.Part)[batch.Length];
			Parallel.For(0, batch.Length, i => parts[i] = mesher.Build(batch[i] % World.NCX, batch[i] / World.NCX));
			for (int i = 0; i < batch.Length; i++) SetChunkMesh(batch[i], parts[i].Item1.ToMesh(), parts[i].Item2.ToMesh());
		}
	}
	void SetChunkMesh(int c, ArrayMesh solid, ArrayMesh water)
	{
		if (!chunkNodes.TryGetValue(c, out var n))
		{
			n = (new MeshInstance3D { MaterialOverride = blockMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off },
				 new MeshInstance3D { MaterialOverride = waterMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
			AddChild(n.solid); AddChild(n.water);
			chunkNodes[c] = n;
		}
		n.solid.Mesh = solid; n.water.Mesh = water;
	}

	// ---- the outline around the block you're looking at ----
	static MeshInstance3D MakeOutline()
	{
		var im = new ImmediateMesh();
		im.SurfaceBegin(Mesh.PrimitiveType.Lines);
		float a = -0.002f, b = 1.002f;
		var c = new[] { new Vector3(a, a, a), new Vector3(b, a, a), new Vector3(b, a, b), new Vector3(a, a, b), new Vector3(a, b, a), new Vector3(b, b, a), new Vector3(b, b, b), new Vector3(a, b, b) };
		int[] e = { 0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
		foreach (int i in e) im.SurfaceAddVertex(c[i]);
		im.SurfaceEnd();
		return new MeshInstance3D { Mesh = im, MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0, 0, 0, 0.7f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha }, Visible = false };
	}
	void UpdateOutline()
	{
		var h = Input.MouseMode == LockMode ? player.Raycast() : null;
		outline.Visible = h != null;
		if (h is not { } hit) return;
		Vector3 pos = new(hit.X, hit.Y, hit.Z), size = Vector3.One;
		if (hit.Sub >= 0) { pos += new Vector3((hit.Sub & 1) * 0.5f, ((hit.Sub >> 1) & 1) * 0.5f, (hit.Sub >> 2) * 0.5f); size = new Vector3(0.5f, 0.5f, 0.5f); }
		else if (Blocks.IsTorch(hit.Id)) { var bx = ChunkMesher.TorchGeo(hit.Id).Box; pos += new Vector3(bx[0], bx[1], bx[2]); size = new Vector3(bx[3] - bx[0], bx[4] - bx[1], bx[5] - bx[2]); }
		else if (Blocks.B[hit.Id].Box is { } bb) { pos += new Vector3(bb[0], bb[1], bb[2]); size = new Vector3(bb[3] - bb[0], bb[4] - bb[1], bb[5] - bb[2]); }
		outline.Transform = new Transform3D(Basis.Identity.Scaled(size), pos);
	}


	// ---- Ever Torches give off little fire essence sparks (your fire_essence picture) that drift up and fade ----
	const float SparkLife = 1.6f, SparkEvery = 0.35f, SparkSize = 0.14f, SparkRange = 40; const int SparkMax = 300;
	readonly List<(int i, int x, int y, int z, float[] t)> everNear = new();
	readonly List<(Sprite3D s, float age, Vector3 v)> sparks = new();
	float everScan; Texture2D sparkTex;
	void TickEverSparks(float dt)
	{
		if ((everScan -= dt) <= 0)
		{
			everScan = 1; everNear.Clear();
			foreach (int i in world.Torches)
			{
				if (Blocks.B[world.Blocks[i]].Burn != 3) continue;
				var (x, y, z) = World.FromIdx(i);
				if (MathF.Abs(x - player.Pos.X) < SparkRange && MathF.Abs(z - player.Pos.Z) < SparkRange && MathF.Abs(y - player.Pos.Y) < SparkRange)
					everNear.Add((i, x, y, z, new[] { (float)rnd.NextDouble() * SparkEvery }));
			}
		}
		sparkTex ??= ImageTexture.CreateFromImage(Atlas.Icon(Blocks.FireEssence));
		foreach (var e in everNear)
		{
			int id = world.Blocks[e.i];
			if (id == 0 || Blocks.B[id].Burn != 3 || !Blocks.IsTorch(id)) continue;   // (broken since the last look)
			if ((e.t[0] -= dt) > 0 || sparks.Count >= SparkMax) continue;
			e.t[0] += SparkEvery * (0.6f + (float)rnd.NextDouble() * 0.8f);
			var box = ChunkMesher.TorchGeo(id).Box;   // spark from the top of the torch(es)
			var s = new Sprite3D
			{
				Texture = sparkTex, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
				PixelSize = SparkSize / Atlas.TILE, Shaded = false, AlphaCut = SpriteBase3D.AlphaCutMode.Disabled, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				Position = new Vector3(e.x + (box[0] + box[3]) / 2 + ((float)rnd.NextDouble() - 0.5f) * 0.2f, e.y + box[4], e.z + (box[2] + box[5]) / 2 + ((float)rnd.NextDouble() - 0.5f) * 0.2f),
			};
			AddChild(s);
			sparks.Add((s, 0, new Vector3(((float)rnd.NextDouble() - 0.5f) * 0.2f, 0.5f + (float)rnd.NextDouble() * 0.3f, ((float)rnd.NextDouble() - 0.5f) * 0.2f)));
		}
		for (int k = sparks.Count - 1; k >= 0; k--)
		{
			var (s, age, v) = sparks[k];
			age += dt;
			if (age >= SparkLife) { s.QueueFree(); sparks.RemoveAt(k); continue; }
			sparks[k] = (s, age, v);
			s.Position += v * dt;
			float f = age / SparkLife;
			s.Modulate = new Color(1, 1, 1, 1 - f);
			s.PixelSize = SparkSize * (1 - 0.5f * f) / Atlas.TILE;
		}
	}

	double lastSpace;
	Input.MouseModeEnum lastMouseMode = (Input.MouseModeEnum)(-1); int clickWait = 60, clickStep;
	// Looking around with the mouse. Godot's own "captured" mouse gets its movements a special way (raw input) that
	// doesn't reach the game on some PCs, so instead the pointer is hidden and kept inside the window, and each frame
	// the game sees how far it moved from the middle of the window, turns you by that much, and puts it back.
	public const Input.MouseModeEnum LockMode = Input.MouseModeEnum.ConfinedHidden;
	bool lookPrimed;
	void MouseLook()
	{
		bool locked = Input.MouseMode == LockMode && GetWindow().HasFocus();
		var mode = Input.MouseMode;
		if (mode != lastMouseMode) { lastMouseMode = mode; GD.Print($"mouse: {mode}"); }   // (a note in the game's log)
		if (!locked) { lookPrimed = false; return; }
		var middle = DisplayServer.WindowGetSize() / 2;
		var at = DisplayServer.MouseGetPosition() - DisplayServer.WindowGetPosition();
		if (lookPrimed)
		{
			var moved = at - middle;
			player.Yaw -= moved.X * MouseSens;
			player.Pitch = Math.Clamp(player.Pitch - moved.Y * MouseSens, -1.55f, 1.55f);
		}
		if (at != middle) DisplayServer.WarpMouse(middle);
		lookPrimed = true;
	}
	public override void _UnhandledInput(InputEvent e)
	{
		if (skinPanel?.Visible == true) { SkinKey(e); return; }   // (the skin editor has keys of its own)
		if (!ready) return;
		bool locked = Input.MouseMode == LockMode;
		if (e is InputEventMouseMotion && locked) { }   // (looking around is done in MouseLook, every frame)
		else if (e is InputEventMouseButton mb && mb.Pressed)
		{
			if (invOpen || menuOpen || pauseMenu.Visible || dead) return;
			if (!locked) { Input.MouseMode = LockMode; return; }
			if (mb.ButtonIndex == MouseButton.Left) { SwingHand(); Break(); }
			else if (mb.ButtonIndex == MouseButton.Right) { SwingHand(); Place(); }
			else if (mb.ButtonIndex == MouseButton.WheelDown) SelectSlot(inv.Selected + 1);
			else if (mb.ButtonIndex == MouseButton.WheelUp) SelectSlot(inv.Selected - 1);
		}
		else if (e is InputEventKey k && k.Pressed && !k.Echo)
		{
			if (k.PhysicalKeycode == Key.Escape)
			{   // Esc: closes whatever's open; while playing, pauses
				if (confirm.Visible) return;
				if (settingsPanel.Visible || worldsPanel.Visible || whatsNewPanel.Visible || multiPanel.Visible) { settingsPanel.Visible = whatsNewPanel.Visible = multiPanel.Visible = false; ShowWorlds(false); }
				else if (invOpen) CloseInventory();
				else if (pauseMenu.Visible) Resume();
				else if (!menuOpen) Input.MouseMode = Input.MouseModeEnum.Visible;
				return;
			}
			if (menuOpen || pauseMenu.Visible || AnyPanel || dead) return;
			if (k.PhysicalKeycode == Key.M) { settings.Muted = !settings.Muted; ApplySettings(); ShowNote(settings.Muted ? "Sound off" : "Sound on"); }
			else if (k.PhysicalKeycode == Key.T && locked) sky.TimeOfDay = (sky.TimeOfDay + 0.125f) % 1;   // skip ahead 1/8 of a day
			else if (k.PhysicalKeycode == Key.F && locked && !inv.Survival) { player.Flying = !player.Flying; player.Vel.Y = 0; }
			else if (k.PhysicalKeycode == Key.E) { if (invOpen) CloseInventory(); else OpenPanel(invPanel); }
			else if (k.PhysicalKeycode >= Key.Key1 && k.PhysicalKeycode <= Key.Key9) SelectSlot((int)(k.PhysicalKeycode - Key.Key1));
			else if (k.PhysicalKeycode == Key.Key0) SelectSlot(9);
			else if (k.PhysicalKeycode == Key.Space && locked && !inv.Survival)
			{
				// double-tap space to fly
				double now = Time.GetTicksMsec() / 1000.0;
				if (now - lastSpace < 0.3) { player.Flying = !player.Flying; player.Vel.Y = 0; lastSpace = 0; }
				else lastSpace = now;
			}
		}
	}
}
