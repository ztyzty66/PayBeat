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
    /// it is replaced. If a profile with the same effective date but different content exists,
    /// the new one replaces it. No two profiles of the same type share the same EffectiveFrom.
    /// </summary>
    public static List<T> Upsert<T>(List<T> profiles, T newProfile, Func<T, DateOnly> getEffectiveFrom, Func<T, T, bool> areEqual)
    {
        var effectiveFrom = getEffectiveFrom(newProfile);
        var result = new List<T>(profiles);

        // Find any existing profile with the same effective date
        var existingIndex = result.FindIndex(p => getEffectiveFrom(p) == effectiveFrom);
        if (existingIndex >= 0)
        {
            // Same date: replace in place (upsert same-date entry)
            result[existingIndex] = newProfile;
        }
        else
        {
            // Different date: add the new profile
            result.Add(newProfile);
        }

        return result;
    }

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
}
