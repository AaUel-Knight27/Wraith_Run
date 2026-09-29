using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Every hand-authored adjustment to how Manny's rig, first-person camera and overall size look,
/// in one place - the data half of the Character Rig Editor (F2, scripts/debug/CharacterRigTuner.cs).
/// Static on purpose, same reasoning as AnimationTuning: there is one calibration for every player in
/// the process, including remote copies, because everyone shares the same manny.glb rig.
///
/// SHAPE: overrides are grouped by PoseMode (Idle, Crouch, Aim, Fire, ...), and a special "Global"
/// mode acts as the base layer every other mode falls back to. CharacterRigOverrideModifier and
/// CharacterRigCameraScaleApplier (CharacterRigApplier.cs) read this every frame and pick, for each
/// bone/camera/scale target, the most specific mode that is both CURRENTLY ACTIVE for that player and
/// actually defines that target - see ActiveLayersHighToLow.
///
/// UNITS: bone position offsets and camera position offsets are metres, matching every other tunable
/// number in this project (WeaponData, LeftHandIk). Bone positions are stored in metres here even
/// though Skeleton3D's own bone-pose API works in the skeleton's raw authored units (Mixamo
/// centimetres) - CharacterRigOverrideModifier converts, the same way LeftHandIk's OffsetMeters does.
/// Rotations are degrees. Scale is a plain multiplier (1.0 = unchanged).
///
/// PERMANENCE: LoadDefaults() is the paste target. The Character Rig Editor's "Copy / Print ALL"
/// button builds exactly the SetBone/SetCamera/SetCharacterScale lines that belong inside it. Paste
/// them in and the calibration ships in release builds - nothing about applying it is debug-only,
/// only the live editor UI is. Until you paste something, every lookup here falls through to the
/// identity default and nothing changes from today's behaviour.
/// </summary>
public static class CharacterRigTuning
{
	private const string SavePath = "user://character_rig_tuning.cfg";

	/// <summary>
	/// Global is the fallback layer, always considered. The eight movement modes share their names
	/// with PlayerMovement.MovementState on purpose - ActiveLayersHighToLow converts one to the other
	/// with Enum.TryParse(state.ToString(), ...), the same trick PlayerAudio.CurrentState() already
	/// uses for ReplicatedMovementState. Aim/Reload/Fire are orthogonal to movement (see PlayerMovement.
	/// IsAiming's own doc comment on why aiming is a flag, not a state) and layer on top when active.
	/// </summary>
	public enum PoseMode
	{
		Global,
		Idle, Walk, Sprint, Crouch, CrouchWalk, Slide, Jump, Fall,
		Aim, Reload, Fire,
	}

	public struct BoneOverride
	{
		public Vector3 PositionOffset;        // metres, added to the bone's pose this frame
		public Vector3 RotationOffsetDegrees;  // degrees, composed on top of the bone's pose rotation
		public float ScaleMultiplier;          // multiplies the bone's pose scale

		public static readonly BoneOverride Identity = new() { ScaleMultiplier = 1.0f };
		public readonly bool IsIdentity =>
			PositionOffset == Vector3.Zero && RotationOffsetDegrees == Vector3.Zero
			&& Mathf.IsEqualApprox(ScaleMultiplier, 1.0f);
	}

	public struct CameraOverride
	{
		public Vector3 PositionOffset;         // metres, added to FirstPersonCamera's local position
		public Vector3 RotationOffsetDegrees;  // degrees, added to FirstPersonCamera's local rotation

		public static readonly CameraOverride Identity = new();
		public readonly bool IsIdentity => PositionOffset == Vector3.Zero && RotationOffsetDegrees == Vector3.Zero;
	}

	private static readonly Dictionary<PoseMode, Dictionary<string, BoneOverride>> BoneOverrides = new();
	private static readonly Dictionary<PoseMode, CameraOverride> CameraOverrides = new();
	private static readonly Dictionary<PoseMode, float> CharacterScaleOverrides = new();

	private static bool _loaded;

	// -------------------------------------------------------------------------------------------
	// Reading/writing overrides - used by the applier (read-only) and the editor (read + write)
	// -------------------------------------------------------------------------------------------

