using System;
using System.Collections.Generic;
using System.Linq;

namespace Cubeland;

// Kilns (the web version's "furnace"): put fuel in, pick what to smelt. Each one takes SmeltTime seconds and then
// waits inside the kiln (Done) until you open it and take it. Picking a recipe more than once lines them up.
// The front glows while it burns. In creative it needs no fuel or ingredients.
// The Foundry's Furnace works the same way, with its own list (At "foundry"), but only while the whole Foundry is built
// around it (FoundryBuilt). A recipe takes Needs: [(id, how many), ...] (usually just one thing).
public class Kilns
{
	public const float SmeltTime = 5;
	public record Smelt(int Out, (int Id, int N)[] Needs, string At = "kiln") { public int From => Needs[0].Id; }
	static Smelt S(int o, int from, string at = "kiln") => new(o, new[] { (from, 1) }, at);
	public static readonly Smelt[] Smelts = new[]
	{
		S(Blocks.Brick, Blocks.ClayBall),   // Clay Ball -> Brick (quicker than leaving it on the ground to dry)
		S(Blocks.Terracotta, Blocks.Clay),  // Clay -> Terracotta
		S(Blocks.Stone, Blocks.Cobblestone),// Cobblestone -> Stone
		S(Blocks.BoneAsh, Blocks.Bone),     // Bone -> Bone Ash
		S(Blocks.Bread, Blocks.Flour),      // Flour -> Bread
		S(Blocks.CookedMeat, Blocks.RawMeat), // Raw Meat -> Cooked Meat
	}.Concat(Enumerable.Range(0, World.TallWorlds ? Blocks.StrataLayers - 1 : 0).Select(k => S(Blocks.Strata + k, Blocks.DeepCobble + k)))   // a deep layer's Cobblestone -> its Stone
	.Concat(new[]
	{
		S(Blocks.FireclayBrick, Blocks.FireclayBall),        // Fireclay Ball -> Fireclay Brick
		S(Blocks.FireclayTerracotta, Blocks.Fireclay),       // Fireclay -> Fireclay Terracotta
		S(Blocks.SmoothSandstone, Blocks.Sandstone),         // Sandstone -> Smooth Sandstone
		S(Blocks.MudBricks, Blocks.Mud),                     // Mud -> Mud Bricks
	})
	// the Foundry: a Chunk -> its Brick; two metals' Bricks -> an alloy; an Iron Brick -> Steel Scrap, 4 Steel Scrap -> a Steel Brick
	.Concat(Blocks.Metals.Select(m => S(Blocks.MetalBrick[m], Blocks.Chunk[m], "foundry")))
	.Concat(new[]
	{
		new Smelt(Blocks.MetalBrick["bronite"], new[] { (Blocks.MetalBrick["copper"], 1), (Blocks.MetalBrick["tin"], 1) }, "foundry"),
		new Smelt(Blocks.MetalBrick["rosium"], new[] { (Blocks.MetalBrick["gold"], 1), (Blocks.MetalBrick["copper"], 1) }, "foundry"),
		S(Blocks.SteelScrap, Blocks.MetalBrick["iron"], "foundry"),
		new Smelt(Blocks.SteelBrick, new[] { (Blocks.SteelScrap, 4) }, "foundry"),
	}).ToArray();
	// how many things one of it smelts: Log / Stripped Log 4, Planks 2, Stick 1; Peat (from the swamp) 8
	public static readonly Dictionary<int, int> Fuel = new() { [Blocks.Log] = 4, [Blocks.StrippedLog] = 4, [Blocks.Planks] = 2, [Blocks.Stick] = 1,
		[Blocks.FirLog] = 4, [Blocks.StrippedFirLog] = 4, [Blocks.FirPlanks] = 2, [Blocks.PalmLog] = 4, [Blocks.StrippedPalmLog] = 4, [Blocks.PalmPlanks] = 2,
		[Blocks.WillowLog] = 4, [Blocks.StrippedWillowLog] = 4, [Blocks.WillowPlanks] = 2, [Blocks.Peat] = 8 };

