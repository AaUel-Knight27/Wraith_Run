using System;

/// <summary>
/// The style bonuses a single confirmed kill can be evaluated against. Flags, because the v2.0
/// design (Part 6, FR-SC-02) requires every applicable bonus to stack additively on the same kill
/// with no cap.
/// </summary>
[Flags]
public enum KillStyle
{
    None = 0,
    Headshot = 1 << 0,
    HipFire = 1 << 1,
    NoScope360 = 1 << 2,
    VergeOfDeath = 1 << 3,

    /// <summary>Not a scoring bonus (KillStyleBonus.Evaluate never checks it) - carried through
    /// styleFlags purely so Health.ReceiveDamage can tell a blade kill apart from a bullet kill when
    /// it decides which death-audio clip the victim should play. See the note on
    /// Health.DetermineIfDeathStruggles.</summary>
    Melee = 1 << 4,
}
