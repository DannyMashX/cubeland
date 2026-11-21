using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Cubeland.Net;
using Godot;

namespace Cubeland;

// Multiplayer, the same as the web version's (so Godot and web players can play together):
// one player hosts the world they're in and gets a 5-letter code; friends type the code to join.
// The host's game is the boss: it runs the mobs, water and dropped items, and saves the world. Joined games
// send the host what their player does (moving, breaking and placing blocks, hitting mobs, picking things up).
// Players look like a Grr, recoloured a cool colour, with their name above them.
public partial class Main
{
	// Everyone has to be on the same version. This is the web version this one plays with (the web version
	// checks it), so it changes whenever the web version's multiplayer or blocks change.
	public static string NetVersion => Version;   // (the game's own version, which matches the web game's)
	const string Prefix = "cubeland-", CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
	static readonly int[] Hues = { 200, 265, 300, 180, 235, 320, 215, 285 };   // blues, purples, magentas, cyans
	// Relay server (Metered): used when two players' connections can't reach each other directly.
	// This key is the one made for use in game pages; it can only fetch relay logins.
	const string RelayApp = "cubeland", RelayKey = "85e6efd2e639c189f35cc67fb7ea57d7ce5b";
	static List<(string[] urls, string user, string pass)> relayServers;

	// a friend's game you're joining (kept while the game restarts into their world)
	public record JoinInfo(string Code, int Seed, int Gen, string Mode, string Host, int H, int S);
	static JoinInfo joinInfo;
	bool netClient => joinInfo != null && current?.Id == "join-" + joinInfo.Code;

	Peer peer; DataConnection hostConn;
	bool hosting, welcomed, applying, netLeft;
	string netCode, myId = "host";
	float netTimer, lastHeard, joinTimer;
	public class Other { public string Name, Skin; public int Hue, Held; public int[] Gear; public float X, Y, Z, Yaw, Tx, Ty, Tz, Tyaw; public Mob Av; public Label3D Tag; }
	readonly Dictionary<string, Other> others = new();
	readonly Dictionary<string, string> skins = new();   // other players' skins (PNG data: text), by their id
	static Dictionary<string, object> SkinMsg(string id, string s) => new() { ["t"] = "skin", ["id"] = id ?? "", ["s"] = s ?? "" };
	// your skin changed: your hand is made again with it, and everyone else in the game gets it
	void SkinChanged()
	{
		RemakeHand();
		if (hosting) foreach (var f in friends.Values) f.conn.Send(SkinMsg("host", PlayerSkin.DataUrl));
		else if (netClient && welcomed && hostConn?.IsOpen == true) hostConn.Send(SkinMsg(myId, PlayerSkin.DataUrl));
	}
	readonly Dictionary<string, (DataConnection conn, string name, int hue, float seen)> friends = new();
	readonly List<int> pending = new();
	readonly HashSet<int> goneMobs = new();
	Dictionary<string, object> earlyWelcome;
	float Now => Time.GetTicksMsec() / 1000f;

	static async Task<List<(string[], string, string)>> IceServers()
	{
		var list = new List<(string[], string, string)> { (new[] { "stun:stun.l.google.com:19302" }, null, null) };
		if (relayServers == null)
		{
			try
			{
				using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
				var json = await http.GetStringAsync($"https://{RelayApp}.metered.live/api/v1/turn/credentials?apiKey={RelayKey}");
				relayServers = new();
				foreach (var s in JsonDocument.Parse(json).RootElement.EnumerateArray())
				{
					var u = s.GetProperty("urls");
					var urls = u.ValueKind == JsonValueKind.Array ? u.EnumerateArray().Select(x => x.GetString()).ToArray() : new[] { u.GetString() };
					relayServers.Add((urls, s.TryGetProperty("username", out var un) ? un.GetString() : null, s.TryGetProperty("credential", out var cr) ? cr.GetString() : null));
				}
			}
			catch (Exception e) { GD.PrintErr("Could not reach the relay server: " + e.Message); }
		}
		if (relayServers != null) list.AddRange(relayServers);
		return list;
	}
	static string MakeCode() => new(Enumerable.Range(0, 5).Select(_ => CodeChars[Random.Shared.Next(CodeChars.Length)]).ToArray());
	static double R2(float v) => Math.Round(v * 100) / 100;

