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
}
