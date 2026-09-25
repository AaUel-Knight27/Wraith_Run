using Godot;
using System;

/// <summary>
/// Local-only player locomotion. Network prediction/synchronisation belongs in a later layer.
/// </summary>
public partial class PlayerMovement : CharacterBody3D
{
    public enum MovementState { Idle, Walk, Sprint, Crouch, CrouchWalk, Slide, Jump, Fall, Aim }

    // These three used to be `const`. They are now per-instance so an equipped operator's
    // CharacterEffectIds.MoveSpeedMult / CrouchWalkSpeedMult can scale them in _Ready() below -
    // everything that reads WalkSpeed/SprintSpeed/CrouchSpeed elsewhere in this file is
    // unaffected, since it was already reading through the field/property, not the literal.
    [Export] public float WalkSpeed { get; set; } = 4.7f;
    [Export] public float SprintSpeed { get; set; } = 7.2f;
    [Export] public float CrouchSpeed { get; set; } = 2.3f;
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

    // Optional first-person feelers (the TODO these replace). All tuned to read as subtle rather
    // than disorienting - they are meant to sell the movement, not to distract from the shootout.
    private const float HeadBobTransitionSeconds = 0.15f;
    private const float HeadBobFrequencyWalk = 2.1f;
    private const float HeadBobFrequencySprint = 2.8f;
    private const float HeadBobAmplitudeWalk = 0.045f;
    private const float HeadBobAmplitudeSprint = 0.028f;
    private const float CameraShakeDecayPerSecond = 6.0f;
    private const float LandingShakeStrength = 0.08f;
    private const float SlideStopShakeStrength = 0.12f;

    [Export] public float MouseSensitivity { get; set; } = 0.0025f;
    [Export] public float CameraPitchLimitDegrees { get; set; } = 85.0f;

    // FOV is exported rather than const so it can be tuned in the inspector while the game runs.
    // These are VERTICAL degrees: the camera is set to KEEP_HEIGHT, which is both Godot's default
    // and the axis Unity's Camera.fieldOfView used, so the values carried over from the Unity
    // build mean the same thing here. Horizontal FOV is derived from the window aspect, so a
    // non-16:9 window will stretch these - that is a window problem, not a camera problem.
    [Export] public float BaseFov { get; set; } = 75.0f;
    [Export] public float SprintFov { get; set; } = 90.0f;
    [Export] public float AimFov { get; set; } = 55.0f;
    [Export] public float FovTransitionSeconds { get; set; } = 0.15f;

    public MovementState State { get; private set; } = MovementState.Fall;
    /// <summary>
    /// Aiming down sights. Deliberately NOT a MovementState: it used to be one, and that is what
    /// broke ADS. As a state it was mutually exclusive with Crouch/Walk/Jump, so holding aim while
    /// crouched silently stood the capsule back up, holding it while walking froze the locomotion
    /// animation, and holding it in the air did nothing at all because the airborne branch never
    /// assigned it. FOV now reads this flag, which is evaluated every frame regardless of state.
    /// </summary>
    public bool IsAiming { get; private set; }
    // These are intentionally replicated by MultiplayerSynchronizer.  Movement remains client-authoritative.
    [Export] public string ReplicatedMovementState { get; set; } = MovementState.Idle.ToString();
    [Export] public bool ReplicatedAimState { get; set; }

    private CollisionShape3D _collisionShape = null!;
    private ShapeCast3D _groundProbe = null!;
    private Node3D _head = null!;
    private Camera3D _camera = null!;
    private PlayerAnimationController _animationController = null!;
    private Health? _health;
    private CharacterLoadout? _loadout;
    private CapsuleShape3D _capsule = null!;
    private Vector3 _slideVelocity;
    private float _pitch;
    private float _landingCooldown;
    // Starts true: the player spawns on the floor, so the first grounded frame is not a "landing".
    // A real landing is a transition from airborne to grounded, which only happens after _wasGrounded
    // has been set false by a frame where the player was in the air.
    private bool _wasGrounded = true;
    private float _bobPhase;
    private float _bobAmplitude;
    private Vector3 _headOffset;
    private Vector3 _shakeOffset;
    // Eye height before bob/shake are layered on. Kept as a field so _head.Position is written
    // exactly once per frame; it used to be assigned twice, with the second write reading back the
    // first one's Y and dropping the base X/Z entirely.
    private float _headBaseHeight;
    // Recoil is a decaying offset layered on top of the mouse-driven pitch, not a change to _pitch
    // itself. Keeping them separate means the mouse still owns where the player is aiming and the
    // kick simply rides on top of it, so nothing has to be "given back" when the kick decays.
    private float _recoilPitchDegrees;
    private float _recoilYawDegrees;
    private float _recoilRecoveryPerSecond = 20.0f;

