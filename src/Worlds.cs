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
		public string Id, Name, Mode = "creative", Ver; public int Seed, Gen = 2, H = 192, S = 640, Land; public double B = 1; public long Created, Played;   // (H: the world's height; worlds from before Alpha 0.1 are 192 tall. S: its width and depth; worlds made before 1280 are 640. B: how big its biomes are, 1 = the usual size. Land: see World.LandVersion)
		// Os: a Biomes world from before the staircases (land 0 to 4) that grows them too; OsSkip: the chunks near which none grow
		// (see OldStairs). Decided once, the first time it's opened in a version with staircases. The same as the web version.
		public bool Os; public int[] OsSkip;
		[JsonIgnore] public bool Survival => Mode == "survival";
	}
	public class PlayerSave { public float X, Y, Z, Yaw, Pitch, DarkTime, LightTime; public bool Fly; public int Health, Gloom; }
	public class MobSave { public string Type; public float X, Y, Z, Yaw; public int Health, Layer; public float Baby, Happy, Room, MateCool; public int Fed;   // (the last five: cows and the other animals)
		public float EggIn, Milky; public float? HungerIn; public string Sow; }   // (the other animals: an egg on its way, a goat's milk, a bear's hunger, a deer's sapling)
	public class DropSave { public int Id, Wear; public float X, Y, Z, Age; public Inventory.PackData Data; public bool Keep; }
	public class Save
	{
		public int Version = 1, Seed; public long SavedAt;
		public long[] Idxs = Array.Empty<long>(); public int[] Ids = Array.Empty<int>();
		public long[] Pieces = Array.Empty<long>();   // pieces: index, 8 block ids, index, ... (64-bit, as block numbers are; old saves' numbers read the same)
		public PlayerSave Player; public float Time;
		public int[] Hotbar, Counts, Wear; public int Selected, OffSel; public Dictionary<int, Inventory.PackData> Data;
		public List<MobSave> Mobs = new(); public List<DropSave> Drops = new();
		public int[] Stairs = Array.Empty<int>();   // staircases that moved somewhere the world had no spot for (x, y, z, way, steps, block, ...)
		public bool Os; public int[] OsSkip;        // (kept here too, as in the world's list: see Info and OldStairs)
		public Dictionary<long, float> Drying = new(), TorchBurn = new(), Crops = new();   // (crops: lucky ticks so far in each stage)
		public Dictionary<long, int> Fert = new();   // fertilized soil -> harvests left
		public Dictionary<long, float> Grazed = new();   // grass a cow ate -> seconds until it grows back
		public Dictionary<long, float> Saplings = new();   // saplings: lucky ticks so far
		public long[] Grown = Array.Empty<long>();          // the blocks saplings grew (natural, not put there)
		public Dictionary<long, Kilns.Kiln> Kilns = new();
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
	public static Info Create(string name, string seedText, int gen = 3, string mode = "creative", int size = World.DefaultSize, double biomes = 1)
	{
		Load();
		name = (name ?? "").Trim(); if (name.Length > 32) name = name[..32];
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		var w = new Info { Id = Convert.ToString(now, 16) + Random.Shared.Next(0x1000, 0xffff).ToString("x"), Name = name == "" ? $"World {list.Count + 1}" : name,
			Seed = SeedFrom(seedText), Gen = gen, Mode = mode == "survival" ? "survival" : "creative", Ver = Main.Version, Created = now, Played = now, H = World.TallWorlds ? 640 : 192, S = World.ValidSize(World.TallWorlds ? 640 : size, World.TallWorlds ? 640 : 192), B = World.ValidBiomes(biomes), Land = World.NewLand };
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
	// (testing: a save's text read and written back, as the game would write it: --resave checks a change to the save's types
	//  writes old saves the same)
	public static string Reserialize(string text) => JsonSerializer.Serialize(JsonSerializer.Deserialize<Save>(text, Opts), Opts);
	public static bool WriteSave(Info w, Save s)
	{
		if (Get(w.Id) == null) return false;   // (deleted)
		bool ok = Write($"{Dir}/{w.Id}.json", JsonSerializer.Serialize(s, Opts));
		if (ok) MarkOpened(w);
		return ok;
	}
	// An older Biomes world (land 0 to 4) grows the staircases too, as new worlds do, but none within 2 chunks of a chunk that held
	// any change (block) when it was first opened in a version with them. That's decided once, here, and kept in the list and the
	// save, so building near a staircase later never takes it away. (If the list lost it but the save has it, the save's is used.)
	// Chunks are 16 x 16 columns, all heights: z / 16 * (width / 16) + x / 16. The same as the web version.
	public static void OldStairs(Info w)
	{
		if (w == null || w.Gen != 3 || w.Land >= 5) return;
		if (w.Os) { w.OsSkip = TidyChunks(w.OsSkip); return; }
		Save s = null;
		try { s = LoadSave(w.Id); } catch { return; }   // (a save that can't be read: decide another time, never without its changes)
		if (s != null && s.Seed != w.Seed) s = null;
		if (s != null && s.Os) w.OsSkip = TidyChunks(s.OsSkip);
		else
		{
			int size = World.ValidSize(w.S, w.H == 640 ? 640 : 192), n = size / World.CS;
			var skip = new SortedSet<int>();
			foreach (long i in s?.Idxs ?? Array.Empty<long>()) if (i >= 0) skip.Add((int)(i / size % size / World.CS * n + i % size / World.CS));
			w.OsSkip = skip.ToArray();
		}
		w.Os = true;
		Load(); SaveList();
	}
	public static int[] TidyChunks(IEnumerable<int> a) => (a ?? Array.Empty<int>()).Where(c => c >= 0).Distinct().OrderBy(c => c).ToArray();
	public static void Remove(string id)
	{
		Load(); list.RemoveAll(w => w.Id == id); SaveList();
		DirAccess.RemoveAbsolute($"{Dir}/{id}.json");
	}
}
