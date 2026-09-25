using Godot;
using System.Reflection;

/// <summary>
/// Every number that decides how the player's legs and hands look, in one place, so the animation
/// tuner (F8) can change them while the game is running and the animation code reads them fresh
/// every frame. Static on purpose: there is one tuning for every player in the process, including
/// the remote copies of other players.
///
/// The defaults are MEASURED from the clips, not guessed. "Authored speed" is how fast the character
/// has to travel for the planted foot to stay still on the ground when the clip plays at 1x, i.e.
/// the ground speed the animator built the cycle for. Measured by playing each clip in Godot and
/// tracking the foot that is on the floor.
///
/// In debug builds the tuner saves to user://animation_tuning.cfg and this class loads it on
/// startup, so a tuning session survives a restart. Once you are happy, paste the numbers the tuner
/// copies (F8 panel, "Copy values") over the defaults below and the file no longer matters.
/// </summary>
public static class AnimationTuning
{
	private const string SavePath = "user://animation_tuning.cfg";

	// --- clip families: ground speed the clips were authored for (metres per second) ----------
	// Indexed by the angle the clip travels at: 0 = straight ahead, 45 = forward diagonal, 135 =
	// backward diagonal, 180 = straight back. The standing set is the "sprint_*" clips: at ~6 m/s
	// forward they are the only standing clips fast enough for this game's 4.7 m/s walk and 7.2 m/s
	// sprint (the "walk_*" clips are authored for 1-2 m/s and would have to play 3x too fast).
	// Backward clips were authored slower. Values come from the Godot-side calibration probe.
	public static float RunSpeedAt0 = 5.95f;
	public static float RunSpeedAt45 = 6.12f;
	public static float RunSpeedAt135 = 4.52f;
	public static float RunSpeedAt180 = 4.44f;
	// The crouch diagonals are the one place the source clips themselves are uneven: in
	// locomotion_crouch_walk_fwd_right, for instance, the trailing foot travels less than half as far
	// per second as the leading one (measured 1.16 vs 1.70 m/s) - a single playback-speed number can only
	// match their average, so a touch of slide on crouch diagonals is the clip, not the code. The F8
	// tuner's per-direction sliders are there to trade that off by eye if it bothers you in practice.
	public static float CrouchSpeedAt0 = 2.08f;
	public static float CrouchSpeedAt45 = 1.33f;
	public static float CrouchSpeedAt135 = 1.62f;
	public static float CrouchSpeedAt180 = 2.04f;

	// --- playback speed ---------------------------------------------------------------------
	/// <summary>Playback speed is clamped to this range. Outside it the cadence starts to look
	/// silly (a jog in slow motion, or a cartoon sprint), so a mismatch shows as foot sliding
	/// instead - which is the honest thing to show.</summary>
	public static float MinTimeScale = 0.55f;
	public static float MaxTimeScale = 1.45f;

	/// <summary>How quickly the measured speed/heading follow the character (seconds). Larger is
	/// smoother but lags; remote players need the smoothing because their position arrives in steps.</summary>
	public static float VelocitySmoothingSeconds = 0.08f;
	public static float DirectionSmoothingSeconds = 0.08f;

	/// <summary>Below StopSpeed a walking state is shown as idle (the character is pressed against
	/// a wall, not walking); it has to exceed StartSpeed to count as moving again.</summary>
	public static float StopSpeed = 0.25f;
	public static float StartSpeed = 0.6f;

	public static float CrossfadeSeconds = 0.24f;

	// --- leg heading (strafing) -----------------------------------------------------------------
	public static bool LegTwistEnabled = true;
	/// <summary>Heading beyond which the backward clips take over, and the heading below which the
	/// forward clips come back. The gap between them is the dead band that stops flicker.</summary>
	public static float BackwardSwitchAngle = 110.0f;
	public static float ForwardSwitchAngle = 70.0f;
	public static float TwistSmoothingSeconds = 0.07f;

