using Godot;
using System.Collections.Generic;

/// <summary>
/// Object pool for positional 3D audio playback. Avoids per-shot AudioStreamPlayer3D instantiation
/// and scene tree churn during intense firefights.
/// Pool size default: 32 concurrent voices (User Decision 3).
/// </summary>
public partial class AudioPool : Node
{
	public const int DefaultPoolSize = 32;
	private static AudioPool? _instance;
	public static AudioPool? Instance => _instance;

	private readonly List<AudioStreamPlayer3D> _pool = new();
	private readonly Queue<AudioStreamPlayer3D> _available = new();

	public override void _EnterTree()
	{
		_instance = this;
	}

	public override void _Ready()
	{
		InitializePool(DefaultPoolSize);
	}

	public override void _ExitTree()
	{
		if (_instance == this) _instance = null;
		_pool.Clear();
		_available.Clear();
	}

	private void InitializePool(int count)
	{
		for (int i = 0; i < count; i++)
		{
			var player = new AudioStreamPlayer3D
			{
				Name = $"PooledAudio_{i}",
				Bus = "Weapons",
			};
			AddChild(player);
			_pool.Add(player);
			_available.Enqueue(player);
			player.Finished += () => ReleasePlayer(player);
		}
	}

	/// <summary>
	/// Borrows an available player, or recycles the oldest active player if all 32 are in use.
	/// </summary>
	public static AudioStreamPlayer3D? GetPlayer(Node? caller = null)
	{
		EnsureInstance(caller);
		if (_instance == null) return null;
		if (_instance._pool.Count == 0) _instance.InitializePool(DefaultPoolSize);

		AudioStreamPlayer3D player;
		if (_instance._available.Count > 0)
		{
			player = _instance._available.Dequeue();
		}
		else if (_instance._pool.Count > 0)
		{
			// Pool exhausted: steal oldest player
			player = _instance._pool[0];
			_instance._pool.RemoveAt(0);
			_instance._pool.Add(player);
			if (player.Playing) player.Stop();
		}
		else
		{
			return null;
		}

		return player;
	}

	/// <summary>
	/// Returns a player to the available pool.
	/// </summary>
	public static void ReleasePlayer(AudioStreamPlayer3D player)
	{
		if (_instance == null || !IsInstanceValid(player)) return;
		if (player.Playing) player.Stop();
		if (!_instance._available.Contains(player))
		{
			_instance._available.Enqueue(player);
		}
	}

	/// <summary>
	/// Plays a 3D sound event at the specified position using the pooled player.
	/// </summary>
	public static void PlayAt(AudioStream? stream, Vector3 worldPosition, float unitSize = 6.0f,
		float maxDistance = 60.0f, float volumeDb = 0.0f, string bus = "Weapons", float pitchScale = 1.0f,
		Node? caller = null)
	{
		if (stream == null) return;
		AudioStreamPlayer3D? player = GetPlayer(caller);
		if (player == null) return;

		player.GlobalPosition = worldPosition;
		player.Stream = stream;
		player.UnitSize = unitSize;
		player.MaxDistance = maxDistance;
		player.VolumeDb = volumeDb;
		player.Bus = bus;
		player.PitchScale = pitchScale;
		player.Play();
	}

	private static void EnsureInstance(Node? fallback)
	{
		if (_instance != null && IsInstanceValid(_instance)) return;
		SceneTree? tree = Engine.GetMainLoop() as SceneTree;
		if (tree?.Root == null && fallback != null) tree = fallback.GetTree();
		if (tree?.Root == null) return;

		var existing = tree.Root.GetNodeOrNull<AudioPool>("AudioPool");
		if (existing != null)
		{
			_instance = existing;
			return;
		}

		var pool = new AudioPool { Name = "AudioPool" };
		tree.Root.AddChild(pool);
		_instance = pool;
	}
}
