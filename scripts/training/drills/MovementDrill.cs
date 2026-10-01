using Godot;
using System.Collections.Generic;

/// <summary>A timed obstacle course that only lets you through by using each movement skill: sprint, crouch under a
/// bar, slide, jump a wall, then creep through a quiet zone without sprinting. Mistakes cost points, not the run.</summary>
public partial class MovementDrill : TrainingDrill
{
    private enum GateKind { Sprint, Crouch, Slide, Jump, Quiet }

    private sealed class Gate
    {
        public string Name = "";
        public GateKind Kind;
        public float Z;
        public float Half = 3.0f;
        public bool Inside;
        public bool Satisfied;
        public bool Done;
        public bool Failed;
    }

    private const float FinishZ = -122.0f;
    private const float TimeLimit = 120.0f;
    private const float ParSeconds = 55.0f;

    private Transform3D _anchor;
    private readonly List<Gate> _gates = new();
    private int _mistakes;
    private int _gatesCleared;

    public override string Objective => "Run the course in order: SPRINT the marked lane, CROUCH under the bar, SLIDE under the next, JUMP the wall, then WALK (no sprinting) through the quiet zone to the finish.";
    public override string StatusLine => $"Gates {_gatesCleared}/{_gates.Count}   mistakes {_mistakes}";

    protected override void OnBegin()
    {
        _anchor = Session.Anchor(TrainingAnchors.MoveCourse);
        Session.PlacePlayer(_anchor, new Vector3(0, 0, 4.0f));

        AddGate("SPRINT", GateKind.Sprint, -18.0f, 3.0f, new Color(0.4f, 0.8f, 1.0f));
        AddGate("CROUCH", GateKind.Crouch, -36.0f, 2.5f, new Color(1.0f, 0.8f, 0.4f));
        AddGate("SLIDE", GateKind.Slide, -54.0f, 3.5f, new Color(1.0f, 0.6f, 0.4f));
        AddGate("JUMP", GateKind.Jump, -72.0f, 2.5f, new Color(0.6f, 1.0f, 0.6f));
        AddGate("QUIET ZONE - WALK", GateKind.Quiet, -96.0f, 15.0f, new Color(0.8f, 0.6f, 1.0f));

        // Side walls keep you on the lane; the low bars and the wall are what force each move.
        AddBox(new Vector3(0.6f, 3.0f, 134.0f), new Vector3(-12.3f, 1.5f, -62.0f), new Color(0.25f, 0.27f, 0.3f));
        AddBox(new Vector3(0.6f, 3.0f, 134.0f), new Vector3(12.3f, 1.5f, -62.0f), new Color(0.25f, 0.27f, 0.3f));
        AddBox(new Vector3(24.0f, 0.5f, 0.6f), new Vector3(0, 1.35f, -36.0f), new Color(0.9f, 0.7f, 0.2f));   // crouch bar (underside 1.1 m)
        AddBox(new Vector3(24.0f, 0.5f, 0.6f), new Vector3(0, 1.35f, -54.0f), new Color(0.9f, 0.45f, 0.2f));  // slide bar
        AddBox(new Vector3(24.0f, 0.9f, 0.6f), new Vector3(0, 0.45f, -72.0f), new Color(0.3f, 0.8f, 0.3f));   // jump wall (0.9 m)

        Session.Props.AddChild(WorldFlat(TrainingUtil.GroundDisc(6.0f, new Color(0.3f, 1.0f, 0.4f), Vector3.Zero, 0.4f), new Vector3(0, 0.05f, FinishZ)));
        Session.Props.AddChild(WorldFlat(TrainingUtil.Label("FINISH", Vector3.Zero, new Color(0.5f, 1.0f, 0.6f), 96, false), new Vector3(0, 4.0f, FinishZ)));
    }

    private void AddGate(string name, GateKind kind, float z, float half, Color color)
    {
        _gates.Add(new Gate { Name = name, Kind = kind, Z = z, Half = half, Satisfied = kind == GateKind.Quiet });
        Session.Props.AddChild(WorldFlat(TrainingUtil.Label(name, Vector3.Zero, color, 72, false), new Vector3(0, 3.6f, z)));
        Session.Props.AddChild(WorldFlat(TrainingUtil.GroundDisc(kind == GateKind.Quiet ? 10.0f : 4.0f, color, Vector3.Zero, 0.28f), new Vector3(0, 0.04f, z)));
    }

