using Educast.Protocol.Pairing;

namespace Educast.Protocol.Tests.Pairing;

public class JPakeExchangeTests
{
    private static (byte[] Master, byte[] Agent) Run(string masterCode, string agentCode)
    {
        var master = new JPakeExchange(JPakeRole.Master, System.Text.Encoding.ASCII.GetBytes(masterCode));
        var agent = new JPakeExchange(JPakeRole.Agent, System.Text.Encoding.ASCII.GetBytes(agentCode));

        var m1 = master.CreateRound1();
        var a1 = agent.CreateRound1();
        Assert.True(agent.TryAcceptRound1(m1));
        Assert.True(master.TryAcceptRound1(a1));

        var m2 = master.CreateRound2();
        var a2 = agent.CreateRound2();
        Assert.True(agent.TryAcceptRound2(m2));
        Assert.True(master.TryAcceptRound2(a2));

        return (master.DeriveSessionKey(), agent.DeriveSessionKey());
    }

    [Fact]
    public void SameCode_GivesBothSidesTheSame32ByteKey()
    {
        var (master, agent) = Run("482913", "482913");

        Assert.Equal(JPakeExchange.SessionKeyLength, master.Length);
        Assert.Equal(master, agent);
    }

    [Fact]
    public void DifferentCode_GivesDifferentKeys_AndDoesNotThrow()
    {
        // The exchange itself must not reveal whether the code matched: that is
        // learned only from the proofs, which is what limits a stranger to one guess.
        var (master, agent) = Run("482913", "482914");

        Assert.NotEqual(master, agent);
    }

    [Fact]
    public void EveryRun_GivesAFreshKey()
    {
        var first = Run("482913", "482913");
        var second = Run("482913", "482913");

        Assert.NotEqual(first.Master, second.Master);
    }

    [Fact]
    public void Round1_IsRefused_WhenTamperedMalformedOrReflected()
    {
        var master = new JPakeExchange(JPakeRole.Master, "482913"u8);
        var agent = new JPakeExchange(JPakeRole.Agent, "482913"u8);
        var genuine = master.CreateRound1();

        var flipped = genuine.Gx1.ToArray();
        flipped[5] ^= 1;

        Assert.False(agent.TryAcceptRound1(null));
        Assert.False(agent.TryAcceptRound1(genuine with { Gx1 = flipped }));
        Assert.False(agent.TryAcceptRound1(genuine with { Gx1 = [] }));
        Assert.False(agent.TryAcceptRound1(genuine with { Gx2 = [1] }));
        Assert.False(agent.TryAcceptRound1(genuine with { Gx2 = new byte[JPakeExchange.MaxElementLength + 1] }));
        Assert.False(agent.TryAcceptRound1(genuine with { ProofX1 = [genuine.ProofX1[0]] }));
        Assert.False(agent.TryAcceptRound1(agent.CreateRound1())); // its own message echoed back

        // A refused message must not poison the exchange for the genuine one.
        Assert.True(agent.TryAcceptRound1(genuine));
    }

    [Fact]
    public void EmptyCode_IsAProgrammingError()
    {
        Assert.Throws<ArgumentException>(() => new JPakeExchange(JPakeRole.Master, []));
    }
}
