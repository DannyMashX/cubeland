using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace Cubeland;

// Mobs. Models come from your Blockbench .json files in model/, skins from textures/entity/mobs/<name>/.
// Sizes are in model pixels: 16 = one block.
public partial class Main
{
	public class MobType
	{
		public string Name; public int Health; public float Speed; public int Max;
		public bool Hostile, Night, Glow, Curious; public int Damage; public float Sight, Reach, Hearing, LightRange, Scale = 1;
		public string Growl; public (int id, int lo, int hi, float chance)[] Drops = Array.Empty<(int, int, int, float)>();
	}
	// drops: (item, fewest, most, chance) dropped when killed. hostile: chases and attacks you, survival only. night: only spawns after dark.
	// growl: its voice, heard every so often (and when hit) up to ~28 blocks away. curious: drawn to block sounds and torches.
	public static readonly Dictionary<string, MobType> MobTypes = new()
	{
		["danthechocolateman"] = new() { Name = "Dan The Chocolate Man", Health = 5, Speed = 1.3f, Max = 6, Drops = new[] { (Blocks.Chocolate, 1, 2, 1f) }, Growl = "hrr" },
		// scale: Grr's model is 18 pixels tall; 1.6x makes him 1.8 blocks, as tall as you
		["grrangry"] = new() { Name = "Grr Angry", Health = 8, Speed = 1.5f, Max = 5, Hostile = true, Night = true, Damage = 3, Sight = 16, Reach = 1.8f, Glow = true,
			Curious = true, Hearing = 24, LightRange = 16, Growl = "growl", Scale = 1.6f, Drops = new[] { (Blocks.FireEssence, 1, 1, 1 / 20f) } },
	};

	public class Mob
	{
		public string Type; public MobType T;
		public float X, Y, Z, Vx, Vy, Vz, Yaw, Walk, Stride, Timer, Hurt, Flee, Dead, AttackCool, Swing, LightTimer, GoalCheck, GrowlIn = -1, Sky = 1, Torch;
		public float? TurnTo; public int Health; public bool Walking, OnGround, Bumped;
		public int K, Hue = -1; public float Tx, Ty, Tz, Tyaw; public bool NetHurt;   // K: its own number (multiplayer); T: where the host's game says it is; Hue: recoloured skin (players)
		public (string kind, float x, float y, float z, float time)? Goal;
		public Node3D Root; public List<(Node3D pivot, float phase)> Legs = new(); public List<Node3D> Arms = new(); public List<ShaderMaterial> Mats = new();
		public float Half = 0.3f, Height = 1;
	}
	readonly List<Mob> mobList = new();
	Node3D mobRoot; float mobSpawnTimer; int mobCount;
	static Dictionary<string, (JsonElement json, string folder)> models;
	static readonly Dictionary<string, Texture2D> mobTex = new();
	static Shader itemShader;

