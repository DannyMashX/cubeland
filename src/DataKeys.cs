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
	//  states that aren't the usual one after a #, in this order: layer (deep sets, 2-8), wall and count (torches), facing
	//  and lit (anything with a front), level (water), stage (Grain, always), layers (snow, 2-7), axis (lying logs),
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
		if (d.Shape == "torch") { if (d.Wi > 0) states.Add($"wall={d.Wi}"); if (d.Count > 1) states.Add($"count={d.Count}"); }
		if (d.Front != null) { if (d.Facing > 0) states.Add($"facing={d.Facing}"); if (d.Lit) states.Add("lit"); }
		if (d.Liquid && d.Level > 0) states.Add($"level={d.Level}");
		if (Blocks.IsCrop(id)) states.Add($"stage={Array.IndexOf(Blocks.GrainStages, id)}");
		if (d.Shape == "snow" && d.Layers >= 2) states.Add($"layers={d.Layers}");
		if (d.Axis > 0) states.Add($"axis={d.Axis}");
		if (d.Look > 0) states.Add($"look={d.Look}");
		if (placed) states.Add("placed");
		string slug = Snake(d.Name);
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
		return key != null && ids.TryGetValue(key, out int v) ? v : throw new InvalidOperationException($"a data file names \"{key}\", which no block or item has (see DataKeys.cs)");
	}
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
