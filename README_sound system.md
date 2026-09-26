# Wraith Run sound system

What this is: a realistic (not "cinematic") weapon sound system built on top of what's already in
your project - `WeaponRegistry.tres`, `WeaponAttachment.cs`, `PlayerAudio.cs`, and the same
live-tuner pattern `WeaponGripTuner.cs` and `AnimationTuner.cs` already use. Four new pieces:

| File | What it is |
|---|---|
| `scripts/autoloads/SoundManager.cs` | Runtime engine: real distance falloff, wall occlusion, directional muzzle blast, speed-of-sound report delay |
| `scripts/debug/WeaponSoundTuner.cs` | Live in-game slider console for a weapon's sound, F6, with duo/LAN sync |
| `default_bus_layout.tres` | Audio buses, the sidechain compressor that makes gunfire mask footsteps, and 5 echo-zone presets |
| This file | Why the numbers are what they are, and every patch you need to paste into your existing scripts |

I don't have a Godot install in this environment, so none of this has been compiled or run. Read
it over in the editor before trusting it blind - I've cross-checked every API name against your
existing code and the current Godot 4 class docs, but I'd rather tell you that than pretend I
tested it.

---

## 1. What's actually in `WeaponRegistry.tres` right now

I read your actual weapon data (not the orphaned copies in `data/weapons/*.tres` - those aren't
referenced anywhere, `WeaponRegistry.tres` is the one `WeaponSwitcher.cs` loads and it embeds
everything inline). Worth fixing regardless of anything else below:

1. **M4, P90, and Kriss Vector all point at files that don't exist**: `Rifle_Shoot-002.wav`,
   `Rifle_Shoot-003.wav`, and `rifle_reload.wav`. Only `Rifle_Shoot-001.wav` and `AK_fire.wav`
   exist in `audio/Clips/Weapons/Rifle/Shoot/`, and only
   `ak_47_assault_rifle_being_unloaded_and_reloaded.wav` exists in `Rifle/Reload/`. Right now
   these three weapons will either throw `GD.PushError("Missing weapon audio...")` or silently
   pick whichever entry resolves, depending on where `PickOne`/the variant array lands.
2. **P90 and Kriss Vector are using the *rifle* fire sound**, not anything SMG-shaped. There is no
   SMG-family clip anywhere in the project yet - see the download list.
3. **Shotgun's reload path is `shotgun_reload_shell.wav`**, but the file on disk is
   `Shotgun_reload.wav` (capitalization and the missing `_shell`). Broken reference.
4. **Bazooka has no explosion wired up.** Its `ImpactSounds` points at the generic pistol impact
   thud. You already own real explosion audio -
   `audio/Clips/Weapons/Grenade Launcher/Explode/Grenade Explode-00{1,2,3}.wav` - it's just not
   referenced by the Bazooka's block. Free fix, no download needed.
5. **Desert Eagle and Glock 19 share the exact same fire clip** (`pistol_shoot.wav`). A .50 AE
   Desert Eagle and a 9mm Glock reading as identical is the kind of thing that undercuts "real."
6. Every weapon's `EmptySound` is the same `reload_click_rifle.wav`, pistols included. Low
   priority, but a distinct pistol/SMG dry-click is a cheap realism win once you have one.

None of this is required for the system below to work - it'll run with whatever's currently
wired, warnings and all - but #2, #4, and #6 are exactly what a "not-Hollywood" pass should fix
first, and #1/#3 are just bugs.

---

## 2. Turning your dB numbers into actual game distances

Real SPL numbers (dB re 20 micropascals at the muzzle) can't go into Godot directly - `VolumeDb`
on an `AudioStreamPlayer3D` is a *relative* gain (0 dB = the clip's own recorded level, roughly
-80 dB = silence), not absolute sound-pressure level. What we can carry over faithfully is the
**relationship between the weapons**, using the one piece of real acoustics that actually matters
here:

> Sound pressure level falls off with distance from a point source as an inverse square law, which
> works out to **-6 dB every time the distance doubles** (20·log₁₀(2) ≈ 6.02). This is also
> exactly what Godot's `INVERSE_SQUARE_DISTANCE` attenuation model does, where `unit_size` is the
> distance at which the sound has dropped -6 dB.

