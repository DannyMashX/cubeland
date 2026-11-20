using System;
using Godot;

namespace Cubeland;

// One square slot showing a block or item: its picture, a small number in the corner, how many there are.
// Can be clicked, dragged from and dropped onto.
public partial class Slot : Panel
{
	public readonly TextureRect Pic;
	public readonly Label Num, Count;
	public Func<string> DragData;            // what dragging this slot carries ("slot:3", "item:12"), or null
	public Func<string, bool> CanDrop;
	public Action<string> Dropped;
	public Action<MouseButton> Clicked;
	public Action Hover, Leave;
	bool sel, hot;
	readonly ColorRect wearBg, wearBar;

	public Slot(int size = 44)
	{
		CustomMinimumSize = new Vector2(size, size);
		int pic = size - 12;
		Pic = new TextureRect { Position = new Vector2(6, 6), Size = new Vector2(pic, pic), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.Scale, TextureFilter = TextureFilterEnum.Nearest, MouseFilter = MouseFilterEnum.Ignore };
		AddChild(Pic);
		Num = MakeLabel(new Vector2(4, 0)); AddChild(Num);
		Count = MakeLabel(Vector2.Zero); Count.HorizontalAlignment = HorizontalAlignment.Right; Count.Position = new Vector2(0, size - 20); Count.Size = new Vector2(size - 4, 18);
		Count.AddThemeFontSizeOverride("font_size", 12);
		AddChild(Count);
		// a worn tool's bar along the bottom: what's left, green when new through yellow to red
		wearBg = new ColorRect { Color = Colors.Black, Position = new Vector2(6, size - 7), Size = new Vector2(size - 12, 4), Visible = false, MouseFilter = MouseFilterEnum.Ignore };
		wearBar = new ColorRect { Position = Vector2.Zero, Size = new Vector2(size - 12, 2), MouseFilter = MouseFilterEnum.Ignore };
		wearBg.AddChild(wearBar); AddChild(wearBg);
		MouseEntered += () => { hot = true; Restyle(); Hover?.Invoke(); };
		MouseExited += () => { hot = false; Restyle(); Leave?.Invoke(); };
		Restyle();
	}
	static Label MakeLabel(Vector2 at)
	{
		var l = new Label { Position = at, MouseFilter = MouseFilterEnum.Ignore };
		l.AddThemeFontSizeOverride("font_size", 12);
		l.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.9f));
		l.AddThemeConstantOverride("shadow_offset_y", 1);
		return l;
	}
	public void Show(int id, int count = 0)
	{
		Pic.Texture = id != 0 ? Atlas.SlotIcon(id) : null;
		// a cube icon is drawn bigger than the slot, so it's smoothed down rather than missing every other row
		Pic.TextureFilter = Atlas.Is3DIcon(id) ? TextureFilterEnum.Linear : TextureFilterEnum.Nearest;
		Count.Text = count > 1 ? count.ToString() : "";
	}
	// left: how much of the tool is left (0-1), or below 0 for no bar
	public void ShowWear(float left)
	{
		wearBg.Visible = left >= 0;
		if (left < 0) return;
		wearBar.Size = new Vector2(Math.Max(1, MathF.Round(left * wearBg.Size.X)), 2);
		wearBar.Color = Color.FromHsv(left / 3, 0.9f, 0.85f);
	}
	public bool Selected { get => sel; set { sel = value; Restyle(); } }
	public bool Interactive = true;
	void Restyle()
	{
		// slots are drawn like buttons, and light up when selected or under the mouse
		AddThemeStyleboxOverride("panel", Skin.Button(sel || (hot && Interactive)));
	}
	public override void _GuiInput(InputEvent e)
	{
		if (e is InputEventMouseButton mb && mb.Pressed && (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Right)) { Clicked?.Invoke(mb.ButtonIndex); AcceptEvent(); }
	}
	public override Variant _GetDragData(Vector2 atPosition)
	{
		string d = DragData?.Invoke();
		if (d == null || Pic.Texture == null) return default;
		var prev = new TextureRect { Texture = Pic.Texture, Size = new Vector2(32, 32), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, TextureFilter = TextureFilterEnum.Nearest, Position = new Vector2(-16, -16) };
		var holder = new Control(); holder.AddChild(prev);
		SetDragPreview(holder);
		return d;
	}
	public override bool _CanDropData(Vector2 atPosition, Variant data) => data.VariantType == Variant.Type.String && (CanDrop?.Invoke((string)data) ?? false);
	public override void _DropData(Vector2 atPosition, Variant data) => Dropped?.Invoke((string)data);
}
