Saved textures: kept for later, not used by the game.
Nothing here is packed into textures.js (build-textures.ps1 only reads textures\) and Godot skips this folder (.gdignore).

basket_weave\  - the "basket" weave Linen Cloth sheets (48x48, 3x3 cut-shape sheets laid out like textures\sheet-layout.json),
                 one per cloth colour. The user chose the "fine" weave for Linen Cloth (2026-11-24) and asked to keep these
                 for another block later.

linen_gear\    - the dropped Worn Linen Backpack and Linen Utility Belt (2026-11-25: the user replaced them with a Linen Bag item).
                 Same paths as textures\: entity\backpack\linen_backpack.png and entity\belt\linen_utility_belt.png (64x64 worn
                 skins on the Backpack / UtilityBelt models, same layout as the leather ones), items\linen_backpack.png and
                 items\linen_utility_belt.png (16x16 icons).
                 installed_greige_woven\ - what was in textures\ until then: greige (stripped aspen colours) with the fine weave
                                           over the leather's mottle (the user found it blotchy on a player).
                 drafts\bone_plain\      - first try: the leather recoloured to bone.
                 drafts\oatmeal\, straw\ - the other woven colour options (stripped birch, stripped orange wood).
                 drafts\greige_smooth\   - one even, low-contrast greige weave all over.
                 drafts\greige_panels\   - plain greige, weave only on the backpack's flap and pocket and the belt's pouches
                                           (the user's last pick before the gear was dropped).
