using System;
using System.Collections.Generic;

namespace Cubeland;

// Kilns (the web version's "furnace"): put fuel in, pick what to smelt. Each one takes SmeltTime seconds and then
// waits inside the kiln (Done) until you open it and take it. Picking a recipe more than once lines them up.
// The front glows while it burns. In creative it needs no fuel or ingredients.
public class Kilns
{
	public const float SmeltTime = 5;
	public static readonly (int Out, int From)[] Smelts =
	{
		(Blocks.Brick, Blocks.ClayBall),   // Clay Ball -> Brick (quicker than leaving it on the ground to dry)
		(Blocks.Terracotta, Blocks.Clay),  // Clay -> Terracotta
		(Blocks.Stone, Blocks.Cobblestone),// Cobblestone -> Stone
		(Blocks.BoneAsh, Blocks.Bone),     // Bone -> Bone Ash
		(Blocks.Bread, Blocks.Flour),      // Flour -> Bread
		(Blocks.CookedMeat, Blocks.RawMeat), // Raw Meat -> Cooked Meat
	};
	// how many things one of it smelts: Log / Stripped Log 4, Planks 2, Stick 1
	public static readonly Dictionary<int, int> Fuel = new() { [Blocks.Log] = 4, [Blocks.StrippedLog] = 4, [Blocks.Planks] = 2, [Blocks.Stick] = 1 };

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
			if (f.Queue.Count > 0 && !f.Burning)
			{   // start the next one: it needs fuel (one Log lasts for 4)
				if (!Survival) f.Burning = true;
				else
				{
					if (f.Heat <= 0 && f.FuelN > 0) { f.Heat += Fuel.TryGetValue(f.Fuel, out var h) ? h : 1; if (--f.FuelN == 0) f.Fuel = 0; changed?.Invoke(i); }
					if (f.Heat > 0) { f.Heat--; f.Burning = true; }
				}
				f.T = 0;
			}
			if (f.Burning && (f.T += dt) >= SmeltTime)
			{   // done: it goes into the kiln's own storage, waiting for you
				int outId = f.Queue[0]; f.Queue.RemoveAt(0);
				f.Done[outId] = f.Done.GetValueOrDefault(outId) + 1;
				f.Burning = false; f.T = 0;
				changed?.Invoke(i);
			}
			// the front glows while it's burning
			int want = Blocks.Furnace + Blocks.B[W.Blocks[i]].Facing + (f.Burning ? 4 : 0);
			if (W.Blocks[i] != want) { var (x, y, z) = World.FromIdx(i); W.SetBlock(x, y, z, want); }
		}
	}
	// a kiln that's broken: its fuel, anything waiting to be smelted and what it made fall out
	public void Empty(int i, Action<int, int, int, int> collect)
	{
		if (!All.Remove(i, out var f)) return;
		var (x, y, z) = World.FromIdx(i);
		for (int n = f.FuelN; n > 0; n--) collect(f.Fuel, x, y, z);
		foreach (int o in f.Queue) collect(Array.Find(Smelts, s => s.Out == o).From, x, y, z);
		if (Survival) foreach (var (id, n) in f.Done) for (int k = n; k > 0; k--) collect(id, x, y, z);
	}
}