	// ---- reading the models ----
	static void LoadModels()
	{
		if (models != null) return;
		models = new();
		foreach (var f in DirAccess.GetFilesAt("res://model"))
		{
			if (!f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
			try
			{
				string name = f[..^5];
				models[name.ToLowerInvariant()] = (JsonDocument.Parse(FileAccess.GetFileAsString("res://model/" + f)).RootElement.Clone(), name);
			}
			catch (Exception e) { GD.PrintErr($"couldn't read model {f}: {e.Message}"); }
		}
	}
	// res:// names are case-sensitive once the game is exported: find a picture whatever the capitals in its name
	static string FindRes(string path)
	{
		if (ResourceLoader.Exists(path)) return path;
		string at = "res:/";
		foreach (var part in path["res://".Length..].Split('/'))
		{
			string dir = at == "res:/" ? "res://" : at;
			bool last = part.Contains('.');
			var names = last ? DirAccess.GetFilesAt(dir).Select(n => n.Replace(".import", "").Replace(".remap", "")) : DirAccess.GetDirectoriesAt(dir);
			string hit = names.FirstOrDefault(n => string.Equals(n, part, StringComparison.OrdinalIgnoreCase));
			if (hit == null) return null;
			at = (at == "res:/" ? "res://" : at + "/") + hit;
		}
		return at;
	}
	static Texture2D MobTexture(string key, int hue = -1)
	{
		string ck = hue < 0 ? key : key + "#hue" + hue;
		if (mobTex.TryGetValue(ck, out var t)) return t;
		var p = FindRes("res://textures/" + key + ".png");
		var tex = p == null ? null : ResourceLoader.Load<Texture2D>(p);
		if (tex != null && hue >= 0) tex = ImageTexture.CreateFromImage(Recolor(tex.GetImage(), hue));
		return mobTex[ck] = tex;
	}
	// shift every pixel's colour around the colour wheel so the skin's main colour (its most common hue) becomes `hue`;
	// shading and the darker/lighter details stay the same (used for other players' bodies)
	static Image Recolor(Image src, int hue)
	{
		var img = (Image)src.Duplicate(); if (img.IsCompressed()) img.Decompress(); img.Convert(Image.Format.Rgba8);
		static (float h, float s, float l) Hsl(Color c)
		{
			float r = c.R, g = c.G, b = c.B, mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), l = (mx + mn) / 2;
			if (mx == mn) return (0, 0, l);
			float d = mx - mn, s = l > 0.5f ? d / (2 - mx - mn) : d / (mx + mn);
			float h = mx == r ? (g - b) / d + (g < b ? 6 : 0) : mx == g ? (b - r) / d + 2 : (r - g) / d + 4;
			return (h * 60, s, l);
		}
		var counts = new int[36];
		for (int y = 0; y < img.GetHeight(); y++) for (int x = 0; x < img.GetWidth(); x++) { var c = img.GetPixel(x, y); if (c.A > 0.5f) { var (h, s, _) = Hsl(c); if (s > 0.25f) counts[(int)(h / 10) % 36]++; } }
		float shift = hue - (Array.IndexOf(counts, counts.Max()) * 10 + 5);
		for (int y = 0; y < img.GetHeight(); y++) for (int x = 0; x < img.GetWidth(); x++)
		{
			var c = img.GetPixel(x, y); if (c.A == 0) continue;
			var (h, s, l) = Hsl(c);
			float hh = (((h + shift) % 360) + 360) % 360 / 360;
			float q = l < 0.5f ? l * (1 + s) : l + s - l * s, pp = 2 * l - q;
			float Ch(float tt) { tt = ((tt % 1) + 1) % 1; return tt < 1 / 6f ? pp + (q - pp) * 6 * tt : tt < 0.5f ? q : tt < 2 / 3f ? pp + (q - pp) * (2 / 3f - tt) * 6 : pp; }
			img.SetPixel(x, y, new Color(Ch(hh + 1 / 3f), Ch(hh), Ch(hh - 1 / 3f), c.A));
		}
		return img;
	}

	static float[] Vec(JsonElement e) => e.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();
	// Blockbench face name -> box face (+x, -x, +y, -y, +z, -z), laid out like three.js's BoxGeometry so your uv's land the same way
	static readonly string[] McFaces = { "east", "west", "up", "down", "south", "north" };
	// (u axis, v axis, w axis, udir, vdir, w sign) for each box face
	static readonly (int u, int v, int w, int ud, int vd, int ws)[] Planes =
		{ (2, 1, 0, -1, -1, 1), (2, 1, 0, 1, -1, -1), (0, 2, 1, 1, 1, 1), (0, 2, 1, 1, -1, -1), (0, 1, 2, 1, -1, 1), (0, 1, 2, -1, -1, -1) };
	static readonly int[][] TurnOrder = { new[] { 0, 1, 2, 3 }, new[] { 2, 0, 3, 1 }, new[] { 3, 2, 1, 0 }, new[] { 1, 3, 0, 2 } };

