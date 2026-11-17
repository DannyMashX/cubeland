using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace Cubeland.Net;

// A small copy of PeerJS 1.5.4 (what the web version uses), so Godot games and web games can join each other.
// The free PeerJS server only introduces the two games (over a WebSocket); after that they talk directly over
// WebRTC (Godot's WebRTC add-on in addons/webrtc_native), or through the relay server when they can't.
// Call Poll() every frame.
public class Peer
{
	public const string Version = "1.5.4", Host = "0.peerjs.com";
	public string Id { get; }
	public bool Open { get; private set; }
	public bool Destroyed { get; private set; }
	public event Action OnOpen;
	public event Action<string, string> OnError;     // (type, message) types like PeerJS: "unavailable-id", "peer-unavailable", "network", "server-error"
	public event Action<DataConnection> OnConnection;
	public event Action OnDisconnected;

	readonly WebSocketPeer ws = new();
	readonly Godot.Collections.Dictionary config;
	readonly Dictionary<string, DataConnection> conns = new();          // connection id -> connection
	readonly Dictionary<string, List<JsonObject>> lost = new();          // messages for connections that don't exist yet
	double heartbeat;
	bool wsOpened;

	// iceServers: [{urls, username, credential}] (STUN and the relay server)
	public Peer(string id, IEnumerable<(string[] urls, string user, string pass)> iceServers)
	{
		Id = id ?? Guid.NewGuid().ToString();
		var list = new Godot.Collections.Array();
		foreach (var (urls, user, pass) in iceServers)
		{
			// libjuice (inside the WebRTC add-on) only does UDP relaying, so skip the TCP/TLS relay addresses
			var ok = urls.Where(u => !u.StartsWith("turns:") && !u.Contains("transport=tcp")).ToArray();
			if (ok.Length == 0) continue;
			var s = new Godot.Collections.Dictionary { ["urls"] = new Godot.Collections.Array(ok.Select(u => Variant.From(u))) };
			if (user != null) { s["username"] = user; s["credential"] = pass ?? ""; }
			list.Add(s);
		}
		config = new Godot.Collections.Dictionary { ["iceServers"] = list };
		string token = Convert(Random.Shared.NextInt64(), 36);
		var err = ws.ConnectToUrl($"wss://{Host}:443/peerjs?key=peerjs&id={Uri.EscapeDataString(Id)}&token={token}&version={Version}");
		if (err != Error.Ok) Fail("network", "Couldn't reach the multiplayer server.");
	}
	static string Convert(long v, int b) { v = Math.Abs(v); const string c = "0123456789abcdefghijklmnopqrstuvwxyz"; var s = ""; do { s = c[(int)(v % b)] + s; v /= b; } while (v > 0); return s; }

	void Fail(string type, string msg) { OnError?.Invoke(type, msg); }

	public DataConnection Connect(string peerId)
	{
		var c = new DataConnection(this, peerId, "dc_" + Convert(Random.Shared.NextInt64(), 36), config, null);
		conns[c.ConnectionId] = c;
		return c;
	}

	internal void SendServer(object msg)
	{
		if (Destroyed || ws.GetReadyState() != WebSocketPeer.State.Open) return;
		ws.SendText(JsonSerializer.Serialize(msg));
	}
	internal void Forget(DataConnection c) => conns.Remove(c.ConnectionId);

	public void Poll(double dt)
	{
		if (Destroyed) return;
		ws.Poll();
		var st = ws.GetReadyState();
		if (st == WebSocketPeer.State.Open)
		{
			wsOpened = true;
			if ((heartbeat += dt) > 5) { heartbeat = 0; SendServer(new { type = "HEARTBEAT" }); }
			while (ws.GetAvailablePacketCount() > 0)
			{
				var text = ws.GetPacket().GetStringFromUtf8();
				try { if (JsonNode.Parse(text) is JsonObject m) Handle(m); }
				catch (Exception e) { GD.PrintErr("multiplayer: bad server message: " + e.Message); }
			}
		}
		else if (st == WebSocketPeer.State.Closed)
		{
			bool was = Open || wsOpened;
			Open = false;
			if (was) OnDisconnected?.Invoke();
			else Fail("network", "Couldn't reach the multiplayer server.");
			Destroyed = true;
		}
		foreach (var c in conns.Values.ToArray()) c.Poll();
	}

