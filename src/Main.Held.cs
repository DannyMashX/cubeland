using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Cubeland;

// Survival inventory: a stack held on the mouse, as in other block games (the same rules as the web version).
//  - click: pick up a slot's stack; with one held, put it down / stack it on the same item / swap it
//  - right-click: pick up half (the bigger half); with one held, put down one
//  - drag a held stack over slots: left shares it out evenly, right puts one in each (it acts when the button comes up)
//  - double-click: gathers that item from the hotbar, pockets and backpack onto the held stack
//  - shift-click: moves the stack to the other part of your slots (see QuickMove)
//  - click outside the screen: drops the held stack; closing the screen puts it back in your slots
//  - Q drops one of the slot under the mouse (in the game: the selected hotbar slot), Ctrl+Q or Shift+Q the whole stack
// Sort tidies up the pockets and backpack.
public partial class Main
{
	int heldId, heldN, heldWear; Inventory.PackData heldData;
	TextureRect heldPic; Label heldCount; Control heldView;
	readonly Dictionary<Slot, int> slotIndex = new();   // (the survival slots: which inventory slot each one shows)
	// dragging a held stack across slots
	bool spreading; MouseButton spreadBtn; readonly List<int> spreadSlots = new();
	int lastPick = -1; ulong lastPickMs;

