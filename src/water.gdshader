// Water: see-through (the picture's own transparency), drawn from both sides so the surface still shows
// when you look up at it from underwater. Lit and hazed like the blocks.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix, skip_vertex_transform;

uniform sampler2D atlas : filter_nearest, repeat_disable;
global uniform vec3 day_tint;
global uniform vec3 torch_color;
global uniform vec3 fog_color;
global uniform float fog_thickness;
global uniform float fog_max;

varying float v_torch;
varying float v_sky;
varying float v_depth;

void vertex() {
	v_torch = UV2.x;
	v_sky = UV2.y;
	VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_depth = -VERTEX.z;
}

void fragment() {
	vec4 t = texture(atlas, UV);
	vec3 c = t.rgb * COLOR.rgb * max(day_tint * v_sky, v_torch * torch_color);
	float haze = (1.0 - exp(-fog_thickness * v_depth)) * fog_max;
	ALBEDO = mix(c, fog_color, haze);
	ALPHA = t.a;
}
