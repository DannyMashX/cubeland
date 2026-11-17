using System;
using System.Collections.Generic;
using Godot;

namespace Cubeland;

// Day and night: the sky colour, the sun and moon, stars, blocky drifting clouds, and how bright the daylight is.
public partial class Sky : Node3D
{
	public const float DayLength = 12 * 60;    // seconds for one full day + night
	public float TimeOfDay = 0.08f;             // 0 = sunrise, 0.25 = noon, 0.5 = sunset, 0.75 = midnight
	public float SkyDay = 1;                    // 0 at night, 1 in full day
	public Vector3 DayTint = Vector3.One;
	public Color Now;                           // the sky colour right now
	static readonly Color SkyDayC = new(0x8ec9ffff), SkyNight = new(0, 0, 0), SkyDusk = new(0xff7a38ff);
	const float SkyDist = 900;                  // sun, moon and stars sit just inside the camera's far limit
	MeshInstance3D sun, sunGlow, moon, moonGlow, stars;
	readonly List<MeshInstance3D> clouds = new();
	ShaderMaterial cloudMat;
	float cloudDrift;
	const float CloudY = 150, CloudCell = 12, CloudThick = 4, CloudSpeed = 1.2f, CloudCoverage = 0.42f; const int CloudGrid = 48;

	static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
	static Func<double> SkyRng(int seed)
	{
		uint a = (uint)seed;
		return () => { a += 0x6D2B79F5; uint t = (a ^ (a >> 15)) * (1 | a); t = (t + ((t ^ (t >> 7)) * (61 | t))) ^ t; return (t ^ (t >> 14)) / 4294967296.0; };
	}
	static ShaderMaterial Mat(Color c, int priority, string file = "sky")
	{
		var m = new ShaderMaterial { Shader = GD.Load<Shader>($"res://src/{file}.gdshader"), RenderPriority = priority };
		m.SetShaderParameter("color", c);
		return m;
	}
	MeshInstance3D Body(Color c, float size, float alpha = 1)
	{
		var m = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(size, size) }, MaterialOverride = Mat(new Color(c, alpha), -2), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
		m.ExtraCullMargin = 2000;
		AddChild(m);
		return m;
	}
	public override void _Ready()
	{
		sun = Body(new Color(0xfff2b0ff), 70);
		sunGlow = Body(new Color(0xffd27aff), 120, 0.25f);
		moon = Body(new Color(0xdde4f0ff), 100);
		moonGlow = Body(new Color(0xaab8e0ff), 190, 0.12f);
		// stars: random points on a big sphere around you, visible only at night
		var pts = new List<Vector3>(); var r = SkyRng(4242);
		for (int i = 0; i < 900; i++)
		{
			double u = r() * 2 - 1, a = r() * Math.PI * 2, s = Math.Sqrt(1 - u * u);
			if (u < -0.1) continue;   // none below the horizon
			pts.Add(new Vector3((float)(Math.Cos(a) * s * SkyDist), (float)(u * SkyDist), (float)(Math.Sin(a) * s * SkyDist)));
		}
		var arr = new Godot.Collections.Array(); arr.Resize((int)Mesh.ArrayType.Max); arr[(int)Mesh.ArrayType.Vertex] = pts.ToArray();
		var sm = new ArrayMesh(); sm.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arr);
		stars = new MeshInstance3D { Mesh = sm, MaterialOverride = Mat(Colors.White, -3, "stars"), ExtraCullMargin = 2000 };
		AddChild(stars);
		// clouds: one repeating pattern; a 3x3 grid of copies follows you, so the sky never runs out
		var cm = CloudMesh(); cloudMat = Mat(new Color(1, 1, 1, 0.85f), 2, "clouds");
		for (int i = 0; i < 9; i++) { var c = new MeshInstance3D { Mesh = cm, MaterialOverride = cloudMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off }; AddChild(c); clouds.Add(c); }
	}
	static ArrayMesh CloudMesh()
	{
		int N = CloudGrid; var r = SkyRng(777);
		// smooth wrapping noise: coarse random grid, blended, plus a bit of fine detail
		const int C = 8; var coarse = new double[C * C]; for (int i = 0; i < C * C; i++) coarse[i] = r();
		double Cg(int x, int z) => coarse[((z % C + C) % C) * C + ((x % C + C) % C)];
		var filled = new bool[N * N];
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			double fx = (double)x / N * C, fz = (double)z / N * C; int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
			double tx = Smooth(0, 1, (float)(fx - ix)), tz = Smooth(0, 1, (float)(fz - iz));
			double v = (Cg(ix, iz) * (1 - tx) + Cg(ix + 1, iz) * tx) * (1 - tz) + (Cg(ix, iz + 1) * (1 - tx) + Cg(ix + 1, iz + 1) * tx) * tz;
			filled[z * N + x] = v + (r() - 0.5) * 0.25 > 1 - CloudCoverage;
		}
		bool At(int x, int z) => filled[((z + N) % N) * N + ((x + N) % N)];
		var pos = new List<Vector3>(); var col = new List<Color>();
		float s = CloudCell, h = CloudThick;
		void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float shade) { pos.AddRange(new[] { a, b, c, a, c, d }); for (int i = 0; i < 6; i++) col.Add(new Color(shade, shade, shade)); }
		for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
		{
			if (!At(x, z)) continue;
			float x0 = x * s, x1 = x0 + s, z0 = z * s, z1 = z0 + s;
			Quad(new(x0, h, z0), new(x0, h, z1), new(x1, h, z1), new(x1, h, z0), 1.0f);
			Quad(new(x0, 0, z0), new(x1, 0, z0), new(x1, 0, z1), new(x0, 0, z1), 0.72f);
			if (!At(x - 1, z)) Quad(new(x0, 0, z0), new(x0, 0, z1), new(x0, h, z1), new(x0, h, z0), 0.85f);
			if (!At(x + 1, z)) Quad(new(x1, 0, z1), new(x1, 0, z0), new(x1, h, z0), new(x1, h, z1), 0.85f);
			if (!At(x, z - 1)) Quad(new(x1, 0, z0), new(x0, 0, z0), new(x0, h, z0), new(x1, h, z0), 0.8f);
			if (!At(x, z + 1)) Quad(new(x0, 0, z1), new(x1, 0, z1), new(x1, h, z1), new(x0, h, z1), 0.8f);
		}
		var arr = new Godot.Collections.Array(); arr.Resize((int)Mesh.ArrayType.Max);
		arr[(int)Mesh.ArrayType.Vertex] = pos.ToArray(); arr[(int)Mesh.ArrayType.Color] = col.ToArray();
		var m = new ArrayMesh(); m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
		return m;
	}

	public void Tick(float dt, Camera3D cam)
	{
		TimeOfDay = (TimeOfDay + dt / DayLength) % 1;
		float ang = TimeOfDay * MathF.PI * 2;
		var sunDir = new Vector3(MathF.Cos(ang), MathF.Sin(ang), 0.25f).Normalized();   // rises in the east (+x), sets in the west
		float height = sunDir.Y;
		float day = Smooth(-0.18f, 0.22f, height);
		SkyDay = day;
		float dusk = MathF.Pow(Math.Max(0, 1 - MathF.Abs(height) / 0.35f), 1.5f) * 0.9f;   // orange glow around sunrise and sunset
		Now = SkyNight.Lerp(SkyDayC, day).Lerp(SkyDusk, dusk * (0.4f + 0.6f * day));
		// blocks fade to pitch black at night (only torches light things up)
		DayTint = new Vector3(day * (0.82f + 0.18f * day) + dusk * 0.06f, day * (0.88f + 0.12f * day), day);
		RenderingServer.GlobalShaderParameterSet("day_tint", DayTint);

		var c = cam.GlobalPosition; var face = cam.GlobalBasis;
		void Place(MeshInstance3D m, Vector3 dir, float dist) { m.GlobalPosition = c + dir * dist; m.GlobalBasis = face; }
		Place(sun, sunDir, SkyDist); Place(sunGlow, sunDir, SkyDist + 5);
		Place(moon, -sunDir, SkyDist); Place(moonGlow, -sunDir, SkyDist + 5);
		sun.Visible = sunGlow.Visible = height > -0.2f;
		moon.Visible = moonGlow.Visible = height < 0.2f;
		((ShaderMaterial)sun.MaterialOverride).SetShaderParameter("color", new Color(1, 0.95f - dusk * 0.25f, 0.7f - dusk * 0.35f));
		((ShaderMaterial)moon.MaterialOverride).SetShaderParameter("color", new Color(0xdde4f0ff) { A = 1 - day * 0.7f });
		((ShaderMaterial)moonGlow.MaterialOverride).SetShaderParameter("color", new Color(0xaab8e0ff) { A = 0.12f * (1 - day) });
		stars.GlobalPosition = c;
		float sa = 1 - Smooth(0.12f, 0.44f, day);
		stars.Visible = sa > 0;
		((ShaderMaterial)stars.MaterialOverride).SetShaderParameter("color", new Color(1, 1, 1, sa));
		// clouds: drift slowly, follow you in whole pattern-sized steps, tinted with the time of day
		cloudDrift += dt * CloudSpeed;
		float P = CloudGrid * CloudCell;
		float ox = MathF.Floor((c.X - cloudDrift) / P) * P + cloudDrift - P, oz = MathF.Floor(c.Z / P) * P - P;
		for (int i = 0; i < 9; i++) clouds[i].Position = new Vector3(ox + (i % 3) * P, CloudY, oz + (i / 3) * P);
		var ct = new Color(1, 1, 1) * (0.02f + 0.98f * day); ct = ct.Lerp(SkyDusk, dusk * 0.35f); ct.A = 0.85f;
		cloudMat.SetShaderParameter("color", ct);
	}
}
