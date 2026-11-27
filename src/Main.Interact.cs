using System;
using Godot;

namespace Cubeland;

// Breaking and placing: whole blocks, pieces, slabs and stairs, torches (and relighting them), water, rocks, kilns.
public partial class Main
{
	bool Survival => inv.Survival;
	bool ShiftDown => Input.IsPhysicalKeyPressed(Key.Shift);
	static double SeedChance => Nums.N.FARM.seedChance;   // (tilling Grass: the chance of turning up a Grain Seed; data/numbers.json)

	// survival: what breaking a block gives you
	static int DropFor(int b) =>
		Blocks.Get(b) is { } d && d.Drop > 0 ? d.Drop : Blocks.IsTorch(b) ? Blocks.TorchDrop(b) :
		Array.IndexOf(Blocks.Items, b) >= 0 && !Blocks.IsWater(b) ? b : 0;   // (Red Bricks' other looks drop Red Bricks: their drop)
	void Collect(int id, int x, int y, int z) { if (!Survival || id == 0 || Blocks.Get(id) == null) return; if (netClient) { inv.Give(id); return; } drops.Add(id, x + 0.5f, y + 0.3f, z + 0.5f); }
	void UseOne() => inv.UseOne();
	// a block went down: its sound, and one used up
	Player.Hit lastAim;
	void Placed(int block) { MobNoise(lastAim.X, lastAim.Y, lastAim.Z); if (Blocks.IsWater(block)) Sound.I?.Pour(); else Sound.I?.Place(block); inv.UseOne(); }
	// survival: logs and planks only break with an axe, stone-type blocks only with a pickaxe (true, with a note, when you can't)
	// staged: a block as the world grew it, with stage breaking on (logs by hand, deep Stone with any pickaxe); the rest the old way
	bool NeedTool(int b0, bool staged = false)
	{
		int b = Blocks.Plain(b0);   // (a block standing in snow: the block itself)
		if (!Survival) return false;
		bool Tell(string text) { ShowNote(text, 2); return true; }
		if (b == Blocks.Bedrock) return Tell("Bedrock can't be broken");
		// ore: only a good enough pickaxe mines it
		int pick = Blocks.Get(b)?.Pick ?? 0;
		if (pick > 0)
		{
			if (!inv.Holding("pickaxe")) return Tell($"You need a pickaxe to mine {Blocks.B[b].Name}");
			if ((Blocks.PickTier.TryGetValue(Blocks.B[inv.Held].Material ?? "", out int tier) ? tier : 0) < pick) return Tell($"You need {new[] { "", "a stone", "a copper", "an iron" }[pick]} pickaxe or better to mine {Blocks.B[b].Name}");
			return TooDeep();
		}
		if (staged && Blocks.IsLogBlock(b)) return false;   // a tree's logs: any tool, or your hand (an axe is just quicker)
		if (TooDeep()) return true;
		// put-down deep Stone or deep ore: the pickaxe has to be good enough for its layer (natural ones just take more swings)
		bool TooDeep()
		{
			int layer = Blocks.StoneLayer(b);
			return !staged && layer > 1 && inv.Holding("pickaxe") && layer > (Blocks.StrataPickaxe.TryGetValue(Blocks.B[inv.Held].Material ?? "", out int deepest) ? deepest : 0)
				&& Tell($"You need a better pickaxe to mine {Blocks.B[b].Name} this deep");
		}
		string kind = NeededTool(b);
		if (kind == null || inv.Holding(kind)) return false;
		ShowNote(kind == "axe" ? $"You need an axe to cut {Blocks.B[b].Name}" : $"You need a pickaxe to mine {Blocks.B[b].Name}", 2);
		return true;
	}
	// survival: dirt, grass, sand and gravel dug by hand (no shovel) give 1-8 pieces of it instead of the whole block
	void CollectBlock(int b, int x, int y, int z)
	{
		int id = DropFor(b); var hand = Blocks.Get(b)?.HandDrop;
		if (Survival && hand != null && !inv.Holding("shovel"))   // (Fireclay by hand: a few Fireclay Balls)
			for (int n = hand[1] + rnd.Next(hand[2] - hand[1] + 1); n > 0; n--) Collect(hand[0], x, y, z);
		else if (Survival && Diggable(b) && !inv.Holding("shovel") && Blocks.FormItem("piece", id) > 0)
			for (int n = 1 + rnd.Next(8); n > 0; n--) Collect(Blocks.FormItem("piece", id), x, y, z);
		else Collect(id, x, y, z);
	}
	// saplings (data/numbers.json SAPLING): the chance leaves broken without a hoe drop theirs; the chance one Bone Ash grows one
	static readonly double SaplingDrop = Nums.N.SAPLING.dropChance, BoneAshGrows = Nums.N.SAPLING.boneAshChance;
	// the ground a sapling can be planted on (sand: one that grows on Sand too)
	static bool SaplingGround(int b, bool sand) => Blocks.IsGrassy(b) || b == Blocks.Dirt || b == Blocks.Mud || b == Blocks.Peat || (sand && b == Blocks.Sand);
	// the tool block b can't be broken without in survival ("axe", "pickaxe"; null: any)
	static string NeededTool(int b) => Blocks.Get(b)?.Needs;
	// a shovel is block b's tool (and dug by hand it comes up as pieces, if it has a piece item)
	static bool Diggable(int b) => Blocks.Get(b)?.Shovel == true;
	// the tool each block is meant for (null: none in particular)
	static string RightTool(int b) => (b = Blocks.Plain(b)) < 0 ? null : NeededTool(b) ?? (Blocks.Get(b)?.Pick > 0 ? "pickaxe" : Diggable(b) ? "shovel" : Blocks.IsLeaves(b) ? "hoe" : null);
	// breaking block b with what you're holding: 1, or 2 with the wrong tool
	int BreakWear(int b) { string k = RightTool(b); return k == null || inv.Holding(k) ? 1 : 2; }
	// use up some of the selected tool; it breaks when it's used up
	void WearTool(int cost) { if (inv.WearTool(cost) is { } name) { Sound.I?.Break(Blocks.Stone); ShowNote($"Your {name} broke", 2); } }
	// armor took a hit
	void WearArmor() { foreach (var name in inv.WearArmor()) { Sound.I?.Break(Blocks.Stone); ShowNote($"Your {name} broke", 2); } }
	// which slot a click uses (survival): right-click puts down what's in your hand if it's something you can put down, and
	// otherwise uses the picked offhand item if there is one; breaking a block uses the best tool of the right kind from your
	// tool slots when your hand isn't holding one; hitting a mob uses your best sword
	int ActSlot(MouseButton button)
	{
		int sel = inv.Selected;
		if (!Survival) return sel;
		if (button == MouseButton.Right) return CanPutDown(inv.Id[sel]) || inv.Id[Inventory.Off0 + inv.OffSel] == 0 ? sel : Inventory.Off0 + inv.OffSel;
		if (button != MouseButton.Left) return sel;
		string kind;
		if (MobInSights() != null) kind = "sword";
		else
		{
			if (player.Raycast() is not { } h) return sel;
			int b = h.Id;
			if (h.Sub >= 0 && world.PieceMap.TryGetValue(World.Idx(h.X, h.Y, h.Z), out var a) && a != null) b = World.SpotBlock(a[h.Sub]);
			kind = RightTool(b);
		}
		if (kind == null || Blocks.Get(inv.Id[sel])?.Tool == kind) return sel;
		int k = inv.BestTool(kind);
		return k >= 0 ? k : sel;
	}
	// something you can put down: a block (torches, snow...), a shape or piece, a door, or an item that lies on the ground
	// (a Rock, Clay Ball or Brick). (Tools, food, Snowballs, Sticks, seeds... aren't: with those the offhand gets the right-click.)
	static bool CanPutDown(int id) => Blocks.Get(id) is { } b && (!b.Item || b.Form != null || b.Piece > 0 || b.Places != 0 || Blocks.DoorFirst(id) > 0);
	// ---- Offhand: R picks the next offhand slot, F swaps it with the selected hotbar slot ----
	void CycleOffhand()
	{
		inv.OffSel = (inv.OffSel + 1) % inv.OffCount;
		int id = inv.Id[Inventory.Off0 + inv.OffSel];
		DrawHotbar();
		ShowNote($"Offhand {inv.OffSel + 1}: {(id != 0 ? Blocks.B[id].Name : "Empty")}");
	}
	void SwapOffhand()
	{
		int i = Inventory.Off0 + inv.OffSel;
		if (inv.Id[i] == 0 && inv.Id[inv.Selected] == 0) return;
		inv.Swap(i, inv.Selected); inv.Changed?.Invoke(); SelectSlot(inv.Selected);
	}
	// one Stick from the selected slot becomes a Torch (right-click a lit kiln)
	void LightStick()
	{
		if (Survival)
		{
			inv.UseOne();
			if (!inv.Give(Blocks.Torch)) { inv.SetSlot(inv.ActSlot, Blocks.Stick, (inv.Held == Blocks.Stick ? inv.Count[inv.ActSlot] : 0) + 1); ShowNote("No room in your hotbar for a torch", 2); return; }
		}
		else inv.Give(Blocks.Torch);
		ShowNote("Lit a Torch");
	}

