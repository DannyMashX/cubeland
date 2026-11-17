// The clouds: a flat colour (x the vertex shading), slightly see-through, never hazed.
// Only the faces turned towards you are drawn, so a cloud's top can never show through its underside as
// stripes on faraway clouds; depth_prepass_alpha keeps just the nearest surface on top of that.
// (The cloud mesh is wound the other way round from what Godot calls front, hence cull_front.)
shader_type spatial;
render_mode unshaded, cull_front, blend_mix, depth_prepass_alpha, fog_disabled;

uniform vec4 color = vec4(1.0);
void fragment() {
	ALBEDO = color.rgb * COLOR.rgb;
	ALPHA = color.a;
}
