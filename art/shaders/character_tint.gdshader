shader_type spatial;

// Paint convention (see the Blender addon, next up): R = jacket/top, G = pants/legs,
// B = boots/gloves/accent, and an unpainted vertex (0,0,0) is left exactly as the base material -
// skin, hair, face never get tinted because nothing paints them.
uniform vec4 jacket_color : source_color = vec4(1.0);
uniform vec4 pants_color : source_color = vec4(1.0);
uniform vec4 accent_color : source_color = vec4(1.0);

// Optional base texture. manny.glb currently has no material at all (confirmed empty on export),
// so this defaults to plain white - set it once a real texture exists without touching the tint
// logic below.
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap;
uniform bool has_albedo_texture = false;

uniform float roughness : hint_range(0.0, 1.0) = 0.9;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;

void fragment() {
	vec4 base = has_albedo_texture ? texture(albedo_texture, UV) : vec4(1.0);

	vec3 mask = COLOR.rgb;
	// Neutral = whatever fraction of the vertex is unpainted. Clamped so a mis-painted vertex
	// with r+g+b > 1 never goes negative instead of just over-tinting.
	float neutral = clamp(1.0 - mask.r - mask.g - mask.b, 0.0, 1.0);

	vec3 tinted = base.rgb * neutral
		+ base.rgb * jacket_color.rgb * mask.r
		+ base.rgb * pants_color.rgb * mask.g
		+ base.rgb * accent_color.rgb * mask.b;

	ALBEDO = tinted;
	ROUGHNESS = roughness;
	METALLIC = metallic;
}
