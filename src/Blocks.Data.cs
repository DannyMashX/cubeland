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
	public static readonly string[] RowTables = { "BASE", "KILN", "TORCHES", "WATER", "LYING_LOGS", "SNOW", "TOOLS", "DEEP_STONE", "SOILS", "GRAIN", "COLOURS", "METALS", "DEEP_ORES", "FOUNDRY", "GEAR", "FLOWERS", "WOODS" };
	// every table in the file, in its order there (the row tables, then the doors as they stand, the snow-logged blocks and the
	// pieces and shapes)
	public static readonly string[] FileTables = RowTables.Concat(new[] { "DOORS", "SNOWY", "FORMS" }).ToArray();
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
		Food = r.food, Tool = r.tool, Material = r.material, Damage = r.damage, Log = r.log, Grows = r.grows, Sand = r.sand,
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
	// the other blocks a row names: drop, places, handDrop [thing, fewest, most], dye, fruit
	public static void RowRefs(BlockDef b, BlockRow r, Dictionary<string, int> keys)
	{
		b.Drop = RowRef(keys, r.drop, r); b.Places = RowRef(keys, r.places, r); b.Dye = RowRef(keys, r.dye, r); b.Fruit = RowRef(keys, r.fruit, r); b.Sapling = RowRef(keys, r.sapling, r);
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
	// A DOORS row's 32 doors as they stand: (id, block), id = first + facing x 8 + hinge x 4 + open x 2 + half. Each is named like
	// its item and drops it; the planks' picture on its edges, <item's key>_bottom / _top on its big faces (T[3]); its box is
	// the panel (see DoorBox). (of, item: the planks and the item, already made; itemKey: the item's key)
	public static IEnumerable<(int id, BlockDef d)> DoorBlocks(DoorRow r, int of, BlockDef ofDef, int item, BlockDef itemDef, string itemKey, Func<string[], int[]> pics)
	{
		int[] bottom = pics(new[] { itemKey + "_bottom" }), top = pics(new[] { itemKey + "_top" });
		for (int s = 0; s < 32; s++)
		{
			int facing = s / 8, hinge = s / 4 % 2, open = s / 2 % 2, half = s % 2, edge = ofDef.T[0];
			yield return (r.first + s, new BlockDef
			{
				Name = itemDef.Name, T = new[] { edge, edge, edge, (half == 0 ? bottom : top)[0] }, Transparent = true, Shape = "door",
				Facing = facing, Hinge = hinge, Open = open, Half = half, Box = DoorBox(facing, hinge, open == 1), Drop = item,
				Sound = "wood", Needs = ofDef.Needs,
			});
		}
	}
	// the way a door's front faces (the Kiln's numbers: 0 +z, 1 +x, 2 -z, 3 -x), and its right as you look at its front
	public static (int x, int z) DoorFront(int facing) => facing switch { 0 => (0, 1), 1 => (1, 0), 2 => (0, -1), _ => (-1, 0) };
	public static (int x, int z) DoorRight(int facing) { var (fx, fz) = DoorFront(facing); return (fz, -fx); }
	// a door's panel in its block: 4 pixels thick. Closed, across the middle of the block; open, lying against its hinge side
	// (hinge 0: on the left as you look at its front, the -right side; 1: the +right side), the whole depth of the block
	public static float[] DoorBox(int facing, int hinge, bool open)
	{
		var (fx, _) = DoorFront(facing);
		if (!open) return fx == 0 ? new[] { 0, 0, 0.375f, 1, 1, 0.625f } : new[] { 0.375f, 0, 0, 0.625f, 1, 1 };
		var (rx, rz) = DoorRight(facing);
		int sx = hinge == 0 ? -rx : rx, sz = hinge == 0 ? -rz : rz;   // (the side the hinge is on)
		return sx < 0 ? new[] { 0, 0, 0, 0.25f, 1, 1 } : sx > 0 ? new[] { 0.75f, 0, 0, 1, 1, 1 }
			: sz < 0 ? new[] { 0, 0, 0, 1, 1, 0.25f } : new[] { 0, 0, 0.75f, 1, 1, 1 };
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
