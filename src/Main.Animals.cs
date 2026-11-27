using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Cubeland;

// ===================== Animals =====================
// Chickens, Ducks, Deer, Goats, Boar and Bears (data/mobs.json: their MOBS rows say animal; ANIMALS has their numbers, named
// as in COW where they mean the same). They follow the cows' rules: kept with the world, happy from their food (right-click
// holding it) or their own foraging, mating when happy and not crowded (counting their own kind), no young born once a chunk
// holds perChunk of their kind, the young (the <type>baby model) growing up in babyTime (food takes babyFoodBoost off). Each
// also helps or harms your land, in a loop you can tend:
// - Chickens: fed Corn or seeds, they lay an Egg a while later. Scratching on empty tilled soil fertilizes it now and then;
//   among young crops they peck them back a stage (a seedling is eaten).
// - Ducks: live on water and turn back at the banks (ashore, they head for water). Fed, they lay an Egg on the nearest bank.
//   Wet soil near one grows crops faster (see DuckNear).
// - Deer: run from you unless you hold their food. They carry the sapling of the leaves they last passed and now and then
//   plant it where they roam (it counts as natural), and they nibble saplings (yours too).
// - Goats: eat Dead Bushes and low leaves that grew there (not a hedge you planted). Fed, they're milky for a while: a Clay
//   Jug used on one gives a Jug of Milk.
// - Boar: near an Oak or a Maple they root up Grass to Dirt (ready to till), now and then turning up a Truffle. Hit, they
//   fight back (survival).
// - Bears: hungry now and then, they hunt Chickens, Ducks, Deer and Boar (never cows), which run from them; a kill leaves
//   only its Feathers. They leave you alone unless you hit one or go near its cubs. They can't open doors either.
// Spawned only in Biomes worlds. The host's game runs them. (The same as the web version, index.html's Animals.)
public partial class Main
{
	static int KeyOr(string key) => DataKeys.TryIdOf(key, out int id) ? id : -1;
	static readonly int Feather = KeyOr("feather"), Egg = KeyOr("egg"), Truffle = KeyOr("truffle"), ClayJug = KeyOr("clay_jug"), JugOfMilk = KeyOr("jug_of_milk");
	static readonly Dictionary<string, HashSet<int>> AnimalFood = new(), AnimalGround = new(), AnimalLogs = new();
	static readonly Dictionary<string, int[]> AnimalBiomes = new();
	static Main()
	{
		foreach (var (type, a) in Nums.Mobs.ANIMALS ?? new Dictionary<string, AnimalNumbers>())
		{
			AnimalFood[type] = (a.food ?? Array.Empty<string>()).Select(DataKeys.IdOf).ToHashSet();
			AnimalGround[type] = (a.ground ?? Array.Empty<string>()).Where(k => k != "water").Select(DataKeys.IdOf).ToHashSet();
			AnimalBiomes[type] = (a.biomes ?? Array.Empty<string>()).Select(b => Array.IndexOf(WorldGen.BiomeNames, b)).ToArray();
			AnimalLogs[type] = (a.logs ?? Array.Empty<string>()).Select(DataKeys.IdOf).ToHashSet();   // (boar: the trees they root under)
		}
	}
	float RandIn(float[] r) => r[0] + (float)rnd.NextDouble() * (r[1] - r[0]);
	static float D2(Mob a, Mob b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);
	int KinNear(Mob m, float r) => mobList.Count(o => o != m && o.Type == m.Type && o.Dead == 0 && D2(o, m) < r * r);
	int KinInChunk(string type, (int, int) c) => mobList.Count(o => o.Type == type && o.Dead == 0 && ChunkOf(o.X, o.Z) == c);
	static bool GrownBear(Mob o) => o.Type == "bear" && o.Dead == 0 && o.Baby <= 0;

	Mob SpawnAnimal(string type, float x, float y, float z, float baby = 0)
	{
		var A = MobTypes[type].A;
		var m = SpawnMob(type, x, y, z, baby > 0 ? 1 : 0);
		m.Baby = baby; if (baby > 0) m.Health = A.babyHealth;
		if (type == "bear") m.HungerIn = A.huntCool;
		if (A.scratchTime != null) m.ScratchIn = RandIn(A.scratchTime);
		if (A.peckTime != null) m.PeckIn = RandIn(A.peckTime);
		m.SowIn = A.sowEvery; m.NibbleIn = A.nibbleEvery;
		return m;
	}