	// ---------------- other players' bodies ----------------
	Other SetOther(string id, string name, int hue, float x, float y, float z, float yaw, int held = 0, int[] gear = null)
	{
		if (!others.TryGetValue(id, out var p)) { p = new Other { X = x, Y = y, Z = z, Yaw = yaw }; others[id] = p; }
		string sk = skins.GetValueOrDefault(id);
		if (p.Av != null && (p.Name != name || p.Hue != hue || p.Skin != sk)) RemoveBody(p);
		p.Name = name; p.Skin = sk; p.Hue = hue; p.Tx = x; p.Ty = y; p.Tz = z; p.Tyaw = yaw;
		if (p.Av == null && ready)
		{
			p.Av = new Mob { Type = "player", T = new MobType { Scale = 0.9f }, Hue = hue, Skin = PlayerSkin.TextureFrom(sk) };   // the Player model is 32 pixels tall; 0.9x = your height
			BuildMob(p.Av);
			p.Tag = new Label3D { Text = name, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Font = Skin.Font, FontSize = 48, OutlineSize = 0, PixelSize = 0.006f, NoDepthTest = false, Position = new Vector3(0, p.Av.Height + 0.35f, 0), Modulate = Colors.White, OutlineModulate = new Color(0, 0, 0, 0.6f) };
			p.Av.Root.AddChild(p.Tag);
		}
		p.Held = held;
		if (p.Av != null) SetHeld(p.Av, held);   // what they're holding shows in their hand
		p.Gear = gear;
		if (p.Av != null) SetGear(p.Av, gear);   // and what they wear on them
		return p;
	}
	void RemoveBody(Other p) { p.Av?.Root.QueueFree(); p.Av = null; p.Tag = null; }
	void RemoveOther(string id) { if (others.Remove(id, out var p)) RemoveBody(p); }
	void AnimateOthers(float dt)
	{
		float a = Math.Min(1, dt * 10);
		foreach (var p in others.Values)
		{
			var av = p.Av; if (av == null) continue;
			float ox = p.X, oz = p.Z;
			p.X += (p.Tx - p.X) * a; p.Y += (p.Ty - p.Y) * a; p.Z += (p.Tz - p.Z) * a;
			p.Yaw += Turn(p.Yaw, p.Tyaw) * a;
			float moved = MathF.Sqrt((p.X - ox) * (p.X - ox) + (p.Z - oz) * (p.Z - oz));
			av.Stride += moved * 5.5f;
			av.Walk += ((moved > 0.002f ? 1 : 0) - av.Walk) * Math.Min(1, dt * 8);
			PoseBody(av, dt, 0, false);
			av.Root.Position = new Vector3(p.X, p.Y, p.Z); av.Root.Rotation = new Vector3(0, p.Yaw, 0);
			if ((av.LightTimer -= dt) <= 0)
			{
				av.LightTimer = 0.25f;
				int bx = (int)MathF.Floor(p.X), by = (int)MathF.Floor(p.Y + 0.4f), bz = (int)MathF.Floor(p.Z);
				av.Sky = world.SkyExposure(bx, by, bz, null); av.Torch = World.LightCurve(world.LightAt(bx, by, bz));
			}
			var t = sky.DayTint; var tc = new Vector3(1, 0.82f, 0.58f) * Math.Max(av.Torch, HeldLightAt(new Vector3(p.X, p.Y + 0.9f, p.Z)));
			var light = new Vector3(Math.Max(t.X * av.Sky, tc.X), Math.Max(t.Y * av.Sky, tc.Y), Math.Max(t.Z * av.Sky, tc.Z));
			foreach (var mat in av.Mats) mat.SetShaderParameter("light", light);
		}
	}

