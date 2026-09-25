# Wraith Run — Touch / Android Controls patch

This is a small patch, not a full re-export of your project: it only contains the files that are
new or changed. Unzip it over your project root (overwrite when prompted) and these 10 paths will
be added/updated; everything else — art, audio, data, maps — is untouched.

## What's in it

**New**
- `scripts/autoloads/SettingsManager.cs` — autoload that persists settings to
  `user://settings.cfg` and is the single source of truth for look sensitivity and every
  Touch/Android control setting below.
- `scripts/player/VirtualJoystick.cs` — the on-screen movement stick.
- `scripts/player/TouchControls.cs` — builds the whole mobile overlay (joystick, Fire, ADS, Jump,
  Crouch, Sprint, Reload, weapon-switch, quick-melee) and reads the gyroscope when enabled.

**Changed**
- `scripts/player/PlayerMovement.cs` — touch-drag look (shares the same rotation code mouse-look
  already used), a gyro-look entry point, Auto Sprint, and mouse-capture is now skipped on mobile.
- `scripts/player/WeaponSwitcher.cs` — one-line visibility change (`private` → `internal`) so
  TouchControls can read the real loadout order instead of hardcoding weapon indices.
- `scripts/player/PlayerHud.cs` — on touch, health/ammo/kill-feed move from the bottom corners to
  the top, so TouchControls has the entire bottom of the screen to itself.
- `scripts/ui/MenuStyle.cs` — adds `ToggleRow` and `SliderRow`, two small reusable controls (an
  on/off row and a labelled slider) in the same style as the existing `SelectRow`/`Card`/`Panel`
  helpers. Nothing existing changed, just two new methods.
- `scripts/network/LanMenu.cs` — the gear icon on every main-menu screen used to open the generic
  "NOT BUILT YET" placeholder (the same one Train/Customize Gun/etc. use) with the text "Audio,
  video and control settings aren't built yet." It now opens a real **Settings** screen instead —
  see below. Audio and Video are still `InfoRow`s marked not-built (same lock-icon convention the
  menu already uses for single-value settings), since neither has a system behind it yet.
- `project.godot` — registers the new autoload, and turns off
  `input_devices/pointing/emulate_mouse_from_touch` so a touch doesn't also fire as a synthetic
  mouse click/motion (would have double-applied look and could misfire the mouse-recapture check).
- `scenes/Player.tscn` — adds a `TouchControls` node as a sibling of `Hud`, same pattern (local +
  authority-only, built entirely in code).

## The new Settings screen

Gear icon (Main / Play / Play Locally) → **SETTINGS**. Two columns, four panels:

- **Look & Aim** — Invert Look Y, Mouse Sensitivity, Touch Look Sensitivity
- **Gameplay** — Hold to Fire, Hold to ADS, Auto Sprint
- **Touch / Android Layout** — Left-Handed Layout, Floating vs Fixed Joystick, Joystick Deadzone,
  Button Scale, Button Opacity
- **Gyroscope Look** — Off / On / ADS Only, plus a sensitivity slider once it's not Off
- **Audio & Video** — still the "not built yet" lock rows, honestly, since there's no mixer or
  graphics-quality system to hook up yet

Toggles and select-rows save immediately and rebuild the screen (same pattern the existing
Host/Connect toggle on the Play Locally screen already used). Sliders update live and only write
to disk when you let go of the handle, so dragging one doesn't hammer the file system or rebuild
the screen out from under your cursor.

## How touch controls turn on

Automatic: `SettingsManager.ShouldShowTouchControls()` returns true on an actual mobile export
(`OS.HasFeature("mobile")`). Nothing to do for an Android build. There's no in-menu "force touch
mode on desktop" switch yet — if you want to preview the on-screen layout without a device, set
`SettingsManager.Instance.TouchControlsMode = SettingsManager.TouchMode.ForceOn` from the debugger
(or temporarily in code). You can click-drag with a mouse against the on-screen buttons, since
Godot's Control/GUI system accepts mouse and touch the same way — the one thing you can't emulate
that way is real touch-drag look, since we deliberately turned off touch↔mouse emulation (see
project.godot above) to keep the real thing clean on device.

## Every button drives the same Input actions your keyboard already does

`move_forward/back/left/right`, `jump`, `crouch`, `sprint`, `aim`, `fire`, `reload`, `weapon_1..8`
— nothing in `PlayerMovement` or `WeaponSwitcher` had to change to understand touch input; as far
as they're concerned it's just another device pressing the same actions. The one exception is
look: mouse motion bypasses the action system entirely in this codebase, so touch-drag look and
gyro look are wired straight into `PlayerMovement.ApplyLookDelta` / `ApplyGyroLookDelta` instead.

## What's still deliberately not here

- **Interact / Auto Pickup buttons** — there's no interact-with-world or item-pickup system in the
  codebase yet, so a button for either would just do nothing. Wire them into `TouchControls.Build`
  once those systems exist.
- **Audio and Video settings** — no mixer buses or graphics-quality system exists yet to hook a
  real screen up to; the Settings screen says so rather than pretending otherwise.
- **Exact pixel layout of the touch overlay** — sizes and margins are a first functional pass
  (grouped near the top of `TouchControls.cs`), tuned by reading other screens' sizes rather than
  by seeing it rendered. Worth an actual look on-device or in-editor before calling it final,
  especially the vertical stacking of the Jump/Crouch/Reload column.
