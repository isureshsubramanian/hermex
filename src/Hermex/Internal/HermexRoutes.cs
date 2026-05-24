namespace Hermex.Internal;

/// <summary>Fixed route paths used by the dashboard, API and SignalR hub.</summary>
internal static class HermexRoutes
{
    /// <summary>Root of the dashboard UI.</summary>
    public const string Base = "/hermex";

    /// <summary>Root of the JSON API consumed by the dashboard.</summary>
    public const string ApiBase = "/hermex/api";

    /// <summary>SignalR hub endpoint for live inbox updates.</summary>
    public const string Hub = "/hermex/hub";
}
