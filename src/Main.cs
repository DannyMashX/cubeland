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
	readonly Dictionary<int, (MeshInstance3D solid, MeshInstance3D water)> groupNodes = new();   // one pair of meshes per 32x32x32 group
	readonly Dictionary<int, (ChunkMesher.Part solid, ChunkMesher.Part water)> chunkParts = new();   // each built 16x16x16 chunk's pieces
	readonly HashSet<int> dirty = new(), soon = new();
	readonly HashSet<int> far = new();   // chunks waiting to be built that are past the render distance (built once you come near)
	Task genTask; volatile float genProgress; bool ready;
	Label info, loading;
	MeshInstance3D outline;
	Inventory inv; Drops drops; Falling falling; Kilns kilns;
	Sky sky; Godot.Environment env;
	float camSky = 1, camSkyCheck; bool nightMusic, wasUnder;
	readonly Random rnd = new();
	static bool HasArg(string a) => OS.GetCmdlineUserArgs().Contains(a);
	static int ArgInt(string prefix, int fallback) => OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith(prefix)) is { } a ? int.Parse(a[prefix.Length..]) : fallback;

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
		autoGui = (int)uiScale;
		LoadSettings();
		if (uiArg == null && settings.Gui > 0) uiScale = settings.Gui;   // (the GUI scale you picked in Settings)
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
		if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--atlasdump=")) is { } ad) { Atlas.Dump(ad[12..]); SetProcess(false); GetTree().Quit(); return; }   // (testing)
		if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--sheetcheck=")) is { } sc) { Atlas.SheetCheck(sc[13..]); SetProcess(false); GetTree().Quit(); return; }   // (testing)
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
		if (current == null) return;   // (the main menu, with no world: see PickWorld)
		world = new World(current.Seed, current.Gen, current.H, current.S, current.B, current.Land);
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
		stages.Clear(); foreach (var (m, _) in cracks.Values) if (GodotObject.IsInstanceValid(m)) m.QueueFree(); cracks.Clear();   // (a new world: no blocks half broken)
		loading.Visible = false;
		mesher = new ChunkMesher(world);
		player = new Player(world);
		inv = new Inventory(current.Survival);
		drops = new Drops(); AddChild(drops); drops.Init(world, player, inv); drops.DayTint = () => sky.DayTint; drops.HeldLight = HeldLightAt;
		falling = new Falling { Client = netClient }; AddChild(falling); falling.Init(world, Collect, DropFor); falling.DayTint = () => sky.DayTint; falling.HeldLight = HeldLightAt;
		kilns = new Kilns(world) { Survival = inv.Survival };
		world.Drop = Collect;
		world.NoWater = netClient;   // (in a joined game the host's game runs the water)
		drops.Client = netClient; drops.AskPickup = d => hostConn?.Send(new Dictionary<string, object> { ["t"] = "pickup", ["k"] = d.K });
		world.BlockChanged = (i, old, now, _) => { NetBlockChanged(i, now); if (Blocks.IsFurnace(old) && !Blocks.IsFurnace(now)) { if (kilnAt == i) CloseInventory(); kilns.Empty(i, Collect); } };
		BuildHud(); BuildTips(); BuildHeld();
		BuildLifeUi(); BuildArmorBar();
		InitMobs();
		world.ChunksChanged = set => { lock (dirty) foreach (int c in set) dirty.Add(c); };
		world.NearbyRebuild = (x, y, z) =>
		{
			// (sky rays only go up or sideways, so only spots at or below the change can see the difference)
			const int R = 18;
			for (int cy = 0; cy <= Math.Min(World.NCY - 1, (y + 1) / World.CS); cy++)
				for (int cz = Math.Max(0, (z - R) / World.CS); cz <= Math.Min(World.NCZ - 1, (z + R) / World.CS); cz++)
					for (int cx = Math.Max(0, (x - R) / World.CS); cx <= Math.Min(World.NCX - 1, (x + R) / World.CS); cx++)
						soon.Add(World.ChunkKey(cx, cy, cz));
		};
		for (int c = 0; c < World.NChunks; c++) { var (cx, cy, cz) = World.FromChunkKey(c); if (cy * World.CS < world.ChunkTop[cz * World.NCX + cx]) dirty.Add(c); }   // (chunks of empty sky have nothing to build)
		player.Pos = spawnPoint = FindSpawn();
		outline = MakeOutline(); AddChild(outline);
		ApplyPlayerSave();
		// (test: `--at=x,y,z[,yaw,pitch]` puts you there, flying)
		if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--at=")) is { } atArg)
		{
			var v = atArg[5..].Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
			player.Pos = new Vector3(v[0], v[1], v[2]); player.Flying = true;
			if (v.Length > 4) { player.Yaw = v[3]; player.Pitch = v[4]; }
		}
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
		Atlas.SpinSaw(delta);   // (the Cutter's saw blade)
		if (current == null) { MenuOnly(dt); return; }
		PlaceToolRow(); PlaceTip(); UpdateHeld();
		if (ready) { HoldToBreak(dt); UpdateCracks(dt); }
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
		Panorama(dt);
		if (!cam.IsInsideTree()) return;   // (the scene is being swapped for another world)
		sky.Tick(dt, cam);
		UpdateSkyAndWater(dt);
		UpdateHealth(dt);
		if (moving) { UpdateStairs(); UpdateEerie(dt); }
		TickLifeUi(dt);
		UpdateMobs(moving || invOpen || hosting || netClient ? dt : 0);   // (mobs wait while you're paused, like the rest of the world; a joined game follows the host)
		UpdateHand(dt);
		if ((moving || invOpen || hosting) && !netClient)
		{   // (torches burning down, clay drying and kilns all wait while you're paused)
			world.TickTimers(dt);
			kilns.Tick(dt, i => { if (i == kilnAt) DrawKiln(); });
		}
		world.UpdateWater(dt);
		UpdateScreens(dt);
		drops.Update(dt);
		falling.Update(dt);
		TickHud(dt);
		TickEverSparks(dt);
		BuildChunks();
		UpdateOutline();
		TestShot(dt);
		var p = player.Pos;
		info.Text = $"{Title} {Version}\n{Engine.GetFramesPerSecond()} fps\nx {p.X:0.0}  y {p.Y:0.0}  z {p.Z:0.0}" + (player.Flying ? "\nflying" : "") + (settings.Muted ? "\nSound off (M)" : "") + NetInfoLine();
	}

	// The main menu's panorama: behind the title, the camera slowly turns all the way round.
	// It shows the update's own panorama: six pictures (panorama/0.jpg .. 5.jpg, a cube around one spot) of somewhere that shows
	// off what the update adds, taken with make-panorama.ps1. Without them it looks out over your own world instead, from a little
	// above where you are (above the ground, even in a cave or a house). Leaving the menu puts the camera back.
	const float PanoTurn = 0.05f;   // how fast it turns (radians a second: once round in about 2 minutes)
	float panoYaw = float.NaN;
	Node3D panoBox; bool panoTried;
	// the main menu with no world (see PickWorld): only the panorama turning (or the sky, without its pictures)
	float menuShotTimer;
	void MenuOnly(float dt)
	{
		loading.Visible = false; uiLayer.Visible = false;
		if (!panoTried) { panoTried = true; panoBox = MakePanoBox(); }
		if (float.IsNaN(panoYaw)) panoYaw = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--panoyaw=")) is { } py ? float.Parse(py[10..], System.Globalization.CultureInfo.InvariantCulture) : 0;
		panoYaw += dt * PanoTurn;
		cam.Position = new Vector3(0, 100, 0);
		cam.Rotation = new Vector3(-0.1f, panoYaw, 0);
		if (panoBox != null) { panoBox.Visible = true; panoBox.GlobalPosition = cam.GlobalPosition; }
		sky.Tick(dt, cam);
		// (test: --shot=file.png takes a picture of the menu and quits)
		if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--shot=")) is { } shot && (menuShotTimer += dt) > 1.5f)
		{
			var open = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--open="));
			if (open == "--open=worlds" && !worldsPanel.Visible) { ShowWorlds(true); menuShotTimer = 0; return; }
			if (open == "--open=multi" && !multiPanel.Visible) { multiPanel.Visible = true; Host(); menuShotTimer = 0; return; }
			GetViewport().GetTexture().GetImage().SavePng(shot[7..]);
			GD.Print("shot saved (main menu, no world)");
			GetTree().Quit();
		}
	}
	// the cube's six sides: which way each picture looks, and which way is up in it (the same in the web version)
	public static readonly Vector3[] PanoDirs = { new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1) };
	public static readonly Vector3[] PanoUps = { new(0, 1, 0), new(0, 1, 0), new(0, 0, 1), new(0, 0, -1), new(0, 1, 0), new(0, 1, 0) };
	void Panorama(float dt)
	{
		if (!panoTried) { panoTried = true; panoBox = MakePanoBox(); }
		if (panoBox != null) panoBox.Visible = menuOpen;
		if (!menuOpen) { panoYaw = float.NaN; return; }
		if (float.IsNaN(panoYaw)) panoYaw = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--panoyaw=")) is { } py ? float.Parse(py[10..], System.Globalization.CultureInfo.InvariantCulture)   // (test: start turned this far)
			: panoBox != null ? 0 : player.Yaw;
		panoYaw += dt * PanoTurn;
		var e = player.EyePos;
		int x = Math.Clamp((int)MathF.Floor(e.X), 0, World.WX - 1), z = Math.Clamp((int)MathF.Floor(e.Z), 0, World.WZ - 1);
		cam.Position = new Vector3(e.X, Math.Max(e.Y, world.Heights[z * World.WX + x] + 3.5f), e.Z);
		cam.Rotation = new Vector3(-0.1f, panoYaw, 0);
		if (panoBox != null) panoBox.GlobalPosition = cam.GlobalPosition;   // (it goes wherever the camera is, so it's all you see)
	}
	// the panorama's cube: six pictures on the inside of a small cube around the camera, drawn over everything else
	Node3D MakePanoBox()
	{
		var tex = new Texture2D[6];
		for (int k = 0; k < 6; k++)
		{
			string p = $"res://panorama/{k}.jpg";
			if (!ResourceLoader.Exists(p)) return null;
			tex[k] = GD.Load<Texture2D>(p);
		}
		var box = new Node3D { Visible = false }; AddChild(box);
		for (int k = 0; k < 6; k++)
		{
			Vector3 d = PanoDirs[k], u = PanoUps[k], r = d.Cross(u);   // (a picture's right, as the camera that took it saw it)
			var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
			void V(Vector3 at, float tu, float tv) { st.SetUV(new Vector2(tu, tv)); st.AddVertex(at); }
			V(d - r + u, 0, 0); V(d + r + u, 1, 0); V(d + r - u, 1, 1);
			V(d - r + u, 0, 0); V(d + r - u, 1, 1); V(d - r - u, 0, 1);
			var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoTexture = tex[k], NoDepthTest = true, DisableFog = true,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, RenderPriority = 127,
				TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear };
			box.AddChild(new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
		}
		return box;
	}
	// (make-panorama.ps1: `--panorama=<folder>` takes the six pictures from where you are, then quits)
	bool panoCapturing;
	async void CapturePanorama(string dir)
	{
		panoCapturing = true;
		if (handRoot != null) handRoot.Visible = false;
		if (outline != null) outline.Visible = false;
		var vp = new SubViewport { Size = new Vector2I(1024, 1024), RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
		var c = new Camera3D { Fov = 90, Near = 0.05f, Far = 1000 };
		vp.AddChild(c); AddChild(vp);
		DirAccess.MakeDirRecursiveAbsolute(dir);
		var at = cam.GlobalPosition;
		for (int k = 0; k < 6; k++)
		{
			c.LookAtFromPosition(at, at + PanoDirs[k], PanoUps[k]);
			for (int f = 0; f < 4; f++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			vp.GetTexture().GetImage().SaveJpg($"{dir}/{k}.jpg", 0.9f);
		}
		GD.Print($"panorama saved to {dir} from {at}");
		GetTree().Quit();
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
			if (fell > 3.5f && !stairFall) HurtPlayer((int)MathF.Floor(fell - 3));
			stairFall = false;
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
			RenderingServer.GlobalShaderParameterSet("fog_thickness", under ? 0.22f : FogThickness);
			RenderingServer.GlobalShaderParameterSet("fog_max", under ? 0.97f : 0.82f);
			cam.Far = under ? 40 : 1000;
			waterOverlay.Visible = under;
			Sound.I?.Underwater(under);
		}
		// night music comes on once it's properly dark, and goes back to day music once it's light again
		nightMusic = nightMusic ? sky.SkyDay < 0.5f : sky.SkyDay < 0.12f;
		Sound.I?.Music(nightMusic ? "night" : "day");
		if (Sound.I != null) Sound.I.Hushed = NearStair();
	}
	float target = 1;

	// for testing: `-- --shot=file.png` saves a picture of the screen once the world has loaded, then quits
	(int x, int g, int z)? fallAt;   // (--falltest)
	bool caveTested, caveFound;   // (--cavetest)
	float shotTimer; bool timeArgDone, skinTested, dropsTested, farmTested, cowsTested, snowTested, cutTested, throwTested, throwShown, hudTested, stageTested, testGear, heldTested, heldPlaced;
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
		if (args.Contains("--gear") && !gearShown && inv.Survival && !menuOpen)
		{   // (test: survival gear to look at: iron armor, an iron backpack with things in it, a bronite belt, tools, Pockets)
			gearShown = true;
			for (int k = 0; k < 4; k++) inv.SetSlot(Inventory.Armor0 + k, Blocks.ArmorItem(2, k), 1, k == 1 ? 120 : 0);
			inv.SetSlot(Inventory.BackSlot, Blocks.Backpacks[2]); inv.SetSlot(Inventory.BeltSlot, Blocks.Belts[3]);
			inv.SetSlot(Inventory.Tool0, Blocks.Tools[1]); inv.SetSlot(Inventory.Tool0 + 1, 649 + 1, 1, 200); inv.SetSlot(Inventory.Tool0 + 2, Blocks.CrudeAxe);
			inv.SetSlot(Inventory.Off0, Blocks.Torch, 12); inv.SetSlot(Inventory.Off0 + 1, Blocks.Cobblestone, 40);
			inv.SetSlot(Inventory.Pocket0, Blocks.Leather, 9); inv.SetSlot(Inventory.Pocket0 + 1, Blocks.MetalBrick["iron"], 14); inv.SetSlot(Inventory.Pocket0 + 2, Blocks.Backpacks[0]);
			inv.SetSlot(Inventory.Pack0, Blocks.Rock, 30); inv.SetSlot(Inventory.Pack0 + 1, Blocks.Grain, 8); inv.SetSlot(Inventory.Pack0 + 2, Blocks.Planks, 64);
			inv.SetSlot(0, Blocks.Dirt, 20); inv.SetSlot(1, Blocks.Tools[1]);
		}
		if (args.Contains("--inventory") && !invOpen && !menuOpen) OpenPanel(invPanel);   // (test: the inventory open)
		var varArg = args.FirstOrDefault(a => a.StartsWith("--search="));   // (test: the creative inventory searching for this)
		if (varArg != null && invOpen && creativeSearch != null && creativeSearch.Text == "") { creativeSearch.Text = varArg[9..].Replace("_", " "); ShowCreative(); }
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
				if (type == "player" && args.Contains("--gear")) SetGear(m, new[] { Blocks.ArmorItem(2, 0), Blocks.ArmorItem(2, 1), Blocks.ArmorItem(2, 2), Blocks.ArmorItem(2, 3), Blocks.Backpacks[2], Blocks.Belts[3] });   // (test: wearing gear)
			}
			player.Pitch = -0.15f;
			var pa = args.FirstOrDefault(a => a.StartsWith("--pitch="));
			if (pa != null) player.Pitch = float.Parse(pa[8..], System.Globalization.CultureInfo.InvariantCulture);
			if (inv.Survival) { health = 13; gloom = 3; changeTime = 0; DrawHearts(); }
		}
		if (args.Contains("--cows") && !cowsTested && !menuOpen)
		{   // (test: cows on a grass field up in the air: the 8-a-chunk limit, feeding, growing up, mating, crowding, grazing, drops)
			cowsTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			foreach (var o in mobList.ToArray()) RemoveMob(o);
			int x0 = (int)MathF.Floor(player.Pos.X / 16) * 16 - 16, z0 = (int)MathF.Floor(player.Pos.Z / 16) * 16, fy = 150;
			for (int x = x0; x < x0 + 64; x++) for (int z = z0; z < z0 + 16; z++) { world.SetBlock(x, fy, z, Blocks.Grass); world.SetBlock(x, fy + 1, z, 0); world.SetBlock(x, fy + 2, z, 0); }
			for (int x = x0 - 1; x <= x0 + 64; x++) for (int z = z0 - 1; z <= z0 + 16; z++)
				if (x < x0 || x >= x0 + 64 || z < z0 || z >= z0 + 16) { world.SetBlock(x, fy + 1, z, Blocks.Stone); world.SetBlock(x, fy + 2, z, Blocks.Stone); }   // (a wall round it)
			float Y = fy + 1;
			var A = (x0 / 16, z0 / 16);
			// 1. the 8-a-chunk limit is only for breeding: 12 cows can all be in one chunk
			for (int k = 0; k < 12; k++) SpawnCow(x0 + 2 + k % 6 * 2, Y, z0 + 3 + k / 6 * 4);
			Check($"12 cows can all be in one chunk (got {CowsInChunk(A)})", CowsInChunk(A) == 12);
			foreach (var o in mobList.ToArray()) RemoveMob(o);
			// 2. two happy cows with room, in a chunk with 8 cows in all (a calf and a fat cow among them): no calf.
			//    With one of the others gone (7), they have one.
			Mob MakePair(out Mob b2) { var a2 = SpawnCow(x0 + 8, Y, z0 + 8); b2 = SpawnCow(x0 + 9, Y, z0 + 8); a2.Happy = b2.Happy = 100; return a2; }
			var others = new List<Mob> { SpawnCow(x0 + 0.5f, Y, z0 + 0.5f, CalfTime), SpawnCow(x0 + 15.5f, Y, z0 + 0.5f), SpawnCow(x0 + 0.5f, Y, z0 + 15.5f),
				SpawnCow(x0 + 15.5f, Y, z0 + 15.5f), SpawnCow(x0 + 0.5f, Y, z0 + 8), SpawnCow(x0 + 15.5f, Y, z0 + 8) };
			for (int k = 0; k < FatAt; k++) Feed(others[1]);
			others[1].Happy = 0;
			var pa = MakePair(out var pb);
			bool Born() => mobList.Count(o => o.T.Cow && o.Baby > 0) > 1;
			for (int f = 0; f < 600 && !Born(); f++) { CowThink(pa, 0.1f); CowThink(pb, 0.1f); }
			Check($"8 cows in the chunk (calf and fat cow count too): no breeding ({CowsInChunk(A)} cows)", CowsInChunk(A) == 8 && !Born());
			RemoveMob(others[5]); pa.MateCool = pb.MateCool = 0; pa.Happy = pb.Happy = 100;
			for (int f = 0; f < 600 && !Born(); f++) { CowThink(pa, 0.1f); CowThink(pb, 0.1f); }
			Check($"7 cows: they have a calf ({CowsInChunk(A)} cows now)", Born() && CowsInChunk(A) == 8);
			foreach (var o in mobList.ToArray()) RemoveMob(o);
			// 3. feeding Grain: fatter (the fat model after FatAt), happy, and it stops wanting it when full
			var c = SpawnCow(x0 + 40.5f, Y, z0 + 8);
			for (int k = 0; k < FatAt; k++) Feed(c);
			Check($"after {FatAt} Grain it's a fat cow (fed {c.Fed}, model {ModelOf(c)})", c.Fed == FatAt && c.Variant == 2 && ModelOf(c) == "cowfat" && c.Happy > 0);
			Feed(c); Feed(c);
			Check($"fed up to {FedMax}, then it turns Grain down", c.Fed == FedMax && !Feed(c));
			// 4. drops: 1 + Grain eaten Raw Meat; Leather by room (all the room in the world: 3)
			c.Room = 1; int before = drops.All.Count; MobDies(c);
			int meat = drops.All.Skip(before).Count(d => d.Id == Blocks.RawMeat), leather = drops.All.Skip(before).Count(d => d.Id == Blocks.Leather);
			Check($"a well-fed cow with room drops {meat} Raw Meat and {leather} Leather (expect {1 + FedMax} and 3)", meat == 1 + FedMax && leather == 3);
			var crowdedCow = SpawnCow(x0 + 44.5f, Y, z0 + 8); crowdedCow.Room = 0.2f; before = drops.All.Count; MobDies(crowdedCow);
			Check("a crowded, unfed cow drops 1 Raw Meat and 1 Leather", drops.All.Skip(before).Count(d => d.Id == Blocks.RawMeat) == 1 && drops.All.Skip(before).Count(d => d.Id == Blocks.Leather) == 1);
			foreach (var o in mobList.ToArray()) RemoveMob(o);
			foreach (var d in drops.All.ToArray()) drops.Remove(d);
			// 5. a calf grows up into a cow
			var calf = SpawnCow(x0 + 40.5f, Y, z0 + 8, 1);
			Check("a calf wears the calf model", calf.Variant == 1 && ModelOf(calf) == "cowbaby" && calf.Health == CalfHealth);
			CowThink(calf, 1.5f);
			Check("and grows up into a cow", calf.Baby == 0 && calf.Variant == 0 && calf.Health == MobTypes["cow"].Health);
			RemoveMob(calf);
			// 6. two happy cows with room: a calf
			var a = SpawnCow(x0 + 40.5f, Y, z0 + 8); var b = SpawnCow(x0 + 41.5f, Y, z0 + 8);
			a.Happy = b.Happy = 100;
			for (int f = 0; f < 600 && mobList.Count(o => o.T.Cow) == 2; f++) { CowThink(a, 0.1f); CowThink(b, 0.1f); }
			Check($"two happy cows with room have a calf ({mobList.Count(o => o.T.Cow && o.Baby > 0)} calf)", mobList.Count(o => o.T.Cow && o.Baby > 0) == 1 && a.MateCool > 0 && b.MateCool > 0);
			foreach (var o in mobList.ToArray()) RemoveMob(o);
			// 7. crowded: five happy cows close together never mate
			var five = Enumerable.Range(0, 5).Select(k => SpawnCow(x0 + 34.5f + k % 3, Y, z0 + 7 + k / 3)).ToList();
			foreach (var o in five) { o.Happy = 100; o.RoomIn = 0; }
			for (int f = 0; f < 600; f++) foreach (var o in five) { CowThink(o, 0.1f); o.Grazing = 0; }
			Check("five happy cows crowded together don't mate", mobList.Count(o => o.T.Cow) == 5);
			Check("happy cows' hearts don't make them mate: feeding alone doesn't either", five.All(o => o.Mate == null));
			foreach (var o in mobList.ToArray()) RemoveMob(o);
			// 8. grazing: an unhappy cow eats the grass under it (Dirt), gets happy, and the grass grows back
			var gz = SpawnCow(x0 + 52.5f, Y, z0 + 8); gz.Walking = false;
			int gx = (int)MathF.Floor(gz.X), gzz = (int)MathF.Floor(gz.Z);
			for (int f = 0; f < 400 && world.GetBlock(gx, fy, gzz) == Blocks.Grass; f++) { gz.Walking = false; CowThink(gz, 0.1f); }
			Check("an unhappy cow grazes: the grass under it is eaten", world.GetBlock(gx, fy, gzz) == Blocks.Dirt && world.Grazed.ContainsKey(World.Idx(gx, fy, gzz)) && gz.Happy > 0);
			world.TickTimers(World.RegrowMax + 2);
			world.TickTimers(1.1f);
			Check("and it grows back to Grass", world.GetBlock(gx, fy, gzz) == Blocks.Grass && !world.Grazed.ContainsKey(World.Idx(gx, fy, gzz)));
			RemoveMob(gz);
			// for the picture: a cow, a calf and a fat cow side by side, happy (hearts), in front of you
			player.Pos = new Vector3(x0 + 40.5f, Y + 0.3f, z0 + 10.5f); player.Yaw = MathF.PI; player.Pitch = -0.3f; player.Flying = true;
			var p1 = SpawnCow(x0 + 37f, Y, z0 + 5); var p2 = SpawnCow(x0 + 43.5f, Y, z0 + 5.5f, CalfTime); var p3 = SpawnCow(x0 + 40.3f, Y, z0 + 5);
			for (int k = 0; k < FatAt; k++) Feed(p3);
			foreach (var o in new[] { p1, p2, p3 }) { o.Yaw = 2.5f; o.TurnTo = o.Yaw; o.Walking = false; o.Timer = 999; o.Happy = 100; o.MateCool = 999; o.GrazeIn = 999; o.Grazing = 0; }
			p1.Grazing = 999;   // (head down, eating)
		}
		if (args.Contains("--falltest") && fallAt == null && !menuOpen && shotTimer > 0.5f)   // (once the world is built)
		{   // (test: Sand, Gravel, Dirt and Sand put down in the air in front of you; they fall and stack up on the ground)
			player.Yaw = 0; player.Pitch = 1.2f; player.Flying = true;
			int x = (int)MathF.Floor(player.Pos.X), z = (int)MathF.Floor(player.Pos.Z) - 6, g = world.Heights[z * World.WX + x];
			for (int u = 1; u < 20; u++) world.SetBlock(x, g + u, z, 0);
			int[] ids = { Blocks.Sand, Blocks.Gravel, Blocks.Dirt, Blocks.Sand };
			for (int k = 0; k < 4; k++) world.SetBlock(x, (int)player.Pos.Y + 25 + k * 2, z, ids[k]);
			fallAt = (x, g, z);
		}
		if (args.FirstOrDefault(a => a.StartsWith("--cavetest=")) is { } ct && !caveTested && !menuOpen)
		{   // (test: find an open cave near the middle of the world at about this height, light it with torches and look around it)
			caveTested = true;
			int want = int.Parse(ct[11..]);
			bool Open(int x, int y, int z) => world.GetBlock(x, y, z) == 0 && world.GetBlock(x, y + 1, z) == 0;
			for (int r = 0; r < 200 && !caveFound; r += 2)
				for (int dz = -r; dz <= r && !caveFound; dz += 2) for (int dx = -r; dx <= r && !caveFound; dx += 2)
				{
					int x = World.WX / 2 + dx, z = World.WZ / 2 + dz;
					for (int y = want + 20; y > want - 20 && !caveFound; y--)
						if (Open(x, y, z) && Open(x + 3, y, z) && Open(x - 3, y, z) && Open(x, y, z + 3) && Open(x, y, z - 3))
						{
							caveFound = true;
							player.Pos = new Vector3(x + 0.5f, y, z + 0.5f); player.Flying = true; player.Pitch = -0.2f;
							for (int ox = -9; ox <= 9; ox += 3) for (int oz = -9; oz <= 9; oz += 3)
								for (int ty = y + 4; ty > y - 10; ty--)
									if (world.GetBlock(x + ox, ty, z + oz) == 0 && world.GetBlock(x + ox, ty - 1, z + oz) is int u && u != 0 && !Blocks.B[u].Transparent) { world.SetBlock(x + ox, ty, z + oz, Blocks.Torch); break; }
							GD.Print($"cavetest: at {x},{y},{z}: stone layers here " + string.Join(" ", Enumerable.Range(-12, 25).Select(d => Blocks.StoneLayer(world.GetBlock(x, y + d, z + 4)))));
						}
				}
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
			for (int s = 0; s < 400; s++) world.TickTimers(1);
			GD.Print("farm after 403s (soil/crop): " + Soil());
			var (fx, fz) = At(1);
			Check("wet + fertilized is ripe", world.GetBlock(fx, y + 1, fz) == Blocks.GrainRipe);
			double Avg(int a, int b) => Enumerable.Range(a, b - a + 1).Average(k => { var (x, z) = At(k); return world.GetBlock(x, y + 1, z); });
			Check("wet grows faster than dry (on average)", Avg(2, 6) > Avg(7, 8));
			{   // random growth: how long 2000 plants take from seed to ripe, at each growing speed
				var r = new Random(1);
				foreach (var (name, rate) in new[] { ("wet", 1f), ("dry", World.DryRate), ("wet+fertilized", World.FertRate) })
				{
					var times = new List<int>();
					for (int p = 0; p < 2000; p++) { int t = 0, st = 0; while (st < 7 * World.StageHits) { t++; if (r.NextDouble() < World.StageChance(rate, 1)) st++; } times.Add(t); }
					times.Sort(); double want = 7 * World.StageTime / rate;
					GD.Print($"ripe ({name}): average {times.Average():0}s (fixed timers gave {want:0}s), half of them between {times[500]}s and {times[1500]}s, fastest {times[0]}s, slowest {times[^1]}s");
					Check($"random growth keeps the average ({name})", Math.Abs(times.Average() - want) < want * 0.05);
				}
			}
			world.SetBlock(fx, y + 1, fz, 0);
			Check("a harvest uses up one of the fertilizer's 2", world.FertLeft.GetValueOrDefault(World.Idx(fx, y, fz)) == 1);
			Check("Flour by hand, Bread in the kiln, Bread is food", Inventory.Recipes.Any(r => r.Out == Blocks.Flour) && Kilns.Smelts.Any(s => s.Out == Blocks.Bread && s.From == Blocks.Flour) && Blocks.Get(Blocks.Bread)?.Food > 0);
			inv.SetSlot(0, Blocks.Bread); player.Pitch = -0.45f;
		}
		if (args.Contains("--snowlog") && !snowTested && !menuOpen)
		{   // (test: snow round torches and flowers, put down and taken away the way you would, looking straight down at each spot)
			snowTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int x0 = (int)player.Pos.X + 3, z0 = (int)player.Pos.Z, y = world.Heights[z0 * World.WX + x0] + 1;
			for (int dx = -1; dx < 14; dx++) for (int dz = -2; dz <= 2; dz++)
			{
				world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Grass);
				for (int u = 0; u < 5; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0);
			}
			void Aim(int x, int yy, int z) { player.Pos = new Vector3(x + 0.5f, yy + 1.2f, z + 0.5f); player.Flying = true; player.Pitch = -MathF.PI / 2 + 0.001f; player.Yaw = 0; }
			void Hold(int id) { inv.SetSlot(0, id); SelectSlot(0); }
			// a flower planted in a layer of snow keeps it; more Snow on it does nothing (one layer only)
			int f = Blocks.Flowers[0];
			world.SetBlock(x0, y, z0, Blocks.Snow);
			Aim(x0, y, z0); Hold(f); Place();
			Check("a flower planted in snow keeps the snow", world.GetBlock(x0, y, z0) == Blocks.WithSnow(f, 1));
			Hold(Blocks.Snow); Aim(x0, y, z0); Place();
			Check("more snow on it does nothing", world.GetBlock(x0, y, z0) == Blocks.WithSnow(f, 1));
			// snow put on a plain flower; breaking takes the flower and leaves the snow
			world.SetBlock(x0 + 2, y, z0, f); Aim(x0 + 2, y, z0); Hold(Blocks.Snow); Place();
			Check("snow on a flower", world.GetBlock(x0 + 2, y, z0) == Blocks.WithSnow(f, 1));
			Aim(x0 + 2, y, z0); Hold(0); Break();
			Check("breaking it takes the flower, the snow stays", world.GetBlock(x0 + 2, y, z0) == Blocks.Snow);
			// a torch in snow; another added; it burns down and keeps its snow
			world.SetBlock(x0 + 4, y - 1, z0, Blocks.Stone);
			world.SetBlock(x0 + 4, y, z0, Blocks.WithSnow(Blocks.Torch, 1));
			Aim(x0 + 4, y, z0); Hold(Blocks.Torch); Place();
			Check("a second torch joins it in the snow", world.GetBlock(x0 + 4, y, z0) == Blocks.WithSnow(Blocks.TorchVariant(0, 2, 0), 1));
			Check("it still gives light", world.Torches.Contains(World.Idx(x0 + 4, y, z0)));
			for (int s = 0; s < 3000; s++) world.TickTimers(1);
			Check("burnt down, the snow is still round it", Blocks.SnowIn(world.GetBlock(x0 + 4, y, z0)) == 1 && Blocks.B[world.GetBlock(x0 + 4, y, z0)].Burn == 2);
			world.SetBlock(x0 + 6, y, z0, Blocks.Snow); Aim(x0 + 6, y, z0); Hold(Blocks.Torch); Place();
			Check("a torch put on a layer of snow stands in it", world.GetBlock(x0 + 6, y, z0) == Blocks.WithSnow(Blocks.Torch, 1));
			// a wall torch in snow: its wall goes, the snow stays; the ground goes, everything falls
			world.SetBlock(x0 + 8, y, z0, Blocks.Stone);
			world.SetBlock(x0 + 9, y, z0, Blocks.WithSnow(Blocks.TorchVariant(Blocks.WallIndex(-1, 0), 1, 0), 1));
			world.SetBlock(x0 + 8, y, z0, 0); world.DropTorchesAround(x0 + 8, y, z0, DropFor);
			Check("wall gone: the torch falls, the snow stays", world.GetBlock(x0 + 9, y, z0) == Blocks.Snow);
			world.SetBlock(x0 + 10, y, z0, Blocks.WithSnow(f, 1));
			world.SetBlock(x0 + 10, y - 1, z0, 0); world.DropTorchesAround(x0 + 10, y - 1, z0, DropFor);
			Check("ground gone: the flower and its snow fall", world.GetBlock(x0 + 10, y, z0) == 0);
			// stairs, slabs and pieces: put into snow, snow put on them, and the last piece taken away leaves the snow
			world.SetBlock(x0 + 12, y, z0, Blocks.Snow);
			world.SetSpots(x0 + 12, y, z0, 0x33 | 0x88, Blocks.Planks);   // (stairs)
			Check("stairs put into snow keep it", world.GetBlock(x0 + 12, y, z0) == Blocks.SnowyPieces);
			world.SetSpots(x0 + 12, y, z0, 0x33 | 0x88, 0);
			Check("the stairs taken away leave the snow", world.GetBlock(x0 + 12, y, z0) == Blocks.Snow);
			world.SetSpots(x0 + 11, y, z0, 0x33, Blocks.Planks);   // (a slab)
			Aim(x0 + 11, y, z0); Hold(Blocks.Snow); Place();
			Check("snow put on a slab lies round it", world.GetBlock(x0 + 11, y, z0) == Blocks.SnowyPieces && world.PieceMap.ContainsKey(World.Idx(x0 + 11, y, z0)));
			// a rock on snow, and the Cutter
			world.SetBlock(x0 + 13, y, z0, Blocks.Snow); Aim(x0 + 13, y, z0); Hold(Blocks.Rock); Place();
			Check("a rock put on a layer of snow lies on it", world.GetBlock(x0 + 13, y, z0) == Blocks.WithSnow(Blocks.RockBlock, 1));
			int rolls = Enumerable.Range(0, 2000).Select(_ => Blocks.SnowballsFor(3, rnd)).Distinct().Count();
			Check("snow breaks into 2-4 Snowballs a layer (3 layers: 6-12)", Enumerable.Range(0, 2000).All(_ => Blocks.SnowballsFor(3, rnd) is >= 6 and <= 12) && rolls == 7);
			Check("4 Snowballs make a Snow Block, which makes 8 Snow", Inventory.Recipes.Any(r => r.Out == Blocks.SnowBlock && r.Needs.Contains((Blocks.Snowball, 4))) && Inventory.Recipes.Any(r => r.Out == Blocks.Snow && r.N == 8 && r.Needs.Contains((Blocks.SnowBlock, 1))));
			Check("numbers match the web version (torches then flowers from 400; 388-392 pieces, Cutter, rock, clay ball, brick; 706 Snowball)",
				Blocks.WithSnow(Blocks.Torch, 1) == 400 && Blocks.WithSnow(Blocks.Flowers[4], 1) == 460 && Blocks.WithSnow(Blocks.Pieces, 1) == 388 && Blocks.WithSnow(Blocks.Cutter, 1) == 389
				&& Blocks.WithSnow(Blocks.RockBlock, 1) == 390 && Blocks.WithSnow(Blocks.ClayBallBlock, 1) == 391 && Blocks.WithSnow(Blocks.BrickBlock, 1) == 392 && Blocks.B[706]?.Name == "Snowball");
			// a row to look at: each kind standing in snow
			for (int dx = 0; dx < 14; dx++) world.SetBlock(x0 + dx, y, z0 + 2, 0);
			world.SetBlock(x0, y, z0 + 2, Blocks.WithSnow(Blocks.Flowers[2], 1));
			world.SetBlock(x0 + 1, y, z0 + 2, Blocks.WithSnow(Blocks.Torch, 1));
			world.SetBlock(x0 + 2, y, z0 + 2, Blocks.WithSnow(Blocks.RockBlock, 1));
			world.SetBlock(x0 + 3, y, z0 + 2, Blocks.WithSnow(Blocks.Cutter, 1));
			world.SetBlock(x0 + 4, y, z0 + 2, Blocks.Snow); world.SetSpots(x0 + 4, y, z0 + 2, 0x33 | 0x88, Blocks.Planks);
			world.SetBlock(x0 + 5, y, z0 + 2, Blocks.Snow); world.SetPiece(x0 + 5, y, z0 + 2, 0, 3);
			world.SetBlock(x0 + 6, y, z0 + 2, Blocks.Snow); world.SetSpots(x0 + 6, y, z0 + 2, 0x0C | 0xC0, Blocks.Planks);   // (a top slab)
			world.SetBlock(x0 + 7, y, z0 + 2, Blocks.Snow);
			player.Pos = new Vector3(x0 + 3.5f, y + 2.6f, z0 + 4.5f); player.Yaw = 0; player.Pitch = -0.75f; Hold(Blocks.Snowball);
		}
		if (args.Contains("--throwtest") && !throwTested && !menuOpen)
		{   // (test: throw Snowballs at a wall and at a cow, and check the new sounds are there)
			throwTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int x0 = (int)player.Pos.X, z0 = (int)player.Pos.Z, y = world.Heights[z0 * World.WX + x0] + 1;
			for (int dx = -2; dx <= 2; dx++) for (int dz = -12; dz <= 2; dz++) { world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Stone); for (int u = 0; u < 5; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0); }
			for (int dx = -2; dx <= 2; dx++) for (int u = 0; u < 5; u++) world.SetBlock(x0 + dx, y + u, z0 - 12, Blocks.Stone);
			player.Pos = new Vector3(x0 + 0.5f, y, z0 + 0.5f); player.Vel = Vector3.Zero; player.Yaw = 0; player.Pitch = 0; player.Flying = false;
			inv.SetSlot(0, Blocks.Snowball, 5); SelectSlot(0); throwCool = 0;
			int before = rocks.Count;
			Check("right-click with a Snowball throws it", ThrowSnowball() && rocks.Count == before + 1 && rocks[^1].Item == Blocks.Snowball);
			for (int i = 0; i < 100 && rocks.Any(r => r.Item == Blocks.Snowball); i++) UpdateRocks(0.02f);
			Check("it bursts on the wall, with a puff", !rocks.Any(r => r.Item == Blocks.Snowball) && bits.Count == 8);
			var cow = SpawnMob("cow", x0 + 0.5f, y, z0 - 5.5f); int hp = cow.Health; cow.Vx = cow.Vz = 0;
			throwCool = 0; player.Pitch = -0.05f; ThrowSnowball();
			for (int i = 0; i < 100 && rocks.Any(r => r.Item == Blocks.Snowball); i++) UpdateRocks(0.02f);
			Check("a cow hit by a Snowball is pushed back, unharmed", cow.Vz < -1 && cow.Health == hp);
			Check("the thrower isn't hit by their own Snowball", player.Vel.Length() < 0.01f);
			var rk = MakeRock(player.EyePos + new Vector3(0, 0, -1)); rk.V = new Vector3(0, 0, 0);
			Check("a thrown Rock is a flat picture", rk.Mesh.Mesh is QuadMesh); RemoveRock(rk);
			foreach (var k in new[] { "snow:step", "snow:dig", "snow:place", "throw:say", "splat:say" }) Check("sound " + k + " is made", Sound.Loudness(k) > 0.1f);
			world.SetBlock(x0, y, z0, Blocks.Snow);
			Check("walking in snow sounds like snow", player.BlockUnder() == Blocks.Snow && Sound.MatOf(Blocks.Snow) == "snow" && Sound.MatOf(Blocks.SnowBlock) == "snow");
			Check("a torch in snow breaks like a torch", Sound.MatOf(Blocks.WithSnow(Blocks.Torch, 1)) == "wood");
			Check("lying logs sound like wood", Enumerable.Range(0, 16).All(k => Sound.MatOf(Blocks.TurnedLogs + k) == "wood"));
			world.SetBlock(x0, y, z0, 0); RemoveMob(cow);
		}
		if (throwTested && !throwShown && shotTimer > 1.2f) { throwShown = true; var e = player.EyePos; MakeRock(e + new Vector3(-0.35f, 0, -1.4f)).V = new Vector3(0, 3, 0); MakeRock(e + new Vector3(0.35f, 0, -1.4f), Blocks.Snowball).V = new Vector3(0, 3, 0); }   // (thrown things to look at)
		if (args.Contains("--hudtest") && !hudTested && !menuOpen && inv.Survival && shotTimer > 0.3f)
		{   // (test, survival: the Fireclay recipe, the tool row beside the hotbar and its highlight, cows' hearts in the dark)
			hudTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			var fc = new Inventory(true); fc.Give(63); fc.Give(Blocks.Sand); fc.Give(11);
			Check("Clay + Sand + Gravel make Fireclay at the bench", Inventory.Recipes.Any(r => r.Out == Blocks.Fireclay && r.At == "bench" && fc.CanCraft(r)));
			inv.SetSlot(Inventory.BeltSlot, Blocks.Belts[1]);
			inv.SetSlot(Inventory.Tool0, Blocks.Tools[1]); inv.SetSlot(Inventory.Tool0 + 2, Blocks.CrudeAxe, 1, 10); inv.SetSlot(Inventory.Tool0 + 4, 649 + 1, 1, 200);
			SelectSlot(0);
			Check("the tool row shows a slot for each tool slot", toolSlots.Count(s => s.Visible) == inv.ToolCount && inv.ToolCount > 5);
			UsedToolSlot(Inventory.Tool0 + 2, Blocks.CrudeAxe);
			Check("the tool picked for you is highlighted", toolSlots[2].Selected && toolSlots.Count(s => s.Selected) == 1);
			UsedToolSlot(inv.Selected, inv.Held);
			Check("using what's in your hand leaves the highlight", toolSlots[2].Selected);
			inv.SetSlot(Inventory.Tool0 + 2, 0);
			Check("it goes when the tool leaves its slot", toolSlots.All(s => !s.Selected));
			inv.SetSlot(Inventory.Tool0 + 2, Blocks.CrudeAxe, 1, 10); UsedToolSlot(Inventory.Tool0 + 4, 649 + 1);
			// a cow's heart deep underground at night is dark, one in the open at noon is bright
			int x = (int)player.Pos.X, z = (int)player.Pos.Z, top = world.Heights[z * World.WX + x];
			sky.TimeOfDay = 0.0f; AddHeart(new Vector3(x + 0.5f, top - 12.5f, z + 0.5f));
			var dark = (Vector3)hearts[^1].Mat.GetShaderParameter("light");
			sky.TimeOfDay = 0.5f; AddHeart(new Vector3(x + 0.5f, top + 3.5f, z + 0.5f));
			var lit = (Vector3)hearts[^1].Mat.GetShaderParameter("light");
			Check($"hearts take the light where they are (cave {dark.X:0.00}, open {lit.X:0.00})", dark.X < 0.3f && lit.X > 0.7f);
			sky.TimeOfDay = 0.3f;
			for (int k = 0; k < 6; k++) AddHeart(player.EyePos + new Vector3(-0.6f + k * 0.25f, -0.2f, -1.6f), k * 0.15f);   // (to look at)
		}
		if (args.Contains("--heldtest") && !heldTested && !menuOpen && inv.Survival && shotTimer > 0.5f)
		{   // (test, survival: the held stack, shift-click, Sort, the creative tabs, How to make, the Cutter's search, the pause
			//  screen and settings, the armor bar and the tool row's place)
			heldTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			void Empty() { for (int k = 0; k < Inventory.NS; k++) if (k != Inventory.BackSlot && k != Inventory.BeltSlot) inv.SetSlot(k, 0); ClearHeld(); }
			int P = Blocks.Planks, St = Blocks.Stone, max = Blocks.StackMaxOf(P), p0 = Inventory.Pocket0;
			Empty();
			inv.SetSlot(0, P, 10); PickUp(0, inv.Count[0]);
			Check("click picks up the whole stack", heldId == P && heldN == 10 && inv.Id[0] == 0);
			PutDown(p0);
			Check("click again puts it down", heldId == 0 && inv.Id[p0] == P && inv.Count[p0] == 10);
			PickUp(p0, (inv.Count[p0] + 1) / 2);
			Check("right-click picks up half (the bigger half)", heldN == 5 && inv.Count[p0] == 5);
			PutOne(p0 + 1);
			Check("right-click with a stack held puts down one", heldN == 4 && inv.Id[p0 + 1] == P && inv.Count[p0 + 1] == 1);
			spreading = true; spreadBtn = MouseButton.Left; spreadSlots.Clear(); spreadSlots.AddRange(new[] { p0 + 2, p0 + 3 }); EndSpread();
			Check("a left drag shares the held stack out evenly", heldId == 0 && inv.Count[p0 + 2] == 2 && inv.Count[p0 + 3] == 2);
			PickUp(p0, inv.Count[p0]);   // (5 held)
			spreading = true; spreadBtn = MouseButton.Right; spreadSlots.Clear(); spreadSlots.AddRange(new[] { p0 + 4, p0 + 5, p0 + 6 }); EndSpread();
			Check("a right drag puts one in each", heldN == 2 && inv.Count[p0 + 4] == 1 && inv.Count[p0 + 5] == 1 && inv.Count[p0 + 6] == 1);
			inv.SetSlot(1, St, 3); PutDown(1);
			Check("putting down on a different item swaps them", heldId == St && heldN == 3 && inv.Id[1] == P && inv.Count[1] == 2);
			ReturnHeld(false);
			Check("closing puts the held stack back", heldId == 0 && inv.CountOf(St) == 3);
			PickUp(p0 + 1, 1); Gather();
			Check("double-click gathers the same item", heldN == 10 && inv.CountOf(P) == 0);
			Empty();
			int pick = Blocks.Tools.First(t => Blocks.B[t].Tool == "pickaxe"), helmet = Blocks.GearItems.First(g => Blocks.B[g].Armor == 0);
			inv.SetSlot(p0, pick); QuickMove(p0);
			Check("shift-click from pockets: a tool goes to a free tool slot first", inv.Id[Inventory.Tool0] == pick && inv.Id[p0] == 0);
			inv.SetSlot(3, helmet); QuickMove(3);
			Check("shift-click from the hotbar: armor goes on", inv.Id[Inventory.Armor0] == helmet && inv.Id[3] == 0);
			QuickMove(Inventory.Armor0);
			Check("shift-click from a gear slot: to the hotbar", inv.Id[0] == helmet);
			inv.SetSlot(5, P, 5); inv.SetSlot(p0 + 3, P, 10); QuickMove(p0 + 3);
			Check("shift-click from pockets tops up the hotbar's stack first", inv.Count[5] == 15 && inv.Id[p0 + 3] == 0);
			QuickMove(5);
			Check("shift-click from the hotbar goes to the pockets", inv.Id[5] == 0 && inv.Id[p0] == P && inv.Count[p0] == 15);
			Empty();
			inv.SetSlot(p0 + 5, Blocks.Stick, 3); inv.SetSlot(p0 + 1, P, 7); inv.SetSlot(p0 + 8, Blocks.Cobblestone, 4); inv.SetSlot(p0 + 2, P, max - 2);
			SortPockets();
			Check("Sort: same items merged, in the tabs' order (Building, Wood, ..., Materials), fullest first",
				inv.Id[p0] == Blocks.Cobblestone && inv.Id[p0 + 1] == P && inv.Count[p0 + 1] == max && inv.Id[p0 + 2] == P && inv.Count[p0 + 2] == 5 && inv.Id[p0 + 3] == Blocks.Stick && inv.Id[p0 + 4] == 0);
			Empty();
			Check("Hearts in a tooltip: 2 -> 0.5, 4 -> 1, 6 -> 1.5", Hearts(2) == "0.5" && Hearts(4) == "1" && Hearts(6) == "1.5");
			Check("every creative block and item is in a tab", AllCreative().All(i => TabPlace(i).at != int.MaxValue) && CreativeTabs.Length == 9);
			ShowHowTo(Blocks.FormItem("stairs", Blocks.Stone));
			Check("How to make Stone Stairs: the Cutter, 3 Stone make 4", howPanel.Visible && howTitle.Text == "How to make Stone Stairs" && howList.GetChildren().Count(c => !c.IsQueuedForDeletion()) == 1);
			ShowHowTo(Blocks.Grass);
			Check("How to make Grass: can't be made", howList.GetChildren().Where(c => !c.IsQueuedForDeletion()).OfType<Label>().Any(l => l.Text.StartsWith("Can't be made")));
			CloseInventory();
			Check("closing How to make closes the screen", !howPanel.Visible && !invOpen);
			cutSearch.Text = "oak"; FilterCutter();
			Check("the Cutter's search finds only what matches", cutSlots.Where(c => c.s.Visible).All(c => Blocks.B[c.id].Name.ToLowerInvariant().Contains("oak")) && cutSlots.Any(c => c.s.Visible));
			cutSearch.Text = ""; cutCan.ButtonPressed = true; FilterCutter();
			Check("the Cutter's \"Can make now\" shows nothing when you have no blocks", cutSlots.All(c => !c.s.Visible));
			cutCan.ButtonPressed = false; FilterCutter();
			Check("the pause screen has all its buttons (Save & quit was missing)", quitBtn.IsInsideTree() && discardBtn.IsInsideTree() && saveBtn.IsInsideTree());
			Check("Settings in four tabs: Video, Sound, Controls, Game", settingsPages.Select(p => p.tab.Text).SequenceEqual(new[] { "Video", "Sound", "Controls", "Game" }));
			int Piece(int k) => Blocks.GearItems.First(g => Blocks.B[g].Armor == k && Blocks.B[g].Material == "iron");
			int Wear(params int[] ks) { for (int k = 0; k < 4; k++) inv.SetSlot(Inventory.Armor0 + k, ks.Contains(k) ? Piece(k) : 0); DrawArmorBar(); return ArmorQuarters(inv.ArmorBlock()); }
			string Icons() => string.Join(",", Enumerable.Range(0, HEARTS).Select(j => ArmorIconQuarters(j)));   // (quarters in each icon, left to right)
			Check("armor bar quarters: helmet 4, chest 8, legs 5, boots 3, full set 20, chest + legs 13, none 0",
				Wear(0) == 4 && Wear(1) == 8 && Wear(2) == 5 && Wear(3) == 3 && Wear(0, 1, 2, 3) == 20 && Wear(1, 2) == 13 && Wear() == 0);
			Check("the armor bar mirrors the hearts: as many, shown (empty) with no armor on", armorShown && armorIcons.Length == HEARTS && armorIcons.All(t => t.Texture == armorPics[0]));
			Wear(0);
			Check($"a helmet: the rightmost icon full, the rest empty ({Icons()})", Icons() == "0,0,0,0,4" && armorIcons[HEARTS - 1].Texture == armorPics[4] && armorIcons.All(t => !t.FlipH));
			Wear(2);
			Check($"leggings: one and a quarter from the right, drawn as they are ({Icons()})", Icons() == "0,0,0,1,4" && armorIcons[HEARTS - 2].Texture == armorPics[1] && !armorIcons[HEARTS - 2].FlipH);
			Wear(3);
			Check($"boots: three quarters ({Icons()})", Icons() == "0,0,0,0,3" && armorIcons[HEARTS - 1].Texture == armorPics[3]);
			Wear(1, 2);   // (left on, to see where the bar goes)
			Check($"chest + legs: three and a quarter ({Icons()})", Icons() == "0,1,4,4,4" && armorIcons[1].Texture == armorPics[1]);
		}
		if (args.Contains("--heldtest") && heldTested && !heldPlaced && shotTimer > 1.5f)
		{   // (a frame later: where the tool row and armor bar went)
			heldPlaced = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			var bar = hotBarBox.GetGlobalRect();
			if (toolRow.Position.Y > bar.Position.Y) Check("the tool row sits 4 from the hotbar", Math.Abs(toolRow.Position.X - (bar.End.X + 4)) < 0.5f);
			else Check("the tool row sits over the hotbar's right end, 4 above the armor bar", Math.Abs(toolRow.GetGlobalRect().End.X - bar.End.X) < 0.5f && Math.Abs(armorBar.GetGlobalRect().Position.Y - 4 - toolRow.GetGlobalRect().End.Y) < 0.5f);
			Check("the offhand slot sits 4 from the hotbar", Math.Abs(hotBarBox.GetGlobalRect().Position.X - offBox.GetGlobalRect().End.X - 4) < 0.5f);
			var hr = heartsRect.GetGlobalRect(); var ar = armorBar.GetGlobalRect();
			Check($"the armor bar: the hearts' size and line, its right end at the hotbar's (hearts {hr}, armor {ar}, hotbar {bar})",
				ar.Size == hr.Size && Math.Abs(ar.Position.Y - hr.Position.Y) < 0.5f && Math.Abs(ar.End.X - bar.End.X) < 0.5f && Math.Abs(hr.Position.X - bar.Position.X) < 0.5f);
		}
		if (args.Contains("--stagetest") && !stageTested && !StageBreakingOn && !menuOpen && inv.Survival && shotTimer > 0.3f)
		{   // (test, survival, stage breaking held back: everything breaks as in Beta 0.1.2.0, and new worlds are land 1)
			stageTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int Tool(string kind, string mat) => Blocks.Tools.First(t => Blocks.B[t].Tool == kind && Blocks.B[t].Material == mat);
			int x0 = (int)player.Pos.X + 3, z0 = (int)player.Pos.Z + 3, y = world.Heights[z0 * World.WX + x0] + 1;
			for (int dx = -1; dx < 14; dx++) for (int dz = -1; dz < 8; dz++) { world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Stone); for (int u = 0; u < 9; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0); }
			void Grow(int x, int yy, int z, int b) { world.SetBlock(x, yy, z, b); world.Edits.Remove(World.Idx(x, yy, z)); }   // (as if the world grew it)
			void Aim(int x, int yy, int z) { player.Pos = new Vector3(x + 0.5f, yy + 1.2f, z + 0.5f); player.Flying = true; player.Pitch = -MathF.PI / 2 + 0.001f; player.Yaw = 0; }
			void Hold(int id) { inv.SetSlot(0, id); SelectSlot(0); for (int k = Inventory.Tool0; k < Inventory.Off0; k++) inv.SetSlot(k, 0); }
			void AimSide(int x, int yy, int z) { player.Pos = new Vector3(x + 0.5f, yy - 1, z + 1.8f); player.Flying = true; player.Pitch = 0; player.Yaw = 0; }   // (from the side, level with it)
			int Swings(int x, int yy, int z, int max = 40, bool side = false) { int b = world.GetBlock(x, yy, z), n = 0; if (side) AimSide(x, yy, z); else Aim(x, yy, z); while (world.GetBlock(x, yy, z) == b && n < max) { inv.Act = -1; Break(); n++; } return n; }
			int Dropped(int id) => drops.All.Where(d => d.Id == id).Count();
			void Clear() { foreach (var d in drops.All.ToArray()) drops.Remove(d); }
			Grow(x0, y, z0, Blocks.Stone); Hold(0); Aim(x0, y, z0); Break();
			Check("OFF: Stone by hand needs a pickaxe", world.GetBlock(x0, y, z0) == Blocks.Stone);
			Clear(); Hold(Tool("pickaxe", "stone"));
			Check("OFF: Stone: 1 click with a stone pickaxe, 1 Cobblestone", Swings(x0, y, z0) == 1 && Dropped(8) == 1);
			Grow(x0 + 1, y, z0, Blocks.Strata + 6);
			Check("OFF: layer-8 Stone is too deep for a stone pickaxe", Swings(x0 + 1, y, z0, 3) == 3 && world.GetBlock(x0 + 1, y, z0) == Blocks.Strata + 6);
			Grow(x0 + 3, y, z0, Blocks.IronOre); Hold(Tool("pickaxe", "iron")); Clear();
			Check("OFF: Iron Ore: 1 click, 1 Chunk", Swings(x0 + 3, y, z0) == 1 && Dropped(Blocks.Chunk["iron"]) == 1);
			int deep = Blocks.DeepOreOf(1, 8); Grow(x0 + 4, y, z0, deep);
			Check("OFF: layer-8 Iron Ore is too deep for an iron pickaxe", Swings(x0 + 4, y, z0, 3) == 3 && world.GetBlock(x0 + 4, y, z0) == deep);
			Hold(Tool("pickaxe", "steel")); Clear();
			Check("OFF: layer-8 Iron Ore with a steel pickaxe: 1 click, 1 Chunk", Swings(x0 + 4, y, z0) == 1 && Dropped(Blocks.Chunk["iron"]) == 1);
			Grow(x0 + 9, y, z0, Blocks.Sandstone); Hold(0); Clear(); Aim(x0 + 9, y, z0); Break();
			Check("OFF: Sandstone by hand needs a pickaxe (as in desktop's Beta 0.1.2.0)", world.GetBlock(x0 + 9, y, z0) == Blocks.Sandstone);
			Hold(Tool("pickaxe", "stone"));
			Check("OFF: Sandstone: 1 click with a pickaxe, gives itself (the drop fix stays)", Swings(x0 + 9, y, z0) == 1 && Dropped(Blocks.Sandstone) == 1);
			Grow(x0, y, z0 + 2, Blocks.Log); Hold(0);
			Check("OFF: a log by hand needs an axe", Swings(x0, y, z0 + 2, 3) == 3 && world.GetBlock(x0, y, z0 + 2) == Blocks.Log);
			void Tree(int tx, int tz) { for (int u = 0; u < 4; u++) Grow(tx, y + u, tz, Blocks.Log); for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++) for (int u = 3; u <= 4; u++) if (world.GetBlock(tx + dx, y + u, tz + dz) == 0) Grow(tx + dx, y + u, tz + dz, Blocks.Leaves); }
			Tree(x0 + 5, z0 + 5); Hold(Tool("axe", "steel")); Clear();
			int clicks = Swings(x0 + 5, y, z0 + 5, side: true);
			if (!TreeFellingOn) Check("OFF: a tree's bottom log: 1 click, and the tree stays up", clicks == 1 && world.GetBlock(x0 + 5, y + 1, z0 + 5) == Blocks.Log && Dropped(Blocks.Log) == 1);
			else
			{   // felling on its own (stage breaking off): an axe breaks the bottom log in one click, as always, and the tree comes down
				Check($"FELLING ALONE: a tree's bottom log, 1 click with an axe, fells the tree ({Dropped(Blocks.Log)} logs)", clicks == 1 && Enumerable.Range(0, 6).All(u => world.GetBlock(x0 + 5, y + u, z0 + 5) == 0) && world.GetBlock(x0 + 4, y + 3, z0 + 5) == 0 && Dropped(Blocks.Log) == 4);
				Tree(x0 + 9, z0 + 5); Swings(x0 + 9, y + 1, z0 + 5, side: true);
				Check("FELLING ALONE: breaking a middle log fells nothing", world.GetBlock(x0 + 9, y + 2, z0 + 5) == Blocks.Log && world.GetBlock(x0 + 9, y, z0 + 5) == Blocks.Log);
				Tree(x0 + 1, z0 + 5); world.SetBlock(x0 + 1, y + 4, z0 + 5, Blocks.Log);   // (a log put down on top)
				Swings(x0 + 1, y, z0 + 5, side: true);
				Check("FELLING ALONE: a put-down log touching a tree stays up", world.GetBlock(x0 + 1, y + 4, z0 + 5) == Blocks.Log && world.GetBlock(x0 + 1, y + 2, z0 + 5) == 0);
			}
			Check("OFF: nothing half broken, no cracks", stages.Count == 0);
			Check($"new worlds are land {(World.OreSpreadOn ? 2 : 1)} (this one: {current.Land})", World.NewLand == (World.OreSpreadOn ? 2 : 1) && current.Land == World.NewLand);
		}
		if (args.Contains("--stagetest") && !stageTested && !menuOpen && inv.Survival && shotTimer > 0.3f)
		{   // (test, survival: stage breaking of Stone, ores and logs, natural or put down, and tree felling)
			stageTested = true;
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int Tool(string kind, string mat) => Blocks.Tools.First(t => Blocks.B[t].Tool == kind && Blocks.B[t].Material == mat);
			int x0 = (int)player.Pos.X + 3, z0 = (int)player.Pos.Z + 3, y = world.Heights[z0 * World.WX + x0] + 1;
			for (int dx = -1; dx < 14; dx++) for (int dz = -1; dz < 8; dz++) { world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Stone); for (int u = 0; u < 9; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0); }
			void Grow(int x, int yy, int z, int b) { world.SetBlock(x, yy, z, b); world.Edits.Remove(World.Idx(x, yy, z)); }   // (as if the world grew it)
			void Aim(int x, int yy, int z) { player.Pos = new Vector3(x + 0.5f, yy + 1.2f, z + 0.5f); player.Flying = true; player.Pitch = -MathF.PI / 2 + 0.001f; player.Yaw = 0; }
			void Hold(int id) { inv.SetSlot(0, id); SelectSlot(0); for (int k = Inventory.Tool0; k < Inventory.Off0; k++) inv.SetSlot(k, 0); }
			void AimSide(int x, int yy, int z) { player.Pos = new Vector3(x + 0.5f, yy - 1, z + 1.8f); player.Flying = true; player.Pitch = 0; player.Yaw = 0; }   // (from the side, level with it)
			int Swings(int x, int yy, int z, int max = 40, bool side = false) { int b = world.GetBlock(x, yy, z), n = 0; if (side) AimSide(x, yy, z); else Aim(x, yy, z); while (world.GetBlock(x, yy, z) == b && n < max) { inv.Act = -1; Break(); n++; } return n; }
			int Dropped(int id) => drops.All.Where(d => d.Id == id).Count();
			void Clear() { foreach (var d in drops.All.ToArray()) drops.Remove(d); }
			// Stone
			Grow(x0, y, z0, Blocks.Stone); Hold(0); Aim(x0, y, z0); Break(); Break();
			Check("natural Stone by hand: nothing happens", world.GetBlock(x0, y, z0) == Blocks.Stone);
			Clear(); Hold(Tool("pickaxe", "stone"));
			Check("natural Stone: 4 swings with a stone pickaxe, 1 Cobblestone", Swings(x0, y, z0) == 4 && Dropped(8) == 1);
			Grow(x0 + 1, y, z0, Blocks.Strata + 6); Clear();
			Check("natural layer-8 Stone: 32 swings with a stone pickaxe (no longer too deep)", Swings(x0 + 1, y, z0) == 32 && Dropped(Blocks.DeepCobble + 6) == 1);
			Grow(x0 + 2, y, z0, Blocks.Strata + 6); Hold(Tool("pickaxe", "steel"));
			Check("layer-8 Stone: 1 swing with a steel pickaxe", Swings(x0 + 2, y, z0) == 1);
			// ores
			Grow(x0 + 3, y, z0, Blocks.IronOre); Hold(Tool("pickaxe", "iron")); Clear();
			Check("Iron Ore: 1 swing with an iron pickaxe, 1 Chunk", Swings(x0 + 3, y, z0) == 1 && Dropped(Blocks.Chunk["iron"]) == 1);
			int deep = Blocks.DeepOreOf(1, 8); Grow(x0 + 4, y, z0, deep); Clear();
			Check($"layer-8 Iron Ore ({deep}): 8 swings with an iron pickaxe, 8 Chunks", deep == 483 && Swings(x0 + 4, y, z0) == 8 && Dropped(Blocks.Chunk["iron"]) == 8);
			Grow(x0 + 5, y, z0, deep); Hold(Tool("pickaxe", "stone"));
			Check("layer-8 Iron Ore: a stone pickaxe can't", Swings(x0 + 5, y, z0, 5) == 5 && world.GetBlock(x0 + 5, y, z0) == deep);
			// put down: the old way
			world.SetBlock(x0 + 6, y, z0, Blocks.Stone); Clear();
			Check("put-down Stone: 1 click", Swings(x0 + 6, y, z0) == 1 && Dropped(8) == 1);
			world.SetBlock(x0 + 7, y, z0, Blocks.Strata + 6);
			Check("put-down layer-8 Stone: too deep for a stone pickaxe", Swings(x0 + 7, y, z0, 3) == 3 && world.GetBlock(x0 + 7, y, z0) == Blocks.Strata + 6);
			world.SetBlock(x0 + 8, y, z0, deep); Hold(Tool("pickaxe", "steel")); Clear();
			Check("put-down deep ore: 1 click, 1 Chunk", Swings(x0 + 8, y, z0) == 1 && Dropped(Blocks.Chunk["iron"]) == 1);
			Grow(x0 + 9, y, z0, Blocks.Sandstone); Hold(Tool("pickaxe", "stone")); Clear();
			Check("natural Sandstone: 4 swings, gives itself", Swings(x0 + 9, y, z0) == 4 && Dropped(Blocks.Sandstone) == 1);
			Grow(x0 + 10, y, z0, 65);
			Check("natural Terracotta: 4 swings", Swings(x0 + 10, y, z0) == 4);
			// logs
			Grow(x0, y, z0 + 2, Blocks.Log); Hold(0); Clear();
			Check("a natural log: 8 swings by hand, gives the log", Swings(x0, y, z0 + 2) == 8 && Dropped(Blocks.Log) == 1);
			Grow(x0 + 1, y, z0 + 2, Blocks.Log); Hold(Tool("axe", "stone"));
			Check("a natural log: 2 swings with a stone axe", Swings(x0 + 1, y, z0 + 2) == 2);
			world.SetBlock(x0 + 2, y, z0 + 2, Blocks.Log); Hold(0);
			Check("a put-down log by hand: needs an axe", Swings(x0 + 2, y, z0 + 2, 3) == 3 && world.GetBlock(x0 + 2, y, z0 + 2) == Blocks.Log);
			// a tree: trunk of 4 with a crown of leaves; breaking its bottom log fells it all
			void Tree(int tx, int tz) { for (int u = 0; u < 4; u++) Grow(tx, y + u, tz, Blocks.Log); for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++) for (int u = 3; u <= 4; u++) if (world.GetBlock(tx + dx, y + u, tz + dz) == 0) Grow(tx + dx, y + u, tz + dz, Blocks.Leaves); }
			Tree(x0 + 5, z0 + 5); Hold(Tool("axe", "steel")); Clear(); felled = 0;
			Swings(x0 + 5, y, z0 + 5, side: true);
			bool gone = Enumerable.Range(0, 6).All(u => world.GetBlock(x0 + 5, y + u, z0 + 5) == 0 && world.GetBlock(x0 + 4, y + 3, z0 + 5) == 0);
			if (!TreeFellingOn) Check("STAGES ALONE: a tree's bottom log comes out, the tree stays up", world.GetBlock(x0 + 5, y, z0 + 5) == 0 && world.GetBlock(x0 + 5, y + 1, z0 + 5) == Blocks.Log);
			else {
			Check($"breaking a tree's bottom log fells the tree ({felled} more blocks, {Dropped(Blocks.Log)} logs)", gone && Dropped(Blocks.Log) == 4);
			Tree(x0 + 9, z0 + 5); Swings(x0 + 9, y + 1, z0 + 5, side: true);
			Check("breaking a middle log fells nothing", world.GetBlock(x0 + 9, y + 2, z0 + 5) == Blocks.Log && world.GetBlock(x0 + 9, y, z0 + 5) == Blocks.Log);
			for (int u = 0; u < 3; u++) Grow(x0 + 12, y + u, z0 + 5, Blocks.Log);
			Swings(x0 + 12, y, z0 + 5, side: true);
			Check("a pile of logs with no leaves stays up", world.GetBlock(x0 + 12, y + 1, z0 + 5) == Blocks.Log);
			Tree(x0 + 1, z0 + 5); world.SetBlock(x0 + 1, y + 4, z0 + 5, Blocks.Log);   // (a log put down on top)
			Swings(x0 + 1, y, z0 + 5, side: true);
			Check("a put-down log touching a tree stays up", world.GetBlock(x0 + 1, y + 4, z0 + 5) == Blocks.Log && world.GetBlock(x0 + 1, y + 2, z0 + 5) == 0);
			}   // (felling on)
			Check($"new worlds are land {(World.OreSpreadOn ? 2 : 1)} (this one: {current.Land})", World.NewLand == (World.OreSpreadOn ? 2 : 1) && current.Land == World.NewLand);
			// half broken, to look at
			Grow(x0 + 3, y, z0 + 4, Blocks.Stone); Hold(Tool("pickaxe", "stone")); Aim(x0 + 3, y, z0 + 4); Break(); Break();
			Grow(x0 + 4, y, z0 + 4, Blocks.Log); Hold(0); Aim(x0 + 4, y, z0 + 4); for (int k = 0; k < 7; k++) Break();
			Check("half-broken blocks stay, with their progress", stages.Count == 2);
			player.Pos = new Vector3(x0 + 3.5f, y + 1, z0 + 1.2f); player.Pitch = -0.45f; player.Yaw = MathF.PI;
		}
		if (args.Contains("--cutshapes") && !cutTested && !menuOpen)
		{   // (test: slabs, stairs, quarters and pieces of stone, grass and terracotta to look at, and their icons in the hotbar)
			cutTested = true;
			int x0 = (int)player.Pos.X + 2, z0 = (int)player.Pos.Z - 4, y = world.Heights[z0 * World.WX + x0] + 1;
			for (int dx = -1; dx < 16; dx++) for (int dz = -1; dz < 8; dz++) { world.SetBlock(x0 + dx, y - 1, z0 + dz, Blocks.Stone); for (int u = 0; u < 4; u++) world.SetBlock(x0 + dx, y + u, z0 + dz, 0); }
			int[] masks = { 0x33, 0xCC, 0x33 | 0x88, 0x55, 0x30, 0x01, 0xFF ^ 0xA0, 0x33 | 0x44 };
			int[] mats = { Blocks.Stone, 1, 65 };
			for (int r = 0; r < mats.Length; r++) for (int k = 0; k < masks.Length; k++) world.SetSpots(x0 + k * 2, y, z0 + r * 2, masks[k], mats[r]);
			// double slabs and loose pieces: nothing joins together any more
			void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
			int r4 = z0 + 6;
			world.SetSpots(x0, y, r4, 0x33, Blocks.Stone); world.SetSpots(x0, y, r4, 0xCC, Blocks.Stone);
			Check("two slabs stay a double slab", Blocks.IsPieces(world.GetBlock(x0, y, r4)));
			var ds = world.PieceMap[World.Idx(x0, y, r4)];
			Check("its top slab is a slab on its own", World.ShapeAt(ds, 2) is { shape: "slab", mask: 0xCC });
			world.SetSpots(x0, y, r4, World.ShapeAt(ds, 2).Value.mask, 0);
			Check("taking the top slab leaves the bottom one", Blocks.IsPieces(world.GetBlock(x0, y, r4)) && World.ShapeAt(world.PieceMap[World.Idx(x0, y, r4)], 0) is { shape: "slab" });
			world.SetSpots(x0, y, r4, 0xCC, Blocks.Stone);   // (and back, to look at)
			for (int s = 0; s < 8; s++) world.SetPiece(x0 + 2, y, r4, s, 65);
			Check("8 pieces stay 8 pieces", Blocks.IsPieces(world.GetBlock(x0 + 2, y, r4)) && Enumerable.Range(0, 8).All(s => World.ShapeAt(world.PieceMap[World.Idx(x0 + 2, y, r4)], s) == null));
			for (int s = 0; s < 4; s++) world.SetPiece(x0 + 4, y, r4, new[] { 0, 1, 4, 5 }[s], Blocks.Stone);
			Check("4 pieces side by side aren't a slab", World.ShapeAt(world.PieceMap[World.Idx(x0 + 4, y, r4)], 0) == null);
			world.SetSpots(x0 + 6, y, r4, 0x33, 1); world.SetSpots(x0 + 6, y, r4, 0xCC, 1);   // (a grass double slab)
			world.SetSpots(x0 + 8, y, r4, 0x33, 65); world.SetSpots(x0 + 8, y, r4, 0xCC, 65);
			// snow on the stone row's slabs and stairs: a layer on every top
			for (int k = 0; k < masks.Length; k++) world.SetBlock(x0 + k * 2, y, z0, Blocks.SnowyPieces);
			world.SetSpots(x0 + 12, y, r4, 0x33, Blocks.Stone); world.SetBlock(x0 + 12, y, r4, Blocks.SnowyPieces);
			world.SetSpots(x0 + 12, y, r4, 0x33, 0);
			Check("the last slab gone from snow on stone leaves the Snow", world.GetBlock(x0 + 12, y, r4) == Blocks.Snow);
			world.SetBlock(x0 + 12, y - 1, r4, 0); world.SetBlock(x0 + 12, y, r4, 0);
			world.SetSpots(x0 + 12, y, r4, 0xCC, Blocks.Stone); world.SetBlock(x0 + 12, y, r4, Blocks.SnowyPieces);
			world.SetSpots(x0 + 12, y, r4, 0xCC, 0);
			Check("the last slab gone from snow over a hole leaves air", world.GetBlock(x0 + 12, y, r4) == 0);
			world.SetBlock(x0 + 12, y - 1, r4, Blocks.Stone);
			string[] forms = { "slab", "stairs", "vslab", "quarter", "vstairs", "piece" };
			for (int k = 0; k < forms.Length; k++) inv.SetSlot(k, Blocks.FormItem(forms[k], Blocks.Stone));
			inv.SetSlot(6, Blocks.FormItem("slab", 1)); inv.SetSlot(7, Blocks.FormItem("stairs", 65));
			SelectSlot(1);
			player.Pos = new Vector3(x0 + 4.5f, y + 4.5f, z0 + 10f); player.Flying = true; player.Yaw = 0; player.Pitch = -0.95f;
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
		if (!testGear && inv.Survival && (args.Contains("--wear") || args.Contains("--tools")))
		{   // (test: armor on, and tools in the tool slots, to see the armor bar and the tool row with)
			testGear = true;
			if (args.Contains("--wear"))
				foreach (int id in Blocks.GearItems.Where(i => Blocks.Get(i)?.Armor >= 0 && Blocks.B[i].Material == "iron")) inv.SetSlot(Inventory.Armor0 + Blocks.B[id].Armor, id);
			if (args.Contains("--tools"))
			{
				var tools = Blocks.Tools.Where(t => Blocks.B[t].Material == "iron").ToArray();
				for (int k = 0; k < Math.Min(tools.Length, inv.ToolCount); k++) inv.SetSlot(Inventory.Tool0 + k, tools[k]);
			}
		}
		string open = args.FirstOrDefault(a => a.StartsWith("--open="));
		if (open is "--open=worlds" or "--open=settings" or "--open=pause" or "--open=menu" or "--open=new")
		{
			if (open == "--open=worlds" && !worldsPanel.Visible) ShowWorlds(true);
			if (open == "--open=settings") { ShowSettings(true); if (ArgInt("--tab=", -1) >= 0) ShowSettingsTab(ArgInt("--tab=", 0)); }
			if (open == "--open=new" && !whatsNewPanel.Visible) whatsNewPanel.Visible = true;
			if (open == "--open=pause" && !pauseMenu.Visible) { if (menuOpen) ShowMainMenu(false); pauseMenu.Visible = true; UpdateSaveText(); Input.MouseMode = Input.MouseModeEnum.Visible; }
		}
		else if (open != null && !invOpen)
		{
			if (args.Contains("--stuff") && inv.Survival)   // (test: a few things in your slots, to see the recipe book with)
			{
				foreach (var (id, n) in new[] { (Blocks.Planks, 12), (Blocks.Stick, 5), (Blocks.Cobblestone, 9), (Blocks.Stone, 1), (Blocks.Rock, 3), (Blocks.Log, 4) })
					for (int k = 0; k < n; k++) inv.Give(id);
			}
			if (open == "--open=howto") { OpenPanel(invPanel); ShowHowTo(Blocks.FormItem("stairs", Blocks.Stone)); }
			else if (open == "--open=kiln") { int x = (int)player.Pos.X, y = (int)player.Pos.Y + 3, z = (int)player.Pos.Z; world.SetBlock(x, y, z, Blocks.Furnace); kilnAt = World.Idx(x, y, z); var f = kilns.Of(kilnAt); f.Queue.Add(Blocks.Brick); f.Queue.Add(Blocks.Stone); f.Done[Blocks.Terracotta] = 3; OpenPanel(kilnPanel); }
			else if (open == "--open=bench") { benchBlock = Blocks.Planks; benchMode = 3; OpenBench(true); }
			else if (open == "--open=craftbench") OpenBench(false);
			else OpenPanel(invPanel);
		}
		lock (dirty) if (dirty.Count > 0) return;
		shotTimer += dt;
		var stay = args.FirstOrDefault(a => a.StartsWith("--stay="));
		if (shotTimer < (stay != null ? float.Parse(stay[7..]) : sceneBuilt ? 4f : 1.5f)) return;
		if (args.FirstOrDefault(a => a.StartsWith("--panorama=")) is { } panoArg) { if (!panoCapturing) CapturePanorama(panoArg[11..]); return; }
		GetViewport().GetTexture().GetImage().SavePng(arg[7..]);
		GD.Print($"shot saved, {chunkParts.Count} chunks in {groupNodes.Count} groups");
		foreach (var m in mobList) GD.Print($"mob {m.Type}: layer {m.Layer}, health {m.Health}{(m.Burning ? ", burning" : "")}{(m.Fade > 0 ? ", fading" : "")} at y {m.Y:0.0}");
		GD.Print($"your health {health}, rocks flying {rocks.Count}");
		if (fallAt is var (tx, tg, tz)) GD.Print($"falltest: {falling.All.Count} still falling; ground {world.GetBlock(tx, tg, tz)} at y {tg}, then " + string.Join(", ", Enumerable.Range(1, 5).Select(u => world.GetBlock(tx, tg + u, tz))) + $"; you at {player.Pos} yaw {player.Yaw} pitch {player.Pitch}; falling at " + string.Join(" ", falling.All.Select(b => $"{b.Mesh.GlobalPosition}{(b.Mesh.IsVisibleInTree() ? "" : " hidden")}")));
		GetTree().Quit();
	}

	// scripted checks of the hotbar, crafting, bench, kiln and drops (prints PASS / FAIL lines)
	void SelfTest()
	{
		void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
		Check($"bedrock along the bottom of the world ({World.WY} tall)", world.GetBlock(100, 0, 100) == Blocks.Bedrock && world.GetBlock(500, 0, 37) == Blocks.Bedrock
			&& Blocks.Items.Contains(Blocks.Bedrock) && Blocks.Items.Contains(Blocks.Strata) == World.TallWorlds);
		// the chunk palette: block ids past 255, and a chunk holding more than 256 kinds of block (2-byte places)
		{
			var bs = new BlockStore(32, 32, 32);
			bs.Set(1, 2, 3, 70000); bs.Set(17, 2, 3, 300);
			for (int n = 0; n < 600; n++) bs.Set(n % 16, n / 256, (n / 16) % 16, 1000 + n);
			bool ok = bs.Get(17, 2, 3) == 300 && bs.Get(0, 0, 0) == 1000 && bs.Get(599 % 16, 599 / 256, (599 / 16) % 16) == 1599 && bs.Get(20, 20, 20) == 0;
			for (int n = 0; n < 600 && ok; n++) ok = bs[World.Idx(0, 0, 0) + 0] == 1000 && bs.Get(n % 16, n / 256, (n / 16) % 16) == 1000 + n;
			bs.Set(17, 2, 3, 0);
			Check("chunk palette: ids past 255 and chunks with more than 256 kinds of block", ok && bs.Get(17, 2, 3) == 0 && bs.Usage().bytes > 8192);
		}
		// Pack & Plate: ores in the ground (gold only deep, or at every depth from Land 2), and the counts to compare with the web version
		var oreCount = new int[5]; int goldShallow = 0;
		for (int q = 0; q < world.Blocks.Length; q++)
		{
			int ob = world.Blocks[q]; if (ob < Blocks.CopperOre || ob > Blocks.Fireclay) continue;
			oreCount[ob - Blocks.CopperOre]++;
			if (ob == Blocks.GoldOre) { var (ox, oy, oz) = World.FromIdx(q); if (world.Heights[oz * World.WX + ox] - oy < 20) goldShallow++; }
		}
		GD.Print("ores 142-146: " + string.Join(", ", oreCount));
		if (current.Land >= 2) Check("ores and fireclay are in the ground, gold at every depth (Land 2)", oreCount.All(n => n > 0) && goldShallow > 0);
		else Check("ores and fireclay are in the ground, gold only deep", oreCount.All(n => n > 0) && goldShallow == 0);
		Check("ores need the right pickaxe", Blocks.B[Blocks.CopperOre].Pick == Blocks.PickTier["stone"] && Blocks.B[Blocks.IronOre].Pick == Blocks.PickTier["copper"]
			&& Blocks.B[Blocks.TinOre].Pick == Blocks.PickTier["iron"] && Blocks.B[Blocks.GoldOre].Pick == Blocks.PickTier["iron"] && Blocks.B[Blocks.IronOre].Drop == Blocks.Chunk["iron"]);
		// the Foundry: only works when it's all built
		{
			int fx = (int)player.Pos.X, fy = (int)player.Pos.Y + 12, fz = (int)player.Pos.Z;
			for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
			{
				world.SetBlock(fx + dx, fy - 1, fz + dz, Blocks.FireclayTerracotta); world.SetBlock(fx + dx, fy + 1, fz + dz, Blocks.FireclayBricks);
				world.SetBlock(fx + dx, fy, fz + dz, dx != 0 || dz != 0 ? Blocks.FireclayCasing : 0);
			}
			world.SetBlock(fx, fy, fz + 1, Blocks.Foundry);   // facing +z: the middle is behind it
			var fk = new Kilns(world) { Survival = false }; int fi = World.Idx(fx, fy, fz + 1);
			bool built = fk.FoundryBuilt(fi);
			world.SetBlock(fx - 1, fy, fz, 0);
			fk.Of(fi).Queue.Add(Blocks.MetalBrick["bronite"]);
			for (int t = 0; t < 80; t++) fk.Tick(0.1f, null);
			bool waited = fk.Of(fi).Done.Count == 0 && !Blocks.B[world.Blocks[fi]].Lit;
			world.SetBlock(fx - 1, fy, fz, Blocks.FireclayCasing);
			for (int t = 0; t < 80; t++) fk.Tick(0.1f, null);
			Check("the Foundry waits until it's all built, then makes Bronite", built && waited && fk.Of(fi).Done.GetValueOrDefault(Blocks.MetalBrick["bronite"]) == 1);
			Check("Foundry recipes: chunks, alloys, steel; the Kiln fires Fireclay", Kilns.Smelts.Count(m => m.At == "foundry") == 8
				&& Kilns.Smelts.Any(m => m.Out == Blocks.FireclayBrick && m.At == "kiln") && Inventory.Recipes.Any(r => r.Out == Blocks.Foundry) && Inventory.Recipes.Any(r => r.Out == 646));
		}
		// tool durability: a stone pickaxe lasts 160 uses, its wear moves with it, and it breaks at the end
		{
			var w = new Inventory(true); w.Give(Blocks.Tools[1]); w.Selected = 0;
			bool none = w.WearTool(159) == null && w.Id[0] == Blocks.Tools[1];
			w.Move(0, 3); bool moved = w.Wear[3] == 159 && w.Wear[0] == 0;
			w.Selected = 3; bool broke = w.WearTool(1) == "Stone Pickaxe" && w.Id[3] == 0;
			w.Give(Blocks.Tools[1], 40); bool kept = w.Wear[Array.IndexOf(w.Id, Blocks.Tools[1])] == 40;
			var cw = new Inventory(false); cw.SetSlot(0, Blocks.Tools[1]); cw.Selected = 0; cw.WearTool(500);
			Check("tools wear out and break; the wear moves and drops with them; creative tools never wear", none && moved && broke && kept && cw.Id[0] == Blocks.Tools[1]
				&& Inventory.MaxWear(649 + 1) == 480 && Inventory.MaxWear(Blocks.CrudeAxe) == 32 && Inventory.MaxWear(Blocks.Stick) == 0);
		}
		// gear: Pockets fill after the hotbar; a backpack opens its slots and keeps what's in it when taken off; slots only take
		// what fits; a belt's offhand slots empty out when it comes off; a full set of armor takes half; armor wears and breaks;
		// the tool slots give the longest-lasting tool of a kind
		{
			const int P = Inventory.Pocket0, B0 = Inventory.Pack0;
			var g = new Inventory(true);
			for (int k = 0; k < 10; k++) g.SetSlot(k, Blocks.Dirt, 64);
			g.Give(Blocks.Stick); bool pocket = g.Id[P] == Blocks.Stick && !g.SlotOpen(B0);
			g.SetSlot(P + 1, Blocks.Backpacks[2]);
			bool worn = g.Move(P + 1, Inventory.BackSlot) && g.PackSize == 15 && g.SlotOpen(B0 + 14) && !g.SlotOpen(B0 + 15);
			g.SetSlot(B0, Blocks.Rock, 5);
			g.Move(Inventory.BackSlot, P + 1); bool kept = g.Data[P + 1] != null && g.Id[B0] == 0 && g.PackSize == 0;
			g.Move(P + 1, Inventory.BackSlot); bool back = g.Id[B0] == Blocks.Rock && g.Count[B0] == 5;
			bool fits = !g.Fits(Inventory.Tool0, Blocks.Dirt) && g.Fits(Inventory.Tool0, Blocks.Tools[1]) && g.Fits(Inventory.Armor0 + 1, Blocks.ArmorItem(2, 1))
				&& !g.Fits(Inventory.Armor0, Blocks.ArmorItem(2, 1)) && !g.Fits(B0, Blocks.Backpacks[0]) && !g.Move(P + 2 - 2, Inventory.Armor0);
			int thrown = 0; g.ThrowOut = k => { thrown++; g.SetSlot(k, 0); return true; };
			g.SetSlot(P + 2, Blocks.Belts[3]); bool six = g.Move(P + 2, Inventory.BeltSlot) && g.OffCount == 6;
			g.SetSlot(Inventory.Off0 + 4, Blocks.Torch, 3); g.Move(Inventory.BeltSlot, P + 2); bool out1 = thrown == 1 && g.OffCount == 1;
			g.Move(P + 2, Inventory.BeltSlot); bool tools = g.ToolCount == 10 && g.SlotOpen(Inventory.Tool0 + 9) && !g.SlotOpen(Inventory.Tool0 + 10);
			g.SetSlot(Inventory.Tool0 + 9, Blocks.Tools[0]); g.Move(Inventory.BeltSlot, P + 2); out1 &= tools && thrown == 2 && g.ToolCount == 5;
			for (int k = 0; k < 4; k++) g.SetSlot(Inventory.Armor0 + k, Blocks.ArmorItem(0, k));
			bool half = Math.Abs(g.ArmorBlock() - 0.5f) < 0.001f;
			g.Wear[Inventory.Armor0] = 99; var broke = g.WearArmor();
			bool brk = broke.Count == 1 && broke[0] == "Leather Helmet" && g.Id[Inventory.Armor0] == 0 && g.Wear[Inventory.Armor0 + 1] == 1 && Inventory.MaxWear(Blocks.ArmorItem(5, 0)) == 900;
			g.SetSlot(Inventory.Tool0, Blocks.Tools[1]); g.SetSlot(Inventory.Tool0 + 1, 649 + 1); bool best = g.BestTool("pickaxe") == Inventory.Tool0 + 1 && g.BestTool("hoe") == -1;
			Check($"gear: pockets {pocket}, backpack {worn}/{kept}/{back}, fits {fits}, belt {six}/{out1}, armor {half}/{brk}, tool slots {best}", pocket && worn && kept && back && fits && six && out1 && half && brk && best
				&& Blocks.StackMaxOf(Blocks.Belts[0]) == 1 && Inventory.Recipes.Any(r => r.Out == Blocks.ArmorItem(5, 1) && r.Needs[0] == (Blocks.SteelBrick, 5)));
		}
		var s = new Inventory(true);
		for (int k = 0; k < 2; k++) s.Give(Blocks.Rock); s.Give(Blocks.Stick);
		var axe = Inventory.Recipes.First(r => r.Out == Blocks.CrudeAxe);
		Check("survival: 2 rocks + stick can make a Crude Axe", s.CanCraft(axe));
		// the Cutter: made at the bench from 3 Planks + a Copper Brick; cutting moved off the Crafting Bench onto it
		Check("the Cutter: a block made from 3 Planks + a Copper Brick at the bench", Blocks.B[Blocks.Cutter]?.Name == "Cutter" && Blocks.Items.Contains(Blocks.Cutter)
			&& Inventory.Recipes.Any(r => r.Out == Blocks.Cutter && r.At == "bench" && r.Needs.Contains((Blocks.Planks, 3)) && r.Needs.Contains((Blocks.MetalBrick["copper"], 1))));
		Check("the Cutter is half a block tall and its saw blade spins (frame 1, then frame 2, then frame 1)", Blocks.B[Blocks.Cutter].Shape == "half" && Atlas.SawSpins());
		Check("crafting it works", s.Craft(axe) == true && s.CountOf(Blocks.CrudeAxe) == 1 && s.CountOf(Blocks.Rock) == 0 && s.CountOf(Blocks.Stick) == 0);
		s.Give(Blocks.Log);
		var planks = Inventory.Recipes.First(r => r.Out == Blocks.Planks && r.Needs[0].id == Blocks.Log);
		Check("log -> 4 planks with an axe", s.Craft(planks) == true && s.CountOf(Blocks.Planks) == 4);
		Check("bench: planks need 3 for stairs", s.BenchCut(Blocks.Planks, 3).StartsWith("Made 4") && s.CountOf(1200 + Blocks.Planks) == 4 && s.CountOf(Blocks.Planks) == 1);
		Check("bench: 1 plank -> 8 pieces", s.BenchCut(Blocks.Planks, 0).StartsWith("Took 8") && s.CountOf(Blocks.PieceItem + Blocks.Planks) == 8);
		s.Give(Blocks.FirLog);
		var firPlanks = Inventory.Recipes.First(r => r.Out == Blocks.FirPlanks && r.Needs[0].id == Blocks.FirLog);
		Check("Fir: the original wood is now Oak, and a Fir Log -> 4 Fir Planks (which cut into Fir Planks Stairs)", Blocks.B[Blocks.Log].Name == "Oak Log" && s.Craft(firPlanks) == true
			&& s.CountOf(Blocks.FirPlanks) == 4 && s.BenchCut(Blocks.FirPlanks, 3).StartsWith("Made 4") && Blocks.Get(1200 + Blocks.FirPlanks)?.Name == "Fir Planks Stairs");
		s.Give(Blocks.PalmLog);
		var palmPlanks = Inventory.Recipes.First(r => r.Out == Blocks.PalmPlanks && r.Needs[0].id == Blocks.PalmLog);
		Check("desert and swamp: a Palm Log -> 4 Palm Planks (which cut into stairs); Mud fires into Mud Bricks; Peat is fuel; Dates are food",
			s.Craft(palmPlanks) == true && s.CountOf(Blocks.PalmPlanks) == 4 && s.BenchCut(Blocks.PalmPlanks, 3).StartsWith("Made 4")
			&& s.CountOf(Blocks.FormItem("stairs", Blocks.PalmPlanks)) == 4 && Blocks.Get(Blocks.FormItem("stairs", Blocks.PalmPlanks))?.Name == "Palm Planks Stairs"
			&& Kilns.Smelts.Any(m => m.Out == Blocks.MudBricks && m.From == Blocks.Mud) && Kilns.Fuel[Blocks.Peat] == 8 && Blocks.B[Blocks.Dates].Food > 0
			&& Blocks.B[Blocks.WillowLeaves].T[0] != Blocks.B[Blocks.Leaves].T[0] && Blocks.TakesBiomeTint(Blocks.PalmLeaves));
		// deep stone sets: every piece / slab / stairs item has its own number, and each layer's stone gives its own cobblestone
		var forms = Blocks.Splittable.SelectMany(b => Blocks.Forms.Select(f => (b, f, id: Blocks.FormItem(f, b)))).ToArray();
		Check("deep sets: all piece and shape items are different", forms.Select(x => x.id).Distinct().Count() == forms.Length
			&& forms.All(x => x.f == "piece" ? Blocks.B[x.id].Piece == x.b : Blocks.B[x.id].Of == x.b && Blocks.B[x.id].Form == x.f));
		Check("deep sets: layer 5 stone gives layer 5 cobblestone (which smelts back while tall worlds are on)", Blocks.B[Blocks.Strata + 3].Drop == Blocks.DeepCobble + 3 && Blocks.B[Blocks.DeepCobble + 3].Layer == 5
			&& Kilns.Smelts.Any(m => m.Out == Blocks.Strata + 3 && m.From == Blocks.DeepCobble + 3) == World.TallWorlds && Blocks.B[Blocks.FormItem("slab", Blocks.DeepBricks + 3)].Of == Blocks.DeepBricks + 3);
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
		world.SetBlock(cx + 3, cy + 1, cz + 1, Blocks.Cutter);                                   // the Cutter (half a block tall, its saw blade standing up)
		for (int k = 0; k < 12; k++)
		{   // along the back: every colour's bricks, terracotta and chiseled terracotta, bottom to top
			world.SetBlock(cx - 6 + k, cy + 1, cz - 3, Blocks.ColourBricks[k]); world.SetBlock(cx - 6 + k, cy + 2, cz - 3, Blocks.ColourTerracotta[k]);
			world.SetBlock(cx - 6 + k, cy + 3, cz - 3, Blocks.ColourChiseled[k]);
		}
		for (int k = 0; k < 5; k++) { world.SetBlock(cx - 2 + k, cy, cz + 2, Blocks.Grass); world.SetBlock(cx - 2 + k, cy + 1, cz + 2, Blocks.Flowers[k]); }   // the flowers, on grass
		player.Pos = new Vector3(cx + 0.5f, cy + 1.5f, cz + 7.5f); player.Flying = true; player.Yaw = 0; player.Pitch = -0.35f;
		GD.Print($"burn timers: {world.TorchBurn.Count}  (expect 2: lit + smoldering)");
	}

	// ---- render distance (also how far out mobs are simulated) ----
	const int MinRender = 4, MaxRender = 32;
	const float ForgetMobs = 128;   // (mobs other than cows are forgotten past this, or the render distance if it's nearer)
	float RenderBlocks => (settings?.RenderDist ?? 10) * World.CS;   // (in blocks)
	float FogThickness => 0.045f * 10 / (settings?.RenderDist ?? 10);   // (the haze thins out as you see further, so 10 chunks looks like it always did)
	void ApplyRenderDist()
	{
		RenderingServer.GlobalShaderParameterSet("fog_edge", RenderBlocks);
		if (!wasUnder) RenderingServer.GlobalShaderParameterSet("fog_thickness", FogThickness);
		lastGroupAt = -1;   // (show and hide groups, and build the newly near ones, straight away)
	}
	// how far (sideways) the camera is from the nearest edge of group g
	static float GroupDist(int g, float px, float pz)
	{
		const int S = World.CS * World.GS;
		var (gx, _, gz) = World.FromGroupKey(g);
		float dx = Math.Max(0, Math.Max(gx * S - px, px - (gx + 1) * S)), dz = Math.Max(0, Math.Max(gz * S - pz, pz - (gz + 1) * S));
		return MathF.Sqrt(dx * dx + dz * dz);
	}
	// how far up or down the camera is from group g (tall worlds: deep caves and the sky far above aren't built or drawn)
	static float GroupDistY(int g, float py)
	{
		const int S = World.CS * World.GS;
		var (_, gy, _) = World.FromGroupKey(g);
		return Math.Max(0, Math.Max(gy * S - py, py - (gy + 1) * S));
	}
	const int ViewUpDown = 64;
	bool InRange(int g) => player == null || GroupDist(g, player.Pos.X, player.Pos.Z) < RenderBlocks && GroupDistY(g, player.Pos.Y) < RenderBlocks + ViewUpDown;
	int lastGroupAt = -1;
	// whenever you move into another group: hide the groups past the render distance, show the ones within it, and
	// let the chunks that were too far to build get built if they're near enough now
	void CullGroups()
	{
		const int S = World.CS * World.GS;
		int at = ((int)(player.Pos.X / S) * 1000 + (int)(player.Pos.Z / S)) * 100 + (int)Math.Floor(player.Pos.Y / S);
		if (at == lastGroupAt) return;
		lastGroupAt = at;
		foreach (var (g, n) in groupNodes) { bool v = InRange(g); n.solid.Visible = v; n.water.Visible = v; }
		lock (dirty) { foreach (int c in far) dirty.Add(c); far.Clear(); }
	}

	// rebuild changed chunks, nearest first, for up to a few milliseconds a frame (several at once, on all CPU cores)
	void BuildChunks()
	{
		CullGroups();
		var start = Time.GetTicksUsec();
		// (nearest GROUP first, and a group's chunks together, so each group gets glued together once)
		const int S = World.CS * World.GS;
		int pgx = (int)player.Pos.X / S, pgy = (int)player.Pos.Y / S, pgz = (int)player.Pos.Z / S;
		int Dist(int c) { int g = World.GroupOf(c); var (gx, gy, gz) = World.FromGroupKey(g); int dx = gx - pgx, dy = gy - pgy, dz = gz - pgz; return (dx * dx + dy * dy + dz * dz) * World.NGroups + g; }
		var touched = new HashSet<int>();
		try { BuildSome(start, Dist, touched); }
		finally { foreach (int g in touched) MergeGroup(g); }
	}
	void BuildSome(ulong start, Func<int, int> Dist, HashSet<int> touched)
	{
		int want = Math.Max(2, System.Environment.ProcessorCount);
		var best = new int[want]; var bestD = new int[want];
		while (Time.GetTicksUsec() - start < 8000)
		{
			int[] batch;
			lock (dirty)
			{
				if (dirty.Count == 0 && soon.Count > 0) { foreach (int c in soon) dirty.Add(c); soon.Clear(); }
				if (dirty.Count == 0) return;
				// the nearest few (one pass, keeping a short sorted list, rather than sorting thousands of chunks every time)
				int n = 0; List<int> tooFar = null;
				foreach (int c in dirty)
				{
					if (!InRange(World.GroupOf(c))) { (tooFar ??= new()).Add(c); continue; }
					int d = Dist(c);
					if (n == want && d >= bestD[n - 1]) continue;
					int j = n < want ? n++ : n - 1;
					while (j > 0 && bestD[j - 1] > d) { best[j] = best[j - 1]; bestD[j] = bestD[j - 1]; j--; }
					best[j] = c; bestD[j] = d;
				}
				if (tooFar != null) foreach (int c in tooFar) { dirty.Remove(c); soon.Remove(c); far.Add(c); }
				if (n == 0) return;
				batch = best[..n];
				foreach (int c in batch) { dirty.Remove(c); soon.Remove(c); }
			}
			var parts = new (ChunkMesher.Part, ChunkMesher.Part)[batch.Length];
			Parallel.For(0, batch.Length, i => { var (cx, cy, cz) = World.FromChunkKey(batch[i]); parts[i] = mesher.Build(cx, cy, cz); });
			for (int i = 0; i < batch.Length; i++)
			{
				var (s, w) = parts[i];
				if (s.Ind.Count == 0 && w.Ind.Count == 0) chunkParts.Remove(batch[i]);
				else chunkParts[batch[i]] = (s.Ind.Count > 0 ? s : null, w.Ind.Count > 0 ? w : null);
				touched.Add(World.GroupOf(batch[i]));
			}
		}
	}
	// glue a group's chunks into one mesh for the solid blocks and one for the water
	void MergeGroup(int g)
	{
		var (gx, gy, gz) = World.FromGroupKey(g);
		var solids = new List<ChunkMesher.Part>(); var waters = new List<ChunkMesher.Part>();
		for (int dy = 0; dy < World.GS; dy++) for (int dz = 0; dz < World.GS; dz++) for (int dx = 0; dx < World.GS; dx++)
			if (chunkParts.TryGetValue(World.ChunkKey(gx * World.GS + dx, gy * World.GS + dy, gz * World.GS + dz), out var p))
			{ if (p.solid != null) solids.Add(p.solid); if (p.water != null) waters.Add(p.water); }
		var solid = solids.Count == 0 ? null : solids.Count == 1 ? solids[0].ToMesh() : ChunkMesher.Part.Join(solids).ToMesh();
		var water = waters.Count == 0 ? null : waters.Count == 1 ? waters[0].ToMesh() : ChunkMesher.Part.Join(waters).ToMesh();
		if (!groupNodes.TryGetValue(g, out var n))
		{
			if (solid == null && water == null) return;   // (an empty group, all air or buried rock: no nodes for it)
			n = (new MeshInstance3D { MaterialOverride = blockMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off },
				 new MeshInstance3D { MaterialOverride = waterMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
			AddChild(n.solid); AddChild(n.water);
			groupNodes[g] = n;
			n.solid.Visible = n.water.Visible = InRange(g);
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
	bool gearShown;   // (test: --gear)
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
			if (mb.ButtonIndex == MouseButton.Left) { LeftClick(); holdSwing = SwingTime; }
			else if (mb.ButtonIndex == MouseButton.Right) { inv.Act = ActSlot(mb.ButtonIndex); SwingHand(); try { Place(); } finally { inv.Act = -1; } }
			else if (mb.ButtonIndex == MouseButton.WheelDown) SelectSlot(inv.Selected + 1);
			else if (mb.ButtonIndex == MouseButton.WheelUp) SelectSlot(inv.Selected - 1);
			else if (mb.ButtonIndex == MouseButton.Middle) PickBlock();
		}
		else if (e is InputEventKey k && k.Pressed && !k.Echo)
		{
			if (k.PhysicalKeycode == Key.Escape)
			{   // Esc: closes whatever's open; while playing, pauses
				if (confirm.Visible) return;
				if (settingsPanel.Visible || worldsPanel.Visible || whatsNewPanel.Visible || multiPanel.Visible) { settingsPanel.Visible = whatsNewPanel.Visible = multiPanel.Visible = false; ShowWorlds(false); }
				else if (howPanel?.Visible == true) howPanel.Visible = false;
				else if (invOpen) CloseInventory();
				else if (pauseMenu.Visible) Resume();
				else if (!menuOpen) Input.MouseMode = Input.MouseModeEnum.Visible;
				return;
			}
			if (menuOpen || pauseMenu.Visible || AnyPanel || dead) return;
			if (k.PhysicalKeycode == Key.M) { settings.Muted = !settings.Muted; ApplySettings(); ShowNote(settings.Muted ? "Sound off" : "Sound on"); }
			else if (k.PhysicalKeycode == Key.T && locked) sky.TimeOfDay = (sky.TimeOfDay + 0.125f) % 1;   // skip ahead 1/8 of a day
			else if (k.PhysicalKeycode == Key.F && locked && inv.Survival) SwapOffhand();   // survival: swap the offhand item with your hand
			else if (k.PhysicalKeycode == Key.F && locked) { player.Flying = !player.Flying; player.Vel.Y = 0; }
			else if (k.PhysicalKeycode == Key.R && invOpen) { if (tipSlot != null && GodotObject.IsInstanceValid(tipSlot) && tipSlot.IsVisibleInTree() && tipSlot.ShownId != 0) ShowHowTo(tipSlot.ShownId); }   // R over an item: how to make it
			else if (k.PhysicalKeycode == Key.R && inv.Survival) CycleOffhand();             // the next offhand slot
			else if (k.PhysicalKeycode == Key.Q && inv.Survival) DropFromSlot(invOpen ? HoveredSlot() : (locked ? inv.Selected : -1), k.CtrlPressed || k.ShiftPressed);   // Q drops one, Ctrl+Q (or Shift+Q) the stack
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
