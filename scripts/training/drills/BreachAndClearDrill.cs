using Godot;
using System.Collections.Generic;

/// <summary>
/// DEMOLISHER comms: a breach with a team. Two teammates stack against the wall - INSIDE the blast radius - and three
/// enemies wait in the room behind it. Plant the charge, and before it blows call FIRE IN THE HOLE so your team clears the
/// zone. When the wall is down, call MOVE IN and they push through with you. Blow it without the call and your friends
/// eat the blast. Callouts here change what your teammates actually do.
/// </summary>
public partial class BreachAndClearDrill : TrainingDrill
{
    private const float TimeLimit = 130.0f;
    private const float ParSeconds = 60.0f;

    private Transform3D _anchor;
    private ExplosiveSystem _explosives = null!;
    private BreachWall _wall = null!;
    private readonly List<TrainingActor> _enemies = new();
    private readonly List<TrainingActor> _friends = new();
    private bool _planted;
    private bool _breached;
    private bool _moveInCalled;
    private float _lead;
    private bool _calledBeforeBlast;
    private float _blastDamageToFriends;
    private int _friendsLost;
    private int _enemiesDown;
    private float _breachedAt;
    private float _breachedClock = float.MaxValue;

    public override CalloutType? ContextCallout => !_planted ? CalloutType.HoldPosition : !_breached ? CalloutType.FireInTheHole : CalloutType.MoveIn;

    public override string Objective
    {
        get
        {
            string plant = TrainingInput.KeyText(TrainingInput.AbilityPrimary);
            string quick = TrainingInput.KeyText(TrainingInput.CalloutQuick);
            if (!_planted) return $"Your two teammates are stacked at the wall, inside the blast radius. Plant a charge on the breach wall (hold [{plant}]). Then warn them: FIRE IN THE HOLE [{quick}].";
            if (!_breached) return $"Charge armed! Call FIRE IN THE HOLE [{quick}] NOW so your team clears the blast zone - then get clear yourself.";
            return $"The wall is down. Call MOVE IN [{quick}] so your team pushes with you, and clear the room. ({_enemies.Count - _enemiesDown} enemies left)";
        }
    }

    public override string StatusLine => $"Enemies {_enemiesDown}/{_enemies.Count}   friends lost {_friendsLost}   blast damage to friends {_blastDamageToFriends:0}";

    protected override void OnBegin()
    {
        _anchor = Session.Anchor(TrainingAnchors.BreachSite);
        Session.PlacePlayer(_anchor, new Vector3(0, 0, 2.0f));

        BuildRoom();
        BuildTeam();

        _explosives = Session.AddMechanic<ExplosiveSystem>();
        _explosives.Planted += charge => _planted = true;
        _explosives.Detonated += OnDetonated;
    }

    private void BuildRoom()
    {
        Color wallColor = new Color(0.4f, 0.4f, 0.44f);
        AddSolid(new Vector3(16.0f, 3.2f, 0.6f), new Vector3(-10.5f, 1.6f, -16.0f), wallColor);
        AddSolid(new Vector3(16.0f, 3.2f, 0.6f), new Vector3(10.5f, 1.6f, -16.0f), wallColor);
        AddSolid(new Vector3(37.0f, 3.2f, 0.6f), new Vector3(0, 1.6f, -34.0f), wallColor);
        AddSolid(new Vector3(0.6f, 3.2f, 18.0f), new Vector3(-18.5f, 1.6f, -25.0f), wallColor);
        AddSolid(new Vector3(0.6f, 3.2f, 18.0f), new Vector3(18.5f, 1.6f, -25.0f), wallColor);

        _wall = new BreachWall { Size = new Vector3(5.0f, 3.2f, 0.6f) };
        _wall.Breached += w =>
        {
            _breached = true;
            _breachedAt = Elapsed;
            _breachedClock = Session.Callouts.Clock;
        };
        DrillHelpers.Place(Session, _anchor, _wall, new Vector3(0, 0, -16.0f));
    }

    private void AddSolid(Vector3 size, Vector3 local, Color color)
    {
        StaticBody3D body = TrainingUtil.SolidBox(size, color, Vector3.Zero, "RoomWall");
        Session.Props.AddChild(body);
        body.GlobalTransform = new Transform3D(_anchor.Basis, _anchor * local);
    }

