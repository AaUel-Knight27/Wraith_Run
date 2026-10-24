using Godot;
using System.Collections.Generic;

/// <summary>
/// Object pool for impact and particle VFX instances, eliminating runtime instantiation spikes.
/// </summary>
public partial class VFXPool : Node
{
	private static VFXPool? _instance;
	public static VFXPool? Instance => _instance;

	private readonly Dictionary<string, Queue<Node3D>> _availablePools = new();
	private readonly Dictionary<string, List<Node3D>> _activePools = new();
	private const int MaxInstancesPerEffect = 16;
	private ulong _spawnCounter;

	public override void _EnterTree()
	{
		_instance = this;
	}

	public override void _ExitTree()
	{
		if (_instance == this) _instance = null;
		_availablePools.Clear();
		_activePools.Clear();
	}

	public static Node3D? Spawn(PackedScene scene, Vector3 position, Transform3D? transform = null,
		float autoReleaseSeconds = 1.5f, Node? caller = null)
	{
		if (scene == null) return null;
		EnsureInstance(caller);
		if (_instance == null) return null;

		string key = scene.ResourcePath;
		if (!_instance._availablePools.TryGetValue(key, out Queue<Node3D>? available))
		{
			available = new Queue<Node3D>();
			_instance._availablePools[key] = available;
			_instance._activePools[key] = new List<Node3D>();
		}

		Node3D node;
		if (available.Count > 0)
		{
			node = available.Dequeue();
		}
		else if (_instance._activePools[key].Count >= MaxInstancesPerEffect)
		{
			// Steal oldest active
			node = _instance._activePools[key][0];
			_instance._activePools[key].RemoveAt(0);
		}
		else
		{
			var inst = scene.Instantiate<Node3D>();
			_instance.AddChild(inst);
			node = inst;
		}

		_instance._activePools[key].Add(node);
		node.Visible = true;
		if (transform.HasValue)
			node.GlobalTransform = transform.Value;
		else
			node.GlobalPosition = position;

		ulong currentSpawnId = ++_instance._spawnCounter;
		node.SetMeta("SpawnId", currentSpawnId);

		if (autoReleaseSeconds > 0)
		{
			var timer = _instance.GetTree().CreateTimer(autoReleaseSeconds);
			timer.Timeout += () =>
			{
				if (IsInstanceValid(node) && node.HasMeta("SpawnId") && (ulong)node.GetMeta("SpawnId") == currentSpawnId)
					Release(scene, node);
			};
		}

		return node;
	}

	public static void Release(PackedScene scene, Node3D node)
	{
		if (_instance == null || !IsInstanceValid(node)) return;
		node.SetMeta("SpawnId", 0UL);
		string key = scene.ResourcePath;
		if (_instance._activePools.TryGetValue(key, out List<Node3D>? active))
		{
			active.Remove(node);
		}

		node.Visible = false;
		if (_instance._availablePools.TryGetValue(key, out Queue<Node3D>? available))
		{
			if (!available.Contains(node))
			{
				available.Enqueue(node);
			}
		}
	}

	private static void EnsureInstance(Node? fallback)
	{
		if (_instance != null && IsInstanceValid(_instance)) return;
		SceneTree? tree = Engine.GetMainLoop() as SceneTree;
		if (tree?.Root == null && fallback != null) tree = fallback.GetTree();
		if (tree?.Root == null) return;

		var existing = tree.Root.GetNodeOrNull<VFXPool>("VFXPool");
		if (existing != null)
		{
			_instance = existing;
			return;
		}

		var pool = new VFXPool { Name = "VFXPool" };
		tree.Root.AddChild(pool);
		_instance = pool;
	}
}