	void BuildMob(Mob m)
	{
		var root = new Node3D(); m.Root = root; mobRoot.AddChild(root);
		if (!models.TryGetValue(m.Type, out var md)) return;
		var model = md.json; float S = m.T.Scale;
		var body = new Node3D { Scale = Vector3.One * S }; root.AddChild(body);
		var els = model.GetProperty("elements").EnumerateArray().ToList();
		float mnx = 1e9f, mxx = -1e9f, mnz = 1e9f, mxz = -1e9f, top = 0;
		foreach (var e in els) { var f = Vec(e.GetProperty("from")); var t = Vec(e.GetProperty("to")); mnx = Math.Min(mnx, f[0]); mxx = Math.Max(mxx, t[0]); mnz = Math.Min(mnz, f[2]); mxz = Math.Max(mxz, t[2]); top = Math.Max(top, t[1]); }
		float cx = (mnx + mxx) / 2, cz = (mnz + mxz) / 2, foot = 0;
		var texNames = model.TryGetProperty("textures", out var tx) ? tx : default;
		ShaderMaterial MatFor(string reff)
		{
			string id = reff.Replace("#", "");
			string name = texNames.ValueKind == JsonValueKind.Object && texNames.TryGetProperty(id, out var n) ? n.GetString() : "missing";
			// a plain name ("body") lives in the mob's own folder; a path ("Textures/entity/mobs/GrrAngry/grr_angry") is used as-is
			string key = name.Contains('/') ? System.Text.RegularExpressions.Regex.Replace(name, "^textures/", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase) : $"entity/mobs/{md.folder}/{name}";
			var tex = MobTexture(key, m.Hue);
			var have = m.Mats.FirstOrDefault(x => (Texture2D)x.GetShaderParameter("tex") == tex);
			if (have != null) return have;
			itemShader ??= GD.Load<Shader>("res://src/item.gdshader");
			var mat = new ShaderMaterial { Shader = itemShader }; mat.SetShaderParameter("tex", tex); m.Mats.Add(mat);
			return mat;
		}
		// Blockbench groups (outliner folders) named like "arms" / "legs" mark which parts are arms and legs
		var groupOf = new Dictionary<int, string>();
		void Walk(JsonElement list, string tag)
		{
			if (list.ValueKind != JsonValueKind.Array) return;
			foreach (var c in list.EnumerateArray())
			{
				if (c.ValueKind == JsonValueKind.Number) { if (tag != null) groupOf[c.GetInt32()] = tag; continue; }
				string gn = c.TryGetProperty("name", out var g) ? g.GetString() ?? "" : "";
				Walk(c.TryGetProperty("children", out var ch) ? ch : default, gn.Contains("arm", StringComparison.OrdinalIgnoreCase) ? "arm" : gn.Contains("leg", StringComparison.OrdinalIgnoreCase) ? "leg" : tag);
			}
		}
		if (model.TryGetProperty("groups", out var groups)) Walk(groups, null);
		for (int ei = 0; ei < els.Count; ei++)
		{
			var e = els[ei]; var from = Vec(e.GetProperty("from")); var to = Vec(e.GetProperty("to"));
			float w = (to[0] - from[0]) / 16, h = (to[1] - from[1]) / 16, d = (to[2] - from[2]) / 16;
			var size = new[] { w, h, d };
			// one surface per picture
			var surf = new Dictionary<ShaderMaterial, (List<Vector3> v, List<Vector2> uv, List<int> idx)>();
			e.TryGetProperty("faces", out var faces);
			for (int fi = 0; fi < 6; fi++)
			{
				JsonElement face = default; bool has = faces.ValueKind == JsonValueKind.Object && faces.TryGetProperty(McFaces[fi], out face);
				var mat = has && face.TryGetProperty("texture", out var ft) && ft.ValueKind == JsonValueKind.String ? MatFor(ft.GetString()) : (m.Mats.FirstOrDefault() ?? MatFor("#0"));
				if (!surf.TryGetValue(mat, out var s)) surf[mat] = s = (new(), new(), new());
				float[] fuv = has && face.TryGetProperty("uv", out var fu) ? Vec(fu) : new float[] { 0, 0, 16, 16 };
				int turn = has && face.TryGetProperty("rotation", out var fr) ? ((int)(fr.GetDouble() / 90)) & 3 : 0;
				var c = new[] { new Vector2(fuv[0], fuv[1]), new Vector2(fuv[2], fuv[1]), new Vector2(fuv[0], fuv[3]), new Vector2(fuv[2], fuv[3]) };
				var p = Planes[fi]; int b = s.v.Count;
				for (int k = 0; k < 4; k++)
				{
					int ix = k & 1, iy = k >> 1;
					var pos = new float[3];
					pos[p.u] = (ix - 0.5f) * size[p.u] * p.ud;
					pos[p.v] = (iy - 0.5f) * size[p.v] * p.vd;
					pos[p.w] = size[p.w] / 2 * p.ws;
					s.v.Add(new Vector3(pos[0], pos[1], pos[2]));
					s.uv.Add(c[TurnOrder[turn][k]] / 16);
				}
				s.idx.AddRange(new[] { b, b + 1, b + 2, b + 2, b + 1, b + 3 });
			}
			var mesh = new ArrayMesh();
			foreach (var (mat, s) in surf)
			{
				var arr = new Godot.Collections.Array(); arr.Resize((int)Mesh.ArrayType.Max);
				arr[(int)Mesh.ArrayType.Vertex] = s.v.ToArray(); arr[(int)Mesh.ArrayType.TexUV] = s.uv.ToArray(); arr[(int)Mesh.ArrayType.Index] = s.idx.ToArray();
				mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
				mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
			}
			var mi = new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
			var mid = new Vector3(((from[0] + to[0]) / 2 - cx) / 16, (from[1] + to[1]) / 2 / 16, ((from[2] + to[2]) / 2 - cz) / 16);
			string en = e.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
			string part = en.Contains("arm", StringComparison.OrdinalIgnoreCase) ? "arm" : en.Contains("leg", StringComparison.OrdinalIgnoreCase) ? "leg" : groupOf.GetValueOrDefault(ei);
			if (part != "arm")   // arms sticking out don't count
				foot = new[] { foot, Math.Abs(from[0] - cx) * 2, Math.Abs(to[0] - cx) * 2, Math.Abs(from[2] - cz) * 2, Math.Abs(to[2] - cz) * 2 }.Max();
			e.TryGetProperty("rotation", out var rot);
			if (part == "leg" || (part == null && from[1] == 0 && to[1] < top))
			{   // a leg: swings from its top; diagonal pairs move together
				var pivot = new Node3D { Position = new Vector3(mid.X, to[1] / 16, mid.Z) };
				mi.Position = new Vector3(0, -h / 2, 0); pivot.AddChild(mi); body.AddChild(pivot);
				m.Legs.Add((pivot, MathF.Sign((mid.X == 0 ? 1 : mid.X) * (mid.Z == 0 ? 1 : mid.Z))));
			}
			else if (part == "arm")
			{   // an arm: swings from its pivot point (the shoulder) when attacking
				var o = rot.ValueKind == JsonValueKind.Object ? Vec(rot.GetProperty("origin")) : new[] { (from[0] + to[0]) / 2, to[1], to[2] };
				var pivot = new Node3D { Position = new Vector3((o[0] - cx) / 16, o[1] / 16, (o[2] - cz) / 16) };
				mi.Position = mid - pivot.Position; pivot.AddChild(mi); body.AddChild(pivot);
				m.Arms.Add(pivot);
			}
			else if (rot.ValueKind == JsonValueKind.Object && rot.TryGetProperty("angle", out var ang) && ang.GetDouble() != 0)
			{
				var o = Vec(rot.GetProperty("origin"));
				var pivot = new Node3D { Position = new Vector3((o[0] - cx) / 16, o[1] / 16, (o[2] - cz) / 16) };
				float a = (float)(ang.GetDouble() * Math.PI / 180);
				var r = rot.GetProperty("axis").GetString(); pivot.Rotation = r == "x" ? new Vector3(a, 0, 0) : r == "y" ? new Vector3(0, a, 0) : new Vector3(0, 0, a);
				mi.Position = mid - pivot.Position; pivot.AddChild(mi); body.AddChild(pivot);
			}
			else { mi.Position = mid; body.AddChild(mi); }
		}
		// Half = half the width of its body for bumping into blocks; Height = how tall it stands (both in blocks)
		m.Half = Math.Min(0.49f, foot / 32 * 0.85f * S); m.Height = top / 16 * S;
	}

