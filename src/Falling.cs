using System;
using System.Collections.Generic;
using Godot;

namespace Cubeland;

// Falling blocks: Sand, Gravel and Dirt need something under them. Whenever a block next to one changes (or one is put down),
// it's checked: if the space under it is empty (or water, a torch, a plant or something lying there) it comes loose and falls,
// and lands on the first thing it hits, knocking off whatever small thing was in the spot it lands in. Land the world was made
// with that floats over a cave stays put until something next to it changes. (The checks are queued by World.WakeFall.)
public partial class Falling : Node3D
{
	// K: its own number, so players in a multiplayer game know which one is which. Y: the height of its bottom; Ty: (joined game) where the host's game says it is
	public class Block { public int Id, K, X, Z; public float Y, Ty, Vy, LightT; public MeshInstance3D Mesh; public ShaderMaterial Mat; }
	public static readonly HashSet<int> Falls = new() { Blocks.Dirt, Blocks.Sand, Blocks.Gravel };
	public readonly List<Block> All = new();
	public bool Client;   // in a friend's world: their game moves them and puts the blocks down
	int count;
	World W;
	Action<int, int, int, int> collect; Func<int, int> dropFor;
	static Shader shader;
	public Func<Vector3> DayTint = () => Vector3.One;
	public Vector3 TorchColor = new(1, 0.82f, 0.58f);
	public Func<Vector3, float> HeldLight = _ => 0;

	public void Init(World w, Action<int, int, int, int> collect, Func<int, int> dropFor) { W = w; this.collect = collect; this.dropFor = dropFor; }

	public static bool FallsThrough(int id)
	{
		if (id == 0 || Blocks.IsWater(id) || Blocks.IsTorch(id)) return true;
		return Blocks.Get(id)?.Shape is "flat" or "plant";
	}

	public Block Add(int id, int x, float y, int z)
	{
		shader ??= GD.Load<Shader>("res://src/item.gdshader");
		var model = Atlas.BoxModel(id);
		var mat = new ShaderMaterial { Shader = shader };
		mat.SetShaderParameter("tex", model.tex);
		var mesh = new MeshInstance3D { Mesh = model.mesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
		AddChild(mesh);
		var f = new Block { K = ++count, Id = id, X = x, Y = y, Ty = y, Z = z, Mesh = mesh, Mat = mat };
		Place(f);
		All.Add(f);
		return f;
	}
	public void Remove(Block f) { f.Mesh.QueueFree(); All.Remove(f); }

	// it lands with its bottom at y: whatever small thing was there pops off (a torch goes out), and it becomes a block again
	void Land(Block f, int y)
	{
		Remove(f);
		while (y < World.WY && !FallsThrough(W.GetBlock(f.X, y, f.Z))) y++;   // (something was put in its way while it fell: it lands on top)
		if (y >= World.WY) { collect(f.Id, f.X, World.WY - 1, f.Z); return; }
		int there = W.GetBlock(f.X, y, f.Z);
		if (Blocks.IsTorch(there)) for (int n = Blocks.B[there].Count; n > 0; n--) collect(Blocks.TorchDrop(there), f.X, y, f.Z);
		else if (there != 0 && !Blocks.IsWater(there)) collect(dropFor(there), f.X, y, f.Z);
		for (int n = Blocks.SnowballsFor(Blocks.SnowIn(there), Random.Shared); n > 0; n--) collect(Blocks.Snowball, f.X, y, f.Z);   // (and any snow round it)
		W.SetBlock(f.X, y, f.Z, f.Id);
	}
	// (saving: anything still in the air lands straight away, so it's never lost)
	public void Settle()
	{
		if (Client) return;
		foreach (var f in All.ToArray()) { int y = (int)MathF.Floor(f.Y); while (y > 0 && FallsThrough(W.GetBlock(f.X, y - 1, f.Z))) y--; Land(f, y); }
	}

	public void Update(float dt)
	{
		if (!Client)
		{
			// loose blocks come away
			int n = 0;
			foreach (long i in new List<long>(W.FallQueue))
			{
				W.FallQueue.Remove(i);
				if (++n > 4000) break;
				int id = W.Blocks[i];
				var (x, y, z) = World.FromIdx(i);
				if (!Falls.Contains(id) || y == 0 || !FallsThrough(W.GetBlock(x, y - 1, z))) continue;
				W.SetBlock(x, y, z, 0);
				W.DropTorchesAround(x, y, z, dropFor);   // (torches stuck to its sides fall off)
				Add(id, x, y, z);
			}
		}
		foreach (var f in All.ToArray())
		{
			if (Client) f.Y += (f.Ty - f.Y) * Math.Min(1, dt * 10);   // joined game: the host's game moves them
			else
			{
				// fall, and land on the first thing it hits (its bottom going down past the top of a block that holds it up)
				f.Vy = Math.Max(-40, f.Vy - 30 * dt);
				float ny = f.Y + f.Vy * dt;
				bool landed = false;
				for (int c = (int)MathF.Ceiling(f.Y) - 1; c >= (int)MathF.Floor(ny); c--)
					if (c < 0 || !FallsThrough(W.GetBlock(f.X, c, f.Z))) { Land(f, c + 1); landed = true; break; }
				if (landed) continue;
				f.Y = ny;
			}
			Place(f);
			// lit like everything else
			if ((f.LightT -= dt) <= 0)
			{
				f.LightT = 0.15f;
				int by = (int)MathF.Floor(f.Y + 0.5f);
				float sky = W.SkyExposure(f.X, by, f.Z, null), torch = MathF.Max(World.LightCurve(W.LightAt(f.X, by, f.Z)), HeldLight(f.Mesh.Position));
				var t = DayTint();
				f.Mat.SetShaderParameter("light", new Vector3(MathF.Max(t.X * sky, torch * TorchColor.X), MathF.Max(t.Y * sky, torch * TorchColor.Y), MathF.Max(t.Z * sky, torch * TorchColor.Z)));
			}
		}
	}
	static void Place(Block f) => f.Mesh.Position = new Vector3(f.X + 0.5f, f.Y + 0.5f, f.Z + 0.5f);
}
