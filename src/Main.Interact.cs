using System;
using Godot;

namespace Cubeland;

// Breaking and placing: whole blocks, pieces, slabs and stairs, torches (and relighting them), water, rocks, kilns.
public partial class Main
{
	bool Survival => inv.Survival;
	bool ShiftDown => Input.IsPhysicalKeyPressed(Key.Shift);

	// survival: what breaking a block gives you
	static int DropFor(int b) =>
		Blocks.Get(b) is { } d && d.Drop > 0 ? d.Drop : Blocks.IsTorch(b) ? Blocks.TorchDrop(b) :
		Array.IndexOf(Blocks.BrickVariants, b) >= 0 ? Blocks.BrickVariants[0] : Array.IndexOf(Blocks.Items, b) >= 0 && !Blocks.IsWater(b) ? b : 0;
	void Collect(int id, int x, int y, int z) { if (!Survival || id == 0 || Blocks.Get(id) == null) return; if (netClient) { inv.Give(id); return; } drops.Add(id, x + 0.5f, y + 0.3f, z + 0.5f); }
	void UseOne() => inv.UseOne();
	// a block went down: its sound, and one used up
	Player.Hit lastAim;
	void Placed(int block) { MobNoise(lastAim.X, lastAim.Y, lastAim.Z); if (Blocks.IsWater(block)) Sound.I?.Pour(); else Sound.I?.Place(block); inv.UseOne(); }
	// survival: logs and planks only break with an axe, stone-type blocks only with a pickaxe (true, with a note, when you can't)
	bool NeedTool(int b)
	{
		if (!Survival) return false;
		string kind = Array.IndexOf(Blocks.NeedsAxe, b) >= 0 ? "axe" : Array.IndexOf(Blocks.NeedsPickaxe, b) >= 0 ? "pickaxe" : null;
		if (kind == null || inv.Holding(kind)) return false;
		ShowNote(kind == "axe" ? $"You need an axe to cut {Blocks.B[b].Name}" : $"You need a pickaxe to mine {Blocks.B[b].Name}", 2);
		return true;
	}
	// survival: dirt, grass, sand and gravel dug by hand (no shovel) give 1-8 pieces of it instead of the whole block
	void CollectBlock(int b, int x, int y, int z)
	{
		int id = DropFor(b);
		if (Survival && Array.IndexOf(Blocks.Diggable, b) >= 0 && !inv.Holding("shovel") && Blocks.Get(Blocks.PieceItem + id) != null)
			for (int n = 1 + rnd.Next(8); n > 0; n--) Collect(Blocks.PieceItem + id, x, y, z);
		else Collect(id, x, y, z);
	}
	// one Stick from the selected slot becomes a Torch (right-click a lit kiln)
	void LightStick()
	{
		if (Survival)
		{
			inv.UseOne();
			if (!inv.Give(Blocks.Torch)) { inv.SetSlot(inv.Selected, Blocks.Stick, (inv.Held == Blocks.Stick ? inv.Count[inv.Selected] : 0) + 1); ShowNote("No room in your hotbar for a torch", 2); return; }
		}
		else inv.Give(Blocks.Torch);
		ShowNote("Lit a Torch");
	}

	void Break()
	{
		// a mob in front of you gets hit first
		if (MobInSights() is { } mob) { HitMob(mob); return; }
		if (player.Raycast() is not { } h) return;
		MobNoise(h.X, h.Y, h.Z);   // (curious mobs come to see)
		// pieces: break just the one you're looking at (or the whole slab / stairs it's part of)
		if (h.Sub >= 0)
		{
			world.PieceMap.TryGetValue(World.Idx(h.X, h.Y, h.Z), out var a);
			if (a != null && NeedTool(a[h.Sub])) return;
			if (a != null && World.ShapeAt(a, h.Sub) is { } g)
			{
				Collect(Blocks.ShapeItem[g.shape] + (g.block == Blocks.Stone ? Blocks.Cobblestone : g.block), h.X, h.Y, h.Z);   // (stone gives cobblestone)
				Sound.I?.Break(g.block);
				world.SetSpots(h.X, h.Y, h.Z, g.mask, 0);
				return;
			}
			if (a != null) Sound.I?.Break(a[h.Sub]);
			if (a != null) Collect(Blocks.PieceItem + (a[h.Sub] == Blocks.Stone ? Blocks.Cobblestone : a[h.Sub]), h.X, h.Y, h.Z);
			world.SetPiece(h.X, h.Y, h.Z, h.Sub, 0);
			return;
		}
		if (h.Y <= 0) return;   // the bottom of the world can't be broken
		int hb = h.Id;
		if (NeedTool(hb)) return;
		CollectBlock(hb, h.X, h.Y, h.Z);
		Sound.I?.Break(hb);
		if (hb == Blocks.Leaves && rnd.NextDouble() < 0.2) Collect(Blocks.Stick, h.X, h.Y, h.Z);   // leaves: 1 in 5 chance of a stick too
		var b = Blocks.B[hb];
		if (Blocks.IsTorch(hb) && b.Count > 1) world.SetBlock(h.X, h.Y, h.Z, Blocks.TorchVariant(b.Wi, b.Count - 1, b.Burn));   // take one torch off
		else { world.SetBlock(h.X, h.Y, h.Z, 0); world.DropTorchesAround(h.X, h.Y, h.Z, DropFor); }
	}