	void BuildHeld()
	{
		var layer = new CanvasLayer { Layer = 19 }; AddChild(layer);   // (over the screens, under the tooltip)
		heldView = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false, Size = new Vector2(32, 32) };
		heldPic = new TextureRect { Size = new Vector2(32, 32), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore };
		heldCount = new Label { Position = new Vector2(0, 18), Size = new Vector2(34, 18), HorizontalAlignment = HorizontalAlignment.Right, MouseFilter = Control.MouseFilterEnum.Ignore };
		heldCount.AddThemeFontSizeOverride("font_size", 12); heldCount.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.9f)); heldCount.AddThemeConstantOverride("shadow_offset_y", 1);
		heldView.AddChild(heldPic); heldView.AddChild(heldCount); layer.AddChild(heldView);
		// clicking the dark space around a screen with a stack held drops it
		dim.GuiInput += e => { if (e is InputEventMouseButton mb && mb.Pressed && (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Right) && heldId != 0) DropHeld(); };
	}
	// a survival slot (hotbar, pockets, backpack, gear) is clicked
	void SurvivalSlotPress(int i, MouseButton b)
	{
		if (!inv.SlotOpen(i)) return;
		bool shift = Input.IsKeyPressed(Key.Shift);
		ulong now = Time.GetTicksMsec();
		if (b == MouseButton.Left && heldId != 0 && lastPick == i && now - lastPickMs < 350) { Gather(); lastPick = -1; return; }   // (double-click)
		if (heldId == 0)
		{
			if (inv.Id[i] == 0) return;
			if (shift && b == MouseButton.Left) { QuickMove(i); return; }
			PickUp(i, b == MouseButton.Right ? (inv.Count[i] + 1) / 2 : inv.Count[i]);
			if (b == MouseButton.Left) { lastPick = i; lastPickMs = now; }
			return;
		}
		// holding a stack: wait to see if it's a drag across slots (it acts when the button comes up)
		spreading = true; spreadBtn = b; spreadSlots.Clear(); spreadSlots.Add(i); MarkSpread();
	}
	// the mouse moved onto a slot while dragging a held stack
	void SpreadOver(int i)
	{
		if (!spreading || spreadSlots.Contains(i) || !Input.IsMouseButtonPressed(spreadBtn) || !CanTake(i)) return;
		spreadSlots.Add(i); MarkSpread();
	}
	// can slot i take some of the held stack? (empty, or the same item with room)
	bool CanTake(int i) => inv.SlotOpen(i) && inv.Fits(i, heldId) && (inv.Id[i] == 0 || (inv.Id[i] == heldId && inv.Counted(heldId) && inv.Count[i] < Blocks.StackMaxOf(heldId) && heldData == null && inv.Data[i] == null));
	void MarkSpread() { foreach (var (s, i) in slotIndex) s.Modulate = spreading && spreadSlots.Count > 1 && spreadSlots.Contains(i) ? new Color(1.3f, 1.3f, 1.0f) : Colors.White; }
	// every frame: the held stack follows the mouse, and a drag ends when its button comes up
	void UpdateHeld()
	{
		if (heldView == null) return;
		if (spreading && !Input.IsMouseButtonPressed(spreadBtn)) EndSpread();
		heldView.Visible = heldId != 0 && invOpen;
		if (!heldView.Visible) return;
		heldView.Position = (heldView.GetViewport().GetMousePosition() - new Vector2(16, 16)).Round();
	}
	void ShowHeld()
	{
		if (heldView == null) return;
		heldPic.Texture = heldId != 0 ? Atlas.SlotIcon(heldId) : null;
		heldPic.TextureFilter = Atlas.Is3DIcon(heldId) ? CanvasItem.TextureFilterEnum.Linear : CanvasItem.TextureFilterEnum.Nearest;
		heldCount.Text = heldN > 1 ? heldN.ToString() : "";
	}
	void EndSpread()
	{
		spreading = false;
		var slots = spreadSlots.Where(CanTake).ToList();
		if (spreadSlots.Count <= 1 || slots.Count <= 1) { int i = spreadSlots[0]; MarkSpread(); if (spreadBtn == MouseButton.Right) PutOne(i); else PutDown(i); return; }
		if (spreadBtn == MouseButton.Right) foreach (int i in slots) { if (heldN == 0) break; Put(i, 1); }
		else { int each = heldN / slots.Count; if (each > 0) foreach (int i in slots) Put(i, each); }
		MarkSpread(); Changed();
	}
	// put n of the held stack into slot i (empty or the same item), as much as there's room for
	void Put(int i, int n)
	{
		if (!CanTake(i)) return;
		int room = Blocks.StackMaxOf(heldId) - (inv.Id[i] == heldId ? inv.Count[i] : 0); n = Math.Min(n, Math.Min(room, heldN));
		if (n <= 0) return;
		bool wasEmpty = inv.Id[i] == 0;
		inv.Id[i] = heldId; inv.Count[i] = (wasEmpty ? 0 : inv.Count[i]) + (inv.Counted(heldId) ? n : 0); if (wasEmpty) { inv.Wear[i] = heldWear; inv.Data[i] = heldData; }
		heldN -= n; if (heldN <= 0) ClearHeld();
		AfterChange(i);
	}
	void PickUp(int i, int n)
	{
		if (i == Inventory.BackSlot) inv.PackBack();   // (a backpack takes what's in it along)
		heldId = inv.Id[i]; heldWear = inv.Wear[i]; heldData = inv.Data[i];
		heldN = inv.Counted(heldId) ? Math.Max(1, n) : 1;
		if (inv.Counted(heldId) && inv.Count[i] > heldN) { inv.Count[i] -= heldN; heldData = null; }
		else { inv.Id[i] = 0; inv.Count[i] = 0; inv.Wear[i] = 0; inv.Data[i] = null; }
		AfterChange(i);
	}
	// a click with a stack held: put it all down, stack it on the same item, or swap it with what's there
	void PutDown(int i)
	{
		if (!inv.SlotOpen(i)) return;
		if (inv.Id[i] == 0 || (inv.Id[i] == heldId && CanTake(i))) { Put(i, heldN); Changed(); return; }
		if (inv.Id[i] == heldId) return;   // (a full stack of it)
		if (!inv.Fits(i, heldId)) { NoGo(); return; }
		if (i == Inventory.BackSlot) inv.PackBack();   // (the backpack coming off takes what's in it along)
		var (id, n, w, d) = (heldId, heldN, heldWear, heldData);
		heldId = inv.Id[i]; heldN = Math.Max(1, inv.Count[i]); heldWear = inv.Wear[i]; heldData = inv.Data[i];
		inv.Id[i] = id; inv.Count[i] = inv.Counted(id) ? n : 0; inv.Wear[i] = w; inv.Data[i] = d;
		AfterChange(i); Changed();
	}
	void PutOne(int i) { if (CanTake(i)) { Put(i, 1); Changed(); } else if (inv.Id[i] != heldId && inv.Fits(i, heldId)) PutDown(i); }
	// after a slot changed: a backpack put on unpacks, a belt taken off lets go of the slots it gave
	void AfterChange(int i)
	{
		if (i == Inventory.BackSlot && inv.Id[i] != 0) inv.UnpackBack();
		if (i == Inventory.BeltSlot) inv.FitBelt();
		ShowHeld();
	}
	void Changed() { inv.Changed?.Invoke(); ShowHeld(); }
	void ClearHeld() { heldId = heldN = heldWear = 0; heldData = null; ShowHeld(); }
	void NoGo() { if (invName != null) invName.Text = "That doesn't go there"; }
	// double-click: the same item from the hotbar, pockets and backpack onto the held stack
	void Gather()
	{
		if (heldId == 0 || !inv.Counted(heldId) || heldData != null) return;
		int max = Blocks.StackMaxOf(heldId);
		foreach (int k in inv.StoreSlots())
		{
			if (heldN >= max) break;
			if (inv.Id[k] != heldId || inv.Data[k] != null) continue;
			int n = Math.Min(inv.Count[k], max - heldN); heldN += n; inv.Count[k] -= n;
			if (inv.Count[k] == 0) { inv.Id[k] = 0; inv.Wear[k] = 0; }
		}
		Changed();
	}
	// shift-click: a slot's stack goes to the other parts of your slots, topping up the same item first, then into empty slots
	//  hotbar: its gear slot (a free tool slot, or its armor / backpack / belt slot if empty), then pockets, then backpack
	//  pockets: its gear slot, then hotbar, then backpack · backpack: its gear slot, then hotbar, then pockets
	//  gear (tool / offhand / armor / backpack / belt): hotbar, pockets, backpack
	void QuickMove(int i)
	{
		int id = inv.Id[i]; if (id == 0) return;
		IEnumerable<int> R(int a, int b) => Enumerable.Range(a, Math.Max(0, b - a));
		var hot = R(0, Inventory.Slots); var pockets = R(Inventory.Pocket0, Inventory.Pack0); var pack = R(Inventory.Pack0, Inventory.Pack0 + inv.PackSize);
		var d = Blocks.B[id];
		var gear = (d.Tool != null ? R(Inventory.Tool0, Inventory.Tool0 + inv.ToolCount) : d.Armor >= 0 ? new[] { Inventory.Armor0 + d.Armor } : d.Pack > 0 ? new[] { Inventory.BackSlot } : d.Belt > 0 ? new[] { Inventory.BeltSlot } : Enumerable.Empty<int>())
			.Where(k => inv.Id[k] == 0);
		IEnumerable<int> to;
		if (i < Inventory.Slots) to = gear.Concat(pockets).Concat(pack);
		else if (i >= Inventory.Pocket0 && i < Inventory.Pack0) to = gear.Concat(hot).Concat(pack);
		else if (i >= Inventory.Pack0 && i < Inventory.Tool0) to = gear.Concat(hot).Concat(pockets);
		else to = hot.Concat(pockets).Concat(pack);
		var list = to.Where(k => inv.SlotOpen(k) && inv.Fits(k, id)).ToList();
		PickUp(i, inv.Count[i]);
		foreach (int k in list) if (heldId != 0 && inv.Id[k] == heldId) Put(k, heldN);   // (topping up first)
		foreach (int k in list) if (heldId != 0 && inv.Id[k] == 0) Put(k, heldN);
		if (heldId != 0) Put(i, heldN);   // (no room for the rest: it stays where it was)
		Changed();
	}
	// Sort: the pockets and backpack, tidied: the same items in full stacks, in the creative tabs' order, then by name, fullest first
	void SortPockets()
	{
		var slots = Enumerable.Range(Inventory.Pocket0, Inventory.Pack0 - Inventory.Pocket0).Concat(Enumerable.Range(Inventory.Pack0, inv.PackSize)).ToList();
		var loose = new List<(int id, int n, int w, Inventory.PackData d)>();
		var counts = new Dictionary<int, int>();
		foreach (int k in slots)
		{
			int id = inv.Id[k]; if (id == 0) continue;
			if (inv.Counted(id) && inv.Wear[k] == 0 && inv.Data[k] == null && Blocks.StackMaxOf(id) > 1) counts[id] = counts.GetValueOrDefault(id) + inv.Count[k];
			else loose.Add((id, inv.Count[k], inv.Wear[k], inv.Data[k]));
			inv.Id[k] = 0; inv.Count[k] = 0; inv.Wear[k] = 0; inv.Data[k] = null;
		}
		foreach (var (id, n) in counts) for (int left = n; left > 0; left -= Blocks.StackMaxOf(id)) loose.Add((id, Math.Min(left, Blocks.StackMaxOf(id)), 0, null));
		var sorted = loose.OrderBy(e => TabPlace(e.id).tab).ThenBy(e => Blocks.B[e.id].Name, StringComparer.Ordinal).ThenByDescending(e => e.n).ToList();
		for (int j = 0; j < sorted.Count && j < slots.Count; j++) { int k = slots[j]; (inv.Id[k], inv.Count[k], inv.Wear[k], inv.Data[k]) = sorted[j]; }
		Changed();
	}
	// the held stack back into your slots (as picking things up does); false if some had nowhere to go
	bool ReturnHeld(bool dropRest)
	{
		if (heldId == 0) return true;
		foreach (int k in inv.StoreSlots()) if (heldId != 0 && inv.Id[k] == heldId) Put(k, heldN);
		foreach (int k in inv.StoreSlots()) if (heldId != 0 && inv.Id[k] == 0) Put(k, heldN);
		if (heldId != 0 && dropRest && !netClient) DropHeld();
		Changed();
		return heldId == 0;
	}
	// throw the held stack out in front of you
	void DropHeld()
	{
		if (heldId == 0) return;
		if (netClient) { ShowNote("You can't throw things out in a friend's world yet", 2); return; }
		var at = player.Pos + player.Forward * 1.2f;
		for (int n = heldN; n > 0; n--) { var d = drops.Add(heldId, at.X, player.Pos.Y + 1.2f, at.Z); d.Age = -1; d.Wear = heldWear; d.Data = heldData; }
		ClearHeld();
	}
	// Q: one of slot i out in front of you (all: the whole stack)
	void DropFromSlot(int i, bool all)
	{
		if (!inv.Survival || i < 0 || inv.Id[i] == 0) return;
		if (all || inv.Count[i] <= 1) { ThrowSlot(i); return; }
		if (netClient) { ShowNote("You can't throw things out in a friend's world yet", 2); return; }
		var at = player.Pos + player.Forward * 1.2f;
		var d = drops.Add(inv.Id[i], at.X, player.Pos.Y + 1.2f, at.Z); d.Age = -1; d.Wear = inv.Wear[i];
		inv.UseOneAt(i);
	}
	// which slot the mouse is over (with a screen open), or -1
	int HoveredSlot() => tipSlot != null && GodotObject.IsInstanceValid(tipSlot) && slotIndex.TryGetValue(tipSlot, out int i) ? i : -1;
}
