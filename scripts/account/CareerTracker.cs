using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Autoload (see project.godot, next to MatchStats) that turns what happens in a match into the
/// local player's lifetime record, saves it, and shares the player's card with everyone else in the
/// session.
///
/// WHAT IT RECORDS - only ever for the person sitting at this machine (never for bots, never for
/// other players; each player's own device keeps their own career):
///   kills, deaths, assists and every medal   from KillFeed.KillAnnounced, which every peer
///                                            receives in the same order (see KillFeed / MatchStats);
///   the weapon, distance, last-bullet and    from NoteHit, called by WeaponSwitcher at the moment
///   one-shot context of a kill               a bullet lands, because only the shooter knows it;
///   playtime                                 while a match is running;
///   matches / wins / ties / losses           when the match ends or the player leaves (FinishMatch).
///
/// HOW A MATCH RESULT IS DECIDED (every match needs one, so Wins + Ties + Losses = Matches Played):
///   Team Deathmatch that ends normally   the team that won; a drawn match is a tie.
///   anything the player leaves early     the standing at that moment: ahead = win, level = tie,
///                                        behind = loss (team score in TDM, points in Deathmatch).
///   A match only counts if the player scored/died/assisted or stayed at least a minute, and only
///   if someone else was in it; joining for ten seconds, or playing alone, is not a match.
///
/// HOW CARDS TRAVEL: when a client connects it broadcasts its card to everyone and each existing
/// player answers with theirs, so within a moment everyone has everyone's card. Opening a card in the
/// PLAYERS list asks for a fresh copy, and a player re-broadcasts when a match ends. Cards are
/// untrusted input: they are size-limited, sanitised and rate-limited (see PlayerCardCodec).
/// </summary>
public partial class CareerTracker : Node
{
    public static CareerTracker? Instance { get; private set; }

    /// <summary>Raised on this machine when the local player earns a notable medal; the HUD shows it
    /// on the feed. Argument = the medal's display name.</summary>
    public event Action<string>? MedalEarned;
    private readonly List<Medal> _earnedScratch = new();

    private const double SaveIntervalSeconds = 10.0;
    private const double MinMatchSeconds = 60.0;
    private const ulong HitRecordLifetimeMsec = 8000;
    private const ulong MinCardIntervalMsec = 300;
    private const float FullHealthFraction = 0.99f;

    // ---- what the shooter saw when bullets landed ---------------------------------------------

    private sealed class HitRecord
    {
        public string Weapon = string.Empty;
        public float Distance = -1.0f;
        public bool LastBullet;
        public bool FirstHitAtFullHealth;
        public int DistinctShots;
        public int LastShotSerial = -1;
        public ulong LastMsec;
    }

    private readonly Dictionary<long, HitRecord> _hits = new();

    // ---- per-match state (reset by BeginMatch) ------------------------------------------------

    private bool _matchActive;
    private bool _matchRecorded;
    private double _matchSeconds;
    private int _myKills;
    private int _myDeaths;
    private int _myAssists;

    private readonly Dictionary<long, int> _streaks = new();
    private readonly Dictionary<long, int> _matchKills = new();
    private int _totalKillsSeen;
    private long _lastKillerOfMe = -1;
    private ulong _lastKillMsec;
    private int _chain;
    private int _lastKillShotSerial = -1;
    private int _multiShotAwardedSerial = -1;

    // ---- saving + networking ------------------------------------------------------------------

    private KillFeed? _killFeed;
    private bool _dirty;
    private double _sinceSave;
    private readonly Dictionary<long, ulong> _lastCardMsec = new();

    private CareerStats Stats => PlayerAccount.Current.Stats;

    // -------------------------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------------------------

    public override void _Ready()
    {
        Instance = this;
        PlayerDirectory.SetLocal(PlayerAccount.Current);

        _killFeed = KillFeed.From(this);
        if (_killFeed != null) _killFeed.KillAnnounced += OnKillAnnounced;
        else GD.PushWarning("CareerTracker could not reach the KillFeed autoload; career stats will not be recorded.");

        if (MatchManager.Instance != null) MatchManager.Instance.MatchEnded += OnMatchEnded;
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
    }

