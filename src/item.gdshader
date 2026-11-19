// Things that aren't blocks (items on the ground, mobs...): their picture x their light, fading into the haze like the land.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;

uniform sampler2D tex : filter_nearest, repeat_disable;
uniform vec3 light = vec3(1.0);
uniform float haze_amount = 1.0;   // (0 in the skin editor's preview)
uniform float fade = 0.0;          // 0..1: dissolving away (a Gloomling fading out)
global uniform vec3 fog_color;
global uniform float fog_thickness;
global uniform float fog_max;
global uniform float fog_edge;

void fragment() {
	vec4 t = texture(tex, UV);
	if (t.a < 0.5) discard;
	if (fade > 0.0 && fract(sin(dot(floor(FRAGCOORD.xy / 2.0), vec2(12.9898, 78.233))) * 43758.5453) < fade) discard;
	float haze = (1.0 - exp(-fog_thickness * -VERTEX.z)) * fog_max;
	if (fog_edge > 0.0) haze = max(haze, smoothstep(fog_edge * 0.6, fog_edge, length(VERTEX)));
	haze *= haze_amount;
	ALBEDO = mix(t.rgb * COLOR.rgb * light, fog_color, haze);   // (COLOR: a held block's face shading; white otherwise)
}
