using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cubeland;

// Now and then, something strange: footsteps behind you, the cows all stopping to look at you, the music going a little
// wrong, or a doorway standing on its own that's gone once you look away. Never explained.
public partial class Main
{
	const float EerieEvery = 8;     // a roll every this many seconds of play...
	const int EerieOdds = 1000;     // ...that comes up 1 time in this many
	float eerieTimer = EerieEvery;

	// (test: `--eerie=N` makes thing N happen a few seconds in, 0-3)
	int EerieForce => OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--eerie=")) is { } a ? int.Parse(a[8..]) : -1;
	bool eerieForced;

	// (test: `--eerietest` makes each thing happen once and checks it)
	bool eerieTested;
	void EerieTest()
	{
		eerieTested = true;
		void Check(string what, bool ok) => GD.Print((ok ? "PASS " : "FAIL ") + what);
		player.Flying = false;
		float camWas = camSky; camSky = 0.2f;   // (as if underground)
		Check("footsteps start (alone, underground)", StartSteps() && stepsLeft > 0);
		int left = stepsLeft; for (int k = 0; k < 40; k++) TickSteps(0.05f);
		Check("  and step closer", stepsLeft < left && stepsLeft > 0);
		player.Yaw += MathF.PI; TickSteps(0.05f); player.Yaw -= MathF.PI;
		Check("  and stop when you turn round", stepsLeft == 0);
		camSky = camWas;
		var cow = SpawnMob("cow", player.Pos.X + 6, player.Pos.Y + 1, player.Pos.Z + 6);
		Check("cows stop and stare", cow != null && CowsStare() && cow.Stare > 4);
		if (cow != null) { cow.Yaw += 2; for (int k = 0; k < 60; k++) UpdateMobs(0.05f); }
		float toward = cow == null ? 0 : MathF.Atan2(cow.X - player.Pos.X, cow.Z - player.Pos.Z);
		Check("  turning to face you", cow != null && MathF.Abs(Mathf.Wrap(cow.Yaw - toward, -MathF.PI, MathF.PI)) < 0.2f && !cow.Walking);
		Check($"the music goes out of tune (music playing here: {Sound.I?.MusicPlaying})", Sound.I == null || !Sound.I.MusicPlaying || MusicWrong());
		int edits = world.Edits.Count;
		Check("a doorway stands somewhere behind you", PlaceDoor() && door.Count == 7 && door.All(c => world.GetBlock(c.x, c.y, c.z) == DoorBlock));
		Check("  not saved as one of your changes", world.Edits.Count == edits);
		var (dx, _, dz) = door[^1];
		void Face(float x, float z) { player.Yaw = MathF.Atan2(player.Pos.X - x, player.Pos.Z - z); cam.Rotation = new Vector3(0, player.Yaw, 0); cam.Position = player.EyePos; }
		TickDoor(0.1f);
		Check("  still there before you've seen it", door.Count == 7);
		var cells = door.ToList();
		Face(dx + 0.5f, dz + 0.5f); TickDoor(0.2f); TickDoor(0.2f);
		Check("  seen when you look at it, and still there", doorSeen && door.Count == 7);
		if (HasArg("--doorshot")) { doorAge = -9999; doorSeen = false; doorLook = -9999; var away = new Vector3(player.Pos.X - dx, 0, player.Pos.Z - dz).Normalized(); player.Flying = true; player.Pos = new Vector3(dx + 0.5f + away.X * 7, door.Min(c => c.y) + 0.6f, dz + 0.5f + away.Z * 7); Face(dx + 0.5f, dz + 0.5f); return; }   // (for a picture of it)
		Face(2 * player.Pos.X - dx, 2 * player.Pos.Z - dz); TickDoor(0.1f);
		Check("  gone once you look away", door.Count == 0 && cells.All(c => world.GetBlock(c.x, c.y, c.z) == 0) && world.Edits.Count == edits);
	}

	void UpdateEerie(float dt)
	{
		if (!WorldGen.EerieOn) return;   // (held back for now: see WorldGen.EerieReady)
		if (!eerieTested && HasArg("--eerietest")) EerieTest();
		TickSteps(dt); TickDoor(dt);
		if (world == null || dead) return;
		if ((eerieTimer -= dt) > 0) return;
		eerieTimer = EerieEvery;
		int force = EerieForce;
		if (force >= 0) { if (eerieForced) return; eerieForced = true; Eerie(force); return; }
		if (rnd.Next(EerieOdds) != 0) return;
		// one of them, picked at random (if it can't happen right now, another that can)
		foreach (int k in Enumerable.Range(0, 4).OrderBy(_ => rnd.Next())) if (Eerie(k)) return;
	}
	bool Eerie(int which) => which switch { 0 => StartSteps(), 1 => CowsStare(), 2 => MusicWrong(), 3 => PlaceDoor(), _ => false };
	bool Alone => !(hosting && friends.Count > 0) && !netClient;

