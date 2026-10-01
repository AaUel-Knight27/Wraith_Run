using Godot;
using System;
using System.Collections.Generic;

/// <summary>Shared blast math: distance falloff, damage to every receiver in range, and to the trainee.</summary>
public static class ExplosionUtil
{
    /// <summary>Returns true if the trainee was inside the radius and took damage.</summary>
    public static bool Blast(TrainingSession session, Vector3 origin, float radius, float maxDamage, long sourcePeerId)
    {
        foreach (Node node in session.GetTree().GetNodesInGroup("blast_receiver"))
        {
            if (node is not IExplosionReceiver receiver) continue;
            float d = receiver.BlastPosition.DistanceTo(origin);
            if (d > radius) continue;
            receiver.ReceiveBlast(maxDamage * (1.0f - d / radius), origin, sourcePeerId);
        }

        bool traineeHit = false;
        float playerDistance = (session.Player.GlobalPosition + new Vector3(0, 1.0f, 0)).DistanceTo(origin);
        if (playerDistance <= radius)
        {
            float damage = maxDamage * (1.0f - playerDistance / radius) * session.Kit.ExplosionDamageTakenMult;
            TrainingDamage.HitTrainee(session.PlayerHealth, damage);
            traineeHit = true;
        }

        SpawnFlash(session, origin, radius);
        return traineeHit;
    }

    private static void SpawnFlash(TrainingSession session, Vector3 origin, float radius)
    {
        var flash = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = radius * 0.55f, Height = radius * 1.1f },
            MaterialOverride = TrainingUtil.Mat(new Color(1.0f, 0.55f, 0.15f), true, 0.7f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        session.Props.AddChild(flash);
        flash.GlobalPosition = origin;
        session.GetTree().CreateTimer(0.35).Timeout += () => TrainingUtil.SafeFree(flash);

        const string sound = "res://audio/Clips/Weapons/RPG/Impact/rpg_explosion.wav";
        if (!ResourceLoader.Exists(sound)) return;
        var audio = new AudioStreamPlayer3D { Stream = ResourceLoader.Load<AudioStream>(sound), MaxDistance = 200.0f, UnitSize = 14.0f };
        session.Props.AddChild(audio);
        audio.GlobalPosition = origin;
        audio.Play();
        session.GetTree().CreateTimer(4.0).Timeout += () => TrainingUtil.SafeFree(audio);
    }
}

/// <summary>
/// DEMOLISHER - plant and detonate charges. Aim at a plantable surface (IPlantable: breach wall, vehicle) within
/// reach and hold [Q] for the arming time (5 s baseline). The charge then runs a short fuse (ASSUMED 4 s baseline;
/// with the manual-detonation perk it waits for [F] instead) and blasts everything inside the radius - friends,
/// enemies, walls, vehicles and YOU, with distance falloff.
///
/// REAL WIRING: real breachable objects implement IPlantable + IExplosionReceiver and join group "blast_receiver".
/// </summary>
public partial class ExplosiveSystem : TrainingMechanic
{
    public const float PlantReach = 3.6f;

    public int Charges { get; private set; }
    public readonly HoldChannel Channel = new();

    public event Action<PlacedCharge>? Planted;
    /// <summary>Args: charge, whether the trainee was inside the blast.</summary>
    public event Action<PlacedCharge, bool>? Detonated;
    public event Action<string>? PlantInterrupted;

    private bool _needRelease;
    private Node? _plantCollider;
    private Vector3 _plantPoint;
    private Vector3 _plantNormal;
    private Vector3 _plantStandPoint;

    public void SetCharges(int charges) => Charges = charges;

    public override void _Ready()
    {
        Charges = Session.Kit.ExplosiveCharges;
        Channel.Completed += OnPlanted;
        Channel.Interrupted += OnInterrupted;
        Session.Callouts.Issued += OnCallout;
    }

    public override void _ExitTree()
    {
        if (TrainingSession.Instance != null && IsInstanceValid(TrainingSession.Instance.Callouts))
            TrainingSession.Instance.Callouts.Issued -= OnCallout;
        base._ExitTree();
    }

