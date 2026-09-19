using System.Diagnostics;
using DeviceHub.Core.Acquisition;
using DeviceHub.Drivers.Simulated;
using Xunit;

namespace DeviceHub.Tests;

public class AcquisitionEngineTests
{
    private static async Task<AcquisitionEngine> CreateStartedEngineAsync()
    {
        var driver = new SimulatedDriver();
        await driver.ConnectAsync();
        var engine = new AcquisitionEngine(
            driver,
            SimulatedDriver.DefaultPoints,
            TimeSpan.FromMilliseconds(50));
        engine.Start();
        return engine;
    }

    [Fact]
    public async Task Start_ShouldRaisePointReadRepeatedly()
    {
        var engine = await CreateStartedEngineAsync();

        try
        {
            var count = 0;
            engine.PointRead += _ => Interlocked.Increment(ref count);

            var stopwatch = Stopwatch.StartNew();
            while (count < 10 && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(20);
            }

            Assert.True(count >= 10, $"5 秒内应至少收到 10 次读数，实际 {count} 次");
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopAsync_ShouldStopTheLoop()
    {
        var engine = await CreateStartedEngineAsync();

        try
        {
            var count = 0;
            engine.PointRead += _ => Interlocked.Increment(ref count);

            var stopwatch = Stopwatch.StartNew();
            while (count < 10 && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(20);
            }

            await engine.StopAsync();
            Assert.False(engine.IsRunning);

            var countAtStop = count;
            await Task.Delay(300);

            Assert.True(count >= 10);
            Assert.Equal(countAtStop, count);
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }
}
