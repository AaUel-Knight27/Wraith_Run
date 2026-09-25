# Wraith Run map kit

C# tools for building outdoor and indoor maps fast, with ambientCG as the texture source.
Everything here is `[Tool]` code — it runs in the editor when you press its "Build Map" button,
and produces plain scenes/meshes that ship normally. None of it runs or costs anything at play time.

## Folders

- `Core/` — shared pieces every builder uses: `SurfaceLayer` (one PBR material), `MeshBatcher`
  (merges geometry into a handful of draw calls per chunk), `PropDefinition` (a placeable object,
  real mesh or primitive placeholder), `ScatterLayer` + `ScatterField` (random and grid placement).
- `Outdoor/OutdoorMapBuilder.cs` — noise terrain + hand-drawn roads/trenches + scatter.
- `Indoor/IndoorMapBuilder.cs` — ASCII-grid floorplans (`RoomLayout`) for any indoor space.
- `Materials/MaterialImporter.cs` — turns an ambientCG download into a `SurfaceLayer`.

## First time setup: importing ambientCG materials

1. On ambientcg.com, always download the **1K-JPG** (or 2K-PNG) package for a material —
   never the **"Blend"** package. "Blend" is a ready-made Blender node setup with no plain image
   files in it; this kit (and Godot) can't do anything with a `.blend` file, and doesn't need to —
   it reads the individual Color/NormalGL/Roughness/etc. images directly.
2. Unzip each material into its own folder under `res://art/_ambientcg_source/`, e.g.
   `res://art/_ambientcg_source/Asphalt026/Asphalt026_1K-JPG_Color.jpg` (+ NormalGL, Roughness, ...).
3. Add a `MaterialImporter` node anywhere in the editor (New Node → search "MaterialImporter"),
   leave the default folders, click **Import All Materials** in the inspector.
4. One `SurfaceLayer` resource appears per material under `res://art/materials/`. Drag those into
   the `Ground` / `RoadSurface` / `WallSurface` / etc. slots on the builders below.
5. Once you've imported what you need, you can delete `_ambientcg_source/` (or exclude it from
   your export filters) — the generated SurfaceLayers embed the actual pixel data, so they don't
   depend on the raw downloads anymore.

Recommended resolution for this hardware: 1K. Skip Displacement maps (unused, wastes memory).

## Outdoor maps (town, war land, trench, forest, long asphalt road...)

Add an `OutdoorMapBuilder` node. Assign a `Ground` SurfaceLayer, set `SizeMeters`. For a road,
add a `Path3D` child, draw its curve in the 3D view (the curve's own height IS the road's
elevation — the terrain blends to meet it), point `RoadPath` at it. Trenches work the same way
via `TrenchPaths`. Add `ScatterLayer`s (rubble, dead trees, barrels) for the "war land" look.
Click **Build Map**.

## Indoor maps (train station, museum, lab, classrooms, ship interior...)

Add an `IndoorMapBuilder` node, create a `RoomLayout` resource, type an ASCII floorplan into
its `Rows`:

```
#########
#.......#
#..####.#
#..#WW#.#
D..####.#
#.......#
#########
```

Legend: `#` wall block, `W` wall block with WindowSurface instead, `.` open floor, `D` open
floor marked as a doorway, ` ` (space) void — leave cells blank for L-shaped buildings.
Assign Wall/Window/Floor/Ceiling SurfaceLayers, optionally add `FurnishArea`s (desk rows,
display cases, benches — any rectangle of evenly-spaced props). Click **Build Map**. A different
grid + different surfaces is a different building — same builder for every room type.

## Props before you have real art

`PropDefinition.Scene` can point at a real model once you have one (Hunyuan 3D output, etc).
Until then, leave it empty — it builds a plain box/cylinder/barrier primitive instead, so every
map is playable and readable today and swaps to real art later with no layout changes.

## Known gaps / simplifications (by design, to ship v1)

- Road surface doesn't bank into turns (assumes flat "up" cross-section).
- Windows are a full transparent panel on the whole wall cell, not a cut hole with a sill.
- No multi-floor/stairs yet — one `WallHeight` per `IndoorMapBuilder`.
- No LOD mesh swapping — relies on chunk batching + shadow toggling for perf, which is the
  bigger win on this hardware anyway.