	void InitMobs() { LoadModels(); mobRoot = new Node3D(); AddChild(mobRoot); }
	Mob SpawnMob(string type, float x, float y, float z)
	{
		var t = MobTypes[type];
		var m = new Mob { K = ++mobCount, Type = type, T = t, X = x, Y = y, Z = z, Yaw = (float)(rnd.NextDouble() * Math.PI * 2), Timer = 1 + (float)rnd.NextDouble() * 3, Health = t.Health };
		BuildMob(m); mobList.Add(m);
		return m;
	}
	void RemoveMob(Mob m) { m.Root.QueueFree(); mobList.Remove(m); }
	// a grassy spot with room to stand, between minR and maxR blocks from you
	Vector3? FindMobSpot(float minR, float maxR)
	{
		for (int tries = 0; tries < 12; tries++)
		{
			double a = rnd.NextDouble() * Math.PI * 2, r = minR + rnd.NextDouble() * (maxR - minR);
			int x = (int)Math.Floor(player.Pos.X + Math.Cos(a) * r), z = (int)Math.Floor(player.Pos.Z + Math.Sin(a) * r);
			if (x < 2 || z < 2 || x >= World.WX - 2 || z >= World.WZ - 2) continue;
			int y = World.WY - 1;
			while (y > 0 && world.GetBlock(x, y, z) == 0) y--;
			if (world.GetBlock(x, y, z) != Blocks.Grass || world.GetBlock(x, y + 1, z) != 0 || world.GetBlock(x, y + 2, z) != 0) continue;
			return new Vector3(x + 0.5f, y + 1, z + 0.5f);
		}
		return null;
	}
	bool MobBlocked(Mob m, float x, float y, float z)
	{
		int x0 = (int)MathF.Floor(x - m.Half), x1 = (int)MathF.Floor(x + m.Half - 1e-6f), z0 = (int)MathF.Floor(z - m.Half), z1 = (int)MathF.Floor(z + m.Half - 1e-6f);
		int y0 = (int)MathF.Floor(y), y1 = (int)MathF.Floor(y + m.Height - 1e-6f);
		for (int yy = y0; yy <= y1; yy++) for (int zz = z0; zz <= z1; zz++) for (int xx = x0; xx <= x1; xx++) if (world.Solid(xx, yy, zz)) return true;
		return false;
	}
	// a mob's voice from where it stands: fades out by ~28 blocks
	void MobGrowl(Mob m)
	{
		float d = new Vector3(m.X - player.Pos.X, m.Y - player.Pos.Y, m.Z - player.Pos.Z).Length();
		float vol = 0.85f * MathF.Pow(Math.Max(0, 1 - d / 28), 1.5f) * (m.T.Growl == "hrr" ? 0.8f : 1);
		Sound.I?.PlayAt(m.T.Growl, new Vector3(m.X, m.Y + m.Height * 0.8f, m.Z), vol);
	}
	// a block broke or was placed: curious mobs that can hear it go and check it out (newest sound wins)
	void MobNoise(int x, int y, int z)
	{
		foreach (var m in mobList)
		{
			if (!m.T.Curious || m.Dead > 0) continue;
			float d = new Vector3(m.X - x - 0.5f, m.Y - y, m.Z - z - 0.5f).Length();
			if (d < m.T.Hearing) m.Goal = ("noise", x + 0.5f, y, z + 0.5f, 6 + d * 0.6f);
		}
	}
	(string, float, float, float, float)? NearestTorchGoal(Mob m, float range)
	{
		(string, float, float, float, float)? best = null; float bd = range;
		foreach (int i in world.Torches)
		{
			if (Blocks.B[world.Blocks[i]].Burn == 2) continue;   // (a burnt torch gives no light)
			var (x, y, z) = World.FromIdx(i);
			float d = new Vector3(x + 0.5f - m.X, y - m.Y, z + 0.5f - m.Z).Length();
			if (d < bd) { bd = d; best = ("light", x + 0.5f, y, z + 0.5f, 8 + (float)rnd.NextDouble() * 6); }
		}
		return best;
	}
	static float Turn(float from, float to) => MathF.Atan2(MathF.Sin(to - from), MathF.Cos(to - from));