    public override void _ExitTree()
    {
        if (_killFeed != null && IsInstanceValid(_killFeed)) _killFeed.KillAnnounced -= OnKillAnnounced;
        if (MatchManager.Instance != null && IsInstanceValid(MatchManager.Instance))
            MatchManager.Instance.MatchEnded -= OnMatchEnded;
        Multiplayer.ConnectedToServer -= OnConnectedToServer;
        Multiplayer.PeerDisconnected -= OnPeerDisconnected;
        if (_dirty) SaveNow();
        if (Instance == this) Instance = null;
    }

    public override void _Notification(int what)
    {
        // Window closed mid-match: bank it. (Leaving through the menus goes through GameSession.)
        if (what == (int)NotificationWMCloseRequest)
        {
            FinishMatch();
            SaveNow();
        }
        // Android may kill a backgrounded app without another chance to save.
        else if (what == (int)NotificationApplicationPaused) SaveNow();
    }

    public override void _Process(double delta)
    {
        PlayerDirectory.SetLocalPeer(LocalId());

        if (_matchActive && !_matchRecorded)
        {
            _matchSeconds += delta;
            Stats.AddPlaytime(delta);
            _dirty = true;
        }

        _sinceSave += delta;
        if (_dirty && _sinceSave >= SaveIntervalSeconds) SaveNow();
    }

    /// <summary>This machine's peer id. With no network peer (running GameWorld straight from the
    /// editor) everything is peer 1, which is also what PlayerIdentity reports there.</summary>
    private long LocalId() => Multiplayer.HasMultiplayerPeer() ? Multiplayer.GetUniqueId() : 1;

    private void SaveNow()
    {
        _dirty = false;
        _sinceSave = 0.0;
        PlayerAccount.Save();
    }

    // -------------------------------------------------------------------------------------------
    // Match begin / end
    // -------------------------------------------------------------------------------------------

    /// <summary>GameWorld calls this when the match scene loads.</summary>
    public void BeginMatch()
    {
        _hits.Clear();
        _streaks.Clear();
        _matchKills.Clear();
        _totalKillsSeen = 0;
        _lastKillerOfMe = -1;
        _lastKillMsec = 0;
        _chain = 0;
        _lastKillShotSerial = -1;
        _multiShotAwardedSerial = -1;
        _myKills = _myDeaths = _myAssists = 0;
        _matchSeconds = 0.0;
        _matchRecorded = false;
        _matchActive = true;
    }

    /// <summary>GameSession.TearDownSession calls this when the player leaves for the menu, and the
    /// window-close handler calls it too. Safe to call twice and when no match is running: the
    /// first call banks the match, later ones do nothing.</summary>
    public void FinishMatch() => FinishMatch(ended: false, winningTeam: -1);

    private void FinishMatch(bool ended, int winningTeam)
    {
        if (!_matchActive || _matchRecorded) return;
        _matchRecorded = true;
        _matchActive = false;

        bool didSomething = _myKills + _myDeaths + _myAssists > 0 || _matchSeconds >= MinMatchSeconds;
        if (didSomething && TryDecideResult(ended, winningTeam, out MatchResult result))
            Stats.RecordMatch(result);

        SaveNow();
        BroadcastCard(wantReply: false); // everyone else's copy of my card catches up
    }

    private void OnMatchEnded(int winningTeam, string reason)
    {
        // One frame later: this signal is raised from inside MatchManager's own kill handler, which
        // runs before ours, so the winning kill has not been counted here yet.
        Callable.From(() => FinishMatch(ended: true, winningTeam: winningTeam)).CallDeferred();
    }

