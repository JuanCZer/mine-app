using Microsoft.Extensions.Configuration;
using Notes.Api.Data;
using System.Reflection;

var counter = typeof(IMapRepository).Assembly.GetType("Notes.Api.Data.ProximityCounter")!;
var execute = counter.GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Static)!;
var configuration = new ConfigurationBuilder().Build();
var empty = new List<(double Latitude, double Longitude)>();
Task<MapSectors> Run(Func<int, CancellationToken, Task<List<(double Latitude, double Longitude)>>> load, CancellationToken token = default) =>
    (Task<MapSectors>)execute.Invoke(null, [configuration, load, token])!;
async Task Expect<T>(Task task) where T : Exception
{
    try { await task; throw new Exception($"Expected {typeof(T).Name}"); }
    catch (T) { }
}
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var cancel = new CancellationTokenSource();
var first = Run(async (_, ct) => { firstEntered.SetResult(); await release.Task.WaitAsync(ct); return empty; });
await firstEntered.Task;
var second = Run(async (_, ct) => { secondEntered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return empty; }, cancel.Token);
await secondEntered.Task;
await Expect<NoteRepositoryUnavailableException>(Run((_, _) => Task.FromResult(empty)));
release.SetResult();
await first;
await Run((_, _) => Task.FromResult(empty));
cancel.Cancel();
await Expect<OperationCanceledException>(second);
await Run((_, _) => Task.FromResult(empty));
await Expect<InvalidOperationException>(Run((_, _) => throw new InvalidOperationException()));
await Run((_, _) => Task.FromResult(empty));
using var alreadyCancelled = new CancellationTokenSource();
alreadyCancelled.Cancel();
await Expect<OperationCanceledException>(Run((_, _) => Task.FromResult(empty), alreadyCancelled.Token));
await Run((_, _) => Task.FromResult(empty));
Console.WriteLine("Proximity concurrency, cancellation and gate release: PASS");