	// --- left hand IK ------------------------------------------------------------------------
	public static bool HandIkEnabled = true;
	public static float HandIkBlendPerSecond = 8.0f;
	/// <summary>Where the elbow is pushed towards, in metres from the chest, in the player's frame
	/// (X right, Y up, -Z forward). Left, down and slightly back keeps the elbow tucked.</summary>
	public static float PoleX = -0.45f;
	public static float PoleY = -0.30f;
	public static float PoleZ = 0.15f;

	/// <summary>Ground speed a clip playing at the given absolute travel angle (0 = forward,
	/// 180 = back, in degrees) was authored for.</summary>
	public static float AuthoredSpeed(bool crouch, float absAngleDegrees)
	{
		float a = Mathf.Clamp(absAngleDegrees, 0.0f, 180.0f);
		float s0 = crouch ? CrouchSpeedAt0 : RunSpeedAt0;
		float s45 = crouch ? CrouchSpeedAt45 : RunSpeedAt45;
		float s135 = crouch ? CrouchSpeedAt135 : RunSpeedAt135;
		float s180 = crouch ? CrouchSpeedAt180 : RunSpeedAt180;
		if (a <= 45.0f) return Mathf.Lerp(s0, s45, a / 45.0f);
		if (a <= 135.0f) return Mathf.Lerp(s45, s135, (a - 45.0f) / 90.0f);
		return Mathf.Lerp(s135, s180, (a - 135.0f) / 45.0f);
	}

	/// <summary>Playback speed that makes a clip travelling at <paramref name="absAngleDegrees"/> keep
	/// pace with a character moving at <paramref name="speed"/> m/s.</summary>
	public static float TimeScaleFor(bool crouch, float speed, float absAngleDegrees)
	{
		float authored = AuthoredSpeed(crouch, absAngleDegrees);
		return Mathf.Clamp(speed / Mathf.Max(authored, 0.01f), MinTimeScale, MaxTimeScale);
	}

	private static bool _loaded;

	/// <summary>Loads the saved tuning once per run. Release exports never read it.</summary>
	public static void EnsureLoaded()
	{
		if (_loaded) return;
		_loaded = true;
		if (!OS.IsDebugBuild()) return;
		var config = new ConfigFile();
		if (config.Load(SavePath) != Error.Ok) return;
		foreach (FieldInfo field in Fields())
		{
			if (!config.HasSectionKey("tuning", field.Name)) continue;
			Variant value = config.GetValue("tuning", field.Name);
			if (field.FieldType == typeof(float)) field.SetValue(null, (float)value.AsDouble());
			else if (field.FieldType == typeof(bool)) field.SetValue(null, value.AsBool());
		}
	}

	public static void Save()
	{
		if (!OS.IsDebugBuild()) return;
		var config = new ConfigFile();
		foreach (FieldInfo field in Fields())
			config.SetValue("tuning", field.Name, field.FieldType == typeof(bool)
				? Variant.From((bool)field.GetValue(null)!)
				: Variant.From((float)field.GetValue(null)!));
		config.Save(SavePath);
	}

	/// <summary>Forgets the saved file so the next run starts from the defaults in this class.</summary>
	public static void DeleteSaved()
	{
		if (FileAccess.FileExists(SavePath)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(SavePath));
	}

	/// <summary>The current values as C# lines, ready to paste over the defaults above.</summary>
	public static string ToSourceLines()
	{
		var lines = new System.Text.StringBuilder();
		foreach (FieldInfo field in Fields())
		{
			string type = field.FieldType == typeof(bool) ? "bool" : "float";
			object value = field.GetValue(null)!;
			string literal = value is bool b
				? (b ? "true" : "false")
				: ((float)value).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "f";
			lines.Append($"public static {type} {field.Name} = {literal};\n");
		}
		return lines.ToString();
	}

	private static System.Collections.Generic.IEnumerable<FieldInfo> Fields()
	{
		foreach (FieldInfo field in typeof(AnimationTuning).GetFields(BindingFlags.Public | BindingFlags.Static))
			if (field.FieldType == typeof(float) || field.FieldType == typeof(bool))
				yield return field;
	}
}
