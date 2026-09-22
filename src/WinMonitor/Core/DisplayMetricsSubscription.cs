using Microsoft.Win32;

namespace WinMonitor.Core;

public interface IDisplaySettingsSource
{
    event EventHandler Changed;
}

/// <summary>Owns the real display event -> UI dispatch -> metrics -> redraw pipeline.</summary>
public sealed class DisplayMetricsSubscription : IDisposable
{
    private sealed class WindowsSource : IDisplaySettingsSource
    {
        public event EventHandler Changed
        {
            add => SystemEvents.DisplaySettingsChanged += value;
            remove => SystemEvents.DisplaySettingsChanged -= value;
        }
    }

    private readonly IDisplaySettingsSource _source;
    private readonly Action<Action> _dispatch;
    private readonly Func<bool> _refresh;
    private readonly Action _redraw;
    private bool _disposed;

    public DisplayMetricsSubscription(Action<Action> dispatch, Func<bool> refresh, Action redraw,
        IDisplaySettingsSource? source = null)
    {
        _source = source ?? new WindowsSource();
        _dispatch = dispatch;
        _refresh = refresh;
        _redraw = redraw;
        _source.Changed += OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _dispatch(Refresh);
    }

    private void Refresh()
    {
        if (!_disposed && _refresh()) _redraw();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.Changed -= OnChanged;
    }
}
