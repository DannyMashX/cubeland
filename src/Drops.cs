using System;
using System.Collections.Generic;
using Godot;

namespace Cubeland;

// Dropped items: little spinning pictures of the item that you pick up by walking into them (survival).
public partial class Drops : Node3D
{
	// K: its own number, so players in a multiplayer game know which one is which. T: (joined game) where the host's game says it is
	public class Drop { public int Id, K; public Vector3 P, V, T; public float Age, Spin, LightT; public bool Asked; public MeshInstance3D Mesh; public ShaderMaterial Mat; }
	int count;
	public bool Client;                      // in a friend's world: their game moves the items; walking into one asks them for it
	public Action<Drop> AskPickup;
	public readonly List<Drop> All = new();
	World W; Player player; Inventory inv;
	static Shader shader; static QuadMesh quad;
	readonly Random rnd = new();
	public Func<Vector3> DayTint = () => Vector3.One;
	public Vector3 TorchColor = new(1, 0.82f, 0.58f);
	public Func<Vector3, float> HeldLight = _ => 0;   // the light of a torch in your hand, at a spot

	public void Init(World w, Player p, Inventory i) { W = w; player = p; inv = i; }

	public Drop Add(int id, float x, float y, float z)
	{
		shader ??= GD.Load<Shader>("res://src/item.gdshader");
		quad ??= new QuadMesh { Size = new Vector2(0.4f, 0.4f) };
		var mat = new ShaderMaterial { Shader = shader };
		mat.SetShaderParameter("tex", Atlas.IconTexture(id));
		var mesh = new MeshInstance3D { Mesh = quad, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
		AddChild(mesh);
		double a = rnd.NextDouble() * Math.PI * 2;
		var d = new Drop { K = ++count, Id = id, P = new Vector3(x, y, z), V = new Vector3((float)Math.Cos(a) * 1.5f, 4, (float)Math.Sin(a) * 1.5f), Spin = (float)rnd.NextDouble() * 6, Mesh = mesh, Mat = mat };
		All.Add(d);
		return d;
	}
	public void Remove(Drop d) { d.Mesh.QueueFree(); All.Remove(d); }
	public void Clear() { foreach (var d in All.ToArray()) Remove(d); }

	public void Update(float dt)
	{
		foreach (var d in All.ToArray())
		{
			d.Age += dt;
			int F(float v) => (int)MathF.Floor(v);
			if (Client)
			{
				d.P += (d.T - d.P) * Math.Min(1, dt * 10);
				var cp = player.Pos + new Vector3(0, 0.9f, 0);
				if (!d.Asked && MathF.Abs(d.P.X - cp.X) < 1.1f && MathF.Abs(d.P.Y + 0.25f - cp.Y) < 1.4f && MathF.Abs(d.P.Z - cp.Z) < 1.1f) { d.Asked = true; AskPickup?.Invoke(d); }
			}
			else {
			if (d.Age > 300) { Remove(d); continue; }   // gone after 5 minutes
			// fall and slide to a stop, resting on top of blocks
			d.V.Y = Math.Max(-30, d.V.Y - 22 * dt);
			float nx = d.P.X + d.V.X * dt, nz = d.P.Z + d.V.Z * dt, ny = d.P.Y + d.V.Y * dt;
			if (!W.Solid(F(nx), F(d.P.Y), F(d.P.Z))) d.P.X = nx; else d.V.X = 0;
			if (!W.Solid(F(d.P.X), F(d.P.Y), F(nz))) d.P.Z = nz; else d.V.Z = 0;
			if (W.Solid(F(d.P.X), F(ny), F(d.P.Z)))
			{
				if (d.V.Y < 0) { d.P.Y = F(ny) + 1; d.V.X *= 0.5f; d.V.Z *= 0.5f; }
				d.V.Y = 0;
			}
			else d.P.Y = ny;
			if (d.P.Y < -10) { Remove(d); continue; }
			// pick up: walk into it (after a short moment so it can pop out first)
			var pp = player.Pos + new Vector3(0, 0.9f, 0);
			if (d.Age > 0.5f && MathF.Abs(d.P.X - pp.X) < 1.1f && MathF.Abs(d.P.Y + 0.25f - pp.Y) < 1.4f && MathF.Abs(d.P.Z - pp.Z) < 1.1f && inv.Give(d.Id)) { Remove(d); continue; }
			}
			d.Spin += dt * 2;
			d.Mesh.Position = new Vector3(d.P.X, d.P.Y + 0.25f + MathF.Sin(d.Age * 3) * 0.06f, d.P.Z);
			d.Mesh.Rotation = new Vector3(0, d.Spin, 0);
			// lit like everything else
			if ((d.LightT -= dt) <= 0)
			{
				d.LightT = 0.3f;
				int bx = F(d.P.X), by = F(d.P.Y), bz = F(d.P.Z);
				float sky = W.SkyExposure(bx, by, bz, null), torch = MathF.Max(World.LightCurve(W.LightAt(bx, by, bz)), HeldLight(d.P));
				var t = DayTint();
				d.Mat.SetShaderParameter("light", new Vector3(MathF.Max(t.X * sky, torch * TorchColor.X), MathF.Max(t.Y * sky, torch * TorchColor.Y), MathF.Max(t.Z * sky, torch * TorchColor.Z)));
			}
		}
	}
}
