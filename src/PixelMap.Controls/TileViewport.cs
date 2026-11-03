using PixelMap.Data;

namespace PixelMap.Controls;

/// <summary>UI-thread viewport residency for one asynchronously supplied XYZ layer.</summary>
internal sealed class TileViewport<T>(Func<TileId, CancellationToken, Task<T>> load, Action changed,
    bool retainPreviousOnFailure = false) where T : class
{
    private readonly Dictionary<TileId, T> _loaded = [];
    private readonly Dictionary<TileId, CancellationTokenSource> _requests = [];
    private readonly HashSet<TileId> _failed = [];
    private TileId[] _wanted = [];
    private IReadOnlyList<T> _previousLevel = [];
    public IReadOnlyList<T> Ready { get; private set; } = [];
    public IReadOnlyList<T> Display => _previousLevel.Count == 0 ? Ready : _previousLevel;
    public bool HasFailures => _failed.Count != 0;
    public int Count => _wanted.Length;

    public void Update(TileId[] wanted)
    {
        if (_wanted.SequenceEqual(wanted)) return;
        // Mixed-resolution layers can keep a world overview first in the request list.
        int previousZoom = _wanted.Length == 0 ? -1 : _wanted.Max(id => id.Z);
        if (wanted.Length > 0 && previousZoom != wanted.Max(id => id.Z) &&
            _loaded.Keys.Any(id => id.Z == previousZoom))
            _previousLevel = Ready;
        _wanted = wanted;
        foreach (var id in _requests.Keys.Except(wanted).ToArray())
        {
            var request = _requests[id];
            _requests.Remove(id);
            request.Cancel();
        }
        _failed.RemoveWhere(id => !wanted.Contains(id));
        foreach (var id in _loaded.Keys.Except(wanted).ToArray()) _loaded.Remove(id);
        Rebuild();
        foreach (var id in wanted)
        {
            if (_loaded.ContainsKey(id) || _requests.ContainsKey(id)) continue;
            var request = new CancellationTokenSource();
            _requests.Add(id, request);
            _ = Fetch(id, request);
        }
    }

    private async Task Fetch(TileId id, CancellationTokenSource request)
    {
        var token = request.Token;
        int delay = 4;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var tile = await load(id, token);
                    if (!token.IsCancellationRequested)
                    {
                        _loaded[id] = tile;
                        _failed.Remove(id);
                        Rebuild();
                    }
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception)
                {
                    if (token.IsCancellationRequested) return;
                    if (_failed.Add(id)) Rebuild();
                }
                await Task.Delay(TimeSpan.FromSeconds(delay), token);
                delay = Math.Min(60, delay * 2);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (_requests.TryGetValue(id, out var current) && current == request) _requests.Remove(id);
            request.Dispose();
        }
    }

    private void Rebuild()
    {
        Ready = _wanted.Where(_loaded.ContainsKey).Select(id => _loaded[id]).ToArray();
        if (Ready.Count == _wanted.Length || (HasFailures && !retainPreviousOnFailure)) _previousLevel = [];
        changed();
    }

    public void Clear()
    {
        _wanted = [];
        var requests = _requests.Values.ToArray();
        _requests.Clear();
        foreach (var request in requests) request.Cancel();
        _loaded.Clear();
        _failed.Clear();
        Ready = _previousLevel = [];
    }
}
