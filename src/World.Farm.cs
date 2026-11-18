using System.Collections.Generic;
using B = Cubeland.Blocks;

namespace Cubeland;

// ---- Farming (Till & Tend) ----
// A Hoe tills Grass or Dirt into soil, Grain Seeds planted on it grow through 8 stages. Soil joined (through other
// tilled soil) to water up to WetReach blocks away is wet and grows twice as fast; Bone Ash fertilizes it for
// FertHarvests ripe harvests. Dry tilled soil with nothing on it turns back into Dirt after RevertTime seconds.
// Only the host runs the timers (TickTimers isn't called in a friend's world); the block changes sync as usual.
public partial class World
{
	public const float StageTime = 40, DryRate = 0.5f, FertRate = 1.6f, RevertTime = 180;
	public const int WetReach = 5, FertHarvests = 2;
	const int Up = WX * WZ;
	public readonly Dictionary<int, float> Crops = new();      // crop spot -> time left in this stage
	public readonly HashSet<int> Soils = new();
	public readonly Dictionary<int, int> FertLeft = new();     // fertilized soil -> harvests left
	readonly Dictionary<int, float> soilIdle = new();
	float farmTimer, wetTimer;

	// keeps the lists above up to date when a block changes (loading: while a save is being put back)
	void FarmChanged(int i, int old, int id, bool loading)
	{
		if (B.IsSoil(id)) Soils.Add(i); else { Soils.Remove(i); soilIdle.Remove(i); }
		if (!B.SoilFert(id)) FertLeft.Remove(i);
		else if (!FertLeft.ContainsKey(i)) FertLeft[i] = FertHarvests;
		if (B.IsCrop(id) && id != B.GrainRipe) { if (old != id) Crops[i] = StageTime; } else Crops.Remove(i);
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
		foreach (var (i, left) in new List<KeyValuePair<int, float>>(Crops))
		{
			int id = Blocks[i];
			if (!B.IsCrop(id) || id == B.GrainRipe) { Crops.Remove(i); continue; }
			float t = left - step * (i >= Up ? GrowRate(Blocks[i - Up]) : 0);
			if (t > 0) { Crops[i] = t; continue; }
			var (x, y, z) = FromIdx(i);
			SetBlock(x, y, z, id + 1);   // (the next stage starts its own timer)
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