	public static void SetBone(PoseMode mode, string boneName, Vector3 positionOffset,
		Vector3 rotationOffsetDegrees, float scaleMultiplier)
	{
		if (!BoneOverrides.TryGetValue(mode, out var bones))
			BoneOverrides[mode] = bones = new Dictionary<string, BoneOverride>();
		bones[boneName] = new BoneOverride
		{
			PositionOffset = positionOffset,
			RotationOffsetDegrees = rotationOffsetDegrees,
			ScaleMultiplier = scaleMultiplier,
		};
	}

	public static BoneOverride GetBone(PoseMode mode, string boneName) =>
		TryGetBone(mode, boneName, out var ov) ? ov : BoneOverride.Identity;

	/// <summary>True only if <paramref name="mode"/> has an EXPLICIT entry for this bone - distinct
	/// from GetBone's fallback-to-identity, so a deliberate "reset to identity in this one mode" can
	/// still outrank a lower-priority mode that has a real (non-identity) override for the same bone.
	/// This is what CharacterRigOverrideModifier's layering actually walks.</summary>
	public static bool TryGetBone(PoseMode mode, string boneName, out BoneOverride ov)
	{
		if (BoneOverrides.TryGetValue(mode, out var bones) && bones.TryGetValue(boneName, out ov)) return true;
		ov = BoneOverride.Identity;
		return false;
	}

	public static void ClearBone(PoseMode mode, string boneName)
	{
		if (BoneOverrides.TryGetValue(mode, out var bones)) bones.Remove(boneName);
	}

	public static void SetCamera(PoseMode mode, Vector3 positionOffset, Vector3 rotationOffsetDegrees) =>
		CameraOverrides[mode] = new CameraOverride
		{
			PositionOffset = positionOffset,
			RotationOffsetDegrees = rotationOffsetDegrees,
		};

	public static CameraOverride GetCamera(PoseMode mode) =>
		TryGetCamera(mode, out var ov) ? ov : CameraOverride.Identity;

	/// <summary>Presence check, same reasoning as TryGetBone.</summary>
	public static bool TryGetCamera(PoseMode mode, out CameraOverride ov) => CameraOverrides.TryGetValue(mode, out ov);

	public static void ClearCamera(PoseMode mode) => CameraOverrides.Remove(mode);

	public static void SetCharacterScale(PoseMode mode, float scale) => CharacterScaleOverrides[mode] = scale;

	public static float GetCharacterScale(PoseMode mode) =>
		TryGetScale(mode, out var s) ? s : 1.0f;

	/// <summary>Presence check, same reasoning as TryGetBone.</summary>
	public static bool TryGetScale(PoseMode mode, out float scale) => CharacterScaleOverrides.TryGetValue(mode, out scale);

	public static void ClearCharacterScale(PoseMode mode) => CharacterScaleOverrides.Remove(mode);

	/// <summary>Every bone name that has an override in ANY mode - what the editor's target list
	/// badges as "already tuned" and what a per-bone lookup below has to consider.</summary>
	public static IEnumerable<string> TunedBoneNames() =>
		BoneOverrides.Values.SelectMany(d => d.Keys).Distinct();

	// -------------------------------------------------------------------------------------------
	// Layering: which modes are active right now, most specific first
	// -------------------------------------------------------------------------------------------

	/// <summary>
	/// Ordered highest-to-lowest priority. The FIRST mode in this sequence that actually defines a
	/// given bone/camera/scale wins for that target this frame; Global is always last, so it is what
	/// you get when nothing more specific has been tuned. Fire and Reload outrank Aim (you can aim
	/// while firing, but the shot itself is the more specific thing happening), which outranks the
	/// movement state, which outranks Global.
	///
	/// Reads PlayerMovement.State directly on the authority (the only place it is actually kept up to
	/// date - see PlayerMovement's own class comment) and ReplicatedMovementState/ReplicatedAimState
	/// everywhere else, exactly the choice PlayerAudio.CurrentState() already makes for the same
	/// reason: this same code runs on every peer's copy of every player, local and remote alike.
	/// </summary>
	public static IEnumerable<PoseMode> ActiveLayersHighToLow(PlayerMovement movement, PlayerAnimationController controller)
	{
		if (controller.IsFirePlaying) yield return PoseMode.Fire;
		if (controller.IsReloadPlaying) yield return PoseMode.Reload;

		bool authority = movement.IsMultiplayerAuthority();
		bool aiming = authority ? movement.IsAiming : movement.ReplicatedAimState;
		if (aiming) yield return PoseMode.Aim;

		string stateName = authority ? movement.State.ToString() : movement.ReplicatedMovementState;
		if (!Enum.TryParse(stateName, out PoseMode moveMode)) moveMode = PoseMode.Idle;
		yield return moveMode;

		yield return PoseMode.Global;
	}

