using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// A 2D walkability grid of the arena, built from the real physics colliders when the match starts, so
/// bots work on any map without hand-placed waypoints or a baked navmesh.
///
/// Each cell is probed twice: a short ray finds the floor under it (rejects pits and void), then two
/// cell-sized boxes test the body-height band for walls, props and low ceilings. The probe deliberately
/// looks only between knee and head height, so roof slabs above the play space do not turn the ground
/// beneath them into "obstacles". Walkable cells are then shrunk by the body radius, and only the largest
/// connected region is kept, so a bot can never be sent to an unreachable pocket.
///
/// It also answers the tactical questions bots ask: A* path, line of sight (aware of low cover that only
/// blocks crouched sightlines), and "where can I spawn / take cover".
/// </summary>
public sealed class NavGrid
{
	public const float StepHeight = 0.35f;
	private const float LowBandLo = 0.35f;
	private const float LowBandHi = 1.0f;
	private const float HighBandHi = 1.85f;
	private const int MaxAStarIterations = 40000;

	public readonly float Cell;
	public readonly int Width;
	public readonly int Depth;
	private readonly float _minX;
	private readonly float _minZ;

	private readonly bool[] _solid;
	private readonly bool[] _blocked;
	private readonly byte[] _level;      // 0 open, 1 low cover (blocks below ~1 m), 2 tall (blocks eye level)
	private readonly float[] _groundY;
	private int[] _walkable = Array.Empty<int>();

	private readonly float[] _g;
	private readonly int[] _parent;
	private readonly int[] _seen;
	private readonly int[] _closed;
	private int _stamp;
	private readonly PriorityQueue<int, float> _open = new PriorityQueue<int, float>();
	private readonly List<int> _rawCells = new List<int>();

	public int WalkableCount => _walkable.Length;

	private NavGrid(int width, int depth, float cell, float minX, float minZ)
	{
		Width = width;
		Depth = depth;
		Cell = cell;
		_minX = minX;
		_minZ = minZ;
		int n = width * depth;
		_solid = new bool[n];
		_blocked = new bool[n];
		_level = new byte[n];
		_groundY = new float[n];
		_g = new float[n];
		_parent = new int[n];
		_seen = new int[n];
		_closed = new int[n];
	}

	// ------------------------------------------------------------------------------------------------
	// Build
	// ------------------------------------------------------------------------------------------------

	public static NavGrid Build(PhysicsDirectSpaceState3D space, Godot.Collections.Array<Rid> exclude,
		Vector2 center, float halfExtent, float cell)
	{
		int n = Mathf.CeilToInt(halfExtent * 2.0f / cell);
		var grid = new NavGrid(n, n, cell, center.X - n * cell * 0.5f, center.Y - n * cell * 0.5f);

		float footprint = cell * 0.8f;
		var lowShape = new BoxShape3D { Size = new Vector3(footprint, LowBandHi - LowBandLo, footprint) };
		var highShape = new BoxShape3D { Size = new Vector3(footprint, HighBandHi - LowBandHi, footprint) };
		var lowQuery = new PhysicsShapeQueryParameters3D { Shape = lowShape, CollideWithBodies = true, CollideWithAreas = false, Exclude = exclude };
		var highQuery = new PhysicsShapeQueryParameters3D { Shape = highShape, CollideWithBodies = true, CollideWithAreas = false, Exclude = exclude };

		for (int z = 0; z < n; z++)
		{
			for (int x = 0; x < n; x++)
			{
				int i = z * n + x;
				Vector3 c = grid.CellCenter(x, z);

				var ray = PhysicsRayQueryParameters3D.Create(new Vector3(c.X, 1.0f, c.Z), new Vector3(c.X, -3.0f, c.Z), 0xFFFFFFFF, exclude);
				var hit = space.IntersectRay(ray);
				bool ground = false;
				float gy = 0.0f;
				if (hit.Count > 0)
				{
					gy = ((Vector3)hit["position"]).Y;
					ground = gy >= -0.4f && gy <= StepHeight;
				}

				if (!ground)
				{
					grid._solid[i] = true;
					grid._level[i] = 2;
					continue;
				}

				grid._groundY[i] = gy;
				lowQuery.Transform = new Transform3D(Basis.Identity, new Vector3(c.X, gy + (LowBandLo + LowBandHi) * 0.5f, c.Z));
				highQuery.Transform = new Transform3D(Basis.Identity, new Vector3(c.X, gy + (LowBandHi + HighBandHi) * 0.5f, c.Z));
				bool low = space.IntersectShape(lowQuery, 1).Count > 0;
				bool high = space.IntersectShape(highQuery, 1).Count > 0;
				if (low || high)
				{
					grid._solid[i] = true;
					grid._level[i] = high ? (byte)2 : (byte)1;
				}
			}
		}

		grid.Finish();
		return grid;
	}

