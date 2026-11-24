using System;
using Godot;

namespace Cubeland;

// Doors (one for each wood, data/blocks.json's DOORS): 2 blocks tall, a 4-pixel panel across the middle of its block. Its
// front faces you when you put it down, and its hinge goes on the side of the face you clicked. Right-click opens or shuts it:
// open, it lies against its hinge side. It swings away from you: a shut door opened from behind is turned round first
// (facing + 2, the hinge the other side, which looks the same shut). A door beside it on its other side with the other hinge
// (a double door) goes with it. Breaking either half takes the whole door. Mobs can't open them. (The same as the web version.)
public partial class Main
{
	// put down a door (item: the door you're holding) where a block would go, if there's room for both halves
	void PlaceDoor(Player.Hit h, int item)
	{
		int x = h.X + h.Nx, y = h.Y + h.Ny, z = h.Z + h.Nz;
		if (!World.Inside(x, y, z) || !World.Inside(x, y + 1, z)) return;
		if (world.GetBlock(x, y, z) != 0 || world.GetBlock(x, y + 1, z) != 0) return;
		if (player.Touches(x, y, z) || player.Touches(x, y + 1, z)) return;
		// its front faces you (the Kiln's rule), and its hinge is on the side of the face you clicked, as you look at its front
		float dx = player.Pos.X - (x + 0.5f), dz = player.Pos.Z - (z + 0.5f);
		int facing = MathF.Abs(dx) > MathF.Abs(dz) ? (dx > 0 ? 1 : 3) : (dz >= 0 ? 0 : 2);
		var (rx, rz) = Blocks.DoorRight(facing);
		int hinge = (h.Pt.X - (x + 0.5f)) * rx + (h.Pt.Z - (z + 0.5f)) * rz < 0 ? 0 : 1;
		int bottom = Blocks.DoorState(item, facing, hinge, false, 0);
		world.SetBlock(x, y, z, bottom); world.SetBlock(x, y + 1, z, Blocks.DoorState(item, facing, hinge, false, 1));
		lastAim = new Player.Hit { X = x, Y = y, Z = z, Sub = -1, Id = bottom };
		Placed(bottom);
	}

