using System;
using System.Threading.Tasks;

namespace Cubeland;

// The world's blocks, kept column by column. A column is 16 x 16 blocks across and the world's full height, made of chunks
// (16x16x16 cubes) from the bottom up. Each chunk has a palette: the list of the block ids in it, as full numbers (so there's
// no limit of 256 kinds of block), and each of its 4096 blocks stores only its place in that list: 1 byte, or 2 bytes once a
// chunk has held more than 256 kinds of block. A chunk that's all one block (open sky, solid rock) shares one list of places
// with every other such chunk until something in it changes, so it costs almost nothing. Blocks are found by world index
// (World.Idx) or by x, y, z, the same as when the world was one big list.
// (Columns are what a world that grows as players come near grows and drops one at a time: Earthlike worlds (gen 4) start with
// every column not grown (the one shared Empty column: all air, and nothing can be put in it), then PutColumn grows one from
// its drawing and DropColumn lets it go again. The other world types grow every column at once, up front.)
public sealed class BlockStore
{
	// (Readers on other threads, like the chunk builders, may read a chunk while the game changes it: a chunk only ever
	// grows its palette, writing a new entry before any block points at it, and switching to 2-byte places makes a new
	// Chunk, so a reader always sees a block id that was really there.)
	public sealed class Chunk { public int[] Pal; public int N; public byte[] B; public ushort[] U; }
	public sealed class Column { public readonly Chunk[] Cubes; public Column(int n) { Cubes = new Chunk[n]; } }   // (its chunks, bottom up)
	readonly Column Empty;    // (gen 4: every column not grown is this one: all air, never written)
	static readonly byte[] Shared = new byte[4096];   // the places of a chunk that's all one block (never written)
	readonly Column[] cols;   // (column (cx, cz) is cols[cz * ncx + cx])
	readonly int wx, wz, ncx, ncz, ncy;
	public long Length { get; }
	public const int CS = 16, Wide = 256;   // (a chunk with more kinds of block than Wide switches to 2-byte places)

	// lazy: gen 4, every column starts not grown (see PutColumn)
	// (lazy, gen 4: every column starts as one shared column of `away` blocks, Bedrock: the land not grown yet reads as solid
	// rock, so water, falling blocks, light and mobs stop at its edge, and faces there aren't drawn)
	public BlockStore(int wx, int wy, int wz, bool lazy = false, int away = 0)
	{
		this.wx = wx; this.wz = wz; ncx = wx / CS; ncz = wz / CS; ncy = wy / CS;
		Length = (long)wx * wy * wz;
		cols = new Column[ncx * ncz];
		if (lazy) { Empty = AirColumn(away); Array.Fill(cols, Empty); return; }
		for (int k = 0; k < cols.Length; k++) cols[k] = AirColumn();
	}
	Column AirColumn(int id = 0)
	{
		var c = new Column(ncy);
		for (int cy = 0; cy < ncy; cy++) c.Cubes[cy] = new Chunk { Pal = new[] { id }, N = 1, B = Shared };
		return c;
	}
	// (lazy: what a spot in a column not grown reads as BY WORLD INDEX, the way the ticks read: the world gives its change
	// waiting in Edits, so a furnace, a crop or a torch isn't forgotten while its column is away. Main thread only.)
	public Func<long, int> Away;
	// gen 4: is column (cx, cz) grown? (always, in the other world types)
	public bool Grown(int cx, int cz) => cols[cz * ncx + cx] != Empty;
	// gen 4: grow column (cx, cz) from its drawing (EarthCol.Draw: cube by cube from the bottom, each (y & 15) << 8 | z << 4 | x).
	// The whole column is made first and then put in at once, so a reader on another thread sees the old one or the new one
	public void PutColumn(int cx, int cz, int[] blocks) => PutColumn(cx, cz, Pack(blocks, ncy));
	public void PutColumn(int cx, int cz, Column c) => cols[cz * ncx + cx] = c;
	// a drawn column's blocks (y, then z, then x) packed as the store keeps them: on any thread (the grower's workers)
	public static Column Pack(int[] blocks, int ncy)
	{
		var c = new Column(ncy);
		var map = new System.Collections.Generic.Dictionary<int, int>();
		for (int cy = 0; cy < ncy; cy++)
		{
			int o = cy * 4096, first = blocks[o]; bool one = true;
			for (int l = 1; l < 4096; l++) if (blocks[o + l] != first) { one = false; break; }
			if (one) { c.Cubes[cy] = new Chunk { Pal = new[] { first }, N = 1, B = Shared }; continue; }
			map.Clear(); var pal = new int[8]; int n = 0;
			for (int l = 0; l < 4096; l++) if (!map.ContainsKey(blocks[o + l])) { if (n == pal.Length) Array.Resize(ref pal, n * 2); map[blocks[o + l]] = n; pal[n++] = blocks[o + l]; }
			if (n <= Wide) { var b = new byte[4096]; for (int l = 0; l < 4096; l++) b[l] = (byte)map[blocks[o + l]]; c.Cubes[cy] = new Chunk { Pal = pal, N = n, B = b }; }
			else { var u = new ushort[4096]; for (int l = 0; l < 4096; l++) u[l] = (ushort)map[blocks[o + l]]; c.Cubes[cy] = new Chunk { Pal = pal, N = n, U = u }; }
		}
		return c;
	}
	// gen 4: let column (cx, cz) go (its blocks are the land's again, and its changes are kept in the world's Edits)
	public void DropColumn(int cx, int cz) { if (Empty != null) cols[cz * ncx + cx] = Empty; }

