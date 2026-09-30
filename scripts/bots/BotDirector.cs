using Godot;
using System.Collections.Generic;

/// <summary>
/// Keeps a match fun. Watches the last few fights between people and bots; if a person is getting
/// crushed the bots ease off a little (slower reactions, more wobble), and if a person is dominating they
/// tighten up slightly. It never turns Advanced into Easy: the range is capped at a gentle nudge.
/// Only runs on the host and only when "Adaptive Bots" is on.
/// </summary>
public partial class BotDirector : Node
{
	private const int WindowSize = 10;
	private readonly Queue<int> _results = new Queue<int>();
	private KillFeed? _feed;

	public override void _Ready()
	{
		_feed = KillFeed.From(this);
		if (_feed != null) _feed.KillAnnounced += OnKill;
	}

	public override void _ExitTree()
	{
		if (_feed != null && IsInstanceValid(_feed)) _feed.KillAnnounced -= OnKill;
		BotSenses.Ease = 0.0f;
	}

	private void OnKill(long killerPeerId, long victimPeerId, int styleFlags, int basePoints, int totalPoints, long assistPeerId, int assistPoints, bool struggles)
	{
		bool killerBot = BotRoster.IsBot(killerPeerId);
		bool victimBot = BotRoster.IsBot(victimPeerId);
		if (killerBot == victimBot) return; // bot vs bot / person vs person says nothing about bot balance

		_results.Enqueue(killerBot ? -1 : 1);
		while (_results.Count > WindowSize) _results.Dequeue();

		int sum = 0;
		foreach (int r in _results) sum += r;

		float target = 0.0f;
		if (sum <= -4) target = Mathf.Clamp((-sum - 3) * 0.06f, 0.0f, 0.30f);
		else if (sum >= 5) target = -Mathf.Clamp((sum - 4) * 0.03f, 0.0f, 0.12f);

		BotSenses.Ease = Mathf.MoveToward(BotSenses.Ease, target, 0.06f);
	}
}