	// -------------------------------------------------------------------------------------------
	// Permanence: paste target, persistence, print/copy
	// -------------------------------------------------------------------------------------------

	/// <summary>Loads the shipped calibration (below) plus, in debug builds, whatever a tuning
	/// session already saved on top of it. Safe to call once per player spawn - guarded so the actual
	/// work happens once per process.</summary>
	public static void EnsureLoaded()
	{
		if (_loaded) return;
		_loaded = true;
		LoadDefaults();
		if (!OS.IsDebugBuild()) return;
		LoadSavedSession();
	}

	/// <summary>Forgets every live edit and goes back to exactly what LoadDefaults() below says,
	/// ignoring any saved session file. Called locally by the editor's "Reload ALL defaults" button,
	/// and mirrored to every other peer via CharacterRigTuner.RpcReloadAllDefaults - no data needs to
	/// cross the network for that RPC, since LoadDefaults() is the same compiled code on every peer.</summary>
	public static void ReloadFromDefaults()
	{
		BoneOverrides.Clear();
		CameraOverrides.Clear();
		CharacterScaleOverrides.Clear();
		LoadDefaults();
	}

	/// <summary>
	/// THE PASTE TARGET. Run the game, open the Character Rig Editor (F2), tune whatever you want,
	/// press "Copy / Print ALL", and paste the lines it gives you here. Empty until you have pasted
	/// something - every lookup above quietly falls through to Global/identity until then, so this
	/// system does nothing at all to the game unless you have actually tuned something and pasted it.
	/// </summary>
	private static void LoadDefaults()
	{
		// ============================================================================
		// PASTE GENERATED LINES HERE (from the Character Rig Editor's "Copy / Print ALL"
		// button). This is what ships, including in release builds. The debug-only saved
		// session (character_rig_tuning.cfg) only exists so a tuning session survives a
		// restart before you have pasted its results here - see LoadSavedSession below.
		// ============================================================================
	}

	/// <summary>The current values as C# lines, ready to paste into LoadDefaults() above. Skips
	/// anything that is still at its identity value, so re-running this after a session that touched
	/// ten bones but only actually changed three prints exactly three lines.</summary>
	public static string ToSourceLines()
	{
		var sb = new System.Text.StringBuilder();
		foreach (PoseMode mode in Enum.GetValues<PoseMode>())
		{
			if (BoneOverrides.TryGetValue(mode, out var bones))
			{
				foreach (var name in bones.Keys.OrderBy(n => n, StringComparer.Ordinal))
				{
					var ov = bones[name];
					if (ov.IsIdentity) continue;
					sb.Append($"SetBone(PoseMode.{mode}, \"{name}\", ")
						.Append($"new Vector3({F(ov.PositionOffset.X)}f, {F(ov.PositionOffset.Y)}f, {F(ov.PositionOffset.Z)}f), ")
						.Append($"new Vector3({F(ov.RotationOffsetDegrees.X)}f, {F(ov.RotationOffsetDegrees.Y)}f, {F(ov.RotationOffsetDegrees.Z)}f), ")
						.Append($"{F(ov.ScaleMultiplier)}f);\n");
				}
			}
			if (CameraOverrides.TryGetValue(mode, out var cam) && !cam.IsIdentity)
			{
				sb.Append($"SetCamera(PoseMode.{mode}, ")
					.Append($"new Vector3({F(cam.PositionOffset.X)}f, {F(cam.PositionOffset.Y)}f, {F(cam.PositionOffset.Z)}f), ")
					.Append($"new Vector3({F(cam.RotationOffsetDegrees.X)}f, {F(cam.RotationOffsetDegrees.Y)}f, {F(cam.RotationOffsetDegrees.Z)}f));\n");
			}
			if (CharacterScaleOverrides.TryGetValue(mode, out float scale) && !Mathf.IsEqualApprox(scale, 1.0f))
				sb.Append($"SetCharacterScale(PoseMode.{mode}, {F(scale)}f);\n");
		}
		return sb.ToString();
	}

