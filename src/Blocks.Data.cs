using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Cubeland;

// data/blocks.json's rows as blocks: a row's pictures (by name), and the other rows it names (by key: DataKeys' rules worked
// out from the rows themselves, since the rest of the blocks don't exist yet when the rows are read)
public static partial class Blocks
{
	// the tables, in the order they're read (the same as the web version, and the order they come in the file)
	public static readonly string[] RowTables = { "BASE", "KILN", "TORCHES", "WATER", "LYING_LOGS", "SNOW", "TOOLS", "DEEP_STONE", "SOILS", "GRAIN", "COLOURS", "METALS", "DEEP_ORES", "FOUNDRY", "GEAR", "FLOWERS" };
	// every table in the file, in its order there (the row tables, then the snow-logged blocks and the pieces and shapes)
	public static readonly string[] FileTables = RowTables.Concat(new[] { "SNOWY", "FORMS" }).ToArray();
	public static BlockRow[] RowTable(BlocksData d, string name) => (BlockRow[])typeof(BlocksData).GetField(name).GetValue(d) ?? Array.Empty<BlockRow>();
	// every row, table by table
	public static List<BlockRow> AllRows(BlocksData d) => RowTables.SelectMany(t => RowTable(d, t)).ToList();

	// a row's pictures: one name for every side, [side, top, bottom], or [side, top, bottom, front]
	public static string[] RowPictures(BlockRow r)
	{
		var p = r.pictures;
		if (p.ValueKind == JsonValueKind.String) { string n = p.GetString(); return new[] { n, n, n }; }
		if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() is 3 or 4 && p.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String))
			return p.EnumerateArray().Select(e => e.GetString()).ToArray();
		throw new InvalidOperationException($"data/blocks.json: block {r.id} ({r.name}) has pictures {(p.ValueKind == JsonValueKind.Undefined ? "missing" : p.GetRawText())} (one name, or 3 or 4)");
	}
	// a row as a block, all but the other blocks it names (pics: the atlas places of pictures)
	public static BlockDef RowDef(BlockRow r, Func<string[], int[]> pics) => new()
	{
		Name = r.name ?? throw new InvalidOperationException($"data/blocks.json: block {r.id} has no name"), T = pics(RowPictures(r)),
		Icon = r.icon != null ? pics(new[] { r.icon })[0] : -1,
		Transparent = r.transparent, NoCollide = r.noCollide, Liquid = r.liquid, Item = r.item, Lit = r.lit, Shape = r.shape, Box = r.box,
		Front = r.front, Facing = r.facing, Level = r.level, Layers = r.layers, Axis = r.axis, Pick = r.pick, Layer = r.layer, Wall = r.wall, Wi = r.wi, Count = r.count, Burn = r.burn,
		Armor = r.armor ?? -1, Pack = r.pack, Belt = r.belt, BeltTools = r.tools,
		Needs = r.needs, Sound = r.sound, Shovel = r.shovel == true, Leaves = r.leaves == true, Inventory = r.inventory == true, Look = r.look,
		Food = r.food, Tool = r.tool, Material = r.material, Damage = r.damage,
	};
	// the key a row goes by: DataKeys' rules, from the block it makes (a flat block is "#placed" when its drop is the item of the
	// same name). A key two rows share can't be pointed at (-1).
	public static string RowKey(BlockRow r, BlockDef b, HashSet<string> itemSlugs)
	{
		string slug = DataKeys.Snake(r.name ?? "");
		return DataKeys.Key(r.id, b, r.shape == "flat" && !r.item && r.drop == slug && itemSlugs.Contains(slug));
	}
	public static Dictionary<string, int> RowKeys(IEnumerable<(BlockRow r, BlockDef b)> rows)
	{
		var list = rows.ToList(); var keys = new Dictionary<string, int>();
		var items = list.Where(x => x.r.item).Select(x => DataKeys.Snake(x.r.name ?? "")).ToHashSet();
		foreach (var (r, b) in list) { string key = RowKey(r, b, items); keys[key] = keys.ContainsKey(key) ? -1 : r.id; }
		return keys;
	}
	static int RowRef(Dictionary<string, int> keys, string key, BlockRow r) => KeyRef(keys, key, $"block {r.id} ({r.name})");
	// the block a key names among the rows (0 for none); who: what names it, for the message if it isn't one block
	public static int KeyRef(Dictionary<string, int> keys, string key, string who) => key == null ? 0
		: keys.TryGetValue(key, out int id) ? id >= 0 ? id : throw new InvalidOperationException($"data/blocks.json: {who} names \"{key}\", which more than one block is")
		: throw new InvalidOperationException($"data/blocks.json: {who} names \"{key}\", which no block is");
	// the other blocks a row names: drop, places, handDrop [thing, fewest, most], dye
	public static void RowRefs(BlockDef b, BlockRow r, Dictionary<string, int> keys)
	{
		b.Drop = RowRef(keys, r.drop, r); b.Places = RowRef(keys, r.places, r); b.Dye = RowRef(keys, r.dye, r);
		b.HandDrop = r.handDrop == null ? null : r.handDrop.Length == 3 && r.handDrop[0].ValueKind == JsonValueKind.String
			? new[] { RowRef(keys, r.handDrop[0].GetString(), r), r.handDrop[1].GetInt32(), r.handDrop[2].GetInt32() }
			: throw new InvalidOperationException($"data/blocks.json: block {r.id} ({r.name}) has a handDrop that isn't [thing, fewest, most]");
	}
	// every row as a block, the blocks they name filled in (pics: the atlas places of pictures); keys: the rows' keys
	public static List<(BlockRow r, BlockDef b)> RowBlocks(IEnumerable<BlockRow> rows, Func<string[], int[]> pics, out Dictionary<string, int> keys)
	{
		var defs = rows.Select(r => (r, b: RowDef(r, pics))).ToList();
		keys = RowKeys(defs);
		foreach (var (r, b) in defs) RowRefs(b, r, keys);
		return defs;
	}
	// a SNOWY row's block: its inner block (already made), then the row's own fields, then inner and snowed 1 (and not inventory)
	public static BlockDef SnowyBlock(SnowyRow r, int inner, BlockDef innerDef, Dictionary<string, int> keys)
	{
		var d = innerDef.Copy();
		if (r.drop != null) d.Drop = KeyRef(keys, r.drop, $"snow-logged block {r.id}");
		if (r.box != null) d.Box = r.box;
		d.Inner = inner; d.Snowed = 1; d.Inventory = false;   // (a snow-logged block is never in your inventory)
		return d;
	}
	// a FORMS row's item id for one form (piece, slab, ...)
	public static int FormRowId(FormRow r, string form) => typeof(FormRow).GetField(form)?.GetValue(r) is int v ? v
		: throw new InvalidOperationException($"data/blocks.json: FORM_NAMES has \"{form}\", which FORMS rows don't have");
	// a FORMS row's items, in FORM_NAMES' order: (id, item) (of: the block, already made; names: FORM_NAMES)
	public static IEnumerable<(int id, BlockDef d)> FormItems(FormRow r, int of, BlockDef ofDef, Dictionary<string, string> names)
	{
		foreach (var (f, word) in names)
			yield return (FormRowId(r, f), f == "piece"
				? new BlockDef { Name = $"{ofDef.Name} {word}", T = ofDef.T, Item = true, Piece = of }
				: new BlockDef { Name = $"{ofDef.Name} {word}", T = ofDef.T, Item = true, Form = f, Of = of });
	}
}
