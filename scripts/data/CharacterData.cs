using Godot;
using System;

/// <summary>
/// One playable operator from the roster doc: their class, their named skill, its buff and
/// drawback (each a list of StatModifier so a skill with more than one number - most of them -
/// is represented exactly, not squashed into one field). Supersedes the first draft of this
/// class, which had ad hoc fields like MaxHealth/MoveSpeedMultiplier directly on CharacterData;
/// those are now just entries in Buffs/Drawbacks with EffectId = CharacterEffectIds.OnFootMaxHealthMult
/// etc., which is what actually scales to 25 operators with ~40 distinct effects between them.
///
/// Mirrors WeaponData/WeaponRegistry's shape - one .tres per operator under data/characters/,
/// referenced by CharacterRegistry.tres the same way data/weapons/*.tres are referenced today.
/// </summary>
[GlobalClass]
public partial class CharacterData : Resource
{
	public enum CharacterClass { Driver, Fixer, Operator, Medic, Demolisher }

	[Export] public string CharacterId { get; set; } = string.Empty;
	[Export] public string DisplayName { get; set; } = string.Empty;
	[Export] public CharacterClass Class { get; set; } = CharacterClass.Operator;

	[Export] public string SkillName { get; set; } = string.Empty;
	[Export] public StatModifier[] Buffs { get; set; } = Array.Empty<StatModifier>();
	[Export] public StatModifier[] Drawbacks { get; set; } = Array.Empty<StatModifier>();

	[Export(PropertyHint.File, "*.glb,*.gltf,*.tscn")] public string MeshScenePath { get; set; } = string.Empty;
	[Export(PropertyHint.File, "*.png,*.jpg")] public string PortraitPath { get; set; } = string.Empty;

	// ---------------------------------------------------------------------------------------
	// Appearance - maps to the glTF COLOR_0 vertex-paint regions the Blender addon paints on
	// export: R = jacket/top, G = pants/legs, B = boots/gloves/accent.
	// ---------------------------------------------------------------------------------------
	[Export] public Color JacketColor { get; set; } = Colors.White;
	[Export] public Color PantsColor { get; set; } = Colors.White;
	[Export] public Color AccentColor { get; set; } = Colors.White;

	/// <summary>Sum of every Buffs+Drawbacks entry matching effectId (most operators have at most
	/// one, but nothing stops a future operator having two entries for the same effect). Returns
	/// defaultValue - not 0 - when the operator has no such modifier, so a Mult lookup can default
	/// to 1.0 and a Delta lookup can default to 0.0 from the same method.</summary>
	public float GetModifier(string effectId, float defaultValue)
	{
		bool found = false;
		float total = 0.0f;
		foreach (var mod in Buffs) if (mod.EffectId == effectId) { total += mod.Value; found = true; }
		foreach (var mod in Drawbacks) if (mod.EffectId == effectId) { total += mod.Value; found = true; }
		return found ? total : defaultValue;
	}
}