    private void AddBox(Vector3 size, Vector3 localPosition, Color color)
    {
        StaticBody3D body = TrainingUtil.SolidBox(size, color, Vector3.Zero, "CourseBlock");
        Session.Props.AddChild(body);
        body.GlobalPosition = _anchor * localPosition;
        body.GlobalRotation = new Vector3(0, _anchor.Basis.GetEuler().Y, 0);
    }

    /// <summary>Wraps a node in a positioned holder, so local anchor coordinates work for anything.</summary>
    private Node3D WorldFlat(Node3D child, Vector3 localPosition)
    {
        var holder = new Node3D();
        holder.AddChild(child);
        holder.Transform = new Transform3D(_anchor.Basis, _anchor * localPosition);
        return holder;
    }

    protected override void OnTick(float dt)
    {
        Vector3 local = _anchor.AffineInverse() * Session.Player.GlobalPosition;
        PlayerMovement.MovementState state = Session.Movement.State;

        foreach (Gate gate in _gates)
        {
            if (gate.Done) continue;
            bool inRange = Mathf.Abs(local.Z - gate.Z) <= gate.Half;
            if (inRange)
            {
                gate.Inside = true;
                Evaluate(gate, state);
            }
            else if (gate.Inside && local.Z < gate.Z - gate.Half)
            {
                CloseGate(gate);
            }
        }

        if (local.Z <= FinishZ + 3.0f) Finish(true, "Course complete.");
        else if (Elapsed >= TimeLimit) Finish(false, "Out of time.");
    }

    private void Evaluate(Gate gate, PlayerMovement.MovementState state)
    {
        switch (gate.Kind)
        {
            case GateKind.Sprint:
                if (state == PlayerMovement.MovementState.Sprint) gate.Satisfied = true;
                break;
            case GateKind.Crouch:
                if (state == PlayerMovement.MovementState.Crouch || state == PlayerMovement.MovementState.CrouchWalk || state == PlayerMovement.MovementState.Slide) gate.Satisfied = true;
                break;
            case GateKind.Slide:
                if (state == PlayerMovement.MovementState.Slide) gate.Satisfied = true;
                break;
            case GateKind.Jump:
                if (state == PlayerMovement.MovementState.Jump || state == PlayerMovement.MovementState.Fall) gate.Satisfied = true;
                break;
            case GateKind.Quiet:
                if (state == PlayerMovement.MovementState.Sprint && !gate.Failed)
                {
                    gate.Failed = true;
                    gate.Satisfied = false;
                    Session.Toast("Too loud - walk through the quiet zone", new Color(1.0f, 0.6f, 0.4f), 2.0f);
                }
                break;
        }
    }

    private void CloseGate(Gate gate)
    {
        gate.Done = true;
        if (gate.Satisfied)
        {
            _gatesCleared++;
            return;
        }
        _mistakes++;
        string need = gate.Kind switch
        {
            GateKind.Sprint => "you must be sprinting through the SPRINT lane",
            GateKind.Crouch => "get low - crouch under the bar",
            GateKind.Slide => "sprint, then crouch to SLIDE under the bar",
            GateKind.Jump => "jump the wall",
            _ => "walk, don't sprint",
        };
        Session.Toast($"{gate.Name}: {need}", new Color(1.0f, 0.65f, 0.4f), 2.4f);
    }

    protected override float ComputeRating()
    {
        float time = TimeScore(Elapsed, ParSeconds);
        float clean = Mathf.Clamp(100.0f - _mistakes * 25.0f, 0.0f, 100.0f);
        return time * 0.6f + clean * 0.4f;
    }

    protected override void BuildResults()
    {
        Row("Time", TrainingUtil.Clock(Elapsed));
        Row("Par", TrainingUtil.Clock(ParSeconds));
        Row("Gates cleared cleanly", $"{_gatesCleared} / {_gates.Count}");
        Row("Mistakes", _mistakes.ToString());
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        if (_mistakes > 0) tips.Add("A missed gate names the skill you skipped. Each one is worth practising on its own before you chase time.");
        if (Elapsed > ParSeconds * 1.3f) tips.Add("Sprint between gates and only slow down inside them - the course is won on the straights.");
        if (tips.Count == 0) tips.Add("Clean and fast. Try again for an S, or move on to the aim drills.");
        return tips.ToArray();
    }
}
