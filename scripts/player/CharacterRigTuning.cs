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
/// SHAPE: overrides are keyed by (PoseMode, weapon). PoseMode has a "Global" fallback every other
/// mode falls back to; weapon has an "AnyWeapon" fallback every specific weapon falls back to. So a
/// bone/camera/scale target checks, in order: this mode + this weapon, this mode + any weapon, then
/// repeats for each less-specific mode down to Global + any weapon. CharacterRigOverrideModifier and
/// CharacterRigCameraScaleApplier (CharacterRigApplier.cs) read this every frame and apply whichever
/// of those is the first one actually defined - see ActiveLayerKeysHighToLow.
///
/// UNITS: bone position offsets and camera position offsets are metres, matching every other tunable
/// number in this project (WeaponData, LeftHandIk). Bone positions are stored in metres here even
/// though Skeleton3D's own bone-pose API works in the skeleton's raw authored units (Mixamo
/// centimetres) - CharacterRigOverrideModifier converts, the same way LeftHandIk's OffsetMeters does.
/// Rotations are degrees. Scale is a plain multiplier (1.0 = unchanged).
///
/// PERMANENCE: LoadDefaults() is the paste target. The Character Rig Editor's "Copy / Print ALL"
/// button builds exactly the SetBone/SetCamera/SetCharacterScale lines that belong inside it - weapon
/// is a trailing optional argument, omitted when it's AnyWeapon, so untouched weapon-agnostic tuning
/// prints exactly as it did before this axis existed. Paste the lines in and the calibration ships in
/// release builds - nothing about applying it is debug-only, only the live editor UI is. Until you
/// paste something, every lookup here falls through to the identity default and nothing changes from
/// today's behaviour.
/// </summary>
public static class CharacterRigTuning
{
	private const string SavePath = "user://character_rig_tuning.cfg";

	/// <summary>The weapon-scope wildcard: "apply regardless of which weapon (or no weapon) is
	/// equipped". A real WeaponData.WeaponName is never expected to equal this.</summary>
	public const string AnyWeapon = "*";

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

	// Keyed by (mode, weapon) rather than mode alone - see the class comment's SHAPE paragraph. A
	// plain ValueTuple key works fine as a Dictionary key (structural equality/hashing built in) and
	// keeps every existing per-mode dictionary shape otherwise unchanged.
	private static readonly Dictionary<(PoseMode Mode, string Weapon), Dictionary<string, BoneOverride>> BoneOverrides = new();
	private static readonly Dictionary<(PoseMode Mode, string Weapon), CameraOverride> CameraOverrides = new();
	private static readonly Dictionary<(PoseMode Mode, string Weapon), float> CharacterScaleOverrides = new();

	private static bool _loaded;

	// -------------------------------------------------------------------------------------------
	// Reading/writing overrides - used by the applier (read-only) and the editor (read + write).
	// weapon defaults to AnyWeapon on every convenience method, so code that doesn't care about
	// per-weapon tuning (or predates this axis) reads exactly as it always did.
	// -------------------------------------------------------------------------------------------

	public static void SetBone(PoseMode mode, string boneName, Vector3 positionOffset,
		Vector3 rotationOffsetDegrees, float scaleMultiplier, string weapon = AnyWeapon)
	{
		var key = (mode, weapon);
		if (!BoneOverrides.TryGetValue(key, out var bones))
			BoneOverrides[key] = bones = new Dictionary<string, BoneOverride>();
		bones[boneName] = new BoneOverride
		{
			PositionOffset = positionOffset,
			RotationOffsetDegrees = rotationOffsetDegrees,
			ScaleMultiplier = scaleMultiplier,
		};
	}

	public static BoneOverride GetBone(PoseMode mode, string boneName, string weapon = AnyWeapon) =>
		TryGetBone(mode, boneName, weapon, out var ov) ? ov : BoneOverride.Identity;

