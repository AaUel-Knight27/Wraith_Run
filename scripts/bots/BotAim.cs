using Godot;
using System;

/// <summary>
/// Human-like mouse hand. Instead of snapping the crosshair onto a target it simulates a hand: an
/// acceleration-limited controller that flicks fast toward a far target, brakes a little late (so flicks
/// overshoot on weak bots), lands with a small error, then corrects; while tracking it adds slow hand
/// wobble. Skill levels are just different parameter sets (see BotProfile).
///
/// Angles are radians: yaw is positive to the LEFT (same as Node3D.RotateY), pitch positive UP.
/// This class only produces the angle deltas to apply this frame; BotBrain feeds them to PlayerMovement.
/// </summary>
public sealed class BotAim
{
	private readonly RandomNumberGenerator _rng;
	private float _vy, _vp;
	private float _noiseY, _noiseP;
	private float _flickY, _flickP;
	private bool _flicking;
	private bool _slowTurn;

	public bool IsFlicking => _flicking;

	public BotAim(RandomNumberGenerator rng) { _rng = rng; }

	public void Reset()
	{
		_vy = _vp = 0.0f;
		_noiseY = _noiseP = 0.0f;
		_flickY = _flickP = 0.0f;
		_flicking = false;
		_slowTurn = false;
	}

	public static float WrapPi(float a)
	{
		while (a > Mathf.Pi) a -= Mathf.Tau;
		while (a < -Mathf.Pi) a += Mathf.Tau;
		return a;
	}

	private float Gauss() => _rng.Randfn(0.0f, 1.0f);

	private static float AxisStep(float err, ref float v, float dt, float maxSpeed, float accel, float overconf, float tau)
	{
		float brake = accel * overconf;
		float profile = Mathf.Sign(err) * Mathf.Min(maxSpeed, Mathf.Sqrt(2.0f * brake * Mathf.Abs(err)));
		float prop = err / tau;
		float desired = Mathf.Abs(prop) < Mathf.Abs(profile) ? prop : profile;
		float step = desired - v;
		float limit = accel * dt;
		step = Mathf.Clamp(step, -limit, limit);
		v += step;
		return v * dt;
	}

	/// <param name="engaged">true while fighting (flicks + full skill); false when just looking around.</param>
	/// <param name="noiseScale">multiplier on the hand wobble (movement, ease, panic...).</param>
	public void Step(float dt, float wantYaw, float wantPitch, float curYaw, float curPitch,
		BotProfile p, bool engaged, float noiseScale, float easeAim,
		out float dYaw, out float dPitch)
	{
		float errY = WrapPi(wantYaw - curYaw);
		float errP = wantPitch - curPitch;
		float mag = Mathf.Sqrt(errY * errY + errP * errP);
		float threshold = Mathf.DegToRad(p.FlickThresholdDeg);

		// wobble (Ornstein-Uhlenbeck): smooth, mean-reverting, never a robotic constant offset
		float sigma = Mathf.DegToRad(p.HandNoiseDeg) * noiseScale;
		float tau = Mathf.Max(p.HandNoiseTau, 0.02f);
		float kick = sigma * Mathf.Sqrt(2.0f * dt / tau);
		_noiseY += (-_noiseY / tau) * dt + kick * Gauss();
		_noiseP += (-_noiseP / tau) * dt + kick * Gauss();

		float maxSpeed;
		float accel;
		float overconf;

		if (engaged)
		{
			if (!_flicking && mag > threshold)
			{
				bool doFlick = _rng.Randf() < p.FlickUse;
				_slowTurn = !doFlick;
				if (doFlick)
				{
					_flicking = true;
					float fe = Mathf.DegToRad(p.FlickErrorDeg) * easeAim;
					_flickY = Gauss() * fe;
					_flickP = Gauss() * fe * 0.6f;
				}
			}
			if (_flicking && mag < Mathf.DegToRad(4.0f)) _flicking = false;
			if (mag < Mathf.DegToRad(4.0f)) _slowTurn = false;

			if (!_flicking)
			{
				float decay = Mathf.Exp(-dt / Mathf.Max(p.CorrectionTau * easeAim, 0.02f));
				_flickY *= decay;
				_flickP *= decay;
			}

			if (_flicking)
			{
				maxSpeed = Mathf.DegToRad(p.FlickSpeedDeg);
				accel = Mathf.DegToRad(p.FlickAccelDeg);
				overconf = p.Overconfidence;
			}
			else
			{
				maxSpeed = Mathf.DegToRad(p.TrackSpeedDeg);
				accel = Mathf.DegToRad(p.FlickAccelDeg) * 0.6f;
				overconf = 1.0f;
				if (_slowTurn) maxSpeed = Mathf.Min(maxSpeed, Mathf.DegToRad(170.0f));
			}
		}
		else
		{
			_flicking = false;
			_slowTurn = false;
			float decay = Mathf.Exp(-dt / 0.2f);
			_flickY *= decay;
			_flickP *= decay;
			maxSpeed = Mathf.DegToRad(p.LookSpeedDeg);
			accel = Mathf.DegToRad(p.LookSpeedDeg) * 6.0f;
			overconf = 1.0f;
		}

		float targetY = curYaw + errY + _noiseY + _flickY;
		float targetP = wantPitch + _noiseP + _flickP;
		float trackTau = Mathf.Max(p.TrackTau, 0.02f);

		dYaw = AxisStep(WrapPi(targetY - curYaw), ref _vy, dt, maxSpeed, accel, overconf, trackTau);
		dPitch = AxisStep(targetP - curPitch, ref _vp, dt, maxSpeed, accel, overconf, trackTau);
	}
}