	private void Finish()
	{
		// Shrink by one cell (== 0.5 m at the default resolution) so a 0.35 m radius body clears corners.
		for (int z = 0; z < Depth; z++)
		{
			for (int x = 0; x < Width; x++)
			{
				int i = z * Width + x;
				if (_solid[i]) { _blocked[i] = true; continue; }
				bool near = false;
				for (int dz = -1; dz <= 1 && !near; dz++)
				{
					for (int dx = -1; dx <= 1; dx++)
					{
						int nx = x + dx, nz = z + dz;
						if (nx < 0 || nz < 0 || nx >= Width || nz >= Depth || _solid[nz * Width + nx]) { near = true; break; }
					}
				}
				_blocked[i] = near;
			}
		}

		KeepLargestRegion();

		var list = new List<int>();
		for (int i = 0; i < _blocked.Length; i++)
			if (!_blocked[i]) list.Add(i);
		_walkable = list.ToArray();
	}

	private void KeepLargestRegion()
	{
		int n = _blocked.Length;
		var label = new int[n];
		for (int i = 0; i < n; i++) label[i] = -1;
		var sizes = new List<int>();
		var stack = new Stack<int>();
		for (int start = 0; start < n; start++)
		{
			if (_blocked[start] || label[start] >= 0) continue;
			int id = sizes.Count;
			int count = 0;
			label[start] = id;
			stack.Push(start);
			while (stack.Count > 0)
			{
				int cur = stack.Pop();
				count++;
				int cx = cur % Width, cz = cur / Width;
				TryFlood(cx + 1, cz, id, label, stack);
				TryFlood(cx - 1, cz, id, label, stack);
				TryFlood(cx, cz + 1, id, label, stack);
				TryFlood(cx, cz - 1, id, label, stack);
			}
			sizes.Add(count);
		}
		if (sizes.Count <= 1) return;
		int best = 0;
		for (int i = 1; i < sizes.Count; i++)
			if (sizes[i] > sizes[best]) best = i;
		for (int i = 0; i < n; i++)
			if (!_blocked[i] && label[i] != best) _blocked[i] = true;
	}

	private void TryFlood(int x, int z, int id, int[] label, Stack<int> stack)
	{
		if (x < 0 || z < 0 || x >= Width || z >= Depth) return;
		int i = z * Width + x;
		if (_blocked[i] || label[i] >= 0) return;
		label[i] = id;
		stack.Push(i);
	}

	// ------------------------------------------------------------------------------------------------
	// Coordinates
	// ------------------------------------------------------------------------------------------------

	public Vector3 CellCenter(int x, int z) =>
		new Vector3(_minX + (x + 0.5f) * Cell, 0.0f, _minZ + (z + 0.5f) * Cell);

	private bool ToCell(Vector3 p, out int x, out int z)
	{
		x = Mathf.FloorToInt((p.X - _minX) / Cell);
		z = Mathf.FloorToInt((p.Z - _minZ) / Cell);
		return x >= 0 && z >= 0 && x < Width && z < Depth;
	}

	private Vector3 CenterOf(int index)
	{
		int x = index % Width, z = index / Width;
		Vector3 c = CellCenter(x, z);
		c.Y = _groundY[index];
		return c;
	}

	public bool IsWalkable(Vector3 p) => ToCell(p, out int x, out int z) && !_blocked[z * Width + x];

	public float GroundY(Vector3 p) => ToCell(p, out int x, out int z) ? _groundY[z * Width + x] : 0.0f;

	/// <summary>Nearest walkable cell centre within a few metres (or the cell itself if already walkable).</summary>
	public bool TryNearest(Vector3 p, out Vector3 result)
	{
		result = p;
		if (!SnapToWalkable(p, out int x, out int z)) return false;
		result = CenterOf(z * Width + x);
		return true;
	}

