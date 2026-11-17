// Things that aren't blocks (items on the ground, mobs...): their picture x their light, fading into the haze like the land.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;

uniform sampler2D tex : filter_nearest, repeat_disable;
uniform vec3 light = vec3(1.0);
uniform float haze_amount = 1.0;   // (0 in the skin editor's preview)
global uniform vec3 fog_color;
global uniform float fog_thickness;
global uniform float fog_max;

void fragment() {
	vec4 t = texture(tex, UV);
	if (t.a < 0.5) discard;
	float haze = (1.0 - exp(-fog_thickness * -VERTEX.z)) * fog_max * haze_amount;
	ALBEDO = mix(t.rgb * COLOR.rgb * light, fog_color, haze);   // (COLOR: a held block's face shading; white otherwise)
}
