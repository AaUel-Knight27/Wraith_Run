using Godot;
using System;
using System.Collections.Generic;

/// <summary>Single availability decision for the runtime-only viewmodel tuning surface.</summary>
public static class ViewModelTunerGate
{
	public static bool IsAvailable => !OS.HasFeature("headless") && !OS.HasFeature("dedicated_server") &&
		(OS.HasFeature("editor") || OS.IsDebugBuild() || OS.HasFeature("viewmodel_tuner") || HasCommandLineFlag());

	private static bool HasCommandLineFlag()
	{
		foreach (string argument in OS.GetCmdlineArgs()) if (argument == "--vmtuner") return true;
		return false;
	}
}

/// <summary>Runtime editor for local ViewModelWeaponData resources; no editor-only APIs are used.</summary>
public partial class ViewModelTuner : Node
{
	public ViewModelManager? Manager { get; set; }
	private Window? _window;
	private OptionButton? _weaponPicker;
	private VBoxContainer? _properties;
	private Label? _status;
	private Label? _modified;
	private string _selectedWeapon = string.Empty;
	private Input.MouseModeEnum _mouseMode;
	private bool _f2Held;
	private string _projectFolder = string.Empty;
	private readonly Stack<ViewModelWeaponData> _undo = new();
	private readonly Stack<ViewModelWeaponData> _redo = new();
	// These are editing conveniences, not weapon data. A lock applies only while this tuner window
	// is open and the current weapon is selected.
	private readonly HashSet<string> _uniformScaleLocks = new();

	public override void _Ready()
	{
		SetProcess(ViewModelTunerGate.IsAvailable);
	}

	public override void _Process(double delta)
	{
		bool held = Input.IsKeyPressed(Key.F2);
		if (held && !_f2Held) Toggle();
		_f2Held = held;
		if (_window != null && _window.Visible && Input.IsKeyPressed(Key.Ctrl) && Input.IsKeyPressed(Key.Z)) Undo();
		if (_window != null && _window.Visible && Input.IsKeyPressed(Key.Ctrl) && Input.IsKeyPressed(Key.Y)) Redo();
	}

	private void Toggle()
	{
		if (!ViewModelTunerGate.IsAvailable || Manager == null) return;
		if (_window == null) CreateWindow();
		if (_window == null) return;
		if (_window.Visible) Hide(); else Show();
	}

