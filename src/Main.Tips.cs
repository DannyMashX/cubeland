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
	Slot tipPin;   // (pictures: the tooltip shown by this slot, as if the mouse were on it)
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
		if (tipSlot == null || !GodotObject.IsInstanceValid(tipSlot) || !tipSlot.IsVisibleInTree() || tipSlot.ShownId == 0 || Locked)
		{ tipBox.Visible = false; return; }
		tipText.Text = TipFor(tipSlot.ShownId, tipSlot.TipWear, tipSlot.TipData);
		tipBox.ResetSize();
		tipBox.Visible = true;
		PlaceTip();
	}
	// every frame: the tooltip stays by the mouse, on the screen
	void PlaceTip()
	{
		if (tipBox == null || !tipBox.Visible) return;
		if (tipSlot == null || !GodotObject.IsInstanceValid(tipSlot) || !tipSlot.IsVisibleInTree() || Locked) { tipBox.Visible = false; return; }
		var m = tipPin != null && tipSlot == tipPin ? tipPin.GlobalPosition + tipPin.Size / 2 : tipBox.GetViewport().GetMousePosition(); var s = tipBox.GetViewportRect().Size; var z = tipBox.Size;
		var at = m + new Vector2(14, 14);
		if (at.X + z.X > s.X - 4) at.X = m.X - 10 - z.X;
		if (at.Y + z.Y > s.Y - 4) at.Y = m.Y - 10 - z.Y;
		tipBox.Position = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));
	}
	// what a tooltip says about item id (wear: how much it's been used, -1 if not known)
	static string TipFor(int id, int wear, Inventory.PackData bag = null)
	{
		var d = Blocks.Get(id); if (d == null) return "";
		var lines = new List<string> { d.Name };
		if (d.Bag)
		{   // a bag: what it holds, newest first (8 at most, then how many more), and its room
			int used = Inventory.BagUsed(bag), n = bag?.Id?.Length ?? 0;
			if (n == 0) { lines.Add("Empty"); return string.Join("\n", lines); }
			for (int k = n - 1; k >= Math.Max(0, n - 8); k--) lines.Add(bag.N[k] > 1 ? $"{bag.N[k]} {Blocks.B[bag.Id[k]].Name}" : Blocks.B[bag.Id[k]].Name);
			if (n > 8) lines.Add($"and {n - 8} more");
			lines.Add(used >= Inventory.BagRoom ? "Full" : $"Room for {Inventory.BagRoom - used} more");
			return string.Join("\n", lines);
		}
		int max = Inventory.MaxWear(id);
		if (max > 0) lines.Add($"Durability {max - Math.Clamp(wear < 0 ? 0 : wear, 0, max)} / {max}");
		if (d.Food > 0) { var h = Hearts(d.Food); lines.Add($"Heals {h} heart{(h == "1" ? "" : "s")}"); }
		if (Kilns.Fuel.TryGetValue(id, out int f)) lines.Add($"Fuel: smelts {f} ({f * Kilns.SmeltTime:0} s)");
		return string.Join("\n", lines);
	}
	// a slot showing a bag: its full picture while it holds anything, its fill bar, and what it holds for the tooltip
	static void ShowBag(Slot s, int id, Inventory.PackData data)
	{
		s.TipData = data;
		if (!Blocks.IsBag(id)) return;
		s.Pic.Texture = Atlas.SlotIcon(id, data != null);
		s.ShowFill((float)Inventory.BagUsed(data) / Inventory.BagRoom);
	}
	// quarter hearts as hearts, in halves (rounding up a half): 2 -> "0.5", 3 -> "1", 6 -> "1.5" (the same as the web version; the font has no ½)
	static string Hearts(int q) { int halves = (q + 1) / 2; return (halves / 2) + (halves % 2 == 1 ? ".5" : ""); }
}