	void Place()
	{
		// holding food: eat it (survival); holding Fire Essence: it lifts the gloom
		if (UseFireEssence() || Eat()) return;
		if (player.Raycast() is not { } h) return;
		lastAim = h;
		int hb = h.Id, id = inv.Held;
		// right-click a Crafting Bench to use it (hold Shift to build against it instead)
		if (hb == Blocks.CraftingBench && !ShiftDown) { OpenPanel(benchPanel); return; }
		// right-click a lit (burning) kiln holding a Stick: the stick catches fire and becomes a Torch
		if (Blocks.IsFurnace(hb) && Blocks.B[hb].Lit && id == Blocks.Stick) { LightStick(); return; }
		if (Blocks.IsFurnace(hb) && !ShiftDown) { kilnAt = World.Idx(h.X, h.Y, h.Z); OpenPanel(kilnPanel); return; }
		if (id == 0) return;
		if (Blocks.FormOf(id) != null) { PlaceShape(h, id); return; }
		// a piece goes in the half-block spot just in front of whatever you're looking at
		if (Blocks.IsPiece(id))
		{
			var p = HalfSpot(h);
			int x = World.FloorDiv(p.X, 2), y = World.FloorDiv(p.Y, 2), z = World.FloorDiv(p.Z, 2);
			if (player.TouchesBox(p.X / 2f, p.Y / 2f, p.Z / 2f, 0.5f)) return;
			if (world.SetPiece(x, y, z, (p.X - 2 * x) + (p.Y - 2 * y) * 2 + (p.Z - 2 * z) * 4, Blocks.B[id].Piece)) Placed(Blocks.B[id].Piece);
			return;
		}
		if (Blocks.IsItem(id) && Blocks.B[id].Places == 0) return;   // items that don't go down on the ground
		int bx = h.X + h.Nx, by = h.Y + h.Ny, bz = h.Z + h.Nz;
		if (Blocks.IsTorch(id))
		{
			int burn = Blocks.B[id].Burn;   // what you're holding: a Torch (lit), or a Smoldering / Burnt / Ever one
			if (Blocks.IsTorch(hb))
			{
				var tb = Blocks.B[hb];
				// a lit torch lights a smoldering one up again (it isn't used up)
				if ((burn == 0 || burn == 3) && tb.Burn == 1) { world.SetBlock(h.X, h.Y, h.Z, Blocks.TorchVariant(tb.Wi, tb.Count, 0)); ShowNote("Lit the torch again"); return; }
				// clicking a torch adds another one of the same kind to that spot (up to 4 standing, 2 on a wall)
				if (tb.Burn == burn && tb.Count < Blocks.TorchMax(tb.Wi)) { world.SetBlock(h.X, h.Y, h.Z, Blocks.TorchVariant(tb.Wi, tb.Count + 1, burn)); Placed(id); }
				return;
			}
			// otherwise: on top of a block (standing) or on its side (leaning off the wall), never underneath
			if (h.Ny < 0) return;
			int wi = h.Ny > 0 ? 0 : Blocks.WallIndex(-h.Nx, -h.Nz);
			if (wi < 0 || !World.Inside(bx, by, bz)) return;
			int there = world.GetBlock(bx, by, bz);
			if (there == 0) world.SetBlock(bx, by, bz, Blocks.TorchVariant(wi, 1, burn));
			else if (Blocks.IsTorch(there) && Blocks.B[there].Wi == wi && Blocks.B[there].Burn == burn && Blocks.B[there].Count < Blocks.TorchMax(wi))
				world.SetBlock(bx, by, bz, Blocks.TorchVariant(wi, Blocks.B[there].Count + 1, burn));
			else return;
			Placed(id);
			return;
		}
		{
			if (!World.Inside(bx, by, bz)) return;
			int there = world.GetBlock(bx, by, bz);
			if (there != 0 && !Blocks.IsWater(there)) return;   // (water can be built over)
			if (id == Blocks.Water) { world.SetBlock(bx, by, bz, Blocks.Water); Placed(Blocks.Water); return; }   // water can go anywhere, even where you stand
			int pid = Blocks.PlaceId(id, rnd);
			if (Blocks.B[pid].Shape == "flat")
			{   // a rock: only on top of a solid block
				if (by != h.Y + 1 || !world.Solid(bx, by - 1, bz) || Blocks.IsWater(there)) return;
				world.SetBlock(bx, by, bz, pid); Placed(pid); return;
			}
			if (pid == Blocks.Furnace)
			{   // a kiln turns its front towards you
				float dx = player.Pos.X - (bx + 0.5f), dz = player.Pos.Z - (bz + 0.5f);
				for (int k = 0; k < 4; k++)
				{
					var f = Blocks.FurnaceFronts[k];
					if (MathF.Abs(dx) > MathF.Abs(dz) ? f[0] == MathF.Sign(dx) : f[1] == (dz == 0 ? 1 : MathF.Sign(dz))) { pid = Blocks.Furnace + k; break; }
				}
			}
			if (player.Touches(bx, by, bz)) return;
			world.SetBlock(bx, by, bz, pid); Placed(pid);
		}
	}
	// the half-block spot just in front of where you aim (on the half-block grid: 2x world position)
	static Vector3I HalfSpot(Player.Hit h) =>
		new((int)MathF.Floor((h.Pt.X + h.Nx * 0.25f) * 2), (int)MathF.Floor((h.Pt.Y + h.Ny * 0.25f) * 2), (int)MathF.Floor((h.Pt.Z + h.Nz * 0.25f) * 2));