	private void CreateWindow()
	{
		if (Manager == null) return;
		bool native = DisplayServer.HasFeature(DisplayServer.Feature.Subwindows);
		GetViewport().GuiEmbedSubwindows = !native;
		_window = new Window
		{
			Name = "ViewModelTunerWindow", Title = "Wraith Run Viewmodel Tuner", Size = new Vector2I(720, 780),
			Transient = false, Exclusive = false, Visible = false,
		};
		AddChild(_window);
		_window.CloseRequested += Hide;
		var root = new VBoxContainer
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
		};
		// Window is not a Container, so SizeFlags alone leave this root at its toolbar-sized
		// minimum. Fill the Window explicitly or the ScrollContainer receives zero height.
		root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_window.AddChild(root);
		var top = new HBoxContainer(); root.AddChild(top);
		_weaponPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		foreach (string weapon in Manager.WeaponNames) _weaponPicker.AddItem(weapon);
		_weaponPicker.ItemSelected += index => SelectWeapon(_weaponPicker.GetItemText((int)index), true);
		top.AddChild(_weaponPicker);
		_modified = new Label(); top.AddChild(_modified);
		AddButton(top, "Equip", () =>
		{
			Manager.EquipForTuner(_selectedWeapon);
			CallDeferred(nameof(BuildProperties));
		});
		AddButton(top, "Test fire", () => { Manager.ActiveViewModel?.PlayMuzzleFire(); Manager.Play("fire"); });
		AddButton(top, "Undo", Undo); AddButton(top, "Redo", Redo);
		var actions = new HBoxContainer(); root.AddChild(actions);
		AddButton(actions, "Draw", () => Manager.Play("draw")); AddButton(actions, "Idle", () => Manager.Play("idle"));
		AddButton(actions, "Fire", () => Manager.Play("fire")); AddButton(actions, "Reload", () => Manager.Play("reload"));
		AddButton(actions, "Walk", () => Manager.Play("walk")); AddButton(actions, "Run", () => Manager.Play("run"));
		AddButton(actions, "Revert saved", RevertSaved); AddButton(actions, "Save", Save);
		var copy = new HBoxContainer(); root.AddChild(copy);
		var sourcePicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		var groupPicker = new OptionButton();
		foreach (string weapon in Manager.WeaponNames) sourcePicker.AddItem(weapon);
		foreach (string group in CopyGroups) groupPicker.AddItem(group);
		if (sourcePicker.ItemCount > 0) sourcePicker.Select(0);
		if (groupPicker.ItemCount > 0) groupPicker.Select(0);
		copy.AddChild(new Label { Text = "Copy" }); copy.AddChild(sourcePicker); copy.AddChild(groupPicker);
		AddButton(copy, "Copy group", () => CopyGroup(sourcePicker.GetItemText(sourcePicker.Selected), groupPicker.GetItemText(groupPicker.Selected)));
		var debug = new HBoxContainer(); root.AddChild(debug);
		var gizmo = new CheckBox { Text = "Muzzle gizmo" }; gizmo.Toggled += on => Manager.ActiveViewModel?.SetMuzzleGizmoVisible(on); debug.AddChild(gizmo);
		var bones = new CheckBox { Text = "Attach/bone axes" }; bones.Toggled += on => Manager.ActiveViewModel?.SetDebugOptions(on, false); debug.AddChild(bones);
		_projectFolder = LoadProjectFolder();
		var projectPath = new LineEdit { PlaceholderText = "Optional project folder for .tres saves", Text = _projectFolder, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		projectPath.TextSubmitted += value => { _projectFolder = value; SaveProjectFolder(); Status("Project folder saved locally."); };
		debug.AddChild(projectPath);
		AddButton(debug, "Save project .tres", SaveProjectResource);
		AddButton(debug, "Export overrides", ExportOverrides);
		var scroll = new ScrollContainer
		{
			Name = "PropertyScroll",
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0.0f, 320.0f),
		};
		root.AddChild(scroll);
		_properties = new VBoxContainer
		{
			Name = "PropertySections",
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		scroll.AddChild(_properties);
		_status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		root.AddChild(_status);
		if (_weaponPicker.ItemCount > 0) SelectWeapon(Manager.ActiveWeaponName, false);
	}

	private static void AddButton(Container parent, string text, Action action)
	{
		var button = new Button { Text = text };
		button.Pressed += action;
		parent.AddChild(button);
	}

	private void Show()
	{
		if (_window == null) return;
		_mouseMode = Input.MouseMode;
		Input.MouseMode = Input.MouseModeEnum.Visible;
		GameUiState.SetViewModelTunerOpen(true);
		_window.Show();
		_window.GrabFocus();
		Status(DisplayServer.HasFeature(DisplayServer.Feature.Subwindows) ? "Native tuner window." : "Embedded tuner fallback (native windows unsupported).");
	}

	private void Hide()
	{
		if (_window == null) return;
		_window.Hide();
		GameUiState.SetViewModelTunerOpen(false);
		Input.MouseMode = _mouseMode;
	}

	private void SelectWeapon(string weapon, bool equip)
	{
		if (Manager == null || string.IsNullOrEmpty(weapon) || Manager.GetWeaponData(weapon) == null)
			weapon = Manager?.ActiveWeaponName ?? string.Empty;
		if (string.IsNullOrEmpty(weapon)) return;
		_selectedWeapon = weapon;
		if (equip) Manager?.EquipForTuner(weapon);
		if (_weaponPicker != null)
			for (int i = 0; i < _weaponPicker.ItemCount; i++) if (_weaponPicker.GetItemText(i) == weapon) _weaponPicker.Select(i);
		_uniformScaleLocks.Clear();
		_undo.Clear(); _redo.Clear(); BuildProperties();
	}

