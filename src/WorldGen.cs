using System;

namespace Cubeland;

// Terrain generation: the same maths as the web version, so the same seed grows the same world.
public class WorldGen
{
	readonly World W;
	readonly long seed;
	readonly int gen;
	const byte RAW = 255;   // "land, decide grass/dirt/stone later"
	public int SeaLevel => gen == 1 ? 38 : 63;

	public WorldGen(World w, long seed, int gen) { W = w; this.seed = seed; this.gen = gen; }

	// ---- noise (matches the JavaScript bit for bit: Math.imul and >>> become unchecked int maths) ----
	static int Imul(long a, int b) => unchecked((int)a * b);
	public double Hash(int x, int z, int s = 0)
	{
		int h = unchecked(Imul(x, 374761393) + Imul(z, 668265263) + Imul(seed + s, 1442695041));
		h = Imul(h ^ (int)((uint)h >> 13), 1274126177);
		h ^= (int)((uint)h >> 16);
		return (uint)h / 4294967296.0;
	}
	double Hash3(int x, int y, int z, int s)
	{
		int h = unchecked(Imul(x, 374761393) + Imul(y, 1103515245) + Imul(z, 668265263) + Imul(seed + s, 1442695041));
		h = Imul(h ^ (int)((uint)h >> 13), 1274126177);
		h ^= (int)((uint)h >> 16);
		return (uint)h / 4294967296.0;
	}
	public double ValueNoise(double x, double z, int s)
	{
		int xi = (int)Math.Floor(x), zi = (int)Math.Floor(z);
		double fx = x - xi, fz = z - zi;
		double u = fx * fx * (3 - 2 * fx), v = fz * fz * (3 - 2 * fz);
		double a = Hash(xi, zi, s), b = Hash(xi + 1, zi, s), c = Hash(xi, zi + 1, s), d = Hash(xi + 1, zi + 1, s);
		return (a + (b - a) * u) + ((c + (d - c) * u) - (a + (b - a) * u)) * v;
	}
	public double Fbm(double x, double z, int oct, int s)
	{
		double sum = 0, amp = 0.5, f = 1;
		for (int i = 0; i < oct; i++) { sum += ValueNoise(x * f, z * f, s + i * 17) * amp; amp *= 0.5; f *= 2; }
		return sum;
	}
	static double Smoothstep(double a, double b, double v) { double t = Math.Max(0, Math.Min(1, (v - a) / (b - a))); return t * t * (3 - 2 * t); }
	double Noise3(double x, double y, double z, int s)
	{
		int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y), zi = (int)Math.Floor(z);
		double fx = x - xi, fy = y - yi, fz = z - zi;
		fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
		static double L(double a, double b, double t) => a + (b - a) * t;
		return L(
			L(L(Hash3(xi, yi, zi, s), Hash3(xi + 1, yi, zi, s), fx), L(Hash3(xi, yi + 1, zi, s), Hash3(xi + 1, yi + 1, zi, s), fx), fy),
			L(L(Hash3(xi, yi, zi + 1, s), Hash3(xi + 1, yi, zi + 1, s), fx), L(Hash3(xi, yi + 1, zi + 1, s), Hash3(xi + 1, yi + 1, zi + 1, s), fx), fy),
			fz);
	}

	byte[] world => W.Blocks;
	static int Idx(int x, int y, int z) => World.Idx(x, y, z);
	const int WX = World.WX, WY = World.WY, WZ = World.WZ;
	void Put(int x, int y, int z, int id, bool onlyAir = false)
	{
		if (x < 0 || z < 0 || x >= WX || z >= WZ || y < 0 || y >= WY) return;
		int i = Idx(x, y, z);
		if (onlyAir && world[i] != 0) return;
		world[i] = (byte)id;
	}

	public Action<float> Progress;

	public void Generate()
	{
		if (gen == 1) for (int z = 0; z < WZ; z++) for (int x = 0; x < WX; x++)
		{
			int h0 = GroundHeight(x, z);
			for (int y = 0; y < h0; y++) world[Idx(x, y, z)] = RAW;
		}
		else GenerateHills();
		Progress?.Invoke(0.35f);
		W.RecomputeHeights();
		if (gen == 1) Landmarks();
		Paint();
		W.RecomputeHeights();
		Gravel();
		Progress?.Invoke(0.55f);
		// Water: every low spot open to the sky fills up to sea level
		for (int z = 0; z < WZ; z++) for (int x = 0; x < WX; x++)
			for (int y = SeaLevel; y > 0 && world[Idx(x, y, z)] == 0; y--) world[Idx(x, y, z)] = Blocks.Water;
		if (gen == 2) CarveCaves();
		Progress?.Invoke(0.75f);
		Trees();
		Rocks();
		Progress?.Invoke(0.9f);
	}

	// --- Plateaus (the original style): flat steps at many heights, with curvy swirled edges ---
	const int TerraceStep = 5;
	int GroundHeight(int x, int z)
	{
		double wx = x + (Fbm(x / 90.0, z / 90.0, 2, 7) - 0.45) * 110;
		double wz = z + (Fbm(x / 90.0 + 50, z / 90.0 + 50, 2, 8) - 0.45) * 110;
		double region = Fbm(wx / 220, wz / 220, 2, 200);
		double h = 4 + Fbm(wx / 70, wz / 70, 2, 1) * 70 + region * 50;
		return (int)(Math.Floor(h / TerraceStep) * TerraceStep);
	}

	// --- Rolling hills (the style for new worlds): density worked out on a coarse grid and blended ---
	void GenerateHills()
	{
		const int GX = 4, GY = 8;
		int nx = WX / GX + 1, ny = WY / GY + 1, nz = WZ / GX + 1;
		var dens = new float[nx * ny * nz];
		System.Threading.Tasks.Parallel.For(0, nz, gz =>
		{
			for (int gx = 0; gx < nx; gx++)
			{
				int x = gx * GX, z = gz * GX;
				double land = Fbm(x / 280.0, z / 280.0, 3, 900);
				double bas = SeaLevel + 1 + (land - 0.44) * 120;
				double rough = Smoothstep(0.38, 0.62, Fbm(x / 170.0 + 40, z / 170.0 + 40, 2, 910));
				double amp = 5 + rough * 34;
				for (int gy = 0; gy < ny; gy++)
				{
					int y = gy * GY;
					double n = Noise3(x / 52.0, y / 34.0, z / 52.0, 920) * 0.6 + Noise3(x / 22.0, y / 15.0, z / 22.0, 930) * 0.3 + Noise3(x / 9.0, y / 8.0, z / 9.0, 940) * 0.1 - 0.5;
					dens[(gy * nz + gz) * nx + gx] = (float)((bas - y) + n * 2 * amp);
				}
			}
		});
		System.Threading.Tasks.Parallel.For(0, WZ, z =>
		{
			var col = new float[ny];
			int gz = z / GX; float fz = (z % GX) / (float)GX;
			for (int x = 0; x < WX; x++)
			{
				int gx = x / GX; float fx = (x % GX) / (float)GX;
				for (int gy = 0; gy < ny; gy++)
				{
					int o = gy * nz;
					float a = dens[(o + gz) * nx + gx], b = dens[(o + gz) * nx + gx + 1];
					float c = dens[(o + gz + 1) * nx + gx], d = dens[(o + gz + 1) * nx + gx + 1];
					col[gy] = (a + (b - a) * fx) * (1 - fz) + (c + (d - c) * fx) * fz;
				}
				for (int y = 0; y < WY - 2; y++)
				{
					int gy = y / GY; float fy = (y % GY) / (float)GY;
					if (col[gy] + (col[gy + 1] - col[gy]) * fy > 0 || y == 0) world[Idx(x, y, z)] = RAW;
				}
			}
		});
	}

	// --- Landmarks (plateau worlds only): mushroom spires, balanced boulders, stone rings, floating cubes ---
	void Landmarks()
	{
		const int CELL = 24;
		for (int cz = 0; cz < WZ; cz += CELL) for (int cx = 0; cx < WX; cx += CELL)
		{
			int x = cx + 4 + (int)Math.Floor(Hash(cx, cz, 1235) * (CELL - 8));
			int z = cz + 4 + (int)Math.Floor(Hash(cx, cz, 1236) * (CELL - 8));
			int y = W.Heights[z * WX + x];
			if (y > 115) continue;
			double r = Hash(cx, cz, 1234), v = Hash(cx, cz, 1237);
			if (r < 0.10) MushroomSpire(x, y, z, v);
			else if (r < 0.17) BalancedBoulder(x, y, z, v);
			else if (r < 0.22) StoneRing(x, y, z, v);
			else if (r < 0.32) FloatingCubes(x, y, z, v);
		}
	}
	void MushroomSpire(int x, int y, int z, double v)
	{
		int H = 14 + (int)Math.Floor(v * 30), R = 3 + (int)Math.Floor(Hash(x, z, 31) * 5);
		for (int yy = y + 1; yy <= y + H; yy++) for (int d = -1; d <= 1; d++) { Put(x + d, yy, z, 4); Put(x, yy, z + d, 4); }
		for (int dz = -R; dz <= R; dz++) for (int dx = -R; dx <= R; dx++)
		{
			double d = Math.Sqrt(dx * dx + dz * dz) / R;
			if (d > 1) continue;
			int capTop = y + H + (int)Math.Round((1 - d * d) * 3, MidpointRounding.AwayFromZero);
			for (int yy = y + H - 1 + (int)Math.Round(d * 2, MidpointRounding.AwayFromZero); yy <= capTop; yy++) Put(x + dx, yy, z + dz, RAW);
		}
	}
	void BalancedBoulder(int x, int y, int z, double v)
	{
		int H = 5 + (int)Math.Floor(v * 10), R = 3 + (int)Math.Floor(Hash(x, z, 41) * 3);
		for (int yy = y + 1; yy <= y + H; yy++) Put(x, yy, z, 3);
		int cy = y + H + R - 1;
		for (int dy = -R; dy <= R; dy++) for (int dz = -R; dz <= R; dz++) for (int dx = -R; dx <= R; dx++)
			if (dx * dx + dy * dy + dz * dz <= R * R + 1) Put(x + dx, cy + dy, z + dz, RAW);
	}
	void StoneRing(int x, int y, int z, double v)
	{
		int R = 7 + (int)Math.Floor(v * 8); bool alongX = Hash(x, z, 77) < 0.5; int cy = y + R - 3;
		for (int a = -R - 2; a <= R + 2; a++) for (int b = -R - 2; b <= R + 2; b++)
		{
			if (Math.Abs(Math.Sqrt(a * a + b * b) - R) > 1.7) continue;
			for (int t = -1; t <= 1; t++) Put(alongX ? x + a : x + t, cy + b, alongX ? z + t : z + a, 3);
		}
	}
	void FloatingCubes(int x, int y, int z, double v)
	{
		int n = 3 + (int)Math.Floor(v * 5);
		for (int k = 0; k < n; k++)
		{
			int s = 2 + (int)Math.Floor(Hash(x + k, z, 51) * 3);
			int ox = x + (int)Math.Floor((Hash(x, z + k, 52) - 0.5) * 20), oz = z + (int)Math.Floor((Hash(x + k, z + k, 53) - 0.5) * 20);
			int oy = y + 10 + (int)Math.Floor(Hash(k, x + z, 54) * 30);
			for (int dy = 0; dy < s; dy++) for (int dz = 0; dz < s; dz++) for (int dx = 0; dx < s; dx++) Put(ox + dx, oy + dy, oz + dz, RAW);
		}
	}

	// --- Paint the land: grass on top, then dirt, then stone; sand, gravel and clay by the water ---
	void Paint()
	{
		System.Threading.Tasks.Parallel.For(0, WZ, z =>
		{
			for (int x = 0; x < WX; x++)
			{
				int depth = -1, surf = 1;
				double beach = gen == 1 ? 0 : Fbm(x / 40.0, z / 40.0, 2, 950), gravelly = gen == 1 ? 0 : Fbm(x / 30.0 + 70, z / 30.0 + 70, 2, 960);
				double clayey = gen == 1 ? 0 : Fbm(x / 14.0 + 140, z / 14.0 + 140, 2, 975);
				for (int y = WY - 1; y >= 0; y--)
				{
					int i = Idx(x, y, z);
					if (world[i] == 0) { depth = -1; continue; }
					depth++;
					if (world[i] != RAW) continue;
					if (gen == 1)
					{
						bool low = y <= SeaLevel;
						world[i] = (byte)(depth == 0 ? (low ? 6 : 1) : depth <= 3 ? (low ? 6 : 2) : 3);
						continue;
					}
					if (depth == 0)
					{
						if (y > SeaLevel + 1) surf = 1;
						else if (y < SeaLevel && y >= SeaLevel - 8 && clayey > 0.62) surf = 63;
						else if (gravelly > 0.6) surf = 11;
						else if (y >= SeaLevel - 4) surf = beach > 0.4 ? 6 : y >= SeaLevel ? 1 : 2;
						else surf = 6;
					}
					world[i] = (byte)(depth == 0 ? surf : depth <= 3 ? (surf == 1 ? 2 : surf) : 3);
				}
			}
		});
	}

	// --- Gravel: rounded clumps scattered through the stone ---
	void Gravel()
	{
		const int PATCHES = 10000;
		for (int i = 0, tries = 0; i < PATCHES && tries < PATCHES * 20; tries++)
		{
			int x = (int)Math.Floor(Hash(tries, 1, 7001) * WX), z = (int)Math.Floor(Hash(tries, 2, 7002) * WZ);
			int y = 1 + (int)Math.Floor(Hash(tries, 3, 7003) * (WY - 2));
			if (world[Idx(x, y, z)] != 3) continue;
			i++;
			double rx = 1.5 + Hash(tries, 4, 7004) * 2.5, ry = 1 + Hash(tries, 5, 7005) * 1.5, rz = 1.5 + Hash(tries, 6, 7006) * 2.5;
			for (int dy = -(int)Math.Ceiling(ry); dy <= Math.Ceiling(ry); dy++)
				for (int dz = -(int)Math.Ceiling(rz); dz <= Math.Ceiling(rz); dz++)
					for (int dx = -(int)Math.Ceiling(rx); dx <= Math.Ceiling(rx); dx++)
					{
						if (Math.Pow(dx / rx, 2) + Math.Pow(dy / ry, 2) + Math.Pow(dz / rz, 2) > 1) continue;
						int gx = x + dx, gy = y + dy, gz = z + dz;
						if (gx < 0 || gz < 0 || gx >= WX || gz >= WZ || gy < 1 || gy >= WY) continue;
						if (world[Idx(gx, gy, gz)] == 3) world[Idx(gx, gy, gz)] = 11;
					}
		}
	}

	// --- Caves (hills worlds): winding "worm" tunnels, never cutting right next to water ---
	void CarveCaves()
	{
		int n = WX * WZ / 400;
		void Carve(double cx, double cy, double cz, double r)
		{
			int R = (int)Math.Ceiling(r);
			for (int dy = -R; dy <= R; dy++) for (int dz = -R; dz <= R; dz++) for (int dx = -R; dx <= R; dx++)
			{
				if (dx * dx + dy * dy * 1.6 + dz * dz > r * r) continue;
				int x = (int)Math.Floor(cx) + dx, y = (int)Math.Floor(cy) + dy, z = (int)Math.Floor(cz) + dz;
				if (x < 1 || z < 1 || x >= WX - 1 || z >= WZ - 1 || y < 2 || y >= WY - 1) continue;
				int i = Idx(x, y, z); int b = world[i];
				if (b == 0 || Blocks.IsWater(b)) continue;
				if (Blocks.IsWater(world[i + 1]) || Blocks.IsWater(world[i - 1]) || Blocks.IsWater(world[i + WX]) || Blocks.IsWater(world[i - WX]) ||
					Blocks.IsWater(world[Idx(x, y + 1, z)]) || Blocks.IsWater(world[Idx(x, y - 1, z)])) continue;
				world[i] = 0;
			}
		}
		for (int k = 0; k < n; k++)
		{
			double x = Hash(k, 1, 5001) * WX, z = Hash(k, 2, 5002) * WZ, y = 6 + Hash(k, 3, 5003) * (SeaLevel + 12);
			double yaw = Hash(k, 4, 5004) * Math.PI * 2, pitch = (Hash(k, 5, 5005) - 0.5) * 0.5;
			double len = 60 + Hash(k, 6, 5006) * 140, r0 = 1.3 + Hash(k, 7, 5007) * 1.9;
			for (int s = 0; s < len; s++)
			{
				Carve(x, y, z, r0 * (0.6 + 0.6 * Math.Sin(Math.PI * s / len)));
				x += Math.Cos(yaw) * Math.Cos(pitch); z += Math.Sin(yaw) * Math.Cos(pitch); y += Math.Sin(pitch);
				yaw += (Hash(k, s, 5008) - 0.5) * 0.45;
				pitch = pitch * 0.85 + (Hash(k, s, 5009) - 0.5) * 0.35;
			}
		}
	}

	// --- Trees: normal, lollipop, tall spindly, and big puffballs ---
	void LeafBall(int cx, int cy, int cz, double r)
	{
		int R = (int)Math.Ceiling(r);
		for (int dy = -R; dy <= R; dy++) for (int dz = -R; dz <= R; dz++) for (int dx = -R; dx <= R; dx++)
			if (dx * dx + dy * dy + dz * dz <= r * r) Put(cx + dx, cy + dy, cz + dz, 5, true);
	}
	void Trunk(int x, int y, int z, int h) { for (int yy = y + 1; yy <= y + h; yy++) Put(x, yy, z, 4, true); }
	double ForestAt(int x, int z) => Smoothstep(0.47, 0.6, Fbm(x / 110.0 + 20, z / 110.0 + 20, 2, 970));
	void Trees()
	{
		for (int z = 3; z < WZ - 3; z++) for (int x = 3; x < WX - 3; x++)
		{
			int h = W.Heights[z * WX + x];
			if (world[Idx(x, h, z)] != 1 || Hash(x, z, 999) > (gen == 1 ? 0.011 : 0.003 + ForestAt(x, z) * 0.045)) continue;
			double kind = gen == 1 ? Hash(x, z, 555) : Hash(x, z, 555) < 0.93 ? 0 : 0.95, v = Hash(x, z, 77);
			if (kind < 0.45)
			{
				int th = 4 + (int)(v * 3);
				for (int dy = -2; dy <= 1; dy++)
				{
					int r = dy >= 0 ? 1 : 2, ly = h + th + dy;
					for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++)
					{
						if (Math.Abs(dx) == r && Math.Abs(dz) == r && Hash(x + dx, z + dz, ly) < 0.6) continue;
						Put(x + dx, ly, z + dz, 5, true);
					}
				}
				Trunk(x, h, z, th);
			}
			else if (kind < 0.75) { int th = 5 + (int)(v * 4); Trunk(x, h, z, th); LeafBall(x, h + th + 2, z, 2.6); }
			else if (kind < 0.92) { int th = 10 + (int)(v * 8); Trunk(x, h, z, th); LeafBall(x, h + th + 1, z, 1.6); LeafBall(x, h + (int)Math.Floor(th * 0.6), z, 1.2); }
			else { Trunk(x, h, z, 3); LeafBall(x, h + 7, z, 4.5); }
		}
	}

	// --- Rocks: lying on dry sand and gravel beaches, close to the water ---
	void Rocks()
	{
		const double CHANCE = 0.06;
		for (int z = 6; z < WZ - 6; z++) for (int x = 6; x < WX - 6; x++)
		{
			if (Hash(x, z, 4321) > CHANCE) continue;
			int h = W.Heights[z * WX + x], top = world[Idx(x, h, z)];
			if ((top != 6 && top != 11) || h < SeaLevel || world[Idx(x, h + 1, z)] != 0) continue;
			bool wet = false;
			for (int dz = -5; dz <= 5 && !wet; dz++) for (int dx = -5; dx <= 5; dx++) if (Blocks.IsWater(world[Idx(x + dx, SeaLevel, z + dz)])) { wet = true; break; }
			if (wet) { world[Idx(x, h + 1, z)] = Blocks.RockBlock; W.Heights[z * WX + x] = h + 1; }
		}
	}
}
