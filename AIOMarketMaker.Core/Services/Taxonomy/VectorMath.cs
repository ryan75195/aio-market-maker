namespace AIOMarketMaker.Core.Services.Taxonomy;

internal static class VectorMath
{
    internal static float[] Normalize(float[] vector)
    {
        var magnitude = MathF.Sqrt(vector.Sum(v => v * v));
        if (magnitude == 0)
        {
            return vector;
        }
        return vector.Select(v => v / magnitude).ToArray();
    }

    internal static double CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }
        return dot;
    }
}
