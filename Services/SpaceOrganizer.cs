namespace DriveLink.Services;

public static class SpaceOrganizer
{
    public static List<Space> Apply(IEnumerable<Space> spaces, IReadOnlyDictionary<string, SpacePreference> preferences)
    {
        var result = new List<Space>();
        foreach (var space in spaces)
        {
            if (preferences.TryGetValue(space.Id, out var preference))
            {
                if (preference.Hidden) continue;
                space.Pinned = preference.Pinned;
                space.Color = IsValidColor(preference.Color) ? preference.Color : "#3865F3";
            }
            result.Add(space);
        }
        return result.OrderByDescending(s => s.Pinned).ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    public static bool IsValidColor(string value) => value.Length == 7 && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") == -1;
}
