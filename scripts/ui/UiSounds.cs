using Godot;
using System.Collections.Generic;

/// <summary>
/// The menu's interaction sounds, one call away from anywhere: UiSounds.Play(UiSounds.Sound.Click).
///
/// Registered as an autoload (see project.godot) rather than a node inside the menu scene so a
/// sound that is still playing when the scene changes - the start click when a match launches, for
/// instance - is not cut off with the menu. Safe to call before the autoload exists or with the
/// clips missing: it just does nothing.
///
///  - Click:  ordinary buttons - navigation, back, options.
///  - Select: picking an item - a mode, a map, a list entry, a toggle.
///  - Start:  the big "go" actions - PLAY, START HOSTING, joining a game.
///  - Error:  something failed - could not host, could not connect.
///
/// Routed to the "UI" bus, so the UI volume slider in Settings controls all of it.
/// </summary>
public partial class UiSounds : Node
{
	public enum Sound { Click, Select, Start, Error }

	private const string Directory = "res://audio/Clips/UI and HUD/Menu Sounds";

	// Level trims (dB): the four clips are already close in loudness, so these are small.
	private static readonly Dictionary<Sound, (string Path, float VolumeDb)> Clips = new()
	{
		[Sound.Click] = (Directory + "/InterfaceClick.wav", 0.0f),
		[Sound.Select] = (Directory + "/SelectSound.wav", 0.0f),
		[Sound.Start] = (Directory + "/StartClick.wav", 0.0f),
		[Sound.Error] = (Directory + "/ErrorSound.wav", 0.0f),
	};

	public static UiSounds? Instance { get; private set; }

	// One player per sound, so different sounds overlap freely and a repeat of the same sound just
	// restarts it instead of stacking.
	private readonly Dictionary<Sound, AudioStreamPlayer> _players = new();

	public override void _Ready()
	{
		Instance = this;
		// Menus can be shown while the tree is paused; a click on a pause-menu button should still sound.
		ProcessMode = ProcessModeEnum.Always;

		foreach (var pair in Clips)
		{
			if (!ResourceLoader.Exists(pair.Value.Path))
			{
				GD.PushWarning($"UiSounds: missing clip {pair.Value.Path}");
				continue;
			}
			var stream = ResourceLoader.Load<AudioStream>(pair.Value.Path);
			if (stream == null) continue;

			var player = new AudioStreamPlayer
			{
				Name = pair.Key.ToString(),
				Stream = stream,
				VolumeDb = pair.Value.VolumeDb,
				Bus = "UI",
			};
			AddChild(player);
			_players[pair.Key] = player;
		}
	}

	public override void _ExitTree()
	{
		if (Instance == this) Instance = null;
	}

	/// <summary>Plays the given interaction sound. No-op if the autoload or the clip is missing.</summary>
	public static void Play(Sound sound)
	{
		if (Instance == null || !IsInstanceValid(Instance)) return;
		if (Instance._players.TryGetValue(sound, out AudioStreamPlayer? player)) player.Play();
	}
}
