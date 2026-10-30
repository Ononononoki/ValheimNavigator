using UnityEngine;

namespace ValheimNavigator
{
    // the ground Detailed routes walk on, the same as the detailed map draws: at the fine point nearest a sample, the visited ground, else
    // the generated ground as the game builds a zone (from a kept 2 m tile, or the same blend for one point, nothing is built again) shaped
    // around placed locations, plus the terraforming (a guest's copy of the host's too). any thread
    internal static class RouteGround
    {
        // Detailed checks the ground this often along a step of the route grid where it is rough or has small detail, so a short steep bank,
        // a ditch or a dug ramp counts
        internal const float SampleMeters = 2f;

        // the ground here has detail finer than the generated world: terraforming, visited ground or shaped location ground
        internal static bool Detail(float x, float z) =>
            TerrainEdits.HasData(x, z) || LocationShaping.ZoneMods(Mathf.FloorToInt((x + 32f) / 64f), Mathf.FloorToInt((z + 32f) / 64f)) != null;

        internal static float Height(WorldGenerator world, float x, float z)
        {
            int ix = Mathf.RoundToInt(x * 0.5f) * 2, iz = Mathf.RoundToInt(z * 0.5f) * 2;
            if (TerrainEdits.VisitedAt(ix, iz, out float height)) return height;
            if (!FineTerrain.TryBase(ix, iz, out height)) height = FineBuilder.PointHeight(world, ix, iz);
            height = LocationShaping.Shape(ix, iz, height);
            if (TerrainEdits.At(ix, iz, out float delta, out _)) height += delta;
            return height;
        }
    }
}
