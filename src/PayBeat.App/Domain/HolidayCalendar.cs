namespace PayBeat.App.Domain;

/// <summary>One official Chinese statutory holiday/makeup-workday entry.</summary>
/// <param name="Date">The calendar date.</param>
/// <param name="IsOffDay"><see langword="true"/> when the date is a statutory day off;
/// <see langword="false"/> when it is an official makeup workday (补班).</param>
/// <param name="Name">Holiday name, e.g. "春节".</param>
public readonly record struct HolidayEntry(DateOnly Date, bool IsOffDay, string Name);

/// <summary>
/// Immutable built-in official holiday data (State Council Office notices). Data source:
/// <c>Resources/holidays.json</c> embedded resource — offline by design; a failed or missing
/// dataset must never break core salary computation (days simply fall back to weekly policy).
/// Priority: user overrides &gt; this calendar &gt; weekly policy &gt; defaults.
/// </summary>
public sealed class HolidayCalendar
{
    private readonly Dictionary<DateOnly, HolidayEntry> _entries;
    private readonly IReadOnlySet<int> _coveredYears;

    public HolidayCalendar(IEnumerable<HolidayEntry> entries)
    {
        _entries = entries.GroupBy(e => e.Date).ToDictionary(g => g.Key, g => g.First());
        _coveredYears = _entries.Keys.Select(d => d.Year).ToHashSet();
        Version = "unknown";
    }

    private HolidayCalendar(Dictionary<DateOnly, HolidayEntry> entries, string version)
    {
        _entries = entries;
        _coveredYears = entries.Keys.Select(d => d.Year).ToHashSet();
        Version = version;
    }

    /// <summary>Dataset version / coverage note, e.g. "2025-2026".</summary>
    public string Version { get; }

    /// <summary>Years with at least one official holiday entry in this dataset.</summary>
    public IReadOnlySet<int> CoveredYears => _coveredYears;

    /// <summary>Earliest year with coverage, or null if empty.</summary>
    public int? MinCoveredYear => _coveredYears.Count > 0 ? _coveredYears.Min() : null;

    /// <summary>Latest year with coverage, or null if empty.</summary>
    public int? MaxCoveredYear => _coveredYears.Count > 0 ? _coveredYears.Max() : null;

    /// <summary>Returns true if the dataset contains at least one entry for the given year.</summary>
    public bool CoversYear(int year) => _coveredYears.Contains(year);

    /// <summary>Looks up the official entry for a date, or <see langword="null"/>.</summary>
    public HolidayEntry? Get(DateOnly date) =>
        _entries.TryGetValue(date, out var entry) ? entry : null;

    /// <summary>All entries sorted by date (for calendar legend/debug).</summary>
    public IReadOnlyCollection<HolidayEntry> All => _entries.Values.OrderBy(e => e.Date).ToList();

    /// <summary>
    /// Parses the embedded JSON dataset. Expected shape:
    /// <c>{ "years": [ { "year": 2026, "days": [ { "date": "2026-01-01", "off": true, "name": "元旦" } ] } ] }</c>
    /// Returns an empty calendar (never throws) when the resource is missing or malformed.
    /// </summary>
    public static HolidayCalendar FromJson(string json)
    {
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var entries = new List<HolidayEntry>();
            string? version = null;

            if (doc.RootElement.TryGetProperty("version", out var versionEl))
            {
                version = versionEl.GetString();
            }

            if (doc.RootElement.TryGetProperty("years", out var years) && years.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var year in years.EnumerateArray())
                {
                    if (!year.TryGetProperty("days", out var days) || days.ValueKind != System.Text.Json.JsonValueKind.Array)
                    {
                        continue;
                    }
                    foreach (var day in days.EnumerateArray())
                    {
                        var date = day.TryGetProperty("date", out var d) && DateOnly.TryParse(d.GetString(), out var parsed)
                            ? parsed
                            : default;
                        if (date == default)
                        {
                            continue;
                        }
                        var off = day.TryGetProperty("off", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.True;
                        var name = day.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        entries.Add(new HolidayEntry(date, off, name));
                    }
                }
            }

            return new HolidayCalendar(
                entries.GroupBy(e => e.Date).ToDictionary(g => g.Key, g => g.First()),
                version ?? "unknown");
        }
        catch
        {
            return new HolidayCalendar(new Dictionary<DateOnly, HolidayEntry>(), "invalid");
        }
    }
}
