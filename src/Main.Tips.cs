using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Cubeland;

// Tooltips: hover any slot (in any screen, or the hotbar while a screen is open) to see what's in it: its name, how
// worn a tool or piece of armor is, how much a food heals, and how much a fuel smelts.
public partial class Main
{
	PanelContainer tipBox; Label tipText; Slot tipSlot;
	void BuildTips()
	{
		var layer = new CanvasLayer { Layer = 20 }; AddChild(layer);   // (above every screen and menu)
		tipBox = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
		tipBox.AddThemeStyleboxOverride("panel", Skin.Panel(6, 4));
		tipText = Text("", 13); tipText.MouseFilter = Control.MouseFilterEnum.Ignore;
		tipBox.AddChild(tipText); layer.AddChild(tipBox);
		Slot.TipShow = s => { tipSlot = s; ShowTip(); };
		Slot.TipHide = () => { tipSlot = null; tipBox.Visible = false; };
	}
	void ShowTip()
	{
		if (tipSlot == null || !GodotObject.IsInstanceValid(tipSlot) || !tipSlot.IsVisibleInTree() || tipSlot.ShownId == 0 || Input.MouseMode != Input.MouseModeEnum.Visible)
		{ tipBox.Visible = false; return; }
		tipText.Text = TipFor(tipSlot.ShownId, tipSlot.TipWear);
		tipBox.ResetSize();
		tipBox.Visible = true;
		PlaceTip();
	}
	// every frame: the tooltip stays by the mouse, on the screen
	void PlaceTip()
	{
		if (tipBox == null || !tipBox.Visible) return;
		if (tipSlot == null || !GodotObject.IsInstanceValid(tipSlot) || !tipSlot.IsVisibleInTree() || Input.MouseMode != Input.MouseModeEnum.Visible) { tipBox.Visible = false; return; }
		var m = tipBox.GetViewport().GetMousePosition(); var s = tipBox.GetViewportRect().Size; var z = tipBox.Size;
		var at = m + new Vector2(14, 14);
		if (at.X + z.X > s.X - 4) at.X = m.X - 10 - z.X;
		if (at.Y + z.Y > s.Y - 4) at.Y = m.Y - 10 - z.Y;
		tipBox.Position = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));
	}
	// what a tooltip says about item id (wear: how much it's been used, -1 if not known)
	static string TipFor(int id, int wear)
	{
		var d = Blocks.Get(id); if (d == null) return "";
		var lines = new List<string> { d.Name };
		int max = Inventory.MaxWear(id);
		if (max > 0) lines.Add($"Durability {max - Math.Clamp(wear < 0 ? 0 : wear, 0, max)} / {max}");
		if (d.Food > 0) { var h = Hearts(d.Food); lines.Add($"Heals {h} heart{(h == "1" ? "" : "s")}"); }
		if (Kilns.Fuel.TryGetValue(id, out int f)) lines.Add($"Fuel: smelts {f} ({f * Kilns.SmeltTime:0} s)");
		return string.Join("\n", lines);
	}
	// quarter hearts as hearts, in halves (rounding up a half): 2 -> "0.5", 3 -> "1", 6 -> "1.5" (the same as the web version; the font has no ½)
	static string Hearts(int q) { int halves = (q + 1) / 2; return (halves / 2) + (halves % 2 == 1 ? ".5" : ""); }
}
