using System.Diagnostics;
using System.Windows.Automation;
using ProxyDock.Core.Models;

namespace ProxyDock.Infrastructure.Platform;

internal static class CodexQuitMenu
{
    // The Owl desktop shell exposes its File menu through Windows UI Automation.
    // Address the app's menu item directly, without sending keys to the foreground window.
    public static bool TryInvoke(DetectedProcessInfo target, IntPtr window, CancellationToken cancellationToken)
    {
        ExpandCollapsePattern? menu = null;
        bool opened = false;
        bool invoked = false;
        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            if (process.StartTime.ToUniversalTime() != target.StartedUtc) return false;
            var root = AutomationElement.FromHandle(window);
            if (root.Current.ProcessId != target.ProcessId) return false;

            var file = Find(root, ControlType.MenuItem, "application-menu-trigger-file-menu");
            if (file?.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern) != true) return false;
            menu = (ExpandCollapsePattern)pattern;
            if (menu.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
            {
                menu.Expand();
                opened = true;
            }

            var watch = Stopwatch.StartNew();
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var popup = Find(root, ControlType.Menu, "application-menu-content");
                if (popup is not null && popup.Current.Name == file.Current.Name)
                {
                    var items = popup.FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
                    foreach (AutomationElement item in items)
                    {
                        if (item.Current.ProcessId == target.ProcessId && item.Current.IsEnabled
                            && item.Current.Name.EndsWith(" Ctrl+Q", StringComparison.OrdinalIgnoreCase)
                            && item.TryGetCurrentPattern(InvokePattern.Pattern, out var quit) && !process.HasExited)
                        {
                            ((InvokePattern)quit).Invoke();
                            invoked = true;
                            return true;
                        }
                    }
                }
                Thread.Sleep(75);
            } while (watch.ElapsedMilliseconds < 1000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ElementNotAvailableException
            or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            // Older builds and windows that closed during inspection may have no accessible menu.
        }
        finally
        {
            if (opened && !invoked)
            {
                try { menu!.Collapse(); }
                catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or System.Runtime.InteropServices.COMException) { }
            }
        }
        return false;
    }

    private static AutomationElement? Find(AutomationElement root, ControlType type, string id)
        => root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, type),
            new PropertyCondition(AutomationElement.AutomationIdProperty, id)));
}
