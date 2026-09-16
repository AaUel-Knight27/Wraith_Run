using Godot;
using System;

/// <summary>
/// Local-only player locomotion. Network prediction/synchronisation belongs in a later layer.
/// </summary>
public partial class PlayerMovement : CharacterBody3D
{
    public enum MovementState { Idle, Walk, Sprint, Crouch, CrouchWalk, Slide, Jump, Fall, Aim }

    public const float WalkSpeed = 4.7f;
    public const float SprintSpeed = 7.2f;
    public const float CrouchSpeed = 2.3f;
    public const float Gravity = -18.0f;
    public const float JumpVelocity = 6.5727f;
    public const float TerminalFallVelocity = -53.0f;
    public const float SettledGroundVelocity = -3.75f;

    private const float StandingCapsuleHeight = 1.8f;
    private const float CrouchedCapsuleHeight = 1.0f;
    private const float HeightTransitionSeconds = 0.2f;
    private const float SpeedTransitionSeconds = 0.12f;
    private const float AirControlMultiplier = 0.65f;
    private const float SlideSpeedMultiplier = 1.3f;
    private const float MaximumSlideSpeed = 9.4f;
    private const float SlideFriction = 10.0f;
    private const float LandingCooldownSeconds = 0.15f;

    [Export] public float MouseSensitivity { get; set; } = 0.0025f;
    [Export] public float CameraPitchLimitDegrees { get; set; } = 85.0f;

    public MovementState State { get; private set; } = MovementState.Fall;
    // These are intentionally replicated by MultiplayerSynchronizer.  Movement remains client-authoritative.
    [Export] public string ReplicatedMovementState { get; set; } = MovementState.Idle.ToString();
    [Export] public bool ReplicatedAimState { get; set; }

    private CollisionShape3D _collisionShape = null!;
    private ShapeCast3D _groundProbe = null!;
    private Node3D _head = null!;
    private Camera3D _camera = null!;
    private PlayerAnimationController _animationController = null!;
    private Health? _health;
    private CapsuleShape3D _capsule = null!;
    private Vector3 _slideVelocity;
    private float _pitch;
    private float _landingCooldown;
    private bool _wasGrounded;

    public override void _Ready()
    {
        _collisionShape = GetNode<CollisionShape3D>("CollisionShape3D");
        _groundProbe = GetNode<ShapeCast3D>("GroundProbe");
        _head = GetNode<Node3D>("Head");
        _camera = GetNode<Camera3D>("Head/FirstPersonCamera");
        _animationController = GetNode<PlayerAnimationController>("AnimationController");
        _health = GetNodeOrNull<Health>("Health");
        _capsule = (CapsuleShape3D)_collisionShape.Shape;
        ((SphereShape3D)_groundProbe.Shape).Radius = _capsule.Radius;
        // FirstPersonArms used to own this; with a single always-visible body mesh there is no
        // separate view-model to toggle, but each client must still only activate its own camera.
        _camera.Current = IsMultiplayerAuthority();
        if (IsMultiplayerAuthority()) Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!IsMultiplayerAuthority()) return;
        if (@event is InputEventMouseMotion mouseMotion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            RotateY(-mouseMotion.Relative.X * MouseSensitivity);
            _pitch = Mathf.Clamp(_pitch - mouseMotion.Relative.Y * MouseSensitivity,
                Mathf.DegToRad(-CameraPitchLimitDegrees), Mathf.DegToRad(CameraPitchLimitDegrees));
            _head.Rotation = new Vector3(_pitch, 0.0f, 0.0f);
        }

        if (@event.IsActionPressed("ui_cancel"))
            Input.MouseMode = Input.MouseModeEnum.Visible;
        else if (@event is InputEventMouseButton && Input.MouseMode == Input.MouseModeEnum.Visible)
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _PhysicsProcess(double deltaValue)
    {
        if (!IsMultiplayerAuthority())
        {
            if (Enum.TryParse(ReplicatedMovementState, out MovementState remoteState))
                _animationController.SetMovementState(remoteState);
            return;
        }
        if (_health != null && _health.IsDead)
        {
            Velocity = Vector3.Zero;
            return;
        }
        float delta = (float)deltaValue;
        bool grounded = IsOnFloor() || _groundProbe.IsColliding();
        Vector2 moveInput = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
        Vector3 moveDirection = (Transform.Basis * new Vector3(moveInput.X, 0.0f, moveInput.Y)).Normalized();
        bool crouchHeld = Input.IsActionPressed("crouch");

        if (grounded && !_wasGrounded)
            _landingCooldown = LandingCooldownSeconds;

        UpdateVerticalVelocity(delta, grounded);

        if (State == MovementState.Slide)
            UpdateSlide(delta, moveDirection, crouchHeld);
        else if (grounded)
            UpdateGroundMovement(delta, moveInput, moveDirection, crouchHeld);
        else
            UpdateAirMovement(delta, moveDirection);

        UpdateColliderAndHead(delta, State is MovementState.Crouch or MovementState.CrouchWalk or MovementState.Slide);
        float targetFov = State == MovementState.Aim ? 55.0f : 75.0f;
        _camera.Fov = Mathf.MoveToward(_camera.Fov, targetFov, 20.0f * delta / 0.15f);
        MoveAndSlide();
        _wasGrounded = IsOnFloor() || _groundProbe.IsColliding();
        ReplicatedMovementState = State.ToString();
        ReplicatedAimState = State == MovementState.Aim;
        _animationController.SetMovementState(State);

        // TODO: Add optional head bob, camera shake, and sprint FOV effects separately.
    }

