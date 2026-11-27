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
	static long Up => (long)WX * WZ;   // (one block up, in world index steps)
	public readonly Dictionary<long, float> Crops = new();      // growing crop spot -> lucky ticks it has had in this stage (see StageChance)
	public readonly HashSet<long> Soils = new();
	public readonly Dictionary<long, int> FertLeft = new();     // fertilized soil -> harvests left
	readonly Dictionary<long, float> soilIdle = new();
	public void SoilWorked(long i) => soilIdle.Remove(i);   // (a chicken scratching it: its turn-back-to-Dirt timer starts again)
	// a grown duck swimming near soil i (Main.DuckNear): a crop on it, if it's wet, grows DuckRate times as fast
	public System.Func<long, bool> DuckNear;
	public static readonly float DuckRate = Nums.Mobs.ANIMALS?.GetValueOrDefault("duck")?.duckRate ?? 1;
	// grass a cow ate: Dirt spot -> seconds until it's Grass again (if nothing has been put on it)
	public readonly Dictionary<long, float> Grazed = new();
	public static readonly float RegrowMin = Nums.Mobs.COW.regrowMin, RegrowMax = Nums.Mobs.COW.regrowMax;   // (data/mobs.json's COW)
	float farmTimer, wetTimer;

	// ---- Saplings: each one grows into its tree at a random moment (lucky ticks, like Grain: SaplingTime seconds on average,
	// in SaplingHits ticks), if its trunk has room; if not, it tries again at its next lucky tick. A Redwood only grows from four
	// saplings in a 2 x 2 square; an Oak in the plains grows a prairie oak. What a sapling grows is GROWN (Grown): saved like
	// your changes, but natural, not put there by you (see Main.Natural). Only the host runs it, like Grain. (data/numbers.json
	// SAPLING; the same as the web version.) ----
	public static readonly float SaplingTime = Nums.N.SAPLING.growTime;
	public static readonly int SaplingHits = Nums.N.SAPLING.growHits;
	public readonly Dictionary<long, float> Saplings = new();   // sapling spot -> lucky ticks so far
	public readonly HashSet<long> Grown = new();                // the blocks saplings grew (saved; natural, not put there)
	public enum Growth { Grew, NoRoom, NeedsFour }
	// grow the sapling at (x, y, z) into its tree now, if it can (justLook: only see whether it could)
	public Growth GrowSapling(int x, int y, int z, bool justLook = false)
	{
		int id = GetBlock(x, y, z); string tree = B.Get(id)?.Grows;
		if (tree == null || y < 1) return Growth.NoRoom;
		var cells = new List<long> { Idx(x, y, z) }; int gx = x, gz = z;
		if (tree == "redwood")
		{   // (the first whole 2 x 2 square of Redwood saplings it's part of, looked for in this order; the tree's corner is the square's)
			bool found = false;
			foreach (var (ox, oz) in new[] { (-1, -1), (0, -1), (-1, 0), (0, 0) })
			{
				int cx = x + ox, cz = z + oz;
				if (!Inside(cx, y, cz) || !Inside(cx + 1, y, cz + 1)) continue;
				if (GetBlock(cx, y, cz) != id || GetBlock(cx + 1, y, cz) != id || GetBlock(cx, y, cz + 1) != id || GetBlock(cx + 1, y, cz + 1) != id) continue;
				gx = cx; gz = cz; cells = new List<long> { Idx(cx, y, cz), Idx(cx + 1, y, cz), Idx(cx, y, cz + 1), Idx(cx + 1, y, cz + 1) }; found = true; break;
			}
			if (!found) return Growth.NeedsFour;
		}
		else if (tree == "oak" && Gen.BiomeOf(x, z) == WorldGen.Plains) tree = "prairie_oak";
		var plan = Gen.PlanTree(tree, gx, y - 1, gz, (a, b, c) => cells.Contains(Idx(a, b, c)) ? 0 : GetBlock(a, b, c), (a, c) => y - 1);
		if (plan == null) return Growth.NoRoom;
		if (justLook) return Growth.Grew;
		foreach (long i in cells) { var (cx, cy, cz) = FromIdx(i); SetBlock(cx, cy, cz, 0); }
		foreach (var (bx, by, bz, bid) in plan) { SetBlock(bx, by, bz, bid); Grown.Add(Idx(bx, by, bz)); }
		return Growth.Grew;
	}
	readonly System.Random farmRnd = new();

	// Random growth: every second each crop has a small chance of a lucky tick, and StageHits lucky ticks take it to its
	// next stage. On average a stage still takes StageTime / GrowRate seconds, but some plants get ahead and some lag a
	// little, so a field doesn't all ripen at the same moment. (Needing 3 ticks, not 1, keeps the randomness gentle.)
	public static readonly int StageHits = Nums.N.FARM.stageHits;
	public static float StageChance(float rate, float step, float stageTime = 0) => System.Math.Min(1, step * rate * StageHits / (stageTime > 0 ? stageTime : StageTime));

	// keeps the lists above up to date when a block changes (loading: while a save is being put back)
	void FarmChanged(long i, int old, int id, bool loading)
	{
		if (B.IsSoil(id)) Soils.Add(i); else { Soils.Remove(i); soilIdle.Remove(i); }
		if (!B.SoilFert(id)) FertLeft.Remove(i);
		else if (!FertLeft.ContainsKey(i)) FertLeft[i] = FertHarvests;
		if (B.CropGrows(id)) { if (old != id) Crops[i] = 0; } else Crops.Remove(i);
		if (B.Get(id)?.Grows != null) { if (old != id) Saplings[i] = 0; } else Saplings.Remove(i);
		// a ripe crop harvested (or knocked away) from fertilized soil uses up one of the fertilizer's harvests (a Cantaloupe
		// Vine's melons use them as they grow)
		if (!loading && B.CropHarvest(old) && id != old && i >= Up) UseFertilizer(i - Up);
	}
	void UseFertilizer(long soil)
	{
		if (!FertLeft.TryGetValue(soil, out int n)) return;
		if (--n > 0) FertLeft[soil] = n;
		else { var (x, y, z) = FromIdx(soil); SetBlock(x, y, z, B.SoilId(B.SoilWet(Blocks[soil]), false)); }
	}

	// how fast a crop grows on this soil (0 = not on soil: it doesn't grow; crop: its own dry rate, if it has one)
	public static float GrowRate(int soil, int crop = 0) => !B.IsSoil(soil) ? 0 : (B.SoilWet(soil) ? 1 : B.CropNums(crop)?.dryRate ?? DryRate) * (B.SoilFert(soil) ? FertRate : 1);

	void TickFarm(float dt)
	{
		if ((wetTimer -= dt) <= 0) { wetTimer = 2; UpdateWetSoil(); }
		if ((farmTimer += dt) < 1) return;
		float step = farmTimer; farmTimer = 0;
		foreach (long i in new List<long>(Crops.Keys))
		{
			int id = Blocks[i];
			if (!B.CropGrows(id)) { Crops.Remove(i); continue; }
			var (x, y, z) = FromIdx(i);
			var cn = B.CropNums(id);
			bool vine = B.B[id] is { Crop: "cantaloupe", Stage: 3 };   // (a grown vine: its lucky ticks make melons, one at a time)
			if (vine && MelonBeside(x, y, z)) { Crops[i] = 0; continue; }
			float rate = i >= Up ? GrowRate(Blocks[i - Up], id) * (B.SoilWet(Blocks[i - Up]) && DuckNear?.Invoke(i - Up) == true ? DuckRate : 1) : 0;   // (a duck near wet soil: faster)
			if (farmRnd.NextDouble() >= StageChance(rate, step, vine ? cn.melonTime : cn?.stageTime ?? 0)) continue;
			int hits = (int)Crops[i] + 1;
			if (Crops[i] >= StageHits) hits = 1;   // (a save from before random growth held seconds left here)
			if (hits < StageHits) { Crops[i] = hits; continue; }
			if (vine) { if (GrowMelon(x, y, z)) { Crops[i] = 0; UseFertilizer(i - Up); } else Crops[i] = StageHits - 1; continue; }   // (no room: it tries at its next lucky tick)
			int next = B.CropNext(id);
			if (B.B[next] is { Crop: "corn", Stage: 2 } && (!Inside(x, y + 1, z) || GetBlock(x, y + 1, z) != 0)) { Crops[i] = StageHits - 1; continue; }   // (Corn grows its top into air only)
			SetBlock(x, y, z, next);
			if (B.B[next] is { Crop: "corn", Stage: >= 2 } nb) SetBlock(x, y + 1, z, B.CropBlock("corn", nb.Stage, true));
		}
		foreach (long i in new List<long>(Saplings.Keys))
		{
			if (!Saplings.ContainsKey(i) || B.Get(Blocks[i])?.Grows == null) { Saplings.Remove(i); continue; }   // (gone: grown into a Redwood with others, or broken)
			if (farmRnd.NextDouble() >= System.Math.Min(1, step * SaplingHits / SaplingTime)) continue;
			float hits = Saplings[i] + 1;
			if (hits < SaplingHits) { Saplings[i] = hits; continue; }
			var (x, y, z) = FromIdx(i);
			if (GrowSapling(x, y, z) != Growth.Grew) Saplings[i] = SaplingHits - 1;   // (no room yet, or a lone Redwood: it tries again at its next lucky tick)
		}
		foreach (long i in new List<long>(Grazed.Keys))
		{
			if (Blocks[i] != B.Dirt) { Grazed.Remove(i); continue; }   // (dug up, tilled or built over)
			if ((Grazed[i] -= step) > 0) continue;
			Grazed.Remove(i);
			var (x, y, z) = FromIdx(i);
			if (!Solid(x, y + 1, z) && !B.IsWater(GetBlock(x, y + 1, z))) SetBlock(x, y, z, B.Grass);
		}
		foreach (long i in new List<long>(Soils))
		{
			if (Blocks[i] != B.Tilled || (i + Up < Blocks.Length && Blocks[i + Up] != 0)) { soilIdle.Remove(i); continue; }
			float t = soilIdle.GetValueOrDefault(i) + step;
			if (t < RevertTime) { soilIdle[i] = t; continue; }
			soilIdle.Remove(i);
			var (x, y, z) = FromIdx(i);
			SetBlock(x, y, z, B.Dirt);
		}
	}

	// ---- Cantaloupe: a grown vine grows a melon on a free side (air, with Dirt, grass, Sand or tilled soil under it), trying the 4
	// sides from a random one; only when none touches it already ----
	static readonly (int, int)[] sides4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };
	bool MelonBeside(int x, int y, int z) { foreach (var (dx, dz) in sides4) if (GetBlock(x + dx, y, z + dz) == B.Cantaloupe) return true; return false; }
	bool GrowMelon(int x, int y, int z)
	{
		if (MelonBeside(x, y, z)) return false;
		int s0 = farmRnd.Next(4);
		for (int k = 0; k < 4; k++)
		{
			var (dx, dz) = sides4[(s0 + k) % 4]; int mx = x + dx, mz = z + dz;
			if (!Inside(mx, y, mz) || GetBlock(mx, y, mz) != 0 || !B.MelonGround(GetBlock(mx, y - 1, mz))) continue;
			SetBlock(mx, y, mz, B.Cantaloupe);
			return true;
		}
		return false;
	}
	static readonly (int, int, int)[] wetTouch = { (0, 1, 0), (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1) };
	static readonly (int, int, int)[] around6 = { (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0), (0, -1, 0) };
	// which soil is wet: soil touching water (above or beside it), and soil joined to that through other tilled soil
	void UpdateWetSoil()
	{
		if (Soils.Count == 0) return;
		var dist = new Dictionary<long, int>(); var queue = new List<long>();
		foreach (long i in Soils)
		{
			var (x, y, z) = FromIdx(i);
			foreach (var (dx, dy, dz) in wetTouch)
				if (B.IsWater(GetBlock(x + dx, y + dy, z + dz))) { dist[i] = 0; queue.Add(i); break; }
		}
		for (int q = 0; q < queue.Count; q++)
		{
			long i = queue[q]; int d = dist[i];
			if (d >= WetReach) continue;
			var (x, y, z) = FromIdx(i);
			foreach (var (dx, dy, dz) in around6)
			{
				int xx = x + dx, yy = y + dy, zz = z + dz;
				if (!Inside(xx, yy, zz)) continue;
				long n = Idx(xx, yy, zz);
				if (Soils.Contains(n) && !dist.ContainsKey(n)) { dist[n] = d + 1; queue.Add(n); }
			}
		}
		foreach (long i in new List<long>(Soils))
		{
			int want = B.SoilId(dist.ContainsKey(i), B.SoilFert(Blocks[i]));
			if (Blocks[i] != want) { var (x, y, z) = FromIdx(i); SetBlock(x, y, z, want); }
		}
	}
}
