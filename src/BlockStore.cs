using System;
using System.Threading.Tasks;

namespace Cubeland;

// The world's blocks, kept column by column. A column is 16 x 16 blocks across and the world's full height, made of chunks
// (16x16x16 cubes) from the bottom up. Each chunk has a palette: the list of the block ids in it, as full numbers (so there's
// no limit of 256 kinds of block), and each of its 4096 blocks stores only its place in that list: 1 byte, or 2 bytes once a
// chunk has held more than 256 kinds of block. A chunk that's all one block (open sky, solid rock) shares one list of places
// with every other such chunk until something in it changes, so it costs almost nothing. Blocks are found by world index
// (World.Idx) or by x, y, z, the same as when the world was one big list.
// (Columns are what a world that grows as players come near will grow and drop one at a time; the world types so far grow
// every column at once, up front, so every column is always there.)
public sealed class BlockStore
{
	// (Readers on other threads, like the chunk builders, may read a chunk while the game changes it: a chunk only ever
	// grows its palette, writing a new entry before any block points at it, and switching to 2-byte places makes a new
	// Chunk, so a reader always sees a block id that was really there.)
	sealed class Chunk { public int[] Pal; public int N; public byte[] B; public ushort[] U; }
	sealed class Column { public readonly Chunk[] Cubes; public Column(int n) { Cubes = new Chunk[n]; } }   // (its chunks, bottom up)
	static readonly byte[] Shared = new byte[4096];   // the places of a chunk that's all one block (never written)
	readonly Column[] cols;   // (column (cx, cz) is cols[cz * ncx + cx])
	readonly int wx, wz, ncx, ncz, ncy;
	public long Length { get; }
	public const int CS = 16, Wide = 256;   // (a chunk with more kinds of block than Wide switches to 2-byte places)

	public BlockStore(int wx, int wy, int wz)
	{
		this.wx = wx; this.wz = wz; ncx = wx / CS; ncz = wz / CS; ncy = wy / CS;
		Length = (long)wx * wy * wz;
		cols = new Column[ncx * ncz];
		for (int k = 0; k < cols.Length; k++)
		{
			var c = new Column(ncy);
			for (int cy = 0; cy < ncy; cy++) c.Cubes[cy] = new Chunk { Pal = new int[1], N = 1, B = Shared };
			cols[k] = c;
		}
	}

	Chunk[] CubesAt(int x, int z) => cols[(z >> 4) * ncx + (x >> 4)].Cubes;
	static int Local(int x, int y, int z) => ((y & 15) << 8) | ((z & 15) << 4) | (x & 15);

	public int Get(int x, int y, int z)
	{
		var c = CubesAt(x, z)[y >> 4]; int l = Local(x, y, z);
		return c.U != null ? c.Pal[c.U[l]] : c.Pal[c.B[l]];
	}
	public void Set(int x, int y, int z, int id)
	{
		var cubes = CubesAt(x, z); int cy = y >> 4, l = Local(x, y, z);
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
		get { long r = i / wx; return Get((int)(i - r * wx), (int)(r / wz), (int)(r % wz)); }
		set { long r = i / wx; Set((int)(i - r * wx), (int)(r / wz), (int)(r % wz), value); }
	}

	// fills every chunk from a freshly grown world (one byte per block, in world-index order)
	public void Load(byte[] raw)
	{
		int ncxz = ncx * ncz;
		Parallel.For(0, ncxz * ncy, k =>
		{
			int cx = k % ncx, cz = (k / ncx) % ncz, cy = k / ncxz;
			Span<int> place = stackalloc int[256]; place.Fill(-1);
			var pal = new int[8]; int n = 0; var b = new byte[4096];
			for (int ly = 0; ly < CS; ly++) for (int lz = 0; lz < CS; lz++)
			{
				int row = ((cy * CS + ly) * wz + cz * CS + lz) * wx + cx * CS, l = (ly << 8) | (lz << 4);
				for (int lx = 0; lx < CS; lx++)
				{
					int id = raw[row + lx], p = place[id];
					if (p < 0)
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
