using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace Cubeland;

// The main menu (the first thing you see), the pause screen (Esc), Singleplayer (your worlds), Settings and What's new.
// While the main menu is up, the last world you played grows behind it, so Play is quick.
public partial class Main
{
	// which world to open after switching worlds (the scene restarts, like the web version reloads the page)
	static string openWorldId; static bool playOnLoad;
	Worlds.Info current; bool tempWorld;   // tempWorld: a test world that's never saved
	bool menuOpen = true, wantPlay, unsaved, discarding;
	float saveTimer;
	CanvasLayer menuLayer;
	Control mainMenu, pauseMenu, settingsPanel, worldsPanel, whatsNewPanel, multiPanel;
	Label pauseTitle, worldNameLabel, savedNote, newerNote, settingsNote;
	Button resumeBtn, quitBtn, saveBtn, discardBtn;
	VBoxContainer worldList;
	ConfirmationDialog confirm;

	// ---- Settings (saved in the game's user folder) ----
	public class SettingsData
	{
		public int Sens = 100, Fov = 75, RenderDist = 10; public bool Info = true, Autosave = true, Muted; public string Name = "Player";
		public Dictionary<string, int> Vol = new() { ["master"] = 100, ["block"] = 100, ["player"] = 100, ["mob"] = 100, ["music"] = 100 };
	}
	static SettingsData settings;
	static readonly JsonSerializerOptions SetOpts = new() { IncludeFields = true };
	static void LoadSettings()
	{
		if (settings != null) return;
		try { settings = JsonSerializer.Deserialize<SettingsData>(FileAccess.GetFileAsString("user://settings.json"), SetOpts); } catch { }
		settings ??= new();
		foreach (var k in new SettingsData().Vol.Keys) settings.Vol.TryAdd(k, 100);
	}
	void ApplySettings()
	{
		cam.Fov = settings.Fov;
		settings.RenderDist = Math.Clamp(settings.RenderDist, MinRender, MaxRender);
		ApplyRenderDist();
		info.Visible = settings.Info;
		foreach (var (k, v) in settings.Vol) Sound.Levels[k] = v / 100f;
		Sound.Muted = settings.Muted;
		using var f = FileAccess.Open("user://settings.json", FileAccess.ModeFlags.Write);
		f?.StoreString(JsonSerializer.Serialize(settings, SetOpts));
		UpdateSaveText();
	}
	float MouseSens => 0.0025f * settings.Sens / 100f;

	public static int CompareVersions(string a, string b)
	{
		int[] N(string s) => System.Text.RegularExpressions.Regex.Matches(s ?? "", @"\d+").Select(m => int.Parse(m.Value)).ToArray();
		var x = N(a); var y = N(b);
		for (int i = 0; i < Math.Max(x.Length, y.Length); i++) { int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0; if (p != q) return p < q ? -1 : 1; }
		return 0;
	}

	// ---- which world, at startup ----
	void PickWorld()
	{
		LoadSettings();
		var sa = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--seed="));
		if (sa != null)
		{   // testing: a throwaway world that isn't in your list and never saves
			tempWorld = true;
			current = new Worlds.Info { Id = "test", Name = "Test world", Seed = sa != null ? int.Parse(sa[7..]) : Random.Shared.Next(), Gen = HasArg("--biomes") ? 3 : 2, H = HasArg("--tall") || (World.TallWorlds && !HasArg("--short")) ? 640 : 192, S = HasArg("--small") ? 640 : ArgInt("--size=", 1280), B = ArgInt("--biomescale=", 100) / 100.0, Mode = HasArg("--survival") ? "survival" : "creative" };
			menuOpen = HasArg("--menu");
			return;
		}
		if (joinInfo != null && openWorldId == "join-" + joinInfo.Code)
		{   // a friend's world you're joining: it isn't one of your worlds and isn't saved here
			current = new Worlds.Info { Id = openWorldId, Name = $"{joinInfo.Host}'s world", Seed = joinInfo.Seed, Gen = joinInfo.Gen, H = joinInfo.H, S = joinInfo.S, B = joinInfo.B, Mode = joinInfo.Mode == "survival" ? "survival" : "creative", Ver = Version };
			menuOpen = false; wantPlay = true; playOnLoad = false;
			return;
		}
		joinInfo = null;
		// no world picked: the main menu, with no world at all. A world only opens from Singleplayer (or by joining a friend),
		// and quitting to the main menu closes it. (--play for testing: straight into the last world played.)
		if ((openWorldId == null || Worlds.Get(openWorldId) == null) && !HasArg("--play")) { current = null; menuOpen = true; playOnLoad = false; return; }
		current = (openWorldId != null ? Worlds.Get(openWorldId) : null) ?? Worlds.Last();
		Worlds.MarkOpened(current);
		if (HasArg("--play")) menuOpen = false;   // (testing: straight into the game)
		if (playOnLoad) { menuOpen = false; wantPlay = true; playOnLoad = false; }
	}

