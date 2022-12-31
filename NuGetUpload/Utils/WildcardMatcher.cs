using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NuGetUpload.Utils;

public record WildcardMatcher(IEnumerable<string> Matches)
{
    private readonly HashSet<string> _directMatches = new(Matches.Where(a => !a.StartsWith("$")),
        StringComparer.InvariantCultureIgnoreCase);

    private readonly List<Regex> _regexMatches = Matches.Where(a => a.StartsWith("$"))
        .Select(a => new Regex(a[1..], RegexOptions.IgnoreCase)).ToList();

    public IReadOnlySet<string> DirectMatches => _directMatches; // Mainly for API purposes

    public IReadOnlyList<Regex> RegexMatches => _regexMatches; // Mainly for API purposes

    public bool IsMatch(string value) => _directMatches.Contains(value) || _regexMatches.Any(a => a.IsMatch(value));
}