	// ---------------- block changes ----------------
	void NetBlockChanged(int i, int id)
	{
		if (hosting ? friends.Count > 0 : (netClient && !applying)) { pending.Add(i); pending.Add(id); }
	}
	static List<int> Ints(List<object> l) => l?.Select(o => (int)BinaryPack.Num(o)).ToList() ?? new();
	void ApplyNetPieces(List<int> list)
	{
		for (int k = 0; k + 8 < list.Count; k += 9)
		{
			var a = new byte[8];
			for (int j = 0; j < 8; j++) { int id = list[k + 1 + j]; var d = Blocks.Get(id); a[j] = (byte)(d != null && !d.Item && id != Blocks.Pieces ? id : 0); }
			if (a.Any(v => v != 0)) world.PieceMap[list[k]] = a;
		}
	}
	// apply block changes from someone else (without sending them back out again, unless you're the host)
	void ApplySets(List<int> list, List<int> pcs)
	{
		applying = true;
		var before = new Dictionary<int, byte[]>(world.PieceMap);
		ApplyNetPieces(pcs);
		for (int k = 0; k + 1 < list.Count; k += 2)
		{
			int i = list[k], id = list[k + 1];
			if (i < 0 || i >= world.Blocks.Length || (id != 0 && Blocks.Get(id) == null) || (world.Blocks[i] == id && id != Blocks.Pieces)) continue;
			if (id == Blocks.Pieces && !world.PieceMap.ContainsKey(i)) continue;
			if (id == Blocks.Pieces && world.Blocks[i] == Blocks.Pieces && before.TryGetValue(i, out var was) && was.SequenceEqual(world.PieceMap[i])) continue;
			var (x, y, z) = World.FromIdx(i);
			world.SetBlock(x, y, z, id);
		}
		applying = false;
	}
	void Flush()
	{
		if (pending.Count == 0) return;
		var p = new List<object>();
		for (int k = 0; k < pending.Count; k += 2)
			if (pending[k + 1] == Blocks.Pieces && world.PieceMap.TryGetValue(pending[k], out var a)) { p.Add(pending[k]); foreach (var v in a) p.Add((int)v); }
		var msg = new Dictionary<string, object> { ["t"] = "sets", ["l"] = pending.Cast<object>().ToList(), ["p"] = p };
		pending.Clear();
		if (hosting) foreach (var f in friends.Values) f.conn.Send(msg);
		else hostConn?.Send(msg);
	}

