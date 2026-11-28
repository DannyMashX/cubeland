// The moon (Beta 0.1.6.1): a flat colour like the sun's, with its phase drawn on it: the lit part is whole pixel columns of a
// 16 x 16 grid, from the right edge while it waxes and from the left while it wanes. The rest is a faint dark square at night
// (gone by day). The same as the web version.
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_prepass_alpha, fog_disabled;

uniform vec4 color = vec4(1.0);
uniform float lit_cols = 16.0;     // how many of the 16 columns are lit
uniform float from_right = 1.0;    // 1: lit from the right edge (waxing), 0: from the left (waning)
uniform float dark_alpha = 1.0;    // how much the unlit part shows (1 at night, 0 by day)
void fragment() {
	float col = floor(clamp(UV.x, 0.0, 0.9999) * 16.0);
	bool lit = from_right > 0.5 ? col >= 16.0 - lit_cols : col < lit_cols;
	ALBEDO = color.rgb * COLOR.rgb * (lit ? 1.0 : 0.15);
	ALPHA = lit ? color.a : color.a * dark_alpha;
}
