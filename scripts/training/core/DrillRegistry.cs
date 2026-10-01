using Godot;
using System.Collections.Generic;

/// <summary>
/// The catalogue of drills. THIS is the only file you touch to add one: write a TrainingDrill subclass in
/// scripts/training/drills/ and add a line here. The hub, grades, progress, recommendations and results screen all
/// pick it up from the definition.
/// </summary>
public static class DrillRegistry
{
    private static readonly List<DrillDefinition> _all = Build();

    public static IReadOnlyList<DrillDefinition> All => _all;

    public static DrillDefinition? Find(string id)
    {
        foreach (DrillDefinition definition in _all)
            if (definition.Id == id) return definition;
        return null;
    }

    private static List<DrillDefinition> Build()
    {
        var list = new List<DrillDefinition>();

        // ---- FUNDAMENTALS ------------------------------------------------------------------------
        list.Add(new DrillDefinition
        {
            Id = "move_course", Track = TrainingTrack.Fundamentals, Title = "Movement Course",
            Description = "A timed obstacle course that forces each movement skill in turn: sprint, crouch, slide, jump and a silent walk.",
            Goal = "Finish the course with as few missed gates as you can, near par.",
            Controls = "WASD move, Shift sprint, C crouch / slide (sprint, then crouch), Space jump.",
            Skills = new[] { TrainingSkill.Movement, TrainingSkill.Timing }, ParSeconds = 55.0f,
            Create = () => new MovementDrill(),
        });
        list.Add(new DrillDefinition
        {
            Id = "aim_flick", Track = TrainingTrack.Fundamentals, Title = "Flick Aim",
            Description = "Twenty targets pop up one at a time and vanish within two seconds. Build the habit of snapping straight to a target.",
            Goal = "Drop at least half of the targets, fast.",
            Controls = "Mouse aim, left click fire, right click aim down sights.",
            Skills = new[] { TrainingSkill.Aim, TrainingSkill.Timing }, ParSeconds = 40.0f,
            Create = () => new AimDrill(AimDrill.Mode.Flick),
        });
        list.Add(new DrillDefinition
        {
            Id = "aim_precision", Track = TrainingTrack.Fundamentals, Title = "Precision (Headshots)",
            Description = "Twelve distant targets that stay up a little longer. Body shots barely count here - the head is what pays.",
            Goal = "Land headshots on as many targets as possible.",
            Controls = "Aim down sights for distance; steady your breathing between shots.",
            Skills = new[] { TrainingSkill.Aim }, ParSeconds = 50.0f,
            Create = () => new AimDrill(AimDrill.Mode.Precision),
        });
        list.Add(new DrillDefinition
        {
            Id = "aim_tracking", Track = TrainingTrack.Fundamentals, Title = "Tracking",
            Description = "One tough target strafes left and right for thirty seconds. Keep your crosshair on it while you fire.",
            Goal = "Keep your accuracy above about 55%.",
            Controls = "Smooth mouse movement; fire in controlled bursts.",
            Skills = new[] { TrainingSkill.Aim }, ParSeconds = 30.0f,
            Create = () => new AimDrill(AimDrill.Mode.Tracking),
        });
        list.Add(new DrillDefinition
        {
            Id = "aim_recoil", Track = TrainingTrack.Fundamentals, Title = "Recoil Control",
            Description = "Spray the AK-47 at a wall and see where the bullets go. Pull against the climb and drift to keep a tight group on the centre mark.",
            Goal = "Keep most hits within 0.7 m of the centre.",
            Controls = "Pull the mouse down and against the sideways drift as you fire.",
            Skills = new[] { TrainingSkill.Recoil, TrainingSkill.Aim }, ParSeconds = 28.0f,
            Create = () => new AimDrill(AimDrill.Mode.Recoil),
        });
        list.Add(new DrillDefinition
        {
            Id = "enemy_reading", Track = TrainingTrack.Fundamentals, Title = "Read the Enemy",
            Description = "Six rounds, six behaviours: a strafer, a peeker with a tell, a crosser, a flanker, a rusher, and a mix. Learn to see the pattern before you shoot.",
            Goal = "Win at least four of the six rounds.",
            Controls = "Watch the first two rounds' hints, then read the rest yourself.",
            Skills = new[] { TrainingSkill.Awareness, TrainingSkill.Aim }, ParSeconds = 90.0f,
            Create = () => new EnemyReadingDrill(),
        });
        list.Add(new DrillDefinition
        {
            Id = "map_reading", Track = TrainingTrack.Fundamentals, Title = "Map Reading",
            Description = "Navigate to named zones and hunt a reported hostile using only the map. The world shows colours; the map shows names.",
            Goal = "Complete at least four of the six tasks.",
            Controls = "M toggles the map. Your arrow shows where you face; north is up.",
            Skills = new[] { TrainingSkill.Navigation, TrainingSkill.Awareness }, ParSeconds = 200.0f,
            Create = () => new MapReadingDrill(),
        });

        // ---- SHOOTER -----------------------------------------------------------------------------
        list.Add(new DrillDefinition
        {
            Id = "weapon_tour", Track = TrainingTrack.Shooter, Title = "Weapon Tour",
            Description = "Eight stages, one per weapon, each at the range it is built for. Feel what every weapon in your loadout does.",
            Goal = "Clear at least six of the eight stages.",
            Controls = "The drill equips each weapon for you. 1-8 also switch weapons in free roam.",
            Skills = new[] { TrainingSkill.Aim, TrainingSkill.Recoil }, ParSeconds = 150.0f,
            Create = () => new WeaponTourDrill(),
        });
        list.Add(new DrillDefinition
        {
            Id = "reload_timing", Track = TrainingTrack.Shooter, Title = "Reload Discipline",
            Description = "Waves of strafing enemies shoot back while you start each wave half empty. Reload in the lulls, never under fire, and never get caught dry.",
            Goal = "Kill almost everyone with no reload under fire and no dry fires.",
            Controls = "R reloads. Watch the lull between waves.",
            Skills = new[] { TrainingSkill.Timing, TrainingSkill.Awareness }, ParSeconds = 90.0f,
            Create = () => new ReloadTimingDrill(),
        });
        list.Add(new DrillDefinition
        {
            Id = "kill_styles", Track = TrainingTrack.Shooter, Title = "Kill Styles",
            Description = "Hit each bonus on demand: headshot, hip-fire, 360 no-scope, multi-kill and a kill on the verge of death.",
            Goal = "Land at least three of the five bonuses.",
            Controls = "Hip-fire = do not aim. For 360, spin the mouse a full turn and fire as you finish.",
            Skills = new[] { TrainingSkill.Aim, TrainingSkill.Timing }, ParSeconds = 120.0f,
            Create = () => new KillStyleDrill(),
        });
        list.Add(new DrillDefinition
        {
            Id = "vehicle_shooter", Track = TrainingTrack.Shooter, Title = "Shoot From a Vehicle",
            Description = "Ride the shooter's seat while an AI driver follows a road. Targets pop up beside it; friendly silhouettes are mixed in.",
            Goal = "Hit at least seven of the nine enemy targets.",
            Controls = "Aim and fire as normal. The car's motion disturbs your aim.",
            Skills = new[] { TrainingSkill.Aim, TrainingSkill.ClassSkill }, ParSeconds = 40.0f,
            Create = () => new VehicleShooterDrill(),
        });

        // ---- DRIVER ------------------------------------------------------------------------------
        list.Add(new DrillDefinition
        {
            Id = "drv_slalom", Track = TrainingTrack.Driver, Title = "Slalom",
            Description = "Weave a line of eight cones in the Land Cruiser. A touched cone costs two seconds.",
            Goal = "Finish near par with no cone hits.",
            Controls = "WASD drive, Shift boost, C brake, Space handbrake.",
            Skills = new[] { TrainingSkill.ClassSkill, TrainingSkill.Movement }, ParSeconds = 26.0f,
            Create = () => new SlalomDrill(),
        });
        list.Add(new DrillDefinition
        {
            Id = "drv_brake", Track = TrainingTrack.Driver, Title = "Braking Box",
            Description = "Reach 16 m/s on a straight, then stop with the car inside the marked box. Judging stopping distance is the base of every swap and repair.",
            Goal = "Stop inside the green box.",
            Controls = "W accelerate, S or C brake.",
            Skills = new[] { TrainingSkill.ClassSkill, TrainingSkill.Timing }, ParSeconds = 24.0f,
            Create = () => new BrakeBoxDrill(),
        });
        list.Add(new DrillDefinition
        {
            Id = "drv_swap", Track = TrainingTrack.Driver, Title = "Seat Swap",
            Description = "Swap seats at a full stop, shoot as the shooter, then have your AI driver roll while you shoot - and call SLOW DOWN to swap back at the 8 m/s limit.",
            Goal = "Complete both swaps with as few refusals as possible.",
            Controls = "E swaps seats (2 s). G calls SLOW DOWN to your driver.",
            Skills = new[] { TrainingSkill.ClassSkill, TrainingSkill.Comms, TrainingSkill.Timing }, ParSeconds = 100.0f,
            Create = () => new SeatSwapDrill(),
        });
        list.Add(new DrillDefinition
        {
            Id = "drv_hazard", Track = TrainingTrack.Driver, Title = "Hazard Run",
            Description = "A road run on almost no fuel, past spike strips. Boost is fast but burns three times the fuel.",
            Goal = "Reach the finish without hitting a strip or running dry.",
            Controls = "Shift boosts. Steer around the marked strips.",
            Skills = new[] { TrainingSkill.ClassSkill, TrainingSkill.Awareness }, ParSeconds = 24.0f,
            Create = () => new HazardRunDrill(),
        });

        // ---- FIXER -------------------------------------------------------------------------------
        list.Add(new DrillDefinition
        {
            Id = "fix_basics", Track = TrainingTrack.Fixer, Title = "Repair Basics",
            Description = "Fix a damaged parked car with exactly enough charges, then fix a rolling one - which means calling your driver to stop first.",
            Goal = "Get both cars fully repaired.",
            Controls = "Hold Q beside the car to repair. G calls SLOW DOWN to the driver.",
            Skills = new[] { TrainingSkill.ClassSkill, TrainingSkill.Comms }, ParSeconds = 85.0f,
            Create = () => new RepairBasicsDrill(),
        });
        list.Add(new DrillDefinition
        {
            Id = "fix_fight", Track = TrainingTrack.Fixer, Title = "Fix and Fight",
            Description = "Three waves of raiders shoot out the car's tires and shoot at you. Keep the car mobile and kill them - at the same time.",
            Goal = "Keep the car mobile and take down the attackers.",
            Controls = "Hold Q to repair while you aim and fire. Stay within reach of the car.",
            Skills = new[] { TrainingSkill.ClassSkill, TrainingSkill.Aim, TrainingSkill.Awareness }, ParSeconds = 120.0f,
            Create = () => new RepairUnderFireDrill(),
        });

        // ---- MEDIC -------------------------------------------------------------------------------
        list.Add(new DrillDefinition
        {
            Id = "med_triage", Track = TrainingTrack.Medic, Title = "Triage",
            Description = "Four hurt teammates and a pool that is just big enough. Heal the worst first and waste nothing.",
            Goal = "Bring every teammate to 50+ HP.",
            Controls = "Hold Q next to a teammate. Stand still for the 3 second channel.",
            Skills = new[] { TrainingSkill.ClassSkill, TrainingSkill.Timing }, ParSeconds = 40.0f,
            Create = () => new HealBasicsDrill(),
        });
        list.Add(new DrillDefinition
        {
            Id = "med_fire", Track = TrainingTrack.Medic, Title = "Heal Under Fire",
            Description = "A teammate is down in the open and two enemies are shooting. A hit cancels your heal - so blind them with smoke first.",
            Goal = "Stabilise the patient (55+ HP) and survive.",
            Controls = "F throws smoke at your crosshair. Hold Q to heal. G calls COVER ME.",
            Skills = new[] { TrainingSkill.ClassSkill, TrainingSkill.Awareness }, ParSeconds = 35.0f,
            Create = () => new HealUnderFireDrill(),
        });
        list.Add(new DrillDefinition
        {
            Id = "med_comms", Track = TrainingTrack.Medic, Title = "Answer the Call",
            Description = "Five teammates bleed out and call MEDIC one by one. Reply so they know you are coming, then reach them and heal - worst first.",
            Goal = "Save at least four of the five.",
            Controls = "G = 'On my way'. V opens the full callout list. Hold Q to heal.",
            Skills = new[] { TrainingSkill.Comms, TrainingSkill.ClassSkill, TrainingSkill.Awareness }, ParSeconds = 70.0f,
            Create = () => new TriageCommsDrill(),
        });

        // ---- DEMOLISHER --------------------------------------------------------------------------
        list.Add(new DrillDefinition
        {
            Id = "dem_charge", Track = TrainingTrack.Demolisher, Title = "Charge Basics",
            Description = "Plant charges on two walls and get clear before they blow. The red circle shows the blast radius.",
            Goal = "Breach both walls without catching yourself in the blast.",
            Controls = "Aim at a wall and hold Q to arm (5 s). Retreat when armed.",
            Skills = new[] { TrainingSkill.ClassSkill, TrainingSkill.Timing }, ParSeconds = 50.0f,
            Create = () => new ChargeBasicsDrill(),
        });
        list.Add(new DrillDefinition
        {
            Id = "dem_breach", Track = TrainingTrack.Demolisher, Title = "Breach and Clear",
            Description = "Breach a wall with two teammates stacked inside the blast radius. Warn them, blow it, and order the push. Your callouts decide who gets hurt.",
            Goal = "Clear the room with your team intact.",
            Controls = "Q plant. G sends the context callout: FIRE IN THE HOLE, then MOVE IN. V opens the full list.",
            Skills = new[] { TrainingSkill.Comms, TrainingSkill.ClassSkill, TrainingSkill.Aim }, ParSeconds = 60.0f,
            Create = () => new BreachAndClearDrill(),
        });
        list.Add(new DrillDefinition
        {
            Id = "dem_road", Track = TrainingTrack.Demolisher, Title = "Road Denial",
            Description = "Stop an enemy car with a single spike strip per round. Placement and timing decide it.",
            Goal = "Stop at least two of the three cars.",
            Controls = "Aim at the road centre and hold Q to deploy (3 s).",
            Skills = new[] { TrainingSkill.ClassSkill, TrainingSkill.Timing, TrainingSkill.Awareness }, ParSeconds = 60.0f,
            Create = () => new RoadDenialDrill(),
        });
        list.Add(new DrillDefinition
        {
            Id = "dem_heavy", Track = TrainingTrack.Demolisher, Title = "Heavy Weapons",
            Description = "Stop a moving car with tire shots, then destroy another with a Bazooka. Learn which tool does which job.",
            Goal = "Stop the first car and destroy the second.",
            Controls = "The drill equips the M4, then the Bazooka.",
            Skills = new[] { TrainingSkill.ClassSkill, TrainingSkill.Aim }, ParSeconds = 60.0f,
            Create = () => new HeavyWeaponDrill(),
        });

        return list;
    }
}