	/// <summary>True only if (<paramref name="mode"/>, <paramref name="weapon"/>) has an EXPLICIT
	/// entry for this bone - distinct from GetBone's fallback-to-identity, so a deliberate "reset to
	/// identity for this mode+weapon" can still outrank a less-specific entry for the same bone. This
	/// is what CharacterRigOverrideModifier's layering actually walks, once per key ActiveLayerKeysHighToLow yields.</summary>
	public static bool TryGetBone(PoseMode mode, string boneName, string weapon, out BoneOverride ov)
	{
		if (BoneOverrides.TryGetValue((mode, weapon), out var bones) && bones.TryGetValue(boneName, out ov)) return true;
		ov = BoneOverride.Identity;
		return false;
	}

	public static void ClearBone(PoseMode mode, string boneName, string weapon = AnyWeapon)
	{
		if (BoneOverrides.TryGetValue((mode, weapon), out var bones)) bones.Remove(boneName);
	}

	public static void SetCamera(PoseMode mode, Vector3 positionOffset, Vector3 rotationOffsetDegrees, string weapon = AnyWeapon) =>
		CameraOverrides[(mode, weapon)] = new CameraOverride
		{
			PositionOffset = positionOffset,
			RotationOffsetDegrees = rotationOffsetDegrees,
		};

	public static CameraOverride GetCamera(PoseMode mode, string weapon = AnyWeapon) =>
		TryGetCamera(mode, weapon, out var ov) ? ov : CameraOverride.Identity;

	/// <summary>Presence check, same reasoning as TryGetBone.</summary>
	public static bool TryGetCamera(PoseMode mode, string weapon, out CameraOverride ov) =>
		CameraOverrides.TryGetValue((mode, weapon), out ov);

	public static void ClearCamera(PoseMode mode, string weapon = AnyWeapon) => CameraOverrides.Remove((mode, weapon));

	public static void SetCharacterScale(PoseMode mode, float scale, string weapon = AnyWeapon) =>
		CharacterScaleOverrides[(mode, weapon)] = scale;

	public static float GetCharacterScale(PoseMode mode, string weapon = AnyWeapon) =>
		TryGetScale(mode, weapon, out var s) ? s : 1.0f;

	/// <summary>Presence check, same reasoning as TryGetBone.</summary>
	public static bool TryGetScale(PoseMode mode, string weapon, out float scale) =>
		CharacterScaleOverrides.TryGetValue((mode, weapon), out scale);

	public static void ClearCharacterScale(PoseMode mode, string weapon = AnyWeapon) => CharacterScaleOverrides.Remove((mode, weapon));

	/// <summary>Every bone name that has an override under ANY (mode, weapon) key - what the editor's
	/// target list badges as "already tuned" and what a per-bone lookup below has to consider.</summary>
	public static IEnumerable<string> TunedBoneNames() =>
		BoneOverrides.Values.SelectMany(d => d.Keys).Distinct();

	// -------------------------------------------------------------------------------------------
	// Layering: which modes are active right now, most specific first
	// -------------------------------------------------------------------------------------------

	/// <summary>
	/// Ordered highest-to-lowest priority BY MODE ALONE. The FIRST mode in this sequence that
	/// actually defines a given bone/camera/scale (at whatever weapon key - see
	/// ActiveLayerKeysHighToLow) wins for that target this frame; Global is always last, so it is
	/// what you get when nothing more specific has been tuned. Fire and Reload outrank Aim (you can
	/// aim while firing, but the shot itself is the more specific thing happening), which outranks
	/// the movement state, which outranks Global.
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

