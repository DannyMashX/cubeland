using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace Cubeland;

// The shared game data: data/*.json, the same files the web version reads (packed into data.js there). Each file is read
// once, when first asked for. While a table is being moved into data/, --datacheck builds it from the file and compares
// it with the one today's code builds (see Main.Data.cs); the old code only goes once that shows 0 differences.
public static class GameData
{
	static readonly Dictionary<string, JsonElement?> files = new();
	public static readonly JsonSerializerOptions Opts = new() { IncludeFields = true, PropertyNameCaseInsensitive = false, ReadCommentHandling = JsonCommentHandling.Disallow };

	// data/NAME.json, parsed (null if there isn't one)
	public static JsonElement? File(string name)
	{
		if (files.TryGetValue(name, out var e)) return e;
		string path = $"res://data/{name}.json";
		JsonElement? root = null;
		if (FileAccess.FileExists(path))
		{
			try { using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(path)); root = doc.RootElement.Clone(); }
			catch (Exception x) { GD.PushError($"data/{name}.json can't be read: {x.Message}"); }
		}
		return files[name] = root;
	}
	// data/NAME.json as a typed table (fields named as in the file; "_note" fields are only notes)
	public static T Read<T>(string name) where T : class => File(name) is { } r ? r.Deserialize<T>(Opts) : null;
}

