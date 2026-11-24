using System.Globalization;
using ProxyDock.Core.Enums;

namespace ProxyDock.Core.Services;

public readonly record struct SemanticVersion(
    int Major,
    int Minor,
    int Patch,
    string? Prerelease) : IComparable<SemanticVersion>
{
    public bool IsPrerelease => !string.IsNullOrWhiteSpace(Prerelease);

    public int CompareTo(SemanticVersion other)
        => SemanticVersionComparer.Compare(this, other);

    public override string ToString()
        => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";
}

/// <summary>
/// Строгий парсер и компаратор SemVer 2.0.0 для проверки обновлений без возможности downgrade
/// и с исключением prerelease-версий в стабильном канале.
/// </summary>
public static class SemanticVersionComparer
{
    public static bool TryParse(string? raw, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var plusIdx = s.IndexOf('+');
        if (plusIdx >= 0)
        {
            s = s[..plusIdx];
        }

        string? prerelease = null;
        var dashIdx = s.IndexOf('-');
        if (dashIdx >= 0)
        {
            prerelease = s[(dashIdx + 1)..];
            if (string.IsNullOrWhiteSpace(prerelease))
            {
                return false;
            }

            s = s[..dashIdx];
        }

        var parts = s.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public static int Compare(SemanticVersion a, SemanticVersion b)
    {
        var cmp = a.Major.CompareTo(b.Major);
        if (cmp != 0) return cmp;

        cmp = a.Minor.CompareTo(b.Minor);
        if (cmp != 0) return cmp;

        cmp = a.Patch.CompareTo(b.Patch);
        if (cmp != 0) return cmp;

        if (!a.IsPrerelease && !b.IsPrerelease) return 0;
        if (!a.IsPrerelease) return 1;  // 1.0.0 > 1.0.0-alpha
        if (!b.IsPrerelease) return -1;

        var aIds = a.Prerelease!.Split('.');
        var bIds = b.Prerelease!.Split('.');
        var len = Math.Min(aIds.Length, bIds.Length);

        for (var i = 0; i < len; i++)
        {
            var aIsNum = long.TryParse(aIds[i], NumberStyles.None, CultureInfo.InvariantCulture, out var aNum);
            var bIsNum = long.TryParse(bIds[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bNum);

            if (aIsNum && bIsNum)
            {
                cmp = aNum.CompareTo(bNum);
                if (cmp != 0) return cmp;
            }
            else if (aIsNum != bIsNum)
            {
                return aIsNum ? -1 : 1; // Числовые идентификаторы < буквенных
            }
            else
            {
                cmp = string.CompareOrdinal(aIds[i], bIds[i]);
                if (cmp != 0) return cmp;
            }
        }

        return aIds.Length.CompareTo(bIds.Length);
    }

    public static bool IsEligibleUpdate(string? currentVersion, string? candidateVersion, UpdateChannel channel)
    {
        if (!TryParse(currentVersion, out var current) || !TryParse(candidateVersion, out var candidate))
        {
            return false;
        }

        if (channel == UpdateChannel.Stable && candidate.IsPrerelease)
        {
            return false;
        }

        return Compare(candidate, current) > 0;
    }
}