So: if weapon A is X dB louder at the muzzle than weapon B, weapon A stays audible at
**2^(X/6) times B's distance**. That's the whole derivation. Anchoring on Glock 19 (the one
weapon whose current `FireAudioUnitSize = 10.0` already happens to line up with a sensible
baseline) at the midpoint of your 160-165 dB range (162.5 dB):

```
UnitSize(weapon) = 10.0 * 2 ^ ( (dB(weapon) - 162.5) / 6 )
```

**Your table actually says something anti-Hollywood, and it's worth sitting with:** Glock 18
(160-165) and the AK-47 (155-165) overlap almost completely. Real pistols and real rifles are
much closer in raw loudness than movies (and most games) pretend - the huge gap is manufactured
for drama. If you want this to read as *real* rather than *accurate but reskinned Hollywood*,
that overlap is the detail to keep, not smooth away.

Two honest limitations of this model, so you know what you're trading away:

- **Peak muzzle dB isn't the only thing that determines how far a gunshot carries in real life.**
  Rifle rounds add a separate, very loud supersonic crack along the bullet's flight path, and
  muzzle blast directionality (see the cone below) matters as much as raw peak level. Treat the
  numbers below as a well-reasoned starting point, not gospel - nudge them with the tuner once
  they're actually in the level.
- **A rifle really is audible for kilometers in real life.** Useless for a match-sized map. See
  `MaxDistanceMultiplier` below - it compresses absolute range to fit your map while keeping every
  weapon's *relative* reach intact.

### The full table

`MaxDistanceMeters` = `UnitSize x 12` (`SoundManager.MaxDistanceMultiplier`) - one constant, so
raising it once rescales every weapon's audible range together if your finished map turns out
bigger than a few hundred metres across. Karambit isn't a firearm, so it isn't part of the dB
derivation at all - it keeps its existing `6.0` and gets a short fixed range by hand.

| Weapon | Your dB range | Midpoint | Current `UnitSize` | Derived `UnitSize` | Suggested `MaxDistance` |
|---|---|---|---|---|---|
| Glock 19 | 160-165 | 162.5 (anchor) | 10.0 | **10.0** | 120 m |
| Desert Eagle | 160-170 | 165 | 14.0 | **13.3** | 160 m |
| AK-47 | 155-165 | 160 | 16.0 | **7.5** | 90 m |
| M4 | ~165 | 165 | 16.0 | **13.3** | 160 m |
| P90 | 155-165 | 160 | 13.0 | **7.5** | 90 m |
| Kriss Vector | 165-167 | 166 | 13.0 | **15.0** | 180 m |
| Shotgun (not in your list, sourced separately) | ~155-165 | 160 | 18.0 | **7.5** | 90 m |
| Bazooka | 172-185 | 178.5 | 26.0 | **63.5** | 760 m |
| Karambit | n/a (blade) | - | 6.0 | 6.0 (unchanged) | 25 m |
| *AWM (not in the game yet)* | 165-170 | 167.5 | - | *17.8* | *210 m* |
| *Dragunov SVD (not in the game yet)* | 165-170 | 167.5 | - | *17.8* | *210 m* |

The AK-47 number is the one that'll surprise you - going from the current 16.0 down to 7.5 means
it would carry *less* far than the Glock, not more. That's what your own reference numbers imply
once you take them literally (a 2.5 dB gap, pistol louder). Whether to actually ship that or treat
it as a starting point to nudge upward for gameplay balance is a judgment call I'd rather flag
than make for you - it's exactly the kind of thing to A/B in the tuner before committing.

