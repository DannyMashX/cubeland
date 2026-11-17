using System;
using Godot;

namespace Cubeland;

// Your skin: a 64x64 picture, saved as skin.png in the game's user folder (no file = the default skin).
// In multiplayer it's sent as a PNG "data:" text, the same as the web version, so web and desktop players see each other's skins.
public static class PlayerSkin
{
	public const int Size = 64;
	const string File = "user://skin.png";
	static Image image; static Texture2D texture; static string dataUrl; static bool loaded;
	public static event Action Changed;

	static void Load()
	{
		if (loaded) return;
		loaded = true;
		if (!FileAccess.FileExists(File)) return;
		var img = Image.LoadFromFile(ProjectSettings.GlobalizePath(File));
		if (img != null && img.GetWidth() == Size && img.GetHeight() == Size) Use(img);
	}
	static void Use(Image img)
	{
		if (img == null) { image = null; texture = null; dataUrl = null; return; }
		img.Convert(Image.Format.Rgba8);
		image = img; texture = ImageTexture.CreateFromImage(img);
		dataUrl = "data:image/png;base64," + Convert.ToBase64String(img.SavePngToBuffer());
	}
	// your own skin (null = the default one)
	public static Image Image { get { Load(); return image; } }
	public static Texture2D Texture { get { Load(); return texture; } }
	public static string DataUrl { get { Load(); return dataUrl; } }

	// save a new skin (null = back to the default)
	public static void Set(Image img)
	{
		Load();
		if (img == null) { if (FileAccess.FileExists(File)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(File)); Use(null); }
		else { img = (Image)img.Duplicate(); img.SavePng(File); Use(img); }
		Changed?.Invoke();
	}

	// a picture from someone else is only used if it's a small 64x64 PNG
	public static bool Valid(string s) => s != null && s.StartsWith("data:image/png;base64,") && s.Length < 60000;
	public static Image ImageFrom(string s)
	{
		if (!Valid(s)) return null;
		try
		{
			var img = new Image();
			if (img.LoadPngFromBuffer(Convert.FromBase64String(s["data:image/png;base64,".Length..])) != Error.Ok) return null;
			if (img.GetWidth() != Size || img.GetHeight() != Size) return null;
			img.Convert(Image.Format.Rgba8);
			return img;
		}
		catch { return null; }
	}
	public static Texture2D TextureFrom(string s) { var img = ImageFrom(s); return img == null ? null : ImageTexture.CreateFromImage(img); }
}