	// Put down the slab / vertical slab / stairs in your hand, in the space just in front of where you're looking
	// (or into the empty half of a space you're looking at, like a second slab on top of a slab).
	//  slab: bottom half, or the top half if you aim at the upper half of a side or at the underside of something
	//  vertical slab: against the side you aim at (on a top or bottom face: the half you aim at, across where you face)
	//  stairs: the step rises away from you; upside down the same way as a top slab
	void PlaceShape(Player.Hit h, int id)
	{
		string form = Blocks.B[id].Form; int of = Blocks.B[id].Of;
		var p = h.Pt + new Vector3(h.Nx, h.Ny, h.Nz) * 0.25f;
		int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
		static float Frac(float v) => v - MathF.Floor(v);
		bool upper = Frac(p.Y) >= 0.5f;
		float fx = -MathF.Sin(player.Yaw), fz = -MathF.Cos(player.Yaw); bool alongX = MathF.Abs(fx) > MathF.Abs(fz);   // the way you face
		int mask;
		if (form == "quarter" || form == "vquarter")
		{
			// the half-block spot in front of where you aim, plus the one next to it: a quarter lies across your view
			// if there's room, otherwise the other way; a vertical quarter stands up, with the spot above or below
			var hp = HalfSpot(h);
			int qx = World.FloorDiv(hp.X, 2), qy = World.FloorDiv(hp.Y, 2), qz = World.FloorDiv(hp.Z, 2);
			int s = (hp.X - 2 * qx) + (hp.Y - 2 * qy) * 2 + (hp.Z - 2 * qz) * 4;
			foreach (int bit in form == "vquarter" ? new[] { 2 } : alongX ? new[] { 4, 1 } : new[] { 1, 4 })
			{
				int m = (1 << s) | (1 << (s ^ bit));
				if (!player.ShapeFits(qx, qy, qz, m) || !world.SetSpots(qx, qy, qz, m, of)) continue;
				Placed(of); return;
			}
			return;
		}
		if (form == "slab") mask = upper ? 0xCC : 0x33;
		else if (form == "vslab")
		{
			bool onX = h.Ny == 0 ? h.Nx != 0 : alongX;
			bool high = Frac(onX ? p.X : p.Z) >= 0.5f;
			mask = onX ? (high ? 0xAA : 0x55) : (high ? 0xF0 : 0x0F);
		}
		else if (form == "vstairs")
		{   // vertical stairs: the missing corner faces you
			bool lowX = player.Pos.X < x + 0.5f, lowZ = player.Pos.Z < z + 0.5f;
			mask = Blocks.ShapeMasks["vstairs"][(lowX ? 0 : 1) + (lowZ ? 0 : 2)];
		}
		else
		{   // stairs: the step goes on the far side from you
			int side = alongX ? (fx > 0 ? 1 : 0) : (fz > 0 ? 3 : 2);
			mask = Blocks.ShapeMasks["stairs"][side + (upper ? 4 : 0)];
		}
		if (!player.ShapeFits(x, y, z, mask) || !world.SetSpots(x, y, z, mask, of)) return;
		Placed(of);
	}
}