	// ---------------- hosting ----------------
	async void Host()
	{
		if (netClient) { NetStatus("This is a friend's world, so only they can host it."); return; }
		if (tempWorld) { NetStatus("Test worlds can't be hosted."); return; }
		NetStatus("Starting...");
		var ice = await IceServers();
		var code = MakeCode();
		var p = new Peer(Prefix + code, ice);
		peer = p;
		p.OnOpen += () => { hosting = true; netCode = code; NetStatus(""); RefreshNetUi(); GD.Print("net: hosting, code " + code); };
		p.OnError += (type, msg) =>
		{
			if (type == "unavailable-id") { p.Destroy(); peer = null; Host(); }   // that code is taken: pick another
			else if (!hosting) { p.Destroy(); peer = null; NetStatus("Couldn't start multiplayer. Are you online?"); }
		};
		p.OnDisconnected += () => { if (hosting) { NetStatus("Lost the multiplayer server. Friends already here can keep playing."); } };
		p.OnConnection += conn =>
		{
			conn.OnData += d => { try { HostGot(conn, d as Dictionary<string, object>); } catch (Exception e) { GD.PrintErr(e); } };
			conn.OnClose += () => DropFriend(conn.PeerId);
		};
	}
	void StopHosting()
	{
		foreach (var id in friends.Keys.ToArray()) DropFriend(id);
		peer?.Destroy(); peer = null; hosting = false; netCode = null;
		RefreshNetUi();
	}
	void DropFriend(string id)
	{
		if (!friends.Remove(id, out var f)) return;
		RemoveOther(id); skins.Remove(id);
		f.conn.Close();
		RefreshNetUi();
	}
	void HostGot(DataConnection conn, Dictionary<string, object> d)
	{
		if (d == null) return;
		bool known = friends.TryGetValue(conn.PeerId, out var f);
		if (known) friends[conn.PeerId] = f with { seen = Now };
		string t = d.S("t");
		if (t == "hello")   // someone checking which world this is, before they load it
			conn.Send(new Dictionary<string, object> { ["t"] = "info", ["ver"] = NetVersion, ["seed"] = current.Seed, ["gen"] = current.Gen, ["h"] = current.H, ["s"] = current.S, ["mode"] = current.Mode, ["host"] = settings.Name });
		else if (t == "join")
		{
			if (d.S("ver") != NetVersion) { conn.Send(new Dictionary<string, object> { ["t"] = "refuse", ["why"] = $"You need the same version of the game. They have {NetVersion}, you have {d.S("ver")}." }); return; }
			var used = new HashSet<int>(friends.Values.Select(x => x.hue)) { Hues[0] };
			int hue = Hues.Cast<int?>().FirstOrDefault(h => !used.Contains(h.Value)) ?? Hues[1 + friends.Count % (Hues.Length - 1)];
			string name = (d.S("name") ?? "Player"); if (name.Length > 16) name = name[..16];
			friends[conn.PeerId] = (conn, name, hue, Now);
			GD.Print($"net: {name} joined");
			SetOther(conn.PeerId, name, hue, player.Pos.X + 1, player.Pos.Y, player.Pos.Z, 0);
			var idxs = new byte[world.Edits.Count * 4]; var ids = new byte[world.Edits.Count]; int k = 0;
			foreach (var (i, id) in world.Edits) { BitConverter.TryWriteBytes(idxs.AsSpan(k * 4), (uint)i); ids[k++] = (byte)id; }
			var pcs = new List<object>(); foreach (var (i, a) in world.PieceMap) { pcs.Add(i); foreach (var v in a) pcs.Add((int)v); }
			conn.Send(new Dictionary<string, object> { ["t"] = "welcome", ["id"] = conn.PeerId, ["idxs"] = idxs, ["ids"] = ids, ["pieces"] = pcs, ["time"] = sky.TimeOfDay,
				["x"] = player.Pos.X + 1, ["y"] = player.Pos.Y, ["z"] = player.Pos.Z, ["yaw"] = player.Yaw });
			// everyone's skins: yours, and the other friends'
			if (PlayerSkin.DataUrl != null) conn.Send(SkinMsg("host", PlayerSkin.DataUrl));
			foreach (var (sid, s) in skins) if (sid != conn.PeerId && friends.ContainsKey(sid)) conn.Send(SkinMsg(sid, s));
			RefreshNetUi();
		}
		else if (!known) return;
		else if (t == "me") SetOther(conn.PeerId, f.name, f.hue, d.F("x"), d.F("y"), d.F("z"), d.F("yaw"), d.I("h"), Ints(d.L("g")).ToArray());
		else if (t == "sets" && d.L("l") is { } l) ApplySets(Ints(l), Ints(d.L("p")));
		else if (t == "skin")
		{   // a friend's skin: shown on them here, and passed on to everyone else
			string s = PlayerSkin.Valid(d.S("s")) ? d.S("s") : null;
			if (s != null) skins[conn.PeerId] = s; else skins.Remove(conn.PeerId);
			foreach (var (fid, o) in friends) if (fid != conn.PeerId) o.conn.Send(SkinMsg(conn.PeerId, s));
		}
		else if (t == "hit")
		{
			var m = mobList.FirstOrDefault(m => m.K == d.I("k") && m.Dead == 0);
			if (m != null && others.TryGetValue(conn.PeerId, out var o)) HitMob(m, new Vector3(o.X, o.Y, o.Z));
		}
		else if (t == "feed")
		{   // a friend fed a cow Grain (if it didn't want it, they get the Grain back)
			var m = mobList.FirstOrDefault(m => m.K == d.I("k") && m.Dead == 0 && m.T.Cow);
			if (m == null || !Feed(m)) conn.Send(new Dictionary<string, object> { ["t"] = "give", ["id"] = Blocks.Grain });
		}
		else if (t == "pickup")
		{
			var dr = drops.All.FirstOrDefault(x => x.K == d.I("k"));
			if (dr != null && dr.Age > 0.5f) { drops.Remove(dr); conn.Send(new Dictionary<string, object> { ["t"] = "give", ["id"] = dr.Id }); }
		}
	}
	void HostTick()
	{
		// h: the item each player is holding (0 = nothing)
		var players = new List<object> { new Dictionary<string, object> { ["id"] = "host", ["name"] = settings.Name, ["hue"] = Hues[0], ["x"] = R2(player.Pos.X), ["y"] = R2(player.Pos.Y), ["z"] = R2(player.Pos.Z), ["yaw"] = R2(player.Yaw), ["h"] = inv.Held, ["g"] = inv.GearIds() } };
		foreach (var (id, f) in friends) if (others.TryGetValue(id, out var p))
			players.Add(new Dictionary<string, object> { ["id"] = id, ["name"] = f.name, ["hue"] = f.hue, ["x"] = R2(p.Tx), ["y"] = R2(p.Ty), ["z"] = R2(p.Tz), ["yaw"] = R2(p.Tyaw), ["h"] = p.Held, ["g"] = p.Gear ?? Array.Empty<int>() });
		// (a mob's 9th number is 1 while it's fading away, 2 while it's throwing; a cow has a 10th: how it looks, see CowLook)
		var ms = mobList.Select(m => { var l = new List<object> { m.K, m.Type, R2(m.X), R2(m.Y), R2(m.Z), R2(m.Yaw), m.Dead > 0 ? 1 : 0, m.Hurt > 0 ? 1 : 0, m.Fade > 0 ? 1 : m.ThrowT > 0 ? 2 : 0 }; if (m.T.Cow) l.Add(CowLook(m)); return (m, l: (object)l); }).ToList();
		var ds = drops.All.Select(d => (object)new List<object> { d.K, d.Id, R2(d.P.X), R2(d.P.Y), R2(d.P.Z) }).ToList();
		var rs = rocks.Select(r => (object)new List<object> { r.K, R2(r.P.X), R2(r.P.Y), R2(r.P.Z) }).ToList();
		var fs = falling.All.Select(b => (object)new List<object> { b.K, b.Id, b.X, R2(b.Y), b.Z }).ToList();
		foreach (var (id, f) in friends.ToArray())
		{
			if (Now - f.seen > 30) { DropFriend(id); continue; }   // not heard from them in a while: they've gone
			// (each friend is only sent the mobs within the render distance of them)
			float fx = player.Pos.X, fz = player.Pos.Z, r = RenderBlocks;
			if (others.TryGetValue(id, out var o)) { fx = o.X; fz = o.Z; }
			var near = ms.Where(e => (e.m.X - fx) * (e.m.X - fx) + (e.m.Z - fz) * (e.m.Z - fz) < r * r).Select(e => e.l).ToList();
			f.conn.Send(new Dictionary<string, object> { ["t"] = "tick", ["time"] = sky.TimeOfDay, ["players"] = players, ["mobs"] = near, ["drops"] = ds, ["rocks"] = rs, ["falls"] = fs });
		}
	}