	private bool SnapToWalkable(Vector3 p, out int cx, out int cz)
	{
		cx = 0; cz = 0;
		int ox = Mathf.FloorToInt((p.X - _minX) / Cell);
		int oz = Mathf.FloorToInt((p.Z - _minZ) / Cell);
		if (ox >= 0 && oz >= 0 && ox < Width && oz < Depth && !_blocked[oz * Width + ox])
		{
			cx = ox; cz = oz;
			return true;
		}
		float bestD = float.MaxValue;
		bool found = false;
		for (int r = 1; r <= 12; r++)
		{
			for (int dz = -r; dz <= r; dz++)
			{
				for (int dx = -r; dx <= r; dx++)
				{
					if (Math.Abs(dx) != r && Math.Abs(dz) != r) continue;
					int x = ox + dx, z = oz + dz;
					if (x < 0 || z < 0 || x >= Width || z >= Depth || _blocked[z * Width + x]) continue;
					float d = dx * dx + dz * dz;
					if (d < bestD) { bestD = d; cx = x; cz = z; found = true; }
				}
			}
			if (found) return true;
		}
		return false;
	}

	// ------------------------------------------------------------------------------------------------
	// Queries
	// ------------------------------------------------------------------------------------------------

	public Vector3 RandomWalkable(RandomNumberGenerator rng)
	{
		if (_walkable.Length == 0) return Vector3.Zero;
		return CenterOf(_walkable[rng.RandiRange(0, _walkable.Length - 1)]);
	}

	public bool RandomWalkableNear(Vector3 center, float minRadius, float maxRadius, RandomNumberGenerator rng, out Vector3 result)
	{
		for (int attempt = 0; attempt < 14; attempt++)
		{
			float angle = rng.RandfRange(0.0f, Mathf.Tau);
			float radius = rng.RandfRange(minRadius, maxRadius);
			var p = new Vector3(center.X + Mathf.Cos(angle) * radius, 0.0f, center.Z + Mathf.Sin(angle) * radius);
			if (ToCell(p, out int x, out int z) && !_blocked[z * Width + x])
			{
				result = CenterOf(z * Width + x);
				return true;
			}
		}
		result = center;
		return false;
	}

	/// <summary>
	/// Random walkable spot that is as far as possible from everyone in <paramref name="avoid"/> (best of
	/// ~40 random candidates), so bots start scattered across the map instead of clumped together.
	/// </summary>
	public Vector3 PickSpawn(IList<Vector3> avoid, float minDistance, RandomNumberGenerator rng)
	{
		Vector3 best = RandomWalkable(rng);
		float bestScore = -1.0f;
		for (int i = 0; i < 40; i++)
		{
			Vector3 c = RandomWalkable(rng);
			float nearest = 1000.0f;
			for (int k = 0; k < avoid.Count; k++)
			{
				float dx = c.X - avoid[k].X, dz = c.Z - avoid[k].Z;
				nearest = Mathf.Min(nearest, Mathf.Sqrt(dx * dx + dz * dz));
			}
			float score = Mathf.Min(nearest, minDistance * 2.0f);
			if (nearest >= minDistance) score += 100.0f;
			if (score > bestScore) { bestScore = score; best = c; }
		}
		return best;
	}

	/// <summary>Which coarse square (divisions x divisions over the whole grid) a point falls in.</summary>
	public int Sector(Vector3 p, int divisions)
	{
		float fx = (p.X - _minX) / (Width * Cell);
		float fz = (p.Z - _minZ) / (Depth * Cell);
		int sx = Mathf.Clamp((int)(fx * divisions), 0, divisions - 1);
		int sz = Mathf.Clamp((int)(fz * divisions), 0, divisions - 1);
		return sz * divisions + sx;
	}

	/// <summary>0 = wide open, 1 = boxed in. Counts solid cells within <paramref name="radiusCells"/>.</summary>
	public float Enclosure(Vector3 p, int radiusCells)
	{
		if (!ToCell(p, out int cx, out int cz)) return 0.0f;
		int solid = 0, total = 0;
		for (int dz = -radiusCells; dz <= radiusCells; dz++)
		{
			for (int dx = -radiusCells; dx <= radiusCells; dx++)
			{
				int x = cx + dx, z = cz + dz;
				total++;
				if (x < 0 || z < 0 || x >= Width || z >= Depth || _solid[z * Width + x]) solid++;
			}
		}
		return total == 0 ? 0.0f : (float)solid / total;
	}