	void UpdateMobs(float dt)
	{
		// keep a few around you: spawn out of the way, forget ones that wandered far off
		if ((mobSpawnTimer -= dt) <= 0 && dirty.Count == 0 && !HasArg("--nomobs") && !netClient)   // (a joined game shows the host's mobs)
		{
			mobSpawnTimer = 3;
			foreach (var (type, t) in MobTypes)
			{
				if (!models.ContainsKey(type) || (t.Night && sky.SkyDay > 0.3f)) continue;
				if (mobList.Count(m => m.Type == type) < t.Max)
				{
					bool first = mobList.Count == 0;
					if (FindMobSpot(first ? 8 : 24, first ? 30 : 56) is { } s) SpawnMob(type, s.X, s.Y, s.Z);
				}
			}
		}
		var P = player.Pos;
		foreach (var m in mobList.ToArray())
		{
			var t = m.T; float ox = m.X, oz = m.Z;
			if (m.Dead > 0)
			{   // tips over, then disappears
				m.Dead += dt;
				m.Root.Rotation = new Vector3(0, m.Yaw, Math.Min(1, m.Dead / 0.35f) * MathF.PI / 2);
				if (m.Dead > 0.9f) { RemoveMob(m); continue; }
			}
			else if (netClient)
			{   // joined game: glide toward where the host's game says it is
				float a = Math.Min(1, dt * 10);
				m.X += (m.Tx - m.X) * a; m.Y += (m.Ty - m.Y) * a; m.Z += (m.Tz - m.Z) * a;
				m.Yaw += Turn(m.Yaw, m.Tyaw) * a;
			}
			else if (MathF.Sqrt((m.X - P.X) * (m.X - P.X) + (m.Z - P.Z) * (m.Z - P.Z)) > 110 && !NearAFriend(m)) { RemoveMob(m); continue; }
			else
			{
				// wander: stand around, then walk somewhere for a bit. After being hit, run away from you.
				m.Timer -= dt;
				m.AttackCool = Math.Max(0, m.AttackCool - dt);
				float pdist = MathF.Sqrt((m.X - P.X) * (m.X - P.X) + (m.Z - P.Z) * (m.Z - P.Z));
				bool hunting = t.Hostile && inv.Survival && !dead && !player.Flying && pdist < t.Sight && Math.Abs(P.Y - m.Y) < 6;
				if (hunting)
				{   // hostile, survival only: walk straight at you and hit you when in reach
					m.Walking = true; m.Flee = 0;
					float toward = MathF.Atan2(m.X - P.X, m.Z - P.Z);
					m.Yaw += Turn(m.Yaw, toward) * Math.Min(1, dt * 6); m.TurnTo = m.Yaw;
					if (pdist < t.Reach && m.AttackCool <= 0) { m.AttackCool = 1; m.Swing = 0.35f; HurtPlayer(t.Damage, new Vector3(m.X, m.Y, m.Z)); }
				}
				else if (t.Curious && m.Flee <= 0 && (m.Goal != null || (m.GoalCheck -= dt) <= 0))
				{   // curious: heads for the last noise heard, otherwise the nearest torch
					if (m.Goal == null) { m.GoalCheck = 1 + (float)rnd.NextDouble(); m.Goal = NearestTorchGoal(m, t.LightRange); }
					if (m.Goal is { } g)
					{
						g.time -= dt; m.Goal = g;
						float gd = MathF.Sqrt((g.x - m.X) * (g.x - m.X) + (g.z - m.Z) * (g.z - m.Z));
						if (g.time <= 0 || (g.kind == "noise" && gd < 1.5f)) { m.Goal = null; m.Walking = false; m.Timer = 1 + (float)rnd.NextDouble() * 2; }
						else if (gd < (g.kind == "light" ? 2.5f : 1.5f)) { m.Walking = false; m.Timer = 0.5f; }   // made it: hang around the light
						else { m.Walking = true; float toward = MathF.Atan2(m.X - g.x, m.Z - g.z); m.Yaw += Turn(m.Yaw, toward) * Math.Min(1, dt * 4); m.TurnTo = m.Yaw; }
					}
				}
				else if (m.Flee > 0)
				{
					m.Flee -= dt; m.Walking = true;
					float away = MathF.Atan2(-(m.X - P.X), -(m.Z - P.Z));
					m.Yaw += Turn(m.Yaw, away) * Math.Min(1, dt * 6);
				}
				else if (m.Timer <= 0)
				{
					if (!m.Walking) { m.Walking = true; m.Timer = 1.5f + (float)rnd.NextDouble() * 4; m.TurnTo = m.Yaw + ((float)rnd.NextDouble() - 0.5f) * MathF.PI * 1.6f; }
					else { m.Walking = false; m.Timer = 1 + (float)rnd.NextDouble() * 5; }
				}
				if (m.Walking && m.Flee <= 0 && m.TurnTo is { } tt) m.Yaw += Turn(m.Yaw, tt) * Math.Min(1, dt * 3);
				float sp = m.Walking ? t.Speed * (m.Flee > 0 ? 2.4f : hunting ? 1.6f : 1) * (hunting && pdist < t.Reach * 0.7f ? 0 : 1) : 0;
				float tvx = -MathF.Sin(m.Yaw) * sp, tvz = -MathF.Cos(m.Yaw) * sp;   // the front of the model is its north side
				float acc = Math.Min(1, dt * (m.OnGround ? 10 : 2));
				m.Vx += (tvx - m.Vx) * acc; m.Vz += (tvz - m.Vz) * acc;
				// hop up one-block steps; turn around at anything taller
				if (m.Bumped && m.OnGround)
				{
					int fx = (int)MathF.Floor(m.X - MathF.Sin(m.Yaw) * (m.Half + 0.3f)), fz = (int)MathF.Floor(m.Z - MathF.Cos(m.Yaw) * (m.Half + 0.3f)), fy = (int)MathF.Floor(m.Y);
					if (world.Solid(fx, fy, fz) && !world.Solid(fx, fy + 1, fz) && !world.Solid(fx, fy + 2, fz)) m.Vy = 7.5f;
					else if (m.Flee <= 0) m.TurnTo = m.Yaw + MathF.PI * (0.6f + (float)rnd.NextDouble() * 0.8f);
				}
			}
			// falling, swimming and bumping into blocks (in a joined game the host's game does this)
			if (!netClient) {
			if (player.WaterAt(m.X, m.Y + 0.5f, m.Z)) { m.Vy += (2 - m.Vy) * Math.Min(1, dt * 3); if (m.Bumped) m.Vy = 5; }   // float up, paddle out over edges
			else m.Vy = Math.Max(-40, m.Vy - 28 * dt);
			ox = m.X; oz = m.Z; m.OnGround = false; m.Bumped = false;
			int steps = Math.Max(1, (int)MathF.Ceiling(Math.Max(Math.Abs(m.Vx), Math.Max(Math.Abs(m.Vy), Math.Abs(m.Vz))) * dt / 0.3f));
			for (int s = 0; s < steps; s++)
			{
				if (m.Vx != 0) { float b = m.X; m.X += m.Vx * dt / steps; if (MobBlocked(m, m.X, m.Y, m.Z)) { m.X = b; m.Bumped = true; m.Vx = 0; } }
				if (m.Vz != 0) { float b = m.Z; m.Z += m.Vz * dt / steps; if (MobBlocked(m, m.X, m.Y, m.Z)) { m.Z = b; m.Bumped = true; m.Vz = 0; } }
				if (m.Vy != 0) { float b = m.Y, v = m.Vy; m.Y += m.Vy * dt / steps; if (MobBlocked(m, m.X, m.Y, m.Z)) { m.Y = b; if (v < 0) m.OnGround = true; m.Vy = 0; } }
			}
			if (m.Y < -10) { RemoveMob(m); continue; }
			}
			// walking: legs swing in diagonal pairs while moving
			float moved = MathF.Sqrt((m.X - ox) * (m.X - ox) + (m.Z - oz) * (m.Z - oz));
			m.Stride += moved * 5.5f;
			m.Walk += ((moved > 0.002f ? 1 : 0) - m.Walk) * Math.Min(1, dt * 8);
			foreach (var (pivot, phase) in m.Legs) pivot.Rotation = new Vector3(MathF.Sin(m.Stride) * 0.6f * m.Walk * phase, 0, 0);
			// arms: a gentle sway while walking, and a downward swipe when attacking
			m.Swing = Math.Max(0, m.Swing - dt);
			float swipe = m.Swing > 0 ? MathF.Sin((1 - m.Swing / 0.35f) * MathF.PI) * 0.9f : 0;
			for (int k = 0; k < m.Arms.Count; k++) m.Arms[k].Rotation = new Vector3(MathF.Sin(m.Stride + k * MathF.PI) * 0.12f * m.Walk - swipe, 0, 0);
			m.Root.Position = new Vector3(m.X, m.Y, m.Z);
			if (m.Dead == 0) m.Root.Rotation = new Vector3(0, m.Yaw, 0);
			if (t.Growl != null && m.Dead == 0)
			{
				if (m.GrowlIn < 0) m.GrowlIn = 2 + (float)rnd.NextDouble() * 8;
				if ((m.GrowlIn -= dt) <= 0) { m.GrowlIn = 6 + (float)rnd.NextDouble() * 10; MobGrowl(m); }
			}
			// lighting: the same as blocks (sky light x time of day, or torch light, whichever is brighter)
			if ((m.LightTimer -= dt) <= 0)
			{
				m.LightTimer = 0.25f;
				int bx = (int)MathF.Floor(m.X), by = (int)MathF.Floor(m.Y + 0.4f), bz = (int)MathF.Floor(m.Z);
				m.Sky = world.SkyExposure(bx, by, bz, null); m.Torch = World.LightCurve(world.LightAt(bx, by, bz));
			}
			var dtint = sky.DayTint; var tc = new Vector3(1, 0.82f, 0.58f) * m.Torch;
			var light = new Vector3(Math.Max(dtint.X * m.Sky, tc.X), Math.Max(dtint.Y * m.Sky, tc.Y), Math.Max(dtint.Z * m.Sky, tc.Z));
			// glow: lights himself up, so he shows at full brightness in the dark (fades in as night falls)
			if (t.Glow) { float gl = 1 - sky.SkyDay; light = new Vector3(Math.Max(light.X, gl), Math.Max(light.Y, gl), Math.Max(light.Z, gl)); }
			if (m.Hurt > 0) { m.Hurt -= dt; light = light.Lerp(new Vector3(1, 0.15f, 0.1f), 0.6f); }
			foreach (var mat in m.Mats) mat.SetShaderParameter("light", light);
		}
	}
	// distance along the look ray to a mob's box, or infinity
	static float RayToMob(Vector3 o, Vector3 d, Mob m, float max)
	{
		var lo = new Vector3(m.X - m.Half, m.Y, m.Z - m.Half); var hi = new Vector3(m.X + m.Half, m.Y + m.Height, m.Z + m.Half);
		float t0 = 0, t1 = max;
		for (int a = 0; a < 3; a++)
		{
			if (Math.Abs(d[a]) < 1e-9f) { if (o[a] < lo[a] || o[a] > hi[a]) return float.PositiveInfinity; continue; }
			float ta = (lo[a] - o[a]) / d[a], tb = (hi[a] - o[a]) / d[a];
			if (ta > tb) (ta, tb) = (tb, ta);
			t0 = Math.Max(t0, ta); t1 = Math.Min(t1, tb);
			if (t0 > t1) return float.PositiveInfinity;
		}
		return t0;
	}
	Mob MobInSights(float max = 4)
	{
		var o = cam.GlobalPosition; var d = -cam.GlobalBasis.Z;
		Mob best = null; float bt = float.PositiveInfinity;
		foreach (var m in mobList) { if (m.Dead > 0) continue; float t = RayToMob(o, d, m, max); if (t < bt) { bt = t; best = m; } }
		if (best == null) return null;
		// a block in the way wins
		if (player.Raycast() is { } h && new Vector3(h.X + 0.5f - o.X, h.Y + 0.5f - o.Y, h.Z + 0.5f - o.Z).Length() - 0.5f < bt) return null;
		return best;
	}
	// from: who hit it (you, or another player in a hosted game); it gets knocked away from them
	void HitMob(Mob m, Vector3? from = null)
	{
		if (netClient) { hostConn?.Send(new Dictionary<string, object> { ["t"] = "hit", ["k"] = m.K }); m.Hurt = 0.3f; return; }   // joined game: the host's game does the rest
		var P = from ?? player.Pos;
		float d = MathF.Sqrt((m.X - P.X) * (m.X - P.X) + (m.Z - P.Z) * (m.Z - P.Z)); if (d == 0) d = 1;
		m.Vx = (m.X - P.X) / d * 6; m.Vz = (m.Z - P.Z) / d * 6; m.Vy = 5;
		int dmg = from == null ? Blocks.Get(inv.Held)?.Damage ?? 0 : 0;   // a sword (or axe) hits harder; friends' hits count as 1
		m.Hurt = 0.3f; m.Health -= dmg > 0 ? dmg : 1;
		if (!m.T.Hostile) m.Flee = 4;   // peaceful mobs run off; hostile ones don't back down
		if (m.T.Growl != null) { MobGrowl(m); m.GrowlIn = 4 + (float)rnd.NextDouble() * 6; }
		if (m.Health <= 0)
		{
			m.Dead = 0.001f;
			foreach (var (id, lo, hi, chance) in m.T.Drops)
				if (rnd.NextDouble() < chance) for (int n = lo + rnd.Next(hi - lo + 1); n > 0; n--) drops.Add(id, m.X, m.Y + 0.5f, m.Z);
		}
	}
}
