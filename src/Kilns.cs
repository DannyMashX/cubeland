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
	public static readonly float SmeltTime = Nums.N.SMELT_TIME;   // (data/numbers.json)
	public record Smelt(int Out, (int Id, int N)[] Needs, string At = "kiln") { public int From => Needs[0].Id; }
	// what each smelts, in the order the Kiln's and the Foundry's lists show them, and how many things one of each fuel smelts:
	// data/smelting.json (the deep layers' Cobblestone -> its Stone only while tall worlds are on)
	static readonly SmeltingData Data = GameData.Read<SmeltingData>("smelting") ?? throw new InvalidOperationException("data/smelting.json is missing or can't be read");
	public static readonly Smelt[] Smelts = SmeltsFrom(Data, World.TallWorlds);
	public static readonly Dictionary<int, int> Fuel = FuelFrom(Data);
	// data/smelting.json as the game's tables (tall: with the deep layers' smelts)
	public static Smelt[] SmeltsFrom(SmeltingData d, bool tall) => d.SMELTS.Where(s => tall || !s.tall).Select(s => new Smelt(DataKeys.IdOf(s.@out),
		s.needs != null ? s.needs.Select(n => (DataKeys.IdOf(n[0].GetString()), n[1].GetInt32())).ToArray() : new[] { (DataKeys.IdOf(s.from), 1) }, s.at ?? "kiln")).ToArray();
	public static Dictionary<int, int> FuelFrom(SmeltingData d) => d.FUEL.ToDictionary(kv => DataKeys.IdOf(kv.Key), kv => kv.Value);

	public class Kiln
	{
		public int Fuel, FuelN, Heat; public List<int> Queue = new(); public float T; public bool Burning;
		public SortedDictionary<int, int> Done = new();
	}
	public readonly Dictionary<long, Kiln> All = new();   // world index -> kiln
	readonly World W;
	public bool Survival;
	public Kilns(World w) { W = w; }
	public Kiln Of(long i) { if (!All.TryGetValue(i, out var k)) All[i] = k = new Kiln(); return k; }

	public void Tick(float dt, Action<long> changed)
	{
		foreach (var (i, f) in new List<KeyValuePair<long, Kiln>>(All))
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
	public bool FoundryBuilt(long i)
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
	public void Empty(long i, Action<int, int, int, int> collect)
	{
		if (!All.Remove(i, out var f)) return;
		var (x, y, z) = World.FromIdx(i);
		for (int n = f.FuelN; n > 0; n--) collect(f.Fuel, x, y, z);
		foreach (int o in f.Queue) foreach (var (id, n) in Array.Find(Smelts, s => s.Out == o).Needs) for (int k = n; k > 0; k--) collect(id, x, y, z);
		if (Survival) foreach (var (id, n) in f.Done) for (int k = n; k > 0; k--) collect(id, x, y, z);
	}
}
