// Water: see-through (the picture's own transparency), drawn from both sides so the surface still shows
// when you look up at it from underwater. Lit and hazed like the blocks.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix, skip_vertex_transform;

uniform sampler2D atlas : filter_nearest, repeat_disable;
global uniform vec3 day_tint;
global uniform vec3 torch_color;
global uniform float held_light;
global uniform vec3 fog_color;
global uniform float fog_thickness;
global uniform float fog_max;
global uniform float fog_edge;          // the render distance, in blocks

varying float v_torch;
varying float v_sky;
varying float v_depth;
varying vec3 v_eye;
// (since Beta 0.1.6.0, moving river water: where this pixel is in its picture, in blocks, and how fast the picture slides,
// blocks a second along its columns and rows; 0, 0 = an ordinary water face, drawn as before)
varying vec2 v_at;
varying flat vec2 v_slide;

void vertex() {
	v_torch = UV2.x;
	v_sky = UV2.y;
	VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_depth = -VERTEX.z;
	v_eye = VERTEX;
	v_at = CUSTOM0.xy;
	v_slide = CUSTOM0.zw;
}

void fragment() {
	vec4 t;
	if (v_slide.x != 0.0 || v_slide.y != 0.0) {
		// a whole pixel at a time (1/16 of a block), wrapped inside the picture's own 16 x 16 place in the atlas, nearest pixel
		vec2 moved = floor(TIME * v_slide * 16.0) / 16.0;
		ivec2 px = ivec2(floor(fract(v_at - moved) * 16.0));
		ivec2 corner = ivec2(floor(UV * vec2(textureSize(atlas, 0)) + 0.5));
		t = texelFetch(atlas, corner + clamp(px, ivec2(0), ivec2(15)), 0);
	} else {
		t = texture(atlas, UV);
	}
	float held = held_light > 0.0 ? min(1.2, pow(max(0.0, held_light - length(v_eye)) / 14.0, 1.4)) : 0.0;
	vec3 c = t.rgb * COLOR.rgb * max(day_tint * v_sky, max(v_torch, held) * torch_color);
	float haze = (1.0 - exp(-fog_thickness * v_depth)) * fog_max;
	if (fog_edge > 0.0) haze = max(haze, smoothstep(fog_edge * 0.6, fog_edge, length(v_eye)));   // the render distance: fully hazed at its edge, so nothing pops in or out
	ALBEDO = mix(c, fog_color, haze);
	ALPHA = t.a;
}