Shotgun's 160 dB isn't from your table (you didn't list one) - it's the commonly-cited real-world
figure for a 12-gauge muzzle blast, in the same 155-165 dB range as the rest.

### Ready-to-paste blocks

Add these seven lines to each weapon's entry in `WeaponRegistry.tres`, replacing the existing
`FireAudioUnitSize` line. `FireVolumeDb` deltas below are honest mix/feel choices, not derived
from the dB table - the table's realism lever is entirely `UnitSize`/`MaxDistance`, doubling up on
volume too would just make the loud weapons *doubly* overstated.

```
# AK-47
FireAudioUnitSize = 7.5
FireVolumeDb = 0.0
FireMaxDistanceMeters = 90.0
FireEmissionAngleDegrees = 70.0
FireEmissionOffAxisAttenuationDb = -7.0
FireCanDuckMovement = true
FireReportDelayEnabled = true

# M4
FireAudioUnitSize = 13.3
FireVolumeDb = 0.0
FireMaxDistanceMeters = 160.0
FireEmissionAngleDegrees = 70.0
FireEmissionOffAxisAttenuationDb = -7.0
FireCanDuckMovement = true
FireReportDelayEnabled = true

# Desert Eagle
FireAudioUnitSize = 13.3
FireVolumeDb = 0.0
FireMaxDistanceMeters = 160.0
FireEmissionAngleDegrees = 90.0
FireEmissionOffAxisAttenuationDb = -5.0
FireCanDuckMovement = true
FireReportDelayEnabled = true

# Glock 19
FireAudioUnitSize = 10.0
FireVolumeDb = 0.0
FireMaxDistanceMeters = 120.0
FireEmissionAngleDegrees = 90.0
FireEmissionOffAxisAttenuationDb = -5.0
FireCanDuckMovement = true
FireReportDelayEnabled = true

# P90
FireAudioUnitSize = 7.5
FireVolumeDb = 0.0
FireMaxDistanceMeters = 90.0
FireEmissionAngleDegrees = 80.0
FireEmissionOffAxisAttenuationDb = -6.0
FireCanDuckMovement = true
FireReportDelayEnabled = true

# Kriss Vector
FireAudioUnitSize = 15.0
FireVolumeDb = 0.0
FireMaxDistanceMeters = 180.0
FireEmissionAngleDegrees = 80.0
FireEmissionOffAxisAttenuationDb = -6.0
FireCanDuckMovement = true
FireReportDelayEnabled = true

# Shotgun
FireAudioUnitSize = 7.5
FireVolumeDb = 2.0
FireMaxDistanceMeters = 90.0
FireEmissionAngleDegrees = 75.0
FireEmissionOffAxisAttenuationDb = -6.0
FireCanDuckMovement = true
FireReportDelayEnabled = true

# Bazooka
FireAudioUnitSize = 63.5
FireVolumeDb = 4.0
FireMaxDistanceMeters = 760.0
FireEmissionAngleDegrees = 360.0
FireEmissionOffAxisAttenuationDb = 0.0
FireCanDuckMovement = true
FireReportDelayEnabled = true

# Karambit
FireAudioUnitSize = 6.0
FireVolumeDb = -6.0
FireMaxDistanceMeters = 25.0
FireEmissionAngleDegrees = 360.0
FireEmissionOffAxisAttenuationDb = 0.0
FireCanDuckMovement = false
FireReportDelayEnabled = false
```

The Bazooka is deliberately left **omnidirectional** (`360`) rather than given a forward cone like
the rifles. That's not an oversight: an RPG-style launcher has a dangerous *backblast* out the
rear of the tube, so "quieter from behind" would be a less accurate model for this one weapon, not
a more accurate one. Said out loud once, then encoded as a number - that's the level of "careful"
this whole table is aiming for.

---

## 3. Architecture

### Directional muzzle blast (`FireEmissionAngleDegrees` / `FireEmissionOffAxisAttenuationDb`)

Real muzzle blast is louder in front of and beside the gun than directly behind it - Godot's
`emission_angle_*` properties on `AudioStreamPlayer3D` model exactly this. `360` is the sentinel
for "no cone" (omnidirectional); anything less enables it. See the integration patch below for the
one fixed 90-degree rotation this needs to actually point down the barrel instead of out the
grip's original forward axis.

### Range and occlusion (`SoundManager.PlayWeaponFire`)

Every shot goes through one function that: sets `AttenuationModel = InverseSquareDistance` (real
falloff, not the default linear-ish curve most quick implementations end up with), applies the
weapon's `UnitSize`/`MaxDistance`/`VolumeDb`, and does **one raycast per shot** from muzzle to
listener. If something solid is in the way, the shot plays quieter and with a lower low-pass
cutoff - muffled, the way a shot through a wall actually sounds, not just fainter. One raycast per
shot (not per frame) because a one-shot sample doesn't need updating after it's already started.

### The crack-then-boom delay

Past 15 m, a shot's audio is delayed by `distance / 343 m/s` before it plays - real supersonic
gunfire heard from range genuinely arrives after its muzzle flash, and every listener computes
their *own* delay based on their *own* distance to the shot, so it stays correct for every player
independently with no extra networking. This one is arguably the single biggest lever for "not
Hollywood": nothing says movie sound design like a shot 200 m away being heard instantaneously.

### Priority / masking - why a nearby gunshot should drown out a footstep

This is the one place I'd push back gently on "build me a tool that controls it": the right
implementation isn't a script that watches "is a gun currently firing" and mutes something. It's a
**sidechain compressor**, a real mixing-console tool, sitting on the `Movement` bus and keyed off
the `Weapons` bus (`default_bus_layout.tres`, `AudioEffectCompressor` with `sidechain = "Weapons"`
- this is a real, built-in Godot effect, not something scripted here). The reason that matters:

- It reacts to the **already-mixed, already-distance-attenuated signal**. A shot 150 m away is
  quiet by the time it reaches you and barely ducks anything; a shot next to you is loud and ducks
  hard. That's exactly correct real auditory masking (what actually determines whether you can
  hear a footstep is how loud the gunfire is *at your ear*, not how loud the gun itself is), and
  it falls out of the DSP for free.
