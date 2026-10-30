#nullable enable
using Godot;

namespace WraithRun.MapKit;

/// <summary>
/// Ruined, hand-me-down-looking replacements for the plain box/cylinder primitives: a dead tree with
/// snapped branches, a dented barrel that may be leaning or lying down, a broken wall with window-sized
/// holes and rubble at its foot, and a battered shipping container.
///
/// Everything is emitted straight into a MeshBatcher, exactly like the old primitives, so a map gains
/// no extra draw calls. Variation is derived from the prop's position (see Rng), so re-baking a map
/// gives the same ruins every time and no two neighbours look alike.
///
/// Per-vertex colour carries the grime/rust variation. It only shows when the prop's SurfaceLayer has
/// UseVertexColor on; with it off the same shapes still work, just uniformly coloured.
///
/// All shapes sit with the origin at the middle of their base, like the primitives they replace.
/// Triangle winding is corrected automatically from the intended normal (Godot front faces are
/// clockwise), so a mistake in point order cannot produce an invisible face.
/// </summary>
internal static class ProceduralShapes
{
    // ------------------------------------------------------------------ deterministic RNG

    private struct Rng
    {
        private uint _s;

        public Rng(Vector3 p, int salt)
        {
            unchecked
            {
                int x = Mathf.RoundToInt(p.X * 37.0f);
                int y = Mathf.RoundToInt(p.Y * 37.0f);
                int z = Mathf.RoundToInt(p.Z * 37.0f);
                uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791) ^ (uint)(salt * 40503);
                h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;
                _s = h == 0 ? 0x9E3779B9u : h;
            }
        }

        /// <summary>0 (inclusive) to 1 (exclusive).</summary>
        public float Next()
        {
            unchecked
            {
                _s ^= _s << 13;
                _s ^= _s >> 17;
                _s ^= _s << 5;
            }
            return (_s & 0xFFFFFF) / 16777216.0f;
        }