// data/numbers.json: plain gameplay numbers (names as the web version's constants)
public sealed class NumbersData
{
	public int STACK_MAX, LOG_STAGES, STONE_STAGES, HEARTS;
	public float SMELT_TIME;
	public float[][] BURN_TIMES;
	public FarmNumbers FARM;
	public Dictionary<string, CropNumbers> CROPS;   // the crops besides Grain, by their blocks' crop
	public SaplingNumbers SAPLING;
	public Dictionary<string, int> DURABILITY, ARMOR_DURABILITY, PICK_TIER, STRATA_PICKAXE, PICK_STAGES, AXE_STAGES;
	public float[] ARMOR_SHARE;
}
// saplings: the chance leaves drop theirs; growTime seconds a sapling takes on average, in growHits lucky ticks; the chance
// one Bone Ash grows it at once
public sealed class SaplingNumbers
{
	public double dropChance, boneAshChance;
	public float growTime;
	public int growHits;
}
// a crop's own numbers (with FARM's): seconds a stage takes on wet soil; dryRate (if given) in place of FARM's; what a ripe one
// gives, [[thing, fewest, most], ...]; a Cantaloupe Vine's seconds between melons
public sealed class CropNumbers
{
	public float stageTime, melonTime;
	public float? dryRate;
	public JsonElement[][] harvest;
}
public sealed class FarmNumbers
{
	public float stageTime, dryRate, fertRate, revertTime;
	public int stageHits, wetReach, fertHarvests;
	public double seedChance;
}
// data/smelting.json: what burns in a Kiln or Foundry, and every smelt (things named by their keys; see DataKeys.cs)
public sealed class SmeltingData
{
	public Dictionary<string, int> FUEL;
	public SmeltEntry[] SMELTS;
}
public sealed class SmeltEntry
{
	public string @out, from, at;      // at: "foundry", or left out for the Kiln
	public JsonElement[][] needs;      // [[key, how many], ...] in place of from
	public bool tall;                  // only while tall worlds are on
}
// data/recipes.json: crafting by hand and at the Crafting Bench, and the Cutter's shapes
public sealed class RecipesData
{
	public RecipeEntry[] RECIPES;
	public CutterEntry[] CUTTER;
}
public sealed class RecipeEntry
{
	public string @out, at, tool;      // at: "hand" or "bench"; tool: also needs that tool in the hotbar
	public int n;
	public JsonElement[][] needs;      // [[key, how many], ...], all used up
	public bool axe, colour, tall;     // axe: also needs an axe in the hotbar; colour: listed only while carrying an ingredient; tall: only while tall worlds are on
}
public sealed class CutterEntry
{
	public string mode, label;         // mode: "pieces", or the shape's form name (as in keys: slab:stone)
	public int uses, makes;
}
// data/creative.json: the creative inventory's tabs, in button order (Sort uses the same order)
public sealed class CreativeData
{
	public CreativeTab[] TABS;
}
public sealed class CreativeTab
{
	public string name;
	public JsonElement[] items;        // keys in order, and { "tall": [keys] } groups shown only while tall worlds are on
	public string[] forms;             // (Shapes) every block the Cutter can cut, block by block, in these forms
}
// data/mobs.json: every mob (in spawning order), how much stronger they are deeper down, the Gloomling's fade, and the cows
public sealed class MobsData
{
	public MobEntry[] MOBS;
	public float LAYER_BOOST;
	public GloomFadeNumbers GLOOM_FADE;
	public CowNumbers COW;
	public Dictionary<string, AnimalNumbers> ANIMALS;   // the six animals besides the cow (Farming & animals): see Main.Animals.cs
}
public sealed class MobEntry
{
	public string type, name, growl;
	public int health, max, damage;
	public float speed, sight, reach, hearing, lightRange, near, far, faint, scale = 1;
	public bool hostile, dark, night, deep, burns, shamble, glow, curious, thrower, snuffer, cow, animal;
	public JsonElement[][] drops;      // [[key, fewest, most, chance]] (chance left out: always)
}
public sealed class GloomFadeNumbers { public float range, time; }
// an animal's numbers (data/mobs.json ANIMALS: what each means is in its notes). The cow's where they mean the same; then its
// spawning; then its own (chicken: layTime..peckChance; duck: layTime, eggReach..waterRange; deer: logNear..nibbleRange; goat:
// minHeight, milkTime; boar: logs..angerTime; bear: cubs..angerTime)
public sealed class AnimalNumbers
{
	public int perChunk, crowdedAt, fedMax, babyHealth, nearMax;
	public float nearRange, happyMax, mateCooldown, mateCost, babyTime, babyFoodBoost, foodHappy, spawnWeight;
	public string[] food, biomes, ground, logs, prey;
	public ForageNumbers forage;
	public int[] group, cubs;
	public float cubChance;
	public float[] layTime, scratchTime, peckTime;
	public float fertChance, peckChance, duckRate, followRange, shyRange, sowEvery, sowChance, nibbleEvery, nibbleChance, nibbleRange, milkTime;
	public int eggReach, duckReach, waterRange, sowClear, rootNear;
	public int? logNear, minHeight;
	public float truffleChance, angerTime, preyFlee, huntCool, huntRange, huntHeight, chaseTime, retry, mealHappy, cubRange, cubNear;
}
public sealed class ForageNumbers { public float time, below, happy; public float[] every; }
public sealed class CowNumbers
{
	public int perChunk, crowdedAt, fatAt, fedMax, calfHealth;
	public float happyMax, grazeHappy, grainHappy, grazeBelow, grazeTime, mateCooldown, mateCost, calfTime, calfGrainBoost, nearRange, regrowMin, regrowMax;
}
// data/texture-names.json: other spellings of pictures' files, pictures that borrow another's file until they have their own,
// and the names of font letters a file name can't hold (file name -> the character)
public sealed class TextureNamesData
{
	public Dictionary<string, string> TEXTURE_SPELLINGS, TEXTURE_BORROWS, FONT_NAMES;
}
// data/blocks.json: the blocks and items. Ids are written out (they're the save format); other blocks are named by key.
public sealed class BlocksData
{
	public BlockRow[] BASE;            // the ones written out one by one
	public BlockRow[] KILN, TORCHES, WATER, LYING_LOGS, SNOW, TOOLS, DEEP_STONE, SOILS, GRAIN, COLOURS, METALS, DEEP_ORES, FOUNDRY, GEAR, FLOWERS, WOODS;   // families, every member written out (in the order the game loads them)
	public BlockRow[] CROPS, FENCES;   // the crops besides Grain, and the fences (found by their fields, not by number)
	public DoorRow[] DOORS;            // the doors as they stand: each door's 32 states, made from one row
	public Dictionary<string, string> FORM_NAMES;   // the forms (piece, slab, ...) in order, each with the word its items' names end in
	public SnowyRow[] SNOWY;           // the snow-logged blocks: a torch, flower or other small thing with snow lying round it
	public FormRow[] FORMS;            // each block you can cut (in Blocks.Splittable's order), with its piece and shape items' ids
}
public sealed class BlockRow
{
	public int id;
	public string name, shape, drop, places, tool, material, dye;   // drop, places, dye: other blocks, by key
	public JsonElement pictures;       // one name (every side), or [side, top, bottom] / [side, top, bottom, front]
	public string icon;                // its hotbar picture, if not the first one
	public bool transparent, noCollide, item, liquid, lit;
	public float[] box;
	public JsonElement[] handDrop;     // [key, fewest, most]
	public int food, damage, level, layers, facing, axis, pick, layer, wi, count, burn, pack, belt, tools;
	public int? armor;                 // gear: which piece of armor (0 helmet .. 3 boots; left out: not armor)
	public int[] front, wall;          // [x, z] (wall: a torch leaning off a wall, the way to it; null standing)
	public int stage;                  // Grain: its stage (desktop finds it by its place in Blocks.GrainStages; the check compares); CROPS: its stage (0-3)
	public string crop;                // CROPS: which crop it is (numbers.json CROPS)
	public bool top;                   // CROPS: Corn's upper block
	public string of;                  // FENCES: the planks it's made of, by key
	public string plants, returns;     // items: the crop a seed puts down; what's left in your hand after eating it (by key)
	public bool bag; public string full;   // a bag (holds a mix of things, up to one stack's worth); its picture while it holds anything
	public bool soil, wet, fert;       // tilled soils (desktop: Blocks.Tilled + wet + 2 * fert; the check compares)
	public string needs, sound;        // needs: the tool it can't be broken without ("axe", "pickaxe"); sound: its dig, step and place sound (left out: "stone")
	public bool? shovel, leaves, inventory;   // (true, or left out) a shovel's block; leaves; you can have it in your inventory
	public int look;                   // another look of the block it drops (Red Bricks' 13-15: 1-3)
	public bool log;                   // a tree's log, standing (lying logs count through their drop)
	public string fruit;               // leaves: what they drop now and then (1 in 4, broken without a hoe), by key
	public string sapling;             // leaves: their sapling (dropped now and then, broken without a hoe: numbers.json SAPLING), by key
	public string grows;               // a sapling: the tree it grows into (oak, fir, palm, willow, or a worldgen.json TREE_SHAPES tree)
	public bool sand;                  // a sapling that can be planted on Sand too
}
// a SNOWY row: a snow-logged block. It's its inner block (by key) with that block's fields, then this row's own (only drop and
// box so far), with inner and snowed 1
public sealed class SnowyRow
{
	public int id;
	public string inner, drop;
	public float[] box;
}
// a FORMS row: a block you can cut (by key), and the ids of its piece and shape items (each named after the block, then
// FORM_NAMES' word; the block's pictures)
// a DOORS row: a door as it stands, made of planks `of` (its edges look like them; by key), carried as `item`, with its 32
// states from `first`: first + facing x 8 + hinge x 4 + open x 2 + half (see Blocks.DoorBlocks)
public sealed class DoorRow
{
	public string of;
	public int item, first;
}
public sealed class FormRow
{
	public string of;
	public int piece, slab, vslab, stairs, quarter, vstairs, vquarter;
}
// data/worldgen.json: how the land is grown (blocks by key). Changing it changes worlds already made (their untouched ground
// is grown again from the seed every time they load), so a change needs a new land version.
public sealed class WorldgenData
{
	public OreVeinRow[] ORE_VEINS;     // in the order they're grown (a row's place is in its veins' seeds)
	public string[] BADLANDS_BANDS;    // the badlands' stripes, from the bottom up (they repeat)
	// Trees and Leaves (Biomes worlds from land 3): the same kind of tree in patches this many blocks square; each biome's
	// trees at its usual tree spots ([tree, share], in order); the trees with spots of their own (in order: a row's place is
	// in its seeds); how each new tree looks (a row's place is in its seeds too)
	public int TREE_PATCH;
	public Dictionary<string, JsonElement[][]> TREE_MIX;
	public TreeExtraRow[] TREE_EXTRA;
	public TreeShapeRow[] TREE_SHAPES;
	// Farming & animals (Biomes worlds from land 5): crops growing wild, in patches, and Dead Bushes (see the file's notes)
	public WildCropRow[] WILD_CROPS;
	public DeadBushNumbers DEAD_BUSHES;
}
public sealed class WildCropRow
{
	public string crop;
	public int seed, within, spread;
	public string[] biomes, near, ground;
	public double chance;
	public int[] tries;
}
public sealed class DeadBushNumbers
{
	public int seed;
	public string[] biomes, ground;
	public double chance;
}
// a TREE_EXTRA row: tree grows in biome on a chance per column of its ground; grove [size, above]: only where a slow noise
// that size is above that; spacing: one spot in each square this many across; edge: within that many of another biome;
// coast: low down, with sea within that many (with both: either)
public sealed class TreeExtraRow
{
	public string tree, biome;
	public double chance;
	public double[] grove;
	public int spacing, edge, coast;
}
// a TREE_SHAPES row: a tree's log and leaves (keys; leaves left out: none), trunk [shortest, tallest]; crown: a number, or
// [across, up and down]; tiers and gap (cedar), sparse, branch [shortest, longest], branches [fewest, most]
public sealed class TreeShapeRow
{
	public string tree, log, leaves;
	public int[] trunk, branch, branches;
	public JsonElement crown;
	public double[] tiers;
	public int gap;
	public double sparse;
}
// an ORE_VEINS row: veins per 10,000 columns, blocks per vein, shallowest and deepest (blocks below the surface); takesDirt: it
// turns Dirt into its ore too; allDepths: from land 2, 3 below the surface all the way down (more in a taller world), and
// each deep layer's stone becomes that layer's ore (true, or left out)
public sealed class OreVeinRow
{
	public string ore;
	public int per, size, top, deep;
	public bool? takesDirt, allDepths;
}
public static partial class Nums
{
	static NumbersData n;
	// data/numbers.json (the game can't run without it)
	public static NumbersData N => n ??= GameData.Read<NumbersData>("numbers") ?? throw new InvalidOperationException("data/numbers.json is missing or can't be read");
	static MobsData mobs;
	// data/mobs.json (read when first asked for, so it doesn't matter which file's numbers ask first)
	public static MobsData Mobs => mobs ??= GameData.Read<MobsData>("mobs") ?? throw new InvalidOperationException("data/mobs.json is missing or can't be read");
	static BlocksData blocks;
	// data/blocks.json
	public static BlocksData Blocks => blocks ??= GameData.Read<BlocksData>("blocks") ?? throw new InvalidOperationException("data/blocks.json is missing or can't be read");
	static TextureNamesData textureNames;
	// data/texture-names.json
	public static TextureNamesData TextureNames => textureNames ??= GameData.Read<TextureNamesData>("texture-names") ?? throw new InvalidOperationException("data/texture-names.json is missing or can't be read");
	static WorldgenData worldgen;
	// data/worldgen.json
	public static WorldgenData Worldgen => worldgen ??= GameData.Read<WorldgenData>("worldgen") ?? throw new InvalidOperationException("data/worldgen.json is missing or can't be read");
}
