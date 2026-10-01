# Audio asset repair notes

The project contains wired menu, weapon-fire, reload, movement, hit, death, surface-impact,
ricochet, fly-by, landing, and respawn recordings. The supplied Glock, knife, flesh-hit, hit-marker,
landing, respawn and whoosh files are integrated. The Ogg/Opus clips were converted to WAV because
Godot 4.7 cannot import Opus data in an Ogg container.

Current shared or generic clips:

- Generic weapon impact uses BulletWallHit.
- AK/M4/Deagle metal impacts share BulletMetalHeavy; other firearms use BulletMetalThick.
- PlayerHit remains the non-lethal player hurt voice; flesh impacts have their own clip.
- PlayerMedicNeeded.wav is kept in the project for later medic-system work and currently has no game call site.

The master development plan also describes future audio systems (combat music, vehicle engine pitch,
environment-specific footsteps, collapse/cursed-event sounds, and kill-feed/low-health HUD cues).
Those are not part of the current sound-slot implementation and have no matching supplied assets in
this batch, so they remain pending. The current sound-slot list at `audio/SOUND_SLOTS.md` documents
the implemented inventory.