	// ---- Footsteps behind you: alone, at night or underground. They stop if you turn around. ----
	int stepsLeft; float stepIn, stepsYaw;
	bool StartSteps()
	{
		if (!Alone || stepsLeft > 0 || !(sky.SkyDay < 0.3f || camSky < 0.35f) || player.Flying || InWaterNow) return false;
		stepsLeft = 6 + rnd.Next(5); stepIn = 0.4f; stepsYaw = player.Yaw;
		return true;
	}
	bool InWaterNow => player.InWater;
	void TickSteps(float dt)
	{
		if (stepsLeft <= 0) return;
		if (dead || MathF.Abs(Mathf.Wrap(player.Yaw - stepsYaw, -MathF.PI, MathF.PI)) > 1.3f) { stepsLeft = 0; return; }   // (you turned round: nothing there)
		if ((stepIn -= dt) > 0) return;
		stepIn = 0.52f + (float)rnd.NextDouble() * 0.08f;
		stepsLeft--;
		Sound.I?.StepBehind(player.BlockUnder() is int b && b != 0 ? b : Blocks.Dirt, 0.16f + (10 - stepsLeft) * 0.015f);   // (getting a little closer)
	}

	// ---- Every cow nearby stops and turns to look at you for a few seconds ----
	bool CowsStare()
	{
		if (netClient) return false;
		var near = mobList.Where(m => m.T.Cow && m.Dead == 0 && m.Root.Visible && (new Vector3(m.X, m.Y, m.Z) - player.Pos).Length() < 28).ToList();
		if (near.Count == 0) return false;
		foreach (var m in near) m.Stare = 5 + (float)rnd.NextDouble();
		return true;
	}

	// ---- The music plays a few notes slightly out of tune, then carries on ----
	bool MusicWrong()
	{
		if (Sound.I == null || !Sound.I.MusicPlaying) return false;
		Sound.I.Wobble(1.8f);
		return true;
	}

	// ---- A doorway standing on its own, somewhere behind you. Once you've seen it and look away, it's gone. ----
	// It's only in your game: not saved, and not sent to anyone you're playing with.
	readonly List<(int x, int y, int z)> door = new();
	bool doorSeen; float doorAge, doorLook;
	const int DoorBlock = Blocks.StrippedLog;
	bool PlaceDoor()
	{
		if (door.Count > 0) return false;
		Vector3 back = new(MathF.Sin(player.Yaw), 0, MathF.Cos(player.Yaw));   // (you look along -sin, -cos)
		for (int tries = 0; tries < 60; tries++)
		{
			float a = MathF.Atan2(back.X, back.Z) + ((float)rnd.NextDouble() - 0.5f) * 1.6f, d = 16 + (float)rnd.NextDouble() * 14;
			int x = Mathf.FloorToInt(player.Pos.X + MathF.Sin(a) * d), z = Mathf.FloorToInt(player.Pos.Z + MathF.Cos(a) * d);
			if (x < 4 || z < 4 || x >= World.WX - 4 || z >= World.WZ - 4) continue;
			// the frame runs across your line of sight: posts either side, a beam on top, an opening 1 wide and 2 tall
			bool alongX = MathF.Abs(MathF.Sin(a)) < MathF.Abs(MathF.Cos(a));
			int ax = alongX ? 1 : 0, az = alongX ? 0 : 1, g = StairGround(z * World.WX + x);
			var cells = new List<(int x, int y, int z)>(); bool ok = g > world.SeaLevel;
			for (int k = -1; k <= 1 && ok; k++)
			{
				int cx = x + ax * k, cz = z + az * k, top = world.GetBlock(cx, StairGround(cz * World.WX + cx), cz);
				if (StairGround(cz * World.WX + cx) != g || !(top == Blocks.Grass || top == Blocks.SnowyGrass || top == Blocks.Dirt || top == Blocks.SwampGrass)) { ok = false; break; }
				for (int y = g + 1; y <= g + 4; y++) if (world.GetBlock(cx, y, cz) != 0) { ok = false; break; }
				if (k != 0) { cells.Add((cx, g + 1, cz)); cells.Add((cx, g + 2, cz)); }
				cells.Add((cx, g + 3, cz));
			}
			if (!ok) continue;
			foreach (var (cx, cy, cz) in cells) world.SetLooks(cx, cy, cz, DoorBlock);
			door.AddRange(cells); doorSeen = false; doorAge = 0; doorLook = 0;
			return true;
		}
		return false;
	}
	void TickDoor(float dt)
	{
		if (door.Count == 0) return;
		doorAge += dt;
		var (x, y, z) = door[^1];   // (the middle of the beam)
		var mid = new Vector3((float)door.Average(c => c.x) + 0.5f, y - 0.5f, (float)door.Average(c => c.z) + 0.5f);
		var to = mid - cam.GlobalPosition; float dist = to.Length();
		float look = (-cam.GlobalTransform.Basis.Z).Dot(to / Math.Max(0.01f, dist));
		if (look > 0.82f && dist < 90) doorLook += dt;   // (in front of you)
		if (doorLook > 0.3f) doorSeen = true;
		// gone the moment it's out of sight after you've seen it (or, if you never turn round, after a while)
		if ((doorSeen || doorAge > 240) && look < 0.4f) ClearDoor();
	}
	void ClearDoor()
	{
		foreach (var (cx, cy, cz) in door) if (world.GetBlock(cx, cy, cz) == DoorBlock) world.SetLooks(cx, cy, cz, 0);
		door.Clear();
	}
}