	// ---------------- joining ----------------
	// step 1 (from your own world): ask their game which world it is, then restart into it
	async void Join(string code)
	{
		code = new string((code ?? "").ToUpperInvariant().Where(c => char.IsLetterOrDigit(c)).ToArray());
		if (code.Length != 5) { NetStatus("Codes are 5 letters and numbers."); return; }
		if (hosting) { NetStatus("Stop hosting first."); return; }
		NetStatus("Looking for the game...");
		var ice = await IceServers();
		var p = new Peer(null, ice); peer = p;
		bool done = false, reaching = false; joinTimer = 15;
		void Fail(string text) { if (done) return; done = true; joinTimer = 0; NetStatus(text); p.Destroy(); if (peer == p) peer = null; }
		joinFail = () => Fail(reaching
			? $"Found the game {code}, but couldn't link up with it. Your two internet connections can't reach each other directly (this happens with some home routers, school networks and phone hotspots)."
			: $"Couldn't find a game with the code {code}.");
		p.OnError += (type, _) => Fail(type == "peer-unavailable" ? $"Couldn't find a game with the code {code}." : "Couldn't connect. Are you online?");
		p.OnOpen += () =>
		{
			// the server says "peer-unavailable" within a second or two when there's no such game, so after that it was found
			GetTree().CreateTimer(2.5).Timeout += () => { if (!done) { reaching = true; NetStatus("Found the game, linking up..."); } };
			var c = p.Connect(Prefix + code);
			c.OnOpen += () => c.Send(new Dictionary<string, object> { ["t"] = "hello" });
			c.OnData += o =>
			{
				if (o is not Dictionary<string, object> d || d.S("t") != "info" || done) return;
				if (d.S("ver") != NetVersion) { Fail($"{d.S("host")} is playing version {d.S("ver")}, and you have {NetVersion}. You both need the same version."); return; }
				done = true; joinTimer = 0;
				NetStatus("Joining...");
				p.Destroy(); peer = null;
				joinInfo = new JoinInfo(code, d.I("seed"), d.I("gen") == 0 ? 1 : d.I("gen"), d.S("mode"), d.S("host") ?? "Player", d.I("h") == 640 ? 640 : 192, d.I("s") == 1280 ? 1280 : 640);
				SwitchWorld("join-" + code, true);
			};
		};
	}
	Action joinFail;
	// step 2 (after the restart, in their world): connect for real and get the blocks they've changed
	async void ConnectToHost()
	{
		var ice = await IceServers();
		var p = new Peer(null, ice); peer = p;
		p.OnError += (type, _) => { if (!welcomed) GiveUp(type == "peer-unavailable" ? $"The game with the code {joinInfo.Code} has closed." : "Couldn't connect to your friend's game."); };
		p.OnOpen += () =>
		{
			hostConn = p.Connect(Prefix + joinInfo.Code);
			hostConn.OnOpen += () => hostConn.Send(new Dictionary<string, object> { ["t"] = "join", ["name"] = settings.Name, ["ver"] = NetVersion });
			hostConn.OnData += d => { try { ClientGot(d as Dictionary<string, object>); } catch (Exception e) { GD.PrintErr(e); } };
			hostConn.OnClose += HostGone;
		};
		lastHeard = Now;
	}
	void GiveUp(string text) { if (netLeft || !IsInsideTree()) return; Ask(text, LeaveGame); onCancel = LeaveGame; Input.MouseMode = Input.MouseModeEnum.Visible; netLeft = true; }
	void HostGone()
	{
		if (netLeft) return;
		GiveUp($"{joinInfo.Host} stopped hosting (or the connection was lost). Back to your own worlds.");
	}
	void LeaveGame()
	{
		netLeft = true;
		peer?.Destroy(); peer = null;
		joinInfo = null;
		discarding = true;   // (nothing of theirs to save here)
		openWorldId = null; playOnLoad = false;
		Input.MouseMode = Input.MouseModeEnum.Visible;
		GetTree().ReloadCurrentScene();
	}
	void ClientGot(Dictionary<string, object> d)
	{
		if (d == null) return;
		lastHeard = Now;
		string t = d.S("t");
		if (t == "refuse") GiveUp(d.S("why"));
		else if (t == "welcome") { if (ready) ApplyWelcome(d); else earlyWelcome = d; }
		else if (t == "tick" && welcomed)
		{
			sky.TimeOfDay = d.F("time");
			var seenP = new HashSet<string>();
			foreach (var o in d.L("players") ?? new())
			{
				if (o is not Dictionary<string, object> p || p.S("id") == myId) continue;
				seenP.Add(p.S("id")); SetOther(p.S("id"), p.S("name") ?? "Player", p.I("hue"), p.F("x"), p.F("y"), p.F("z"), p.F("yaw"), p.I("h"), Ints(p.L("g")).ToArray());
			}
			foreach (var id in others.Keys.ToArray()) if (!seenP.Contains(id)) RemoveOther(id);
			// mobs: make, move or remove ours to match the host's
			var byK = mobList.ToDictionary(m => m.K); var seenM = new HashSet<int>();
			foreach (var o in d.L("mobs") ?? new())
			{
				if (o is not List<object> a || a.Count < 8) continue;
				int k = (int)BinaryPack.Num(a[0]); string type = a[1] as string; float x = (float)BinaryPack.Num(a[2]), y = (float)BinaryPack.Num(a[3]), z = (float)BinaryPack.Num(a[4]), yaw = (float)BinaryPack.Num(a[5]);
				bool isDead = BinaryPack.Num(a[6]) != 0, hurt = BinaryPack.Num(a[7]) != 0;
				seenM.Add(k);
				if (!byK.TryGetValue(k, out var m))
				{
					if (isDead || goneMobs.Contains(k) || type == null || !MobTypes.ContainsKey(type) || !models.ContainsKey(type)) continue;
					m = SpawnMob(type, x, y, z, a.Count > 9 ? (int)BinaryPack.Num(a[9]) & 3 : 0); m.K = k; m.Yaw = yaw;
				}
				m.Tx = x; m.Ty = y; m.Tz = z; m.Tyaw = yaw;
				if (isDead && m.Dead == 0) { m.Dead = 0.001f; goneMobs.Add(k); }
				if (hurt && !m.NetHurt) m.Hurt = 0.3f;
				m.NetHurt = hurt;
				int act = a.Count > 8 ? (int)BinaryPack.Num(a[8]) : 0;
				if (act == 1 && m.Fade == 0) StartFade(m);
				if (act == 2 && m.ThrowT == 0) m.ThrowT = 0.001f;
				if (m.T.Cow && a.Count > 9) ApplyCowLook(m, (int)BinaryPack.Num(a[9]));
			}
			foreach (var m in mobList.ToArray()) if (m.Dead == 0 && !seenM.Contains(m.K)) RemoveMob(m);
			// Rocks thrown by Skeletons
			var byR = rocks.ToDictionary(r => r.K); var seenR = new HashSet<int>();
			foreach (var o in d.L("rocks") ?? new())
			{
				if (o is not List<object> a || a.Count < 4) continue;
				int k = (int)BinaryPack.Num(a[0]);
				var at = new Vector3((float)BinaryPack.Num(a[1]), (float)BinaryPack.Num(a[2]), (float)BinaryPack.Num(a[3]));
				seenR.Add(k);
				if (!byR.TryGetValue(k, out var r)) { r = MakeRock(at); r.K = k; }
				r.T = at;
			}
			foreach (var r in rocks.ToArray()) if (!seenR.Contains(r.K)) RemoveRock(r);
			// dropped items
			var byD = drops.All.ToDictionary(x => x.K); var seenD = new HashSet<int>();
			foreach (var o in d.L("drops") ?? new())
			{
				if (o is not List<object> a || a.Count < 5) continue;
				int k = (int)BinaryPack.Num(a[0]), id = (int)BinaryPack.Num(a[1]);
				var at = new Vector3((float)BinaryPack.Num(a[2]), (float)BinaryPack.Num(a[3]), (float)BinaryPack.Num(a[4]));
				seenD.Add(k);
				if (!byD.TryGetValue(k, out var dr)) { if (Blocks.Get(id) == null) continue; dr = drops.Add(id, at.X, at.Y, at.Z); dr.K = k; dr.V = Vector3.Zero; dr.P = at; }
				dr.T = at;
			}
			foreach (var dr in drops.All.ToArray()) if (!seenD.Contains(dr.K)) drops.Remove(dr);
			// falling Sand, Gravel and Dirt (the host's game puts the block down where each one lands)
			var byF = falling.All.ToDictionary(x => x.K); var seenF = new HashSet<int>();
			foreach (var o in d.L("falls") ?? new())
			{
				if (o is not List<object> a || a.Count < 5) continue;
				int k = (int)BinaryPack.Num(a[0]), id = (int)BinaryPack.Num(a[1]);
				if (!Falling.Falls.Contains(id)) continue;
				float y = (float)BinaryPack.Num(a[3]);
				seenF.Add(k);
				if (!byF.TryGetValue(k, out var fb)) { fb = falling.Add(id, (int)BinaryPack.Num(a[2]), y, (int)BinaryPack.Num(a[4])); fb.K = k; }
				fb.Ty = y;
			}
			foreach (var fb in falling.All.ToArray()) if (!seenF.Contains(fb.K)) falling.Remove(fb);
		}
		else if (t == "sets" && d.L("l") is { } l) ApplySets(Ints(l), Ints(d.L("p")));
		else if (t == "give") inv.Give(d.I("id"));
		else if (t == "skin" && d.S("id") is { } sid)
		{
			if (PlayerSkin.Valid(d.S("s"))) skins[sid] = d.S("s"); else skins.Remove(sid);
		}
	}
	void ApplyWelcome(Dictionary<string, object> d)
	{
		myId = d.S("id") ?? myId;
		ApplyNetPieces(Ints(d.L("pieces")));
		var ib = d.B("idxs") ?? Array.Empty<byte>(); var idb = d.B("ids") ?? Array.Empty<byte>();
		var idxs = new List<int>(); var ids = new List<int>();
		for (int k = 0; k < idb.Length && k * 4 + 3 < ib.Length; k++) { idxs.Add((int)BitConverter.ToUInt32(ib, k * 4)); ids.Add(idb[k]); }
		world.ApplyEdits(idxs, ids);
		var dirtyNow = new HashSet<int>();
		foreach (int i in idxs) { var (x, y, z) = World.FromIdx(i); World.MarkDirty(x, y, z, dirtyNow); }
		lock (dirty) foreach (int c in dirtyNow) dirty.Add(c);
		player.Pos = new Vector3(d.F("x"), d.F("y"), d.F("z")); player.Yaw = d.F("yaw"); player.Vel = Vector3.Zero;
		sky.TimeOfDay = d.F("time");
		welcomed = true;
		if (PlayerSkin.DataUrl != null) hostConn?.Send(SkinMsg(myId, PlayerSkin.DataUrl));   // your skin, for everyone here
		RefreshNetUi();
		GD.Print($"net: welcomed into {current.Name}, {idxs.Count} changed blocks");
	}