	private static string F(float v)
	{
		string s = v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
		return s == "-0" ? "0" : s;
	}

	// -------------------------------------------------------------------------------------------
	// Debug-only session persistence (survives a restart mid-tuning-session; not shipped)
	// -------------------------------------------------------------------------------------------

	public static void Save()
	{
		if (!OS.IsDebugBuild()) return;
		var config = new ConfigFile();
		foreach (var pair in BoneOverrides)
		{
			string section = "bones_" + pair.Key;
			foreach (var bonePair in pair.Value)
			{
				config.SetValue(section, bonePair.Key + ".pos", bonePair.Value.PositionOffset);
				config.SetValue(section, bonePair.Key + ".rot", bonePair.Value.RotationOffsetDegrees);
				config.SetValue(section, bonePair.Key + ".scale", bonePair.Value.ScaleMultiplier);
			}
		}
		foreach (var pair in CameraOverrides)
		{
			config.SetValue("camera", pair.Key + ".pos", pair.Value.PositionOffset);
			config.SetValue("camera", pair.Key + ".rot", pair.Value.RotationOffsetDegrees);
		}
		foreach (var pair in CharacterScaleOverrides)
			config.SetValue("scale", pair.Key.ToString(), pair.Value);
		config.Save(SavePath);
	}

	/// <summary>Forgets the saved session file so the next run starts from LoadDefaults() alone.</summary>
	public static void DeleteSaved()
	{
		if (FileAccess.FileExists(SavePath)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(SavePath));
	}

	private static void LoadSavedSession()
	{
		var config = new ConfigFile();
		if (config.Load(SavePath) != Error.Ok) return;

		foreach (string section in config.GetSections())
		{
			if (section == "camera")
			{
				var names = SectionModeNames(config, section);
				foreach (string modeName in names)
				{
					if (!Enum.TryParse(modeName, out PoseMode mode)) continue;
					Vector3 pos = GetVector3(config, section, modeName + ".pos");
					Vector3 rot = GetVector3(config, section, modeName + ".rot");
					CameraOverrides[mode] = new CameraOverride { PositionOffset = pos, RotationOffsetDegrees = rot };
				}
			}
			else if (section == "scale")
			{
				foreach (string key in config.GetSectionKeys(section))
				{
					if (!Enum.TryParse(key, out PoseMode mode)) continue;
					CharacterScaleOverrides[mode] = config.GetValue(section, key).AsSingle();
				}
			}
			else if (section.StartsWith("bones_"))
			{
				string modeName = section["bones_".Length..];
				if (!Enum.TryParse(modeName, out PoseMode mode)) continue;
				var bones = new Dictionary<string, BoneOverride>();
				foreach (string boneName in SectionModeNames(config, section))
				{
					bones[boneName] = new BoneOverride
					{
						PositionOffset = GetVector3(config, section, boneName + ".pos"),
						RotationOffsetDegrees = GetVector3(config, section, boneName + ".rot"),
						ScaleMultiplier = config.HasSectionKey(section, boneName + ".scale")
							? config.GetValue(section, boneName + ".scale").AsSingle() : 1.0f,
					};
				}
				BoneOverrides[mode] = bones;
			}
		}
	}

	/// <summary>Every distinct name a "<name>.pos"/"<name>.rot"/"<name>.scale" key group belongs to,
	/// in a section - i.e. every bone name (or, for the camera section, every mode name).</summary>
	private static IEnumerable<string> SectionModeNames(ConfigFile config, string section)
	{
		var names = new HashSet<string>();
		foreach (string key in config.GetSectionKeys(section))
		{
			int dot = key.LastIndexOf('.');
			if (dot > 0) names.Add(key[..dot]);
		}
		return names;
	}

	private static Vector3 GetVector3(ConfigFile config, string section, string key) =>
		config.HasSectionKey(section, key) ? config.GetValue(section, key).AsVector3() : Vector3.Zero;
}