    private void UpdateVerticalVelocity(float delta, bool grounded)
    {
        if (!grounded || _landingCooldown > 0.0f)
        {
            // Exponential terminal approach begins at exactly -18 m/s² from rest, without a hard clamp.
            float terminalApproach = 1.0f - Mathf.Exp(-Mathf.Abs(Gravity / TerminalFallVelocity) * delta);
            Velocity = new Vector3(Velocity.X, Mathf.Lerp(Velocity.Y, TerminalFallVelocity, terminalApproach), Velocity.Z);
            if (grounded)
                _landingCooldown = Mathf.Max(0.0f, _landingCooldown - delta);
            if (!grounded && State is not MovementState.Jump)
                SetState(MovementState.Fall);
            return;
        }

        Velocity = new Vector3(Velocity.X, SettledGroundVelocity, Velocity.Z);
    }

    private void UpdateGroundMovement(float delta, Vector2 moveInput, Vector3 moveDirection, bool crouchHeld)
    {
        if (Input.IsActionJustPressed("crouch") && State == MovementState.Sprint)
        {
            StartSlide(moveDirection);
            return;
        }

        bool canJump = State is MovementState.Idle or MovementState.Walk or MovementState.Sprint or MovementState.Crouch or MovementState.Aim;
        if (canJump && Input.IsActionJustPressed("jump"))
        {
            Velocity = new Vector3(Velocity.X, JumpVelocity, Velocity.Z);
            SetState(MovementState.Jump);
            return;
        }

        bool hasMovement = moveInput.LengthSquared() > 0.001f;
        bool forwardish = moveInput.Y < -0.1f;
        bool sprinting = !crouchHeld && forwardish && Input.IsActionPressed("sprint");
        bool aiming = Input.IsActionPressed("aim") && !sprinting;
        float targetSpeed = crouchHeld ? CrouchSpeed : sprinting ? SprintSpeed : WalkSpeed;
        ApplyHorizontalVelocity(moveDirection * targetSpeed, delta, 1.0f);

        SetState(aiming ? MovementState.Aim : crouchHeld
            ? (hasMovement ? MovementState.CrouchWalk : MovementState.Crouch)
            : sprinting && hasMovement ? MovementState.Sprint
            : hasMovement ? MovementState.Walk
            : MovementState.Idle);
    }

    private void UpdateAirMovement(float delta, Vector3 moveDirection)
    {
        // Air has no independent speed limit; it only receives reduced acceleration toward input velocity.
        ApplyHorizontalVelocity(moveDirection * WalkSpeed, delta, AirControlMultiplier);
        SetState(Velocity.Y > 0.0f ? MovementState.Jump : MovementState.Fall);
    }

    private void StartSlide(Vector3 moveDirection)
    {
        Vector3 horizontal = new Vector3(Velocity.X, 0.0f, Velocity.Z);
        Vector3 direction = horizontal.LengthSquared() > 0.001f ? horizontal.Normalized()
            : moveDirection.LengthSquared() > 0.001f ? moveDirection : -Transform.Basis.Z;
        float speed = Mathf.Min(horizontal.Length() * SlideSpeedMultiplier, MaximumSlideSpeed);
        _slideVelocity = direction * speed;
        SetState(MovementState.Slide);
    }

    private void UpdateSlide(float delta, Vector3 moveDirection, bool crouchHeld)
    {
        float speed = _slideVelocity.Length();
        if (moveDirection.LengthSquared() > 0.001f && speed > 0.001f)
        {
            // Thirty-percent-strength steering retains the slide's existing heading.
            Vector3 steeredDirection = _slideVelocity.Normalized().Lerp(moveDirection, 0.3f * delta * 10.0f).Normalized();
            _slideVelocity = steeredDirection * speed;
        }

        speed = Mathf.MoveToward(speed, 0.0f, SlideFriction * delta);
        _slideVelocity = _slideVelocity.Normalized() * speed;
        Velocity = new Vector3(_slideVelocity.X, Velocity.Y, _slideVelocity.Z);

        if (speed < CrouchSpeed)
            SetState(crouchHeld ? MovementState.Crouch : MovementState.Idle);
    }

    private void ApplyHorizontalVelocity(Vector3 targetVelocity, float delta, float accelerationMultiplier)
    {
        Vector3 horizontal = new(Velocity.X, 0.0f, Velocity.Z);
        float transitionSpeed = Mathf.Max(horizontal.Length(), targetVelocity.Length()) / SpeedTransitionSeconds;
        horizontal = horizontal.MoveToward(targetVelocity, transitionSpeed * accelerationMultiplier * delta);
        Velocity = new Vector3(horizontal.X, Velocity.Y, horizontal.Z);
    }

    private void UpdateColliderAndHead(float delta, bool crouching)
    {
        float targetHeight = crouching ? CrouchedCapsuleHeight : StandingCapsuleHeight;
        _capsule.Height = Mathf.MoveToward(_capsule.Height, targetHeight, (StandingCapsuleHeight - CrouchedCapsuleHeight) * delta / HeightTransitionSeconds);
        _collisionShape.Position = new Vector3(0.0f, _capsule.Height * 0.5f, 0.0f);
        _head.Position = new Vector3(0.0f, _capsule.Height - 0.2f, 0.0f);
    }

    private void SetState(MovementState state)
    {
        if (State == state) return;
        State = state;
        _animationController.SetMovementState(state);
    }
}