	private void BuildProperties()
	{
		if (_properties == null || Manager?.GetWeaponData(_selectedWeapon) is not ViewModelWeaponData data) return;
		foreach (Node child in _properties.GetChildren()) child.QueueFree();
		Godot.Collections.Array<Godot.Collections.Dictionary> allProperties = data.GetPropertyList();
		int matchingProperties = 0;
		string lastGroup = string.Empty;
		VBoxContainer? section = null;
		foreach (Godot.Collections.Dictionary property in allProperties)
		{
			if (!IsStoredExport(property)) continue;
			matchingProperties++;
			string name = property["name"].AsString();
			string group = GroupFor(name);
			if (group != lastGroup)
			{
				lastGroup = group;
				section = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
				bool collapsible = group is "Muzzle" or "Magazine";
				if (collapsible)
				{
					var fold = new Button { Text = group, ToggleMode = true, ButtonPressed = true, Alignment = HorizontalAlignment.Left };
					VBoxContainer toggleTarget = section;
					fold.Toggled += open => toggleTarget.Visible = open;
					_properties.AddChild(fold);
				}
				else _properties.AddChild(new Label { Text = group, ThemeTypeVariation = "HeaderSmall" });
				_properties.AddChild(section);
			}
			section?.AddChild(BuildEditor(data, name, property));
		}
		GD.Print($"VIEWMODEL TUNER weapon='{_selectedWeapon}' properties={matchingProperties}/{allProperties.Count} sections={_properties.GetChildCount()} visible={_properties.Visible}");
		UpdateModifiedIndicator();
	}

