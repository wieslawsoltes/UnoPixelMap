namespace PixelMap.Data;

/// <summary>Coalesces requests by XYZ, bounds parallelism, and evicts least-recently-used tiles by byte cost.</summary>
internal sealed class TileRequestCache<T>(
    int maxZoom, long byteBudget, Func<TileId, CancellationToken, Task<T>> fetch, Func<T, long> estimatedSize)
{
    private readonly object _sync = new();
    private readonly Dictionary<TileId, Request> _pending = [];
    private readonly Dictionary<TileId, LinkedListNode<CachedTile>> _index = [];
    private readonly LinkedList<CachedTile> _lru = new();
    private readonly SemaphoreSlim _slots = new(4);
    private long _cacheBytes;

    private sealed record CachedTile(TileId Id, T Tile, long Bytes);
    private sealed class Request
    {
        public int Waiters;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task<T> GetTileAsync(TileId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id.Z < 0 || id.Z > maxZoom || (uint)id.X >= 1u << id.Z || (uint)id.Y >= 1u << id.Z)
            throw new ArgumentOutOfRangeException(nameof(id), "Supply a canonical XYZ tile at a native zoom.");
        Request request;
        bool start = false;
        lock (_sync)
        {
            if (_index.TryGetValue(id, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Tile;
            }
            if (!_pending.TryGetValue(id, out request!))
            {
                _pending.Add(id, request = new());
                start = true;
            }
            request.Waiters++;
        }

        if (start) _ = ProduceAsync(id, request);
        try { return await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_sync)
            {
                if (--request.Waiters == 0 && _pending.TryGetValue(id, out var current) && current == request)
                {
                    _pending.Remove(id);
                    request.Cancellation.Cancel();
                }
            }
        }
    }

    private async Task ProduceAsync(TileId id, Request request)
    {
        try
        {
            var token = request.Cancellation.Token;
            await _slots.WaitAsync(token).ConfigureAwait(false);
            T tile;
            try { tile = await fetch(id, token).ConfigureAwait(false); }
            finally { _slots.Release(); }

            long bytes = estimatedSize(tile);
            lock (_sync)
            {
                if (!token.IsCancellationRequested)
                {
                    _index[id] = _lru.AddFirst(new CachedTile(id, tile, bytes));
                    _cacheBytes += bytes;
                    while ((_cacheBytes > byteBudget || _lru.Count > 96) && _lru.Last is { } last)
                    {
                        _index.Remove(last.Value.Id);
                        _cacheBytes -= last.Value.Bytes;
                        _lru.RemoveLast();
                    }
                }
                Retire(id, request);
            }
            request.Completion.TrySetResult(tile);
        }
        catch (Exception error)
        {
            lock (_sync) Retire(id, request);
            if (error is OperationCanceledException) request.Completion.TrySetCanceled();
            else request.Completion.TrySetException(error);
        }
        finally { request.Cancellation.Dispose(); }
    }

    private void Retire(TileId id, Request request)
    {
        if (_pending.TryGetValue(id, out var current) && current == request) _pending.Remove(id);
    }

}
