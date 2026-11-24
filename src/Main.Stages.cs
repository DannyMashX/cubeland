using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Cubeland;

// Stage breaking (survival): Stone, ores and logs don't break in one go. Each swing breaks a few stages (more with a better
// pickaxe or axe), and the block stays where it is, cracked to show how far along it is, until its last stage. Your progress
// on a block is kept until the block changes or you leave the world (it isn't saved, or shared in multiplayer).
// Holding the button down keeps swinging at them. Breaking the bottom log of a tree fells the whole tree. (Both held back
// for now: see StageBreakingReady and TreeFellingReady.)
public partial class Main
{
	readonly Dictionary<int, (int block, int done)> stages = new();
	readonly Dictionary<int, (MeshInstance3D mesh, int frame)> cracks = new();
	static Texture2D[] crackPics;
	const float SwingTime = 0.28f;
	float holdSwing;

	// the cracked look: break_stage_1..8 over the block (or, until those pictures exist, a little darker each stage)
	void UpdateCracks(float dt)
	{
		foreach (var i in stages.Keys.ToArray())
		{
			int x = i % World.WX, z = i / World.WX % World.WZ, y = i / (World.WX * World.WZ);
			if (world.GetBlock(x, y, z) != stages[i].block) stages.Remove(i);   // (it changed: start again)
		}
		foreach (var i in cracks.Keys.ToArray()) if (!stages.ContainsKey(i)) { cracks[i].mesh.QueueFree(); cracks.Remove(i); }
		foreach (var (i, (block, done)) in stages)
		{
			int frame = Math.Clamp((int)MathF.Ceiling(8f * done / Blocks.Stages(block)), 1, 8);
			if (cracks.TryGetValue(i, out var c) && c.frame == frame) continue;
			int x = i % World.WX, z = i / World.WX % World.WZ, y = i / (World.WX * World.WZ);
			var mesh = c.mesh ?? new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One * 1.004f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Position = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) };
			if (c.mesh == null) mobRoot.AddChild(mesh);
			mesh.MaterialOverride = CrackMaterial(frame);
			cracks[i] = (mesh, frame);
		}
	}
	static readonly StandardMaterial3D[] crackMats = new StandardMaterial3D[9];
	static Material CrackMaterial(int frame)
	{
		if (crackMats[frame] != null) return crackMats[frame];
		if (crackPics == null)
		{
			crackPics = new Texture2D[9];
			for (int k = 1; k <= 8; k++)
			{
				string p = $"res://textures/blocks/break_stage_{k}.png";
				if (ResourceLoader.Exists(p)) crackPics[k] = ResourceLoader.Load<Texture2D>(p);
				else if (FileAccess.FileExists(p) && Image.LoadFromFile(ProjectSettings.GlobalizePath(p)) is { } img) crackPics[k] = ImageTexture.CreateFromImage(img);
			}
		}
		var m = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, CullMode = BaseMaterial3D.CullModeEnum.Back };
		if (crackPics[frame] != null) m.AlbedoTexture = crackPics[frame];
		else m.AlbedoColor = new Color(0, 0, 0, 0.08f * frame);
		return crackMats[frame] = m;
	}

	// holding the left button on Stone, an ore or a log: another swing each SwingTime
	void HoldToBreak(float dt)
	{
		if (!Survival || dead || invOpen || menuOpen || Input.MouseMode != LockMode || !Input.IsMouseButtonPressed(MouseButton.Left)) { holdSwing = SwingTime; return; }
		if ((holdSwing -= dt) > 0) return;
		holdSwing = SwingTime;
		if (MobInSights() != null || player.Raycast() is not { } h || h.Sub >= 0 || Blocks.Stages(h.Id) == 0 || !StageBreakingOn || !Natural(h.X, h.Y, h.Z)) return;
		LeftClick();
	}
	// one left-click (or held swing): the tool a slot picks for you, a swing, and the break
	void LeftClick()
	{
		inv.Act = ActSlot(MouseButton.Left); int used = inv.Act, usedId = inv.Held;
		SwingHand();
		try { Break(); } finally { inv.Act = -1; }
		UsedToolSlot(used, usedId);
	}

	// Tree felling: the bottom log of a tree was just broken (x, y, z). The logs joined to it (through any of the 26 blocks
	// round each) are a tree if leaves touch any of them; then they all come down, and so do their leaves (the leaves joined
	// to them, up to 6 leaves away). Logs give their logs, leaves what they give broken by hand. A pile of logs without
	// leaves (a building) stays up.
	void Fell(int x0, int y0, int z0)
	{
		var logs = new List<(int x, int y, int z)>(); var seen = new HashSet<int>();
		var todo = new Queue<(int x, int y, int z)>(); todo.Enqueue((x0, y0, z0)); seen.Add(World.Idx(x0, y0, z0));
		bool leafy = false;
		bool InWorld(int x, int y, int z) => x >= 0 && z >= 0 && y > 0 && x < World.WX && z < World.WZ && y < World.WY;
		while (todo.Count > 0 && logs.Count < 512)
		{
			var (x, y, z) = todo.Dequeue();
			for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
			{
				int nx = x + dx, ny = y + dy, nz = z + dz;
				if ((dx | dy | dz) == 0 || !InWorld(nx, ny, nz) || !seen.Add(World.Idx(nx, ny, nz))) continue;
				if (!NaturalLog(nx, ny, nz) || logs.Count >= 512) continue;
				logs.Add((nx, ny, nz)); todo.Enqueue((nx, ny, nz));
			}
		}
		var sides = new (int, int, int)[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) };
		foreach (var (x, y, z) in logs)
			foreach (var (dx, dy, dz) in sides) if (InWorld(x + dx, y + dy, z + dz) && Blocks.IsLeaves(world.GetBlock(x + dx, y + dy, z + dz)) && Natural(x + dx, y + dy, z + dz)) leafy = true;
		if (!leafy) return;
		// the leaves: joined to the logs through other leaves, up to 6 away
		var leaves = new List<(int x, int y, int z)>(); var lseen = new HashSet<int>();
		var front = new Queue<(int x, int y, int z, int d)>();
		foreach (var (x, y, z) in logs) front.Enqueue((x, y, z, 0));
		while (front.Count > 0 && leaves.Count < 2048)
		{
			var (x, y, z, d) = front.Dequeue();
			if (d >= 6) continue;
			foreach (var (dx, dy, dz) in sides)
			{
				int nx = x + dx, ny = y + dy, nz = z + dz;
				if (!InWorld(nx, ny, nz) || !Blocks.IsLeaves(world.GetBlock(nx, ny, nz)) || !Natural(nx, ny, nz) || !lseen.Add(World.Idx(nx, ny, nz)) || leaves.Count >= 2048) continue;
				leaves.Add((nx, ny, nz)); front.Enqueue((nx, ny, nz, d + 1));
			}
		}
		foreach (var (x, y, z) in logs)
		{
			int b = world.GetBlock(x, y, z);
			Collect(DropFor(b), x, y, z);
			world.SetBlock(x, y, z, 0);
		}
		foreach (var (x, y, z) in leaves)
		{
			int b = world.GetBlock(x, y, z);
			if (rnd.NextDouble() < 0.2) Collect(Blocks.Stick, x, y, z);   // (what leaves give broken by hand)
			if (b == Blocks.PalmLeaves && rnd.NextDouble() < 0.25) Collect(Blocks.Dates, x, y, z);
			world.SetBlock(x, y, z, 0);
		}
		// then what lay on them (snow, torches) falls off
		foreach (var (x, y, z) in logs.Concat(leaves)) world.DropTorchesAround(x, y, z, DropFor);
		felled = logs.Count + leaves.Count;
	}
	int felled;   // (testing: how many blocks the last felling brought down)
	// Held back for now, each with its own switch, so each can come in its own update (and any mix of them works):
	//  - stage breaking (stages, cracks, holding to keep breaking, logs by hand, deep Stone with any pickaxe, n Chunks from an
	//    ore in layer n): the Progression update. --stagebreak turns it on for testing.
	//  - tree felling (a natural tree's bottom log, broken, brings the tree down): the Survival & light update. --treefell.
	// With both off, survival breaks as in Beta 0.1.2.0: one click breaks a block, logs need an axe, deep Stone and deep ores
	// a good enough pickaxe, an ore gives 1 Chunk, and no tree comes down.
	public const bool StageBreakingReady = false, TreeFellingReady = false;
	public static readonly bool StageBreakingOn = StageBreakingReady || Array.IndexOf(OS.GetCmdlineUserArgs(), "--stagebreak") >= 0;
	public static readonly bool TreeFellingOn = TreeFellingReady || Array.IndexOf(OS.GetCmdlineUserArgs(), "--treefell") >= 0;
	// as the world grew it: nobody has put a block down there or changed it (a half-broken block is still natural)
	bool Natural(int x, int y, int z) => !world.Edits.ContainsKey(World.Idx(x, y, z));
	bool NaturalLog(int x, int y, int z) => y >= 0 && Blocks.IsLogBlock(world.GetBlock(x, y, z)) && Natural(x, y, z);
}