	// ---- building the screens ----
	static Button Btn(string text, Action click, int w = 300)
	{
		var b = new Button { Text = text, CustomMinimumSize = new Vector2(w, 44) };
		b.AddThemeFontSizeOverride("font_size", 24);
		b.Pressed += click;
		return b;
	}
	Control Backdrop(float alpha)
	{
		var bg = new ColorRect { Color = new Color(0, 0, 0, alpha), Visible = false };
		bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		menuLayer.AddChild(bg);
		return bg;
	}
	VBoxContainer Centered(Control parent, bool panel)
	{
		var c = new CenterContainer(); c.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); parent.AddChild(c);
		var box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; box.AddThemeConstantOverride("separation", 10);
		if (panel) { var p = new PanelContainer(); p.AddThemeStyleboxOverride("panel", PanelStyle()); c.AddChild(p); p.AddChild(box); }
		else c.AddChild(box);
		return box;
	}
	static Label Centre(Label l) { l.HorizontalAlignment = HorizontalAlignment.Center; return l; }

	void BuildMenus()
	{
		menuLayer = new CanvasLayer { Layer = 5 }; AddChild(menuLayer);
		// main menu
		mainMenu = Backdrop(0.15f);   // (only lightly dimmed: the world turns slowly behind it, see Panorama)
		var mm = Centered(mainMenu, false);
		var title = Centre(Text("CUBELAND", 64)); title.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.6f)); title.AddThemeConstantOverride("shadow_offset_y", 4); mm.AddChild(title);
		mm.AddChild(Centre(Text(Version.Split(' ')[0], 18, 0.8f)));   // (the stage: Alpha, Beta, ...)
		mm.AddChild(new Control { CustomMinimumSize = new Vector2(0, 12) });
		mm.AddChild(Btn("Singleplayer", () => ShowWorlds(true)));
		mm.AddChild(Btn("Multiplayer", () => { RefreshNetUi(); multiPanel.Visible = true; }));
		mm.AddChild(Btn("Skin", OpenSkinEditor));
		mm.AddChild(Btn("Settings", () => ShowSettings(true)));
		mm.AddChild(Btn("What's new", () => whatsNewPanel.Visible = true));
		mm.AddChild(Btn("Quit game", () => { SaveWorld(); GetTree().Quit(); }));
		mm.AddChild(Centre(Text($"{Title} {Version}", 13, 0.75f)));

		// pause screen (Esc), also "Ready" when a world has just opened
		pauseMenu = Backdrop(0.45f);
		var pm = Centered(pauseMenu, false);
		pauseTitle = Centre(Text("Paused", 32)); pm.AddChild(pauseTitle);
		worldNameLabel = Centre(Text("", 15, 0.85f)); pm.AddChild(worldNameLabel);
		resumeBtn = Btn("Back to game", Resume); pm.AddChild(resumeBtn);
		saveBtn = Btn("Save world", () => { savedNote.Text = SaveWorld(true) ? "Saved!" : tempWorld ? "Test worlds don't save." : "Could not save the world."; GetTree().CreateTimer(2.5).Timeout += UpdateSaveText; });
		pm.AddChild(saveBtn);
		pm.AddChild(Btn("Settings", () => ShowSettings(true)));
		pm.AddChild(Btn("Skin", OpenSkinEditor));
		pauseMultiBtn = Btn("Multiplayer", () => { RefreshNetUi(); multiPanel.Visible = true; }); pm.AddChild(pauseMultiBtn);
		quitBtn = Btn("Save & quit to main menu", () => { if (netClient) { LeaveGame(); return; } SwitchWorld(null, false); });   // (closes the world: see PickWorld) pm.AddChild(quitBtn);
		discardBtn = Btn("Quit without saving", () => Ask("Quit without saving?\n\nEverything you did since the last save will be lost.", () => { discarding = true; SwitchWorld(null, false); }));
		pm.AddChild(discardBtn);
		savedNote = Centre(Text("", 13, 0.8f)); pm.AddChild(savedNote);
		newerNote = Centre(Text("", 13)); newerNote.AddThemeColorOverride("font_color", new Color(1, 0.8f, 0.4f)); newerNote.AutowrapMode = TextServer.AutowrapMode.WordSmart; newerNote.CustomMinimumSize = new Vector2(420, 0); pm.AddChild(newerNote);

		BuildSettings(); BuildWorlds(); BuildWhatsNew(); BuildMulti(); BuildSkinEditor();
		PlayerSkin.Changed += SkinChanged;
		confirm = new ConfirmationDialog { Title = "CubeLand", OkButtonText = "OK", CancelButtonText = "Cancel" };
		menuLayer.AddChild(confirm);
		UpdateSaveText();
		ShowMainMenu(menuOpen);
	}
	Action onConfirm;
	void Ask(string text, Action yes)
	{
		if (!IsInsideTree()) return;   // (closing down)
		if (!confirmWired) { confirm.Confirmed += RunConfirm; confirm.Canceled += RunCancel; confirmWired = true; }
		onConfirm = yes; onCancel = null;
		confirm.DialogText = text;
		confirm.PopupCentered();
	}
	bool confirmWired;
	Action onCancel;
	void RunConfirm() => onConfirm?.Invoke();
	void RunCancel() => onCancel?.Invoke();

	void BuildSettings()
	{
		settingsPanel = Backdrop(0.5f);
		var b = Centered(settingsPanel, true);
		b.AddChild(Text("Settings", 20));
		var grid = new GridContainer { Columns = 3 }; grid.AddThemeConstantOverride("h_separation", 14); b.AddChild(grid);
		void Slider(string name, int min, int max, int step, Func<int> get, Action<int> set, string unit)
		{
			var nl = Text(name, 15); nl.CustomMinimumSize = new Vector2(150, 0); grid.AddChild(nl);
			var s = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = get(), CustomMinimumSize = new Vector2(240, 24), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
			var v = Text(get() + unit, 15); v.CustomMinimumSize = new Vector2(56, 0);
			s.ValueChanged += x => { set((int)x); v.Text = (int)x + unit; ApplySettings(); };
			grid.AddChild(s); grid.AddChild(v);
		}
		void Toggle(string name, Func<string> label, Action flip)
		{
			var nl = Text(name, 15); nl.CustomMinimumSize = new Vector2(150, 0); grid.AddChild(nl);
			var t = new Button { Text = label(), CustomMinimumSize = new Vector2(240, 32) };
			t.Pressed += () => { flip(); t.Text = label(); ApplySettings(); };
			grid.AddChild(t); grid.AddChild(new Control());
		}
		Slider("Mouse sensitivity", 20, 300, 10, () => settings.Sens, x => settings.Sens = x, "%");
		Slider("Field of view", 50, 110, 5, () => settings.Fov, x => settings.Fov = x, "");
		Slider("Render distance", MinRender, MaxRender, 1, () => settings.RenderDist, x => settings.RenderDist = x, " chunks");
		Toggle("Sound (M)", () => settings.Muted ? "Off" : "On", () => settings.Muted = !settings.Muted);
		Toggle("FPS and position", () => settings.Info ? "Shown" : "Hidden", () => settings.Info = !settings.Info);
		Toggle("Auto save", () => settings.Autosave ? "On (every 30 seconds)" : "Off", () => settings.Autosave = !settings.Autosave);
		b.AddChild(Text("Volume", 17));
		grid = new GridContainer { Columns = 3 }; grid.AddThemeConstantOverride("h_separation", 14); b.AddChild(grid);
		foreach (var (key, name) in new[] { ("master", "Master"), ("block", "Blocks"), ("player", "Player"), ("mob", "Mobs"), ("music", "Music") })
			Slider(name, 0, 100, 5, () => settings.Vol[key], x => settings.Vol[key] = x, "%");
		b.AddChild(Text("Controls", 17));
		foreach (var line in new[] {
			"WASD move · Mouse look · Space jump · Shift sprint",
			"Left click break · Right click place · 1-9, 0 or scroll wheel pick a block · E inventory",
			"Survival: right-click while holding chocolate to eat it and get health back",
			"F or double-tap Space to fly in creative (Space up, Shift down) · T skips time · Esc pauses" })
			b.AddChild(Text("•  " + line, 13, 0.85f));
		b.AddChild(Btn("Back", () => ShowSettings(false), 160));
	}
	void ShowSettings(bool open) { settingsPanel.Visible = open; }

	void BuildWorlds()
	{
		worldsPanel = Backdrop(0.5f);
		var b = Centered(worldsPanel, true);
		b.AddChild(Text("Singleplayer", 20));
		var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(640, 260), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		b.AddChild(scroll);
		worldList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; worldList.AddThemeConstantOverride("separation", 6);
		scroll.AddChild(worldList);
		b.AddChild(Text("New world", 17));
		var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 8); b.AddChild(row);
		var name = new LineEdit { PlaceholderText = "Name", MaxLength = 32, CustomMinimumSize = new Vector2(150, 34) };
		var seed = new LineEdit { PlaceholderText = "Seed (blank = random)", MaxLength = 24, CustomMinimumSize = new Vector2(170, 34) };
		var gen = new OptionButton { TooltipText = "Terrain style" }; gen.AddItem("Biomes", 3); gen.AddItem("Rolling hills", 2); gen.AddItem("Plateaus", 1);
		var mode = new OptionButton { TooltipText = "Game mode" }; mode.AddItem("Creative"); mode.AddItem("Survival");
		row.AddChild(name); row.AddChild(seed); row.AddChild(gen); row.AddChild(mode);
		// how big the world is, and (Biomes worlds) how big its biomes are
		var row2 = new HBoxContainer(); row2.AddThemeConstantOverride("separation", 8); b.AddChild(row2);
		HSlider NewSlider(string label, double min, double max, double step, double value, Func<double, string> show)
		{
			row2.AddChild(Text(label, 14));
			var s = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(170, 24), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
			var v = Text(show(value), 14); v.CustomMinimumSize = new Vector2(84, 0);
			s.ValueChanged += x => v.Text = show(x);
			row2.AddChild(s); row2.AddChild(v);
			return s;
		}
		var size = NewSlider("World size", World.MinSize, World.MaxSize, World.SizeStep, World.DefaultSize, x => $"{(int)x} x {(int)x}");
		var biomes = NewSlider("Biome size", World.MinBiomes, World.MaxBiomes, 0.25, 1, x => x == 1 ? "Normal" : $"x{x:0.##}");
		biomes.TooltipText = "How big the biomes are (Biomes worlds)";
		gen.ItemSelected += _ => biomes.Editable = gen.GetSelectedId() == 3;
		row.AddChild(Btn("Create & play", () =>
		{
			var w = Worlds.Create(name.Text, seed.Text, gen.GetSelectedId(), mode.Selected == 1 ? "survival" : "creative", (int)size.Value, gen.GetSelectedId() == 3 ? biomes.Value : 1);
			SwitchWorld(w.Id, true);
		}, 140));
		b.AddChild(Btn("Back", () => ShowWorlds(false), 160));
	}
	void ShowWorlds(bool open)
	{
		worldsPanel.Visible = open;
		if (!open) return;
		foreach (var c in worldList.GetChildren()) c.QueueFree();
		foreach (var w in Worlds.List())
		{
			var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 8);
			var info = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			info.AddChild(Text(w.Name + (w.Id == current?.Id ? "  (open now)" : ""), 16));
			string played = DateTimeOffset.FromUnixTimeMilliseconds(w.Played).ToLocalTime().ToString("g");
			info.AddChild(Text($"Last played {played} · Seed {w.Seed} · {(w.Gen == 3 ? "Biomes" : w.Gen == 2 ? "Rolling hills" : "Plateaus")} · {World.ValidSize(w.S, w.H)} wide{(w.Gen == 3 && w.B != 1 ? $" · Biomes x{w.B}" : "")} · {(w.H == 640 ? 640 : 192)} tall · {(w.Survival ? "Survival" : "Creative")}{(w.Ver != null ? " · v" + w.Ver : "")}", 12, 0.7f));
			row.AddChild(info);
			var id = w.Id;
			row.AddChild(Btn("Play", () =>
			{
				if (id == current.Id) { ShowWorlds(false); Play(); return; }   // already open: just go in
				if (CompareVersions(w.Ver, Version) > 0)
					Ask($"\"{w.Name}\" was last played in version {w.Ver}, which is newer than this one ({Version}). It might not load right here, and playing it could mess it up. Open it anyway?", () => SwitchWorld(id, true));
				else SwitchWorld(id, true);
			}, 80));
			var del = Btn("Delete", () => Ask($"Delete \"{w.Name}\"? This can't be undone.", () => { Worlds.Remove(id); ShowWorlds(true); }), 80);
			del.Disabled = w.Id == current?.Id; del.AddThemeColorOverride("font_color", new Color(1, 0.55f, 0.5f));
			row.AddChild(del);
			worldList.AddChild(row);
		}
	}

	void BuildWhatsNew()
	{
		whatsNewPanel = Backdrop(0.5f);
		var b = Centered(whatsNewPanel, true);
		b.AddChild(Text("What's new", 20));
		b.AddChild(Text($"{Title} {Version}", 16));
		foreach (var line in new[] {
			"The whole game rebuilt in Godot, as a download for Windows",
			"Everything from the web version: hills and plateaus, torches, water, pieces, slabs and stairs",
			"Survival: crafting, the Crafting Bench, kilns, tools, health and the gloom",
			"Dan The Chocolate Man and Grr Angry, day and night, clouds, sounds and music",
			"Your worlds save in the game's own folder (the web version keeps its worlds in your browser)" })
		{ var l = Text("•  " + line, 14, 0.9f); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(480, 0); b.AddChild(l); }
		b.AddChild(Btn("OK", () => whatsNewPanel.Visible = false, 160));
	}
	void BuildMulti()
	{
		multiPanel = Backdrop(0.5f);
		var b = Centered(multiPanel, true);
		b.AddChild(Text("Multiplayer", 20));
		BuildMultiplayerPanel(b);
		b.AddChild(Btn("Back", () => multiPanel.Visible = false, 160));
	}

	void UpdateSaveText()
	{
		if (quitBtn == null || current == null) return;
		quitBtn.Text = netClient ? "Leave their game" : settings.Autosave ? "Save & quit to main menu" : "Quit to main menu";
		saveBtn.Visible = discardBtn.Visible = !netClient;   // (in a friend's game, their game does the saving)
		savedNote.Text = netClient ? "Their game saves this world." : tempWorld ? "This is a test world: it doesn't save." : settings.Autosave ? "Your world saves automatically." : "Auto save is off: press Save world to keep your changes.";
		worldNameLabel.Text = "World: " + current.Name;
		newerNote.Text = CompareVersions(current.Ver, Version) > 0 ? $"Careful: this world was last played in version {current.Ver}, which is newer than this one. It might not look right here." : "";
	}

	// ---- moving between the screens ----
	bool AnyPanel => settingsPanel.Visible || worldsPanel.Visible || whatsNewPanel.Visible || multiPanel.Visible || skinPanel.Visible;
	void ShowMainMenu(bool open)
	{
		menuOpen = open; mainMenu.Visible = open;
		uiLayer.Visible = !open;   // (no hotbar behind the main menu)
		if (open) { pauseMenu.Visible = false; Input.MouseMode = Input.MouseModeEnum.Visible; }
	}
	// into the game
	void Play()
	{
		if (current == null) return;   // (no world to go into: one opens from Singleplayer)
		ShowMainMenu(false);
		pauseTitle.Text = "Paused"; resumeBtn.Text = "Back to game";
		if (!ready) { wantPlay = true; loading.Visible = true; return; }   // (still growing: goes in once it's ready)
		pauseMenu.Visible = false;
		Input.MouseMode = LockMode;
	}
	void Resume() { if (dead) return; Play(); }
	// open another world: save this one, then start the game over with the other one
	void SwitchWorld(string id, bool play)
	{
		if (!discarding && settings.Autosave) SaveWorld();
		if (!discarding && !settings.Autosave && unsaved)
		{
			Ask("Auto save is off. Save this world before leaving?\n\nOK saves it. Cancel leaves without saving.", () => { SaveWorld(true); discarding = true; SwitchWorld(id, play); });
			confirm.Canceled -= LeaveAnyway; confirm.Canceled += LeaveAnyway;
			leaveTo = (id, play);
			return;
		}
		openWorldId = id; playOnLoad = play;
		Input.MouseMode = Input.MouseModeEnum.Visible;
		GetTree().ReloadCurrentScene();
	}
	(string id, bool play) leaveTo;
	void LeaveAnyway() { confirm.Canceled -= LeaveAnyway; discarding = true; SwitchWorld(leaveTo.id, leaveTo.play); }

	// called every frame: the pause screen shows whenever you're not playing (and not in a menu); auto save
	void UpdateScreens(float dt)
	{
		bool playing = ready && Input.MouseMode == LockMode;
		bool paused = ready && !menuOpen && !invOpen && !dead && !playing;
		if (paused && !pauseMenu.Visible && settings.Autosave) SaveWorld();   // paused: save (if auto save is on)
		pauseMenu.Visible = paused && !wantPlay;
		if (wantPlay && ready && !menuOpen) { wantPlay = false; pauseTitle.Text = netClient ? "Joining a friend" : "Ready"; resumeBtn.Text = "Play"; pauseMenu.Visible = true; }
		if (ready && (playing || invOpen)) unsaved = true;   // playing = something may have changed since the last save
		if (ready && settings.Autosave && (playing || invOpen) && (saveTimer += dt) > 30) SaveWorld();   // every 30 seconds
	}

	// ---- saving and loading ----
	bool SaveWorld(bool force = false)
	{
		if (!ready || tempWorld || discarding || current == null || netClient) return false;   // (a friend's world: their game saves it)
		saveTimer = 0;
		falling.Settle();
		var s = new Worlds.Save
		{
			Seed = current.Seed, SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			Idxs = world.Edits.Keys.ToArray(), Ids = world.Edits.Values.ToArray(),
			Pieces = world.PieceMap.SelectMany(p => new[] { p.Key }.Concat(p.Value.Select(v => (int)v))).ToArray(),
			Stairs = SaveExtraStairs(),
			Player = new() { X = player.Pos.X, Y = player.Pos.Y, Z = player.Pos.Z, Yaw = player.Yaw, Pitch = player.Pitch, Fly = player.Flying, Health = health, Gloom = gloom, DarkTime = darkTime, LightTime = lightTime },
			Time = sky.TimeOfDay, Hotbar = inv.Id.ToArray(), Counts = inv.Count.ToArray(), Wear = inv.Wear.ToArray(), Selected = inv.Selected, OffSel = inv.OffSel,
			Data = Enumerable.Range(0, Inventory.NS).Where(i => inv.Data[i] != null).ToDictionary(i => i, i => inv.Data[i]),
			Mobs = mobList.Where(m => m.Dead == 0).Select(m => new Worlds.MobSave { Type = m.Type, X = m.X, Y = m.Y, Z = m.Z, Yaw = m.Yaw, Health = m.Health, Layer = m.Layer,
				Baby = m.Baby, Happy = MathF.Round(m.Happy), Room = MathF.Round(Math.Max(0, m.Room) * 100) / 100, MateCool = MathF.Round(m.MateCool), Fed = m.Fed }).ToList(),
			Drops = drops.All.Select(d => new Worlds.DropSave { Id = d.Id, X = d.P.X, Y = d.P.Y, Z = d.P.Z, Age = d.Age, Wear = d.Wear, Data = d.Data, Keep = d.Keep }).ToList(),
			Drying = new(world.Drying), TorchBurn = world.TorchBurn.ToDictionary(k => k.Key, k => MathF.Round(k.Value)),
			Crops = world.Crops.ToDictionary(k => k.Key, k => MathF.Round(k.Value)), Fert = new(world.FertLeft),
			Grazed = world.Grazed.ToDictionary(k => k.Key, k => MathF.Round(k.Value)),
			Kilns = new(kilns.All),
		};
		try { if (!Worlds.WriteSave(current, s)) return false; unsaved = false; return true; }
		catch (Exception e) { GD.PrintErr("Could not save the world: " + e.Message); return false; }
	}
	Worlds.Save pendingSave;
	// on the background thread, right after the terrain grows: read the save and put your blocks back
	void LoadSaveBlocks()
	{
		if (tempWorld) return;
		try
		{
			var s = Worlds.LoadSave(current.Id);
			if (s == null || s.Seed != current.Seed) return;
			for (int k = 0; k + 8 < s.Pieces.Length; k += 9)
			{
				var a = new int[8];
				for (int j = 0; j < 8; j++) { int id = s.Pieces[k + 1 + j]; var d = Blocks.Get(id); a[j] = d != null && !d.Item && id != Blocks.Pieces ? id : 0; }
				if (a.Any(v => v != 0)) world.PieceMap[s.Pieces[k]] = a;
			}
			world.ApplyEdits(s.Idxs, s.Ids);
			LoadExtraStairs(s.Stairs);
			foreach (var (i, left) in s.Drying) if (world.Drying.ContainsKey(i)) world.Drying[i] = Math.Max(0, left);
			foreach (var (i, left) in s.TorchBurn) if (world.TorchBurn.ContainsKey(i)) world.TorchBurn[i] = Math.Max(0, left);
			if (s.Crops != null) foreach (var (i, left) in s.Crops) if (world.Crops.ContainsKey(i)) world.Crops[i] = Math.Max(0, left);
			if (s.Fert != null) foreach (var (i, n) in s.Fert) if (world.FertLeft.ContainsKey(i)) world.FertLeft[i] = Math.Max(1, n);
			if (s.Grazed != null) foreach (var (i, left) in s.Grazed) if (i >= 0 && i < world.Blocks.Length && world.Blocks[i] == Blocks.Dirt) world.Grazed[i] = Math.Max(0, left);
			pendingSave = s;
		}
		catch (Exception e) { GD.PrintErr("Could not load the world (it will still play, but may not save): " + e.Message); }
	}
	// back on the main thread, once the game is set up: you, the time, your hotbar, mobs, items, kilns
	void ApplyPlayerSave()
	{
		var s = pendingSave; pendingSave = null;
		if (s == null)
		{   // a brand-new world: keep your block layout, but items you earned belong to the old world
			return;
		}
		foreach (var (i, f) in s.Kilns) if (Blocks.IsFurnace(world.Blocks[i]) && f != null) kilns.All[i] = f;
		var p = s.Player;
		if (p != null && float.IsFinite(p.X))
		{
			player.Pos = new Vector3(p.X, p.Y, p.Z); player.Yaw = p.Yaw; player.Pitch = p.Pitch; player.Flying = p.Fly && !inv.Survival; player.Vel = Vector3.Zero;
			gloom = Math.Min(MaxHealth, p.Gloom); darkTime = p.DarkTime; lightTime = p.LightTime;
			if (p.Health > 0) health = Math.Min(MaxHealth, p.Health);
			if (gloom >= health) gloom = Math.Max(0, health - 1);   // (never load straight into a gloom death)
		}
		if (float.IsFinite(s.Time)) sky.TimeOfDay = s.Time;
		if (s.Hotbar?.Length is Inventory.Slots or Inventory.NS)   // (worlds saved before Pack & Plate have just the hotbar)
		{
			for (int i = 0; i < s.Hotbar.Length; i++)
			{
				int id = s.Hotbar[i]; if (id != 0 && Blocks.Get(id) == null) id = 0;
				inv.SetSlot(i, id, inv.Counted(id) ? Math.Max(1, s.Counts?.ElementAtOrDefault(i) ?? 1) : 0, s.Wear?.ElementAtOrDefault(i) ?? 0, s.Data?.GetValueOrDefault(i));
			}
			inv.OffSel = Math.Clamp(s.OffSel, 0, inv.OffCount - 1);
			SelectSlot(Math.Clamp(s.Selected, 0, Inventory.Slots - 1));
		}
		foreach (var m in s.Mobs) if (ZombieReady && m.Type == "grrangry") m.Type = "zombie";   // worlds saved before the Zombie replaced Grr Angry
		foreach (var m in s.Mobs)
		{
			if (!MobTypes.ContainsKey(m.Type) || !models.ContainsKey(m.Type)) continue;
			var t = MobTypes[m.Type];
			var mob = SpawnMob(m.Type, m.X, m.Y, m.Z, t.Cow ? (m.Baby > 0 ? 1 : m.Fed >= FatAt ? 2 : 0) : 0, Math.Clamp(m.Layer, 1, Blocks.StrataLayers));
			mob.Yaw = m.Yaw; mob.Health = m.Health > 0 ? m.Health : mob.Health;
			if (t.Cow) { mob.Baby = Math.Max(0, m.Baby); mob.Fed = Math.Clamp(m.Fed, 0, FedMax); mob.Happy = Math.Max(0, m.Happy); mob.Room = Math.Clamp(m.Room, 0, 1); mob.MateCool = Math.Max(0, m.MateCool); }
		}
		foreach (var d in s.Drops) if (Blocks.Get(d.Id) != null) { var dr = drops.Add(d.Id, d.X, d.Y, d.Z); dr.V = Vector3.Zero; dr.Age = d.Age; dr.Wear = d.Wear; dr.Data = d.Data; dr.Keep = d.Keep; }
		DrawHearts();
	}

	public override void _Notification(int what)
	{
		// closing the window saves first (if auto save is on)
		if (what == NotificationWMCloseRequest && settings?.Autosave == true) SaveWorld();
		if (what == NotificationWMCloseRequest || what == NotificationPredelete) peer?.Destroy();
		if (what == NotificationExitTree) PlayerSkin.Changed -= SkinChanged;   // (the scene restarts when you switch worlds)
	}
}
