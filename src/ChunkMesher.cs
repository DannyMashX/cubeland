using System;
using System.Collections.Generic;
using Godot;

namespace Cubeland;

// Turns one 16x16x16 cube of the world (a chunk) into meshes: one for the solid blocks, one for the water.
// Each corner carries: its shading (corner shadows and face direction, in COLOR), and its torch light and
// sky light (in UV2), which the shader mixes with the time of day.
public class ChunkMesher
{
	// the 6 faces of a cube: direction, brightness, and 4 corners [x, y, z, u, v]
	public struct Face { public int[] Dir; public float Shade; public int[][] C; }
	public static readonly Face[] Faces =
	{
		new() { Dir = new[] { -1, 0, 0 }, Shade = 0.8f,  C = new[] { new[] { 0,1,0,0,1 }, new[] { 0,0,0,0,0 }, new[] { 0,1,1,1,1 }, new[] { 0,0,1,1,0 } } },
		new() { Dir = new[] {  1, 0, 0 }, Shade = 0.8f,  C = new[] { new[] { 1,1,1,0,1 }, new[] { 1,0,1,0,0 }, new[] { 1,1,0,1,1 }, new[] { 1,0,0,1,0 } } },
		new() { Dir = new[] { 0, -1, 0 }, Shade = 0.5f,  C = new[] { new[] { 1,0,1,1,0 }, new[] { 0,0,1,0,0 }, new[] { 1,0,0,1,1 }, new[] { 0,0,0,0,1 } } },
		new() { Dir = new[] { 0,  1, 0 }, Shade = 1.0f,  C = new[] { new[] { 0,1,1,1,1 }, new[] { 1,1,1,0,1 }, new[] { 0,1,0,1,0 }, new[] { 1,1,0,0,0 } } },
		new() { Dir = new[] { 0, 0, -1 }, Shade = 0.65f, C = new[] { new[] { 1,0,0,0,0 }, new[] { 0,0,0,1,0 }, new[] { 1,1,0,0,1 }, new[] { 0,1,0,1,1 } } },
		new() { Dir = new[] { 0, 0,  1 }, Shade = 0.65f, C = new[] { new[] { 0,0,1,0,0 }, new[] { 1,0,1,1,0 }, new[] { 0,1,1,0,1 }, new[] { 1,1,1,1,1 } } },
	};
	// Corner shadows: how bright a face's corner is with 3 (or 2 at the sides), 2, 1 or no blocks round it in front of the face.
	// The darkest is 0.4 (it was 0.25); the others are the old levels (0.25, 0.45, 0.6, 1) with every corner's shadow
	// (1 - level) shrunk by the same amount, so they still step evenly: 0.4, 0.56, 0.68, 1. (The same as the web
	// version's AO_DARKEST and AO_LEVELS.) (test: --aodarkest=N draws with another darkest level, to compare pictures)
	public const float AoDarkest = 0.4f;
	static readonly float[] AoLevels = AoLevelsFor(TestAoDarkest() ?? AoDarkest);
	public static float[] AoLevelsFor(float darkest)
	{
		float k = (1 - darkest) / 0.75f;
		return new[] { 1 - 0.75f * k, 1 - 0.55f * k, 1 - 0.4f * k, 1f };
	}
	static float? TestAoDarkest()
	{
		foreach (var a in OS.GetCmdlineUserArgs())
			if (a.StartsWith("--aodarkest=") && float.TryParse(a[12..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float d)) return d;
		return null;
	}

	public class Part
	{
		public readonly List<Vector3> Pos = new();
		public readonly List<Vector2> Uv = new(), Uv2 = new();
		public readonly List<Color> Col = new();
		public readonly List<int> Ind = new();
		// (water, since Beta 0.1.6.0: each corner's slide, x y = where it is in the picture, in blocks, z w = how fast the
		// picture moves, blocks a second, along its columns and rows; 0, 0 = not sliding. Null in a part with no water)
		public List<Vector4> Slide;
		public void V(float x, float y, float z, Vector2 uv, float shade, float torch, float sky, Vector4 slide)
		{
			(Slide ??= new()).Add(slide); V(x, y, z, uv, shade, torch, sky);
		}
		// (COLOR: the shading times the biome tint; its alpha holds the plain shading, which the shader uses for every pixel that isn't green)
		public void V(float x, float y, float z, Vector2 uv, float shade, float torch, float sky) => V(x, y, z, uv, shade, torch, sky, new Color(1, 1, 1));
		public void V(float x, float y, float z, Vector2 uv, float shade, float torch, float sky, Color tint)
		{
			Pos.Add(new Vector3(x, y, z)); Uv.Add(uv); Col.Add(new Color(shade * tint.R, shade * tint.G, shade * tint.B, shade)); Uv2.Add(new Vector2(torch, sky));
		}
		// (the web version's triangles, wound the other way round for Godot)
		public void Quad(int b) { Ind.Add(b); Ind.Add(b + 2); Ind.Add(b + 1); Ind.Add(b + 2); Ind.Add(b + 3); Ind.Add(b + 1); }
		public void QuadFlipped(int b) { Ind.Add(b); Ind.Add(b + 3); Ind.Add(b + 1); Ind.Add(b); Ind.Add(b + 2); Ind.Add(b + 3); }
		// several chunks' pieces glued into one (for drawing a whole group at once)
		public static Part Join(List<Part> ps)
		{
			var j = new Part();
			bool slides = false; foreach (var p in ps) slides |= p.Slide != null;
			foreach (var p in ps)
			{
				int b = j.Pos.Count; j.Pos.AddRange(p.Pos); j.Uv.AddRange(p.Uv); j.Uv2.AddRange(p.Uv2); j.Col.AddRange(p.Col); foreach (int i in p.Ind) j.Ind.Add(i + b);
				if (slides) { j.Slide ??= new(); if (p.Slide != null) j.Slide.AddRange(p.Slide); else for (int k = 0; k < p.Pos.Count; k++) j.Slide.Add(Vector4.Zero); }
			}
			return j;
		}
		public ArrayMesh ToMesh()
		{
			if (Ind.Count == 0) return null;
			var arr = new Godot.Collections.Array();
			arr.Resize((int)Mesh.ArrayType.Max);
			arr[(int)Mesh.ArrayType.Vertex] = Pos.ToArray();
			arr[(int)Mesh.ArrayType.TexUV] = Uv.ToArray();
			arr[(int)Mesh.ArrayType.TexUV2] = Uv2.ToArray();
			arr[(int)Mesh.ArrayType.Color] = Col.ToArray();
			arr[(int)Mesh.ArrayType.Index] = Ind.ToArray();
			var m = new ArrayMesh();
			if (Slide == null) { m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr); return m; }
			var cu = new float[Slide.Count * 4];   // (CUSTOM0 in the water shader, four floats a corner)
			for (int i = 0; i < Slide.Count; i++) { cu[4 * i] = Slide[i].X; cu[4 * i + 1] = Slide[i].Y; cu[4 * i + 2] = Slide[i].Z; cu[4 * i + 3] = Slide[i].W; }
			arr[(int)Mesh.ArrayType.Custom0] = cu;
			m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr, null, null, (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift));
			return m;
		}
	}

	readonly World W;
	public ChunkMesher(World w) { W = w; }
	int Occ(int x, int y, int z) { int b = W.GetBlock(x, y, z); return b != 0 && !Blocks.B[b].Transparent ? 1 : 0; }

	public (Part solid, Part water) Build(int cx, int cy, int cz)
	{
		var P = new Part(); var Wt = new Part();
		int y0 = cy * World.CS, top = Math.Min(y0 + World.CS, W.ChunkTop[cz * World.NCX + cx]);   // (nothing above the column's highest block)
		bool anyLight = W.LightMap.Count > 0;
		var sky = new Dictionary<long, float>();
		var p = new int[3]; var q = new int[3]; var ao = new int[4];
		var tints = new Dictionary<int, Color>();   // grass and leaves: the biome colour at each corner (x, z)
		Color TintAt(int x, int z) { int k = x * 65536 + z; if (!tints.TryGetValue(k, out var c)) tints[k] = c = W.Gen.GrassTint(x, z); return c; }
		var white = new Color(1, 1, 1);
		for (int y = y0; y < top; y++) for (int z = cz * World.CS; z < cz * World.CS + World.CS; z++) for (int x = cx * World.CS; x < cx * World.CS + World.CS; x++)
		{
			int id = W.Blocks.Get(x, y, z);
			if (id == 0) continue;
			var b = Blocks.B[id];
			if (b.Snowed > 0 && b.Shape is "torch" or "plant" or "flat")
			{   // snow-logged: the torch, flower or rock (lying on top of the snow), then its layer of snow is drawn below like any snow
				if (b.Shape == "torch") AddTorch(x, y, z, id, P, anyLight, sky); else if (b.Shape == "plant") AddPlant(x, y, z, b, P, anyLight, sky); else AddFlat(x, y, z, b, P, anyLight, sky, 1 / 8f);
				id = Blocks.Snow; b = Blocks.B[id];
			}
			else if (b.Shape == "torch") { AddTorch(x, y, z, id, P, anyLight, sky); continue; }
			if (b.Shape == "flat") { AddFlat(x, y, z, b, P, anyLight, sky); continue; }
			if (b.Shape == "door")
			{   // (a door swinging open or shut is drawn by Main while it swings)
				if (!Swinging.ContainsKey(World.Idx(x, y, z)))
				{
					var (pv, pw, pn) = DoorPose(b.Facing, b.Hinge, b.Open == 1);
					DoorPanel(P, b, new Vector3(x, y, z), pv, pw, pn, anyLight ? World.LightCurve(W.LightAt(x, y, z)) : 0, W.SkyExposure(x, y, z, sky));
				}
				continue;
			}
			if (b.Shape == "plant") { AddPlant(x, y, z, b, P, anyLight, sky); continue; }
			if (b.Shape == "fence") { AddFence(x, y, z, b, P, anyLight, sky); continue; }
			if (b.Liquid) { AddWater(x, y, z, id, Wt, anyLight, sky); continue; }
			if (Blocks.IsPieces(id)) { AddPieces(x, y, z, P, anyLight, sky, TintAt); if (id == Blocks.SnowyPieces) AddSnowUnder(x, y, z, P, anyLight, sky); continue; }
			if (b.Shape == "half")   // (drawn as its 4 bottom pieces, and the Cutter's saw blade on top; in snow, the snow is all under it)
			{
				AddPieces(x, y, z, P, anyLight, sky, TintAt, new[] { id, id, 0, 0, id, id, 0, 0 });
				if (Blocks.Plain(id) == Blocks.Cutter) AddSaw(x, y, z, P, anyLight, sky);
				continue;
			}
			float depth = 0.75f + 0.25f * Math.Min(1, y / 20f);   // a little darker deep down
			bool tinted = Blocks.TakesBiomeTint(id);
			float h = b.Shape == "snow" ? b.Layers / 8f : 1;   // (snow: only as deep as its layers)
			foreach (var f in Faces)
			{
				int n = W.GetBlock(x + f.Dir[0], y + f.Dir[1], z + f.Dir[2]);
				if ((h == 1 || f.Dir[1] <= 0) && n != 0 && (n == id || !Blocks.B[n].Transparent || (id == Blocks.Snow && f.Dir[1] == 0 && Blocks.SnowIn(n) > 0 && !Blocks.IsPieces(n)))) continue;   // (one layer of snow next to a torch or flower in snow: the snow carries on)
				// a lying log: its ends (the top picture) face along its axis, and its bark is turned to run that way
				int tile = b.Axis == 0 ? f.Dir[1] > 0 ? b.T[1] : f.Dir[1] < 0 ? b.T[2] : b.Front != null && f.Dir[0] == b.Front[0] && f.Dir[2] == b.Front[1] ? b.T[3] : b.T[0]
					: (b.Axis == 1 ? f.Dir[0] != 0 : f.Dir[2] != 0) ? b.T[1] : b.T[0];
				bool turn = b.Axis == 1 ? f.Dir[0] == 0 : b.Axis == 2 && f.Dir[0] != 0;
				int bas = P.Pos.Count;
				int na = f.Dir[0] != 0 ? 0 : f.Dir[1] != 0 ? 1 : 2;
				int ta = na == 0 ? 1 : 0, tb = na == 2 ? 1 : 2;
				for (int i = 0; i < 4; i++)
				{
					var c = f.C[i];
					// check the 3 blocks touching this corner, in front of the face (corner shadows)
					p[0] = x + f.Dir[0]; p[1] = y + f.Dir[1]; p[2] = z + f.Dir[2];
					int sa = c[ta] != 0 ? 1 : -1, sb = c[tb] != 0 ? 1 : -1;
					p[ta] += sa; int s1 = Occ(p[0], p[1], p[2]);
					p[tb] += sb; int s3 = Occ(p[0], p[1], p[2]);
					p[ta] -= sa; int s2 = Occ(p[0], p[1], p[2]);
					ao[i] = s1 == 1 && s2 == 1 ? 0 : 3 - (s1 + s2 + s3);
					float shade = f.Shade * depth * AoLevels[ao[i]];
					// torch light and sky light: the average of the open cells around this corner, in front of the face
					int fx = x + f.Dir[0], fy = y + f.Dir[1], fz = z + f.Dir[2];
					float sum = anyLight ? W.LightAt(fx, fy, fz) : 0, skySum = W.SkyExposure(fx, fy, fz, sky); int cnt = 1;
					q[0] = fx; q[1] = fy; q[2] = fz;
					q[ta] += sa; if (s1 == 0) { if (anyLight) sum += W.LightAt(q[0], q[1], q[2]); skySum += W.SkyExposure(q[0], q[1], q[2], sky); cnt++; }
					q[tb] += sb; if (s3 == 0 && !(s1 == 1 && s2 == 1)) { if (anyLight) sum += W.LightAt(q[0], q[1], q[2]); skySum += W.SkyExposure(q[0], q[1], q[2], sky); cnt++; }
					q[ta] -= sa; if (s2 == 0) { if (anyLight) sum += W.LightAt(q[0], q[1], q[2]); skySum += W.SkyExposure(q[0], q[1], q[2], sky); cnt++; }
					float cv = f.Dir[1] == 0 ? c[4] * h : c[4];
					P.V(x + c[0], y + c[1] * h, z + c[2], turn ? Atlas.UV(tile, cv, c[3]) : Atlas.UV(tile, c[3], cv), shade, anyLight ? World.LightCurve(sum / cnt) : 0, skySum / cnt, tinted ? TintAt(x + c[0], z + c[2]) : white);
				}
				// pick the quad diagonal that keeps the shading smooth
				if (ao[0] + ao[3] > ao[1] + ao[2]) P.QuadFlipped(bas); else P.Quad(bas);
			}
		}
		return (P, Wt);
	}

	// ---- torches: 4-pixel-wide sticks; wall torches lean 22.5 degrees away from their wall ----
	const float TorchTilt = 22.5f * MathF.PI / 180;
	static readonly float[][][] StandSpots =
	{
		new[] { new[] { 0f, 0f } }, new[] { new[] { -0.19f, -0.19f }, new[] { 0.19f, 0.19f } },
		new[] { new[] { -0.19f, -0.19f }, new[] { 0.19f, -0.19f }, new[] { 0f, 0.19f } },
		new[] { new[] { -0.19f, -0.19f }, new[] { 0.19f, -0.19f }, new[] { -0.19f, 0.19f }, new[] { 0.19f, 0.19f } },
	};
	static readonly float[][] WallSpots = { new[] { 0f }, new[] { -0.2f, 0.2f }, new[] { -0.3f, 0, 0.3f }, new[] { -0.375f, -0.125f, 0.125f, 0.375f } };
	public class TorchShape { public Vector3[][][] Sticks; public float[] Box; }   // sticks[stick][face][corner]
	static readonly Dictionary<int, TorchShape> torchCache = new();
	public static TorchShape TorchGeo(int id)
	{
		lock (torchCache)
		{
			if (torchCache.TryGetValue(id, out var have)) return have;
			var b = Blocks.B[id]; var w = b.Wall; float lo = 6 / 16f, hi = 10 / 16f;
			float ct = MathF.Cos(TorchTilt), st = MathF.Sin(TorchTilt);
			var spots = new List<float[]>();
			if (w != null) foreach (float o in WallSpots[b.Count - 1]) spots.Add(new[] { -w[1] * o, w[0] * o });
			else spots.AddRange(StandSpots[b.Count - 1]);
			var box = new float[] { 1, 1, 1, 0, 0, 0 };
			var sticks = new Vector3[spots.Count][][];
			for (int si = 0; si < spots.Count; si++)
			{
				sticks[si] = new Vector3[6][];
				for (int fi = 0; fi < 6; fi++)
				{
					sticks[si][fi] = new Vector3[4];
					for (int ci = 0; ci < 4; ci++)
					{
						var c = Faces[fi].C[ci];
						float ox = (c[0] != 0 ? hi : lo) - 0.5f, oy = c[1], oz = (c[2] != 0 ? hi : lo) - 0.5f;
						float px = 0.5f + ox, py = oy, pz = 0.5f + oz;
						if (w != null)
						{
							float lx = -w[0], lz = -w[1];
							float a = ox * lx + oz * lz;
							float qx = ox - a * lx, qz = oz - a * lz;
							float a2 = a * ct + oy * st, y2 = -a * st + oy * ct;
							float d = 0.5f - 0.125f * ct;
							float bx = 0.5f + w[0] * d, bz = 0.5f + w[1] * d;
							px = bx + qx + a2 * lx; py = 0.028f + y2; pz = bz + qz + a2 * lz;
						}
						px += spots[si][0]; pz += spots[si][1];
						box[0] = Math.Min(box[0], px); box[1] = Math.Min(box[1], py); box[2] = Math.Min(box[2], pz);
						box[3] = Math.Max(box[3], px); box[4] = Math.Max(box[4], py); box[5] = Math.Max(box[5], pz);
						sticks[si][fi][ci] = new Vector3(px, py, pz);
					}
				}
			}
			return torchCache[id] = new TorchShape { Sticks = sticks, Box = box };
		}
	}
	void AddTorch(int x, int y, int z, int id, Part P, bool anyLight, Dictionary<long, float> sky)
	{
		var b = Blocks.B[id]; float lo = 6 / 16f, hi = 10 / 16f;
		// a lit torch is always fully lit; a smoldering one glows dimly; a burnt one is lit like anything else around it
		float own = b.Burn == 2 ? (anyLight ? World.LightCurve(W.LightAt(x, y, z)) : 0) : b.Burn == 1 ? 0.6f : 1;
		float skyHere = b.Burn == 2 ? W.SkyExposure(x, y, z, sky) : 1;
		foreach (var stick in TorchGeo(id).Sticks)
			for (int fi = 0; fi < 6; fi++)
			{
				var f = Faces[fi];
				int tile = f.Dir[1] > 0 ? b.T[1] : f.Dir[1] < 0 ? b.T[2] : b.T[0];
				int bas = P.Pos.Count; bool flat = f.Dir[1] != 0;
				for (int ci = 0; ci < 4; ci++)
				{
					var c = f.C[ci]; var pt = stick[fi][ci];
					float u = c[3] != 0 ? hi : lo, v = flat ? (c[4] != 0 ? hi : lo) : c[4];
					P.V(x + pt.X, y + pt.Y, z + pt.Z, Atlas.UV(tile, u, v), 0.8f + 0.2f * f.Shade, own, skyHere);
				}
				P.Quad(bas);
			}
	}
	// ---- doors: a 4-pixel panel. Its pose: where its hinge is (in its block, x and z), the way it reaches from the hinge to its
	// free edge (w), and the way its front faces (n). Its big faces show its picture with the picture's left edge at the hinge
	// (so a door hinged on the right is mirrored, and its back is the mirror of its front); its edges show the planks ----
	public static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> Swinging = new();   // (doors Main draws while they swing)
	// (shut: across the middle of the block; open: turned a quarter about its hinge pin, reaching out of the block's back: see Blocks.DoorPose)
	public static (Vector2 pivot, Vector2 w, Vector2 n) DoorPose(int facing, int hinge, bool open) => Blocks.DoorPose(facing, hinge, open);
	public static void DoorPanel(Part P, BlockDef b, Vector3 at, Vector2 pivot, Vector2 w, Vector2 n, float light, float sky)
	{
		const float T = 0.125f;   // (half its thickness)
		Vector3 Pt(float u, float y, float s) => at + new Vector3(pivot.X + u * w.X + s * n.X, y, pivot.Y + u * w.Y + s * n.Y);
		void Quad(Vector3 a, Vector3 b2, Vector3 c, Vector3 d, int tile, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, float shade)
		{   // (a, b2 along the bottom, c, d along the top; drawn from both sides)
			int bas = P.Pos.Count;
			P.V(a.X, a.Y, a.Z, Atlas.UV(tile, ua.X, ua.Y), shade, light, sky); P.V(b2.X, b2.Y, b2.Z, Atlas.UV(tile, ub.X, ub.Y), shade, light, sky);
			P.V(c.X, c.Y, c.Z, Atlas.UV(tile, uc.X, uc.Y), shade, light, sky); P.V(d.X, d.Y, d.Z, Atlas.UV(tile, ud.X, ud.Y), shade, light, sky);
			P.Quad(bas); P.Ind.Add(bas); P.Ind.Add(bas + 1); P.Ind.Add(bas + 2); P.Ind.Add(bas + 2); P.Ind.Add(bas + 1); P.Ind.Add(bas + 3);
		}
		int pic = b.T[3], edge = b.T[0];
		float lo = 0.5f - T, hi = 0.5f + T;   // (the planks' middle 4 pixels, for the edges)
		foreach (float s in new[] { T, -T })   // its front and back
			Quad(Pt(0, 0, s), Pt(1, 0, s), Pt(0, 1, s), Pt(1, 1, s), pic, new(0, 0), new(1, 0), new(0, 1), new(1, 1), 0.9f);
		foreach (float u in new[] { 0f, 1f })   // its hinge edge and its free edge
			Quad(Pt(u, 0, -T), Pt(u, 0, T), Pt(u, 1, -T), Pt(u, 1, T), edge, new(lo, 0), new(hi, 0), new(lo, 1), new(hi, 1), 0.7f);
		foreach (float y in new[] { 0f, 1f })   // its top and bottom
			Quad(Pt(0, y, -T), Pt(1, y, -T), Pt(0, y, T), Pt(1, y, T), edge, new(0, lo), new(1, lo), new(0, hi), new(1, hi), y > 0 ? 1 : 0.6f);
	}
	// ---- a fence (see Blocks.FenceBoxes): its post and rails, each face showing the planks by its place in the block (so the
	// grain lines up with the planks'), in the block's own light, with no corner shadows. Faces nothing could see are left out:
	// a rail's end against the post, a rail's far end against what it joins, the post's top and bottom against whole blocks ----
	void AddFence(int x, int y, int z, BlockDef b, Part P, bool anyLight, Dictionary<long, float> sky)
	{
		int mask = Blocks.FenceMask(W.GetBlock, x, y, z);
		float light = anyLight ? World.LightCurve(W.LightAt(x, y, z)) : 0, sk = W.SkyExposure(x, y, z, sky);
		bool Whole(int n) => n != 0 && Blocks.B[n] is { Shape: null, Transparent: false, Liquid: false };
		bool coverUp = Whole(W.GetBlock(x, y + 1, z)), coverDown = Whole(W.GetBlock(x, y - 1, z));
		var boxes = Blocks.FenceBoxes(mask, false);
		for (int k = 0; k < boxes.Count; k++)
		{
			var bx = boxes[k];
			for (int fi = 0; fi < 6; fi++)
			{
				var f = Faces[fi];
				if (k == 0 ? (f.Dir[1] > 0 && coverUp) || (f.Dir[1] < 0 && coverDown) : RailHidden(bx, f.Dir, x, y, z)) continue;
				FenceFace(P, b, new Vector3(x, y, z), bx, fi, light, sk);
			}
		}
	}
	bool RailHidden(float[] bx, int[] d, int x, int y, int z)
	{   // (against the post, or at the block's edge against another fence or a whole block)
		bool alongX = bx[3] - bx[0] > bx[5] - bx[2];
		if (d[1] != 0 || (alongX ? d[0] == 0 : d[2] == 0)) return false;   // (only its two ends can be hidden)
		float at =d[0] > 0 ? bx[3] : d[0] < 0 ? bx[0] : d[2] > 0 ? bx[5] : bx[2];
		if (at > 0.01f && at < 0.99f) return true;   // (its end inside the post)
		int n = W.GetBlock(x + d[0], y, z + d[2]);
		return Blocks.IsFence(n) || (n != 0 && Blocks.B[n] is { Shape: null, Transparent: false, Liquid: false });
	}
	public static void FenceFace(Part P, BlockDef b, Vector3 at, float[] bx, int fi, float light, float sky)
	{
		var f = Faces[fi]; var fu = FaceUv[fi];
		int tile = f.Dir[1] > 0 ? b.T[1] : f.Dir[1] < 0 ? b.T[2] : b.T[0], bas = P.Pos.Count;
		var lp = new float[3];
		for (int i = 0; i < 4; i++)
		{
			var c = f.C[i];
			lp[0] = c[0] != 0 ? bx[3] : bx[0]; lp[1] = c[1] != 0 ? bx[4] : bx[1]; lp[2] = c[2] != 0 ? bx[5] : bx[2];
			float u = fu[0].flip ? 1 - lp[fu[0].axis] : lp[fu[0].axis], v = fu[1].flip ? 1 - lp[fu[1].axis] : lp[fu[1].axis];
			P.V(at.X + lp[0], at.Y + lp[1], at.Z + lp[2], Atlas.UV(tile, u, v), f.Shade, light, sky);
		}
		P.Quad(bas);
	}
	// something lying flat on the ground (a rock): its picture just above the block under it, turned a random way
	void AddFlat(int x, int y, int z, BlockDef b, Part P, bool anyLight, Dictionary<long, float> sky, float lift = 0)
	{
		double a = W.Gen.Hash(x, z, y + 4242) * Math.PI * 2; float ca = (float)Math.Cos(a) * 0.5f, sa = (float)Math.Sin(a) * 0.5f;
		float light = anyLight ? World.LightCurve(W.LightAt(x, y, z)) : 0, s = W.SkyExposure(x, y, z, sky);
		int tile = b.T[0], bas = P.Pos.Count;
		foreach (var (u, v) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
		{
			float dx = u - 0.5f, dz = v - 0.5f;
			P.V(x + 0.5f + (dx * ca - dz * sa) * 2, y + lift + 0.02f, z + 0.5f + (dx * sa + dz * ca) * 2, Atlas.UV(tile, u, v), 1, light, s);
		}
		P.Quad(bas); P.Ind.Add(bas); P.Ind.Add(bas + 1); P.Ind.Add(bas + 2); P.Ind.Add(bas + 2); P.Ind.Add(bas + 1); P.Ind.Add(bas + 3);   // both sides
	}
	// the Cutter's saw blade: the top half of the cutter_saw picture, standing up across the middle of the block above its half-tall base,
	// seen from both sides
	void AddSaw(int x, int y, int z, Part P, bool anyLight, Dictionary<long, float> sky)
	{
		float light = anyLight ? World.LightCurve(W.LightAt(x, y, z)) : 0, s = W.SkyExposure(x, y, z, sky);
		int bas = P.Pos.Count;
		foreach (var (u, v) in new[] { (0f, 0.5f), (1f, 0.5f), (0f, 1f), (1f, 1f) })
			P.V(x + 0.5f, y + v, z + u, Atlas.UV(Atlas.SawTile, u, v), 1, light, s);
		P.Quad(bas); P.Ind.Add(bas); P.Ind.Add(bas + 1); P.Ind.Add(bas + 2); P.Ind.Add(bas + 2); P.Ind.Add(bas + 1); P.Ind.Add(bas + 3);   // both sides
	}
	// a plant (a flower): its picture twice, standing up and crossed like an X, seen from both sides.
	// Nudged a little off-centre at random (always the same for the same spot) so a patch doesn't look like a grid.
	void AddPlant(int x, int y, int z, BlockDef b, Part P, bool anyLight, Dictionary<long, float> sky)
	{
		float light = anyLight ? World.LightCurve(W.LightAt(x, y, z)) : 0, s = W.SkyExposure(x, y, z, sky);
		float ox = (float)(W.Gen.Hash(x, z, y + 777) - 0.5) * 0.3f, oz = (float)(W.Gen.Hash(x, z, y + 999) - 0.5) * 0.3f, r = 0.45f;
		int tile = b.T[0];
		foreach (var (ax, az) in new[] { (1, 1), (1, -1) })
		{
			int bas = P.Pos.Count;
			foreach (var (u, v) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
			{
				float d = (u - 0.5f) * 2 * r * MathF.Sqrt(0.5f) * 1.4f;
				P.V(x + 0.5f + ox + d * ax, y + v, z + 0.5f + oz + d * az, Atlas.UV(tile, u, v), 0.9f, light, s);
			}
			P.Quad(bas); P.Ind.Add(bas); P.Ind.Add(bas + 1); P.Ind.Add(bas + 2); P.Ind.Add(bas + 2); P.Ind.Add(bas + 1); P.Ind.Add(bas + 3);   // both sides
		}
	}
	// pieces: half-size cubes, each showing its own quarter of the block's picture
	static readonly (int axis, bool flip)[][] FaceUv = BuildFaceUv();
	static (int, bool)[][] BuildFaceUv()
	{
		var r = new (int, bool)[6][];
		for (int fi = 0; fi < 6; fi++)
		{
			var f = Faces[fi];
			(int, bool) Along(int k)
			{
				for (int a = 0; a < 3; a++)
				{
					bool same = true, opp = true;
					foreach (var c in f.C) { if (c[a] != c[k]) same = false; if (c[a] != 1 - c[k]) opp = false; }
					if (same) return (a, false);
					if (opp) return (a, true);
				}
				return (0, false);
			}
			r[fi] = new[] { Along(3), Along(4) };
		}
		return r;
	}
	// the biome tint at a corner that may be half-way between blocks: the average of the 4 whole-number corners round it
	static Color CornerTint(Func<int, int, Color> tintAt, float px, float pz)
	{
		int x0 = (int)MathF.Floor(px), x1 = (int)MathF.Ceiling(px), z0 = (int)MathF.Floor(pz), z1 = (int)MathF.Ceiling(pz);
		Color a = tintAt(x0, z0), b = tintAt(x1, z0), c = tintAt(x0, z1), d = tintAt(x1, z1);
		return new Color((a.R + b.R + c.R + d.R) / 4, (a.G + b.G + c.G + d.G) / 4, (a.B + b.B + c.B + d.B) / 4);
	}
	// is half-cell (X, Y, Z) filled with something you can't see through (part of a whole block, or a piece)? (corner shadows)
	int OccHalf(int X, int Y, int Z) { int b = W.SubBlock(X, Y, Z); return b != 0 && !Blocks.B[b].Transparent ? 1 : 0; }
	// A pieces space's faces are lit the way whole blocks' faces are, at half-block size: each corner is shaded by the 3 half-cells
	// touching it in front of the face, and takes the torch and sky light of the open ones (each by the block it's in) and of the
	// half-cell in front, averaged. Grass and leaves take the biome tint. (The same as the web version.)
	void AddPieces(int x, int y, int z, Part P, bool anyLight, Dictionary<long, float> sky, Func<int, int, Color> tintAt, int[] a = null)
	{
		bool given = a != null;
		if (a == null && !W.PieceMap.TryGetValue(World.Idx(x, y, z), out a)) return;
		float depth = 0.75f + 0.25f * Math.Min(1, y / 20f);
		var lp = new float[3]; var hp = new int[3]; var ao = new int[4]; var white = new Color(1, 1, 1);
		float sum = 0, skySum = 0; int cnt = 0;
		void LightFrom(int hx, int hy, int hz)
		{
			int bx = World.FloorDiv(hx, 2), by = World.FloorDiv(hy, 2), bz = World.FloorDiv(hz, 2);
			if (anyLight) sum += W.LightAt(bx, by, bz);
			skySum += W.SkyExposure(bx, by, bz, sky); cnt++;
		}
		bool cut = !given;   // (a pieces space: its faces use the cut-shape pictures)
		// does spot s2 show its face fi? (something see-through, and not the same block, in front of it)
		bool Shows(int s2, int fi)
		{
			int id2 = World.SpotBlock(a[s2]);
			if (id2 == 0 || Blocks.B[id2] == null) return false;
			var d = Faces[fi].Dir;
			int n = W.SubBlock(x * 2 + (s2 & 1) + d[0], y * 2 + ((s2 >> 1) & 1) + d[1], z * 2 + (s2 >> 2) + d[2]);
			return n == 0 || (n != id2 && Blocks.B[n].Transparent);
		}
		for (int s = 0; s < 8; s++)
		{
			int raw = a[s], id = World.SpotBlock(raw);   // (block + group: see World.GroupMul)
			if (id == 0 || Blocks.B[id] == null) continue;
			var b = Blocks.B[id]; int sx = s & 1, sy = (s >> 1) & 1, sz = s >> 2;
			int X = x * 2 + sx, Y = y * 2 + sy, Z = z * 2 + sz;
			for (int fi = 0; fi < 6; fi++)
			{
				var f = Faces[fi];
				if (!Shows(s, fi)) continue;
				int tile = f.Dir[1] > 0 ? b.T[1] : f.Dir[1] < 0 ? b.T[2] : b.T[0];
				var fu = FaceUv[fi]; int bas = P.Pos.Count;
				if (cut)
				{   // which quarters of this face's picture show: the 4 spots in the same layer along the face's axis,
					// that hold the same block of the same group (put down together) and show their face this way too
					// (each picks the quarter its face shows). A loose piece is framed all round; a double slab is two slabs.
					int ax = f.Dir[0] != 0 ? 0 : f.Dir[1] != 0 ? 1 : 2, mask = 0;
					int along = ax == 0 ? sx : ax == 1 ? sy : sz;
					for (int t2 = 0; t2 < 8; t2++)
					{
						int[] c2 = { t2 & 1, (t2 >> 1) & 1, t2 >> 2 };
						if (c2[ax] != along || a[t2] != raw || !Shows(t2, fi)) continue;
						for (int k = 0; k < 3; k++) lp[k] = (c2[k] + 0.5f) * 0.5f;
						float u = fu[0].flip ? 1 - lp[fu[0].axis] : lp[fu[0].axis], v = fu[1].flip ? 1 - lp[fu[1].axis] : lp[fu[1].axis];
						mask |= Atlas.Quarter(u > 0.5f ? 1 : 0, v > 0.5f ? 0 : 1);
					}
					tile = Atlas.ShapeTile(tile, mask);
				}
				int na = f.Dir[0] != 0 ? 0 : f.Dir[1] != 0 ? 1 : 2, ta = na == 0 ? 1 : 0, tb = na == 2 ? 1 : 2;
				bool tinted = Blocks.TakesBiomeTint(id);
				for (int i = 0; i < 4; i++)
				{
					var c = f.C[i];
					// the 3 half-cells touching this corner, in front of the face (corner shadows)
					int sa = c[ta] != 0 ? 1 : -1, sb = c[tb] != 0 ? 1 : -1;
					int FX = X + f.Dir[0], FY = Y + f.Dir[1], FZ = Z + f.Dir[2];
					hp[0] = FX; hp[1] = FY; hp[2] = FZ;
					hp[ta] += sa; int s1 = OccHalf(hp[0], hp[1], hp[2]);
					hp[tb] += sb; int s3 = OccHalf(hp[0], hp[1], hp[2]);
					hp[ta] -= sa; int s2 = OccHalf(hp[0], hp[1], hp[2]);
					ao[i] = s1 == 1 && s2 == 1 ? 0 : 3 - (s1 + s2 + s3);
					// the light: in front, then each open one of them (in the order: beside along ta, diagonal, beside along tb)
					sum = skySum = 0; cnt = 0; LightFrom(FX, FY, FZ);
					hp[0] = FX; hp[1] = FY; hp[2] = FZ; hp[ta] += sa;
					if (s1 == 0) LightFrom(hp[0], hp[1], hp[2]);
					hp[tb] += sb; if (s3 == 0 && !(s1 == 1 && s2 == 1)) LightFrom(hp[0], hp[1], hp[2]);
					hp[ta] -= sa; if (s2 == 0) LightFrom(hp[0], hp[1], hp[2]);
					lp[0] = (sx + c[0]) * 0.5f; lp[1] = (sy + c[1]) * 0.5f; lp[2] = (sz + c[2]) * 0.5f;
					float u = fu[0].flip ? 1 - lp[fu[0].axis] : lp[fu[0].axis], v = fu[1].flip ? 1 - lp[fu[1].axis] : lp[fu[1].axis];
					P.V(x + lp[0], y + lp[1], z + lp[2], Atlas.UV(tile, u, v), f.Shade * depth * AoLevels[ao[i]], anyLight ? World.LightCurve(sum / cnt) : 0, skySum / cnt,
						tinted ? CornerTint(tintAt, x + lp[0], z + lp[2]) : white);
				}
				// pick the quad diagonal that keeps the shading smooth (as whole blocks do)
				if (ao[0] + ao[3] > ao[1] + ao[2]) P.QuadFlipped(bas); else P.Quad(bas);
			}
		}
	}
	// a pieces space in snow (388): one layer of snow on every upward surface in it. Each patch is a 1/2 x 1/8 x 1/2 box on a
	// spot: on the floor under an empty bottom spot (where what's below holds snow), on top of a bottom-layer piece with
	// nothing above it, and on top of a top-layer piece (sticking 1/8 up into the space above, if that's air or something
	// you walk through that isn't water or snow). Same as the web version.
	void AddSnowUnder(int x, int y, int z, Part P, bool anyLight, Dictionary<long, float> sky)
	{
		if (!W.PieceMap.TryGetValue(World.Idx(x, y, z), out var a)) return;
		var sb = Blocks.B[Blocks.Snow]; float h = 1 / 8f, depth = 0.75f + 0.25f * Math.Min(1, y / 20f);
		bool Filled(int sx, int sy, int sz) => a[sx + sy * 2 + sz * 4] != 0;
		bool Holds(int n) => Blocks.Get(n) is { } d && d.Shape == null && !d.Liquid && !d.NoCollide && !d.Item;
		bool Open(int n) => n == 0 || (Blocks.B[n].NoCollide && !Blocks.IsWater(n) && !Blocks.IsSnow(n));
		// the snow patches: [column] -> its heights in half-blocks (0 floor, 1 middle, 2 top)
		var at = new List<int>[4];
		for (int sz = 0; sz < 2; sz++) for (int sx = 0; sx < 2; sx++)
		{
			var l = at[sx + sz * 2] = new List<int>();
			if (!Filled(sx, 0, sz) && Holds(W.SubBlock(x * 2 + sx, y * 2 - 1, z * 2 + sz))) l.Add(0);
			if (Filled(sx, 0, sz) && !Filled(sx, 1, sz)) l.Add(1);
			if (Filled(sx, 1, sz) && Open(W.SubBlock(x * 2 + sx, y * 2 + 2, z * 2 + sz))) l.Add(2);
		}
		var lp = new float[3];
		for (int sz = 0; sz < 2; sz++) for (int sx = 0; sx < 2; sx++)
			foreach (int lv in at[sx + sz * 2])
			{
				int cy = y + (lv == 2 ? 1 : 0);   // (the space whose light it takes)
				float light = anyLight ? World.LightCurve(W.LightAt(x, cy, z)) : 0, s2 = W.SkyExposure(x, cy, z, sky);
				for (int fi = 0; fi < 6; fi++)
				{
					var f = Faces[fi];
					if (f.Dir[1] < 0) continue;   // (it lies on something)
					if (f.Dir[1] == 0)
					{
						int nx = sx + f.Dir[0], nz = sz + f.Dir[2];
						if (nx >= 0 && nx <= 1 && nz >= 0 && nz <= 1)
						{   // beside it in this space: a piece at that height, or snow at the same height, hides that side
							if ((lv < 2 && Filled(nx, lv, nz)) || at[nx + nz * 2].Contains(lv)) continue;
						}
						else
						{
							int n = W.SubBlock(x * 2 + nx, y * 2 + lv, z * 2 + nz);
							if (n != 0 && (!Blocks.B[n].Transparent || Blocks.B[n].Shape == "snow" || Blocks.SnowIn(n) > 0)) continue;
						}
					}
					int tile = f.Dir[1] > 0 ? sb.T[1] : sb.T[0];
					var fu = FaceUv[fi]; int bas = P.Pos.Count;
					foreach (var c in f.C)
					{
						lp[0] = (sx + c[0]) * 0.5f; lp[1] = c[1] * h; lp[2] = (sz + c[2]) * 0.5f;
						float u = fu[0].flip ? 1 - lp[fu[0].axis] : lp[fu[0].axis], v = fu[1].flip ? 1 - lp[fu[1].axis] : lp[fu[1].axis];
						P.V(x + lp[0], y + lv * 0.5f + lp[1], z + lp[2], Atlas.UV(tile, u, v), f.Shade * depth, light, s2);
					}
					P.Quad(bas);
				}
			}
	}
	// water: only the sides touching air or see-through blocks; the surface's corners are averaged so it slopes smoothly
	float WaterCornerHeight(int x, int y, int z, int gx, int gz)
	{
		float sum = 0; int n = 0;
		for (int dz = -1; dz <= 0; dz++) for (int dx = -1; dx <= 0; dx++)
		{
			int b = W.GetBlock(gx + dx, y, gz + dz);
			if (!Blocks.IsWater(b)) continue;
			int up = W.GetBlock(gx + dx, y + 1, gz + dz);
			if (Blocks.IsWater(up)) return 1;
			sum += Blocks.WaterHeight(b, up); n++;
		}
		return n > 0 ? sum / n : Blocks.WaterHeight(W.GetBlock(x, y, z), W.GetBlock(x, y + 1, z));
	}
	const float FallSlide = 3;   // (falling water's picture: blocks a second; a river's top moves at its current's speed)
	void AddWater(int x, int y, int z, int id, Part P, bool anyLight, Dictionary<long, float> sky)
	{
		bool full = Blocks.IsWater(W.GetBlock(x, y + 1, z));
		var ch = new float[4]; var got = new bool[4];
		float CornerH(int cx, int cz) { int k = cx * 2 + cz; if (full) return 1; if (!got[k]) { ch[k] = WaterCornerHeight(x, y, z, x + cx, z + cz); got[k] = true; } return ch[k]; }
		float light = anyLight ? World.LightCurve(W.LightAt(x, y, z)) : 0, s = W.SkyExposure(x, y, z, sky);
		foreach (var f in Faces)
		{
			int n = W.GetBlock(x + f.Dir[0], y + f.Dir[1], z + f.Dir[2]);
			if (Blocks.IsWater(n) || (n != 0 && !Blocks.B[n].Transparent)) continue;
			// (no wall of water at the edge of the world: seen through the sea it showed up as see-through panes)
			int ex = x + f.Dir[0], ez = z + f.Dir[2];
			if (ex < 0 || ez < 0 || ex >= World.WX || ez >= World.WZ) continue;
			int tile = Blocks.B[id].T[0], bas = P.Pos.Count;
			// (since Beta 0.1.6.0) a running river's top slides its pace's picture along the current; falling water's sides
			// slide river_falling down, and its top toward the picture's bottom edge (rows run along z there)
			float sx = 0, sz = 0;
			if (f.Dir[1] == 1 && Blocks.RiverCurrent(id, out float cx, out float cz)) { tile = Atlas.RiverTiles[(id - Blocks.RiverWater) / 8]; sx = cx; sz = cz; }
			else if (id == Blocks.FallingWater && f.Dir[1] != -1) { tile = Atlas.RiverTiles[3]; sz = FallSlide; }
			bool slides = sx != 0 || sz != 0;
			foreach (var c in f.C)
			{
				float h = c[1] != 0 ? CornerH(c[0], c[2]) : 0;
				float v = f.Dir[1] != 0 ? c[4] : c[4] != 0 ? h : 0;
				if (!slides) { P.V(x + c[0], y + h, z + c[2], Atlas.UV(tile, c[3], v), f.Shade, light, s, Vector4.Zero); continue; }
				// (where the corner is in the picture: a top's x and z; a side's along-the-face and down from the block's top)
				var at = f.Dir[1] != 0 ? new Vector2(c[0], c[2]) : new Vector2(c[3], 1 - (c[1] != 0 ? h : 0));
				P.V(x + c[0], y + h, z + c[2], Atlas.UV(tile, 0, 1), f.Shade, light, s, new Vector4(at.X, at.Y, sx, sz));
			}
			P.Quad(bas);
		}
	}
}