	void Break()
	{
		// a mob in front of you gets hit first
		if (MobInSights() is { } mob) { var t = Blocks.Get(inv.Held); HitMob(mob); if (t?.Tool != null) WearTool(t.Tool == "sword" ? 1 : 2); return; }   // (a sword is made for hitting; other tools wear twice as fast)
		if (player.Raycast() is not { } h) return;
		MobNoise(h.X, h.Y, h.Z);   // (curious mobs come to see)
		// pieces: break just the one you're looking at (or the whole slab / stairs it's part of)
		if (h.Sub >= 0)
		{
			world.PieceMap.TryGetValue(World.Idx(h.X, h.Y, h.Z), out var a);
			if (a != null && NeedTool(World.SpotBlock(a[h.Sub]))) return;
			if (a != null) WearTool(BreakWear(World.SpotBlock(a[h.Sub])));
			if (a != null && World.ShapeAt(a, h.Sub) is { } g)
			{
				Collect(Blocks.FormItem(g.shape, Blocks.StoneDropOf(g.block)), h.X, h.Y, h.Z);   // (stone gives cobblestone)
				Sound.I?.Break(g.block);
				world.SetSpots(h.X, h.Y, h.Z, g.mask, 0);
				return;
			}
			if (a != null) Sound.I?.Break(World.SpotBlock(a[h.Sub]));
			if (a != null) Collect(Blocks.FormItem("piece", Blocks.StoneDropOf(World.SpotBlock(a[h.Sub]))), h.X, h.Y, h.Z);
			world.SetPiece(h.X, h.Y, h.Z, h.Sub, 0);
			return;
		}
		if (h.Y <= 0) return;   // the bottom of the world can't be broken
		int hb = h.Id;
		bool natural = Natural(h.X, h.Y, h.Z);   // (as the world grew it: nobody has put it down or changed it)
		bool staged = StageBreakingOn && natural;   // (stage breaking: held back for now, see StageBreakingReady)
		if (NeedTool(hb, staged)) return;
		int cost = BreakWear(hb);   // (the tool wears after the block is broken, so its last use still counts as that tool)
		// Stone, ores and logs (survival): each swing breaks a few stages; the block only comes out at its last stage
		if (Survival && staged && Blocks.Stages(hb) is int total and > 0)
		{
			long si = World.Idx(h.X, h.Y, h.Z); int done = stages.TryGetValue(si, out var st) && st.block == hb ? st.done : 0;
			done += Blocks.StagesPerSwing(hb, inv.Held);
			if (done < total) { stages[si] = (hb, done); Sound.I?.Break(hb); return; }
			stages.Remove(si);
		}
		// leaves: only a Hoe gets the Leaves block itself; anything else (or your hand) just has a 1 in 5 chance of a Stick
		if (Blocks.IsSnow(hb) || hb == Blocks.SnowBlock)   // snow breaks into Snowballs: 2-4 for each layer, 4 from a Snow Block
			for (int n = hb == Blocks.SnowBlock ? 4 : Blocks.SnowballsFor(Blocks.B[hb].Layers, rnd); n > 0; n--) Collect(Blocks.Snowball, h.X, h.Y, h.Z);
		else if (Harvest(hb, h.X, h.Y, h.Z)) { }   // (a ripe crop or a Cantaloupe: its harvest)
		else if (!Blocks.IsLeaves(hb) || inv.Holding("hoe")) CollectBlock(hb, h.X, h.Y, h.Z);
		if (Survival && staged && Blocks.OreIndex(hb) >= 0) for (int n = Blocks.B[hb].Layer; n > 1; n--) Collect(DropFor(hb), h.X, h.Y, h.Z);   // (an ore in layer n gives n Chunks)
		Sound.I?.Break(hb);
		if (Blocks.IsLeaves(hb) && !inv.Holding("hoe") && rnd.NextDouble() < 0.2) Collect(Blocks.Stick, h.X, h.Y, h.Z);
		if (Blocks.B[hb].Fruit > 0 && !inv.Holding("hoe") && rnd.NextDouble() < 0.25) Collect(Blocks.B[hb].Fruit, h.X, h.Y, h.Z);
		if (Blocks.B[hb].Sapling > 0 && !inv.Holding("hoe") && rnd.NextDouble() < SaplingDrop) Collect(Blocks.B[hb].Sapling, h.X, h.Y, h.Z);   // and now and then their sapling   // fruit trees' leaves (Palm: Dates): a 1 in 4 chance of their fruit
		if (hb == Blocks.GrainRipe) for (int n = 1 + rnd.Next(2); n > 0; n--) Collect(Blocks.GrainSeeds, h.X, h.Y, h.Z);   // ripe Grain: 1-2 seeds too
		var b = Blocks.B[hb];
		if (Blocks.IsTorch(hb) && b.Count > 1) world.SetBlock(h.X, h.Y, h.Z, Blocks.WithSnow(Blocks.TorchVariant(b.Wi, b.Count - 1, b.Burn), b.Snowed));   // take one torch off
		else if (b.Snowed > 0) world.SetBlock(h.X, h.Y, h.Z, Blocks.Snow);   // snow-logged: the torch, flower, rock or Cutter comes out, the snow stays
		else { world.SetBlock(h.X, h.Y, h.Z, 0); world.DropTorchesAround(h.X, h.Y, h.Z, DropFor); }
		if (b.Shape == "door") BreakDoorHalf(h.X, h.Y, h.Z, b);   // (a door: its other half goes too)
		if (b.Crop == "corn") BreakCornHalf(h.X, h.Y, h.Z, b);    // (Corn: both halves go, with one harvest)
		WearTool(cost);
		if (Survival && TreeFellingOn && natural && Blocks.IsLogBlock(hb) && !NaturalLog(h.X, h.Y - 1, h.Z)) Fell(h.X, h.Y, h.Z);   // (a tree's bottom log: the whole tree comes down)
	}

