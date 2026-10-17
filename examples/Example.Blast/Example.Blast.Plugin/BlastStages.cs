namespace Example.Blast.Plugin;

/// <summary>Static definitions for the two built-in stages.</summary>
public static class BlastStages
{
    public const string RockGarden = "rock-garden";
    public const string UpAndDown = "up-and-down";

    /// <summary>Returns the stage layout for a given id. Falls back to Rock Garden for unknown ids.</summary>
    public static StageLayout Get(string id) => id switch
    {
        UpAndDown => BuildUpAndDown(),
        _ => BuildRockGarden(),
    };

    // ─── Rock Garden (17×15, all flat tier 0) ──────────────────────────────
    // Open edges: no boundary wall, walking off the grid eliminates.
    // Hard pillars provide cover. Eight spawns at corners, edges and center edges.
    // ─────────────────────────────────────────────────────────────────────────

    private static StageLayout BuildRockGarden()
    {
        const int W = 17, H = 15;
        var cells = Fill(W, H, CellDef.Floor0);

        // Hard pillars — arranged symmetrically for fair play.
        int[] px = [3, 13, 3, 13,  6, 10, 6, 10,  7,  9,  4,  8, 12];
        int[] pz = [2,  2, 12, 12,  2, 2, 12, 12,  5,  5,  7,  7,  7];
        for (var i = 0; i < px.Length; i++)
            cells[px[i], pz[i]] = CellDef.Pillar;

        // Spawns: corners + midpoints of each edge, integer grid-cell coords.
        (int X, int Z)[] spawns =
        [
            (1, 1), (15, 1), (1, 13), (15, 13),
            (8, 1), (8, 13), (1, 7),  (15, 7),
        ];

        return new StageLayout
        {
            Id = RockGarden, Name = "Rock Garden",
            Width = W, Height = H,
            Cells = cells, Spawns = spawns,
        };
    }

    // ─── Up & Down (17×15, three tiers connected by ramps, water surround) ─
    // Outer two rows/cols are water. Tier 0 ring, tier 1 inner rectangle,
    // tier 2 small center platform. Four ramps at each tier boundary.
    // ─────────────────────────────────────────────────────────────────────────

    private static StageLayout BuildUpAndDown()
    {
        const int W = 17, H = 15;
        var cells = Fill(W, H, CellDef.Void);

        // Water border (outer 2 cells).
        for (var x = 0; x < W; x++)
            for (var z = 0; z < H; z++)
                if (x < 2 || x > 14 || z < 1 || z > 13)
                    cells[x, z] = CellDef.Water;

        // Tier 0 ground ring: rows/cols 2..14 x 1..13 minus the tier-1 rectangle interior.
        for (var x = 2; x <= 14; x++)
            for (var z = 2; z <= 12; z++)
                cells[x, z] = CellDef.Floor0;

        // Tier 1 inner rectangle: 5..11 x 4..10.
        for (var x = 5; x <= 11; x++)
            for (var z = 4; z <= 10; z++)
                cells[x, z] = CellDef.Floor1;

        // Tier 2 center: 7..9 x 6..8.
        for (var x = 7; x <= 9; x++)
            for (var z = 6; z <= 8; z++)
                cells[x, z] = CellDef.Floor2;

        // Ramps tier 0 → tier 1 (at the four sides of the tier-1 rectangle).
        // North side (z=4 border): ramp at (8,3), going south (+Z) raises from 0 to 1.
        cells[8, 3] = CellDef.Ramp0To1(0, 1);
        // South side: (8,11), ramp going north (−Z).
        cells[8, 11] = CellDef.Ramp0To1(0, -1);
        // West side: (4,7), ramp going east (+X).
        cells[4, 7] = CellDef.Ramp0To1(1, 0);
        // East side: (12,7), ramp going west (−X).
        cells[12, 7] = CellDef.Ramp0To1(-1, 0);

        // Ramps tier 1 → tier 2 (at the four sides of the tier-2 3×3 block).
        cells[8, 5] = CellDef.Ramp1To2(0, 1);
        cells[8, 9] = CellDef.Ramp1To2(0, -1);
        cells[6, 7] = CellDef.Ramp1To2(1, 0);
        cells[10, 7] = CellDef.Ramp1To2(-1, 0);

        // Hard pillars on tier 0 for cover.
        int[] px = [3, 13, 3, 13];
        int[] pz = [3, 3, 11, 11];
        for (var i = 0; i < px.Length; i++)
            cells[px[i], pz[i]] = CellDef.Pillar;

        // Spawns: two on each tier.
        (int X, int Z)[] spawns =
        [
            (3, 2),  (13, 2),  // tier 0, top
            (3, 12), (13, 12), // tier 0, bottom
            (5, 5),  (11, 9),  // tier 1
            (7, 7),  (9, 7),   // tier 2
        ];

        return new StageLayout
        {
            Id = UpAndDown, Name = "Up & Down",
            Width = W, Height = H,
            Cells = cells, Spawns = spawns,
        };
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static CellDef[,] Fill(int w, int h, CellDef def)
    {
        var cells = new CellDef[w, h];
        for (var x = 0; x < w; x++)
            for (var z = 0; z < h; z++)
                cells[x, z] = def;
        return cells;
    }
}