	// ---------------- every frame ----------------
	void NetUpdate(float dt)
	{
		peer?.Poll(dt);
		if (joinTimer > 0 && (joinTimer -= dt) <= 0) joinFail?.Invoke();
		if (!ready) return;
		if (earlyWelcome != null) { var w = earlyWelcome; earlyWelcome = null; ApplyWelcome(w); }
		AnimateOthers(dt);
		// ten times a second
		if ((netTimer -= dt) > 0) return;
		netTimer = 0.1f;
		Flush();
		if (hosting && friends.Count > 0) HostTick();
		if (netClient && welcomed && hostConn?.IsOpen == true)
			hostConn.Send(new Dictionary<string, object> { ["t"] = "me", ["x"] = R2(player.Pos.X), ["y"] = R2(player.Pos.Y), ["z"] = R2(player.Pos.Z), ["yaw"] = R2(player.Yaw), ["h"] = inv.Held, ["g"] = inv.GearIds() });
	if (netClient && welcomed && Now - lastHeard > 30) HostGone();
	}
	string NetInfoLine() => hosting ? $"\nHosting: code {netCode} · {friends.Count} friend{(friends.Count == 1 ? "" : "s")} here" : netClient ? $"\nIn {current.Name}" + (welcomed ? "" : "\nJoining...") : "";
	// (host) keep a mob around if it's near any friend, even when it's far from you
	// is another player here holding a lit torch within r blocks of p? (Gloomlings fade away from them)
	bool FriendTorchNear(Vector3 p, float r) => others.Values.Any(o => IsLitTorch(o.Held) && new Vector3(o.X - p.X, o.Y + 1 - p.Y, o.Z - p.Z).Length() < r);
	bool NearAFriend(Mob m) => others.Values.Any(p => MathF.Sqrt((m.X - p.X) * (m.X - p.X) + (m.Z - p.Z) * (m.Z - p.Z)) < MobRange(m.T));
	float MobRange(MobType t) => t.Cow ? RenderBlocks : Math.Min(RenderBlocks, ForgetMobs);   // (how far from everyone a mob keeps going)

