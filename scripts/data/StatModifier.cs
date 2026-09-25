using Godot;

/// <summary>
/// One line of a character's kit: which effect it touches, how much, and the exact text from the
/// roster doc so the UI never has to regenerate a description from raw numbers. EffectId is a
/// free string on purpose rather than an enum - see CharacterEffectIds.cs for the controlled
/// vocabulary and, critically, for which ones a system actually reads yet. An EffectId with no
/// reader today is not a bug: most of this roster's kit leans on vehicles, the garage, and an
/// ability/charge economy that are still docs-only in this project. Storing the modifier now
/// means the day that system exists, it just starts reading a field that was already there.
/// </summary>
[GlobalClass]
public partial class StatModifier : Resource
{
	[Export] public string EffectId { get; set; } = string.Empty;

	/// <summary>Meaning depends on EffectId - see CharacterEffectIds.cs: a multiplier on a base
	/// stat, a signed delta on a count, an absolute override, or 1/0 for a flag.</summary>
	[Export] public float Value { get; set; }

	/// <summary>Verbatim (or near-verbatim) text from the design doc, for tooltips.</summary>
	[Export] public string Note { get; set; } = string.Empty;
}
