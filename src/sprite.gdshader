// Little flat things that always face you (cows' hearts, the puff from a Snowball): their picture x their light,
// fading into the haze like the land, and fading out (alpha) as they go.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque, blend_mix;

uniform sampler2D tex : filter_nearest, repeat_disable, source_color;
uniform bool use_tex = true;          // false: a plain square of tint
uniform vec4 region = vec4(0, 0, 1, 1);   // the part of the picture to show (x, y, width, height in 0..1)
uniform vec3 tint = vec3(1.0);
uniform vec3 light = vec3(1.0);
uniform float alpha = 1.0;
global uniform vec3 fog_color;
global uniform float fog_thickness;
global uniform float fog_max;
global uniform float fog_edge;

void vertex() {
	// face the camera
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
}

void fragment() {
	vec4 t = use_tex ? texture(tex, region.xy + UV * region.zw) : vec4(1.0);
	if (t.a < 0.5) discard;
	float haze = (1.0 - exp(-fog_thickness * -VERTEX.z)) * fog_max;
	if (fog_edge > 0.0) haze = max(haze, smoothstep(fog_edge * 0.6, fog_edge, length(VERTEX)));
	ALBEDO = mix(t.rgb * tint * light, fog_color, haze);
	ALPHA = alpha;
}
