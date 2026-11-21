using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace Cubeland;

// Your worlds. The same seed always grows the same terrain, so a save only needs the seed plus what you changed.
// The list (worlds.json) and each world's changes (<id>.json) live in the game's user folder:
// on Windows, %APPDATA%\Godot\app_userdata\CubeLand Alpha\worlds\.
public static class Worlds
{
	public class Info
	{
		public string Id, Name, Mode = "creative", Ver; public int Seed, Gen = 2, H = 192, S = 640; public long Created, Played;   // (H: the world's height; worlds from before Alpha 0.1 are 192 tall. S: its width and depth; worlds made before 1280 are 640)
		[JsonIgnore] public bool Survival => Mode == "survival";
	}
	public class PlayerSave { public float X, Y, Z, Yaw, Pitch, DarkTime, LightTime; public bool Fly; public int Health, Gloom; }
	public class MobSave { public string Type; public float X, Y, Z, Yaw; public int Health, Layer; public float Baby, Happy, Room, MateCool; public int Fed; }   // (the last five: cows)
	public class DropSave { public int Id, Wear; public float X, Y, Z, Age; public Inventory.PackData Data; }
	public class Save
	{
		public int Version = 1, Seed; public long SavedAt;
		public int[] Idxs = Array.Empty<int>(), Ids = Array.Empty<int>(), Pieces = Array.Empty<int>();   // pieces: index, 8 block ids, index, ...
		public PlayerSave Player; public float Time;
		public int[] Hotbar, Counts, Wear; public int Selected, OffSel; public Dictionary<int, Inventory.PackData> Data;
		public List<MobSave> Mobs = new(); public List<DropSave> Drops = new();
		public Dictionary<int, float> Drying = new(), TorchBurn = new(), Crops = new();   // (crops: lucky ticks so far in each stage)
		public Dictionary<int, int> Fert = new();   // fertilized soil -> harvests left
		public Dictionary<int, float> Grazed = new();   // grass a cow ate -> seconds until it grows back
		public Dictionary<int, Kilns.Kiln> Kilns = new();
	}
	static readonly JsonSerializerOptions Opts = new() { IncludeFields = true };
	// (testing: --worlds=<folder> keeps test worlds away from your real ones)
	static readonly string Dir = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--worlds="))?[9..] ?? "user://worlds";
	static List<Info> list;

	static void Load()
	{
		if (list != null) return;
		DirAccess.MakeDirRecursiveAbsolute(Dir);
		try { list = JsonSerializer.Deserialize<List<Info>>(FileAccess.GetFileAsString(Dir + "/worlds.json"), Opts); } catch { }
		list ??= new();
	}
	static void SaveList() => Write(Dir + "/worlds.json", JsonSerializer.Serialize(list, Opts));
	// write to a spare file first, so a crash halfway through never leaves a broken save
	static bool Write(string path, string text)
	{
		using (var f = FileAccess.Open(path + ".tmp", FileAccess.ModeFlags.Write)) { if (f == null) return false; f.StoreString(text); }
		return DirAccess.RenameAbsolute(path + ".tmp", path) == Error.Ok;
	}

	public static List<Info> List() { Load(); return list.OrderByDescending(w => w.Played).ToList(); }
	// blank = random; a number is used as-is; any other text is turned into a number (the same way the web version does)
	public static int SeedFrom(string text)
	{
		text = (text ?? "").Trim();
		if (text == "") return (int)(Random.Shared.NextDouble() * 1e9);
		if (long.TryParse(text, out long n) && System.Text.RegularExpressions.Regex.IsMatch(text, @"^-?\d+$")) return unchecked((int)n);
		int h = 0;
		foreach (char c in text) h = unchecked(h * 31 + c);
		return h;
	}
	// gen = terrain style: 2 = rolling hills (default), 1 = plateaus. mode = "creative" or "survival".
	public static Info Create(string name, string seedText, int gen = 3, string mode = "creative")
	{
		Load();
		name = (name ?? "").Trim(); if (name.Length > 32) name = name[..32];
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		var w = new Info { Id = Convert.ToString(now, 16) + Random.Shared.Next(0x1000, 0xffff).ToString("x"), Name = name == "" ? $"World {list.Count + 1}" : name,
			Seed = SeedFrom(seedText), Gen = gen, Mode = mode == "survival" ? "survival" : "creative", Ver = Main.Version, Created = now, Played = now, H = World.TallWorlds ? 640 : 192, S = World.TallWorlds ? 640 : 1280 };
		list.Add(w); SaveList();
		return w;
	}
	public static Info Get(string id) { Load(); return list.FirstOrDefault(w => w.Id == id); }
	// which world to open: the last one played, else a new one
	public static Info Last()
	{
		Load();
		string last = null; try { last = FileAccess.GetFileAsString(Dir + "/last.txt").Trim(); } catch { }
		return list.FirstOrDefault(w => w.Id == last) ?? list.OrderByDescending(w => w.Played).FirstOrDefault() ?? Create("World 1", "");
	}
	public static void MarkOpened(Info w)
	{
		w.Played = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (Main.CompareVersions(Main.Version, w.Ver) > 0) w.Ver = Main.Version;   // (the newest version that has played it)
		SaveList(); Write(Dir + "/last.txt", w.Id);
	}
	public static Save LoadSave(string id)
	{
		string p = $"{Dir}/{id}.json";
		if (!FileAccess.FileExists(p)) return null;
		return JsonSerializer.Deserialize<Save>(FileAccess.GetFileAsString(p), Opts);
	}
	public static bool WriteSave(Info w, Save s)
	{
		if (Get(w.Id) == null) return false;   // (deleted)
		bool ok = Write($"{Dir}/{w.Id}.json", JsonSerializer.Serialize(s, Opts));
		if (ok) MarkOpened(w);
		return ok;
	}
	public static void Remove(string id)
	{
		Load(); list.RemoveAll(w => w.Id == id); SaveList();
		DirAccess.RemoveAbsolute($"{Dir}/{id}.json");
	}
}
