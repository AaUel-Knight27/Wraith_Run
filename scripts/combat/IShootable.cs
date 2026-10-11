using Godot;

/// <summary>
/// Everything a weapon hit needs to tell a target about the shot. Filled in by WeaponSwitcher (the
/// shooter's device) and handed straight to the target - no RPC in between.
/// </summary>
public struct ShotInfo
{
    public float Damage;
    public bool IsHeadshot;
    /// <summary>Kill-style flags captured at fire time (headshot / hip-fire / 360 / verge-of-death / melee).</summary>
    public KillStyle Styles;
    public Vector3 HitPoint;
    public Vector3 Direction;
    public float Distance;
    public long AttackerPeerId;
    public string WeaponName;
    public string WeaponClass;

    public bool IsMelee => WeaponClass == "Melee";
    /// <summary>Launcher-class weapon - the heavy vehicle-killer.</summary>
    public bool IsHeavy => WeaponClass == "Launcher";
}

/// <summary>
/// A physics body that can be shot WITHOUT going through Health.ReceiveDamage's RPC.
///
/// Why this exists: WeaponSwitcher's normal path is targetHealth.RpcId(ownerPeer, ReceiveDamage).
/// That RPC is not CallLocal, so when the target lives on the same peer as the shooter (a training
/// dummy, a bot, a vehicle - anything offline) Godot drops the call and nothing takes damage.
/// WeaponSwitcher checks for this interface first and calls it directly; every other body keeps
/// using the untouched RPC path.
/// </summary>
public interface IShootable
{
    /// <summary>True when worldPoint is inside this target's head zone.</summary>
    bool IsHeadHit(Vector3 worldPoint);

    /// <summary>True for flesh (plays the body-hit sound); false for metal/vehicles (plays the surface impact).</summary>
    bool IsFleshTarget { get; }

    void ReceiveShot(ShotInfo shot);
}

/// <summary>Anything an explosion can hurt.</summary>
public interface IExplosionReceiver
{
    Vector3 BlastPosition { get; }
    /// <summary>damage is already scaled for distance falloff by the caller.</summary>
    void ReceiveBlast(float damage, Vector3 origin, long sourcePeerId);
}
