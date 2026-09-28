namespace CineKros.Api.RealProviders;

/// <summary>Maps structured provider rating fields to the canonical MovieLens 1–5 threshold.</summary>
public static class RatingThresholdNormalizer
{
    public static bool TryNormalize(decimal value, string scale, out decimal normalized)
    {
        normalized = default;
        if (value < 1m || value > 10m) return false;
        switch (scale)
        {
            case "five" when value <= 5m:
                normalized = value;
                return true;
            case "five":
                return false;
            case "ten":
                normalized = value / 2m;
                return true;
            case "unspecified":
                normalized = value <= 5m ? value : value / 2m;
                return true;
            default:
                return false;
        }
    }
}
