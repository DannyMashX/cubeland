// The stars: points of a flat colour (a separate shader, since setting the point size turns every mesh drawn with it into points).
// depth_prepass_alpha: see-through clouds only show their nearest surface (no bright stripes from faces behind).
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_prepass_alpha, fog_disabled;

uniform vec4 color = vec4(1.0);
uniform float point_size = 2.0;

void vertex() {
	POINT_SIZE = point_size;
}

void fragment() {
	ALBEDO = color.rgb * COLOR.rgb;
	ALPHA = color.a;
}
