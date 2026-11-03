using System.Collections.Concurrent;
using PixelMap.Controls;
using PixelMap.Data;

internal static class TileViewportChecks
{
    internal static void Run()
    {
        var previous = SynchronizationContext.Current;
        using var context = new SerialContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            CheckTransitions(includeWorld: false, retainPrevious: false);
            CheckTransitions(includeWorld: true, retainPrevious: false);
            CheckTransitions(includeWorld: true, retainPrevious: true);
            var retries = Task.WhenAll(CheckRetry(worldFails: true), CheckRetry(worldFails: false));
            while (!retries.IsCompleted) context.Dispatch();
            retries.GetAwaiter().GetResult();
            context.Drain();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Console.WriteLine("Tile viewport checks passed for mixed LOD, retained fallback, retries and cancellation.");
    }

    private static void CheckTransitions(bool includeWorld, bool retainPrevious)
    {
        var world = new Resident(new(0, 0, 0));
        var old = new Resident(new(4, 6, 6));
        var next = new Resident(new(5, 12, 12));
        var halo = new Resident(new(5, 13, 12));
        var pending = new Resident(new(6, 24, 24));
        var latest = new Resident(new(7, 48, 48));
        var loader = new ControlledSource(world, old);
        var view = new TileViewport<Resident>(loader.Load, () => { }, retainPrevious);
        TileId[] Wanted(params Resident[] tiles) => includeWorld
            ? new[] { world.Id }.Concat(tiles.Select(tile => tile.Id)).ToArray()
            : tiles.Select(tile => tile.Id).ToArray();

        view.Update(Wanted(old));
        Require(view.Ready.Contains(old), "Initial detail becomes resident.");
        view.Update(Wanted(next, halo));
        Require(view.Display.Contains(old) && !view.Ready.Contains(old), "New LOD preserves the previous displayed detail.");
        loader.Complete(next);
        Require(view.Ready.Contains(next) && view.Display.Contains(old), "Partially ready detail coexists with the prior display.");
        int requests = loader.TotalRequests;
        view.Update(Wanted(halo, next));
        Require(loader.TotalRequests == requests, "Priority reordering reuses pending and ready tiles.");
        loader.Fail(halo.Id);
        Require(view.HasFailures && view.Display.Contains(old) == retainPrevious,
            "Elevation retains failed-request fallback while default vector/imagery behavior stays unchanged.");
        view.Update(Wanted(pending));
        Require(view.Display.Contains(next), "The next zoom retains ready native detail.");
        view.Update(Wanted(latest));
        Require(view.Display.Contains(next), "A second rapid zoom cannot replace detailed fallback with world-only readiness.");
        loader.Complete(pending);
        Require(!view.Ready.Contains(pending), "A superseded request cannot repopulate residency.");
        loader.Complete(latest);
        Require(view.Display.Contains(latest) && !view.Display.Contains(next), "Complete new coverage releases the previous generation.");
        if (includeWorld) Require(loader.Requests(world.Id) == 1, "The world fallback stays resident across detail zooms.");

        var afterClear = new Resident(new(8, 96, 96));
        view.Update(Wanted(afterClear));
        view.Clear();
        loader.Complete(afterClear);
        Require(view.Count == 0 && view.Ready.Count == 0 && view.Display.Count == 0 && !view.HasFailures,
            "Clear cancels pending work and discards all generations.");
        view.Update(Wanted(old));
        Require(view.Ready.Contains(old), "The viewport reloads after clear.");
        view.Clear();
    }

    private static async Task CheckRetry(bool worldFails)
    {
        var world = new Resident(new(0, 0, 0));
        var old = new Resident(new(4, 6, 6));
        var next = new Resident(new(5, 12, 12));
        var loader = worldFails ? new ControlledSource(next) : new ControlledSource(world, old);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TileViewport<Resident>? view = null;
        view = new(loader.Load, () =>
        {
            if (view!.Ready.Contains(world) && view.Ready.Contains(next) && !view.HasFailures) completed.TrySetResult();
        }, retainPreviousOnFailure: true);
        if (!worldFails) view.Update([world.Id, old.Id]);
        view.Update([world.Id, next.Id]);
        var failed = worldFails ? world : next;
        loader.Fail(failed.Id);
        Require(view.HasFailures, "Invalid world and detail payloads enter the same retry path.");
        if (!worldFails) Require(view.Display.Contains(old), "Fine-tile failure retains the prior elevation generation.");
        loader.Complete(failed);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(loader.Requests(failed.Id) == 2 && view.Display.Contains(next) && !view.Display.Contains(old),
            "Provider recovery retries once and releases the prior display when coverage completes.");
        view.Clear();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Resident(TileId Id);

    private sealed class ControlledSource(params Resident[] initial)
    {
        private readonly Dictionary<TileId, Resident> _ready = initial.ToDictionary(tile => tile.Id);
        private readonly Dictionary<TileId, TaskCompletionSource<Resident>> _pending = [];
        private readonly Dictionary<TileId, int> _requests = [];
        internal int TotalRequests => _requests.Values.Sum();
        internal int Requests(TileId id) => _requests.GetValueOrDefault(id);

        internal Task<Resident> Load(TileId id, CancellationToken token)
        {
            _requests[id] = Requests(id) + 1;
            if (_ready.TryGetValue(id, out var tile)) return Task.FromResult(tile);
            _pending.Add(id, new());
            // Ignoring cancellation here exercises the viewport's late-completion guard.
            return _pending[id].Task;
        }

        internal void Complete(Resident tile)
        {
            _ready[tile.Id] = tile;
            if (_pending.Remove(tile.Id, out var pending)) pending.SetResult(tile);
        }

        internal void Fail(TileId id)
        {
            if (!_pending.Remove(id, out var pending)) throw new InvalidOperationException("No pending request to fail.");
            pending.SetException(new InvalidDataException("Test provider payload"));
        }
    }

    // Match the control's UI-thread ownership while real retry timers run asynchronously.
    private sealed class SerialContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));
        internal void Dispatch() { var work = _queue.Take(); work.Callback(work.State); }
        internal void Drain() { while (_queue.TryTake(out var work)) work.Callback(work.State); }
        public void Dispose() => _queue.Dispose();
    }
}
