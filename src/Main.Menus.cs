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
		public int Sens = 100, Fov = 75, RenderDist = 10, Gui; public bool Info = true, Autosave = true, Muted; public string Name = "Player";
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
		ApplyGui();
		info.Visible = settings.Info;
		foreach (var (k, v) in settings.Vol) Sound.Levels[k] = v / 100f;
		Sound.Muted = settings.Muted;
		using var f = FileAccess.Open("user://settings.json", FileAccess.ModeFlags.Write);
		f?.StoreString(JsonSerializer.Serialize(settings, SetOpts));
		UpdateSaveText();
	}
	// GUI scale: 0 = automatic (from your display scaling), or 1-4 times. Always a whole number, so every pixel stays even;
	// a bigger one than the window has room for is made as big as fits.
	static int autoGui = 1;
	const int MaxGui = 4;
	static int GuiFits() { var s = DisplayServer.WindowGetSize(); return Math.Max(1, Math.Min(s.X / 600, s.Y / 360)); }
	void ApplyGui()
	{
		settings.Gui = Math.Clamp(settings.Gui, 0, MaxGui);
		if (OS.GetCmdlineUserArgs().Any(a => a.StartsWith("--uiscale="))) return;
		GetTree().Root.ContentScaleFactor = settings.Gui > 0 ? Math.Min(settings.Gui, GuiFits()) : autoGui;
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
			if (HasArg("--earth"))   // (test: an Earthlike world: gen 4, land 9, 640 tall, --size= 1024 or 2048)
				current = new Worlds.Info { Id = "test", Name = "Test world", Seed = sa != null ? Worlds.SeedFrom(sa[7..]) : Random.Shared.Next(), Gen = 4, H = 640, S = ArgInt("--size=", 2048) == 1024 ? 1024 : 2048, B = 1, Land = 9, Mode = HasArg("--survival") ? "survival" : "creative" };
			else
			current = new Worlds.Info { Id = "test", Name = "Test world", Seed = sa != null ? int.Parse(sa[7..]) : Random.Shared.Next(), Gen = HasArg("--biomes") ? 3 : 2, H = HasArg("--tall") || (World.TallWorlds && !HasArg("--short")) ? 640 : 192, S = HasArg("--small") ? 640 : ArgInt("--size=", 1280), B = ArgInt("--biomescale=", 100) / 100.0, Land = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--land=")) is { } la ? World.ValidLand(int.Parse(la[7..])) : HasArg("--oldland") ? 0 : World.NewLand, Mode = HasArg("--survival") ? "survival" : "creative" };
			if (HasArg("--os")) { current.Os = true; current.OsSkip = Worlds.TidyChunks((OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--osskip=")) ?? "--osskip=")[9..].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)); }   // (testing: an older world given the staircases)
			menuOpen = HasArg("--menu");
			return;
		}
		if (joinInfo != null && openWorldId == "join-" + joinInfo.Code)
		{   // a friend's world you're joining: it isn't one of your worlds and isn't saved here
			current = new Worlds.Info { Id = openWorldId, Name = $"{joinInfo.Host}'s world", Seed = joinInfo.Seed, Gen = joinInfo.Gen, H = joinInfo.H, S = joinInfo.S, B = joinInfo.B, Land = joinInfo.Land, Mode = joinInfo.Mode == "survival" ? "survival" : "creative", Ver = Version, Os = joinInfo.Os, OsSkip = joinInfo.OsSkip };
			menuOpen = false; wantPlay = true; playOnLoad = false;
			return;
		}
		joinInfo = null;
		// no world picked: the main menu, with no world at all. A world only opens from Singleplayer (or by joining a friend),
		// and quitting to the main menu closes it. (--play for testing: straight into the last world played.)
		if ((openWorldId == null || Worlds.Get(openWorldId) == null) && !HasArg("--play")) { current = null; menuOpen = true; playOnLoad = false; return; }
		current = (openWorldId != null ? Worlds.Get(openWorldId) : null) ?? Worlds.Last();
		Worlds.MarkOpened(current);
		if (WorldGen.EerieOn) Worlds.OldStairs(current);   // (an older Biomes world: the staircases, decided once)
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

		// pause screen (Esc), also "Ready" when a world has just opened: the title and world name, a wide "Back to game",
		// Settings / Skin / Multiplayer in a row, "Save world" with how saving goes under it, then (apart, greyer) the ways to quit
		pauseMenu = Backdrop(0.45f);
		var pm = Centered(pauseMenu, false);
		pauseTitle = Centre(Text("Paused", 32)); pm.AddChild(pauseTitle);
		worldNameLabel = Centre(Text("", 15, 0.85f)); pm.AddChild(worldNameLabel);
		const int PauseW = 486; int third = (PauseW - 12) / 3;
		Button Small(string text, Action click, int w) { var sb = Btn(text, click, w); sb.AddThemeFontSizeOverride("font_size", 18); sb.CustomMinimumSize = new Vector2(w, 40); sb.ClipText = true; sb.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; return sb; }
		resumeBtn = Btn("Back to game", Resume, PauseW); pm.AddChild(resumeBtn);
		var trio = new HBoxContainer(); trio.AddThemeConstantOverride("separation", 6); pm.AddChild(trio);
		trio.AddChild(Small("Settings", () => ShowSettings(true), third));
		trio.AddChild(Small("Skin", OpenSkinEditor, third));
		pauseMultiBtn = Small("Multiplayer", () => { RefreshNetUi(); multiPanel.Visible = true; }, third); trio.AddChild(pauseMultiBtn);
		saveBtn = Btn("Save world", () => { savedNote.Text = SaveWorld(true) ? "Saved!" : tempWorld ? "Test worlds don't save." : "Could not save the world."; GetTree().CreateTimer(2.5).Timeout += UpdateSaveText; }, PauseW);
		pm.AddChild(saveBtn);
		savedNote = Centre(Text("", 13, 0.8f)); pm.AddChild(savedNote);
		pm.AddChild(new Control { CustomMinimumSize = new Vector2(0, 14) });
		var quits = new VBoxContainer { Modulate = new Color(0.78f, 0.78f, 0.78f) }; quits.AddThemeConstantOverride("separation", 6); pm.AddChild(quits);
		quitBtn = Small("Save & quit to main menu", () => { if (netClient) { LeaveGame(); return; } SwitchWorld(null, false); }, PauseW);   // (closes the world: see PickWorld)
		quits.AddChild(quitBtn);
		discardBtn = Small("Quit without saving", () => Ask("Quit without saving?\n\nEverything you did since the last save will be lost.", () => { discarding = true; SwitchWorld(null, false); }), PauseW);
		quits.AddChild(discardBtn);
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

	// Settings, in four tabs: Video, Sound, Controls (with every key), Game
	int settingsTab; readonly List<(Button tab, Control page)> settingsPages = new();
	void BuildSettings()
	{
		settingsPanel = Backdrop(0.5f);
		var b = Centered(settingsPanel, true);
		b.AddChild(Text("Settings", 20));
		var tabs = new HBoxContainer(); tabs.AddThemeConstantOverride("separation", 6); b.AddChild(tabs);
		GridContainer grid = null; VBoxContainer page = null;
		void Page(string name)
		{
			page = new VBoxContainer { CustomMinimumSize = new Vector2(560, 330) }; page.AddThemeConstantOverride("separation", 6); b.AddChild(page);
			grid = new GridContainer { Columns = 3 }; grid.AddThemeConstantOverride("h_separation", 14); grid.AddThemeConstantOverride("v_separation", 8); page.AddChild(grid);
			var t = new Button { Text = name, ToggleMode = true, CustomMinimumSize = new Vector2(120, 34) };
			int k = settingsPages.Count; t.Pressed += () => ShowSettingsTab(k);
			tabs.AddChild(t); settingsPages.Add((t, page));
		}
		void Slider(string name, int min, int max, int step, Func<int> get, Action<int> set, string unit, Func<int, string> show = null)
		{
			var nl = Text(name, 15); nl.CustomMinimumSize = new Vector2(170, 0); grid.AddChild(nl);
			var s = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = get(), CustomMinimumSize = new Vector2(240, 24), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
			show ??= x => x + unit;
			var v = Text(show(get()), 15); v.CustomMinimumSize = new Vector2(110, 0);
			s.ValueChanged += x => { set((int)x); v.Text = show((int)x); ApplySettings(); };
			grid.AddChild(s); grid.AddChild(v);
		}
		void Toggle(string name, Func<string> label, Action flip)
		{
			var nl = Text(name, 15); nl.CustomMinimumSize = new Vector2(170, 0); grid.AddChild(nl);
			var t = new Button { Text = label(), CustomMinimumSize = new Vector2(240, 32) };
			t.Pressed += () => { flip(); t.Text = label(); ApplySettings(); };
			grid.AddChild(t); grid.AddChild(new Control());
		}
		Page("Video");
		Slider("Field of view", 50, 110, 5, () => settings.Fov, x => settings.Fov = x, "");
		Slider("Render distance", MinRender, MaxRender, 1, () => settings.RenderDist, x => settings.RenderDist = x, " chunks");
		Slider("GUI scale", 0, MaxGui, 1, () => settings.Gui, x => settings.Gui = x, "", x => x == 0 ? $"Auto ({autoGui}x)" : x + "x");
		Toggle("FPS and position", () => settings.Info ? "Shown" : "Hidden", () => settings.Info = !settings.Info);
		Page("Sound");
		Toggle("Sound (M)", () => settings.Muted ? "Off" : "On", () => settings.Muted = !settings.Muted);
		foreach (var (key, name) in new[] { ("master", "Master volume"), ("block", "Blocks"), ("player", "Player"), ("mob", "Mobs"), ("music", "Music") })
			Slider(name, 0, 100, 5, () => settings.Vol[key], x => settings.Vol[key] = x, "%");
		Page("Controls");
		Slider("Mouse sensitivity", 20, 300, 10, () => settings.Sens, x => settings.Sens = x, "%");
		var keys = new GridContainer { Columns = 2 }; keys.AddThemeConstantOverride("h_separation", 16); keys.AddThemeConstantOverride("v_separation", 3); page.AddChild(keys);
		foreach (var (key, what) in new[] {
			("W A S D", "move (Space jump, Shift sprint)"), ("Mouse, arrow keys", "look around"),
			("Left click", "break (hold to keep breaking)"), ("Right click", "place, use, or eat the food you're holding"),
			("Middle click", "pick the block you're looking at"), ("1-9, 0, scroll wheel", "pick a hotbar slot"),
			("E", "inventory"), ("Esc", "pause"),
			("Q", "drop one (survival)"), ("Ctrl+Q, Shift+Q", "drop the whole stack"),
			("R", "next offhand item · over an item in a screen: how to make it"), ("F", "creative: fly (or double-tap Space) · survival: swap offhand and hand"),
			("Shift + right click", "build against a Crafting Bench, Cutter or Kiln"), ("Shift-click", "in your inventory: move a stack across"),
			("Double-click", "gather a stack together"), ("T · M", "skip time ahead · sound on or off") })
		{
			keys.AddChild(Text(key, 13)); keys.AddChild(Text(what, 13, 0.8f));
		}
		Page("Game");
		Toggle("Auto save", () => settings.Autosave ? "On (every 30 seconds)" : "Off", () => settings.Autosave = !settings.Autosave);
		var back = Btn("Back", () => ShowSettings(false), 160); back.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter; b.AddChild(back);
		ShowSettingsTab(0);
	}
	void ShowSettingsTab(int k)
	{
		settingsTab = k;
		for (int j = 0; j < settingsPages.Count; j++) { settingsPages[j].tab.ButtonPressed = j == k; settingsPages[j].page.Visible = j == k; }
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
		if (WorldGen.EarthlikeOn) gen.AddItem("Earthlike", 4);
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
		// (Earthlike: 1024 or 2048 across, no biome size)
		gen.ItemSelected += _ =>
		{
			bool earth = gen.GetSelectedId() == 4;
			biomes.Editable = gen.GetSelectedId() == 3;
			size.MinValue = earth ? 1024 : World.MinSize; size.Step = earth ? 1024 : World.SizeStep; size.Value = earth ? 2048 : World.DefaultSize;
		};
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
			info.AddChild(Text($"Last played {played} · Seed {w.Seed} · {(w.Gen == 4 ? "Earthlike" : w.Gen == 3 ? "Biomes" : w.Gen == 2 ? "Rolling hills" : "Plateaus")} · {(w.Gen == 4 ? w.S : World.ValidSize(w.S, w.H))} wide{(w.Gen == 3 && w.B != 1 ? $" · Biomes x{w.B}" : "")} · {(w.H == 640 ? 640 : 192)} tall · {(w.Survival ? "Survival" : "Creative")}{(w.Ver != null ? " · v" + w.Ver : "")}", 12, 0.7f));
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
		if (open) { pauseMenu.Visible = false; Unlock(); }
	}
	// into the game
	void Play()
	{
		if (current == null) return;   // (no world to go into: one opens from Singleplayer)
		ShowMainMenu(false);
		pauseTitle.Text = "Paused"; resumeBtn.Text = "Back to game";
		if (!ready) { wantPlay = true; loading.Visible = true; return; }   // (still growing: goes in once it's ready)
		pauseMenu.Visible = false;
		Lock();
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
		Unlock();
		GetTree().ReloadCurrentScene();
	}
	(string id, bool play) leaveTo;
	void LeaveAnyway() { confirm.Canceled -= LeaveAnyway; discarding = true; SwitchWorld(leaveTo.id, leaveTo.play); }

	// called every frame: the pause screen shows whenever you're not playing (and not in a menu); auto save
	void UpdateScreens(float dt)
	{
		bool playing = ready && Locked;
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
		bool wide = World.PiecesNeedWide(world.PieceMap.Values);   // (a piece of a block numbered 4096 or more: the pieces go with "pg": 8192)
		var s = new Worlds.Save
		{
			Seed = current.Seed, SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			Idxs = world.Edits.Keys.ToArray(), Ids = world.Edits.Values.ToArray(),
			Pieces = world.PieceMap.SelectMany(p => new[] { p.Key }.Concat(p.Value.Select(v => wide ? v : World.SpotOut(v)))).ToArray(), Pg = wide ? World.GroupMul : 0,
			Stairs = SaveExtraStairs(), Os = current.Os, OsSkip = current.OsSkip,
			Player = new() { X = player.Pos.X, Y = player.Pos.Y, Z = player.Pos.Z, Yaw = player.Yaw, Pitch = player.Pitch, Fly = player.Flying, Health = health, Gloom = gloom, DarkTime = darkTime, LightTime = lightTime },
			Time = sky.TimeOfDay, Hotbar = inv.Id.ToArray(), Counts = inv.Count.ToArray(), Wear = inv.Wear.ToArray(), Selected = inv.Selected, OffSel = inv.OffSel,
			Data = Enumerable.Range(0, Inventory.NS).Where(i => inv.Data[i] != null).ToDictionary(i => i, i => inv.Data[i]),
			Mobs = mobList.Where(m => m.Dead == 0).Select(m => new Worlds.MobSave { Type = m.Type, X = m.X, Y = m.Y, Z = m.Z, Yaw = m.Yaw, Health = m.Health, Layer = m.Layer,
				Baby = m.Baby, Happy = MathF.Round(m.Happy), Room = MathF.Round(Math.Max(0, m.Room) * 100) / 100, MateCool = MathF.Round(m.MateCool), Fed = m.Fed,
				EggIn = MathF.Round(m.EggIn), Milky = MathF.Round(m.Milky), HungerIn = m.Type == "bear" ? MathF.Round(Math.Max(0, m.HungerIn)) : null, Sow = m.Sow > 0 ? DataKeys.KeyOf(m.Sow) : null }).ToList(),
			Drops = drops.All.Select(d => new Worlds.DropSave { Id = d.Id, X = d.P.X, Y = d.P.Y, Z = d.P.Z, Age = d.Age, Wear = d.Wear, Data = d.Data, Keep = d.Keep }).ToList(),
			Drying = new(world.Drying), TorchBurn = world.TorchBurn.ToDictionary(k => k.Key, k => MathF.Round(k.Value)),
			Crops = world.Crops.ToDictionary(k => k.Key, k => MathF.Round(k.Value)), Fert = new(world.FertLeft),
			Grazed = world.Grazed.ToDictionary(k => k.Key, k => MathF.Round(k.Value)),
			Saplings = new(world.Saplings), Grown = world.Grown.ToArray(),
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
			int pg = s.Pg > 0 ? s.Pg : World.OldGroupMul;
			for (int k = 0; k + 8 < s.Pieces.Length; k += 9)
			{
				var a = new int[8];
				for (int j = 0; j < 8; j++) { long v = World.SpotIn(s.Pieces[k + 1 + j], pg); int id = v < 0 || v > int.MaxValue ? 0 : (int)v, b = World.SpotBlock(id); var d = Blocks.Get(b); a[j] = d != null && !d.Item && !Blocks.IsPieces(b) && id / World.GroupMul <= 15 ? id : 0; }   // (block + group)
				if (a.Any(v => v != 0)) world.PieceMap[s.Pieces[k]] = a;
			}
			world.ApplyEdits(s.Idxs, s.Ids);
			LoadExtraStairs(s.Stairs);
			foreach (var (i, left) in s.Drying) if (world.Drying.ContainsKey(i)) world.Drying[i] = Math.Max(0, left);
			foreach (var (i, left) in s.TorchBurn) if (world.TorchBurn.ContainsKey(i)) world.TorchBurn[i] = Math.Max(0, left);
			if (s.Crops != null) foreach (var (i, left) in s.Crops) if (world.Crops.ContainsKey(i)) world.Crops[i] = Math.Max(0, left);
			if (s.Fert != null) foreach (var (i, n) in s.Fert) if (world.FertLeft.ContainsKey(i)) world.FertLeft[i] = Math.Max(1, n);
			if (s.Grazed != null) foreach (var (i, left) in s.Grazed) if (i >= 0 && i < world.Blocks.Length && World.Regrows(world.Blocks[i])) world.Grazed[i] = Math.Max(0, left);
			if (s.Saplings != null) foreach (var (i, n) in s.Saplings) if (world.Saplings.ContainsKey(i)) world.Saplings[i] = Math.Clamp(n, 0, World.SaplingHits - 1);
			if (s.Grown != null) foreach (long i in s.Grown) if (world.Edits.ContainsKey(i)) world.Grown.Add(i);
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
			if (t.Animal)
			{   // another animal, as it was kept
				if (t.A == null) continue;
				var a = SpawnAnimal(m.Type, m.X, m.Y, m.Z, Math.Max(0, m.Baby));
				a.Yaw = m.Yaw; a.Health = m.Health > 0 ? m.Health : a.Health; a.Fed = Math.Clamp(m.Fed, 0, t.A.fedMax); a.Happy = Math.Max(0, m.Happy); a.MateCool = Math.Max(0, m.MateCool);
				a.EggIn = Math.Max(0, m.EggIn); a.Milky = Math.Max(0, m.Milky); if (m.HungerIn is { } hi) a.HungerIn = Math.Max(0, hi);
				a.Sow = m.Sow != null && DataKeys.TryIdOf(m.Sow, out int sw) ? sw : 0;
				continue;
			}
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
		// the window lost the focus (Alt+Tab, another window clicked): the pointer is let go at once, so it's yours again;
		// you're still playing, and a click back in the game takes it again (see HoldMouse)
		if (what == NotificationWMWindowFocusOut || what == NotificationApplicationFocusOut) ReleaseMouse();
	}
}
