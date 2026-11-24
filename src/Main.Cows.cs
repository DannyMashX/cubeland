using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Cubeland;

// ===================== Cows =====================
// Cows live in herds on grassy ground and are saved with the world (they stay put when you walk away).
// - Grazing: a cow that isn't very happy now and then stops, lowers its head and eats the Grass it's standing on. The spot
//   turns to Dirt and grows back to Grass a few minutes later. Grazing makes it happy.
// - Grain: right-click a cow holding Grain to feed it. That makes it happy too, and each Grain fattens it a little:
//   after FatAt of them it's a big round cow, and a fatter cow gives more Raw Meat. (A calf fed Grain grows up sooner.)
// - Happy cows show pink hearts. Being happy doesn't make a cow mate, but a happy grown cow *may* go looking for another
//   happy one nearby; when they meet, a calf is born. Not if it's crowded: a cow with CrowdedAt or more cows close by
//   won't mate, however happy it is.
// - Breeding stops at CowsPerChunk cows in one 16x16 chunk (all the way up), counting calves, fat cows and the rest:
//   no cow there mates and no calf is born. It only stops breeding: cows can still spawn or wander in past it.
// - Leather: a cow that's had room to roam (few cows close by, for a while) gives more Leather.
// The web game's index.html has the same rules (the COW numbers there must match these).
public partial class Main
{
	public const int CowsPerChunk = 8, CrowdedAt = 4, FatAt = 3, FedMax = 5;
	const float CowHappyMax = 180, GrazeHappy = 45, GrainHappy = 90, GrazeBelow = 60, GrazeTime = 2;
	const float MateCooldown = 300, MateCost = 60, CalfTime = 600, CalfGrainBoost = 60, NearRange = 6;
	const int CalfHealth = 3;

	// which model a cow wears: 0 = cow, 1 = calf (cowbaby), 2 = fat cow (cowfat)
	static int CowVariant(Mob m) => m.Baby > 0 ? 1 : m.Fed >= FatAt ? 2 : 0;
	static string ModelOf(Mob m) => !m.T.Cow ? m.Type : m.Variant == 1 ? "cowbaby" : m.Variant == 2 ? "cowfat" : "cow";
	// a cow grew up or got fat: it's built again in its new shape
	void RebuildMob(Mob m)
	{
		var at = m.Root.Position; var rot = m.Root.Rotation;
		m.Root.QueueFree(); m.Legs.Clear(); m.Arms.Clear(); m.Mats.Clear(); m.Head = null; m.Figure = null; m.HeldMesh = null; m.HeldId = 0;
		BuildMob(m);
		m.Root.Position = at; m.Root.Rotation = rot;
	}
	void SetVariant(Mob m, int v) { if (m.Variant == v) return; m.Variant = v; RebuildMob(m); }

	static (int, int) ChunkOf(float x, float z) => ((int)MathF.Floor(x / World.CS), (int)MathF.Floor(z / World.CS));
	int CowsInChunk((int, int) c, Mob skip = null) => mobList.Count(o => o != skip && o.T.Cow && o.Dead == 0 && ChunkOf(o.X, o.Z) == c);
	int CowsNear(Mob m, float r) => mobList.Count(o => o != m && o.T.Cow && o.Dead == 0 && (o.X - m.X) * (o.X - m.X) + (o.Z - m.Z) * (o.Z - m.Z) < r * r && Math.Abs(o.Y - m.Y) < 4);

	Mob SpawnCow(float x, float y, float z, float baby = 0)
	{
		var m = SpawnMob("cow", x, y, z, baby > 0 ? 1 : 0);
		m.Baby = baby; if (baby > 0) m.Health = CalfHealth;
		m.Room = -1;
		return m;
	}

	// a small herd now and then on grass a little way off, while there are few cows about (cows are never forgotten,
	// so herds you leave behind are still there when you come back)
	float cowSpawnIn = 5;
	void SpawnCows()
	{
		if ((cowSpawnIn -= 3) > 0) return;
		cowSpawnIn = 20 + (float)rnd.NextDouble() * 40;
		var P = player.Pos;
		if (mobList.Count(m => m.T.Cow && (m.X - P.X) * (m.X - P.X) + (m.Z - P.Z) * (m.Z - P.Z) < 80 * 80) >= 6) return;
		bool none = !mobList.Any(m => m.T.Cow);
		if (FindMobSpot(none ? 12 : 30, none ? 32 : 60) is not { } s) return;
		int n = 2 + rnd.Next(3);
		for (int k = 0; k < n; k++)
		{
			float x = s.X + (float)(rnd.NextDouble() - 0.5) * 5, z = s.Z + (float)(rnd.NextDouble() - 0.5) * 5;
			if (StandSpot(x, s.Y, z) is not { } y) continue;
			SpawnCow(x, y, z, k > 0 && rnd.NextDouble() < 0.25 ? CalfTime * (0.3f + (float)rnd.NextDouble() * 0.7f) : 0);
		}
	}
	// the top of the ground at (x, z) within a couple of blocks of y, with room for a cow to stand
	float? StandSpot(float x, float y, float z)
	{
		int bx = (int)MathF.Floor(x), bz = (int)MathF.Floor(z);
		for (int by = (int)MathF.Floor(y) + 2; by >= (int)MathF.Floor(y) - 3; by--)
			if (world.Solid(bx, by - 1, bz) && !Blocks.IsWater(world.GetBlock(bx, by - 1, bz)) && !world.Solid(bx, by, bz) && !world.Solid(bx, by + 1, bz)) return by;
		return null;
	}

