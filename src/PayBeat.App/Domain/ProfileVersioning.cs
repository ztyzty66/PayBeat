namespace PayBeat.App.Domain;

/// <summary>
/// Shared versioning helper for versioned profile collections (SalaryProfile, WorkScheduleProfile,
/// WorkWeekPolicy). Ensures deterministic resolution and prevents same-day duplicates.
/// </summary>
public static class ProfileVersioning
{
    /// <summary>
    /// Resolves the effective profile for a given date from a list of profiles.
    /// Returns the profile with the latest EffectiveFrom that is on or before the requested date.
    /// This is deterministic regardless of list insertion order.
    /// </summary>
    public static T Resolve<T>(IReadOnlyList<T> profiles, DateOnly date, Func<T, DateOnly> getEffectiveFrom, Func<T> createDefault)
    {
        T? best = default;
        foreach (var p in profiles)
        {
            var effectiveFrom = getEffectiveFrom(p);
            if (effectiveFrom <= date && (best is null || effectiveFrom > getEffectiveFrom(best)))
            {
                best = p;
            }
        }
        return best ?? createDefault();
    }

    /// <summary>
    /// Upserts a profile into a collection. If a profile with the same EffectiveFrom already exists,
    /// it is replaced. No two profiles of the same type share the same EffectiveFrom.
    /// Deterministic: same-date always means replace, regardless of content equality.
    /// </summary>
    public static List<T> Upsert<T>(List<T> profiles, T newProfile, Func<T, DateOnly> getEffectiveFrom)
    {
        var effectiveFrom = getEffectiveFrom(newProfile);
        var result = new List<T>(profiles);

        var existingIndex = result.FindIndex(p => getEffectiveFrom(p) == effectiveFrom);
        if (existingIndex >= 0)
        {
            result[existingIndex] = newProfile;
        }
        else
        {
            result.Add(newProfile);
        }

        return result;
    }

    /// <summary>
    /// Legacy overload kept for call-site compatibility. The <paramref name="areEqual"/>
    /// parameter is accepted but intentionally ignored — same-date upsert is always
    /// date-keyed, never content-keyed.
    /// </summary>
    public static List<T> Upsert<T>(List<T> profiles, T newProfile, Func<T, DateOnly> getEffectiveFrom, Func<T, T, bool> areEqual)
        => Upsert(profiles, newProfile, getEffectiveFrom);

    /// <summary>
    /// Ensures no two profiles share the same EffectiveFrom date. Deterministic last-write-wins:
    /// when several entries carry the same date, the one submitted LATEST (highest list index)
    /// survives — a new same-day submission replaces the existing version for that day.
    /// </summary>
    public static List<T> DeduplicateByDate<T>(List<T> profiles, Func<T, DateOnly> getEffectiveFrom)
    {
        var result = new List<T>();
        var seenDates = new HashSet<DateOnly>();

        // Walk newest-submitted → oldest so each date keeps its LAST occurrence.
        for (var i = profiles.Count - 1; i >= 0; i--)
        {
            if (seenDates.Add(getEffectiveFrom(profiles[i])))
            {
                result.Add(profiles[i]);
            }
        }

        result.Reverse();
        return result;
    }

    /// <summary>
    /// Normalizes a profile collection: removes same-date duplicates (last-write-wins) and
    /// sorts by EffectiveFrom. Used during load/migration to clean up any legacy duplicates.
    /// </summary>
    public static List<T> Normalize<T>(List<T> profiles, Func<T, DateOnly> getEffectiveFrom)
    {
        var deduped = DeduplicateByDate(profiles, getEffectiveFrom);
        deduped.Sort((a, b) => getEffectiveFrom(a).CompareTo(getEffectiveFrom(b)));
        return deduped;
    }
}
