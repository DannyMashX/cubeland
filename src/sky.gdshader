// The sun, moon and clouds: a flat colour (x the vertex shading), slightly see-through, never hazed.
// depth_prepass_alpha: see-through clouds only show their nearest surface (no bright stripes from faces behind).
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_prepass_alpha, fog_disabled;

uniform vec4 color = vec4(1.0);
void fragment() {
	ALBEDO = color.rgb * COLOR.rgb;
	ALPHA = color.a;
}