    public override void _Ready()
    {
        _collisionShape = GetNode<CollisionShape3D>("CollisionShape3D");
        _groundProbe = GetNode<ShapeCast3D>("GroundProbe");
        _head = GetNode<Node3D>("Head");
        _camera = GetNode<Camera3D>("Head/FirstPersonCamera");
        _animationController = GetNode<PlayerAnimationController>("AnimationController");
        _health = GetNodeOrNull<Health>("Health");
        _loadout = GetNodeOrNull<CharacterLoadout>("CharacterLoadout");

        // Interaction Drill's "-15% sprint stamina" aside (no stamina meter exists yet, see
        // CharacterEffectIds.SprintStaminaMult), MoveSpeedMult / CrouchWalkSpeedMult are the two
        // roster effects a real system already reads. No operator equipped -> both default to
        // 1.0 -> WalkSpeed/SprintSpeed/CrouchSpeed stay exactly what they are today.
        float moveSpeedMult = _loadout?.GetModifier(CharacterEffectIds.MoveSpeedMult, 1.0f) ?? 1.0f;
        float crouchMult = _loadout?.GetModifier(CharacterEffectIds.CrouchWalkSpeedMult, 1.0f) ?? 1.0f;
        WalkSpeed *= moveSpeedMult;
        SprintSpeed *= moveSpeedMult;
        CrouchSpeed *= moveSpeedMult * crouchMult;

        _capsule = (CapsuleShape3D)_collisionShape.Shape;
        _headBaseHeight = _head.Position.Y;
        _camera.Fov = BaseFov;
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
            // The rotation itself is committed in _PhysicsProcess (see UpdateHeadAim) because the
            // recoil offset has to decay on a fixed step, not only when the mouse happens to move.
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

        // Decay the shake first, before any new shake is set this frame. Decaying after the assignment
        // would zero a fresh shake in the same frame it was created and the shake would never show.
        UpdateCameraShake(delta);

        if (grounded && !_wasGrounded)
            {
                _landingCooldown = LandingCooldownSeconds;
                _shakeOffset = new Vector3(0.0f, LandingShakeStrength, 0.0f);
            }

        UpdateVerticalVelocity(delta, grounded);

        if (State == MovementState.Slide)
            UpdateSlide(delta, moveDirection, crouchHeld);
        else if (grounded)
            UpdateGroundMovement(delta, moveInput, moveDirection, crouchHeld);
        else
            UpdateAirMovement(delta, moveDirection);

        UpdateColliderAndHead(delta, State is MovementState.Crouch or MovementState.CrouchWalk or MovementState.Slide);
        UpdateHeadBob(delta);
        // Sprinting and sliding cancel ADS; every other state - including crouch and airborne -
        // allows it, which is the whole point of pulling this out of the state machine.
        IsAiming = Input.IsActionPressed("aim")
            && State is not MovementState.Sprint and not MovementState.Slide;
        UpdateFov(delta);
        UpdateHeadAim(delta);
        _head.Position = new Vector3(_headOffset.X, _headBaseHeight + _headOffset.Y, _headOffset.Z)
            + _shakeOffset;
        MoveAndSlide();
        _wasGrounded = IsOnFloor() || _groundProbe.IsColliding();
        ReplicatedMovementState = State.ToString();
        ReplicatedAimState = IsAiming;
        _animationController.SetMovementState(State);
    }

    /// <summary>
    /// Adds a recoil kick. Positive pitch throws the view upward; yaw is signed, so the caller
    /// should randomise it. recoveryMs is how long the kick takes to wash out.
    /// </summary>
    public void AddRecoil(float pitchDegrees, float yawDegrees, float recoveryMs)
    {
        _recoilPitchDegrees += pitchDegrees;
        _recoilYawDegrees += yawDegrees;
        _recoilRecoveryPerSecond = recoveryMs > 1.0f ? 1000.0f / recoveryMs : 20.0f;
    }

