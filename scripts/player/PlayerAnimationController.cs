using Godot;

/// <summary>
/// Compatibility surface for gameplay systems that used the former Manny full-body animation graph.
/// Manny clips are no longer loaded or played. First-person motion is driven exclusively by the
/// equipped FPS viewmodel rig through ViewModelManager.
/// </summary>
public partial class PlayerAnimationController : Node
{
	private bool _isDead;

	public bool IsDead => _isDead;
	public bool IsReloadPlaying => false;
	public bool IsFirePlaying => false;
	public float MeasuredSpeed => 0.0f;
	public Vector2 Heading => new(0.0f, 1.0f);
	public float TimeScale => 1.0f;
	public string CurrentGraphState => "FPS viewmodel only";
	public LeftHandIk? HandIk => null;
	public AnimationTree Tree => null!;
	public AnimationPlayer Player => null!;

	public void SetMovementState(PlayerMovement.MovementState state) { }
	public void PlayFire() { }
	public void PlayReload() { }
	public void CancelReload() { }
	public void PlayDeath() => _isDead = true;
	public void ResetAfterRespawn() => _isDead = false;
}
