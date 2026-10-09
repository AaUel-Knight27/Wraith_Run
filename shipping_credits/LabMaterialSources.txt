# Abandoned lab material sources

The lab uses free CC0 PBR materials from Poly Haven and ambientCG. The 1K maps are bundled locally so the map works offline.

- [Concrete Floor Worn 001](https://polyhaven.com/a/concrete_floor_worn_001): stained floor.
- [Rust Coarse 01](https://polyhaven.com/a/rust_coarse_01): corroded panels, pipes, and industrial trim.
- [Concrete 044D](https://ambientcg.com/view?id=Concrete044D): damaged concrete wall sections and chunks.
- [Painted Plaster 018](https://ambientcg.com/view?id=PaintedPlaster018): aged, peeling interior plaster.
- [Ground 111](https://ambientcg.com/view?id=Ground111): dust and rubble pieces.

The ambientCG color, OpenGL normal, roughness, and ambient occlusion maps are stored under `ambientcg/`. `*_ORM.png` packs AO into R, roughness into G, and a zero metalness channel into B for the map kit's SurfaceLayer materials.
