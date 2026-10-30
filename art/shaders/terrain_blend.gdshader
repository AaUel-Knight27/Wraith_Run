shader_type spatial;

// Three-look ground blend for the map kit's terrain (see SurfaceLayer.CreateBlendMaterial).
//
// The terrain builder paints weights into each vertex's colour:
//   COLOR.r = how much of look B (e.g. burnt black soil)
//   COLOR.g = how much of look C (e.g. moss)
//   what is left over (1 - r - g) is look A, the base ground.
// One draw call, six texture reads (albedo + normal per look). No ORM maps on purpose - this keeps it
// cheap on an Intel HD 520; roughness is a plain number per look.

uniform sampler2D a_albedo : source_color, hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D a_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool a_has_normal = false;
uniform vec4 a_tint : source_color = vec4(1.0);
uniform float a_tile = 0.25;
uniform float a_normal_strength = 1.0;
uniform float a_rough = 0.9;

uniform sampler2D b_albedo : source_color, hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D b_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool b_has_normal = false;
uniform vec4 b_tint : source_color = vec4(1.0);
uniform float b_tile = 0.25;
uniform float b_normal_strength = 1.0;
uniform float b_rough = 0.9;

uniform sampler2D c_albedo : source_color, hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D c_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool c_has_normal = false;
uniform vec4 c_tint : source_color = vec4(1.0);
uniform float c_tile = 0.25;
uniform float c_normal_strength = 1.0;
uniform float c_rough = 0.9;

uniform float macro_variation : hint_range(0.0, 1.0) = 0.3;
uniform float macro_tile = 0.035;
uniform float edge_softness : hint_range(0.05, 0.8) = 0.22;

varying vec3 world_pos;

float hash21(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = hash21(i);
	float b = hash21(i + vec2(1.0, 0.0));
	float c = hash21(i + vec2(0.0, 1.0));
	float d = hash21(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float wb = clamp(COLOR.r, 0.0, 1.0);
	float wc = clamp(COLOR.g, 0.0, 1.0);
	float wa = max(0.0, 1.0 - wb - wc);

	vec3 ca = texture(a_albedo, UV * a_tile).rgb * a_tint.rgb;
	vec3 cb = texture(b_albedo, UV * b_tile).rgb * b_tint.rgb;
	vec3 cc = texture(c_albedo, UV * c_tile).rgb * c_tint.rgb;

	// Height-style blend: brighter texels win the edge, so the border between two looks is ragged
	// (soil showing through ash, moss creeping into gaps) instead of a soft crossfade.
	float ha = wa + dot(ca, vec3(0.333)) * 0.5;
	float hb = wb + dot(cb, vec3(0.333)) * 0.5;
	float hc = wc + dot(cc, vec3(0.333)) * 0.5;
	float top = max(ha, max(hb, hc)) - edge_softness;
	float ta = max(ha - top, 0.0) * step(0.0001, wa);
	float tb = max(hb - top, 0.0) * step(0.0001, wb);
	float tc = max(hc - top, 0.0) * step(0.0001, wc);
	float total = max(ta + tb + tc, 0.0001);
	vec3 w = vec3(ta, tb, tc) / total;

	vec3 albedo = ca * w.x + cb * w.y + cc * w.z;

	// Slow brightness wobble so a big flat area does not show the texture repeating.
	float macro = value_noise(world_pos.xz * macro_tile) * 0.65 + value_noise(world_pos.xz * macro_tile * 3.7) * 0.35;
	albedo *= 1.0 + (macro - 0.5) * 2.0 * macro_variation;

	vec3 flat_n = vec3(0.5, 0.5, 1.0);
	vec3 na = a_has_normal ? texture(a_normal, UV * a_tile).rgb : flat_n;
	vec3 nb = b_has_normal ? texture(b_normal, UV * b_tile).rgb : flat_n;
	vec3 nc = c_has_normal ? texture(c_normal, UV * c_tile).rgb : flat_n;

	ALBEDO = albedo;
	ROUGHNESS = a_rough * w.x + b_rough * w.y + c_rough * w.z;
	NORMAL_MAP = na * w.x + nb * w.y + nc * w.z;
	NORMAL_MAP_DEPTH = a_normal_strength * w.x + b_normal_strength * w.y + c_normal_strength * w.z;
}
