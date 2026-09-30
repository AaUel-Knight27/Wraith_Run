using System.Collections.Generic;

/// <summary>
/// Pure-C# registry of who the bots are (names + levels), filled on EVERY peer from the spawn data so
/// the kill feed and name tags can print "Vex [A]" instead of a 10-digit fake peer id.
/// </summary>
public static class BotRoster
{
	/// <summary>Bot ids live above every realistic ENet peer id so they never collide with a person.</summary>
	public const long IdBase = 2_000_000_000L;

	private static readonly Dictionary<long, string> Names = new Dictionary<long, string>();
	private static readonly Dictionary<long, int> Levels = new Dictionary<long, int>();
	private static long _nextId = IdBase;

	public static readonly string[] NamePool =
	{
		"Vex", "Rook", "Nova", "Ghost", "Kestrel", "Onyx", "Mako", "Zephyr", "Cipher", "Blitz",
		"Hex", "Raven", "Jinx", "Talon", "Echo", "Viper", "Drift", "Sable", "Ember", "Quill",
		"Bolt", "Havoc", "Lynx", "Magpie", "Nyx", "Pixel", "Rogue", "Saber", "Tundra", "Wraith",
		"Atlas", "Cobalt", "Dagger", "Falcon", "Grim", "Halo", "Ion", "Jolt", "Krait", "Lotus",
	};

	public static long AllocateId() => _nextId++;

	public static void Register(long id, string name, int level)
	{
		Names[id] = name;
		Levels[id] = level;
	}

	public static bool IsBot(long id) => Names.ContainsKey(id);

	public static void Clear()
	{
		Names.Clear();
		Levels.Clear();
		_nextId = IdBase;
	}

	/// <summary>"Vex [A]" for a bot (E/N/A = level), "Player 12345" for a person.</summary>
	public static string DisplayName(long id)
	{
		if (Names.TryGetValue(id, out string? name))
		{
			string tag = "N";
			if (Levels.TryGetValue(id, out int level))
				tag = level == (int)BotDifficulty.Easy ? "E" : level == (int)BotDifficulty.Advanced ? "A" : "N";
			return $"{name} [{tag}]";
		}
		return $"Player {id}";
	}
}