	Chunk[] CubesAt(int x, int z) => cols[(z >> 4) * ncx + (x >> 4)].Cubes;
	static int Local(int x, int y, int z) => ((y & 15) << 8) | ((z & 15) << 4) | (x & 15);

	public int Get(int x, int y, int z)
	{
		var c = CubesAt(x, z)[y >> 4]; int l = Local(x, y, z);
		return c.U != null ? c.Pal[c.U[l]] : c.Pal[c.B[l]];
	}
	public void Set(int x, int y, int z, int id)
	{
		var col = cols[(z >> 4) * ncx + (x >> 4)];
		if (col == Empty) return;   // (gen 4: a column not grown takes nothing: its changes wait in the world's Edits)
		var cubes = col.Cubes; int cy = y >> 4, l = Local(x, y, z);
		var c = cubes[cy];
		int p = Array.IndexOf(c.Pal, id, 0, c.N);
		if (c.B == Shared)
		{
			if (p == 0) return;   // (already that block)
			c = new Chunk { Pal = (int[])c.Pal.Clone(), N = c.N, B = new byte[4096] }; cubes[cy] = c;
		}
		if (p < 0) p = Add(cubes, cy, ref c, id);
		if (c.U != null) c.U[l] = (ushort)p; else c.B[l] = (byte)p;
	}
	static int Add(Chunk[] cubes, int cy, ref Chunk c, int id)
	{
		if (c.U == null && c.N == Wide)
		{
			var w = new Chunk { Pal = c.Pal, N = c.N, U = new ushort[4096] };
			for (int i = 0; i < 4096; i++) w.U[i] = c.B[i];
			cubes[cy] = c = w;
		}
		if (c.N == ushort.MaxValue + 1) throw new InvalidOperationException("Too many kinds of block in one chunk");
		if (c.N == c.Pal.Length) { var np = new int[c.N * 2]; Array.Copy(c.Pal, np, c.N); np[c.N] = id; c.Pal = np; }
		else c.Pal[c.N] = id;
		return c.N++;
	}

	// by world index: (y * WZ + z) * WX + x (64-bit, like World.Idx)
	public int this[long i]
	{
		get
		{
			long r = i / wx; int x = (int)(i - r * wx), y = (int)(r / wz), z = (int)(r % wz);
			if (Away != null && cols[(z >> 4) * ncx + (x >> 4)] == Empty) return Away(i);
			return Get(x, y, z);
		}
		set { long r = i / wx; Set((int)(i - r * wx), (int)(r / wz), (int)(r % wz), value); }
	}

	// fills every chunk from a freshly grown world (one byte per block, in world-index order)
	// over (lands 7 and 8: the rock blobs): for a chunk (its low corner x0, y0, z0), fills a 4096-block overlay (index
	// (ly << 8) | (lz << 4) | lx) with the ids that replace the grown bytes, 0 where the byte stays
	public void Load(byte[] raw, Action<int, int, int, int[]> over = null)
	{
		int ncxz = ncx * ncz;
		Parallel.For(0, ncxz * ncy, k =>
		{
			int cx = k % ncx, cz = (k / ncx) % ncz, cy = k / ncxz;
			Span<int> place = stackalloc int[256]; place.Fill(-1);
			var pal = new int[8]; int n = 0; var b = new byte[4096];
			int[] ov = null;
			if (over != null) { ov = new int[4096]; over(cx * CS, cy * CS, cz * CS, ov); }
			for (int ly = 0; ly < CS; ly++) for (int lz = 0; lz < CS; lz++)
			{
				int row = ((cy * CS + ly) * wz + cz * CS + lz) * wx + cx * CS, l = (ly << 8) | (lz << 4);
				for (int lx = 0; lx < CS; lx++)
				{
					int id = raw[row + lx], p;
					if (ov != null && ov[l | lx] != 0)
					{   // (an id over 255: found in the palette, or added)
						id = ov[l | lx]; p = -1;
						for (int q = 0; q < n; q++) if (pal[q] == id) { p = q; break; }
						if (p < 0) { if (n == pal.Length) Array.Resize(ref pal, n * 2); pal[n] = id; p = n++; }
					}
					else if ((p = place[id]) < 0)
					{
						if (n == pal.Length) Array.Resize(ref pal, n * 2);
						pal[n] = id; place[id] = p = n++;
					}
					b[l | lx] = (byte)p;
				}
			}
			cols[cz * ncx + cx].Cubes[cy] = n == 1 ? new Chunk { Pal = new[] { pal[0] }, N = 1, B = Shared } : new Chunk { Pal = pal, N = n, B = b };
		});
	}

	// (for testing) how much memory the blocks take, in bytes, and how many chunks are all one block
	public (long bytes, int uniform) Usage()
	{
		long bytes = 0; int uniform = 0;
		foreach (var col in cols)
		{
			bytes += 24 + col.Cubes.Length * 8;
			foreach (var c in col.Cubes)
			{
				bytes += 48 + c.Pal.Length * 4;
				if (c.B == Shared) uniform++; else bytes += c.U != null ? 8192 : 4096;
			}
		}
		return (bytes, uniform);
	}
}
