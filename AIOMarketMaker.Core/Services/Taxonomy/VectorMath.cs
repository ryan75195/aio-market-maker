namespace AIOMarketMaker.Core.Services.Taxonomy;

internal static class VectorMath
{
    internal static float[] Normalize(float[] vector)
    {
        var sum = 0f;
        for (var i = 0; i < vector.Length; i++)
        {
            sum += vector[i] * vector[i];
        }

        var magnitude = MathF.Sqrt(sum);
        if (magnitude == 0)
        {
            return vector;
        }

        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            result[i] = vector[i] / magnitude;
        }

        return result;
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
