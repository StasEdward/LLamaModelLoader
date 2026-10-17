using LLamaModelLoader.Infrastructure;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class SingleInstanceTests
{
    // Synchronous on purpose: the mutex is thread-affine, as on the application's UI thread, so awaits could release it from another thread.
#pragma warning disable xUnit1031
    [Fact]
    public void RepeatedListenReturnsWithoutBlockingAndStillActivates()
    {
        var scope = "single-instance-test-" + Guid.NewGuid();
        using var instance = new SingleInstance(scope);
        Assert.True(instance.IsFirst);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = instance.ListenAsync(() => activated.TrySetResult());

        // The window raises Opened again after being shown from the tray; a second listen must not spin the caller.
        var second = Task.Factory.StartNew(() => instance.ListenAsync(() => activated.TrySetResult()), TaskCreationOptions.LongRunning);
        Assert.True(second.Wait(TimeSpan.FromSeconds(5)), "A repeated ListenAsync call blocked its caller.");

        using var client = new SingleInstance(scope);
        Assert.False(client.IsFirst);
        client.NotifyAsync().GetAwaiter().GetResult();
        Assert.True(activated.Task.Wait(TimeSpan.FromSeconds(5)), "The running instance was not activated.");
    }
#pragma warning restore xUnit1031
}
