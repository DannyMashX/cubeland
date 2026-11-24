using System;
using System.Collections.Generic;
using Godot;

namespace Cubeland;

// You: a 0.6 x 1.8 box that walks, jumps, swims and flies, bumping into blocks (and the pieces inside them).
public class Player
{
	public const float Half = 0.3f, Height = 1.8f, Eye = 1.62f;
	public const float Walk = 4.3f, Sprint = 6.2f, Fly = 11f, Gravity = 28f, Jump = 8.6f;
	public Vector3 Pos, Vel;
	public float Yaw, Pitch;
	public bool OnGround, Flying, InWater;
	readonly World W;
	public Player(World w) { W = w; }

	public Vector3 EyePos => Pos + new Vector3(0, Eye, 0);
	public Vector3 Forward => new(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
	public Vector3 Right => new(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));
	public Vector3 LookDir => new(-MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), -MathF.Cos(Yaw) * MathF.Cos(Pitch));

	// ---- the solid boxes inside one block space ----
	public void SolidBoxes(int x, int y, int z, List<Aabb> into)
	{
		if (x < 0 || z < 0 || x >= World.WX || z >= World.WZ || y < 0) { into.Add(new Aabb(new Vector3(x, y, z), Vector3.One)); return; }   // the world's edge
		if (y >= World.WY) return;
		int id = W.Blocks.Get(x, y, z);
		if (id == 0) return;
		if (Blocks.IsPieces(id))
		{
			if (!W.PieceMap.TryGetValue(World.Idx(x, y, z), out var a)) return;
			for (int s = 0; s < 8; s++)
				if (a[s] != 0 && !Blocks.B[World.SpotBlock(a[s])].NoCollide)
					into.Add(new Aabb(new Vector3(x + (s & 1) * 0.5f, y + ((s >> 1) & 1) * 0.5f, z + (s >> 2) * 0.5f), new Vector3(0.5f, 0.5f, 0.5f)));
			return;
		}
		if (Blocks.B[id].Shape == "half") { into.Add(new Aabb(new Vector3(x, y, z), new Vector3(1, 0.5f, 1))); return; }   // a half-tall block: just its bottom half
		if (Blocks.B[id].Shape == "door") { var bx = Blocks.B[id].Box; into.Add(new Aabb(new Vector3(x + bx[0], y + bx[1], z + bx[2]), new Vector3(bx[3] - bx[0], bx[4] - bx[1], bx[5] - bx[2]))); return; }   // a door: just its panel
		if (Blocks.B[id].Shape == "snow") { if (Blocks.B[id].Layers > 1) into.Add(new Aabb(new Vector3(x, y, z), new Vector3(1, (Blocks.B[id].Layers - 1) / 8f, 1))); return; }   // snow: you sink in one layer
		if (Blocks.B[id].NoCollide) return;
		into.Add(new Aabb(new Vector3(x, y, z), Vector3.One));
	}
	readonly List<Aabb> scratch = new();
	List<Aabb> Overlapping(Vector3 p)
	{
		scratch.Clear();
		var min = new Vector3(p.X - Half, p.Y, p.Z - Half); var max = new Vector3(p.X + Half, p.Y + Height, p.Z + Half);
		var found = new List<Aabb>();
		for (int y = (int)MathF.Floor(min.Y); y <= (int)MathF.Floor(max.Y - 1e-5f); y++)
			for (int z = (int)MathF.Floor(min.Z); z <= (int)MathF.Floor(max.Z - 1e-5f); z++)
				for (int x = (int)MathF.Floor(min.X); x <= (int)MathF.Floor(max.X - 1e-5f); x++)
					SolidBoxes(x, y, z, scratch);
		foreach (var b in scratch)
		{
			var e = b.End;
			if (b.Position.X < max.X && e.X > min.X && b.Position.Y < max.Y && e.Y > min.Y && b.Position.Z < max.Z && e.Z > min.Z) found.Add(b);
		}
		return found;
	}
	public bool Fits(Vector3 p) => Overlapping(p).Count == 0;
	// would you be standing inside the block space (x, y, z)?
	public bool Touches(int x, int y, int z) => TouchesBox(x, y, z, 1);
	public bool TouchesBox(float x, float y, float z, float s) =>
		x + s > Pos.X - Half && x < Pos.X + Half && y + s > Pos.Y && y < Pos.Y + Height && z + s > Pos.Z - Half && z < Pos.Z + Half;
	// false if the spots in `mask` of space (x, y, z) would be inside you
	public bool ShapeFits(int x, int y, int z, int mask)
	{
		for (int s = 0; s < 8; s++)
			if ((mask & (1 << s)) != 0 && TouchesBox(x + (s & 1) * 0.5f, y + ((s >> 1) & 1) * 0.5f, z + (s >> 2) * 0.5f, 0.5f)) return false;
		return true;
	}

	// move along one axis, stopping flush against whatever is in the way
	bool hitWall; float impact;
	void MoveAxis(int axis, float d)
	{
		if (d == 0) return;
		var p = Pos; p[axis] += d;
		var hits = Overlapping(p);
		if (hits.Count == 0) { Pos = p; return; }
		if (axis == 1)
		{
			if (d < 0) { float top = float.MinValue; foreach (var b in hits) top = MathF.Max(top, b.End.Y); p.Y = top; OnGround = true; impact = -Vel.Y; }
			else { float bot = float.MaxValue; foreach (var b in hits) bot = MathF.Min(bot, b.Position.Y); p.Y = bot - Height - 0.001f; }
			Pos = p; Vel.Y = 0;
			return;
		}
		// walking into something only half a block high made of pieces (slabs, stairs, quarters, loose pieces): step up onto
		// it, like a stair. Nothing else: not a whole block (even from on top of a slab), the Cutter, or snow.
		float t = float.MinValue; bool pieces = true;
		foreach (var b in hits) { t = MathF.Max(t, b.End.Y); if (b.Size.X != 0.5f || b.Size.Y != 0.5f || b.Size.Z != 0.5f) pieces = false; }
		if (!Flying && pieces && t > Pos.Y && t - Pos.Y <= 0.5f + 1e-6f && !Fits(Pos - new Vector3(0, 0.05f, 0)))
		{
			var up = p; up.Y = t;
			if (Fits(up)) { Pos = up; Vel.Y = MathF.Max(0, Vel.Y); return; }
		}
		if (d > 0) { float edge = float.MaxValue; foreach (var b in hits) edge = MathF.Min(edge, b.Position[axis]); p[axis] = edge - Half - 0.001f; }
		else { float edge = float.MinValue; foreach (var b in hits) edge = MathF.Max(edge, b.End[axis]); p[axis] = edge + Half + 0.001f; }
		Pos = p;
		if (axis == 0) Vel.X = 0; else Vel.Z = 0;
		hitWall = true;
	}
	// water at a point in the world (counting how high the surface is in that block)
	public bool WaterAt(float x, float y, float z)
	{
		int fx = (int)MathF.Floor(x), fy = (int)MathF.Floor(y), fz = (int)MathF.Floor(z), b = W.GetBlock(fx, fy, fz);
		return Blocks.IsWater(b) && y - fy < Blocks.WaterHeight(b, W.GetBlock(fx, fy + 1, fz));
	}

	// what happened this frame, for sounds and fall damage
	public struct Events { public bool Jumped, Splashed, Stroke, Step, Landed; public float Impact, FallSpeed; }
	float fallSpeed, stepDistance, swimDistance; bool wasSwimming;
	public struct Keys { public bool F, B, L, R, Jump, Down, Sprint; }
	public Events Update(float dt, Keys k)
	{
		var ev = new Events();
		// swimming: in water when your legs or waist are under the surface
		InWater = !Flying && (WaterAt(Pos.X, Pos.Y + 0.2f, Pos.Z) || WaterAt(Pos.X, Pos.Y + 0.9f, Pos.Z));
		var wish = Vector3.Zero;
		if (k.F) wish += Forward; if (k.B) wish -= Forward; if (k.R) wish += Right; if (k.L) wish -= Right;
		if (wish.LengthSquared() > 0) wish = wish.Normalized();
		float speed = Flying ? Fly : (k.Sprint ? Sprint : Walk) * (InWater ? 0.55f : 1);
		float a = MathF.Min(1, dt * (OnGround || Flying ? 14 : InWater ? 6 : 4));
		Vel.X += (wish.X * speed - Vel.X) * a;
		Vel.Z += (wish.Z * speed - Vel.Z) * a;
		if (Flying) Vel.Y += (((k.Jump ? 1 : 0) - (k.Down ? 1 : 0)) * 8 - Vel.Y) * MathF.Min(1, dt * 10);
		else if (InWater)
		{
			// water slows your fall and lets you sink gently; hold Space to swim up, Shift to dive
			Vel.Y += ((k.Jump ? 3.4f : k.Down ? -3.4f : -1.6f) - Vel.Y) * MathF.Min(1, dt * 4);
			if (k.Jump && OnGround) Vel.Y = 5;
			// swimming up against the edge of a pool climbs you out of it
			if (k.Jump && hitWall && !WaterAt(Pos.X, Pos.Y + 1.3f, Pos.Z)) Vel.Y = MathF.Max(Vel.Y, 6.5f);
		}
		else
		{
			Vel.Y = MathF.Max(-40, Vel.Y - Gravity * dt);
			if (k.Jump && OnGround) { Vel.Y = Jump; ev.Jumped = true; }
		}
		// physics with substeps so we never skip through a block
		bool wasOnGround = OnGround; var prev = Pos;
		OnGround = false; hitWall = false; impact = 0;
		float maxv = MathF.Max(MathF.Abs(Vel.X), MathF.Max(MathF.Abs(Vel.Y), MathF.Abs(Vel.Z)));
		int n = Math.Max(1, (int)MathF.Ceiling(maxv * dt / 0.4f));
		float h = dt / n;
		for (int i = 0; i < n; i++) { MoveAxis(0, Vel.X * h); MoveAxis(2, Vel.Z * h); MoveAxis(1, Vel.Y * h); }
		if (OnGround && Flying) Flying = false;
		// a splash when you fall in, a stroke every so often as you swim, footsteps, landing
		if (InWater && !wasSwimming && fallSpeed > 3) { ev.Splashed = true; ev.FallSpeed = fallSpeed; }
		wasSwimming = InWater;
		fallSpeed = Vel.Y < 0 ? -Vel.Y : 0;
		if (InWater && !OnGround) { swimDistance += (Pos - prev).Length(); if (swimDistance > 2.2f) { swimDistance = 0; ev.Stroke = true; } }
		if (OnGround && !Flying && !InWater)
		{
			if (!wasOnGround) { ev.Landed = true; ev.Impact = impact; }
			stepDistance += new Vector2(Pos.X - prev.X, Pos.Z - prev.Z).Length();
			if (stepDistance > 1.7f) { stepDistance = 0; ev.Step = true; }
		}
		return ev;
	}
	// the block you're standing on (checks the corners too, for when you're on an edge), or a piece
	public int BlockUnder()
	{
		// feet in a layer of snow (or in a block with snow round it): you're walking in the snow
		int feet = W.GetBlock((int)MathF.Floor(Pos.X), (int)MathF.Floor(Pos.Y + 0.01f), (int)MathF.Floor(Pos.Z));
		if (Blocks.IsSnow(feet) || Blocks.SnowIn(feet) > 0) return Blocks.Snow;
		foreach (var (dx, dz) in new[] { (0f, 0f), (-Half, -Half), (Half, -Half), (-Half, Half), (Half, Half) })
		{
			int b = W.SubBlock((int)MathF.Floor((Pos.X + dx) * 2), (int)MathF.Floor((Pos.Y - 0.05f) * 2), (int)MathF.Floor((Pos.Z + dz) * 2));
			if (b != 0) return b;
		}
		return 1;
	}

	// ---- what you're looking at (up to 6 blocks away) ----
	public struct Hit { public int X, Y, Z, Nx, Ny, Nz, Sub, Id; public Vector3 Pt; public int this[int k] => k == 0 ? Nx : k == 1 ? Ny : Nz; }
	public Hit? Raycast(float maxDist = 6)
	{
		var o = EyePos; var d = LookDir;
		int x = (int)MathF.Floor(o.X), y = (int)MathF.Floor(o.Y), z = (int)MathF.Floor(o.Z);
		int sx = MathF.Sign(d.X), sy = MathF.Sign(d.Y), sz = MathF.Sign(d.Z);
		float tdx = sx != 0 ? MathF.Abs(1 / d.X) : float.MaxValue, tdy = sy != 0 ? MathF.Abs(1 / d.Y) : float.MaxValue, tdz = sz != 0 ? MathF.Abs(1 / d.Z) : float.MaxValue;
		float tmx = sx > 0 ? (x + 1 - o.X) * tdx : sx < 0 ? (o.X - x) * tdx : float.MaxValue;
		float tmy = sy > 0 ? (y + 1 - o.Y) * tdy : sy < 0 ? (o.Y - y) * tdy : float.MaxValue;
		float tmz = sz > 0 ? (z + 1 - o.Z) * tdz : sz < 0 ? (o.Z - z) * tdz : float.MaxValue;
		for (int step = 0; step < 64; step++)
		{
			var h = HitInCell(x, y, z, o, d, maxDist);
			if (h != null) return h;
			if (tmx < tmy && tmx < tmz) { if (tmx > maxDist) break; x += sx; tmx += tdx; }
			else if (tmy < tmz) { if (tmy > maxDist) break; y += sy; tmy += tdy; }
			else { if (tmz > maxDist) break; z += sz; tmz += tdz; }
		}
		return null;
	}
	Hit? HitInCell(int x, int y, int z, Vector3 o, Vector3 d, float maxDist)
	{
		if (!World.Inside(x, y, z)) return null;
		int id = W.Blocks.Get(x, y, z);
		if (id == 0 || Blocks.B[id].Liquid) return null;
		var b = Blocks.B[id];
		Hit? best = null; float bestT = maxDist;
		void Try(float x0, float y0, float z0, float x1, float y1, float z1, int sub)
		{
			if (RayBox(o, d, new Vector3(x + x0, y + y0, z + z0), new Vector3(x + x1, y + y1, z + z1), out float t, out int axis) && t <= bestT)
			{
				bestT = t;
				int nx = 0, ny = 0, nz = 0;
				if (axis == 0) nx = d.X > 0 ? -1 : 1; else if (axis == 1) ny = d.Y > 0 ? -1 : 1; else nz = d.Z > 0 ? -1 : 1;
				best = new Hit { X = x, Y = y, Z = z, Nx = nx, Ny = ny, Nz = nz, Sub = sub, Id = id, Pt = o + d * t };
			}
		}
		if (Blocks.IsPieces(id))
		{
			if (W.PieceMap.TryGetValue(World.Idx(x, y, z), out var a))
				for (int s = 0; s < 8; s++)
					if (a[s] != 0) { float px = (s & 1) * 0.5f, py = ((s >> 1) & 1) * 0.5f, pz = (s >> 2) * 0.5f; Try(px, py, pz, px + 0.5f, py + 0.5f, pz + 0.5f, s); }
		}
		else if (b.Shape == "torch") { var bx = ChunkMesher.TorchGeo(id).Box; Try(bx[0], bx[1], bx[2], bx[3], bx[4], bx[5], -1); }
		else if (b.Box != null) Try(b.Box[0], b.Box[1], b.Box[2], b.Box[3], b.Box[4], b.Box[5], -1);
		else Try(0, 0, 0, 1, 1, 1, -1);
		if (b.Snowed > 0 && b.Shape is "torch" or "plant" or "flat") Try(0, 0, 0, 1, 1 / 8f, 1, -1);   // (snow-logged: its snow can be aimed at too)
		return best;
	}
	static bool RayBox(Vector3 o, Vector3 d, Vector3 min, Vector3 max, out float t, out int axis)
	{
		float tn = float.MinValue, tf = float.MaxValue; axis = 0; t = 0;
		for (int a = 0; a < 3; a++)
		{
			if (MathF.Abs(d[a]) < 1e-9f) { if (o[a] < min[a] || o[a] > max[a]) return false; continue; }
			float t1 = (min[a] - o[a]) / d[a], t2 = (max[a] - o[a]) / d[a];
			if (t1 > t2) (t1, t2) = (t2, t1);
			if (t1 > tn) { tn = t1; axis = a; }
			tf = MathF.Min(tf, t2);
			if (tn > tf) return false;
		}
		if (tf < 0) return false;
		t = MathF.Max(0, tn);
		return true;
	}
}