	// ---------------- the Multiplayer menu ----------------
	LineEdit mpName, mpCode; Label mpStatus, mpNow, mpWho; Control mpOff, mpOn; Button mpLeave, pauseMultiBtn;
	void BuildMultiplayerPanel(VBoxContainer b)
	{
		var nameRow = new HBoxContainer(); nameRow.AddChild(Text("Your name ", 15));
		mpName = new LineEdit { Text = settings.Name, MaxLength = 16, PlaceholderText = "Your name", CustomMinimumSize = new Vector2(220, 34) };
		mpName.TextChanged += s => { settings.Name = s.Trim() == "" ? "Player" : s.Trim(); ApplySettings(); };
		nameRow.AddChild(mpName); b.AddChild(nameRow);
		var off = new VBoxContainer(); off.AddThemeConstantOverride("separation", 8); mpOff = off; b.AddChild(off);
		off.AddChild(Text("Host this world", 17));
		off.AddChild(Wrap(Text("Friends can join the world you're in now, from the Godot version or the web version. You'll get a code to give them.", 13, 0.8f)));
		off.AddChild(Btn("Host", Host, 160));
		off.AddChild(Text("Join a friend", 17));
		var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 8);
		mpCode = new LineEdit { MaxLength = 5, PlaceholderText = "Their code", CustomMinimumSize = new Vector2(160, 34) };
		mpCode.TextSubmitted += s => Join(s);
		row.AddChild(mpCode); row.AddChild(Btn("Join", () => Join(mpCode.Text), 100)); off.AddChild(row);
		var on = new VBoxContainer(); on.AddThemeConstantOverride("separation", 8); mpOn = on; b.AddChild(on);
		mpNow = Text("", 18); on.AddChild(mpNow);
		mpWho = Wrap(Text("", 13, 0.8f)); on.AddChild(mpWho);
		mpLeave = Btn("Leave", () => { if (hosting) StopHosting(); else if (netClient) LeaveGame(); }, 160); on.AddChild(mpLeave);
		mpStatus = Wrap(Text("", 14)); mpStatus.AddThemeColorOverride("font_color", new Color(1, 0.85f, 0.5f)); b.AddChild(mpStatus);
		RefreshNetUi();
	}
	static Label Wrap(Label l) { l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(440, 0); return l; }
	void NetStatus(string s) { if (mpStatus != null) mpStatus.Text = s ?? ""; if (!string.IsNullOrEmpty(s)) GD.Print("net: " + s); }
	void RefreshNetUi()
	{
		if (mpOff == null) return;
		bool busy = hosting || netClient;
		mpOff.Visible = !busy; mpOn.Visible = busy; mpName.Editable = !busy;
		if (hosting)
		{
			mpNow.Text = "Your code: " + netCode;
			mpWho.Text = friends.Count > 0 ? "Here with you: " + string.Join(", ", friends.Values.Select(f => f.name)) : "Give your friends the code. They type it under \"Join a friend\".";
			mpLeave.Text = "Stop hosting";
		}
		else if (netClient)
		{
			mpNow.Text = $"You're in {current.Name}.";
			mpWho.Text = "Their game saves this world. Leaving takes you back to your own worlds.";
			mpLeave.Text = "Leave";
		}
		if (pauseMultiBtn != null) pauseMultiBtn.Text = hosting ? $"Hosting ({netCode})" : "Multiplayer";
	}
}