	// a ripe crop besides Grain (its harvest: data/numbers.json CROPS; Corn's top: as the bottom), or a Cantaloupe (its slices),
	// broken: what it gives, in place of its drop (false: it isn't one)
	bool Harvest(int b, int x, int y, int z)
	{
		var d = Blocks.Get(b);
		string crop = b == Blocks.Cantaloupe ? "cantaloupe" : d is { Crop: { } c, Stage: 3 } && c != "cantaloupe" ? c : null;
		if (crop == null || Nums.N.CROPS == null || !Nums.N.CROPS.TryGetValue(crop, out var cn) || cn.harvest is not { } list) return false;
		foreach (var e in list)
		{
			int id = DataKeys.IdOf(e[0].GetString()), lo = e[1].GetInt32(), hi = e[2].GetInt32();
			for (int n = lo + rnd.Next(hi - lo + 1); n > 0; n--) Collect(id, x, y, z);
		}
		return true;
	}
	// Corn broken: its other half goes too (no second harvest)
	void BreakCornHalf(int x, int y, int z, BlockDef b)
	{
		int oy = b.Top ? y - 1 : y + 1;
		if (World.Inside(x, oy, z) && Blocks.Get(world.GetBlock(x, oy, z)) is { Crop: "corn" } o && o.Top != b.Top) world.SetBlock(x, oy, z, 0);
	}
	// right-click farming: a Hoe tills Grass or Dirt, a seed (an item that plants) is planted in tilled soil, Bone Ash fertilizes it
	// (clicking the crop works too). Returns true if it did something, or the click should stop there.
	bool FarmUse(int x, int y, int z, int hb)
	{
		int held = inv.Held, above = world.GetBlock(x, y + 1, z);
		bool onCrop = Blocks.IsAnyCrop(hb);
		int ty = onCrop ? (Blocks.B[hb].Top ? y - 2 : y - 1) : y, target = onCrop ? world.GetBlock(x, ty, z) : hb;
		if (inv.Holding("hoe"))
		{
			if ((!Blocks.IsGrassy(hb) && hb != Blocks.Dirt) || (above != 0 && !Blocks.IsWater(above) && !Blocks.IsFlower(above))) return false;
			if (Blocks.IsFlower(above)) { Collect(above, x, y + 1, z); world.SetBlock(x, y + 1, z, 0); }
			world.SetBlock(x, y, z, Blocks.Tilled);
			Sound.I?.Place(Blocks.Dirt); MobNoise(x, y, z); WearTool(1);
			if (Blocks.IsGrassy(hb) && rnd.NextDouble() < SeedChance) Collect(Blocks.GrainSeeds, x, y + 1, z);   // turned up some seeds
			return true;
		}
		if (Blocks.Get(held)?.Plants is int crop and > 0)
		{
			if (!Blocks.IsSoil(hb) || above != 0) return true;
			world.SetBlock(x, y + 1, z, crop);
			Sound.I?.Place(Blocks.Grass); MobNoise(x, y + 1, z); inv.UseOne();
			return true;
		}
		if (held == Blocks.BoneAsh && Blocks.Get(hb)?.Grows != null)
		{   // Bone Ash on a sapling: used up, and now and then it grows into its tree at once (not used if it couldn't grow)
			var could = world.GrowSapling(x, y, z, justLook: true);
			if (could != World.Growth.Grew) { ShowNote(could == World.Growth.NeedsFour ? "A Redwood needs four saplings in a square" : "There's no room for this tree to grow", 2); return true; }
			Sound.I?.Place(Blocks.Grass); MobNoise(x, y, z); inv.UseOne();
			if (rnd.NextDouble() < BoneAshGrows) world.GrowSapling(x, y, z);
			return true;
		}
		if (held == Blocks.BoneAsh && Blocks.IsSoil(target))
		{
			if (Blocks.SoilFert(target)) world.FertLeft[World.Idx(x, ty, z)] = World.FertHarvests;   // (topped up)
			else world.SetBlock(x, ty, z, Blocks.SoilId(Blocks.SoilWet(target), true));
			Sound.I?.Place(Blocks.Dirt); inv.UseOne();
			return true;
		}
		return false;
	}

