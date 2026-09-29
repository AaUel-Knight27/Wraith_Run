using Godot;

/// <summary>
/// Sits as a sibling of Health/PlayerMovement/WeaponSwitcher under the Player node (see
/// Player.tscn) and loads whichever CharacterData CharacterId points at. Every gameplay script
/// that cares about an operator's kit does GetNodeOrNull&lt;CharacterLoadout&gt;("../CharacterLoadout")
/// and calls GetModifier - if this node is missing, or CharacterId is empty, or the registry does
/// not contain it, every lookup falls back to its default and the game behaves exactly as it does
/// today with the single manny character. Nothing about existing behavior changes until an
/// operator is selected in the lobby (Standard Issue = no operator = no modifiers).
///
/// Placed first among Player's children in the scene so its _Ready() (which loads Data) runs
/// before the siblings that read it - Godot readies children in scene-tree order.
/// </summary>
public partial class CharacterLoadout : Node
{
	private const string RegistryPath = "res://data/characters/CharacterRegistry.tres";

	/// <summary>Which operator's kit applies. Left empty in the scene: on the device that owns this
	/// player it is filled in from the lobby's choice (PlayerProfile.OperatorId) in _Ready. Set it
	/// here to force a specific operator for testing. Empty after that = Standard Issue.
	/// Not replicated yet, so the kit and the colours only exist on the owner's own copy.</summary>
	[Export] public string CharacterId { get; set; } = string.Empty;

	public CharacterData? Data { get; private set; }

	public override void _Ready()
	{
		bool isLocal = GetParent() is Node parent && parent.IsMultiplayerAuthority();
		if (string.IsNullOrEmpty(CharacterId) && isLocal) CharacterId = PlayerProfile.OperatorId;

		if (!string.IsNullOrEmpty(CharacterId))
		{
			var registry = ResourceLoader.Load<CharacterRegistry>(RegistryPath);
			if (registry == null)
			{
				GD.PushWarning($"CharacterLoadout: could not load registry at {RegistryPath}.");
			}
			else
			{
				Data = registry.GetById(CharacterId);
				if (Data == null)
					GD.PushWarning($"CharacterLoadout: operator '{CharacterId}' is not in the registry.");
			}
		}

		// Only the owner's body is dressed for now (other peers do not know your operator or
		// colours yet), and only when there is something to show: an operator, or a saved colour set.
		if (!isLocal) return;
		var armature = GetParent()?.GetNodeOrNull<Node>("Armature");
		if (armature == null) return;
		OperatorInfo.ResolveColors(Data, out Color jacket, out Color pants, out Color accent);
		CharacterAppearance.Apply(armature, jacket, pants, accent);
	}

	/// <summary>Convenience passthrough - see CharacterData.GetModifier. Safe to call with no
	/// operator equipped; returns defaultValue every time.</summary>
	public float GetModifier(string effectId, float defaultValue) =>
		Data?.GetModifier(effectId, defaultValue) ?? defaultValue;
}