- It applies to **any** gunfire you can hear, not just your own trigger pulls. If someone fires
  near you, you shouldn't hear their footsteps either, and this handles that with zero extra code.
- Attack time is 20 microseconds (as fast as the effect allows) - it clamps down the instant the
  transient hits, matching how real masking is essentially instantaneous. Release is 300 ms, so a
  burst from something full-auto keeps footsteps ducked for the whole burst rather than flickering
  open between rounds.

`FireCanDuckMovement` is the one per-weapon override, and it exists for exactly one weapon: the
Karambit, whose swing isn't loud enough in reality to mask anything. It works by routing to a
second, parallel `WeaponsQuiet` bus that also feeds `Master` but was never wired as the
compressor's sidechain source - so it plays normally but never counts as "loud" for ducking
purposes.

If you also want gunfire to duck `Ambience` or `Voice`, the extension is one line: add the same
`AudioEffectCompressor` (sidechain `Weapons`) to that bus too in the Audio panel.

### Echo (`Reverb_*` buses + `Area3D`)

Five presets are already built and tuned by rough real RT60 character - Outdoor (dry), SmallRoom,
Hallway, Hall, Tunnel (see the comments in `default_bus_layout.tres` for the reasoning behind each
one's numbers). To use them: drop an `Area3D` around an indoor space in your map (the mapkit
pieces under `art/`), set its `reverb_bus_enabled = true` and `reverb_bus_name` to whichever preset
fits the room, and every sound - footsteps and gunfire alike - that happens inside it automatically
gets sent to that room's echo. No code needed; this is a built-in Godot 4 feature
(`Area3D.reverb_bus_*`).

For live-tuning the echo itself while playing: **use Godot's own Audio panel + Remote scene tree
tab** rather than the custom tuner. With the game running from the editor, the Audio panel at the
bottom lets you drag any bus effect's parameters (room size, wet, predelay) in real time and hear
it change instantly - it's already exactly the "live controller" you described, and it's more
capable than anything I could safely hand-build blind. The custom `WeaponSoundTuner` tool exists
specifically for the case Godot's own tools *can't* cover: syncing live changes to an actual second
machine over LAN (see below) - Remote tab only reaches your own editor's local debug session.

---

## 4. Integration - exact patches

### `scripts/data/WeaponData.cs`