	void Place()
	{
		// holding food: eat it (survival); holding Fire Essence: it lifts the gloom
		if (FeedCow() || FeedAnimalClick() || UseFireEssence() || Eat() || ThrowSnowball()) return;
		if (player.Raycast() is not { } h) return;
		// a single layer of snow is in the way of anything but more snow: aiming at it is aiming at the top of what it lies on
		if (Blocks.Get(h.Id)?.Layers == 1 && inv.Held != Blocks.Snow && h.Y > 0)
			h = new Player.Hit { X = h.X, Y = h.Y - 1, Z = h.Z, Ny = 1, Sub = -1, Id = world.GetBlock(h.X, h.Y - 1, h.Z), Pt = new Vector3(h.Pt.X, h.Y, h.Pt.Z) };
		lastAim = h;
		int hb = h.Id, id = inv.Held;
		// right-click a door to open or shut it (hold Shift to build against it instead)
		if (Blocks.IsDoor(hb) && !ShiftDown) { ToggleDoor(h.X, h.Y, h.Z); return; }
		// right-click a Crafting Bench (survival: its recipes) or a Cutter to use it (hold Shift to build against it instead)
		if ((Blocks.Plain(hb) == Blocks.Cutter || hb == Blocks.CraftingBench && inv.Survival) && !ShiftDown) { OpenBench(Blocks.Plain(hb) == Blocks.Cutter); return; }
		// right-click a lit (burning) kiln holding a Stick: the stick catches fire and becomes a Torch
		if (Blocks.IsFurnace(hb) && Blocks.B[hb].Lit && id == Blocks.Stick) { LightStick(); return; }
		if (Blocks.IsFurnace(hb) && !ShiftDown) { kilnAt = World.Idx(h.X, h.Y, h.Z); OpenPanel(kilnPanel); return; }
		if (FarmUse(h.X, h.Y, h.Z, hb)) return;
		// Snow on something that doesn't fill its space (a torch, flower, rock, the Cutter, stairs, slabs, pieces): one layer of
		// snow lies round it, never more. (Aiming at it, or at the side of the wall a torch is on.)
		if (id == Blocks.Snow && !Blocks.CanSnowLog(hb) && World.Inside(h.X + h.Nx, h.Y + h.Ny, h.Z + h.Nz) && Blocks.CanSnowLog(world.GetBlock(h.X + h.Nx, h.Y + h.Ny, h.Z + h.Nz)))
			h = new Player.Hit { X = h.X + h.Nx, Y = h.Y + h.Ny, Z = h.Z + h.Nz, Ny = 1, Sub = -1, Id = world.GetBlock(h.X + h.Nx, h.Y + h.Ny, h.Z + h.Nz), Pt = h.Pt };
		if (id == Blocks.Snow && Blocks.CanSnowLog(h.Id))
		{
			if (Blocks.SnowIn(h.Id) > 0 || (!Blocks.IsPieces(h.Id) && !SnowRests(world.GetBlock(h.X, h.Y - 1, h.Z)))) return;   // (already in snow: no more. Slabs and stairs take it on their tops whatever is under them)
			lastAim = h;
			world.SetBlock(h.X, h.Y, h.Z, Blocks.WithSnow(h.Id, 1)); Placed(Blocks.Snow); return;
		}
		// Snow on snow: one more layer (8 make a Snow Block)
		if (id == Blocks.Snow && Blocks.IsSnow(hb))
		{
			int more = Blocks.B[hb].Layers + 1;
			if (player.TouchesBox(h.X, h.Y, h.Z, 1) && player.Pos.Y < h.Y + (more - 1) / 8f) return;   // (not up into you)
			world.SetBlock(h.X, h.Y, h.Z, more == 8 ? Blocks.SnowBlock : Blocks.Snow + more - 1); Placed(Blocks.Snow); return;
		}
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
		if (Blocks.DoorFirst(id) > 0) { PlaceDoor(h, id); return; }   // a door: 2 blocks tall, facing you
		if (Blocks.IsItem(id) && Blocks.B[id].Places == 0) return;   // items that don't go down on the ground
		int bx = h.X + h.Nx, by = h.Y + h.Ny, bz = h.Z + h.Nz;
		if (Blocks.IsTorch(id))
		{
			int burn = Blocks.B[id].Burn;   // what you're holding: a Torch (lit), or a Smoldering / Burnt / Ever one
			if (Blocks.IsTorch(hb))
			{
				var tb = Blocks.B[hb];
				// a lit torch lights a smoldering one up again (it isn't used up)
				if ((burn == 0 || burn == 3) && tb.Burn == 1) { world.SetBlock(h.X, h.Y, h.Z, Blocks.WithSnow(Blocks.TorchVariant(tb.Wi, tb.Count, 0), tb.Snowed)); ShowNote("Lit the torch again"); return; }
				// clicking a torch adds another one of the same kind to that spot (up to 4 standing, 2 on a wall)
				if (tb.Burn == burn && tb.Count < Blocks.TorchMax(tb.Wi)) { world.SetBlock(h.X, h.Y, h.Z, Blocks.WithSnow(Blocks.TorchVariant(tb.Wi, tb.Count + 1, burn), tb.Snowed)); Placed(id); }
				return;
			}
			// otherwise: on top of a block (standing) or on its side (leaning off the wall), never underneath
			if (h.Ny < 0) return;
			int wi = h.Ny > 0 ? 0 : Blocks.WallIndex(-h.Nx, -h.Nz);
			if (wi < 0 || !World.Inside(bx, by, bz)) return;
			int there = world.GetBlock(bx, by, bz);
			if (there == 0) world.SetBlock(bx, by, bz, Blocks.TorchVariant(wi, 1, burn));
			else if (there == Blocks.Snow) world.SetBlock(bx, by, bz, Blocks.WithSnow(Blocks.TorchVariant(wi, 1, burn), 1));   // (into a layer of snow: it stays round the torch)
			else if (Blocks.IsTorch(there) && Blocks.B[there].Wi == wi && Blocks.B[there].Burn == burn && Blocks.B[there].Count < Blocks.TorchMax(wi))
				world.SetBlock(bx, by, bz, Blocks.WithSnow(Blocks.TorchVariant(wi, Blocks.B[there].Count + 1, burn), Blocks.B[there].Snowed));
			else return;
			Placed(id);
			return;
		}
		{
			if (!World.Inside(bx, by, bz)) return;
			int there = world.GetBlock(bx, by, bz);
			if (there != 0 && !Blocks.IsWater(there) && Blocks.Get(there)?.Layers != 1) return;   // (water and a thin layer of snow can be built over)
			if (id == Blocks.Water) { world.SetBlock(bx, by, bz, Blocks.Water); Placed(Blocks.Water); return; }   // water can go anywhere, even where you stand
			int pid = Blocks.PlaceId(id, rnd);
			pid = Blocks.Turned(pid, h.Ny != 0 ? 0 : h.Nx != 0 ? 1 : 2);   // a log lies pointing out from the side you put it against
			if (Blocks.B[pid].Shape == "snow")
			{   // snow: only on top of a whole block
				if (!SnowRests(world.GetBlock(bx, by - 1, bz)) || Blocks.IsWater(there)) return;
				world.SetBlock(bx, by, bz, pid); Placed(pid); return;
			}
			if (Blocks.B[pid].Shape == "flat")
			{   // a rock: only on top of a solid block
				if (by != h.Y + 1 || !world.Solid(bx, by - 1, bz) || Blocks.IsWater(there)) return;
				world.SetBlock(bx, by, bz, Blocks.WithSnow(pid, there == Blocks.Snow ? 1 : 0)); Placed(pid); return;   // (on a layer of snow: it lies on it)
			}
			if (Blocks.B[pid].Grows != null)
			{   // a sapling: on top of Grass, Dirt, Snowy Grass, Swamp Grass, Mud or Peat (Palm, Orange and Palo Verde on Sand too), into air
				if (by != h.Y + 1 || there != 0 || !SaplingGround(world.GetBlock(bx, by - 1, bz), Blocks.B[pid].Sand)) return;
				world.SetBlock(bx, by, bz, pid); Placed(pid); return;
			}
			if (Blocks.B[pid].Shape == "plant")
			{   // a flower: planted on top of grass or dirt (a Dead Bush on Sand or any Terracotta too)
				int under = world.GetBlock(bx, by - 1, bz);
				bool dry = pid == Blocks.DeadBush && (under == Blocks.Sand || Blocks.IsTerracotta(under));
				if (by != h.Y + 1 || (!Blocks.IsGrassy(under) && under != Blocks.Dirt && !dry) || (there != 0 && there != Blocks.Snow)) return;
				world.SetBlock(bx, by, bz, Blocks.WithSnow(pid, there == Blocks.Snow ? 1 : 0)); Placed(pid); return;   // (into a layer of snow: it stays round the flower)
			}
			if (pid == Blocks.Furnace || pid == Blocks.Foundry)
			{   // a kiln (or the Foundry's Furnace) turns its front towards you
				int first = pid;
				float dx = player.Pos.X - (bx + 0.5f), dz = player.Pos.Z - (bz + 0.5f);
				for (int k = 0; k < 4; k++)
				{
					var f = Blocks.FurnaceFronts[k];
					if (MathF.Abs(dx) > MathF.Abs(dz) ? f[0] == MathF.Sign(dx) : f[1] == (dz == 0 ? 1 : MathF.Sign(dz))) { pid = first + k; break; }
				}
			}
			if (player.Touches(bx, by, bz) || (Blocks.IsFence(pid) && (player.TouchesFence(bx, by, bz) || there == Blocks.Snow))) return;   // (a fence: not into you, nor into snow)
			world.SetBlock(bx, by, bz, Blocks.WithSnow(pid, there == Blocks.Snow ? 1 : 0)); Placed(pid);   // (the Cutter put down in a layer of snow stands in it)
		}
	}
	// what snow can lie on: whole blocks (not water, plants, torches, pieces or more snow)
	static bool SnowRests(int b) => Blocks.Get(b) is { } d && d.Shape == null && !d.Liquid && !d.NoCollide && !d.Item;
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
