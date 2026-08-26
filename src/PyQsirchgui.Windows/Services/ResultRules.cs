using System.Security.Principal;
using System.Text.RegularExpressions;
using PyQsirchgui.Windows.Models;

namespace PyQsirchgui.Windows.Services;

public sealed class ResultRules(AppConfig config)
{
    private readonly HashSet<string> _identities = CurrentIdentities();

    public bool IsHidden(SearchResult result)
    {
        return IsExcluded(result) || IsVisibilityHidden(result);
    }

    public bool IsExcluded(SearchResult result)
    {
        var path = Normalize(result.Path);
        var fileName = result.FileName;
        var parent = path;
        if (!string.IsNullOrWhiteSpace(fileName) && parent.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
        {
            parent = parent[..^fileName.Length].TrimEnd('\\');
        }
        var components = parent.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { parent, parent + "\\" };
        for (var i = 0; i < components.Length; i++)
        {
            var tail = string.Join('\\', components.Skip(i));
            if (!string.IsNullOrWhiteSpace(tail))
            {
                candidates.Add(tail);
                candidates.Add(tail.TrimEnd('\\') + "\\");
            }
        }

        foreach (var rule in config.Exclude.Folders.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var normalized = Normalize(rule);
            if (HasWildcard(normalized))
            {
                if (candidates.Any(candidate => WildcardMatch(candidate, normalized)) ||
                    components.Any(component => WildcardMatch(component, normalized)))
                {
                    return true;
                }
            }
            else if (components.Any(component => component.Equals(normalized, StringComparison.OrdinalIgnoreCase)) ||
                     parent.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var rule in config.Exclude.Files.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (HasWildcard(rule))
            {
                if (WildcardMatch(fileName, rule))
                {
                    return true;
                }
            }
            else if (fileName.Equals(rule, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsVisibilityHidden(SearchResult result)
    {
        var rules = config.VisibilityRules.Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern)).ToList();
        if (rules.Count == 0)
        {
            return false;
        }

        var matches = new List<(int specificity, string access)>();
        foreach (var rule in rules)
        {
            if (!WildcardMatch(Normalize(result.Path), Normalize(rule.Pattern)))
            {
                continue;
            }
            var specificity = IdentitySpecificity(rule.Identity);
            if (specificity >= 0)
            {
                matches.Add((specificity, rule.Access));
            }
        }

        if (matches.Count == 0)
        {
            return false;
        }
        var best = matches.Max(x => x.specificity);
        return !matches.Where(x => x.specificity == best).Any(x => x.access.Equals("allow", StringComparison.OrdinalIgnoreCase));
    }

    private int IdentitySpecificity(string identity)
    {
        var normalized = (identity ?? "*").Trim();
        if (normalized is "" or "*")
        {
            return 0;
        }
        return _identities.Contains(normalized) ? 2 : -1;
    }

    private static HashSet<string> CurrentIdentities()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var user = Environment.UserName;
        var domain = Environment.UserDomainName;
        set.Add(user);
        set.Add($@"{domain}\{user}");
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            if (!string.IsNullOrWhiteSpace(identity.Name))
            {
                set.Add(identity.Name);
            }
        }
        catch
        {
        }
        return set;
    }

    private static string Normalize(string value) => (value ?? "").Replace('/', '\\').Trim();

    private static bool HasWildcard(string value) => value.IndexOfAny(['*', '?', '[', ']']) >= 0;

    private static bool WildcardMatch(string text, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(text ?? "", regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