Add these fields (I'd put them right after the existing `FireAudioUnitSize` property):

```csharp
	// --- audio: realism (SoundManager.cs / WeaponSoundTuner.cs) -----------------------------
	// See scripts/autoloads/SoundManager.cs for what these actually do. Tune live with the
	// sound tuner (F6) rather than guessing numbers by hand.

	/// <summary>Source loudness trim in dB at the FireAudioUnitSize reference distance. Keep
	/// deltas small - the realism lever is UnitSize/MaxDistance, not this (see the README's
	/// inverse-square derivation); doubling up here just overstates the loud weapons twice.</summary>
	[Export] public float FireVolumeDb { get; set; } = 0.0f;

	/// <summary>Hard cutoff in metres - past this the shot isn't spawned as an audible source at
	/// all (silent AND free, no voice wasted). Should be well beyond FireAudioUnitSize.</summary>
	[Export] public float FireMaxDistanceMeters { get; set; } = 120.0f;

	/// <summary>Half-angle, in degrees, of the cone straight down the barrel that hears the shot
	/// at full volume. 360 = omnidirectional (no cone). Real muzzle blast is louder in front of
	/// and beside the gun than directly behind it.</summary>
	[Export] public float FireEmissionAngleDegrees { get; set; } = 360.0f;

	/// <summary>How many dB quieter the shot is heard from directly behind the muzzle vs on-axis.
	/// Only matters when FireEmissionAngleDegrees is less than 360.</summary>
	[Export] public float FireEmissionOffAxisAttenuationDb { get; set; } = -6.0f;

	/// <summary>Whether this weapon's report ducks the Movement bus (footsteps/jump/land) through
	/// the sidechain compressor on that bus. True for every firearm; false for the Karambit,
	/// which is not loud enough in reality to mask anything.</summary>
	[Export] public bool FireCanDuckMovement { get; set; } = true;

	/// <summary>Whether a listener more than ~15 m away hears this shot's report delayed by
	/// distance / speed-of-sound, the way real gunfire is a crack-then-boom past close range. On
	/// for firearms, off for melee.</summary>
	[Export] public bool FireReportDelayEnabled { get; set; } = true;
```

### `scripts/player/WeaponAttachment.cs`

**`AddAudioPlayers`** - add bus routing and the emission-cone rotation fix right after the three
existing `AddAudioPlayer` calls:

```csharp
	private void AddAudioPlayers(Node3D grip, WeaponData weapon)
	{
		Node host = _muzzle ?? (Node)grip;
		_fireAudio = AddAudioPlayer(host, "WeaponFireAudio", PickOne(weapon.FireSounds), weapon.FireAudioUnitSize);
		_reloadAudio = AddAudioPlayer(grip, "WeaponReloadAudio", PickOne(weapon.ReloadSounds), 8.0f);
		_emptyAudio = AddAudioPlayer(grip, "WeaponEmptyAudio", weapon.EmptySound, 5.0f);

		// NEW: bus routing (WeaponsQuiet skips the footstep-ducking sidechain - see
		// default_bus_layout.tres) and the muzzle-cone rotation fix.
		string bus = weapon.FireCanDuckMovement ? "Weapons" : "WeaponsQuiet";
		if (_fireAudio != null)
		{
			_fireAudio.Bus = bus;
			// AudioStreamPlayer3D's directional cone points down its own local -Z axis, but this
			// project's grip convention has +Y as the barrel/line-of-fire direction (see
			// AddMuzzle). Rotating +90 degrees around local X maps -Z onto that +Y, so
			// FireEmissionAngleDegrees actually points down the barrel. Harmless when the cone is
			// disabled (FireEmissionAngleDegrees = 360).
			_fireAudio.RotationDegrees = new Vector3(90.0f, 0.0f, 0.0f);
		}
		if (_reloadAudio != null) _reloadAudio.Bus = bus;
		if (_emptyAudio != null) _emptyAudio.Bus = bus;

		if (weapon.FireSounds.Length > 1)
		{
			// One player, many clips: the stream is swapped per shot in PlayFire's caller path via
			// _fireVariants so a burst does not repeat the same waveform.
			_fireVariants = new AudioStream?[weapon.FireSounds.Length];
			for (int i = 0; i < weapon.FireSounds.Length; i++)
				_fireVariants[i] = LoadStream(weapon.FireSounds[i]);
		}
		else
		{
			_fireVariants = null;
		}

		if (weapon.FireSounds.Length == 0 && weapon.FireMode != WeaponData.FireModeType.Melee)
			GD.PushWarning($"{weapon.WeaponName} has no fire sounds assigned.");
	}
```

**`PlayFire`** - route through `SoundManager` instead of calling `Play()` directly:

```csharp
	public void PlayFire()
	{
		if (_fireAudio != null)
		{
			RollFireVariant();
			_fireAudio.PitchScale = _rng.RandfRange(0.94f, 1.06f);
			Vector3 muzzlePosition = _muzzle?.GlobalPosition ?? GlobalPosition;
			SoundManager.Instance.PlayWeaponFire(_fireAudio, _weapon!, muzzlePosition);
		}
		if (_flashMesh == null && _flashLight == null) return;
		SetFlashVisible(true);
		_flashRemaining = MuzzleFlashSeconds;
		SetProcess(true);
	}
```

**`SpawnOneShotAt`** - one line, so impact/flesh-impact one-shots also live on the Weapons bus:

```csharp
	private void SpawnOneShotAt(AudioStream? stream, Vector3 worldPosition, float unitSize)
	{
		if (stream == null) return;
		var player = new AudioStreamPlayer3D
		{
			Stream = stream,
			UnitSize = unitSize,
			PitchScale = _rng.RandfRange(0.9f, 1.1f),
			Bus = "Weapons", // NEW
		};
		Node host = GetTree().CurrentScene ?? (Node)this;
		host.AddChild(player);
		player.GlobalPosition = worldPosition;
		player.Finished += player.QueueFree;
		player.Play();
	}
```

**Bazooka explosion**, while you're in there - point its `ImpactSounds` at what you already own:

```
ImpactSounds = ["res://audio/Clips/Weapons/Grenade Launcher/Explode/Grenade Explode-001.wav",
                "res://audio/Clips/Weapons/Grenade Launcher/Explode/Grenade Explode-002.wav",
                "res://audio/Clips/Weapons/Grenade Launcher/Explode/Grenade Explode-003.wav"]
```

### `scripts/player/PlayerAudio.cs`

Two one-word additions - route footsteps and hit/death/respawn sounds onto the buses that matter:

```csharp
		_footstepPlayer = new AudioStreamPlayer3D { Name = "Footsteps", UnitSize = 7.0f, VolumeDb = -3.0f, Bus = "Movement" };
		AddChild(_footstepPlayer);
		_voicePlayer = new AudioStreamPlayer3D { Name = "Voice", UnitSize = 14.0f, Bus = "Voice" };
		AddChild(_voicePlayer);
```

This is also the hook for jump/land sound if you add one later - there's no dedicated clip for
those states yet (`PlayerAudio.cs`'s own comment says Jump/Fall are currently silent). Whenever
you wire one in, give that `AudioStreamPlayer3D` `Bus = "Movement"` too and it's automatically
covered by the same ducking.

### `project.godot`

One line under `[autoload]`, same pattern as the three that are already there:

```
SoundManager="*res://scripts/autoloads/SoundManager.cs"
```

### `default_bus_layout.tres`

Drop the file at your project root (next to `project.godot`) - Godot looks for exactly that path
by default, no `project.godot` change needed. If it doesn't pick it up automatically for any
reason, Audio panel -> the small menu next to "Add Bus" -> Load, and point it at the file.

### `scenes/Player.tscn`

I'd rather you do this one in the editor than have me hand-edit the `.tscn` text and risk an
`ext_resource` id collision I can't verify here. Select the Player root node, add a new child
`Node`, rename it `WeaponSoundTuner`, and attach `scripts/debug/WeaponSoundTuner.cs` as its
script - exactly how `WeaponGripTuner` and `AnimationTuner` are already attached as siblings under
the same root.

---

## 5. Using the tuner

- **F6** - open/close the panel. Opening it releases the mouse cursor and pauses gameplay input
  (same trade-off `WeaponGripTuner`'s F7 panel already makes) so you can drag sliders; use the
  **Test Fire** button in the panel to hear each change instead of needing to actually shoot.
- **F1** - print the current weapon's block to the console and copy it to your clipboard, ready to
  paste over its `FireAudioUnitSize`-onward lines in `WeaponRegistry.tres`.
- **F9** - discard live changes, back to whatever was in `WeaponRegistry.tres` when the level
  loaded.
- Switching weapons while the panel is open re-syncs the sliders to whichever gun you're now
  holding.

**Duo / LAN testing:** while the panel is open, every change is sent to the other connected
players (throttled the same way `WeaponGripTuner`'s grip sync is - at most every 0.05 s, at least
a keep-alive every 1 s) and applied to their own copy of that weapon. This matters because a
`WeaponData` resource loaded in one game process isn't visible to another process at all - without
this, you and a friend tuning together over LAN would each only ever hear your own half-tuned
version of the gun. The panel shows how many other players it's currently syncing to.

---

## 6. Sound effects to source

### Fixes that need zero downloads (wiring only)

- Bazooka explosion -> point at the `Grenade Launcher/Explode` files you already have (section 4).
- Shotgun reload -> either rename `Shotgun_reload.wav` to `shotgun_reload_shell.wav`, or fix the
  path in `WeaponRegistry.tres` to match the file that actually exists. Either takes 30 seconds.

### Real gaps (need new audio)

1. **SMG fire sound, for both P90 and Kriss Vector.** This is the single biggest missing piece -
   right now both are borrowing a rifle sound, and a submachine gun should read as higher-pitched
   and shorter-bodied than a rifle, not identical to one. Ideally two *different* sounds (a P90's
   5.7x28mm bullpup report is genuinely distinct from a Kriss Vector's 9mm/.45 blowback action),
   but even one dedicated SMG sound shared between them is a big step up from what's there now.
   Search: "P90 gunshot sound effect", "FN P90 fire real recording", "submachine gun fire sound
   effect", "Kriss Vector gunshot".
2. **A distinct Desert Eagle report** - something audibly heavier/deeper than the Glock's
   `pistol_shoot.wav`. Search: "Desert Eagle gunshot sound effect", ".50 AE pistol fire recording",
   "large caliber handgun gunshot real".
3. **Two more rifle fire variants** to actually fill the `Rifle_Shoot-002/003.wav` slots
   `WeaponRegistry.tres` already expects for the M4 - or just delete those two lines and let it
   round-robin between the one rifle clip and `AK_fire.wav` if you'd rather not source more right
   now. Real automatic weapons don't produce identical repeats round to round, so a few variants
   in rotation (which `AddAudioPlayers`/`RollFireVariant` already supports) genuinely helps.
   Search: "M4 carbine gunshot sound effect", "AR-15 fire real recording".
4. **A generic AR-pattern reload** (`rifle_reload.wav`) - an AR-15/M4 reloads with a different
   mechanical character than an AK (bolt-catch release vs. the AK's rock-and-lock mag), so reusing
   the AK's reload as a stopgap will read as slightly wrong up close, though it's a fine zero-cost
   placeholder. Search: "M4 reload sound effect", "AR-15 magazine reload real recording".

### Nice-to-have

5. Distinct dry-fire clicks per weapon family (pistol vs. rifle vs. SMG) instead of the one shared
   `reload_click_rifle.wav` everything currently uses.
6. A landing thud and a jump sound for `PlayerAudio.cs` - see the note in section 4. Once it
   exists, it's automatically covered by the same footstep-ducking bus.

### Where to actually get real (not stock/synthetic) recordings

- **[Freesound.org](https://freesound.org)** - free, huge, and mostly Creative Commons (a large
  share CC0/no-attribution-needed, the rest CC-BY or more restrictive - check the license on each
  individual upload before shipping). Quality varies a lot since it's crowd-sourced, so audition
  several candidates per weapon rather than taking the first hit.
- **[Sonniss GDC Game Audio Bundle](https://sonniss.com/gameaudiogdc)** - a free annual bundle
  (a new edition ships around every GDC, currently up to date), royalty-free with no attribution
  required for any project, and it's specifically professional studios' real recorded content
  including weapon foley. Large download, worth it - this is probably the best free source for
  genuinely realistic firearm audio.
- **[Zapsplat](https://www.zapsplat.com)** - free with a registered account (attribution required
  on the free tier; paid removes that), has a real firearms section, mixed realism quality.
- **If budget allows and you want the most authentic samples available**: paid professional
  libraries like **Boom Library**'s weapons packs, **Pro Sound Effects**, or **Soundly** are
  actually range-recorded from real firearms (military/armorer-grade rigs, multiple mic
  positions/distances per shot) rather than synthesized or layered from movie foley - the
  "cinematic" branding some of these use refers to production polish, not fakery.

Whatever you pick, check the specific license on the individual file, not just the site's general
policy - that's true even on Freesound, where licensing is set per-upload rather than site-wide.
