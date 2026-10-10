using Promin.Protocol.Pairing;

namespace Promin.Protocol.Tests.Pairing;

public class PairingBackoffTests
{
    [Fact]
    public void Delays_DoubleFromTwoSeconds_AndStopAtFiveMinutes()
    {
        var backoff = new PairingBackoff();

        var seconds = Enumerable.Range(0, 10).Select(_ => backoff.RecordFailure().TotalSeconds).ToArray();

        Assert.Equal([2, 4, 8, 16, 32, 64, 128, 256, 300, 300], seconds);
    }

    [Fact]
    public void Success_StartsTheSequenceAgain()
    {
        var backoff = new PairingBackoff();
        backoff.RecordFailure();
        backoff.RecordFailure();

        backoff.RecordSuccess();

        Assert.Equal(TimeSpan.FromSeconds(2), backoff.RecordFailure());
    }

    [Fact]
    public void ManyFailures_DoNotOverflow()
    {
        var backoff = new PairingBackoff();

        var last = TimeSpan.Zero;
        for (var i = 0; i < 10_000; i++)
        {
            last = backoff.RecordFailure();
        }

        Assert.Equal(PairingBackoff.Longest, last);
    }
}
