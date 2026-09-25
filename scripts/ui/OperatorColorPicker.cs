using Godot;

/// <summary>
/// A self-contained row of three color pickers for an operator's Jacket/Pants/Accent. Builds its
/// own children in code rather than needing a matching .tscn, so it can be dropped into whatever
/// the eventual Choose Operator menu looks like (still a stub as of this writing - LanMenu.cs
/// says "the roster isn't built yet") with a single AddChild call.
///
/// This only edits colors in memory and emits them - it does not write to disk. The Blender/Godot
/// authoring tool is where a CharacterData.tres actually gets saved; this widget is for a
/// runtime/preview context (a customization screen, a test bench) where you want live feedback.
/// </summary>
public partial class OperatorColorPicker : HBoxContainer
{
	[Signal] public delegate void ColorsChangedEventHandler(Color jacket, Color pants, Color accent);

	private ColorPickerButton _jacket = null!;
	private ColorPickerButton _pants = null!;
	private ColorPickerButton _accent = null!;

	public override void _Ready()
	{
		_jacket = BuildSwatch("Jacket", Colors.White);
		_pants = BuildSwatch("Pants", Colors.White);
		_accent = BuildSwatch("Accent", Colors.White);
	}

	/// <summary>Sets all three swatches from an operator's current colors without emitting
	/// ColorsChanged - call this when loading an operator into the picker, not the other way
	/// around.</summary>
	public void SetColors(Color jacket, Color pants, Color accent)
	{
		_jacket.Color = jacket;
		_pants.Color = pants;
		_accent.Color = accent;
	}

	private ColorPickerButton BuildSwatch(string label, Color initial)
	{
		var column = new VBoxContainer();
		column.AddChild(new Label { Text = label });
		var picker = new ColorPickerButton { Color = initial, CustomMinimumSize = new Vector2(48, 32) };
		picker.ColorChanged += _ => EmitCurrentColors();
		column.AddChild(picker);
		AddChild(column);
		return picker;
	}

	private void EmitCurrentColors() =>
		EmitSignal(SignalName.ColorsChanged, _jacket.Color, _pants.Color, _accent.Color);
}
