namespace Rig.Services;

public static class Heatmaps
{
    // Counts are moved, never invented or discarded, when upgrading the legacy 64 × 36 grid.
    public static int[] Upgrade(int[]? source, int width, int height)
    {
        if (source?.Length == width * height) return source;
        var result = new int[width * height];
        if (source?.Length != 64 * 36) return result;
        for (int y = 0; y < 36; y++)
            for (int x = 0; x < 64; x++)
                result[Math.Min(height - 1, (2 * y + 1) * height / 72) * width + Math.Min(width - 1, (2 * x + 1) * width / 128)] = source[y * 64 + x];
        return result;
    }

    // A small separable Gaussian spreads nearby samples into smooth hotspots instead of giant square pixels.
    public static double[] Smooth(int[] cells, int width, int height)
    {
        var horizontal = new double[cells.Length];
        var result = new double[cells.Length];
        int[] weights = [1, 4, 6, 4, 1];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int k = -2; k <= 2; k++)
                    horizontal[y * width + x] += Math.Max(0, cells[y * width + Math.Clamp(x + k, 0, width - 1)]) * weights[k + 2] / 16.0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int k = -2; k <= 2; k++)
                    result[y * width + x] += horizontal[Math.Clamp(y + k, 0, height - 1) * width + x] * weights[k + 2] / 16.0;
        return result;
    }
}