	/// <summary>
	/// The full lookup order, mode AND weapon together: for each mode from ActiveLayersHighToLow,
	/// yield (mode, the currently equipped weapon's name) before (mode, AnyWeapon) - so a Glock-only
	/// Aim entry outranks a weapon-agnostic Aim entry, which outranks a Fire entry (wrong mode), and
	/// so on down to (Global, AnyWeapon), which is what you get if nothing at all has been tuned.
	/// <paramref name="weaponName"/> is null/empty/AnyWeapon when no weapon is equipped (or the
	/// caller doesn't track one) - in which case only the AnyWeapon half of each pair is yielded.
	/// </summary>
	public static IEnumerable<(PoseMode Mode, string Weapon)> ActiveLayerKeysHighToLow(
		PlayerMovement movement, PlayerAnimationController controller, string? weaponName)
	{
		bool hasSpecificWeapon = !string.IsNullOrEmpty(weaponName) && weaponName != AnyWeapon;
		foreach (PoseMode mode in ActiveLayersHighToLow(movement, controller))
		{
			if (hasSpecificWeapon) yield return (mode, weaponName!);
			yield return (mode, AnyWeapon);
		}
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
	/// something - every lookup above quietly falls through to Global/AnyWeapon/identity until then,
	/// so this system does nothing at all to the game unless you have actually tuned something and
	/// pasted it.
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
	/// ten bones but only actually changed three prints exactly three lines. A weapon-scoped entry
	/// prints with a trailing weapon argument; an AnyWeapon entry omits it, so lines from before this
	/// axis existed and lines that never used it look exactly the same as they always did.</summary>
	public static string ToSourceLines()
	{
		var sb = new System.Text.StringBuilder();
		var keys = BoneOverrides.Keys.Concat(CameraOverrides.Keys).Concat(CharacterScaleOverrides.Keys)
			.Distinct()
			.OrderBy(k => k.Mode)
			.ThenBy(k => k.Weapon == AnyWeapon ? "" : k.Weapon, StringComparer.Ordinal);

		foreach (var key in keys)
		{
			string weaponArg = key.Weapon == AnyWeapon ? "" : $", \"{key.Weapon}\"";

			if (BoneOverrides.TryGetValue(key, out var bones))
			{
				foreach (var name in bones.Keys.OrderBy(n => n, StringComparer.Ordinal))
				{
					var ov = bones[name];
					if (ov.IsIdentity) continue;
					sb.Append($"SetBone(PoseMode.{key.Mode}, \"{name}\", ")
						.Append($"new Vector3({F(ov.PositionOffset.X)}f, {F(ov.PositionOffset.Y)}f, {F(ov.PositionOffset.Z)}f), ")
						.Append($"new Vector3({F(ov.RotationOffsetDegrees.X)}f, {F(ov.RotationOffsetDegrees.Y)}f, {F(ov.RotationOffsetDegrees.Z)}f), ")
						.Append($"{F(ov.ScaleMultiplier)}f{weaponArg});\n");
				}
			}
			if (CameraOverrides.TryGetValue(key, out var cam) && !cam.IsIdentity)
			{
				sb.Append($"SetCamera(PoseMode.{key.Mode}, ")
					.Append($"new Vector3({F(cam.PositionOffset.X)}f, {F(cam.PositionOffset.Y)}f, {F(cam.PositionOffset.Z)}f), ")
					.Append($"new Vector3({F(cam.RotationOffsetDegrees.X)}f, {F(cam.RotationOffsetDegrees.Y)}f, {F(cam.RotationOffsetDegrees.Z)}f){weaponArg});\n");
			}
			if (CharacterScaleOverrides.TryGetValue(key, out float scale) && !Mathf.IsEqualApprox(scale, 1.0f))
				sb.Append($"SetCharacterScale(PoseMode.{key.Mode}, {F(scale)}f{weaponArg});\n");
		}
		return sb.ToString();
	}

	private static string F(float v)
	{
		string s = v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
		return s == "-0" ? "0" : s;
	}

	// -------------------------------------------------------------------------------------------
	// Debug-only session persistence (survives a restart mid-tuning-session; not shipped).
	// Weapon is folded into the key text with a "::" separator so the common AnyWeapon case keeps
	// looking exactly like it did before this axis existed; a key with no "::" is read as AnyWeapon.
	// -------------------------------------------------------------------------------------------

	public static void Save()
	{
		if (!OS.IsDebugBuild()) return;
		var config = new ConfigFile();
		foreach (var pair in BoneOverrides)
		{
			string section = "bones_" + pair.Key.Mode;
			string namePrefix = pair.Key.Weapon == AnyWeapon ? "" : pair.Key.Weapon + "::";
			foreach (var bonePair in pair.Value)
			{
				string name = namePrefix + bonePair.Key;
				config.SetValue(section, name + ".pos", bonePair.Value.PositionOffset);
				config.SetValue(section, name + ".rot", bonePair.Value.RotationOffsetDegrees);
				config.SetValue(section, name + ".scale", bonePair.Value.ScaleMultiplier);
			}
		}
		foreach (var pair in CameraOverrides)
		{
			// Same "<weapon>::<name>" convention as the bones section above (weapon first) so
			// SplitWeapon below can parse all three sections the same way.
			string name = pair.Key.Weapon == AnyWeapon ? pair.Key.Mode.ToString() : $"{pair.Key.Weapon}::{pair.Key.Mode}";
			config.SetValue("camera", name + ".pos", pair.Value.PositionOffset);
			config.SetValue("camera", name + ".rot", pair.Value.RotationOffsetDegrees);
		}
		foreach (var pair in CharacterScaleOverrides)
		{
			string name = pair.Key.Weapon == AnyWeapon ? pair.Key.Mode.ToString() : $"{pair.Key.Weapon}::{pair.Key.Mode}";
			config.SetValue("scale", name, pair.Value);
		}
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
				foreach (string combined in SectionNames(config, section))
				{
					var (modeName, weapon) = SplitWeapon(combined);
					if (!Enum.TryParse(modeName, out PoseMode mode)) continue;
					Vector3 pos = GetVector3(config, section, combined + ".pos");
					Vector3 rot = GetVector3(config, section, combined + ".rot");
					CameraOverrides[(mode, weapon)] = new CameraOverride { PositionOffset = pos, RotationOffsetDegrees = rot };
				}
			}
			else if (section == "scale")
			{
				foreach (string key in config.GetSectionKeys(section))
				{
					var (modeName, weapon) = SplitWeapon(key);
					if (!Enum.TryParse(modeName, out PoseMode mode)) continue;
					CharacterScaleOverrides[(mode, weapon)] = config.GetValue(section, key).AsSingle();
				}
			}
			else if (section.StartsWith("bones_"))
			{
				string modeName = section["bones_".Length..];
				if (!Enum.TryParse(modeName, out PoseMode mode)) continue;
				// One section holds every weapon for this mode - group by the (weapon) half so each
				// weapon gets its own bone dictionary, same shape SetBone builds up live.
				foreach (string combined in SectionNames(config, section))
				{
					var (boneName, weapon) = SplitWeapon(combined);
					var key = (mode, weapon);
					if (!BoneOverrides.TryGetValue(key, out var bones))
						BoneOverrides[key] = bones = new Dictionary<string, BoneOverride>();
					bones[boneName] = new BoneOverride
					{
						PositionOffset = GetVector3(config, section, combined + ".pos"),
						RotationOffsetDegrees = GetVector3(config, section, combined + ".rot"),
						ScaleMultiplier = config.HasSectionKey(section, combined + ".scale")
							? config.GetValue(section, combined + ".scale").AsSingle() : 1.0f,
					};
				}
			}
		}
	}

	/// <summary>Splits a "<weapon>::<name>" key back into (name, weapon) - or (whole string,
	/// AnyWeapon) if there is no "::", so a key saved before this axis existed still loads correctly
	/// as an AnyWeapon entry. Weapon is always first, for bones ("weapon::boneName"), camera
	/// ("weapon::mode") and scale ("weapon::mode") alike - see Save() above.</summary>
	private static (string Name, string Weapon) SplitWeapon(string combined)
	{
		int sep = combined.IndexOf("::", StringComparison.Ordinal);
		return sep < 0 ? (combined, AnyWeapon) : (combined[(sep + 2)..], combined[..sep]);
	}

	/// <summary>Every distinct name a "<name>.pos"/"<name>.rot"/"<name>.scale" key group belongs to,
	/// in a section - i.e. every "[weapon::]boneName" (or "mode[::weapon]" for the camera section).</summary>
	private static IEnumerable<string> SectionNames(ConfigFile config, string section)
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