	// a cow's own thinking each frame (the host's game). Returns true while it's busy (grazing, going to a mate, a calf
	// trotting after a grown cow), so the usual wandering waits.
	bool CowThink(Mob m, float dt)
	{
		m.Happy = Math.Max(0, m.Happy - dt);
		m.MateCool = Math.Max(0, m.MateCool - dt);
		if (m.Baby > 0 && (m.Baby -= dt) <= 0) { m.Baby = 0; m.Health = m.T.Health; SetVariant(m, CowVariant(m)); }
		// how much room it has: how many cows are close by, eased in slowly (Leather)
		if ((m.RoomIn -= dt) <= 0)
		{
			m.RoomIn = 2;
			m.Near = CowsNear(m, NearRange);
			float want = Math.Clamp(1 - m.Near / (float)CrowdedAt, 0, 1);
			m.Room = m.Room < 0 ? want : m.Room + (want - m.Room) * 0.05f;
		}
		// grazing: head down for a moment, then the grass is eaten
		if (m.Grazing > 0)
		{
			m.Walking = false;
			if ((m.Grazing -= dt) <= 0)
			{
				int x = (int)MathF.Floor(m.X), y = (int)MathF.Floor(m.Y) - 1, z = (int)MathF.Floor(m.Z);
				if (world.GetBlock(x, y, z) == Blocks.Grass) { Graze(x, y, z); m.Happy = Math.Min(CowHappyMax, m.Happy + GrazeHappy); }
				m.Timer = 1 + (float)rnd.NextDouble() * 2;
			}
			return true;
		}
		// looking for a mate (grown, happy, not crowded, with room in the chunk for a calf)
		if (m.Mate is { } mate)
		{
			if (mate.Dead > 0 || mate.Mate != m || (m.MateFor -= dt) <= 0 || !mobList.Contains(mate)) { m.Mate = null; if (mate.Mate == m) mate.Mate = null; return false; }
			float d = MathF.Sqrt((mate.X - m.X) * (mate.X - m.X) + (mate.Z - m.Z) * (mate.Z - m.Z));
			if (d < 1.6f) { Calve(m, mate); return false; }
			WalkToward(m, mate.X, mate.Z, dt);
			return true;
		}
		if (m.Baby == 0 && m.Happy > 0 && m.MateCool <= 0 && (m.MateCheck -= dt) <= 0)
		{
			m.MateCheck = 3 + (float)rnd.NextDouble() * 3;
			if (CanMate(m) && rnd.NextDouble() < 0.5)
			{
				var other = mobList.Where(o => o != m && o.T.Cow && o.Mate == null && (o.X - m.X) * (o.X - m.X) + (o.Z - m.Z) * (o.Z - m.Z) < 64 && CanMate(o))
					.OrderBy(o => (o.X - m.X) * (o.X - m.X) + (o.Z - m.Z) * (o.Z - m.Z)).FirstOrDefault();
				if (other != null) { m.Mate = other; other.Mate = m; m.MateFor = other.MateFor = 20; m.Grazing = other.Grazing = 0; return true; }
			}
		}
		// a calf keeps near a grown cow
		if (m.Baby > 0)
		{
			var mum = mobList.Where(o => o.T.Cow && o.Baby == 0 && o.Dead == 0 && (o.X - m.X) * (o.X - m.X) + (o.Z - m.Z) * (o.Z - m.Z) < 16 * 16)
				.OrderBy(o => (o.X - m.X) * (o.X - m.X) + (o.Z - m.Z) * (o.Z - m.Z)).FirstOrDefault();
			if (mum != null && (mum.X - m.X) * (mum.X - m.X) + (mum.Z - m.Z) * (mum.Z - m.Z) > 16) { WalkToward(m, mum.X, mum.Z, dt); return true; }
		}
		// standing about and not very happy: graze (if it's on grass)
		if (!m.Walking && m.Happy < GrazeBelow && (m.GrazeIn -= dt) <= 0)
		{
			m.GrazeIn = 5 + (float)rnd.NextDouble() * 10;
			if (world.GetBlock((int)MathF.Floor(m.X), (int)MathF.Floor(m.Y) - 1, (int)MathF.Floor(m.Z)) == Blocks.Grass) { m.Grazing = GrazeTime; return true; }
		}
		return false;
	}
	bool CanMate(Mob m) => m.Dead == 0 && m.Baby == 0 && m.Happy > 0 && m.MateCool <= 0 && m.Flee <= 0 && CowsNear(m, NearRange) < CrowdedAt
		&& CowsInChunk(ChunkOf(m.X, m.Z)) < CowsPerChunk;
	void WalkToward(Mob m, float x, float z, float dt)
	{
		m.Walking = true;
		float toward = MathF.Atan2(m.X - x, m.Z - z);
		m.Yaw += Turn(m.Yaw, toward) * Math.Min(1, dt * 4); m.TurnTo = m.Yaw;
	}
	// two cows met: a calf between them (if the chunk has room), and neither wants to again for a while
	void Calve(Mob a, Mob b)
	{
		a.Mate = b.Mate = null;
		a.MateCool = b.MateCool = MateCooldown;
		a.Happy = Math.Max(0, a.Happy - MateCost); b.Happy = Math.Max(0, b.Happy - MateCost);
		float x = (a.X + b.X) / 2, z = (a.Z + b.Z) / 2;
		if (CowsInChunk(ChunkOf(x, z)) >= CowsPerChunk) return;   // (the chunk has as many cows as it can breed to)
		if (StandSpot(x, Math.Max(a.Y, b.Y), z) is not { } y) return;
		var calf = SpawnCow(x, y, z, CalfTime);
		calf.Room = (Math.Max(0, a.Room) + Math.Max(0, b.Room)) / 2;
		for (int k = 0; k < 4; k++) AddHeart(new Vector3(x, y + 1, z), k * 0.15f);
	}

