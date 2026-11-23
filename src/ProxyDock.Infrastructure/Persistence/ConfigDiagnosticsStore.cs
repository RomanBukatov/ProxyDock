using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;

namespace ProxyDock.Infrastructure.Persistence;

public sealed class ConfigDiagnosticsStore : IConfigDiagnosticsStore
{
    private readonly List<ConfigLoadWarning> _warnings = [];
    private readonly Lock _gate = new();

    public event EventHandler? WarningsChanged;

    public IReadOnlyList<ConfigLoadWarning> Warnings
    {
        get
        {
            lock (_gate)
            {
                return _warnings.ToList();
            }
        }
    }

    public void AddWarning(ConfigLoadWarning warning)
    {
        lock (_gate)
        {
            _warnings.Add(warning);
        }

        WarningsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearWarnings()
    {
        lock (_gate)
        {
            if (_warnings.Count == 0)
            {
                return;
            }

            _warnings.Clear();
        }

        WarningsChanged?.Invoke(this, EventArgs.Empty);
    }
}