	void Handle(JsonObject m)
	{
		string type = (string)m["type"], src = (string)m["src"];
		var payload = m["payload"] as JsonObject;
		switch (type)
		{
			case "OPEN": Open = true; OnOpen?.Invoke(); break;
			case "ERROR": Fail("server-error", (string)payload?["msg"] ?? "Server error"); break;
			case "ID-TAKEN": Fail("unavailable-id", $"ID \"{Id}\" is taken"); break;
			case "INVALID-KEY": Fail("invalid-key", "Invalid key"); break;
			case "EXPIRE": Fail("peer-unavailable", $"Could not connect to peer {src}"); break;
			case "LEAVE": foreach (var c in conns.Values.Where(c => c.PeerId == src).ToArray()) c.Close(); break;
			case "OFFER":
			{
				if (payload == null || (string)payload["type"] != "data") return;
				string cid = (string)payload["connectionId"];
				if (conns.TryGetValue(cid, out var old)) old.Close();
				var c = new DataConnection(this, src, cid, config, payload);
				conns[cid] = c;
				OnConnection?.Invoke(c);
				if (lost.Remove(cid, out var waiting)) foreach (var w in waiting) c.Handle(w);
				break;
			}
			default:
			{
				if (payload == null) return;
				string cid = (string)payload["connectionId"];
				if (cid == null) return;
				if (conns.TryGetValue(cid, out var c)) c.Handle(m);
				else { if (!lost.TryGetValue(cid, out var l)) lost[cid] = l = new(); l.Add(m); }
				break;
			}
		}
	}

	public void Destroy()
	{
		if (Destroyed) return;
		foreach (var c in conns.Values.ToArray()) c.Close();
		Destroyed = true; Open = false;
		ws.Close();
	}
}

// One link to another game. Messages are BinaryPack'd (PeerJS's "binary" serialization), split into
// 16300-byte chunks when they're bigger, like PeerJS does.
public class DataConnection
{
	public const int ChunkMtu = 16300;
	public string PeerId { get; }
	public string ConnectionId { get; }
	public bool IsOpen { get; private set; }
	public event Action OnOpen;
	public event Action<object> OnData;
	public event Action OnClose;

	readonly Peer peer;
	readonly WebRtcPeerConnection pc = new();
	WebRtcDataChannel dc;
	bool closed;
	int dataCount = 1;
	readonly Dictionary<long, (byte[][] parts, int count)> chunks = new();
	readonly List<(string media, int index, string name)> earlyCandidates = new();
	bool remoteSet, localSet;

	internal DataConnection(Peer peer, string peerId, string connectionId, Godot.Collections.Dictionary config, JsonObject offer)
	{
		this.peer = peer; PeerId = peerId; ConnectionId = connectionId;
		pc.Initialize(config);
		pc.IceCandidateCreated += (media, index, name) =>
			peer.SendServer(new { type = "CANDIDATE", payload = new { candidate = new { candidate = name, sdpMid = media, sdpMLineIndex = index }, type = "data", connectionId = ConnectionId }, dst = PeerId });
		pc.SessionDescriptionCreated += (type, sdp) =>
		{
			pc.SetLocalDescription(type, sdp);
			localSet = true;
			FlushCandidates();
			if (type == "offer")
				peer.SendServer(new { type = "OFFER", payload = new { sdp = new { type, sdp }, type = "data", connectionId = ConnectionId, label = ConnectionId, reliable = true, serialization = "binary" }, dst = PeerId });
			else
				peer.SendServer(new { type = "ANSWER", payload = new { sdp = new { type, sdp }, type = "data", connectionId = ConnectionId }, dst = PeerId });
		};
		if (offer == null)
		{   // we're connecting: make the channel (named after the connection, ordered = reliable), then offer
			Use(pc.CreateDataChannel(ConnectionId, new Godot.Collections.Dictionary { ["ordered"] = true }));
			pc.CreateOffer();
		}
		else
		{   // they're connecting: their channel arrives once we've answered
			pc.DataChannelReceived += ch => Use(ch);
			var sdp = offer["sdp"] as JsonObject;
			pc.SetRemoteDescription("offer", (string)sdp?["sdp"] ?? "");   // (Godot then makes the answer by itself)
			remoteSet = true;
		}
	}
	void Use(WebRtcDataChannel ch) { dc = ch; if (dc != null) dc.WriteMode = WebRtcDataChannel.WriteModeEnum.Binary; }