	// eaten grass: Dirt for now, back to Grass in a few minutes (World.Grazed counts it down)
	void Graze(int x, int y, int z)
	{
		world.SetBlock(x, y, z, Blocks.Dirt);
		world.Grazed[World.Idx(x, y, z)] = World.RegrowMin + (float)rnd.NextDouble() * (World.RegrowMax - World.RegrowMin);
		Sound.I?.Place(Blocks.Grass);
	}

	// right-click a cow holding Grain: it eats it (unless it's had all it wants)
	bool FeedCow()
	{
		if (dead || inv.Held != Blocks.Grain || MobInSights() is not { } m || !m.T.Cow) return false;
		if (netClient)
		{   // a friend's game: their game feeds it (and gives the Grain back if the cow didn't want it)
			hostConn?.Send(new Dictionary<string, object> { ["t"] = "feed", ["k"] = m.K });
			inv.UseOne(); SwingHand();
			return true;
		}
		if (!Feed(m)) return true;   // (it doesn't want it: nothing else happens either)
		inv.UseOne(); SwingHand();
		return true;
	}
	bool Feed(Mob m)
	{
		if (m.Dead > 0 || (m.Fed >= FedMax && m.Happy > CowHappyMax / 2 && m.Baby == 0)) return false;
		m.Happy = Math.Min(CowHappyMax, m.Happy + GrainHappy);
		if (m.Baby > 0) m.Baby = Math.Max(0.01f, m.Baby - CalfGrainBoost);
		else m.Fed = Math.Min(FedMax, m.Fed + 1);
		m.Grazing = 0.6f;   // (a quick munch)
		SetVariant(m, CowVariant(m));
		for (int k = 0; k < 3; k++) AddHeart(new Vector3(m.X, m.Y + m.Height, m.Z), k * 0.2f);
		Sound.I?.Place(Blocks.Grass);
		return true;
	}

	// what a cow leaves behind: more Raw Meat the more it was fed, more Leather the more room it had (a calf: nothing)
	void CowDrops(Mob m)
	{
		if (m.Baby > 0) return;
		int meat = 1 + m.Fed, leather = (int)MathF.Round(Math.Max(0, m.Room) * 3);
		for (int n = 0; n < meat; n++) drops.Add(Blocks.RawMeat, m.X, m.Y + 0.5f, m.Z);
		for (int n = 0; n < leather; n++) drops.Add(Blocks.Leather, m.X, m.Y + 0.5f, m.Z);
	}

	// what other players' games need to show a cow: its shape (0-2), +4 while happy, +8 while grazing
	static int CowLook(Mob m) => m.Variant | (m.Happy > 0 ? 4 : 0) | (m.Grazing > 0 ? 8 : 0);
	void ApplyCowLook(Mob m, int look)
	{
		SetVariant(m, look & 3);
		m.Happy = (look & 4) != 0 ? 1 : 0;
		m.Grazing = (look & 8) != 0 ? 0.2f : 0;
	}