    private void OnCallout(CalloutMessage message)
    {
        if (!message.FromPlayer || message.Type != CalloutType.FireInTheHole) return;
        foreach (Node node in GetTree().GetNodesInGroup("placed_charge"))
            if (node is PlacedCharge charge && !charge.IsSpent && !charge.CalledOut)
                charge.CalledOutClock = Session.Callouts.Clock;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        bool hold = Input.IsActionPressed(TrainingInput.AbilityPrimary);
        if (!hold) _needRelease = false;

        if (Channel.Active)
        {
            string? problem = Validate(hold);
            if (problem != null) Channel.Interrupt(problem);
            else Channel.Tick(dt);
        }
        else if (hold && !_needRelease && Session.DrillLive)
        {
            TryBegin();
        }

        TickCharges(dt);

        if (Channel.Active)
        {
            ShowChannel(Channel.Progress, Channel.Label);
            ShowPrompt(null);
            return;
        }
        HideChannel();

        string key = TrainingInput.KeyText(TrainingInput.AbilityPrimary);
        if (Charges <= 0) ShowPrompt("Out of charges");
        else if (Session.AimRay(PlantReach, out _, out Node? collider, out _) && collider is IPlantable plantable)
            ShowPrompt($"Hold [{key}] to plant a charge on {plantable.DisplayName}   ({Charges} left)");
        else ShowPrompt($"Aim at a plantable surface within {PlantReach:0.#} m   ({Charges} charges)");
    }

    private void TryBegin()
    {
        if (Charges <= 0)
        {
            Session.Toast("Out of charges", new Color(1.0f, 0.5f, 0.4f));
            _needRelease = true;
            return;
        }
        if (!Session.AimRay(PlantReach, out Vector3 point, out Node? collider, out Vector3 normal)) return;
        if (collider is not IPlantable) return;

        _plantCollider = collider;
        _plantPoint = point;
        _plantNormal = normal;
        _plantStandPoint = Session.Player.GlobalPosition;
        Channel.Begin(Session.Kit.ArmingSeconds, "ARMING CHARGE");
    }

    private string? Validate(bool hold)
    {
        if (!hold) return "released too early";
        if (_plantCollider == null || !IsInstanceValid(_plantCollider)) return "surface is gone";
        if (Session.Player.GlobalPosition.DistanceTo(_plantStandPoint) > 2.5f) return "you moved away";
        if (!Session.AimRay(PlantReach + 1.0f, out _, out Node? nowHit, out _) || nowHit != _plantCollider) return "lost aim on the surface";
        return null;
    }

    private void OnPlanted()
    {
        Charges--;
        _needRelease = true;

        var charge = new PlacedCharge
        {
            Radius = Session.Kit.BlastRadius,
            FuseRemaining = TrainingKit.BaseFuseSeconds,
            AutoFuse = !Session.Kit.ManualDetonation,
        };
        Session.Props.AddChild(charge);
        charge.GlobalPosition = _plantPoint + _plantNormal * 0.12f;
        Session.Toast(charge.AutoFuse ? "Charge armed - GET CLEAR" : "Charge armed - detonate with [F]", new Color(1.0f, 0.7f, 0.3f));
        Planted?.Invoke(charge);
    }

    private void OnInterrupted(string reason)
    {
        _needRelease = true;
        Session.Toast($"Arming interrupted: {reason}", new Color(1.0f, 0.55f, 0.4f));
        PlantInterrupted?.Invoke(reason);
    }

    private void TickCharges(float dt)
    {
        var due = new List<PlacedCharge>();
        bool manual = Session.Kit.ManualDetonation && Input.IsActionJustPressed(TrainingInput.AbilitySecondary);

        foreach (Node node in GetTree().GetNodesInGroup("placed_charge"))
        {
            if (node is not PlacedCharge charge || charge.IsSpent) continue;
            if (charge.AutoFuse)
            {
                charge.FuseRemaining -= dt;
                if (charge.FuseRemaining <= 0.0f) due.Add(charge);
            }
            else if (manual && charge.GlobalPosition.DistanceTo(Session.Player.GlobalPosition) <= TrainingKit.ManualDetonationRange)
            {
                due.Add(charge);
            }
        }

        foreach (PlacedCharge charge in due) Detonate(charge);
    }

    private void Detonate(PlacedCharge charge)
    {
        if (charge.IsSpent) return;
        charge.IsSpent = true;
        charge.CalledOutLead = charge.CalledOut ? Session.Callouts.Clock - charge.CalledOutClock : 0.0f;
        bool traineeHit = ExplosionUtil.Blast(Session, charge.GlobalPosition, charge.Radius, TrainingKit.BlastMaxDamage, Multiplayer.GetUniqueId());
        if (traineeHit) Session.Toast("Caught in your own blast!", new Color(1.0f, 0.4f, 0.3f));
        Detonated?.Invoke(charge, traineeHit);
        TrainingUtil.SafeFree(charge);
    }
}
