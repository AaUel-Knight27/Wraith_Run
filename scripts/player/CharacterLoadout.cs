using Godot;

/// <summary>
/// Sits as a sibling of Health/PlayerMovement/WeaponSwitcher under the Player node (see
/// Player.tscn) and loads whichever CharacterData CharacterId points at. Every gameplay script
/// that cares about an operator's kit does GetNodeOrNull&lt;CharacterLoadout&gt;("../CharacterLoadout")
/// and calls GetModifier - if this node is missing, or CharacterId is empty, or the registry does
/// not contain it, every lookup falls back to its default and the game behaves exactly as it does
/// today with the single manny character. Nothing about existing behavior changes until an
/// operator is actually selected.
///
/// Placed first among Player's children in the scene so its _Ready() (which loads Data) runs
/// before the siblings that read it - Godot readies children in scene-tree order.
/// </summary>
public partial class CharacterLoadout : Node
{
	private const string RegistryPath = "res://data/characters/CharacterRegistry.tres";

	/// <summary>Empty by default - no operator selected yet, since the Choose Operator menu is
	/// still a stub. Set this (or replicate it, once operator selection is networked) to switch
	/// which operator's kit applies.</summary>
	[Export] public string CharacterId { get; set; } = string.Empty;

	public CharacterData? Data { get; private set; }

	public override void _Ready()
	{
		if (string.IsNullOrEmpty(CharacterId)) return;

		var registry = ResourceLoader.Load<CharacterRegistry>(RegistryPath);
		if (registry == null)
		{
			GD.PushWarning($"CharacterLoadout: could not load registry at {RegistryPath}.");
			return;
		}

		Data = registry.GetById(CharacterId);
		if (Data == null)
		{
			GD.PushWarning($"CharacterLoadout: operator '{CharacterId}' is not in the registry.");
			return;
		}

		var armature = GetParent()?.GetNodeOrNull<Node>("Armature");
		if (armature != null) CharacterAppearance.Apply(armature, Data);
	}

	/// <summary>Convenience passthrough - see CharacterData.GetModifier. Safe to call with no
	/// operator equipped; returns defaultValue every time.</summary>
	public float GetModifier(string effectId, float defaultValue) =>
		Data?.GetModifier(effectId, defaultValue) ?? defaultValue;
}
