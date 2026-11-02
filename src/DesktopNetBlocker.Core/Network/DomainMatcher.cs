using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DesktopNetBlocker.Core.Models;

namespace DesktopNetBlocker.Core.Network;

public static class DomainMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if a queried domain name matches a rule.
    /// </summary>
    public static bool Matches(string queryDomain, string ruleDomain, MatchingMode mode)
    {
        if (string.IsNullOrWhiteSpace(queryDomain) || string.IsNullOrWhiteSpace(ruleDomain))
            return false;

        string q = queryDomain.Trim().TrimEnd('.').ToLowerInvariant();
        string r = ruleDomain.Trim().TrimEnd('.').ToLowerInvariant();

        switch (mode)
        {
            case MatchingMode.Exact:
                return string.Equals(q, r, StringComparison.OrdinalIgnoreCase);

            case MatchingMode.Subdomain:
                // Rule: example.com matches example.com and any *.example.com
                if (r.StartsWith("*."))
                    r = r[2..];

                if (string.Equals(q, r, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (q.EndsWith("." + r, StringComparison.OrdinalIgnoreCase))
                    return true;

                return false;

            case MatchingMode.Wildcard:
                Regex regex = RegexCache.GetOrAdd(r, pattern =>
                {
                    string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    return new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                });
                return regex.IsMatch(q);

            default:
                return string.Equals(q, r, StringComparison.OrdinalIgnoreCase);
        }
    }
}

