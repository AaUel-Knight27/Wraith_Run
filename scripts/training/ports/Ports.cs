using Godot;

// =====================================================================================================
// PORTS - the seams where the real game systems plug in later.
//
// Each training mechanic (HealSystem, RepairSystem, ExplosiveSystem, ...) only talks to these
// interfaces. Training implements them with training-side stand-ins (TrainingActor, TrainingCar, ...).
// When the real system exists, make the real thing implement the same interface (or write a small
// adapter like PlayerHealable below) and the mechanic - and every drill built on it - works unchanged.
// See TRAINING_CENTER_README.md, section "Wiring the real systems", for the checklist.
// =====================================================================================================

/// <summary>Something a Medic can heal. Real wiring: PlayerHealable (below) already adapts the real Health node.</summary>
public interface IHealable
{
	string DisplayName { get; }
	Vector3 WorldPosition { get; }
	float CurrentHp { get; }
	float MaxHp { get; }
	bool IsAlive { get; }
	void Heal(float amount);
}

public enum RepairResult { None, TireFixed, BodyRepaired }

/// <summary>Something a Fixer can repair. Real wiring: the future vehicle class implements this.</summary>
public interface IRepairable
{
	string DisplayName { get; }
	Vector3 WorldPosition { get; }
	/// <summary>Distance from a world point to the nearest part of the vehicle body (the interact reach is measured from the hull, not its centre).</summary>
	float DistanceToHull(Vector3 worldPoint);
	float CurrentSpeed { get; }
	int FlatTireCount { get; }
	float HpFraction { get; }
	bool IsDestroyed { get; }
	bool NeedsRepair { get; }
	/// <summary>Spends one repair charge worth of work: a flat tire first, otherwise +hpAmount body HP.</summary>
	RepairResult ApplyRepairCharge(float hpAmount);
}

/// <summary>Something a Demolisher charge can be planted on (breach walls, vehicles).</summary>
public interface IPlantable
{
	string DisplayName { get; }
}

/// <summary>A sink for callouts (comms). Training uses LocalCalloutTransport; the real game swaps in an RPC-backed one.</summary>
public interface ICalloutTransport
{
	/// <summary>Send to everyone on the sender's team, including the local peer.</summary>
	void Broadcast(CalloutMessage message);
	event System.Action<CalloutMessage>? Received;
}

/// <summary>Wraps the REAL player Health node so training mechanics can heal the trainee like any friend.</summary>
public sealed class PlayerHealable : IHealable
{
	private readonly Health _health;
	private readonly Node3D _body;

	public PlayerHealable(Health health, Node3D body)
	{
		_health = health;
		_body = body;
	}

	public string DisplayName => "You";
	public Vector3 WorldPosition => _body.GlobalPosition;
	public float CurrentHp => _health.CurrentHealth;
	public float MaxHp => _health.MaxHealth;
	public bool IsAlive => !_health.IsDead;
	public void Heal(float amount)
	{
		if (_health.IsDead) return;
		_health.CurrentHealth = Mathf.Min(_health.MaxHealth, _health.CurrentHealth + amount);
	}
}