	private Control BuildEditor(ViewModelWeaponData data, string name, Godot.Collections.Dictionary property)
	{
		if (name == "ClipMap") return BuildClipMapEditor(data);
		var row = new HBoxContainer();
		row.AddChild(new Label { Text = name, CustomMinimumSize = new Vector2(210, 0) });
		Variant value = data.Get(name);
		switch (value.VariantType)
		{
			case Variant.Type.Bool:
				var check = new CheckBox { ButtonPressed = value.AsBool() }; check.Toggled += v => Mutate(() => data.Set(name, v), false, RequiresRebuild(name)); row.AddChild(check); break;
			case Variant.Type.Float:
			case Variant.Type.Int:
				var spin = new SpinBox
				{
					Step = 0.001,
					Value = value.AsDouble(),
					MinValue = AllowsNegative(name) ? -1000.0 : 0.0,
					MaxValue = 1000.0,
					SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
				};
				if (property.ContainsKey("hint_string")) ApplyRange(spin, property["hint_string"].AsString());
				spin.ValueChanged += v => Mutate(() => data.Set(name, value.VariantType == Variant.Type.Int ? (Variant)(long)v : (Variant)v), false, RequiresRebuild(name)); row.AddChild(spin); break;
			case Variant.Type.Vector3:
				Vector3 vector = value.AsVector3();
				for (int i = 0; i < 3; i++)
				{
					int axis = i;
					string axisName = axis switch { 0 => "X", 1 => "Y", _ => "Z" };
					var axisField = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
					axisField.AddChild(new Label
					{
						Text = axisName,
						TooltipText = AxisTooltip(axis),
						CustomMinimumSize = new Vector2(18.0f, 0.0f),
					});
					// A viewmodel is a child of the first-person camera. Its pivot must remain in
					// front of that camera (negative Z); otherwise it is physically behind the view
					// and appears to vanish. Keep a small buffer beyond the 5 mm camera near plane.
					double maximum = name == "ViewPosition" && axis == 2 ? -0.01 : 1000.0;
					var field = new SpinBox { Step = 0.001, Value = vector[i], MinValue = -1000.0, MaxValue = maximum, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = $"{axisName}: {AxisTooltip(axis)}" };
					field.ValueChanged += v => Mutate(() =>
					{
						Vector3 next = data.Get(name).AsVector3();
						if (name.EndsWith("Scale", StringComparison.Ordinal) && _uniformScaleLocks.Contains(name)) next = Vector3.One * (float)v;
						else next[axis] = (float)v;
						data.Set(name, next);
					}, false, RequiresRebuild(name));
					axisField.AddChild(field);
					row.AddChild(axisField);
				}
				if (name.EndsWith("Scale", StringComparison.Ordinal))
				{
					var uniform = new CheckBox
					{
						Text = "Uniform",
						ButtonPressed = _uniformScaleLocks.Contains(name),
						TooltipText = "Lock all scale axes together. With this checked, changing X, Y, or Z changes all three.",
					};
					uniform.Toggled += locked =>
					{
						if (locked) _uniformScaleLocks.Add(name); else _uniformScaleLocks.Remove(name);
						if (locked) Mutate(() => data.Set(name, Vector3.One * data.Get(name).AsVector3().X), true);
					};
					row.AddChild(uniform);
				}
				break;
			case Variant.Type.Color:
				var color = new ColorPickerButton { Color = value.AsColor() }; color.ColorChanged += v => Mutate(() => data.Set(name, v), false, RequiresRebuild(name)); row.AddChild(color); break;
			case Variant.Type.String:
				var line = new LineEdit { Text = value.AsString(), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; line.TextSubmitted += v => Mutate(() => data.Set(name, v), true, RequiresRebuild(name)); row.AddChild(line); break;
			default:
				row.AddChild(new Label { Text = value.VariantType is Variant.Type.Object ? ResourcePath(value) : value.ToString() }); break;
		}
		var revert = new Button { Text = "↶", TooltipText = "Revert this field to the last saved value" };
		revert.Pressed += () => RevertProperty(name); row.AddChild(revert);
		return row;
	}

	private Control BuildClipMapEditor(ViewModelWeaponData data)
	{
		var table = new VBoxContainer();
		table.AddChild(new Label { Text = "Generic clip                         Rig clip / resolution" });
		foreach (var pair in data.ClipMap)
		{
			string generic = pair.Key;
			var row = new HBoxContainer();
			row.AddChild(new Label { Text = generic, CustomMinimumSize = new Vector2(180, 0) });
			var target = new LineEdit { Text = pair.Value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			target.TextSubmitted += value => Mutate(() => data.ClipMap[generic] = value, true, true);
			row.AddChild(target);
			bool resolved = Manager?.ActiveViewModel?.ResolvedClips.ContainsKey(generic) ?? false;
			row.AddChild(new Label { Text = resolved ? "✓" : "missing", TooltipText = resolved ? "Resolved in the loaded rig." : "Does not resolve in the loaded rig." });
			table.AddChild(row);
		}
		AddButton(table, "Add clip mapping", () => Mutate(() => data.ClipMap[$"clip_{data.ClipMap.Count + 1}"] = string.Empty, true, true));
		return table;
	}

	private void Mutate(Action change, bool immediate, bool rebuild = false)
	{
		if (Manager?.GetWeaponData(_selectedWeapon) is not ViewModelWeaponData data) return;
		_undo.Push((ViewModelWeaponData)data.Duplicate(true)); _redo.Clear();
		change();
		Manager.RefreshActiveViewModel(rebuild);
		if (immediate) BuildProperties();
		else UpdateModifiedIndicator();
	}

	private void RevertProperty(string property)
	{
		if (Manager?.GetWeaponData(_selectedWeapon) is not ViewModelWeaponData data || Manager.GetSavedSnapshot(_selectedWeapon) is not ViewModelWeaponData saved) return;
		Mutate(() => data.Set(property, saved.Get(property)), true);
	}

	private void RevertSaved() { Manager?.RestoreSaved(_selectedWeapon); BuildProperties(); Status("Reverted to the last saved data."); }

	private void CopyGroup(string sourceWeapon, string group)
	{
		if (Manager?.GetWeaponData(sourceWeapon) is not ViewModelWeaponData source || Manager.GetWeaponData(_selectedWeapon) is not ViewModelWeaponData target || sourceWeapon == _selectedWeapon) return;
		bool rebuild = group is "Rig / Attach" or "Clips";
		Mutate(() =>
		{
			foreach (Godot.Collections.Dictionary property in target.GetPropertyList())
				if (IsStoredExport(property))
				{
					string name = property["name"].AsString();
					if (GroupFor(name) == group) target.Set(name, source.Get(name));
				}
		}, true, rebuild);
		Status($"Copied {group} from {sourceWeapon}.");
	}

	private void Undo() => RestoreHistory(_undo, _redo);
	private void Redo() => RestoreHistory(_redo, _undo);
	private void RestoreHistory(Stack<ViewModelWeaponData> from, Stack<ViewModelWeaponData> to)
	{
		if (from.Count == 0 || Manager?.GetWeaponData(_selectedWeapon) is not ViewModelWeaponData target) return;
		to.Push((ViewModelWeaponData)target.Duplicate(true)); Copy(from.Pop(), target); Manager.RefreshActiveViewModel(); BuildProperties();
	}

	private void Save()
	{
		if (Manager == null) return;
		string path = Manager.GetSourcePath(_selectedWeapon);
		if (OS.HasFeature("editor")) Status(Manager.SaveResource(_selectedWeapon, path) == Error.Ok ? $"Saved {path} and removed its override." : $"Could not save {path}.");
		else { Error result = Manager.SaveOverride(_selectedWeapon, out string overridePath); Status(result == Error.Ok ? $"Saved override: {overridePath}" : $"Could not save {overridePath}: {result}"); }
		UpdateModifiedIndicator();
	}

	private void UpdateModifiedIndicator()
	{
		if (_modified != null) _modified.Text = Manager?.IsModified(_selectedWeapon) == true ? "● Modified" : "Saved";
	}

	private void SaveProjectResource()
	{
		if (Manager == null || string.IsNullOrWhiteSpace(_projectFolder)) { Status("Set a valid project folder first."); return; }
		string source = Manager.GetSourcePath(_selectedWeapon);
		string file = source[(source.LastIndexOf('/') + 1)..];
		string path = _projectFolder.TrimEnd('/', '\\') + "/data/viewmodels/" + file;
		Error result = Manager.SaveResource(_selectedWeapon, path);
		Status(result == Error.Ok ? $"Saved project resource: {path}" : $"Could not save project resource ({result}): {path}");
	}

	private void ExportOverrides()
	{
		const string folder = "user://viewmodel_overrides";
		using DirAccess? directory = DirAccess.Open(folder);
		if (directory == null) { Status("No override folder exists yet."); return; }
		var rows = new List<string>(); directory.ListDirBegin(); string file;
		while ((file = directory.GetNext()) != "")
			if (!directory.CurrentIsDir() && file.EndsWith(".json", StringComparison.Ordinal))
				using (FileAccess? input = FileAccess.Open(folder + "/" + file, FileAccess.ModeFlags.Read)) if (input != null) rows.Add($"// {file}\n{input.GetAsText()}");
		directory.ListDirEnd();
		DisplayServer.ClipboardSet(string.Join("\n\n", rows));
		OS.ShellOpen(ProjectSettings.GlobalizePath(folder));
		Status($"Copied {rows.Count} override patch(es) and opened {folder}.");
	}

	private static string LoadProjectFolder()
	{
		var config = new ConfigFile();
		return config.Load("user://viewmodel_tuner.cfg") == Error.Ok ? config.GetValue("paths", "project_folder", "").AsString() : string.Empty;
	}

	private void SaveProjectFolder()
	{
		var config = new ConfigFile(); config.SetValue("paths", "project_folder", _projectFolder); config.Save("user://viewmodel_tuner.cfg");
	}

	private void Status(string text) { if (_status != null) _status.Text = text; }
	private static string ResourcePath(Variant value) => value.AsGodotObject() is Resource resource ? resource.ResourcePath : "(none)";
	private static readonly string[] CopyGroups = { "Identity", "Rig / Attach", "View Placement", "Muzzle", "Magazine", "Clips", "Culling" };
	private static bool IsStoredExport(Godot.Collections.Dictionary property) => property.ContainsKey("usage") && ((PropertyUsageFlags)property["usage"].AsInt64()).HasFlag(PropertyUsageFlags.Storage);
	private static string AxisTooltip(int axis) => axis switch
	{
		0 => "X axis: left / right",
		1 => "Y axis: down / up",
		_ => "Z axis: back / forward (negative Z is forward from the camera)",
	};
	private static void Copy(ViewModelWeaponData source, ViewModelWeaponData target) { foreach (Godot.Collections.Dictionary p in source.GetPropertyList()) if (IsStoredExport(p)) { string n = p["name"].AsString(); target.Set(n, source.Get(n)); } }
	private static bool RequiresRebuild(string property) => property is "RigScene" or "AttachBoneName" or "ClipMap";
	private static bool AllowsNegative(string property) => property is "IdlePoseTime";
	private static string GroupFor(string name) => name.StartsWith("Muzzle", StringComparison.Ordinal) ? "Muzzle" : name.StartsWith("Magazine", StringComparison.Ordinal) || name.StartsWith("HideModelMagazine", StringComparison.Ordinal) ? "Magazine" : name.StartsWith("Clip", StringComparison.Ordinal) || name.StartsWith("IdlePose", StringComparison.Ordinal) ? "Clips" : name.StartsWith("Cull", StringComparison.Ordinal) ? "Culling" : name.StartsWith("View", StringComparison.Ordinal) ? "View Placement" : name.StartsWith("Attach", StringComparison.Ordinal) || name.StartsWith("Rig", StringComparison.Ordinal) || name.StartsWith("WeaponModel", StringComparison.Ordinal) ? "Rig / Attach" : "Identity";
	private static void ApplyRange(SpinBox spin, string hint) { string[] v = hint.Split(','); if (v.Length >= 2 && double.TryParse(v[0], out double min) && double.TryParse(v[1], out double max)) { spin.MinValue = min; spin.MaxValue = max; } }
}
