using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

namespace Cubeland;

// The launcher: lists every version of Cubeland from the website, downloads the ones you pick, and starts them.
//  - Desktop versions (Alpha 0.0.5.1 and on, numbered like the web game) come from desktop-versions.json on the site: each is a zip
//    (a GitHub release file) that's unpacked into the launcher's folder and run from there.
//  - Web versions come from versions.json: each is two files (index.html and textures.js) that are saved here
//    and opened in your browser.
// Downloaded versions stay playable without internet (the web ones still need it for their 3D library and multiplayer).
// Testing: --site=http://localhost:8765/ reads the lists from a local server, --desktop=<file> uses another
// desktop list, --data=<folder> keeps downloads somewhere else, --shot=<png> saves a picture and quits.
public partial class Launcher : Control
{
	public const string Version = "1.0.0";
	const string DefaultSite = "https://dannymashx.github.io/cubeland/";
	static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

	class Entry
	{
		public string Kind, Version, Title, Folder, Date, Url, Exe; public long Size; public string[] Changes = Array.Empty<string>(); public bool Newest;
	}
	readonly List<Entry> entries = new();
	string site, desktopList = "desktop-versions.json", root;
	VBoxContainer list; Label status;
	ConfirmationDialog confirm; Action onYes;
	string busy; Label progress;   // the version being downloaded, and its "45%" label