	public class Kiln
	{
		public int Fuel, FuelN, Heat; public List<int> Queue = new(); public float T; public bool Burning;
		public SortedDictionary<int, int> Done = new();
	}
	public readonly Dictionary<int, Kiln> All = new();   // world index -> kiln
	readonly World W;
	public bool Survival;
	public Kilns(World w) { W = w; }
	public Kiln Of(int i) { if (!All.TryGetValue(i, out var k)) All[i] = k = new Kiln(); return k; }

	public void Tick(float dt, Action<int> changed)
	{
		foreach (var (i, f) in new List<KeyValuePair<int, Kiln>>(All))
		{
			if (!Blocks.IsFurnace(W.Blocks[i])) { All.Remove(i); continue; }
			// the Foundry only works while it's all built: until then it waits (what's burning keeps its place)
			bool ready = !Blocks.IsFoundry(W.Blocks[i]) || FoundryBuilt(i);
			if (ready && f.Queue.Count > 0 && !f.Burning)
			{   // start the next one: it needs fuel (one Log lasts for 4)
				if (!Survival) f.Burning = true;
				else
				{
					if (f.Heat <= 0 && f.FuelN > 0) { f.Heat += Fuel.TryGetValue(f.Fuel, out var h) ? h : 1; if (--f.FuelN == 0) f.Fuel = 0; changed?.Invoke(i); }
					if (f.Heat > 0) { f.Heat--; f.Burning = true; }
				}
				f.T = 0;
			}
			if (ready && f.Burning && (f.T += dt) >= SmeltTime)
			{   // done: it goes into the kiln's own storage, waiting for you
				int outId = f.Queue[0]; f.Queue.RemoveAt(0);
				f.Done[outId] = f.Done.GetValueOrDefault(outId) + 1;
				f.Burning = false; f.T = 0;
				changed?.Invoke(i);
			}
			// the front glows while it's burning
			int want = Blocks.FurnaceBase(W.Blocks[i]) + Blocks.B[W.Blocks[i]].Facing + (f.Burning && ready ? 4 : 0);
			if (W.Blocks[i] != want) { var (x, y, z) = World.FromIdx(i); W.SetBlock(x, y, z, want); }
		}
	}
	// The Foundry: a 3x3x3 with a hollow middle. Bottom: 9 Fireclay Terracotta; middle: the Furnace in the middle of one side
	// (its front facing out) and 7 Fireclay Casing around the empty centre; top: 9 Fireclay Bricks.
	public bool FoundryBuilt(int i)
	{
		var (x, y, z) = World.FromIdx(i); var fr = Blocks.B[W.Blocks[i]].Front; int cx = x - fr[0], cz = z - fr[1];
		for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
		{
			int ax = cx + dx, az = cz + dz, mid = W.GetBlock(ax, y, az);
			if (W.GetBlock(ax, y - 1, az) != Blocks.FireclayTerracotta || W.GetBlock(ax, y + 1, az) != Blocks.FireclayBricks) return false;
			if (dx == 0 && dz == 0) { if (mid != 0) return false; }
			else if ((ax != x || az != z) && mid != Blocks.FireclayCasing) return false;
		}
		return true;
	}
	// a kiln that's broken: its fuel, anything waiting to be smelted and what it made fall out
	public void Empty(int i, Action<int, int, int, int> collect)
	{
		if (!All.Remove(i, out var f)) return;
		var (x, y, z) = World.FromIdx(i);
		for (int n = f.FuelN; n > 0; n--) collect(f.Fuel, x, y, z);
		foreach (int o in f.Queue) foreach (var (id, n) in Array.Find(Smelts, s => s.Out == o).Needs) for (int k = n; k > 0; k--) collect(id, x, y, z);
		if (Survival) foreach (var (id, n) in f.Done) for (int k = n; k > 0; k--) collect(id, x, y, z);
	}
}