	// ---- spawning: now and then, a spot a little way off; one of the animals that live in its biome (weighted by spawnWeight),
	// if fewer than nearMax of them are about, comes as a group (some young), on their kind of ground ----
	float animalSpawnIn = 8;
	void SpawnAnimals()
	{
		if (current?.Gen != 3 || (animalSpawnIn -= 3) > 0) return;
		animalSpawnIn = 20 + (float)rnd.NextDouble() * 40;
		var P = player.Pos;
		bool none = !mobList.Any(m => m.T.Animal);
		double a = rnd.NextDouble() * Math.PI * 2, r = none ? 12 + rnd.NextDouble() * 20 : 30 + rnd.NextDouble() * 30;
		int x = (int)Math.Floor(P.X + Math.Cos(a) * r), z = (int)Math.Floor(P.Z + Math.Sin(a) * r);
		if (x < 3 || z < 3 || x >= World.WX - 3 || z >= World.WZ - 3) return;
		int bi = world.Gen.BiomeOf(x, z);
		int About(string type) => mobList.Count(m => m.Type == type && m.Dead == 0 && (m.X - P.X) * (m.X - P.X) + (m.Z - P.Z) * (m.Z - P.Z) < 80 * 80);
		var kinds = AnimalBiomes.Keys.Where(t => MobTypes.ContainsKey(t) && models.ContainsKey(t) && AnimalBiomes[t].Contains(bi) && About(t) < MobTypes[t].A.nearMax).ToList();
		if (kinds.Count == 0) return;
		double w = rnd.NextDouble() * kinds.Sum(t => MobTypes[t].A.spawnWeight); string type = kinds[^1];
		foreach (var t in kinds) if ((w -= MobTypes[t].A.spawnWeight) < 0) { type = t; break; }
		var A = MobTypes[type].A;
		if (AnimalSpot(type, x, z) is not { } s) return;
		int n = A.group[0] + rnd.Next(A.group[1] - A.group[0] + 1);
		int cubs = A.cubs != null && rnd.NextDouble() < A.cubChance ? A.cubs[0] + rnd.Next(A.cubs[1] - A.cubs[0] + 1) : 0;
		for (int k = 0; k < n + cubs; k++)
		{
			float px = s.X + (k > 0 ? (float)(rnd.NextDouble() - 0.5) * 5 : 0), pz = s.Z + (k > 0 ? (float)(rnd.NextDouble() - 0.5) * 5 : 0);
			float? py = type == "duck" ? s.Y : StandSpot(px, s.Y, pz);
			if (py is not { } yy || (type == "duck" && !player.WaterAt(px, yy, pz))) continue;
			bool young = k >= n || (k > 0 && A.cubs == null && rnd.NextDouble() < 0.25);
			SpawnAnimal(type, px, yy, pz, young ? A.babyTime * (0.3f + (float)rnd.NextDouble() * 0.7f) : 0);
		}
	}
	// where a group of this kind could start at column (x, z) (y: its feet), or null. Ducks: on top of water (not ice) with
	// land within 3. Deer: a log within 6. Boar: one of their trees within 8. Goats: at least minHeight above sea level.
	Vector3? AnimalSpot(string type, int x, int z)
	{
		var A = MobTypes[type].A;
		int y = World.WY - 1;
		while (y > 0 && world.GetBlock(x, y, z) == 0) y--;
		int top = world.GetBlock(x, y, z);
		if (world.GetBlock(x, y + 1, z) != 0 || world.GetBlock(x, y + 2, z) != 0) return null;
		if (type == "duck")
		{
			if (!Blocks.IsWater(top)) return null;
			bool land = false;
			for (int dz = -3; dz <= 3 && !land; dz++) for (int dx = -3; dx <= 3 && !land; dx++) { int b = world.GetBlock(x + dx, y, z + dz); land = b != 0 && !Blocks.IsWater(b) && world.Solid(x + dx, y, z + dz); }
			return land ? new Vector3(x + 0.5f, y + 0.6f, z + 0.5f) : null;
		}
		if (!AnimalGround[type].Contains(top)) return null;
		if (A.minHeight is { } mh && y < world.SeaLevel + mh) return null;
		if (A.logNear is { } ln && !LogNear(x, y + 1, z, ln, AnimalLogs[type].Count > 0 ? AnimalLogs[type] : null)) return null;
		return new Vector3(x + 0.5f, y + 1, z + 0.5f);
	}
	// a log (any, or one of these) within r across of (x, y, z), from 2 below to 8 above
	bool LogNear(int x, int y, int z, int r, HashSet<int> which)
	{
		for (int dy = -2; dy <= 8; dy++) for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++)
		{
			int b = world.GetBlock(x + dx, y + dy, z + dz);
			if (which != null ? which.Contains(b) : Blocks.IsLogBlock(b)) return true;
		}
		return false;
	}

	// ---- each frame, before it thinks: what an animal notices. Prey run from a grown bear within the bear's preyFlee; deer run
	// from you within shyRange unless you hold their food; a bear turns on you if you go near its cubs (survival) ----
	void AnimalAlert(Mob m, float dt)
	{
		if (m.Angry > 0) m.Angry = Math.Max(0, m.Angry - dt);
		m.Chase = false;
		if ((m.AlertIn -= dt) > 0) return;
		m.AlertIn = 0.5f;
		var bearA = MobTypes.TryGetValue("bear", out var bt) ? bt.A : null;
		if (bearA != null && bearA.prey.Contains(m.Type))
		{
			var b = mobList.FirstOrDefault(o => GrownBear(o) && D2(o, m) < bearA.preyFlee * bearA.preyFlee);
			if (b != null) { m.Flee = 1; m.FleeFrom = b; return; }
		}
		var A = m.T.A; var P = player.Pos;
		float pd = MathF.Sqrt((P.X - m.X) * (P.X - m.X) + (P.Z - m.Z) * (P.Z - m.Z));
		if (m.Type == "deer" && !dead && pd < A.shyRange && !AnimalFood["deer"].Contains(inv.Held)) { m.Flee = 1; m.FleeFrom = null; }
		if (m.Type == "bear" && m.Baby <= 0 && inv.Survival && !dead
			&& mobList.Any(o => o.Type == "bear" && o.Baby > 0 && o.Dead == 0 && MathF.Sqrt(D2(o, m)) < A.cubNear && MathF.Sqrt((o.X - P.X) * (o.X - P.X) + (o.Z - P.Z) * (o.Z - P.Z)) < A.cubRange))
			m.Angry = A.angerTime;
	}

	// ---- an animal's own thinking each frame (the host's game). Returns true while it's busy (foraging, chasing, going to a
	// mate, a young one following, a duck heading for water), so the usual wandering waits ----
	bool AnimalThink(Mob m, float dt)
	{
		var A = m.T.A; bool grown = m.Baby <= 0;
		m.Happy = Math.Max(0, m.Happy - dt);
		m.MateCool = Math.Max(0, m.MateCool - dt);
		if (m.Baby > 0 && (m.Baby -= dt) <= 0) { m.Baby = 0; m.Health = m.T.Health; SetVariant(m, 0); }
		if (m.Milky > 0) m.Milky = Math.Max(0, m.Milky - dt);
		if (m.EggIn > 0 && (m.EggIn -= dt) <= 0) { m.EggIn = 0; LayEgg(m); }
		int bx = (int)MathF.Floor(m.X), by = (int)MathF.Floor(m.Y), bz = (int)MathF.Floor(m.Z);
		// the animals' own work, on their own timers
		if (m.Type == "chicken" && grown) ChickenWork(m, dt, bx, by, bz);
		if (m.Type == "deer") DeerWork(m, dt, bx, by, bz);
		if (m.Type == "bear" && grown && BearHunt(m, dt)) return true;
		// foraging: head down for a moment, then its good done (see ForageDone)
		if (m.Grazing > 0)
		{
			m.Walking = false;
			if ((m.Grazing -= dt) <= 0) { ForageDone(m, bx, by, bz); m.Timer = 1 + (float)rnd.NextDouble() * 2; }
			return true;
		}
		// a duck: keeps to the water
		if (m.Type == "duck" && DuckWater(m, dt)) return true;
		// looking for a mate (grown, happy, not crowded, with room in the chunk for young)
		if (m.Mate is { } mate)
		{
			if (mate.Dead > 0 || mate.Mate != m || (m.MateFor -= dt) <= 0 || !mobList.Contains(mate)) { m.Mate = null; if (mate.Mate == m) mate.Mate = null; return false; }
			if (MathF.Sqrt(D2(mate, m)) < 1.6f) { AnimalYoung(m, mate); return false; }
			WalkToward(m, mate.X, mate.Z, dt);
			return true;
		}
		if (grown && m.Happy > 0 && m.MateCool <= 0 && (m.MateCheck -= dt) <= 0)
		{
			m.MateCheck = 3 + (float)rnd.NextDouble() * 3;
			if (CanMateA(m) && rnd.NextDouble() < 0.5)
			{
				var other = mobList.Where(o => o != m && o.Type == m.Type && o.Mate == null && D2(o, m) < 64 && CanMateA(o)).OrderBy(o => D2(o, m)).FirstOrDefault();
				if (other != null) { m.Mate = other; other.Mate = m; m.MateFor = other.MateFor = 20; m.Grazing = other.Grazing = 0; return true; }
			}
		}
		// the young keep near a grown one of their kind
		if (!grown)
		{
			var mum = mobList.Where(o => o.Type == m.Type && o.Baby <= 0 && o.Dead == 0 && D2(o, m) < 16 * 16).OrderBy(o => D2(o, m)).FirstOrDefault();
			if (mum != null && D2(mum, m) > 16) { WalkToward(m, mum.X, mum.Z, dt); return true; }
		}
		// standing about: forage now and then (while not very happy, for most)
		if (A.forage != null && !m.Walking && m.Happy < A.forage.below && (m.GrazeIn -= dt) <= 0)
		{
			m.GrazeIn = RandIn(A.forage.every);
			if (CanForage(m, bx, by, bz)) { m.Grazing = A.forage.time; return true; }
		}
		return false;
	}
	bool CanMateA(Mob m)
	{
		var A = m.T.A;
		return m.Dead == 0 && m.Baby <= 0 && m.Happy > 0 && m.MateCool <= 0 && m.Flee <= 0 && m.Angry <= 0 && KinNear(m, A.nearRange) < A.crowdedAt && KinInChunk(m.Type, ChunkOf(m.X, m.Z)) < A.perChunk;
	}
	// two met: young between them (if the chunk has room), and neither wants to again for a while
	void AnimalYoung(Mob a, Mob b)
	{
		var A = a.T.A;
		a.Mate = b.Mate = null;
		a.MateCool = b.MateCool = A.mateCooldown;
		a.Happy = Math.Max(0, a.Happy - A.mateCost); b.Happy = Math.Max(0, b.Happy - A.mateCost);
		float x = (a.X + b.X) / 2, z = (a.Z + b.Z) / 2;
		float? y = a.Type == "duck" ? Math.Max(a.Y, b.Y) : StandSpot(x, Math.Max(a.Y, b.Y), z);
		if (y is not { } yy || KinInChunk(a.Type, ChunkOf(x, z)) >= A.perChunk) return;
		SpawnAnimal(a.Type, x, yy, z, A.babyTime);
		for (int k = 0; k < 4; k++) AddHeart(new Vector3(x, yy + 1, z), k * 0.15f);
	}
	// can it forage here, now? (chickens: on Grass; ducks: in water; deer: by leaves; goats: something to clear; boar: Grass
	// near their trees)
	bool CanForage(Mob m, int x, int y, int z)
	{
		var A = m.T.A; int under = world.GetBlock(x, y - 1, z);
		switch (m.Type)
		{
			case "chicken": return Blocks.IsGrassy(under);
			case "duck": return player.WaterAt(m.X, m.Y + 0.3f, m.Z);
			case "deer":
				for (int dy = 0; dy <= 3; dy++) for (int dz = -2; dz <= 2; dz++) for (int dx = -2; dx <= 2; dx++) if (Blocks.IsLeaves(world.GetBlock(x + dx, y + dy, z + dz))) return true;
				return false;
			case "goat": return GoatTarget(x, y, z) != null;
			case "boar": return under == Blocks.Grass && LogNear(x, y, z, A.rootNear, AnimalLogs["boar"]);
		}
		return false;
	}
	// foraging done: happier, and for some, a change to the land
	void ForageDone(Mob m, int x, int y, int z)
	{
		var A = m.T.A;
		if (m.Type == "goat") { if (GoatTarget(x, y, z) is not { } t) return; world.SetBlock(t.x, t.y, t.z, 0); Sound.I?.Place(5); }
		if (m.Type == "boar")
		{
			if (world.GetBlock(x, y - 1, z) != Blocks.Grass) return;
			world.SetBlock(x, y - 1, z, Blocks.Dirt); Sound.I?.Place(Blocks.Dirt);   // (rooted up: Dirt, which stays Dirt)
			if (Truffle > 0 && rnd.NextDouble() < A.truffleChance) drops.Add(Truffle, x + 0.5f, y + 0.3f, z + 0.5f);
		}
		m.Happy = Math.Min(A.happyMax, m.Happy + A.forage.happy);
	}
	// what a goat would eat within 2 of it: a Dead Bush, or a natural leaf block with the ground at most 2 below (a shrub, not a
	// tree's crown, and never a hedge someone planted)
	(int x, int y, int z)? GoatTarget(int x, int y, int z)
	{
		bool Ground(int g) => g != 0 && !Blocks.IsLeaves(g) && !Blocks.IsLogBlock(g) && !Blocks.IsWater(g) && Blocks.Get(g) is { NoCollide: false };
		for (int dy = -1; dy <= 2; dy++) for (int dz = -2; dz <= 2; dz++) for (int dx = -2; dx <= 2; dx++)
		{
			int xx = x + dx, yy = y + dy, zz = z + dz, b = world.GetBlock(xx, yy, zz);
			if (b == Blocks.DeadBush) return (xx, yy, zz);
			if (!Blocks.IsLeaves(b) || !World.Inside(xx, yy, zz) || !Natural(xx, yy, zz)) continue;
			int g1 = world.GetBlock(xx, yy - 1, zz), g2 = world.GetBlock(xx, yy - 2, zz);
			if (Ground(g1) || ((g1 == 0 || Blocks.IsLeaves(g1)) && Ground(g2))) return (xx, yy, zz);
		}
		return null;
	}
	// an egg laid: a chicken's where it stands; a duck's on the nearest dry ground within eggReach (or where it is)
	void LayEgg(Mob m)
	{
		if (Egg <= 0) return;
		var at = new Vector3(m.X, m.Y + 0.3f, m.Z);
		if (m.Type == "duck")
		{
			var A = m.T.A; int x = (int)MathF.Floor(m.X), y = (int)MathF.Floor(m.Y + 0.3f), z = (int)MathF.Floor(m.Z), R = A.eggReach;
			int best = int.MaxValue;
			for (int dy = -1; dy <= 1; dy++) for (int dz = -R; dz <= R; dz++) for (int dx = -R; dx <= R; dx++)
			{
				int g = world.GetBlock(x + dx, y + dy - 1, z + dz), d = dx * dx + dz * dz;
				if (d < best && g != 0 && world.Solid(x + dx, y + dy - 1, z + dz) && !Blocks.IsWater(g) && world.GetBlock(x + dx, y + dy, z + dz) == 0) { best = d; at = new Vector3(x + dx + 0.5f, y + dy + 0.3f, z + dz + 0.5f); }
			}
		}
		drops.Add(Egg, at.X, at.Y, at.Z);
	}
	// a chicken's scratching and pecking (grown ones)
	static readonly (int dx, int dz)[] PeckSides = { (0, 0), (0, 1), (1, 0), (0, -1), (-1, 0) };   // (where it stands, then beside it: the web version's order)
	void ChickenWork(Mob m, float dt, int x, int y, int z)
	{
		var A = m.T.A;
		if ((m.ScratchIn -= dt) <= 0)
		{
			m.ScratchIn = RandIn(A.scratchTime);
			int s = world.GetBlock(x, y - 1, z);
			if (Blocks.IsSoil(s) && world.GetBlock(x, y, z) == 0)
			{   // empty tilled soil: it stays tilled, and now and then it's fertilized
				world.SoilWorked(World.Idx(x, y - 1, z));
				if (!Blocks.SoilFert(s) && rnd.NextDouble() < A.fertChance) world.SetBlock(x, y - 1, z, Blocks.SoilId(Blocks.SoilWet(s), true));
			}
		}
		if ((m.PeckIn -= dt) <= 0)
		{
			m.PeckIn = RandIn(A.peckTime);
			if (rnd.NextDouble() >= A.peckChance) return;
			foreach (var (dx, dz) in PeckSides)
			{
				int b = world.GetBlock(x + dx, y, z + dz);
				if (!Blocks.IsAnyCrop(b)) continue;
				bool top = Blocks.B[b].Top;
				if (!Blocks.CropRipe(top ? world.GetBlock(x + dx, y - 1, z + dz) : b) && Blocks.B[b] is not { Crop: "cantaloupe", Stage: 3 }) { PeckCrop(x + dx, top ? y - 1 : y, z + dz); return; }
			}
		}
	}
	// a crop pecked: back a stage (Corn keeps its top only while that stage has one); a crop at its first stage is eaten
	void PeckCrop(int x, int y, int z)
	{
		int id = world.GetBlock(x, y, z);
		if (!Blocks.IsAnyCrop(id) || Blocks.CropRipe(id)) return;
		bool grain = Blocks.IsCrop(id);
		int st = grain ? Array.IndexOf(Blocks.GrainStages, id) : Blocks.B[id].Stage, up = world.GetBlock(x, y + 1, z);
		bool hasTop = Blocks.Get(up) is { Top: true };
		if (st == 0) { world.SetBlock(x, y, z, 0); Sound.I?.Place(1); return; }
		world.SetBlock(x, y, z, grain ? id - 1 : Blocks.CropBlock(Blocks.B[id].Crop, st - 1));
		if (hasTop) world.SetBlock(x, y + 1, z, st - 1 >= 2 ? Blocks.CropBlock("corn", st - 1, true) : 0);
		Sound.I?.Place(1);
	}
	// a deer: remembers the sapling of the last leaves it passed, plants it now and then, and nibbles saplings
	void DeerWork(Mob m, float dt, int x, int y, int z)
	{
		var A = m.T.A;
		if ((m.LookIn -= dt) <= 0)
		{
			m.LookIn = 5;
			for (int dy = 0; dy <= 6; dy++) for (int dz = -3; dz <= 3; dz++) for (int dx = -3; dx <= 3; dx++)
				if (Blocks.Get(world.GetBlock(x + dx, y + dy, z + dz)) is { Leaves: true, Sapling: > 0 } lb) { m.Sow = lb.Sapling; dy = dz = 99; break; }
		}
		if (m.Baby > 0) return;
		if ((m.SowIn -= dt) <= 0)
		{
			m.SowIn = A.sowEvery;
			if (m.Sow > 0 && rnd.NextDouble() < A.sowChance && Blocks.PlantsLikeGrass(world.GetBlock(x, y - 1, z)) && world.GetBlock(x, y, z) == 0 && World.Inside(x, y, z)
				&& world.SkyTop[z * World.WX + x] < y && !LogNear(x, y, z, A.sowClear, null) && !SaplingNear(x, y, z, A.sowClear))
			{ world.SetBlock(x, y, z, m.Sow); world.Grown.Add(World.Idx(x, y, z)); }   // (planted by a deer: natural, like a tree a sapling grew)
		}
		if ((m.NibbleIn -= dt) <= 0)
		{
			m.NibbleIn = A.nibbleEvery;
			for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
			{
				int b = world.GetBlock(x + dx, y + dy, z + dz);
				float hx = x + dx + 0.5f - m.X, hz = z + dz + 0.5f - m.Z;
				if (Blocks.Get(b)?.Grows == null || MathF.Sqrt(hx * hx + hz * hz) > A.nibbleRange) continue;
				if (rnd.NextDouble() < A.nibbleChance) { world.SetBlock(x + dx, y + dy, z + dz, 0); Sound.I?.Place(1); m.Happy = Math.Min(A.happyMax, m.Happy + A.forage.happy); }
				return;
			}
		}
	}
	bool SaplingNear(int x, int y, int z, int r)
	{
		for (int dy = -2; dy <= 2; dy++) for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++) if (Blocks.Get(world.GetBlock(x + dx, y + dy, z + dz))?.Grows != null) return true;
		return false;
	}
	// a duck keeps to the water: in it, it turns back at the banks; ashore, it heads for the nearest water within waterRange. It
	// comes ashore only to follow someone holding its food (within followRange). Returns true while busy.
	bool DuckWater(Mob m, float dt)
	{
		var A = m.T.A; var P = player.Pos;
		bool wet = player.WaterAt(m.X, m.Y + 0.3f, m.Z);
		float pd = MathF.Sqrt((P.X - m.X) * (P.X - m.X) + (P.Z - m.Z) * (P.Z - m.Z));
		m.Ashore = false;
		if (!dead && AnimalFood["duck"].Contains(inv.Held) && pd < A.followRange)
		{
			m.Ashore = true;
			if (pd > 1.5f) { WalkToward(m, P.X, P.Z, dt); return true; }
			m.Walking = false; return true;
		}
		if (wet)
		{
			m.WaterGoal = null;
			if (m.Walking)
			{
				int fx = (int)MathF.Floor(m.X - MathF.Sin(m.Yaw) * (m.Half + 0.4f)), fz = (int)MathF.Floor(m.Z - MathF.Cos(m.Yaw) * (m.Half + 0.4f));
				if (!Blocks.IsWater(world.GetBlock(fx, (int)MathF.Floor(m.Y + 0.3f), fz))) { m.TurnTo = m.Yaw + MathF.PI * (0.6f + (float)rnd.NextDouble() * 0.8f); m.Yaw = m.TurnTo.Value; }
			}
			return false;
		}
		if (m.WaterGoal == null && (m.WaterCheck -= dt) <= 0)
		{
			m.WaterCheck = 3;
			int x = (int)MathF.Floor(m.X), y = (int)MathF.Floor(m.Y), z = (int)MathF.Floor(m.Z), R = A.waterRange, best = int.MaxValue;
			for (int dy = -3; dy <= 1; dy++) for (int dz = -R; dz <= R; dz++) for (int dx = -R; dx <= R; dx++)
			{
				int d = dx * dx + dz * dz;
				if (d < best && Blocks.IsWater(world.GetBlock(x + dx, y + dy, z + dz)) && world.GetBlock(x + dx, y + dy + 1, z + dz) == 0) { best = d; m.WaterGoal = new Vector2(x + dx + 0.5f, z + dz + 0.5f); }
			}
		}
		if (m.WaterGoal is not { } g) return false;
		WalkToward(m, g.X, g.Y, dt);
		return true;
	}
	// a grown bear: hungry huntCool after its last meal; then it looks for prey (the nearest within huntRange, and huntHeight up or
	// down), chases it for up to chaseTime and bites it once a second in reach. A kill is a meal (the prey leaves only Feathers).
	// Giving up, it tries again retry seconds later. Returns true while chasing.
	bool BearHunt(Mob m, float dt)
	{
		var A = m.T.A;
		if (m.HungerIn > 0) { m.HungerIn -= dt; m.Prey = null; return false; }
		var p = m.Prey;
		if (p != null && (p.Dead > 0 || !mobList.Contains(p) || (m.ChaseT -= dt) <= 0))
		{
			if (p.Dead > 0 && p.Eaten) { m.HungerIn = A.huntCool; m.Happy = Math.Min(A.happyMax, m.Happy + A.mealHappy); }
			else m.HuntRetry = A.retry;
			m.Prey = null;
			return false;
		}
		if (p == null)
		{
			if ((m.HuntRetry -= dt) > 0 || (m.PreyCheck -= dt) > 0) return false;
			m.PreyCheck = 3;
			m.Prey = mobList.Where(o => A.prey.Contains(o.Type) && o.Dead == 0 && D2(o, m) < A.huntRange * A.huntRange && Math.Abs(o.Y - m.Y) <= A.huntHeight).OrderBy(o => D2(o, m)).FirstOrDefault();
			m.ChaseT = A.chaseTime;
			if (m.Prey == null) return false;
		}
		var q = m.Prey; float d = MathF.Sqrt(D2(q, m));
		m.Chase = true; m.Grazing = 0;
		WalkToward(m, q.X, q.Z, dt);
		if (d < m.T.Reach && (m.BiteCool -= dt) <= 0)
		{
			m.BiteCool = 1; m.Swing = 0.35f;
			q.Eaten = true; HurtMob(q, MobDamage(m));
			if (q.Dead == 0) q.Eaten = false;
		}
		return true;
	}

	// right-click holding an animal's food, looking at it: it eats it (unless it's had all it wants). Holding a Clay Jug, looking
	// at a milky goat: a Jug of Milk.
	bool FeedAnimalClick()
	{
		if (dead) return false;
		int id = inv.Held;
		if (MobInSights() is not { } m || !m.T.Animal) return false;
		if (id == ClayJug && ClayJug > 0 && m.Type == "goat")
		{
			if (netClient) { SendFeed(m, id); inv.UseOne(); SwingHand(); return true; }   // (the host's game milks it, and sends the jug back full or empty)
			if (MilkGoat(m)) { inv.UseOne(); GiveOrDrop(JugOfMilk); SwingHand(); }
			return true;
		}
		if (!AnimalFood[m.Type].Contains(id)) return false;
		if (netClient) { SendFeed(m, id); inv.UseOne(); SwingHand(); return true; }
		if (FeedAnimal(m)) { inv.UseOne(); SwingHand(); }
		return true;
	}
	void SendFeed(Mob m, int id) => hostConn?.Send(new Dictionary<string, object> { ["t"] = "feed", ["k"] = m.K, ["id"] = id });
	void GiveOrDrop(int id) { if (!inv.Give(id)) drops.Add(id, player.Pos.X, player.Pos.Y + 1, player.Pos.Z); }
	bool MilkGoat(Mob m)
	{
		if (m.Dead > 0 || m.Baby > 0 || m.Milky <= 0) return false;
		m.Milky = 0; Sound.I?.Place(1);
		return true;
	}
	bool FeedAnimal(Mob m)
	{
		var A = m.T.A;
		if (m.Dead > 0 || (m.Fed >= A.fedMax && m.Happy > A.happyMax / 2 && m.Baby <= 0)) return false;
		m.Happy = Math.Min(A.happyMax, m.Happy + A.foodHappy);
		if (m.Baby > 0) m.Baby = Math.Max(0.01f, m.Baby - A.babyFoodBoost);
		else
		{
			m.Fed = Math.Min(A.fedMax, m.Fed + 1);
			if (A.layTime != null && m.EggIn <= 0) m.EggIn = RandIn(A.layTime);   // (chickens and ducks: an egg on its way)
			if (A.milkTime > 0) m.Milky = A.milkTime;                            // (goats: milky for a while)
			if (m.Type == "bear") { m.HungerIn = A.huntCool; m.Prey = null; }     // (a bear: a meal)
		}
		m.Grazing = 0.6f;   // (a quick munch)
		for (int k = 0; k < 3; k++) AddHeart(new Vector3(m.X, m.Y + m.Height, m.Z), k * 0.2f);
		Sound.I?.Place(1);
		return true;
	}
	// what other players' games need to show an animal: 1 while young, +4 happy, +8 eating, +16 angry
	static int AnimalLook(Mob m) => (m.Baby > 0 ? 1 : 0) | (m.Happy > 0 ? 4 : 0) | (m.Grazing > 0 ? 8 : 0) | (m.Angry > 0 ? 16 : 0);
	void ApplyAnimalLook(Mob m, int look)
	{
		SetVariant(m, look & 1);
		m.Happy = (look & 4) != 0 ? 1 : 0;
		m.Grazing = (look & 8) != 0 ? 0.2f : 0;
		m.Angry = (look & 16) != 0 ? 1 : 0;
	}
	// ---- Ducks help the field: a crop on wet tilled soil grows the duck's duckRate times as fast while a grown duck swims within
	// duckReach blocks of it across (both ways: a square) and 2 up or down. More ducks don't add up. (World asks, for soil i.) ----
	bool DuckNear(long i)
	{
		if (!MobTypes.TryGetValue("duck", out var dt) || dt.A == null) return false;
		var (x, y, z) = World.FromIdx(i); int r = dt.A.duckReach;
		return mobList.Any(m => m.Type == "duck" && m.Dead == 0 && m.Baby <= 0 && Math.Abs((int)MathF.Floor(m.X) - x) <= r && Math.Abs((int)MathF.Floor(m.Z) - z) <= r
			&& Math.Abs((int)MathF.Floor(m.Y) - y) <= 2 && player.WaterAt(m.X, m.Y + 0.3f, m.Z));
	}
}