    private bool TryDecideResult(bool ended, int winningTeam, out MatchResult result)
    {
        result = MatchResult.Loss;
        MatchManager? match = MatchManager.Instance;
        long me = LocalId();

        if (match != null && match.GameMode == MatchManager.Mode.TeamDeathmatch)
        {
            int myTeam = match.GetTeam(me);
            if (myTeam < 0) return false; // never got a team: nothing to judge

            if (ended)
            {
                result = winningTeam < 0 ? MatchResult.Tie : (winningTeam == myTeam ? MatchResult.Win : MatchResult.Loss);
                return true;
            }
            int mine = match.GetTeamScore(myTeam);
            int theirs = match.GetTeamScore(1 - myTeam);
            result = mine > theirs ? MatchResult.Win : (mine == theirs ? MatchResult.Tie : MatchResult.Loss);
            return true;
        }

        // Deathmatch: your place on the board right now.
        List<ScoreboardData.Row> rows = ScoreboardData.Collect(GetTree());
        if (rows.Count < 2) return false; // nobody to beat

        ScoreboardData.Row? mineRow = rows.Find(r => r.PeerId == me);
        if (mineRow == null) return false;
        int best = rows[0].Points; // Collect sorts best first
        if (mineRow.Points < best) { result = MatchResult.Loss; return true; }

        int level = rows.FindAll(r => r.Points == best).Count;
        result = level > 1 ? MatchResult.Tie : MatchResult.Win;
        return true;
    }

    /// <summary>Called after the player has left the match: forget everyone else.</summary>
    public void EndSession()
    {
        _matchActive = false;
        _hits.Clear();
        _streaks.Clear();
        _matchKills.Clear();
        _lastCardMsec.Clear();
        PlayerDirectory.ClearRemote();
    }

    // -------------------------------------------------------------------------------------------
    // Shots
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// WeaponSwitcher calls this on the LOCAL player's device each time one of their bullets (or
    /// shotgun pellets, or a knife swing) lands on a living player or bot, before the damage is
    /// sent. When the kill is announced a moment later it is matched up with this record to learn
    /// which weapon made it, from how far, with the last round, and in how many shots.
    /// </summary>
    public void NoteHit(long victimId, string weapon, float distance, int ammoAfterShot, int magazineSize,
        int shotSerial, float victimHealth, float victimMaxHealth)
    {
        if (!_matchActive) return;
        ulong now = Time.GetTicksMsec();

        if (!_hits.TryGetValue(victimId, out HitRecord? record) || now - record.LastMsec > HitRecordLifetimeMsec)
        {
            // First hit on this life. "One shot, one kill" needs the victim to be untouched now.
            record = new HitRecord
            {
                FirstHitAtFullHealth = victimMaxHealth > 0.0f && victimHealth >= victimMaxHealth * FullHealthFraction,
            };
            _hits[victimId] = record;
        }

        // Pellets from one shotgun pull share a serial, so they count as ONE shot.
        if (record.LastShotSerial != shotSerial) record.DistinctShots++;
        record.LastShotSerial = shotSerial;
        record.Weapon = weapon;
        record.Distance = distance;
        record.LastBullet = magazineSize > 0 && ammoAfterShot <= 0;
        record.LastMsec = now;
    }

    // -------------------------------------------------------------------------------------------
    // Kills
    // -------------------------------------------------------------------------------------------

    private void OnKillAnnounced(long killerPeerId, long victimPeerId, int styleFlags, int basePoints,
        int totalPoints, long assistPeerId, int assistPoints, bool struggles)
    {
        if (!_matchActive) return;

        long me = LocalId();
        MatchManager? match = MatchManager.Instance;
        bool validKill = killerPeerId >= 0 && killerPeerId != victimPeerId;
        // With Friendly Fire on, a team-kill reaches here. It is not a kill for anyone's record, the
        // streak table or the leaderboard - the same call MatchManager makes for the team score.
        bool teamKill = validKill && match != null && match.AreTeammates(killerPeerId, victimPeerId);

        _hits.TryGetValue(victimPeerId, out HitRecord? hit);
        int victimStreakBefore = StreakOf(victimPeerId);

        if (victimPeerId == me) HandleMyDeath(killerPeerId, validKill);

        if (validKill && !teamKill)
        {
            if (killerPeerId == me) HandleMyKill(victimPeerId, (KillStyle)styleFlags, hit, victimStreakBefore, match);

            // Shared tables, updated AFTER the checks above so they describe the moment before this kill.
            _totalKillsSeen++;
            _matchKills[killerPeerId] = KillsOf(killerPeerId) + 1;
            _streaks[killerPeerId] = StreakOf(killerPeerId) + 1;
        }
        _streaks[victimPeerId] = 0;
        _hits.Remove(victimPeerId);

        if (assistPeerId == me && assistPeerId != victimPeerId && assistPeerId != killerPeerId && !teamKill)
        {
            Stats.RecordAssist();
            _myAssists++;
            _dirty = true;
        }
    }

