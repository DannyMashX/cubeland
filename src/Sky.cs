using System;
using System.Collections.Generic;
using Godot;

namespace Cubeland;

// Day and night: the sky colour, the sun and moon, stars, blocky drifting clouds, and how bright the daylight is.
public partial class Sky : Node3D
{
	// ---- The clock (Beta 0.1.6.1; the same as the web version): Clock = game seconds since the world's day 0 began. A day is
	// DayLength seconds and starts at sunrise; daylight lasts DayLight(...) seconds of it (longer in summer, shorter in winter),
	// the night the rest. The old 0..1 time of day (0 = sunrise, 0.25 = noon, 0.5 = sunset, 0.75 = midnight) is worked out from
	// it for the place you're at (TimeOfDay), so everything that reads it works as before. Day 0 is the first day of (northern)
	// spring. The season is whole numbers both editions share exactly (S1024), never a cos. ----
	static ClockNumbers CN => Nums.N.CLOCK;
	public static readonly double DayLength = Nums.N.CLOCK.day;
	public double Clock = Nums.N.CLOCK.newWorldT;
	// the place the sky is drawn for (your own): how strongly the seasons change the daylight there (1 outside Earthlike
	// worlds; in Earthlike ones from none at the equator to poleSwing near the poles), and whether it's the south half
	public double PlaceK = 1; public bool PlaceSouth;
	public static long DayOf(double clock) => (long)Math.Floor(clock / DayLength);
	public static int FloorDiv(long a, long b) => (int)Math.Floor((double)a / b);
	static int S24(long d) => Nums.N.WEATHER.S24[(int)(((d % 24) + 24) % 24)];
	// the day of the year (0-23) a place is on: the south is 12 days on
	public static int DayOfYear(long day, bool south) => (int)((((day + (south ? 12 : 0)) % 24) + 24) % 24);
	// the season now: +1024 at midsummer, -1024 at midwinter, blended from today's toward tomorrow's by the game minute
	public static int S1024(double clock, bool south)
	{
		long day = DayOf(clock), minute = (long)Math.Floor(clock / 60); int m = (int)(minute - 40 * day);
		int dl = DayOfYear(day, south);
		return FloorDiv((long)S24(dl) * (40 - m) + (long)S24(dl + 1) * m, 40);
	}
	// how the seasons reach a place: (Earthlike worlds) by its distance from the equator, the world's middle row
	public static (double k, bool south) PlaceOf(bool earthlike, double z, int worldZ)
	{
		if (!earthlike) return (1, false);
		double half = worldZ / 2.0, lat = Math.Abs(z - half) / half;
		return (CN.poleSwing * Math.Min(1, lat), z >= half);
	}
	// seconds of daylight on a day (fixed for the whole day, so the sun never jumps)
	public static double DayLight(long day, double k, bool south) => CN.light + CN.lightSwing * k * S24(DayOfYear(day, south)) / 1024.0;
	// the old 0..1 time of day at a clock and place
	public static double Fraction(double clock, double k, bool south)
	{
		long day = DayOf(clock); double t = clock - DayLength * day, d = DayLight(day, k, south);
		return t < d ? 0.5 * t / d : 0.5 + 0.5 * (t - d) / (DayLength - d);
	}
	// and back: the seconds into a day for a 0..1 time of day
	public static double SecondsAt(double frac, long day, double k, bool south)
	{
		double d = DayLight(day, k, south); frac = ((frac % 1) + 1) % 1;
		return frac < 0.5 ? 2 * frac * d : d + 2 * (frac - 0.5) * (DayLength - d);
	}
	// how much daylight there is (0 at night, 1 in full day) at a 0..1 time of day: the same curve as ever
	public static float DayCurve(double frac)
	{
		float ang = (float)(frac * Math.PI * 2);
		var sunDir = new Vector3(MathF.Cos(ang), MathF.Sin(ang), 0.25f).Normalized();
		return Smooth(-0.18f, 0.22f, sunDir.Y);
	}
	// daylight at another place (a friend far south in an Earthlike world gets their own nights)
	public float SkyDayAt(bool earthlike, double z, int worldZ) { var (k, s) = PlaceOf(earthlike, z, worldZ); return DayCurve(Fraction(Clock, k, s)); }
	public long Day => DayOf(Clock);
	// ---- The moon's own path (Beta 0.1.6.1): it falls behind the sun by a whole turn every moonDays days (el, its elongation, in
	// turns: 0 new, 0.5 full), its path leans off the sun's by a lean that turns every nodeDays days (node), and its distance
	// swings every anomDays days (anom). Each world starts them at its own place, from its seed. The same as the web version. ----
	public int Seed;   // (the world's: set with the place)
	static int Imul(long a, int b) => unchecked((int)a * b);
	// the land's hash at (0, 0) (WorldGen.Hash), for the world's starts
	public static double SeedHash(long seed, int salt)
	{
		int h = unchecked(Imul(0, 374761393) + Imul(0, 668265263) + Imul(seed + salt, 1442695041));
		h = Imul(h ^ (int)((uint)h >> 13), 1274126177);
		h ^= (int)((uint)h >> 16);
		return (uint)h / 4294967296.0;
	}
	public static int Start(long seed, int salt) => (int)Math.Floor(65536 * SeedHash(seed, salt));
	static double Frac(double x) => x - Math.Floor(x);
	static double MoonSecs => CN.moonDays * DayLength; static double NodeSecs => CN.nodeDays * DayLength; static double AnomSecs => CN.anomDays * DayLength;
	public static double El(double clock, long seed) => Frac(clock / MoonSecs + Start(seed, 7320) / 65536.0);
	public static double Node(double clock, long seed) => Frac(clock / NodeSecs + Start(seed, 7321) / 65536.0);
	public static double Anom(double clock, long seed) => Frac(clock / AnomSecs + Start(seed, 7322) / 65536.0);
	// the same in whole numbers (0-65535), for events and tests
	static int Whole(double clock, double secs, long seed, int salt) => (int)((((long)Math.Floor(Math.Floor(clock) * 65536 / secs) + Start(seed, salt)) % 65536 + 65536) % 65536);
	public static (int el, int node, int anom) MoonWhole(double clock, long seed) => (Whole(clock, MoonSecs, seed, 7320), Whole(clock, NodeSecs, seed, 7321), Whole(clock, AnomSecs, seed, 7322));
	// how much of the moon is lit (0 new, 1 full), and its phase 0-7 (0 new, 4 full)
	public static float MoonLit(double el) => (float)((1 - Math.Cos(2 * Math.PI * el)) / 2);
	public static int PhaseOf(double el) => (int)((Math.Floor(8 * el + 0.5) % 8 + 8) % 8);
	public double MoonEl => El(Clock, Seed);
	public int MoonPhase => PhaseOf(MoonEl);
	// where the moon is in the sky, for a sun at angle a (radians)
	public static Vector3 MoonDir(double a, double el, double node) => new Vector3((float)Math.Cos(a - 2 * Math.PI * el), (float)Math.Sin(a - 2 * Math.PI * el), (float)(0.25 + CN.moonLean * Math.Sin(2 * Math.PI * (el - node)))).Normalized();
	public static float MoonScale(double anom) => (float)(1 + CN.moonBig * (1 + Math.Cos(2 * Math.PI * anom)) / 2);
	public static readonly string[] Seasons = { "Spring", "Summer", "Autumn", "Winter" };
	// the date, as the pause menu shows it: "Day 3 of Autumn, Year 1" (the day of its season, 1-6; the south has its own season)
	// (Beta 0.1.6.1) the Clock's and the Calendar's words and pictures (the same as the web version)
	public static readonly string[] Parts = { "Dawn", "Morning", "Midday", "Afternoon", "Dusk", "Evening", "Midnight", "Late night" };
	public static int PartOf(double f) => (int)((Math.Floor(8 * f + 0.5) % 8 + 8) % 8);
	public static int SeasonOf(long day, bool south) => DayOfYear(day, south) / 6;
	public static string ClockWords(double clock, double k, bool south)
	{
		long day = DayOf(clock); double t = clock - DayLength * day, d = DayLight(day, k, south);
		long n = (long)Math.Ceiling((t < d ? d - t : DayLength - t) / 60);
		return $"{Parts[PartOf(Fraction(clock, k, south))]}: {n} minute{(n == 1 ? "" : "s")} {(t < d ? "of daylight left" : "to sunrise")}";
	}
	public static string CalendarWords(double clock, bool south) => DateText(DayOf(clock), south);
	public static string DateText(long day, bool south) { int dl = DayOfYear(day, south); return $"Day {dl % 6 + 1} of {Seasons[dl / 6]}, Year {day / 24 + 1}"; }
	// (testing: --clocktest) the clock's numbers at set clocks and places, the same lines as the web version's
	public static void ClockTest()
	{
		var ic = System.Globalization.CultureInfo.InvariantCulture;
		GD.Print("SEASON S24 " + string.Join(",", Nums.N.WEATHER.S24));
		var places = new (string name, bool earth, double z, int wz)[] { ("other", false, 0, 0), ("earth1024z100", true, 100, 1024), ("earth1024z512", true, 512, 1024), ("earth1024z900", true, 900, 1024), ("earth2048z40", true, 40, 2048) };
		foreach (var (name, earth, z, wz) in places)
			foreach (double c in new[] { 0, 240, 1499.5, 2399.9, 8200, 22200, 52200, 72123, 129634 })
			{
				var (k, south) = PlaceOf(earth, z, wz); long day = DayOf(c); int m = (int)((long)Math.Floor(c / 60) - 40 * day);
				GD.Print(string.Format(ic, "CLOCK {0} c={1:F1} day={2} m={3} dl={4} s={5} D={6} f={7:F5} date={8}",
					name, c, day, m, DayOfYear(day, south), S1024(c, south), DayLight(day, k, south).ToString("R", ic), Fraction(c, k, south), DateText(day, south)));
			}
		// the moon's own path, for three seeds
		var seeds = new long[] { 5, 777, 12345 };
		foreach (long seed in seeds)
			foreach (double c in new[] { 0, 240, 1499.5, 2399.9, 8200, 22200, 52200, 72123, 129634 })
			{
				var (we, wn, wa) = MoonWhole(c, seed); double el = El(c, seed), a = 2 * Math.PI * Fraction(c, 1, false), b = a - 2 * Math.PI * el;
				GD.Print(string.Format(ic, "SKY seed={0} c={1:F1} EL={2} NODE={3} AN={4} el={5} a={6} b={7} lit={8:F3}", seed, c, we, wn, wa, el.ToString("R", ic), a.ToString("R", ic), b.ToString("R", ic), MoonLit(el)));
			}
		uint h = 2166136261;
		foreach (long seed in seeds)
			for (int k = 0; k < 1000; k++)
			{
				var (we, wn, wa) = MoonWhole(1237.0 * k, seed);
				foreach (int v in new[] { we, wn, wa }) h = unchecked((h ^ (uint)v) * 16777619);
			}
		GD.Print("SKYHASH " + h.ToString("x8"));
		// the Clock's and the Calendar's words and pictures, at the CLOCK lines' places and clocks
		foreach (var (name, earth, z, wz) in places)
			foreach (double c in new[] { 0, 240, 1499.5, 2399.9, 8200, 22200, 52200, 72123, 129634 })
			{
				var (k, south) = PlaceOf(earth, z, wz);
				GD.Print(string.Format(ic, "ITEMWORDS {0} c={1:F1} i={2} s={3} clock=\"{4}\" calendar=\"{5}\"",
					name, c, PartOf(Fraction(c, k, south)), SeasonOf(DayOf(c), south), ClockWords(c, k, south), CalendarWords(c, south)));
			}
	}
	public float TimeOfDay
	{
		get => (float)Fraction(Clock, PlaceK, PlaceSouth);
		set { long day = Day; Clock = day * DayLength + SecondsAt(value, day, PlaceK, PlaceSouth); }   // (moves the time within today; the date stays)
	}
	public float SkyDay = 1;                    // 0 at night, 1 in full day
	public Vector3 DayTint = Vector3.One;
	public Color Now;                           // the sky colour right now
	static readonly Color SkyDayC = new(0x8ec9ffff), SkyNight = new(0, 0, 0), SkyDusk = new(0xff7a38ff);
	const float SkyDist = 900;                  // sun, moon and stars sit just inside the camera's far limit
	MeshInstance3D sun, sunGlow, moon, moonGlow, stars;
	readonly List<MeshInstance3D> clouds = new();
	ShaderMaterial cloudMat;
	float cloudDrift;
	static float CloudY => World.Lift + 150; const float CloudCell = 12, CloudThick = 4, CloudSpeed = 1.2f, CloudCoverage = 0.42f; const int CloudGrid = 48;

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
		moon.MaterialOverride = Mat(new Color(0xdde4f0ff), -1, "moon");   // (its phase is drawn on it; drawn after the sun, so it can cover it)
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
		Clock += dt;
		float ang = TimeOfDay * MathF.PI * 2;
		var sunDir = new Vector3(MathF.Cos(ang), MathF.Sin(ang), 0.25f).Normalized();   // rises in the east (+x), sets in the west
		float height = sunDir.Y;
		float day = Smooth(-0.18f, 0.22f, height);
		SkyDay = day;
		float dusk = MathF.Pow(Math.Max(0, 1 - MathF.Abs(height) / 0.35f), 1.5f) * 0.9f;   // orange glow around sunrise and sunset
		Now = SkyNight.Lerp(SkyDayC, day).Lerp(SkyDusk, dusk * (0.4f + 0.6f * day));
		// blocks fade to pitch black at night (only torches light things up)
		DayTint = new Vector3(day * (0.82f + 0.18f * day) + dusk * 0.06f, day * (0.88f + 0.12f * day), day);
		// (Beta 0.1.6.1) moonlight: near the full moon a faint blue outdoors at night. Only the tint: SkyDay (night mobs, burning,
		// music) never sees it
		double el0 = MoonEl; float lit = MoonLit(el0); var ml = CN.moonlight;
		float moonUp = Smooth(-0.05f, 0.1f, MoonDir(ang, el0, Node(Clock, Seed)).Y);   // (only while the moon is up)
		if (ml is { Length: 3 }) DayTint += new Vector3(ml[0], ml[1], ml[2]) * lit * (1 - day) * moonUp;
		RenderingServer.GlobalShaderParameterSet("day_tint", DayTint);

