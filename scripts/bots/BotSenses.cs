using Godot;
using System.Collections.Generic;

/// <summary>
/// Shared "what is happening in the match" hub for bots: gunshots and team callouts are broadcast here
/// and each bot decides, according to its own level, whether it heard/noticed them. Also holds the
/// adaptive-difficulty offset written by BotDirector.
/// </summary>
public static class BotSenses
{
	private static readonly List<BotBrain> Brains = new List<BotBrain>();

	/// <summary>-0.12 (bots tighten up) .. +0.30 (bots ease off). 0 = the level exactly as tuned.</summary>
	public static float Ease { get; set; }

	public static void Register(BotBrain brain)
	{
		if (!Brains.Contains(brain)) Brains.Add(brain);
	}

	public static void Unregister(BotBrain brain) => Brains.Remove(brain);

	public static void Reset()
	{
		Brains.Clear();
		Ease = 0.0f;
	}

	public static void ReportGunshot(Vector3 position, float loudness, Node shooter)
	{
		for (int i = 0; i < Brains.Count; i++)
		{
			BotBrain b = Brains[i];
			if (GodotObject.IsInstanceValid(b)) b.HearGunshot(position, loudness, shooter);
		}
	}

	public static void ReportCallout(BotBrain from, Vector3 position, float certainty)
	{
		for (int i = 0; i < Brains.Count; i++)
		{
			BotBrain b = Brains[i];
			if (b != from && GodotObject.IsInstanceValid(b)) b.ReceiveCallout(from, position, certainty);
		}
	}
}
