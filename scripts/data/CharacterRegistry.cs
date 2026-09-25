using Godot;
using System;

/// <summary>
/// The full playable roster. One CharacterRegistry.tres, populated by dropping each character's
/// CharacterData.tres (exported by the Blender/Godot tool) into the Characters array - same
/// pattern as WeaponRegistry, just keyed by CharacterId instead of WeaponName.
/// </summary>
[GlobalClass]
public partial class CharacterRegistry : Resource
{
	[Export] public CharacterData[] Characters { get; set; } = Array.Empty<CharacterData>();

	public CharacterData? GetById(string characterId)
	{
		foreach (var character in Characters)
		{
			if (character.CharacterId == characterId)
				return character;
		}
		return null;
	}
}
