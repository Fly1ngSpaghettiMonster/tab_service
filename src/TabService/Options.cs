namespace TabService;

/// <summary>Settings bound from the "Tab" configuration section (env vars: Tab__ApiKey, ...).</summary>
public sealed class TabOptions
{
    public const string Section = "Tab";

    /// <summary>Shared secret the phone must send to create a sharing session.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Secret that unlocks the admin dashboard (all sessions, open or closed). Empty disables the admin API.
    /// Deliberately separate from ApiKey: that one ships inside the phone app and can be extracted from it.
    /// </summary>
    public string AdminKey { get; set; } = "";

    /// <summary>Optional path prefix when not served from a domain root (e.g. "/tab"). Empty by default.</summary>
    public string PathBase { get; set; } = "";

    /// <summary>Where the web viewer lives. The viewer token is appended as the URL fragment.</summary>
    public string PublicViewerUrl { get; set; } = "https://tab.aabder.xyz/view";

    /// <summary>Comma-separated browser origins allowed to call the API/hub (for a viewer on another origin).</summary>
    public string AllowedOrigins { get; set; } = "";

    /// <summary>How long sessions and their samples are kept.</summary>
    public int RetentionDays { get; set; } = 60;

    /// <summary>Open sessions with no uploads for this long are closed automatically.</summary>
    public int StaleSessionHours { get; set; } = 12;

    /// <summary>Maximum number of samples accepted in one PublishSamples call.</summary>
    public int MaxBatchSize { get; set; } = 500;
}
