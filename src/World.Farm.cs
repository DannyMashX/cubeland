using System.Collections.Generic;
using B = Cubeland.Blocks;

namespace Cubeland;

// ---- Farming (Till & Tend) ----
// A Hoe tills Grass or Dirt into soil, Grain Seeds planted on it grow through 8 stages (at random moments: see StageChance). Soil joined (through other
// tilled soil) to water up to WetReach blocks away is wet and grows twice as fast; Bone Ash fertilizes it for
// FertHarvests ripe harvests. Dry tilled soil with nothing on it turns back into Dirt after RevertTime seconds.
// Only the host runs the timers (TickTimers isn't called in a friend's world); the block changes sync as usual.
public partial class World
{
	public static readonly float StageTime = Nums.N.FARM.stageTime, DryRate = Nums.N.FARM.dryRate, FertRate = Nums.N.FARM.fertRate, RevertTime = Nums.N.FARM.revertTime;   // (data/numbers.json)
	public static readonly int WetReach = Nums.N.FARM.wetReach, FertHarvests = Nums.N.FARM.fertHarvests;
	static int Up => WX * WZ;   // (one block up, in world index steps)
	public readonly Dictionary<int, float> Crops = new();      // growing crop spot -> lucky ticks it has had in this stage (see StageChance)
	public readonly HashSet<int> Soils = new();
	public readonly Dictionary<int, int> FertLeft = new();     // fertilized soil -> harvests left
	readonly Dictionary<int, float> soilIdle = new();
	// grass a cow ate: Dirt spot -> seconds until it's Grass again (if nothing has been put on it)
	public readonly Dictionary<int, float> Grazed = new();
	public static readonly float RegrowMin = Nums.Mobs.COW.regrowMin, RegrowMax = Nums.Mobs.COW.regrowMax;   // (data/mobs.json's COW)
	float farmTimer, wetTimer;
	readonly System.Random farmRnd = new();

	// Random growth: every second each crop has a small chance of a lucky tick, and StageHits lucky ticks take it to its
	// next stage. On average a stage still takes StageTime / GrowRate seconds, but some plants get ahead and some lag a
	// little, so a field doesn't all ripen at the same moment. (Needing 3 ticks, not 1, keeps the randomness gentle.)
	public static readonly int StageHits = Nums.N.FARM.stageHits;
	public static float StageChance(float rate, float step) => System.Math.Min(1, step * rate * StageHits / StageTime);

	// keeps the lists above up to date when a block changes (loading: while a save is being put back)
	void FarmChanged(int i, int old, int id, bool loading)
	{
		if (B.IsSoil(id)) Soils.Add(i); else { Soils.Remove(i); soilIdle.Remove(i); }
		if (!B.SoilFert(id)) FertLeft.Remove(i);
		else if (!FertLeft.ContainsKey(i)) FertLeft[i] = FertHarvests;
		if (B.IsCrop(id) && id != B.GrainRipe) { if (old != id) Crops[i] = 0; } else Crops.Remove(i);
		// a ripe Grain harvested (or knocked away) from fertilized soil uses up one of the fertilizer's harvests
		if (!loading && old == B.GrainRipe && id != B.GrainRipe && i >= Up && FertLeft.TryGetValue(i - Up, out int n))
		{
			if (--n > 0) FertLeft[i - Up] = n;
			else { var (x, y, z) = FromIdx(i - Up); SetBlock(x, y, z, B.SoilId(B.SoilWet(Blocks[i - Up]), false)); }
		}
	}

	// how fast Grain grows on this soil (0 = not on soil: it doesn't grow)
	public static float GrowRate(int soil) => !B.IsSoil(soil) ? 0 : (B.SoilWet(soil) ? 1 : DryRate) * (B.SoilFert(soil) ? FertRate : 1);

	void TickFarm(float dt)
	{
		if ((wetTimer -= dt) <= 0) { wetTimer = 2; UpdateWetSoil(); }
		if ((farmTimer += dt) < 1) return;
		float step = farmTimer; farmTimer = 0;
		foreach (int i in new List<int>(Crops.Keys))
		{
			int id = Blocks[i];
			if (!B.IsCrop(id) || id == B.GrainRipe) { Crops.Remove(i); continue; }
			if (farmRnd.NextDouble() >= StageChance(i >= Up ? GrowRate(Blocks[i - Up]) : 0, step)) continue;
			int hits = (int)Crops[i] + 1;
			if (Crops[i] >= StageHits) hits = 1;   // (a save from before random growth held seconds left here)
			if (hits < StageHits) { Crops[i] = hits; continue; }
			var (x, y, z) = FromIdx(i);
			SetBlock(x, y, z, id + 1);
		}
		foreach (int i in new List<int>(Grazed.Keys))
		{
			if (Blocks[i] != B.Dirt) { Grazed.Remove(i); continue; }   // (dug up, tilled or built over)
			if ((Grazed[i] -= step) > 0) continue;
			Grazed.Remove(i);
			var (x, y, z) = FromIdx(i);
			if (!Solid(x, y + 1, z) && !B.IsWater(GetBlock(x, y + 1, z))) SetBlock(x, y, z, B.Grass);
		}
		foreach (int i in new List<int>(Soils))
		{
			if (Blocks[i] != B.Tilled || (i + Up < Blocks.Length && Blocks[i + Up] != 0)) { soilIdle.Remove(i); continue; }
			float t = soilIdle.GetValueOrDefault(i) + step;
			if (t < RevertTime) { soilIdle[i] = t; continue; }
			soilIdle.Remove(i);
			var (x, y, z) = FromIdx(i);
			SetBlock(x, y, z, B.Dirt);
		}
	}

	static readonly (int, int, int)[] wetTouch = { (0, 1, 0), (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1) };
	static readonly (int, int, int)[] around6 = { (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0), (0, -1, 0) };
	// which soil is wet: soil touching water (above or beside it), and soil joined to that through other tilled soil
	void UpdateWetSoil()
	{
		if (Soils.Count == 0) return;
		var dist = new Dictionary<int, int>(); var queue = new List<int>();
		foreach (int i in Soils)
		{
			var (x, y, z) = FromIdx(i);
			foreach (var (dx, dy, dz) in wetTouch)
				if (B.IsWater(GetBlock(x + dx, y + dy, z + dz))) { dist[i] = 0; queue.Add(i); break; }
		}
		for (int q = 0; q < queue.Count; q++)
		{
			int i = queue[q], d = dist[i];
			if (d >= WetReach) continue;
			var (x, y, z) = FromIdx(i);
			foreach (var (dx, dy, dz) in around6)
			{
				int xx = x + dx, yy = y + dy, zz = z + dz;
				if (!Inside(xx, yy, zz)) continue;
				int n = Idx(xx, yy, zz);
				if (Soils.Contains(n) && !dist.ContainsKey(n)) { dist[n] = d + 1; queue.Add(n); }
			}
		}
		foreach (int i in new List<int>(Soils))
		{
			int want = B.SoilId(dist.ContainsKey(i), B.SoilFert(Blocks[i]));
			if (Blocks[i] != want) { var (x, y, z) = FromIdx(i); SetBlock(x, y, z, want); }
		}
	}
}
