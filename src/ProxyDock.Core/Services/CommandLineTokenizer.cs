using System.Text;

namespace ProxyDock.Core.Services;

/// <summary>
/// Детерминированный парсер аргументов командной строки в список аргументов (для ProcessStartInfo.ArgumentList).
/// </summary>
public static class CommandLineTokenizer
{
    public static bool TryTokenize(string? commandLine, out List<string> arguments, out string? error)
    {
        arguments = [];
        error = null;

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return true;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        var i = 0;

        while (i < commandLine.Length)
        {
            var c = commandLine[i];

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                i++;
                continue;
            }

            hasToken = true;

            if (c == '\\')
            {
                var slashCount = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    slashCount++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashCount / 2);
                    if (slashCount % 2 == 1)
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                        i++;
                    }
                }
                else
                {
                    current.Append('\\', slashCount);
                }

                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        if (inQuotes)
        {
            arguments.Clear();
            error = "Обнаружены незакрытые двойные кавычки в аргументах запуска.";
            return false;
        }

        if (hasToken)
        {
            arguments.Add(current.ToString());
        }

        return true;
    }
}
