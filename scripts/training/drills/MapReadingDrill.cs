using Godot;
using System.Collections.Generic;

/// <summary>
/// Map reading. The arena is split into named, colour-coded zones (your TrainingZone nodes, or a generated 3x3 grid).
/// The world shows only colours and a tall pillar per zone; the [M] map shows the names. Tasks alternate between
/// NAVIGATE (reach a named zone) and LOCATE (a hostile is reported in a zone - go and find him).
/// </summary>
public partial class MapReadingDrill : TrainingDrill
{
    private const int TaskCount = 6;

    private static readonly string[] GridNames = { "ALPHA", "BRAVO", "CHARLIE", "DELTA", "ECHO", "FOXTROT", "GOLF", "HOTEL", "INDIA" };
    private static readonly string[] GridColorNames = { "red", "orange", "yellow", "green", "teal", "blue", "purple", "pink", "white" };
    private static readonly Color[] GridColors =
    {
        new Color(0.95f, 0.3f, 0.3f), new Color(1.0f, 0.6f, 0.2f), new Color(1.0f, 0.9f, 0.3f),
        new Color(0.4f, 0.9f, 0.4f), new Color(0.3f, 0.85f, 0.8f), new Color(0.35f, 0.55f, 1.0f),
        new Color(0.65f, 0.4f, 0.95f), new Color(1.0f, 0.5f, 0.75f), new Color(0.92f, 0.92f, 0.92f),
    };

    private sealed class MapTask
    {
        public bool Locate;
        public TrainingZone Zone = null!;
        public float Par;
        public float Limit;
        public float Time;
        public bool Done;
    }

    private Transform3D _anchor;
    private readonly RandomNumberGenerator _rng = new();
    private readonly List<TrainingZone> _zones = new();
    private readonly List<MapTask> _tasks = new();
    private readonly Dictionary<TrainingZone, string> _colorNames = new();
    private int _index = -1;
    private float _taskTimer;
    private float _inZoneFor;
    private bool _taskActive;
    private float _pause = 0.6f;
    private TrainingActor? _hostile;
    private MapMarker? _hint;

    private MapTask? Current => _index >= 0 && _index < _tasks.Count ? _tasks[_index] : null;
    private int Completed
    {
        get
        {
            int n = 0;
            foreach (MapTask task in _tasks) if (task.Done) n++;
            return n;
        }
    }

    public override string Objective
    {
        get
        {
            MapTask? task = Current;
            string key = TrainingInput.KeyText(TrainingInput.MapToggle);
            if (task == null) return $"Open the map with [{key}]. Zone names are on the map; the world only shows colours.";
            string color = _colorNames.TryGetValue(task.Zone, out string? name) ? $" ({name})" : "";
            return task.Locate
                ? $"Task {_index + 1}/{TaskCount}: LOCATE. Intel: a hostile is hiding in {task.Zone.ZoneName}{color}. Find and eliminate him.\nMap: [{key}]"
                : $"Task {_index + 1}/{TaskCount}: NAVIGATE to {task.Zone.ZoneName}{color} and stand in it.\nMap: [{key}]";
        }
    }

    public override string StatusLine => $"Done {Completed}/{TaskCount}   task time {(_taskActive ? _taskTimer : 0.0f):0}s";

    protected override void OnBegin()
    {
        _rng.Randomize();
        _anchor = Session.Anchor(TrainingAnchors.MapZones);

        List<TrainingZone> existing = TrainingAnchors.GetZones(Session.GetTree());
        if (existing.Count >= 3) _zones.AddRange(existing);
        else BuildGridZones();

        Session.PlacePlayer(_anchor, new Vector3(0, 0, 80.0f));
        Session.Toast($"Press [{TrainingInput.KeyText(TrainingInput.MapToggle)}] for the map", new Color(0.8f, 0.9f, 1.0f), 3.0f);
        BuildTasks();
    }

    private void BuildGridZones()
    {
        for (int i = 0; i < 9; i++)
        {
            int col = i % 3;
            int row = i / 3;
            Vector3 local = new Vector3((col - 1) * 40.0f, 0, (row - 1) * 40.0f);
            var zone = new TrainingZone { Name = "Zone_" + GridNames[i], ZoneName = GridNames[i], HalfSize = new Vector2(18.0f, 18.0f), ZoneColor = GridColors[i] };
            Session.Props.AddChild(zone);
            zone.GlobalTransform = new Transform3D(_anchor.Basis, _anchor * local);
            _zones.Add(zone);
            _colorNames[zone] = GridColorNames[i];

            // Landmarks: colour on the ground and a tall pillar. No names - reading the map is the point.
            Session.Props.AddChild(Holder(TrainingUtil.GroundDisc(14.0f, GridColors[i], Vector3.Zero, 0.4f), local + new Vector3(0, 0.05f, 0)));
            StaticBody3D pillar = TrainingUtil.SolidBox(new Vector3(1.4f, 9.0f, 1.4f), GridColors[i], Vector3.Zero, "Landmark_" + GridNames[i]);
            Session.Props.AddChild(pillar);
            pillar.GlobalPosition = _anchor * (local + new Vector3(0, 4.5f, 0));
        }
    }