    private void HandleMyDeath(long killerPeerId, bool validKill)
    {
        Stats.RecordDeath(LocalWeaponName());
        _myDeaths++;
        _chain = 0;
        _dirty = true;
        if (validKill) _lastKillerOfMe = killerPeerId;
    }

    private void HandleMyKill(long victimId, KillStyle styles, HitRecord? hit, int victimStreakBefore,
        MatchManager? match)
    {
        long me = LocalId();
        ulong now = Time.GetTicksMsec();

        // Chain of kills inside the multi-kill window (same window PlayerScore uses).
        bool inWindow = _chain > 0 && now - _lastKillMsec <= (ulong)(MedalRules.MultiKillWindowSeconds * 1000.0f);
        _chain = inWindow ? _chain + 1 : 1;
        _lastKillMsec = now;

        int shotSerial = hit?.LastShotSerial ?? -1;
        bool oneShotMulti = inWindow && shotSerial >= 0 && shotSerial == _lastKillShotSerial
            && shotSerial != _multiShotAwardedSerial;
        if (oneShotMulti) _multiShotAwardedSerial = shotSerial;
        _lastKillShotSerial = shotSerial;

        int myStreakNow = StreakOf(me) + 1; // this kill included
        bool revenge = victimId == _lastKillerOfMe;
        if (revenge) _lastKillerOfMe = -1;

        var context = new KillContext
        {
            Styles = styles,
            Distance = hit?.Distance ?? -1.0f,
            LastBullet = hit?.LastBullet ?? false,
            OneShotOneKill = hit != null && hit.DistinctShots == 1 && hit.FirstHitAtFullHealth,
            OneShotMultiKill = oneShotMulti,
            WallBang = false, // shots stop at the first surface today; see KillContext.WallBang
            Revenge = revenge,
            StoppedStreak = victimStreakBefore >= MedalRules.StopStreakMinimum,
            ReachedMajorStreak = myStreakNow % MedalRules.MajorStreakInterval == 0,
            KingSlayer = IsKingSlayer(me, victimId),
            FirstBlood = _totalKillsSeen == 0,
            TeamWipe = IsTeamWipe(match, me, victimId),
            MultiKillCount = _chain,
        };

        _earnedScratch.Clear();
        Stats.RecordKill(hit != null && hit.Weapon.Length > 0 ? hit.Weapon : LocalWeaponName(), in context, _earnedScratch);
        foreach (Medal medal in _earnedScratch)
            if (MedalCatalog.IsNotable(medal)) MedalEarned?.Invoke(MedalCatalog.Get(medal).Label);
        _myKills++;
        _dirty = true;
    }

    private int StreakOf(long id) => _streaks.TryGetValue(id, out int streak) ? streak : 0;
    private int KillsOf(long id) => _matchKills.TryGetValue(id, out int kills) ? kills : 0;

    /// <summary>The victim leads the match outright and has a real lead worth ending.</summary>
    private bool IsKingSlayer(long me, long victimId)
    {
        if (victimId == me) return false;
        int victimKills = KillsOf(victimId);
        if (victimKills < MedalRules.KingSlayerMinimumKills) return false;
        foreach (KeyValuePair<long, int> entry in _matchKills)
            if (entry.Key != victimId && entry.Value >= victimKills) return false;
        return true;
    }

