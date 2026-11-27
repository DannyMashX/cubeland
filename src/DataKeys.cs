using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cubeland;

// Keys: the names the shared data files use for blocks and items ("oak_planks", "slab:oak_planks", "dye:red"), worked out
// from today's ids by the same rules as the web version, so a file can say "stick" and both editions get the same id.
// (Step 0 of the data files: the two editions' --keydump lists must match before any file uses keys.)
public static class DataKeys
{
	// The rules (the same, in the same order, as the web version's blockKey):
	//  0 is "air"; snow-logged: "snowy:" + its inner block's key; a shape item: form + ":" + its block's key ("slab:grass");
	//  a piece item: "piece:" + its block's key; anything else: its name in lower case with _ between words, then any
	//  states that aren't the usual one after a #, in this order: layer (deep sets, 2-8), in (an ore in a rock: Earthlike's),
	//  wall and count (torches), facing and lit (anything with a front), facing, half, hinge and open (a door as it stands,
	//  always all four), level (water), flow and pace (river water: always both), stage (Grain and the other crops, always; then
	//  top for Corn's upper block), top (any other two-tall plant's upper block), layers (snow, 2-7), axis (lying logs),
	//  look (Red Bricks' other looks), placed (a flat block whose drop is an item of the same name).
	public static string KeyOf(int id)
	{
		if (id == 0) return "air";
		var d = Blocks.Get(id);
		if (d == null) return "";
		if (d.Snowed > 0 && d.Inner > 0) return "snowy:" + KeyOf(d.Inner);
		if (d.Form != null && d.Of > 0) return d.Form + ":" + KeyOf(d.Of);
		if (d.Piece > 0) return "piece:" + KeyOf(d.Piece);
		return Key(id, d, d.Shape == "flat" && Blocks.Get(d.Drop) is { Item: true } dropped && dropped.Name == d.Name && d.Drop != id);
	}
	// a plain block's key (not snow-logged, a shape or a piece), from its own fields; placed: a flat block whose drop is the
	// item of the same name (worked out by the caller: data/blocks.json's rows do it before the blocks they name exist)
	public static string Key(int id, BlockDef d, bool placed)
	{
		var states = new List<string>();
		if (d.Layer >= 2) states.Add($"layer={d.Layer}");
		if (d.In != null) states.Add($"in={d.In}");
		if (d.Shape == "torch") { if (d.Wi > 0) states.Add($"wall={d.Wi}"); if (d.Count > 1) states.Add($"count={d.Count}"); }
		if (d.Front != null) { if (d.Facing > 0) states.Add($"facing={d.Facing}"); if (d.Lit) states.Add("lit"); }
		if (d.Shape == "door") states.Add($"facing={d.Facing},half={d.Half},hinge={d.Hinge},open={d.Open}");   // (a door as it stands: always all four)
		if (d.Liquid && d.Level > 0) states.Add($"level={d.Level}");
		if (d.Flow >= 0) states.Add($"flow={d.Flow},pace={d.Pace}");
		if (Blocks.IsCrop(id)) states.Add($"stage={Array.IndexOf(Blocks.GrainStages, id)}");
		else if (d.Crop != null) { states.Add($"stage={d.Stage}"); if (d.Top) states.Add("top"); }   // (the other crops: their stage, always; Corn's upper block: top)
		else if (d.Top) states.Add("top");   // (a two-tall plant's upper block)
		if (d.Shape == "snow" && d.Layers >= 2) states.Add($"layers={d.Layers}");
		if (d.Axis > 0) states.Add($"axis={d.Axis}");
		if (d.Look > 0) states.Add($"look={d.Look}");
		if (placed) states.Add("placed");
		// (an ore in a rock is named for its rock, "Copper Ore in Granite", but keyed as "copper_ore#in=granite": the name's
		// " in <its rock>" ending is left out, as its in says it; Beta 0.1.5.3. The same in the web version)
		string name = d.In != null && d.Name.EndsWith(" in " + d.In, StringComparison.OrdinalIgnoreCase) ? d.Name[..^(" in " + d.In).Length] : d.Name;
		string slug = Snake(name);
		return states.Count == 0 ? slug : slug + "#" + string.Join(",", states);
	}
	static Dictionary<string, int> ids;
	// the id a data file's key stands for; a key nothing has stops the game, naming it (so a typo can't slip by quietly)
	public static int IdOf(string key)
	{
		if (ids == null)
		{
			var m = new Dictionary<string, int> { ["air"] = 0 };
			for (int id = 1; id < Blocks.MaxId; id++) if (Blocks.Get(id) != null) m.TryAdd(KeyOf(id), id);
			ids = m;
		}
		return key != null && (ids.TryGetValue(key, out int v) || ids.TryGetValue(NewKey(key), out v)) ? v : throw new InvalidOperationException($"a data file names \"{key}\", which no block or item has (see DataKeys.cs)");
	}
	// Old keys (the Andesite rename, Beta 0.1.5.2: same ids, new names) still find their blocks: a key nothing has is tried again
	// with each part between ':' having its name (before any '#') swapped by this list, exact names only. (The same in the web
	// version.)
	public static readonly Dictionary<string, string> OldNames = new()
	{
		["stone"] = "andesite", ["cobblestone"] = "cobbled_andesite", ["stone_bricks"] = "andesite_bricks", ["chiseled_stone"] = "chiseled_andesite",
	};
	public static string NewKey(string key)
	{
		var parts = key.Split(':');
		for (int k = 0; k < parts.Length; k++)
		{
			int h = parts[k].IndexOf('#'); string name = h < 0 ? parts[k] : parts[k][..h];
			if (OldNames.TryGetValue(name, out var now)) parts[k] = now + (h < 0 ? "" : parts[k][h..]);
		}
		return string.Join(":", parts);
	}
	// the same, for a key from a save (an unknown one is just not found)
	public static bool TryIdOf(string key, out int id) { try { id = IdOf(key); return true; } catch (InvalidOperationException) { id = 0; return false; } }
	// lower case; each run of anything that isn't a-z or 0-9 becomes one _; no _ at either end
	public static string Snake(string name)
	{
		var sb = new StringBuilder();
		foreach (char c in name.ToLowerInvariant())
		{
			if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
			else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
		}
		return sb.ToString().TrimEnd('_');
	}
	// every id has a key, and no two share one
	public static List<string> Check()
	{
		var found = new List<string>();
		var seen = new Dictionary<string, int>();
		for (int id = 1; id < Blocks.MaxId; id++)
		{
			if (Blocks.Get(id) == null) continue;
			string k = KeyOf(id);
			if (k == "") found.Add($"{id}: no key");
			else if (seen.TryGetValue(k, out int other)) found.Add($"{id}: key \"{k}\" is {other}'s too");
			else seen[k] = id;
		}
		return found;
	}
}