	// right-click a door: open it or shut it (with the other half of a double door)
	void ToggleDoor(int x, int y, int z)
	{
		int by = Blocks.B[world.GetBlock(x, y, z)].Half == 0 ? y : y - 1;   // (its bottom half)
		int id = world.GetBlock(x, by, z);
		if (!Blocks.IsDoor(id)) return;
		var b = Blocks.B[id];
		var (fx, fz) = Blocks.DoorFront(b.Facing); var (rx, rz) = Blocks.DoorRight(b.Facing);
		// the other half of a double door: beside it on its other side (away from its hinge), facing the same way, with the other
		// hinge, just as open (looked for before either changes)
		int nx = x + (b.Hinge == 0 ? rx : -rx), nz = z + (b.Hinge == 0 ? rz : -rz);
		int n = World.Inside(nx, by, nz) ? world.GetBlock(nx, by, nz) : 0;
		bool pair = Blocks.IsDoor(n) && Blocks.B[n] is { Half: 0 } nb && nb.Facing == b.Facing && nb.Hinge != b.Hinge && nb.Open == b.Open;
		// shut and opened from behind: turned round first, so it swings away from you
		bool turn = b.Open == 0 && (player.Pos.X - (x + 0.5f)) * fx + (player.Pos.Z - (z + 0.5f)) * fz < 0;
		FlipDoor(x, by, z, turn);
		if (pair) FlipDoor(nx, by, nz, turn);
		Sound.I?.Place(id); MobNoise(x, by, z);
	}
	// a door's both halves: opened if shut, shut if open (turn: facing + 2 and the hinge the other side, first)
	void FlipDoor(int x, int by, int z, bool turn)
	{
		int id = world.GetBlock(x, by, z);
		var b = Blocks.B[id];
		int facing = turn ? (b.Facing + 2) % 4 : b.Facing, hinge = turn ? 1 - b.Hinge : b.Hinge;
		bool open = b.Open == 0;
		StartDoorSwing(x, by, z, id, facing, hinge, open);
		world.SetBlock(x, by, z, Blocks.DoorWith(id, facing, hinge, open, 0));
		if (World.Inside(x, by + 1, z) && Blocks.IsDoor(world.GetBlock(x, by + 1, z))) world.SetBlock(x, by + 1, z, Blocks.DoorWith(id, facing, hinge, open, 1));
	}
	// ---- the swing (only for show): for a moment the panel turns about its hinge into its new pose, away from whoever opened
	// it; the chunk leaves that door out meanwhile (ChunkMesher.Swinging) ----
	const float DoorSwingTime = 0.15f;
	sealed class DoorSwing { public int X, Y, Z, Bottom, Top; public (Vector2 p, Vector2 w, Vector2 n) From, To; public float T; public MeshInstance3D Node; }
	readonly System.Collections.Generic.List<DoorSwing> swings = new();
	void StartDoorSwing(int x, int by, int z, int id, int facing, int hinge, bool open)
	{
		int top = World.Inside(x, by + 1, z) && Blocks.IsDoor(world.GetBlock(x, by + 1, z)) ? Blocks.DoorWith(id, facing, hinge, open, 1) : 0;
		var s = new DoorSwing
		{
			X = x, Y = by, Z = z, Bottom = Blocks.DoorWith(id, facing, hinge, open, 0), Top = top,
			From = ChunkMesher.DoorPose(facing, hinge, !open), To = ChunkMesher.DoorPose(facing, hinge, open),
			Node = new MeshInstance3D { MaterialOverride = blockMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off },
		};
		AddChild(s.Node);
		ChunkMesher.Swinging[World.Idx(x, by, z)] = 0; if (top != 0) ChunkMesher.Swinging[World.Idx(x, by + 1, z)] = 0;
		swings.Add(s); DrawSwing(s);
	}
	void UpdateDoorSwings(float dt)
	{
		for (int i = swings.Count - 1; i >= 0; i--)
		{
			var s = swings[i]; s.T += dt;
			if (s.T < DoorSwingTime) { DrawSwing(s); continue; }
			EndSwing(s); swings.RemoveAt(i);
		}
	}
	void EndSwing(DoorSwing s)
	{
		s.Node.QueueFree();
		ChunkMesher.Swinging.TryRemove(World.Idx(s.X, s.Y, s.Z), out _); ChunkMesher.Swinging.TryRemove(World.Idx(s.X, s.Y + 1, s.Z), out _);
		world?.Redraw(s.X, s.Y, s.Z);
	}
	void ClearDoorSwings() { foreach (var s in swings) { s.Node.QueueFree(); } swings.Clear(); ChunkMesher.Swinging.Clear(); }
	void DrawSwing(DoorSwing s)
	{
		float k = Math.Clamp(s.T / DoorSwingTime, 0, 1), a = k * MathF.PI / 2, c = MathF.Cos(a), sn = MathF.Sin(a);
		var pivot = s.From.p.Lerp(s.To.p, k); var w = s.From.w * c + s.To.w * sn; var n = s.From.n * c + s.To.n * sn;
		var part = new ChunkMesher.Part(); var sky = new System.Collections.Generic.Dictionary<int, float>();
		ChunkMesher.DoorPanel(part, Blocks.B[s.Bottom], new Vector3(s.X, s.Y, s.Z), pivot, w, n, World.LightCurve(world.LightAt(s.X, s.Y, s.Z)), world.SkyExposure(s.X, s.Y, s.Z, sky));
		if (s.Top != 0) ChunkMesher.DoorPanel(part, Blocks.B[s.Top], new Vector3(s.X, s.Y + 1, s.Z), pivot, w, n, World.LightCurve(world.LightAt(s.X, s.Y + 1, s.Z)), world.SkyExposure(s.X, s.Y + 1, s.Z, sky));
		s.Node.Mesh = part.ToMesh();
	}

	// a door broken: its other half goes too (no second drop)
	void BreakDoorHalf(int x, int y, int z, BlockDef b)
	{
		int oy = b.Half == 0 ? y + 1 : y - 1;
		if (World.Inside(x, oy, z) && Blocks.Get(world.GetBlock(x, oy, z)) is { Shape: "door" } o && o.Half != b.Half && o.Drop == b.Drop) world.SetBlock(x, oy, z, 0);
	}
}
