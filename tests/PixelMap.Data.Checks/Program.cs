using System.Net;
using PixelMap.Data;

var handler = new TileHandler();
using var http = new HttpClient(handler);
var source = new VectorTileSource(http);
var seed = await source.GetTileAsync(new(14, 4823, 6160));
var buildings = seed.Features.Where(f => f.Layer == "building").ToArray();
Require(buildings.Any(f => f.Height > 100), "Lower Manhattan has buildings taller than 100 metres.");
Require(buildings.SelectMany(f => f.Paths).Any(ring => SignedArea(ring) < 0),
    "The source retains interior rings with their original winding.");
Require(handler.Requests == 0, "Initial geometry must work while offline.");
Require(ReferenceEquals(seed, await source.GetTileAsync(seed.Id)), "Decoded tiles are reused.");

try
{
    VectorTileDecoder.Decode(new(0, 0, 0), [26, 3, 10, 1]);
    throw new Exception("A truncated protobuf tile must be rejected.");
}
catch (InvalidDataException) { }

// Reuse a real seed as an HTTP response. One waiter leaves while the second still needs the tile.
TileId tileId = new(14, 8300, 5500);
using var firstWaiter = new CancellationTokenSource();
var first = source.GetTileAsync(tileId, firstWaiter.Token);
var second = source.GetTileAsync(tileId);
await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
firstWaiter.Cancel();
await RequireCanceled(first);
Require(!second.IsCompleted, "Canceling one waiter must preserve the shared tile request.");
handler.Release.TrySetResult();
var online = await second;
Require(handler.Requests == 1 && online.Features.Count == seed.Features.Count,
    "Both consumers must share one HTTP request and gzip must round-trip.");

handler.Reset();
using var lastWaiter = new CancellationTokenSource();
var stale = source.GetTileAsync(new(14, 8301, 5500), lastWaiter.Token);
await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
lastWaiter.Cancel();
await RequireCanceled(stale);
await handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
handler.Release.TrySetResult();
var retried = await source.GetTileAsync(new(14, 8301, 5500));
Require(retried.Features.Count == seed.Features.Count && handler.Requests == 3,
    "An abandoned fetch must be retryable.");

Console.WriteLine($"Tile geometry and request lifetime checks passed ({buildings.Length} anchor-tile building features).");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task RequireCanceled(Task task)
{
    try { await task; }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("Expected cancellation.");
}

static long SignedArea(TilePoint[] ring)
{
    long twiceArea = 0;
    for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        twiceArea += (long)ring[j].X * ring[i].Y - (long)ring[i].X * ring[j].Y;
    return twiceArea;
}

sealed class TileHandler : HttpMessageHandler
{
    public int Requests;
    public TaskCompletionSource Started = Signal(), Release = Signal(), Canceled = Signal();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Reset() { Started = Signal(); Release = Signal(); Canceled = Signal(); }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri!.AbsolutePath == "/planet")
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tiles":["https://unit.test/{z}/{x}/{y}.pbf"]}""")
            };

        Interlocked.Increment(ref Requests);
        Started.TrySetResult();
        try { await Release.Task.WaitAsync(token); }
        catch (OperationCanceledException) { Canceled.TrySetResult(); throw; }
        var bytes = typeof(VectorTileSource).Assembly.GetManifestResourceStream("PixelMap.Seed.14-4823-6160.pbf.gz")!;
        return new(HttpStatusCode.OK) { Content = new StreamContent(bytes) };
    }
}