		var c = cam.GlobalPosition; var face = cam.GlobalBasis;
		void Place(MeshInstance3D m, Vector3 dir, float dist) { m.GlobalPosition = c + dir * dist; m.GlobalBasis = face; }
		Place(sun, sunDir, SkyDist); Place(sunGlow, sunDir, SkyDist + 5);
		double el = MoonEl; var moonDir = MoonDir(ang, el, Node(Clock, Seed)); float msc = MoonScale(Anom(Clock, Seed));
		Place(moon, moonDir, SkyDist - 20); Place(moonGlow, moonDir, SkyDist - 15);   // (a little nearer than the sun, so it can pass in front)
		moon.Scale = moonGlow.Scale = Vector3.One * msc;
		sun.Visible = sunGlow.Visible = height > -0.2f;
		moon.Visible = moonGlow.Visible = moonDir.Y > -0.2f;
		((ShaderMaterial)sun.MaterialOverride).SetShaderParameter("color", new Color(1, 0.95f - dusk * 0.25f, 0.7f - dusk * 0.35f));
		var mm = (ShaderMaterial)moon.MaterialOverride;
		mm.SetShaderParameter("color", new Color(0xdde4f0ff) { A = 1 - day * 0.7f });
		mm.SetShaderParameter("lit_cols", MathF.Round(16 * lit)); mm.SetShaderParameter("from_right", el < 0.5 ? 1f : 0f); mm.SetShaderParameter("dark_alpha", 1 - day);
		((ShaderMaterial)moonGlow.MaterialOverride).SetShaderParameter("color", new Color(0xaab8e0ff) { A = 0.12f * (1 - day) * lit });
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