    /// <summary>
    /// Commits pitch + recoil to the Head pivot. Recoil lands on Head rather than on the camera
    /// alone so it moves the real point of aim - the weapon's hitscan ray is cast from the camera,
    /// which is a child of Head.
    /// </summary>
    private void UpdateHeadAim(float delta)
    {
        float decay = Mathf.Exp(-_recoilRecoveryPerSecond * delta);
        _recoilPitchDegrees *= decay;
        _recoilYawDegrees *= decay;
        if (Mathf.Abs(_recoilPitchDegrees) < 0.001f) _recoilPitchDegrees = 0.0f;
        if (Mathf.Abs(_recoilYawDegrees) < 0.001f) _recoilYawDegrees = 0.0f;

        // Clamped again after the kick is added: without this a long burst could throw the camera
        // past vertical and flip the view.
        float limit = Mathf.DegToRad(CameraPitchLimitDegrees);
        float pitch = Mathf.Clamp(_pitch + Mathf.DegToRad(_recoilPitchDegrees), -limit, limit);
        _head.Rotation = new Vector3(pitch, Mathf.DegToRad(_recoilYawDegrees), 0.0f);
    }

    private void UpdateFov(float delta)
    {
        float targetFov = IsAiming ? AimFov
            : State == MovementState.Sprint ? SprintFov
            : BaseFov;
        // Step is expressed as "full zoom range per FovTransitionSeconds" so the timing stays the
        // same if the FOV values are retuned. The old hardcoded 20 deg/0.15 s silently changed the
        // transition duration whenever the numbers moved.
        float range = Mathf.Max(Mathf.Abs(BaseFov - AimFov), Mathf.Abs(SprintFov - BaseFov));
        float step = range * delta / Mathf.Max(FovTransitionSeconds, 0.001f);
        _camera.Fov = Mathf.MoveToward(_camera.Fov, targetFov, step);
    }

    private void UpdateHeadBob(float delta)
    {
        bool moving = State is MovementState.Walk or MovementState.Sprint;
        float targetAmplitude = moving
            ? (State == MovementState.Sprint ? HeadBobAmplitudeSprint : HeadBobAmplitudeWalk)
            : 0.0f;
        // Fixed step in amplitude-per-second. The earlier version used targetAmplitude * delta /
        // HeadBobTransitionSeconds as the step, which collapses to zero when the target is zero -
        // so MoveToward returned the current value and the bob never faded out after you stopped.
        float step = delta / HeadBobTransitionSeconds;
        _bobAmplitude = Mathf.MoveToward(_bobAmplitude, targetAmplitude, step);
        if (_bobAmplitude <= 0.0001f)
        {
            _headOffset = Vector3.Zero;
            return;
        }
        float frequency = State == MovementState.Sprint ? HeadBobFrequencySprint : HeadBobFrequencyWalk;
        _bobPhase += delta * frequency;
        float wave = Mathf.Sin(_bobPhase);
        float bobY = _bobAmplitude * wave;
        float bobZ = _bobAmplitude * 0.5f * Mathf.Cos(_bobPhase * 0.5f);
        _headOffset = new Vector3(0.0f, bobY, bobZ);
    }

    private void UpdateCameraShake(float delta)
    {
        if (_shakeOffset.LengthSquared() <= 0.000001f) return;
        // Exponential decay, not MoveToward: with a fixed per-second rate the shake survives
        // regardless of frame rate, whereas a MoveToward step of rate*delta exceeds the shake
        // magnitude at normal framerates and killed it in the same frame it was created.
        float factor = Mathf.Exp(-CameraShakeDecayPerSecond * delta);
        _shakeOffset *= factor;
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
        float targetSpeed = crouchHeld ? CrouchSpeed : sprinting ? SprintSpeed : WalkSpeed;
        ApplyHorizontalVelocity(moveDirection * targetSpeed, delta, 1.0f);

        // MovementState.Aim survives only as an ANIMATION state, and only when it has no real
        // locomotion to overwrite: standing still, upright, on the ground. Crouching or walking
        // while aiming now keeps the crouch/walk clip (and the crouched capsule) and lets the FOV
        // change alone sell the ADS.
        SetState(crouchHeld
            ? (hasMovement ? MovementState.CrouchWalk : MovementState.Crouch)
            : sprinting && hasMovement ? MovementState.Sprint
            : hasMovement ? MovementState.Walk
            : Input.IsActionPressed("aim") ? MovementState.Aim
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
            {
                if (State == MovementState.Slide)
                    _shakeOffset = new Vector3(SlideStopShakeStrength, 0.0f, 0.0f);
                SetState(crouchHeld ? MovementState.Crouch : MovementState.Idle);
            }
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
        _headBaseHeight = _capsule.Height - 0.2f;
    }

    
    private void SetState(MovementState state)
    {
        if (State == state) return;
        State = state;
        _animationController.SetMovementState(state);
    }
}
