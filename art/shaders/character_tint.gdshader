shader_type spatial;

// Paint convention (see the Blender addon, next up): R = jacket/top, G = pants/legs,
// B = boots/gloves/accent, and an unpainted vertex (0,0,0) is left exactly as the base material -
// skin, hair, face never get tinted because nothing paints them.
uniform vec4 jacket_color : source_color = vec4(1.0);
uniform vec4 pants_color : source_color = vec4(1.0);
uniform vec4 accent_color : source_color = vec4(1.0);

// Colour of the unpainted vertices (head, neck). White keeps the old behaviour exactly.
uniform vec4 skin_color : source_color = vec4(1.0);

// false = ignore the vertex-colour mask and wear jacket_color everywhere. CharacterAppearance
// turns this off when it could not write a region mask onto the mesh - with plain
// white vertex colours the mask formula below would otherwise add all three colours together.
uniform bool use_regions = true;

// Keep the imported body's albedo and normal maps while applying operator colours.
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap;
uniform bool has_albedo_texture = false;
uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap;
uniform bool has_normal_texture = false;
uniform sampler2D ambient_occlusion_texture : hint_default_white, filter_linear_mipmap;
uniform bool has_ambient_occlusion_texture = false;

uniform float roughness : hint_range(0.0, 1.0) = 0.9;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;

void fragment() {
	vec4 base = has_albedo_texture ? texture(albedo_texture, UV) : vec4(1.0);

	vec3 mask = COLOR.rgb;
	// Neutral = whatever fraction of the vertex is unpainted. Clamped so a mis-painted vertex
	// with r+g+b > 1 never goes negative instead of just over-tinting.
	float neutral = clamp(1.0 - mask.r - mask.g - mask.b, 0.0, 1.0);

	vec3 tinted = base.rgb * skin_color.rgb * neutral
		+ base.rgb * jacket_color.rgb * mask.r
		+ base.rgb * pants_color.rgb * mask.g
		+ base.rgb * accent_color.rgb * mask.b;
	if (!use_regions) {
		tinted = base.rgb * jacket_color.rgb;
	}

	ALBEDO = tinted;
	if (has_normal_texture) NORMAL_MAP = texture(normal_texture, UV).rgb;
	AO = has_ambient_occlusion_texture ? texture(ambient_occlusion_texture, UV).r : 1.0;
	ROUGHNESS = roughness;
	METALLIC = metallic;
}