	/// <summary>
	/// Grid line of sight between two eye/aim heights. Tall obstacles always block; low cover (crates,
	/// half walls) only blocks lines that pass below about a metre, matching how a crouched player hides.
	/// </summary>
	public bool HasLos(Vector3 a, float heightA, Vector3 b, float heightB)
	{
		float dx = b.X - a.X, dz = b.Z - a.Z;
		float dist = Mathf.Sqrt(dx * dx + dz * dz);
		if (dist < 0.01f) return true;
		float stepLen = Cell * 0.5f;
		int steps = Mathf.Max(1, Mathf.CeilToInt(dist / stepLen));
		for (int s = 1; s < steps; s++)
		{
			float t = (float)s / steps;
			var p = new Vector3(a.X + dx * t, 0.0f, a.Z + dz * t);
			if (!ToCell(p, out int x, out int z)) return false;
			byte level = _level[z * Width + x];
			if (level == 0) continue;
			if (level >= 2) return false;
			float h = Mathf.Lerp(heightA, heightB, t);
			if (h < LowBandHi) return false;
		}
		return true;
	}

	/// <summary>True if a body can walk the straight segment without touching an (inflated) obstacle.</summary>
	public bool WalkableLine(Vector3 a, Vector3 b)
	{
		float dx = b.X - a.X, dz = b.Z - a.Z;
		float dist = Mathf.Sqrt(dx * dx + dz * dz);
		int steps = Mathf.Max(1, Mathf.CeilToInt(dist / (Cell * 0.5f)));
		for (int s = 0; s <= steps; s++)
		{
			float t = (float)s / steps;
			var p = new Vector3(a.X + dx * t, 0.0f, a.Z + dz * t);
			if (!ToCell(p, out int x, out int z) || _blocked[z * Width + x]) return false;
		}
		return true;
	}

	// ------------------------------------------------------------------------------------------------
	// A*
	// ------------------------------------------------------------------------------------------------

	private static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
	private static readonly int[] Dz = { 0, 0, 1, -1, 1, -1, 1, -1 };

	private float Heuristic(int index, int goalX, int goalZ)
	{
		int x = index % Width, z = index / Width;
		int ax = Math.Abs(x - goalX), az = Math.Abs(z - goalZ);
		return (ax + az) + (1.4142f - 2.0f) * Math.Min(ax, az);
	}

	/// <summary>Shortest smoothed route. <paramref name="path"/> receives waypoints (start excluded).</summary>
	public bool FindPath(Vector3 from, Vector3 to, List<Vector3> path)
	{
		path.Clear();
		if (!SnapToWalkable(from, out int sx, out int sz)) return false;
		if (!SnapToWalkable(to, out int gx, out int gz)) return false;
		int start = sz * Width + sx;
		int goal = gz * Width + gx;
		if (start == goal)
		{
			path.Add(CenterOf(goal));
			return true;
		}

		_stamp++;
		_open.Clear();
		_g[start] = 0.0f;
		_parent[start] = -1;
		_seen[start] = _stamp;
		_open.Enqueue(start, Heuristic(start, gx, gz));

		int iterations = 0;
		bool reached = false;
		while (_open.TryDequeue(out int cur, out float _))
		{
			if (cur == goal) { reached = true; break; }
			if (_closed[cur] == _stamp) continue;
			_closed[cur] = _stamp;
			if (++iterations > MaxAStarIterations) break;

			int cx = cur % Width, cz = cur / Width;
			for (int k = 0; k < 8; k++)
			{
				int nx = cx + Dx[k], nz = cz + Dz[k];
				if (nx < 0 || nz < 0 || nx >= Width || nz >= Depth) continue;
				int ni = nz * Width + nx;
				if (_blocked[ni]) continue;
				bool diagonal = Dx[k] != 0 && Dz[k] != 0;
				if (diagonal && (_blocked[cz * Width + nx] || _blocked[nz * Width + cx])) continue;
				float ng = _g[cur] + (diagonal ? 1.4142f : 1.0f);
				if (_seen[ni] != _stamp || ng < _g[ni])
				{
					_seen[ni] = _stamp;
					_g[ni] = ng;
					_parent[ni] = cur;
					_open.Enqueue(ni, ng + Heuristic(ni, gx, gz));
				}
			}
		}
		if (!reached) return false;

		_rawCells.Clear();
		for (int c = goal; c != -1; c = _parent[c]) _rawCells.Add(c);
		_rawCells.Reverse();

		int anchor = 0;
		int last = _rawCells.Count - 1;
		while (anchor < last)
		{
			int far = last;
			Vector3 a = CenterOf(_rawCells[anchor]);
			while (far > anchor + 1 && !WalkableLine(a, CenterOf(_rawCells[far]))) far--;
			path.Add(CenterOf(_rawCells[far]));
			anchor = far;
		}
		return path.Count > 0;
	}
}
