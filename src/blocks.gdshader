// The world's blocks: the atlas picture x corner shading x light (sky light by time of day, or torch light,
// whichever is brighter), fading into the haze with distance. Worked out in the same colour space as the
// web version.
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_opaque, skip_vertex_transform;

uniform sampler2D atlas : filter_nearest, repeat_disable;
global uniform vec3 day_tint;         // daylight brightness / colour (set by the day and night cycle)
global uniform vec3 torch_color;
global uniform vec3 fog_color;
global uniform float fog_thickness;   // how fast the haze builds up with distance
global uniform float fog_max;         // the most it can ever cover

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
	if (t.a < 0.5) discard;
	vec3 c = t.rgb * COLOR.rgb * max(day_tint * v_sky, v_torch * torch_color);
	float haze = (1.0 - exp(-fog_thickness * v_depth)) * fog_max;
	ALBEDO = mix(c, fog_color, haze);   // (the Compatibility renderer shows this as-is, like the web version did)
}