	static string Arg(string name) => OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];

	public override void _Ready()
	{
		DisplayServer.WindowSetTitle($"CubeLand Launcher {Version}");
		Skin.Load();
		site = Arg("--site") ?? DefaultSite; if (!site.EndsWith("/")) site += "/";
		desktopList = Arg("--desktop") ?? desktopList;
		root = Arg("--data") ?? ProjectSettings.GlobalizePath("user://versions");
		Directory.CreateDirectory(root);
		BuildUi();
		_ = Start();
		var shot = Arg("--shot");
		if (shot != null) GetTree().CreateTimer(double.Parse(Arg("--stay") ?? "6", System.Globalization.CultureInfo.InvariantCulture)).Timeout += () =>
		{
			GetViewport().GetTexture().GetImage().SavePng(shot);
			GetTree().Quit();
		};
	}

	// ---- the window ----
	static Label Text(string t, int size = 12, float alpha = 1)
	{
		var l = new Label { Text = t, Modulate = new Color(1, 1, 1, alpha) };
		l.AddThemeFontSizeOverride("font_size", size);
		return l;
	}
	void BuildUi()
	{
		var bg = new ColorRect { Color = new Color(0x15171cff) }; bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); AddChild(bg);
		var margin = new MarginContainer(); margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 24);
		AddChild(margin);
		var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 10); margin.AddChild(col);
		var title = Text("CUBELAND", 60); title.HorizontalAlignment = HorizontalAlignment.Center;
		title.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.6f)); title.AddThemeConstantOverride("shadow_offset_y", 4);
		col.AddChild(title);
		var sub = Text("Launcher", 24, 0.85f); sub.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(sub);
		status = Text("", 12); status.HorizontalAlignment = HorizontalAlignment.Center; status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		status.AddThemeColorOverride("font_color", new Color(1, 0.82f, 0.48f)); col.AddChild(status);
		var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		col.AddChild(scroll);
		list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; list.AddThemeConstantOverride("separation", 8); scroll.AddChild(list);
		var bottom = new HBoxContainer(); bottom.AddThemeConstantOverride("separation", 10); col.AddChild(bottom);
		bottom.AddChild(Button("Refresh", () => _ = Refresh(), 140));
		bottom.AddChild(Button("Open downloads folder", () => OS.ShellOpen(root), 260));
		var ver = Text($"Launcher {Version}", 12, 0.5f); ver.SizeFlagsHorizontal = SizeFlags.ExpandFill; ver.HorizontalAlignment = HorizontalAlignment.Right;
		ver.VerticalAlignment = VerticalAlignment.Center; bottom.AddChild(ver);
		confirm = new ConfirmationDialog { Title = "CubeLand Launcher" };
		confirm.Confirmed += () => onYes?.Invoke();
		AddChild(confirm);
	}
	static Button Button(string text, Action click, int w = 0)
	{
		var b = new Button { Text = text, CustomMinimumSize = new Vector2(w, 40) };
		b.AddThemeFontSizeOverride("font_size", 24);
		b.Pressed += click;
		return b;
	}
	void Ask(string text, Action yes) { onYes = yes; confirm.DialogText = text; confirm.PopupCentered(); }

	async Task Start() { await Refresh(); await TestGet(); }

	// ---- the lists of versions (kept here too, so the launcher still works without internet) ----
	async Task Refresh()
	{
		status.Text = "Getting the list of versions...";
		string desk = await Fetch(desktopList, "desktop.json"), web = await Fetch("versions.json", "web.json");
		entries.Clear();
		try { if (desk != null) entries.AddRange(ParseDesktop(desk)); } catch (Exception e) { GD.PrintErr("desktop list: " + e.Message); }
		try { if (web != null) entries.AddRange(ParseWeb(web)); } catch (Exception e) { GD.PrintErr("web list: " + e.Message); }
		status.Text = entries.Count == 0 ? "Couldn't reach the Cubeland website. Check your internet and press Refresh."
			: online ? "" : "Couldn't reach the Cubeland website, so this is the list from last time. You can still play what you've downloaded.";
		Fill();
	}
	bool online;
	async Task<string> Fetch(string file, string cacheName)
	{
		string cache = Path.Combine(root, "..", cacheName);
		try
		{
			string url = site + file + (site.StartsWith("http") ? "?t=" + DateTime.UtcNow.Ticks : "");
			string text = await Http.GetStringAsync(url);
			File.WriteAllText(cache, text);
			online = true;
			return text;
		}
		catch (Exception e)
		{
			GD.Print($"launcher: couldn't get {file}: {e.Message}");
			online = false;
			return File.Exists(cache) ? File.ReadAllText(cache) : null;
		}
	}
	static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
	static string[] Changes(JsonElement e) => e.TryGetProperty("changes", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(x => x.GetString()).ToArray() : Array.Empty<string>();
	IEnumerable<Entry> ParseDesktop(string json)
	{
		bool first = true;
		foreach (var v in JsonDocument.Parse(json).RootElement.EnumerateArray())
		{
			string url = S(v, "url") ?? "";
			if (!url.StartsWith("http")) url = site + url;   // (a path on the site, for testing)
			yield return new Entry
			{
				Kind = "desktop", Version = S(v, "version"), Title = S(v, "title"), Folder = S(v, "folder"), Date = S(v, "date"), Url = url, Exe = S(v, "exe"),
				Size = v.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0, Changes = Changes(v), Newest = first,
			};
			first = false;
		}
	}
	IEnumerable<Entry> ParseWeb(string json)
	{
		bool first = true;
		foreach (var v in JsonDocument.Parse(json).RootElement.EnumerateArray())
		{
			string folder = S(v, "folder") ?? S(v, "version");
			yield return new Entry { Kind = "web", Version = S(v, "version"), Title = S(v, "title"), Folder = folder, Date = S(v, "date"), Url = site + "versions/" + folder + "/", Changes = Changes(v), Newest = first };
			first = false;
		}
	}

	// ---- the list on screen ----
	string Dir(Entry e) => Path.Combine(root, e.Kind, e.Folder);
	bool Installed(Entry e) => File.Exists(Path.Combine(Dir(e), ".installed"));
	void Fill()
	{
		foreach (var c in list.GetChildren()) c.QueueFree();
		progress = null;
		var desk = entries.Where(e => e.Kind == "desktop").ToList(); var web = entries.Where(e => e.Kind == "web").ToList();
		if (desk.Count > 0) { list.AddChild(Heading("Desktop game")); foreach (var e in desk) list.AddChild(Row(e)); }
		if (web.Count > 0) { list.AddChild(Heading("Web versions (they open in your browser)")); foreach (var e in web) list.AddChild(Row(e)); }
	}
	static Control Heading(string t) { var l = Text(t, 24); l.AddThemeConstantOverride("line_spacing", 0); return l; }
	Control Row(Entry e)
	{
		var panel = new PanelContainer();
		var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 12); panel.AddChild(row);
		var info = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; info.AddThemeConstantOverride("separation", 4); row.AddChild(info);
		var name = new HBoxContainer(); name.AddThemeConstantOverride("separation", 10); info.AddChild(name);
		name.AddChild(Text(e.Kind == "web" ? "Version " + e.Version : e.Version, 24));
		if (!string.IsNullOrEmpty(e.Title)) { var t = Text("- " + e.Title, 24); t.AddThemeColorOverride("font_color", new Color(1, 0.82f, 0.48f)); name.AddChild(t); }
		if (e.Newest) { var t = Text("NEWEST", 12); t.AddThemeColorOverride("font_color", new Color(0.55f, 0.9f, 0.55f)); t.VerticalAlignment = VerticalAlignment.Center; name.AddChild(t); }
		foreach (var c in e.Changes.Take(3))
		{
			var l = Text("+ " + c, 12, 0.8f); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(100, 0); info.AddChild(l);
		}
		if (e.Changes.Length > 3) info.AddChild(Text($"and {e.Changes.Length - 3} more", 12, 0.55f));
		info.AddChild(Text(e.Date ?? "", 12, 0.5f));

		var buttons = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter }; buttons.AddThemeConstantOverride("separation", 6); row.AddChild(buttons);
		if (busy == e.Kind + "/" + e.Folder)
		{
			progress = Text("Starting...", 24); progress.CustomMinimumSize = new Vector2(200, 40);
			progress.HorizontalAlignment = HorizontalAlignment.Center; progress.VerticalAlignment = VerticalAlignment.Center; buttons.AddChild(progress);
		}
		else if (Installed(e))
		{
			buttons.AddChild(Button("Play", () => Play(e), 200));
			var del = Button("Delete", () => Ask($"Delete the downloaded {Name(e)}? You can download it again any time. Your worlds aren't deleted.", () => Delete(e)), 200);
			del.AddThemeColorOverride("font_color", new Color(1, 0.7f, 0.67f)); buttons.AddChild(del);
		}
		else
		{
			var b = Button(e.Size > 0 ? $"Download ({e.Size / 1048576.0:0} MB)" : "Download", () => _ = Download(e), 200);
			b.Disabled = busy != null;
			buttons.AddChild(b);
		}
		return panel;
	}
	static string Name(Entry e) => e.Kind == "web" ? "web version " + e.Version : e.Version;

	// ---- downloading ----
	async Task Download(Entry e)
	{
		if (busy != null) return;
		busy = e.Kind + "/" + e.Folder;
		Fill();
		string dir = Dir(e), part = dir + ".part";
		try
		{
			if (Directory.Exists(part)) Directory.Delete(part, true);
			Directory.CreateDirectory(part);
			if (e.Kind == "desktop")
			{
				string zip = Path.Combine(root, "download.zip");
				await DownloadFile(e.Url, zip);
				if (progress != null) progress.Text = "Unpacking...";
				await Task.Run(() => ZipFile.ExtractToDirectory(zip, part, true));
				File.Delete(zip);
			}
			else
			{
				await DownloadFile(e.Url + "index.html", Path.Combine(part, "index.html"));
				await DownloadFile(e.Url + "textures.js", Path.Combine(part, "textures.js"));
			}
			File.WriteAllText(Path.Combine(part, ".installed"), e.Version);
			if (Directory.Exists(dir)) Directory.Delete(dir, true);
			Directory.CreateDirectory(Path.GetDirectoryName(dir));
			Directory.Move(part, dir);
			status.Text = $"Downloaded {Name(e)}. Press Play!";
		}
		catch (Exception ex)
		{
			GD.PrintErr($"launcher: download of {e.Version} failed: {ex}");
			status.Text = $"Couldn't download {Name(e)}: {ex.Message}";
			try { if (Directory.Exists(part)) Directory.Delete(part, true); } catch { }
		}
		busy = null;
		Fill();
	}
	async Task DownloadFile(string url, string path)
	{
		using var resp = await Http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
		resp.EnsureSuccessStatusCode();
		long total = resp.Content.Headers.ContentLength ?? 0, got = 0;
		await using var src = await resp.Content.ReadAsStreamAsync();
		await using (var dst = File.Create(path))
		{
			var buf = new byte[1 << 16]; int n;
			while ((n = await src.ReadAsync(buf)) > 0)
			{
				await dst.WriteAsync(buf.AsMemory(0, n));
				got += n;
				if (progress != null) progress.Text = total > 0 ? $"{got * 100 / total}%" : $"{got / 1048576.0:0.0} MB";
			}
		}
	}

	// ---- playing and deleting ----
	void Play(Entry e)
	{
		string dir = Dir(e);
		try
		{
			if (e.Kind == "web")
			{
				OS.ShellOpen(new Uri(Path.Combine(dir, "index.html")).AbsoluteUri);
				status.Text = $"Opened {Name(e)} in your browser.";
				return;
			}
			string exe = e.Exe != null ? Path.Combine(dir, e.Exe) : null;
			if (exe == null || !File.Exists(exe)) exe = Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories).FirstOrDefault(f => !f.EndsWith(".console.exe"));
			if (exe == null) { status.Text = $"Couldn't find the game in {Name(e)}. Try deleting it and downloading it again."; return; }
			Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true });
			status.Text = $"Started {Name(e)}.";
		}
		catch (Exception ex) { status.Text = $"Couldn't start {Name(e)}: {ex.Message}"; }
	}
	void Delete(Entry e)
	{
		try { Directory.Delete(Dir(e), true); status.Text = $"Deleted {Name(e)}."; }
		catch (Exception ex) { status.Text = $"Couldn't delete {Name(e)}: {ex.Message} (is it still running?)"; }
		Fill();
	}

	// testing: --get=<folder> downloads that version once the list is up (and --play then starts it)
	async Task TestGet()
	{
		string want = Arg("--get"); if (want == null) return;
		var e = entries.FirstOrDefault(x => x.Folder == want); if (e == null) { GD.Print("launcher: no version " + want); return; }
		if (!Installed(e)) await Download(e);
		GD.Print($"launcher: {want} installed = {Installed(e)}");
		if (OS.GetCmdlineUserArgs().Contains("--play") && Installed(e)) Play(e);
	}
}