    /// <summary>Team Deathmatch: with this kill, every member of the enemy team (two or more of
    /// them) is down. The victim counts as down even though their replicated health may not have
    /// reached this machine yet.</summary>
    private bool IsTeamWipe(MatchManager? match, long me, long victimId)
    {
        if (match == null || match.GameMode != MatchManager.Mode.TeamDeathmatch) return false;
        int myTeam = match.GetTeam(me);
        if (myTeam < 0) return false;

        int enemies = 0;
        foreach (Node node in GetTree().GetNodesInGroup("players"))
        {
            if (node is not CharacterBody3D body || !IsInstanceValid(body) || body.IsQueuedForDeletion()) continue;
            long id = PlayerIdentity.IdOf(body);
            if (id == victimId) { enemies++; continue; }

            int team = match.GetTeam(id);
            if (team < 0 || team == myTeam) continue;
            enemies++;
            Health? health = body.GetNodeOrNull<Health>("Health");
            if (health == null || !health.IsDead) return false;
        }
        return enemies >= 2;
    }

    // -------------------------------------------------------------------------------------------
    // Local player lookups
    // -------------------------------------------------------------------------------------------

    private Node? LocalPlayer()
    {
        foreach (Node node in GetTree().GetNodesInGroup("players"))
            if (IsInstanceValid(node) && PlayerIdentity.IsLocalHuman(node)) return node;
        return null;
    }

    private string LocalWeaponName()
    {
        string? name = LocalPlayer()?.GetNodeOrNull<WeaponSwitcher>("WeaponSwitcher")?.WeaponName;
        return string.IsNullOrEmpty(name) ? "Unknown" : name!;
    }

    // -------------------------------------------------------------------------------------------
    // Sharing cards
    // -------------------------------------------------------------------------------------------

    private bool NetworkReady() =>
        Multiplayer.HasMultiplayerPeer()
        && Multiplayer.MultiplayerPeer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    private string MyCardJson() => PlayerCardCodec.Encode(PlayerAccount.Current, forDisk: false);

    private void OnConnectedToServer()
    {
        PlayerDirectory.SetLocalPeer(LocalId());
        BroadcastCard(wantReply: true); // "this is me - and tell me who you are"
    }

    private void OnPeerDisconnected(long peerId)
    {
        PlayerDirectory.Remove(peerId);
        _lastCardMsec.Remove(peerId);
    }

    /// <summary>Sends my card to every other player. With wantReply, each of them answers with theirs.</summary>
    private void BroadcastCard(bool wantReply)
    {
        if (!NetworkReady() || Multiplayer.GetPeers().Length == 0) return; // solo: nobody to tell
        Rpc(MethodName.ReceiveCard, MyCardJson(), wantReply);
    }

    /// <summary>The PLAYERS list calls this when a card is opened: swap cards with that one player
    /// so what is on screen is current, not as old as the moment they joined.</summary>
    public void RequestFreshCard(long peerId)
    {
        if (peerId == LocalId() || BotRoster.IsBot(peerId) || !NetworkReady()) return;
        RpcId(peerId, MethodName.ReceiveCard, MyCardJson(), true);
    }

    /// <summary>
    /// Another player's card. AnyPeer because every player sends theirs; the engine supplies who
    /// really sent it (GetRemoteSenderId), so one player cannot file a card under someone else's id.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void ReceiveCard(string json, bool wantReply)
    {
        PlayerDirectory.SetLocalPeer(LocalId());
        long sender = Multiplayer.GetRemoteSenderId();
        if (sender <= 0 || sender == LocalId()) return;

        // Flood guard: a card changes at most a few times a minute, so more than ~3 a second from
        // one peer is noise (or an attack), not information.
        ulong now = Time.GetTicksMsec();
        if (_lastCardMsec.TryGetValue(sender, out ulong last) && now - last < MinCardIntervalMsec) return;
        _lastCardMsec[sender] = now;

        if (!PlayerCardCodec.TryDecode(json, out PlayerCard? card) || card == null) return;
        PlayerDirectory.Set(sender, card);

        if (wantReply && NetworkReady())
            RpcId(sender, MethodName.ReceiveCard, MyCardJson(), false);
    }
}