    private void BuildTeam()
    {
        foreach (float x in new[] { -2.8f, 2.8f })
        {
            TrainingActor friend = Session.SpawnActor(TrainingActor.FactionType.Friend, _anchor * new Vector3(x, 0, -12.5f), x < 0 ? "Breacher" : "Cover", a =>
            {
                a.ReactsToCallouts = true;
                a.CanShoot = false;
                a.Accuracy = 0.6f;
                a.ShotDamage = 9.0f;
                a.FireInterval = 0.5f;
                a.MoveSpeed = 5.0f;
                a.ShootRange = 60.0f;
            });
            friend.Died += (a, s) => _friendsLost++;
            friend.Damaged += (a, amount) => { if (!_breached) _blastDamageToFriends += amount; };
            _friends.Add(friend);
        }

        foreach (Vector3 local in new[] { new Vector3(-7.0f, 0, -24.0f), new Vector3(0, 0, -29.0f), new Vector3(7.0f, 0, -24.0f) })
        {
            TrainingActor enemy = Session.SpawnActor(TrainingActor.FactionType.Enemy, _anchor * local, "Hostile", a =>
            {
                a.MaxHp = 60.0f;
                a.CanShoot = true;
                a.Accuracy = 0.45f;
                a.ShotDamage = 6.0f;
                a.FireInterval = 0.8f;
                a.TellSeconds = 0.6f;
                a.ShootRange = 60.0f;
            });
            enemy.Died += OnEnemyDied;
            _enemies.Add(enemy);
        }
    }

    private void OnDetonated(PlacedCharge charge, bool traineeHit)
    {
        _calledBeforeBlast = charge.CalledOut;
        _lead = charge.CalledOutLead;
        _ = traineeHit;
    }

    private void OnEnemyDied(TrainingActor actor, ShotInfo? shot)
    {
        _enemiesDown++;
        if (_enemiesDown >= _enemies.Count) Finish(_friendsLost < _friends.Count, _friendsLost == 0 ? "Room cleared, team intact." : "Room cleared, but you lost people.");
    }

    protected override void OnTick(float dt)
    {
        if (!_moveInCalled && _breached && Session.Callouts.Latest(CalloutType.MoveIn, 999.0f) is CalloutMessage moveIn && moveIn.FromPlayer && moveIn.Time >= _breachedClock) _moveInCalled = true;

        if (_friendsLost >= _friends.Count) Finish(false, "Your whole team is down.");
        else if (Elapsed >= TimeLimit) Finish(false, "Out of time.");
    }

    protected override float ComputeRating()
    {
        float clear = (float)_enemiesDown / Mathf.Max(1, _enemies.Count) * 35.0f;
        float alive = Mathf.Clamp(25.0f - _friendsLost * 12.5f, 0.0f, 25.0f);
        float comms = 0.0f;
        if (_calledBeforeBlast) comms += _lead >= 1.5f ? 15.0f : 8.0f;
        if (_moveInCalled) comms += 10.0f;
        float blast = 15.0f * (1.0f - Mathf.Min(1.0f, _blastDamageToFriends / 60.0f));
        return clear + alive + comms + blast;
    }

    protected override void BuildResults()
    {
        Row("Enemies down", $"{_enemiesDown} / {_enemies.Count}");
        Row("Teammates lost", _friendsLost.ToString());
        Row("FIRE IN THE HOLE before the blast", _calledBeforeBlast ? $"yes ({_lead:0.0} s warning)" : "NO");
        Row("MOVE IN called", _moveInCalled ? "yes" : "no");
        Row("Blast damage to your team", $"{_blastDamageToFriends:0}");
        Row("Time", TrainingUtil.Clock(Elapsed));
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        if (!_calledBeforeBlast) tips.Add("You blew the wall without warning. Teammates only clear the blast zone when you call FIRE IN THE HOLE - always call it, every time.");
        else if (_lead < 1.5f) tips.Add("You called it, but late - they need about 1.5 seconds to clear. Call as soon as the charge is armed.");
        if (!_moveInCalled) tips.Add("After the breach your team waits for your order. Call MOVE IN and the fight is three on three instead of one on three.");
        if (_blastDamageToFriends > 20.0f) tips.Add("Your friends were hurt by your blast. That is what the warning callout is for.");
        if (tips.Count == 0) tips.Add("Textbook breach: plant, warn, blow, order the push.");
        return tips.ToArray();
    }
}
