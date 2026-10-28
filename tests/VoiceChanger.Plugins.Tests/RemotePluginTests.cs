using System.Diagnostics;
using VoiceChanger.Plugins.Hosting;
using static VoiceChanger.Plugins.Tests.TestAudio;

namespace VoiceChanger.Plugins.Tests;

/// <summary>A plug-in in a host process of its own: crashes, hangs and the host's lifetime.</summary>
public sealed class RemotePluginTests
{
    private const int Rate = 48000;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public void The_plugin_runs_in_a_host_process_that_ends_with_it()
    {
        var effect = Open();
        Assert.True(effect.RunsInSeparateProcess);
        int host = HostOf(effect);
        string session = ((RemotePlugin)effect.Instance).Session;
        Assert.NotEqual(Environment.ProcessId, host);

        effect.Dispose();

        Assert.True(HasEnded(host));
        if (Directory.Exists("/dev/shm"))
        {
            // Its shared memory and signals are gone too.
            Assert.DoesNotContain(Directory.GetFiles("/dev/shm"), file => Path.GetFileName(file).Contains(session, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void After_a_crash_the_plugin_starts_again_with_its_settings()
    {
        using var effect = Open();
        effect.Prepare(Rate, 1);
        effect.GetParameter("0").Value = (float)TestPlugin.GainValue(-6);
        effect.GetParameter("1").Value = 1; // 64 samples of latency
        int firstHost = HostOf(effect);
        var input = Enumerable.Repeat(0.1f, 256).ToArray();

        effect.SetNormalized(TestPlugin.Crash, 1);
        var output = Run(effect, input);

        // The block the plug-in crashed in passes through, and so does audio until it is back.
        AssertClose(input, output, 0);
        Assert.True(HasEnded(firstHost));
        Assert.True(SpinWait.SpinUntil(effect.Recover, Patience));
        Assert.False(effect.HasCrashed);
        Assert.NotEqual(firstHost, HostOf(effect));

        // Back with its settings (and without the crash switch).
        Assert.Equal(TestPlugin.GainValue(-6), effect.GetParameter("0").Value, 5);
        Assert.Equal(64, effect.LatencyFrames);
        Run(effect, input);
        output = Run(effect, input);
        Assert.Equal(0.1f * MathF.Pow(10, -6 / 20f), output[^1], 4);
    }

    [Fact]
    public void A_plugin_that_stops_answering_is_restarted()
    {
        using var effect = Open();
        effect.Prepare(Rate, 1);
        int host = HostOf(effect);
        var input = Enumerable.Repeat(0.1f, 256).ToArray();

        effect.SetNormalized(TestPlugin.Hang, 1);
        var watch = Stopwatch.StartNew();
        var output = Run(effect, input);

        // The audio waited at most the hang timeout, then went on without the plug-in.
        Assert.InRange(watch.ElapsedMilliseconds, RemotePlugin.HangTimeoutMilliseconds - 50, RemotePlugin.HangTimeoutMilliseconds + 2000);
        AssertClose(input, output, 0);
        Assert.True(HasEnded(host));
        Assert.True(SpinWait.SpinUntil(effect.Recover, Patience));

        output = Run(effect, input);
        AssertClose(input, output, 1e-6f);
    }

    [Fact]
    public void A_plugin_that_keeps_crashing_is_given_up()
    {
        using var effect = Open();
        effect.Prepare(Rate, 1);
        int crashedEvents = 0;
        effect.Crashed += (_, _) => crashedEvents++;
        var input = Enumerable.Repeat(0.1f, 64).ToArray();

        for (int restart = 0; restart < PluginEffect.MaxRestarts; restart++)
        {
            effect.SetNormalized(TestPlugin.Crash, 1);
            Run(effect, input);
            Assert.True(SpinWait.SpinUntil(effect.Recover, Patience));
        }

        effect.SetNormalized(TestPlugin.Crash, 1);
        Run(effect, input);
        Assert.True(SpinWait.SpinUntil(() => !effect.Instance.IsAlive, Patience));
        SpinWait.SpinUntil(() => effect.Recover() || effect.HasCrashed, Patience);

        Assert.True(effect.HasCrashed);
        Assert.Equal(1, crashedEvents);
        Assert.NotNull(effect.Problem);
        AssertClose(input, Run(effect, input), 0);
    }

    [Fact]
    public void A_host_that_does_not_exist_is_a_load_error()
    {
        var hosting = new PluginHosting
        {
            SeparateProcesses = true,
            HostCommand = new PluginHostCommand(Path.Combine(AppContext.BaseDirectory, "no-such-host"), []),
        };

        var error = Assert.Throws<PluginException>(() => TestPlugin.Open(hosting));
        Assert.Contains(TestPlugin.ClassName, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Opens the plug-in without a synchronisation context, so it doesn't restart on its own after
    /// a crash: the tests call <see cref="PluginEffect.Recover"/> when they are ready.
    /// </summary>
    private static PluginEffect Open()
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return TestPlugin.Open(TestPlugin.SeparateProcesses);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private static int HostOf(PluginEffect effect) => ((RemotePlugin)effect.Instance).HostProcess.Id;

    private static bool HasEnded(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
