using Godot;

/// <summary>
/// Floating name label above a player's head - the "who is on my team" half of the Team
/// Deathmatch system. MatchManager owns which team everyone is on and whether this is even
/// switched on (the lobby's Team Indicators toggle); this script only draws what MatchManager
/// already knows.
///
/// Builds and runs on every peer's copy of every player - the opposite of PlayerHud, which is
/// owner-only - because the whole point is for everyone ELSE to see it. No manual line-of-sight
/// raycast is needed to keep an enemy's label from showing through walls: NoDepthTest is only
/// switched on for an ally, so an enemy's label is left to the ordinary GPU depth test, which
/// already hides it behind geometry for free - the same "ally through walls, enemy needs line of
/// sight" split most tactical shooters use for team awareness.
/// </summary>
public partial class TeamIndicator : Label3D
{
    private long _ownerPeerId;
    private bool _stateKnown;
    private bool _lastVisible;
    private bool _lastAlly;

    public override void _Ready()
    {
        var player = (CharacterBody3D)GetParent();
        _ownerPeerId = player.GetMultiplayerAuthority();

        Text = $"Player {_ownerPeerId}";
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
        FontSize = 40;
        OutlineSize = 12;
        OutlineModulate = new Color(0.0f, 0.0f, 0.0f, 0.85f);
        // A little above the top of the 1.8m capsule (see Player.tscn's CollisionShape3D) so it
        // clears the head rather than sitting inside it.
        Position = new Vector3(0.0f, 2.15f, 0.0f);
        Visible = false;
    }

    public override void _Process(double delta)
    {
        MatchManager? match = MatchManager.Instance;
        bool relevant = match != null && match.GameMode == MatchManager.Mode.TeamDeathmatch
            && match.TeamIndicators && _ownerPeerId != Multiplayer.GetUniqueId();

        int myTeam = relevant ? match!.GetTeam(Multiplayer.GetUniqueId()) : -1;
        int targetTeam = relevant ? match!.GetTeam(_ownerPeerId) : -1;
        bool show = relevant && myTeam >= 0 && targetTeam >= 0;
        bool ally = show && targetTeam == myTeam;

        // Same "only touch the theme data when the state actually flips" rule PlayerHud's
        // ApplyLowHealthColour already follows, so this is not rewriting label properties every
        // single frame for every player on screen.
        if (_stateKnown && show == _lastVisible && ally == _lastAlly) return;
        _stateKnown = true;
        _lastVisible = show;
        _lastAlly = ally;

        Visible = show;
        if (!show) return;

        Modulate = MatchManager.TeamColor(ally ? myTeam : targetTeam);
        NoDepthTest = ally;
    }
}