        public float Range(float a, float b) => a + (b - a) * Next();
        public bool Chance(float p) => Next() < p;
        public int Int(int minInclusive, int maxInclusive) => minInclusive + Mathf.Min(maxInclusive - minInclusive, (int)(Next() * (maxInclusive - minInclusive + 1)));
    }

    // ------------------------------------------------------------------ drawing helpers

    private static Vector3 WorldNormal(Transform3D t, Vector3 localNormal)
        => (t.Basis.Inverse().Transposed() * localNormal).Normalized();

    /// <summary>
    /// A quad given in LOCAL space and transformed by t. 'localNormal' is the way it should face; the
    /// point order is flipped if needed so it really does face that way.
    /// </summary>
    private static void Quad(MeshBatcher batch, object mat, Transform3D t, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
        Vector3 localNormal, Color ca, Color cb, Color cc, Color cd, bool collide)
    {
        Vector3 pa = t * a, pb = t * b, pc = t * c, pd = t * d;
        Vector3 n = WorldNormal(t, localNormal);
        // Godot's front face is clockwise, so the front normal is the NEGATIVE of the right-hand cross.
        if ((pb - pa).Cross(pc - pa).Dot(n) > 0.0f)
        {
            (pb, pd) = (pd, pb);
            (cb, cd) = (cd, cb);
        }
        batch.AddQuad(mat,
            new BatchVertex(pa, n, MeshBatcher.ProjectUV(pa, n), ca),
            new BatchVertex(pb, n, MeshBatcher.ProjectUV(pb, n), cb),
            new BatchVertex(pc, n, MeshBatcher.ProjectUV(pc, n), cc),
            new BatchVertex(pd, n, MeshBatcher.ProjectUV(pd, n), cd),
            collide);
    }

    private static void Quad(MeshBatcher batch, object mat, Transform3D t, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
        Vector3 localNormal, Color colour, bool collide)
        => Quad(batch, mat, t, a, b, c, d, localNormal, colour, colour, colour, colour, collide);

    private static void Tri(MeshBatcher batch, object mat, Transform3D t, Vector3 a, Vector3 b, Vector3 c,
        Vector3 localNormal, Color ca, Color cb, Color cc, bool collide)
    {
        Vector3 pa = t * a, pb = t * b, pc = t * c;
        Vector3 n = WorldNormal(t, localNormal);
        if ((pb - pa).Cross(pc - pa).Dot(n) > 0.0f)
        {
            (pb, pc) = (pc, pb);
            (cb, cc) = (cc, cb);
        }
        batch.AddTriangle(mat,
            new BatchVertex(pa, n, MeshBatcher.ProjectUV(pa, n), ca),
            new BatchVertex(pb, n, MeshBatcher.ProjectUV(pb, n), cb),
            new BatchVertex(pc, n, MeshBatcher.ProjectUV(pc, n), cc),
            collide);
    }

    private static Color Mul(Color c, float k) => new(c.R * k, c.G * k, c.B * k, 1.0f);

    /// <summary>
    /// A tapered tube through the given centre points (local space). 'jag' raises each vertex of the
    /// last ring by up to that many metres along the tube, for a splintered broken end.
    /// </summary>
    private static void Tube(MeshBatcher batch, object mat, Transform3D t, Vector3[] centres, float[] radii, int sides,
        Color bottom, Color top, bool collide, bool capEnd, float jag, ref Rng rng)
    {
        int n = centres.Length;
        if (n < 2) return;

        var ring = new Vector3[n][];
        var ringDir = new Vector3[n][];
        for (int i = 0; i < n; i++)
        {
            Vector3 axis = i == 0 ? centres[1] - centres[0]
                : i == n - 1 ? centres[n - 1] - centres[n - 2]
                : centres[i + 1] - centres[i - 1];
            axis = axis.LengthSquared() > 1e-8f ? axis.Normalized() : Vector3.Up;
            Vector3 helper = Mathf.Abs(axis.Y) < 0.95f ? Vector3.Up : Vector3.Right;
            Vector3 u = axis.Cross(helper).Normalized();
            Vector3 v = axis.Cross(u).Normalized();

            ring[i] = new Vector3[sides];
            ringDir[i] = new Vector3[sides];
            for (int k = 0; k < sides; k++)
            {
                float a = Mathf.Tau * k / sides;
                Vector3 dir = u * Mathf.Cos(a) + v * Mathf.Sin(a);
                ringDir[i][k] = dir;
                ring[i][k] = centres[i] + dir * radii[i];
                if (i == n - 1 && jag > 0.0f) ring[i][k] += axis * rng.Range(0.0f, jag);
            }
        }

        for (int i = 0; i + 1 < n; i++)
        {
            float f0 = (float)i / (n - 1);
            float f1 = (float)(i + 1) / (n - 1);
            Color c0 = bottom.Lerp(top, f0);
            Color c1 = bottom.Lerp(top, f1);
            for (int k = 0; k < sides; k++)
            {
                int k2 = (k + 1) % sides;
                Vector3 outward = (ringDir[i][k] + ringDir[i][k2]).Normalized();
                Quad(batch, mat, t, ring[i][k], ring[i][k2], ring[i + 1][k2], ring[i + 1][k], outward, c0, c0, c1, c1, collide);
            }
        }

        if (capEnd)
        {
            Vector3 centre = centres[n - 1];
            Vector3 axis = (centres[n - 1] - centres[n - 2]).Normalized();
            Color cap = Mul(top, 0.55f);
            for (int k = 0; k < sides; k++)
            {
                int k2 = (k + 1) % sides;
                Tri(batch, mat, t, centre + axis * (jag * 0.4f), ring[n - 1][k], ring[n - 1][k2], axis, cap, cap, cap, collide);
            }
        }
    }

    // ------------------------------------------------------------------ dead tree

    /// <summary>
    /// A leafless trunk that tapers and leans, with a flared root, a handful of bent branches (some
    /// forked) and, often, a snapped splintered top. Only the trunk is solid; branches are visual.
    /// size.X = trunk thickness at the base, size.Y = height.
    /// </summary>
    public static void DeadTree(MeshBatcher batch, object mat, Transform3D t, Vector3 size, bool collide)
    {
        var rng = new Rng(t.Origin, 11);
        float height = size.Y * rng.Range(0.8f, 1.15f);
        bool snapped = rng.Chance(0.4f);
        float trunkH = snapped ? height * rng.Range(0.45f, 0.75f) : height;
        float r0 = size.X * 0.5f * rng.Range(0.9f, 1.25f);
        Vector2 lean = new Vector2(rng.Range(-1.0f, 1.0f), rng.Range(-1.0f, 1.0f)) * trunkH * 0.05f;

        const int rings = 5;
        var c = new Vector3[rings];
        var r = new float[rings];
        for (int i = 0; i < rings; i++)
        {
            float f = (float)i / (rings - 1);
            float bend = f * f;
            c[i] = new Vector3(lean.X * bend, trunkH * f, lean.Y * bend);
            r[i] = r0 * Mathf.Lerp(1.0f, snapped ? 0.7f : 0.3f, f) * (i == 0 ? 1.35f : 1.0f);
        }

        // Charred, near-black at the foot, greyer higher up.
        Color charred = new(0.28f, 0.25f, 0.23f, 1.0f);
        Color bleached = new(0.78f, 0.74f, 0.7f, 1.0f);
        Tube(batch, mat, t, c, r, 7, charred, bleached, collide, true, snapped ? trunkH * 0.12f : 0.0f, ref rng);

        int branches = snapped ? rng.Int(2, 4) : rng.Int(3, 6);
        float scale = height / 5.5f;
        for (int b = 0; b < branches; b++)
        {
            float f = rng.Range(0.3f, 0.92f);
            float fi = f * (rings - 1);
            int i0 = Mathf.Min((int)fi, rings - 2);
            float local = fi - i0;
            Vector3 start = c[i0].Lerp(c[i0 + 1], local);
            float trunkR = Mathf.Lerp(r[i0], r[i0 + 1], local);

            float yaw = rng.Range(0.0f, Mathf.Tau);
            float pitch = Mathf.DegToRad(rng.Range(35.0f, 75.0f));
            Vector3 dir = new(Mathf.Sin(pitch) * Mathf.Cos(yaw), Mathf.Cos(pitch), Mathf.Sin(pitch) * Mathf.Sin(yaw));
            float len = rng.Range(0.7f, 1.9f) * scale * (1.0f - 0.35f * f);
            float rb = Mathf.Max(trunkR * 0.4f, 0.03f);

            Vector3 p1 = start + dir * (len * 0.5f) + Vector3.Down * (len * 0.04f);
            Vector3 p2 = p1 + dir * (len * 0.5f) + Vector3.Down * (len * 0.12f);
            var bc = new[] { start - dir * (rb * 0.5f), p1, p2 };
            var br = new[] { rb, rb * 0.6f, rb * 0.15f };
            Tube(batch, mat, t, bc, br, 4, Mul(charred, 1.2f), bleached, false, false, 0.0f, ref rng);

            if (rng.Chance(0.5f))
            {
                float fork = rng.Range(-0.8f, 0.8f);
                Vector3 d2 = new Basis(Vector3.Up, fork) * dir;
                d2.Y += 0.25f;
                d2 = d2.Normalized();
                Vector3 q = p1 + d2 * (len * 0.45f) + Vector3.Down * (len * 0.06f);
                var fc = new[] { p1, q };
                var fr = new[] { rb * 0.5f, rb * 0.12f };
                Tube(batch, mat, t, fc, fr, 4, bleached, bleached, false, false, 0.0f, ref rng);
            }
        }
    }

    // ------------------------------------------------------------------ barrel

    private static readonly Color[] BarrelPalette =
    {
        new(0.85f, 0.5f, 0.32f, 1.0f),   // rust
        new(0.45f, 0.55f, 0.68f, 1.0f),  // faded blue
        new(0.55f, 0.6f, 0.4f, 1.0f),    // olive
        new(0.3f, 0.3f, 0.3f, 1.0f),     // blackened
        new(0.75f, 0.68f, 0.5f, 1.0f),   // dull yellow
    };

    /// <summary>
    /// A ribbed, bulged, dented drum. Roughly a quarter lie on their side, another quarter lean.
    /// size.X = diameter, size.Y = height.
    /// </summary>
    public static void Barrel(MeshBatcher batch, object mat, Transform3D t, Vector3 size, bool collide)
    {
        var rng = new Rng(t.Origin, 23);
        float rad = size.X * 0.5f;
        float h = size.Y;

        Transform3D pose;
        float roll = rng.Next();
        float yaw = rng.Range(0.0f, Mathf.Tau);
        if (roll < 0.22f)
        {
            // Lying on its side. Rotating +90 degrees about X turns the barrel's up axis into +Z, so
            // shift back by half its length to centre it, and lift by its radius so it rests on the ground.
            pose = new Transform3D(new Basis(Vector3.Up, yaw), Vector3.Zero)
                 * new Transform3D(new Basis(Vector3.Right, Mathf.Pi * 0.5f), new Vector3(0.0f, rad * 0.92f, -h * 0.5f));
        }
        else if (roll < 0.45f)
        {
            float tilt = Mathf.DegToRad(rng.Range(8.0f, 20.0f));
            pose = new Transform3D(new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, tilt), new Vector3(0.0f, -Mathf.Sin(tilt) * rad * 0.8f, 0.0f));
        }
        else
        {
            pose = new Transform3D(new Basis(Vector3.Up, yaw), Vector3.Zero);
        }
        Transform3D T = t * pose;

        // (height fraction, radius factor): chime rings at the ends, a bulge in the middle.
        float[] hy = { 0.0f, 0.06f, 0.22f, 0.5f, 0.78f, 0.94f, 1.0f };
        float[] hr = { 0.93f, 1.0f, 0.99f, 1.05f, 0.99f, 1.0f, 0.93f };
        const int sides = 10;

        Color baseCol = BarrelPalette[rng.Int(0, BarrelPalette.Length - 1)];
        baseCol = Mul(baseCol, rng.Range(0.8f, 1.0f));
        int dentA = rng.Int(0, sides - 1);
        int dentB = rng.Int(0, sides - 1);
        float dentDepth = rng.Range(0.82f, 0.95f);

        var rings = new Vector3[hy.Length][];
        for (int i = 0; i < hy.Length; i++)
        {
            rings[i] = new Vector3[sides];
            for (int k = 0; k < sides; k++)
            {
                float a = Mathf.Tau * k / sides;
                float rr = rad * hr[i];
                // Dents sit on the middle rings only, so the rims stay round.
                if (i >= 2 && i <= 4 && (k == dentA || k == dentB)) rr *= dentDepth;
                rings[i][k] = new Vector3(Mathf.Cos(a) * rr, h * hy[i], Mathf.Sin(a) * rr);
            }
        }

        for (int i = 0; i + 1 < hy.Length; i++)
        {
            Color c0 = Mul(baseCol, Mathf.Lerp(0.5f, 1.0f, hy[i]));
            Color c1 = Mul(baseCol, Mathf.Lerp(0.5f, 1.0f, hy[i + 1]));
            for (int k = 0; k < sides; k++)
            {
                int k2 = (k + 1) % sides;
                float a = Mathf.Tau * (k + 0.5f) / sides;
                Vector3 outward = new(Mathf.Cos(a), 0.0f, Mathf.Sin(a));
                Quad(batch, mat, T, rings[i][k], rings[i][k2], rings[i + 1][k2], rings[i + 1][k], outward, c0, c0, c1, c1, collide);
            }
        }

        // Both ends: a dark rusty lid on top, and a bottom for the ones lying down.
        Color lid = Mul(baseCol, 0.45f);
        int last = hy.Length - 1;
        for (int k = 0; k < sides; k++)
        {
            int k2 = (k + 1) % sides;
            Tri(batch, mat, T, new Vector3(0.0f, h, 0.0f), rings[last][k], rings[last][k2], Vector3.Up, lid, lid, lid, collide);
            Tri(batch, mat, T, new Vector3(0.0f, 0.0f, 0.0f), rings[0][k], rings[0][k2], Vector3.Down, lid, lid, lid, collide);
        }
    }

    // ------------------------------------------------------------------ broken wall

    /// <summary>
    /// A wall built from a grid of cells so it can be genuinely broken: a jagged ragged top, one or two
    /// window-sized holes with chewed edges, a few bits of exposed rebar, and rubble at both feet.
    /// size = (length, height, thickness). Holes are open, so players can look - and crouch - through.
    /// </summary>
    public static void BrokenWall(MeshBatcher batch, object mat, Transform3D t, Vector3 size, bool collide)
    {
        var rng = new Rng(t.Origin, 37);
        float length = size.X, height = size.Y, thick = size.Z;
        const float colW = 0.9f, rowH = 0.55f;
        int cols = Mathf.Max(1, Mathf.RoundToInt(length / colW));
        int rows = Mathf.Max(1, Mathf.CeilToInt(height / rowH));
        float cw = length / cols;
        float rh = height / rows;

        // Skyline: a random walk, with the odd sudden collapse and one column left fully standing.
        var top = new float[cols];
        float level = height * rng.Range(0.55f, 1.0f);
        for (int i = 0; i < cols; i++)
        {
            level += rng.Range(-0.18f, 0.18f) * height;
            level = Mathf.Clamp(level, height * 0.25f, height);
            if (rng.Chance(0.12f)) level = height * rng.Range(0.2f, 0.4f);
            top[i] = level;
        }
        top[rng.Int(0, cols - 1)] = height;

        // Holes (x range, y range) in wall-local space.
        int holeCount = rng.Chance(0.45f) ? 2 : 1;
        var holes = new Vector4[holeCount]; // x0, x1, y0, y1
        for (int hI = 0; hI < holeCount; hI++)
        {
            float hw = Mathf.Min(rng.Range(0.9f, 2.0f), length * 0.5f);
            float margin = hw * 0.5f + 0.3f;
            float hx = margin * 2.0f < length ? rng.Range(-length * 0.5f + margin, length * 0.5f - margin) : 0.0f;
            float hy0 = rng.Range(0.4f, 1.1f);
            float hh = rng.Range(0.8f, 1.6f);
            holes[hI] = new Vector4(hx - hw * 0.5f, hx + hw * 0.5f, hy0, hy0 + hh);
        }

        // Cell heights: 0 = missing. Hole edges are chewed by randomly dropping the cells just outside the hole.
        var ch = new float[cols, rows];
        for (int i = 0; i < cols; i++)
        {
            float cx = -length * 0.5f + (i + 0.5f) * cw;
            for (int j = 0; j < rows; j++)
            {
                float y0 = j * rh;
                float h = Mathf.Clamp(top[i] - y0, 0.0f, rh);
                if (h <= 0.01f) continue;
                float cy = y0 + rh * 0.5f;
                bool gone = false;
                foreach (Vector4 hole in holes)
                {
                    bool core = cx >= hole.X && cx <= hole.Y && cy >= hole.Z && cy <= hole.W;
                    bool fringe = !core
                        && cx >= hole.X - cw * 0.6f && cx <= hole.Y + cw * 0.6f
                        && cy >= hole.Z - rh * 0.6f && cy <= hole.W + rh * 0.6f;
                    if (core || (fringe && rng.Chance(0.4f))) { gone = true; break; }
                }
                if (!gone) ch[i, j] = h;
            }
        }

        float z0 = -thick * 0.5f, z1 = thick * 0.5f;
        for (int i = 0; i < cols; i++)
        {
            float x0 = -length * 0.5f + i * cw;
            float x1 = x0 + cw;
            for (int j = 0; j < rows; j++)
            {
                float h = ch[i, j];
                if (h <= 0.0f) continue;
                float y0 = j * rh, y1 = y0 + h;

                // Grime: darker near the ground, plus per-brick-block variety and the odd scorched block.
                float shade = rng.Range(0.78f, 1.0f) * Mathf.Lerp(0.55f, 1.0f, Mathf.Clamp(y0 / height, 0.0f, 1.0f));
                if (rng.Chance(0.1f)) shade *= 0.4f;
                var col = new Color(shade, shade * 0.97f, shade * 0.94f, 1.0f);

                Quad(batch, mat, t, new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), Vector3.Back, col, collide);
                Quad(batch, mat, t, new Vector3(x1, y0, z0), new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), Vector3.Forward, col, collide);

                float left = i > 0 ? ch[i - 1, j] : 0.0f;
                if (h > left + 0.001f)
                    Quad(batch, mat, t, new Vector3(x0, y0 + left, z1), new Vector3(x0, y0 + left, z0), new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), Vector3.Left, Mul(col, 0.85f), collide);

                float right = i < cols - 1 ? ch[i + 1, j] : 0.0f;
                if (h > right + 0.001f)
                    Quad(batch, mat, t, new Vector3(x1, y0 + right, z0), new Vector3(x1, y0 + right, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), Vector3.Right, Mul(col, 0.85f), collide);

                bool open = h < rh - 0.001f || j == rows - 1 || ch[i, j + 1] <= 0.0f;
                if (open)
                    Quad(batch, mat, t, new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0), Vector3.Up, Mul(col, 1.1f), collide);

                if (j > 0 && ch[i, j - 1] <= 0.0f)
                    Quad(batch, mat, t, new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x0, y0, z1), Vector3.Down, Mul(col, 0.6f), collide);
            }
        }

        // Exposed rebar poking out of the broken columns. Thin and dark rust; never solid.
        var rebarCol = new Color(0.35f, 0.2f, 0.12f, 1.0f);
        for (int i = 0; i < cols; i++)
        {
            if (top[i] >= height - 0.3f || !rng.Chance(0.4f)) continue;
            float cx = -length * 0.5f + (i + 0.5f) * cw;
            float len = rng.Range(0.4f, 0.9f);
            var basis = new Basis(Vector3.Right, rng.Range(-0.35f, 0.35f)) * new Basis(Vector3.Forward, rng.Range(-0.35f, 0.35f));
            batch.AddBox(mat, t * new Transform3D(basis, new Vector3(cx + rng.Range(-0.2f, 0.2f), top[i] + len * 0.35f, 0.0f)),
                new Vector3(0.03f, len, 0.03f), BoxFaces.All, false, rebarCol);
        }

        // Rubble heaped at both feet. Not solid, so nobody snags on a pebble.
        int rubble = rng.Int(5, 10);
        for (int k = 0; k < rubble; k++)
        {
            float rx = rng.Range(-length * 0.5f, length * 0.5f);
            float side = rng.Chance(0.5f) ? 1.0f : -1.0f;
            float rz = side * (thick * 0.5f + rng.Range(0.1f, 0.9f));
            float s = rng.Range(0.18f, 0.5f);
            var rb = new Basis(Vector3.Up, rng.Range(0.0f, Mathf.Tau)) * new Basis(Vector3.Right, rng.Range(-0.5f, 0.5f));
            float shade = rng.Range(0.4f, 0.85f);
            batch.AddBox(mat, t * new Transform3D(rb, new Vector3(rx, s * 0.3f, rz)),
                new Vector3(s * rng.Range(0.8f, 1.6f), s * rng.Range(0.5f, 1.0f), s * rng.Range(0.8f, 1.4f)),
                BoxFaces.All, false, new Color(shade, shade * 0.97f, shade * 0.94f, 1.0f));
        }
    }

    // ------------------------------------------------------------------ container

    // Neutral grime tones only: each container's SurfaceLayer already carries its paint colour (red, green,
    // blue, yellow), and a strong colour multiply on top of that would turn it muddy.
    private static readonly Color[] ContainerPalette =
    {
        new(1.0f, 1.0f, 1.0f, 1.0f),
        new(0.92f, 0.86f, 0.8f, 1.0f),   // rust-stained
        new(0.78f, 0.75f, 0.73f, 1.0f),  // sun-faded
        new(0.52f, 0.49f, 0.47f, 1.0f),  // scorched
    };

    /// <summary>
    /// A battered shipping container: a sagging corner of the roof, grime that darkens toward the
    /// ground, and often an end with one door swung open over a black interior. The body stays solid
    /// (the "interior" is just a dark panel), so it is cover, not a room.
    /// The long side is whichever of size.X / size.Z is bigger; size.Y is the height.
    /// </summary>
    public static void Container(MeshBatcher batch, object mat, Transform3D t, Vector3 size, bool collide)
    {
        var rng = new Rng(t.Origin, 51);
        bool alongZ = size.Z > size.X;
        float L = alongZ ? size.Z : size.X;
        float W = alongZ ? size.X : size.Z;
        float H = size.Y;
        // Work in a frame where the long side is local X.
        Transform3D f = alongZ ? t * new Transform3D(new Basis(Vector3.Up, Mathf.Pi * 0.5f), Vector3.Zero) : t;

        float hl = L * 0.5f, hw = W * 0.5f;
        float sag = rng.Chance(0.5f) ? rng.Range(0.05f, 0.3f) : 0.0f;
        int sagCorner = rng.Int(0, 3);
        // Roof corners in order: (-x,-z) (+x,-z) (+x,+z) (-x,+z)
        float[] topY = { H, H, H, H };
        topY[sagCorner] -= sag;

        Color tone = Mul(ContainerPalette[rng.Int(0, ContainerPalette.Length - 1)], rng.Range(0.85f, 1.0f));
        Color foot = Mul(tone, 0.5f);

        Vector3 b0 = new(-hl, 0.0f, -hw), b1 = new(hl, 0.0f, -hw), b2 = new(hl, 0.0f, hw), b3 = new(-hl, 0.0f, hw);
        Vector3 t0 = new(-hl, topY[0], -hw), t1 = new(hl, topY[1], -hw), t2 = new(hl, topY[2], hw), t3 = new(-hl, topY[3], hw);

        // Four walls (each darker at the foot) and the roof.
        Quad(batch, mat, f, b0, b1, t1, t0, Vector3.Forward, foot, foot, tone, tone, collide);
        Quad(batch, mat, f, b2, b3, t3, t2, Vector3.Back, foot, foot, tone, tone, collide);
        Quad(batch, mat, f, b1, b2, t2, t1, Vector3.Right, foot, foot, tone, tone, collide);
        Quad(batch, mat, f, b3, b0, t0, t3, Vector3.Left, foot, foot, tone, tone, collide);
        Color roofCol = Mul(tone, 0.85f);
        Quad(batch, mat, f, t0, t1, t2, t3, Vector3.Up, roofCol, collide);

        // An open door on the +X end half the time: black recess, one leaf swung out.
        if (rng.Chance(0.55f))
        {
            var dark = new Color(0.03f, 0.03f, 0.035f, 1.0f);
            float e = hl + 0.015f;
            Quad(batch, mat, f, new Vector3(e, 0.12f, -hw + 0.1f), new Vector3(e, 0.12f, hw - 0.1f),
                new Vector3(e, H - 0.12f, hw - 0.1f), new Vector3(e, H - 0.12f, -hw + 0.1f), Vector3.Right, dark, false);

            float angle = Mathf.DegToRad(rng.Range(55.0f, 100.0f));
            float leafW = hw - 0.05f;
            var pivot = new Transform3D(new Basis(Vector3.Up, -angle), new Vector3(hl, H * 0.5f, hw - 0.05f));
            Transform3D leaf = f * pivot * new Transform3D(Basis.Identity, new Vector3(0.0f, 0.0f, -leafW * 0.5f));
            batch.AddBox(mat, leaf, new Vector3(0.05f, H - 0.2f, leafW), BoxFaces.All, false, Mul(tone, 0.8f));
        }
    }
}