	internal void Handle(JsonObject m)
	{
		var payload = m["payload"] as JsonObject;
		switch ((string)m["type"])
		{
			case "ANSWER":
				pc.SetRemoteDescription("answer", (string)(payload?["sdp"] as JsonObject)?["sdp"] ?? "");
				remoteSet = true;
				FlushCandidates();
				break;
			case "CANDIDATE":
				if (payload?["candidate"] is not JsonObject c) return;
				string cand = (string)c["candidate"]; if (string.IsNullOrEmpty(cand)) return;
				string mid = (string)c["sdpMid"] ?? "0"; int idx = c["sdpMLineIndex"] is JsonNode n ? (int)n : 0;
				earlyCandidates.Add((mid, idx, cand));
				FlushCandidates();
				break;
		}
	}
	// candidates can only be added once both descriptions are set (before that there's no ICE transport yet)
	void FlushCandidates()
	{
		if (!remoteSet || !localSet || closed) return;
		foreach (var (media, index, name) in earlyCandidates) pc.AddIceCandidate(media, index, name);
		earlyCandidates.Clear();
	}

	internal void Poll()
	{
		if (closed) return;
		pc.Poll();
		var ps = pc.GetConnectionState();
		if (ps == WebRtcPeerConnection.ConnectionState.Failed || ps == WebRtcPeerConnection.ConnectionState.Closed) { Close(); return; }
		if (dc == null) return;
		dc.Poll();
		var st = dc.GetReadyState();
		if (st == WebRtcDataChannel.ChannelState.Open && !IsOpen) { IsOpen = true; OnOpen?.Invoke(); }
		if (st == WebRtcDataChannel.ChannelState.Closed && IsOpen) { Close(); return; }
		while (!closed && dc.GetAvailablePacketCount() > 0) Received(dc.GetPacket());
	}
	void Received(byte[] data)
	{
		object msg;
		try { msg = BinaryPack.Unpack(data); } catch (Exception e) { GD.PrintErr("multiplayer: bad message: " + e.Message); return; }
		if (msg is Dictionary<string, object> m && m.TryGetValue("__peerData", out var pd) && pd != null)
		{
			if (pd is Dictionary<string, object> pdm && pdm.S("type") == "close") { Close(); return; }
			// a piece of a big message: put them back together
			long id = (long)BinaryPack.Num(pd); int n = m.I("n"), total = m.I("total");
			if (!chunks.TryGetValue(id, out var ch)) ch = (new byte[total][], 0);
			if (n >= 0 && n < ch.parts.Length && ch.parts[n] == null) { ch.parts[n] = m.B("data") ?? Array.Empty<byte>(); ch.count++; }
			chunks[id] = ch;
			if (ch.count == ch.parts.Length) { chunks.Remove(id); Received(ch.parts.SelectMany(b => b).ToArray()); }
			return;
		}
		OnData?.Invoke(msg);
	}

	public void Send(object msg)
	{
		if (!IsOpen || closed) return;
		var blob = BinaryPack.Pack(msg);
		if (blob.Length <= ChunkMtu) { dc.PutPacket(blob); return; }
		int total = (blob.Length + ChunkMtu - 1) / ChunkMtu;
		for (int i = 0, start = 0; start < blob.Length; i++, start += ChunkMtu)
		{
			var part = blob.AsSpan(start, Math.Min(ChunkMtu, blob.Length - start)).ToArray();
			dc.PutPacket(BinaryPack.Pack(new Dictionary<string, object> { ["__peerData"] = dataCount, ["n"] = i, ["data"] = part, ["total"] = total }));
		}
		dataCount++;
	}

	public void Close()
	{
		if (closed) return;
		closed = true;
		bool was = IsOpen; IsOpen = false;
		try { dc?.Close(); pc.Close(); } catch { }
		peer.Forget(this);
		OnClose?.Invoke();
	}
}
