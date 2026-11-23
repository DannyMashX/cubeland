Cut-shape face pictures (drawn 2026-11-23). Pictures only: nothing in the game uses them until the code does.

Why: a block picture has a 1px bevel frame round its edge. When a slab, stairs, quarter, piece... shows only part of a
face, the cut edge has no frame. These pictures are the same face with the frame drawn along the cut edges too,
using only that block's own frame pixels. A 2x2 patch that a cut or a new frame line would slice is taken out
altogether (filled with the colour round it), so no half-patches are left.

Name: <block picture name>_<shape>.png, e.g. stone_top_half.png, grass_side_no_top_right.png.
Each is 16x16 and lines up with the full picture; the empty part is transparent.
<shape> says which quarters of the face (as seen in the picture, top = the picture's top) are filled:
  top_half, bottom_half, left_half, right_half
  top_left, top_right, bottom_left, bottom_right          (one quarter)
  no_top_left, no_top_right, no_bottom_left, no_bottom_right   (three quarters, an L)
  diagonal_a (top-left + bottom-right), diagonal_b (top-right + bottom-left)
All four quarters filled = the normal picture. To draw a face, look at which of its 4 quarters are filled and use the
matching picture for all of them.

Simpler rule, slabs only: textures/blocks/<block picture name>_slab_side.png. Its top half is the top_half picture's,
its bottom half the bottom_half picture's, so each half works as a half-height strip. Vertical cuts (vertical slabs,
pieces...) still need the shapes here.

Not made, on purpose (the user's call: their patterns already work cut): bricks of every kind, planks, chiseled
blocks, and log sides/tops.

Blocks: grass (top), grass_side (cut-down sides keep their grass on top), dirt, stone, cobblestone, gravel, sand, clay,
glass, leaves, fir/palm/willow leaves, terracotta, red_terracotta and the 11 coloured terracottas, sandstone,
smooth_sandstone, mud, peat, stone_layer_2..8, cobblestone_layer_2..8.