    private Node3D Holder(Node3D child, Vector3 local)
    {
        var holder = new Node3D();
        holder.AddChild(child);
        holder.Transform = new Transform3D(_anchor.Basis, _anchor * local);
        return holder;
    }

    private void BuildTasks()
    {
        TrainingZone? previous = null;
        for (int i = 0; i < TaskCount; i++)
        {
            TrainingZone zone;
            int guard = 0;
            do { zone = _zones[_rng.RandiRange(0, _zones.Count - 1)]; guard++; }
            while (zone == previous && guard < 20);
            previous = zone;
            bool locate = i % 2 == 1;
            _tasks.Add(new MapTask { Locate = locate, Zone = zone, Par = locate ? 45.0f : 30.0f, Limit = locate ? 80.0f : 55.0f });
        }
    }

    protected override void OnTick(float dt)
    {
        if (!_taskActive)
        {
            _pause -= dt;
            if (_pause > 0.0f) return;
            if (_index + 1 >= TaskCount)
            {
                int done = Completed;
                Finish(done >= 4, done >= 4 ? "You know your way around." : "You lost too much time - study the map before you move.");
                return;
            }
            StartTask(_index + 1);
            return;
        }

        _taskTimer += dt;
        MapTask task = _tasks[_index];

        if (!task.Locate)
        {
            if (task.Zone.Contains(Session.Player.GlobalPosition)) _inZoneFor += dt;
            else _inZoneFor = 0.0f;
            if (_inZoneFor >= 0.6f) CompleteTask(task, "Arrived");
        }
        else if (_hostile != null && !_hintShown && _taskTimer > 35.0f)
        {
            _hint = new MapMarker { Position = _hostile.GlobalPosition, Color = new Color(1.0f, 0.85f, 0.3f), Label = "contact?", Radius = 9.0f };
            Session.MapMarkers.Add(_hint);
            _hintShown = true;
            Session.Toast("Tip: a contact marker appeared on the map", new Color(1.0f, 0.85f, 0.4f));
        }

        if (_taskActive && _taskTimer >= task.Limit)
        {
            Session.Toast("Out of time on that task", new Color(1.0f, 0.65f, 0.4f));
            EndTask(task);
        }
    }

    private bool _hintShown;

    private void StartTask(int index)
    {
        _index = index;
        MapTask task = _tasks[index];
        _taskTimer = 0.0f;
        _inZoneFor = 0.0f;
        _taskActive = true;
        _hintShown = false;
        _hint = null;

        if (task.Locate)
        {
            Vector3 c = task.Zone.GlobalPosition;
            Vector3 spot = c + new Vector3(_rng.RandfRange(-11.0f, 11.0f), 0, _rng.RandfRange(-11.0f, 11.0f));
            _hostile = Session.SpawnActor(TrainingActor.FactionType.Enemy, spot, "Hostile", a =>
            {
                a.MaxHp = 60.0f;
                a.CanShoot = false;
                a.Mode = TrainingActor.BehaviorType.Idle;
            });
            TrainingActor captured = _hostile;
            captured.Died += (actor, shot) =>
            {
                if (_taskActive && _index < _tasks.Count && _tasks[_index] == task) CompleteTask(task, "Target down");
            };
            Session.Toast($"INTEL: hostile in {task.Zone.ZoneName}", new Color(1.0f, 0.8f, 0.4f), 3.0f);
        }
        else
        {
            Session.Toast($"NAVIGATE to {task.Zone.ZoneName}", new Color(0.7f, 0.9f, 1.0f), 3.0f);
        }
    }

    private void CompleteTask(MapTask task, string message)
    {
        if (!_taskActive) return;
        task.Done = true;
        task.Time = _taskTimer;
        Session.Toast($"{message} - {_taskTimer:0.0}s", new Color(0.6f, 1.0f, 0.6f));
        EndTask(task);
    }

    private void EndTask(MapTask task)
    {
        _taskActive = false;
        _pause = 1.8f;
        if (!task.Done) task.Time = task.Limit;
        if (_hint != null) Session.MapMarkers.Remove(_hint);
        _hint = null;
        if (_hostile != null && IsInstanceValid(_hostile) && !_hostile.IsDead) _hostile.QueueFree();
        _hostile = null;
    }

    protected override float ComputeRating()
    {
        float total = 0.0f;
        foreach (MapTask task in _tasks)
            if (task.Done) total += TimeScore(task.Time, task.Par) * 0.5f + 50.0f;
        return total / TaskCount;
    }

    protected override void BuildResults()
    {
        Row("Tasks completed", $"{Completed} / {TaskCount}");
        for (int i = 0; i < _tasks.Count; i++)
        {
            MapTask task = _tasks[i];
            string label = $"{i + 1}. {(task.Locate ? "Locate" : "Navigate")} {task.Zone.ZoneName}";
            Row(label, task.Done ? $"{task.Time:0.0}s (par {task.Par:0})" : "failed");
        }
    }

    public override string[] CoachingTips() => new[]
    {
        "Before you move, find the target zone on the map and the pillar colour that marks it. Then pick your route from your own arrow on the map.",
        "For LOCATE, clear the zone's edges first - enemies are rarely dead centre.",
    };
}
