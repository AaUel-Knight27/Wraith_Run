using System;
using System.Collections.Generic;
using Godot;

/// <summary>Static range plate that accepts the same hitscan path as game targets.</summary>
public partial class ShootingTarget : StaticBody3D, IShootable
{
	private readonly List<MeshInstance3D> _meshes = new();
	private StandardMaterial3D? _normalMaterial;
	private StandardMaterial3D? _flashMaterial;
	private float _flashRemaining;

	public int LaneIndex { get; private set; }
	public event Action<int>? Hit;
	public bool IsFleshTarget => false;

	public void Configure(int laneIndex, StandardMaterial3D normalMaterial, StandardMaterial3D flashMaterial)
	{
		LaneIndex = laneIndex;
		_normalMaterial = normalMaterial;
		_flashMaterial = flashMaterial;
	}

	public void RegisterMesh(MeshInstance3D mesh) => _meshes.Add(mesh);

	public override void _Process(double delta)
	{
		if (_flashRemaining <= 0.0f) return;
		_flashRemaining -= (float)delta;
		if (_flashRemaining > 0.0f) return;
		foreach (var mesh in _meshes) mesh.MaterialOverride = _normalMaterial;
		SetProcess(false);
	}

	public bool IsHeadHit(Vector3 worldPoint) => false;

	public void ReceiveShot(ShotInfo shot)
	{
		foreach (var mesh in _meshes) mesh.MaterialOverride = _flashMaterial;
		_flashRemaining = 0.22f;
		SetProcess(true);
		Hit?.Invoke(LaneIndex);
	}

	public void ResetTarget()
	{
		_flashRemaining = 0.0f;
		foreach (var mesh in _meshes) mesh.MaterialOverride = _normalMaterial;
		SetProcess(false);
	}
}