	// every frame, after its body is posed: head down while grazing, hearts while happy
	void CowShow(Mob m, float dt)
	{
		m.GrazeAnim += ((m.Grazing > 0 ? 1 : 0) - m.GrazeAnim) * Math.Min(1, dt * 8);
		if (m.Head != null && m.GrazeAnim > 0.01f)
			m.Head.Rotation = new Vector3(-0.85f * m.GrazeAnim + MathF.Sin(m.Anim * 14) * 0.05f * m.GrazeAnim, 0, 0);
		if (m.Happy > 0 && m.Dead == 0 && (m.HeartIn -= dt) <= 0)
		{
			m.HeartIn = 1.2f + (float)rnd.NextDouble() * 1.5f;
			AddHeart(new Vector3(m.X + (float)(rnd.NextDouble() - 0.5) * 0.5f, m.Y + m.Height + 0.1f, m.Z + (float)(rnd.NextDouble() - 0.5) * 0.5f));
		}
	}

	// ---- pink hearts (the pink row, FF00FF, of textures/gui/heart_atlas.png): float up and fade ----
	// Each is lit by the light where it is (like mobs) and fades into the haze, so they don't glow at night or in caves.
	class Heart { public MeshInstance3D S; public ShaderMaterial Mat; public float Age, Delay, LightTimer; public Vector3 At; }
	readonly List<Heart> hearts = new();
	static Texture2D heartAtlas; static Vector4 heartRegion;
	static Shader spriteShader;
	const float HeartLife = 1.3f;
	// the light on a spot: daylight (as much sky as it can see) or torchlight, whichever is brighter, and your held light
	Vector3 LightOnSpot(Vector3 p)
	{
		int bx = (int)MathF.Floor(p.X), by = (int)MathF.Floor(p.Y), bz = (int)MathF.Floor(p.Z);
		float skyx = world.SkyExposure(bx, by, bz, null), torch = World.LightCurve(world.LightAt(bx, by, bz));
		var d = sky.DayTint; var tc = new Vector3(1, 0.82f, 0.58f) * Math.Max(torch, HeldLightAt(p));
		return new Vector3(Math.Max(d.X * skyx, tc.X), Math.Max(d.Y * skyx, tc.Y), Math.Max(d.Z * skyx, tc.Z));
	}
	// a flat square that always faces you, lit and hazed like everything else (tex null: a plain square of tint)
	(MeshInstance3D, ShaderMaterial) Sprite(Texture2D tex, Vector4 region, Vector3 tint, float size, Vector3 at)
	{
		spriteShader ??= GD.Load<Shader>("res://src/sprite.gdshader");
		var mat = new ShaderMaterial { Shader = spriteShader };
		if (tex != null) { mat.SetShaderParameter("tex", tex); mat.SetShaderParameter("region", region); } else mat.SetShaderParameter("use_tex", false);
		mat.SetShaderParameter("tint", tint); mat.SetShaderParameter("light", LightOnSpot(at));
		var mi = new MeshInstance3D { Mesh = new QuadMesh { Size = Vector2.One * size }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Position = at };
		mobRoot.AddChild(mi);
		return (mi, mat);
	}
	void AddHeart(Vector3 at, float delay = 0)
	{
		if (heartAtlas == null)
		{
			heartAtlas = ResourceLoader.Load<Texture2D>("res://textures/gui/heart_atlas.png");
			if (heartAtlas == null) return;
			float hp = heartAtlas.GetWidth() / 9.9f, w = heartAtlas.GetWidth(), h = heartAtlas.GetHeight();
			heartRegion = new Vector4(0, MathF.Round(hp * 8) / h, MathF.Round(hp * 0.9f) / w, MathF.Round(hp * 0.9f) / h);   // (pink row, full heart)
		}
		var (s, mat) = Sprite(heartAtlas, heartRegion, Vector3.One, 0.28f, at);
		s.Visible = delay <= 0;
		hearts.Add(new Heart { S = s, Mat = mat, Delay = delay, At = at });
	}
	void UpdateHearts(float dt)
	{
		foreach (var h in hearts.ToArray())
		{
			if (h.Delay > 0) { h.Delay -= dt; continue; }
			h.S.Visible = true;
			h.Age += dt;
			float f = h.Age / HeartLife;
			if (f >= 1) { h.S.QueueFree(); hearts.Remove(h); continue; }
			h.S.Position = h.At + new Vector3(MathF.Sin(h.Age * 5) * 0.05f, f * 0.7f, 0);
			h.Mat.SetShaderParameter("alpha", f < 0.7f ? 1 : 1 - (f - 0.7f) / 0.3f);
			if ((h.LightTimer -= dt) <= 0) { h.LightTimer = 0.2f; h.Mat.SetShaderParameter("light", LightOnSpot(h.S.Position)); }
		}
	}
}
