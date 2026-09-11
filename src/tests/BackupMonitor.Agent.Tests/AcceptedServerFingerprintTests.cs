using BackupMonitor.Agent;

namespace BackupMonitor.Agent.Tests;

/// <summary>
/// 服务端指纹的接受集合（整改清单 R9 过渡期）。
///
/// 背景是一个死结：Agent 按指纹固定连服务端，而「重新签发服务端证书」会改变指纹，
/// 于是全网 Agent 立刻连不上；连不上就收不到新指纹，唯一的出路是挨台机器重跑安装器。
/// 服务端证书只有两年有效期，这件事迟早要做一次，而它现在做不了。
///
/// 解法是过渡期里同时接受两个指纹。这段逻辑决定「哪些服务端证书算数」，
/// 写错的后果是接受一张不该接受的证书——那正是指纹固定要防的事，
/// 所以它不能只靠读代码确认。
/// </summary>
public class AcceptedServerFingerprintTests
{
    [Fact]
    public void 没有过渡指纹时只接受固定的那一个()
    {
        var accepted = AgentApiClient.BuildAcceptedFingerprints("AA:BB:CC", null);

        Assert.Single(accepted);
        Assert.Equal("aabbcc", accepted[0]);
    }

    [Fact]
    public void 过渡期内新旧两个指纹都接受()
    {
        var accepted = AgentApiClient.BuildAcceptedFingerprints("AA:BB:CC", "DD:EE:FF");

        Assert.Equal(2, accepted.Length);
        Assert.Contains("aabbcc", accepted);
        Assert.Contains("ddeeff", accepted);
    }

    /// <summary>
    /// 过渡指纹和固定指纹是同一个值时不能变成两项。
    /// 重复项本身无害，但它会让「现在接受几个指纹」这个数字失去意义——
    /// 而排障时正是靠它判断「这台机器到底在不在过渡期」。
    /// </summary>
    [Fact]
    public void 过渡指纹与固定指纹相同时不重复()
    {
        var accepted = AgentApiClient.BuildAcceptedFingerprints("aabbcc", "AA:BB:CC");

        Assert.Single(accepted);
    }

    /// <summary>
    /// 空串必须等同于「不在过渡期」。服务端把过渡配置清掉时下发的就是空值，
    /// 这里若把它当成一个指纹留在集合里，就会去比对一个空指纹。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空的过渡指纹等同于不在过渡期(string next)
    {
        var accepted = AgentApiClient.BuildAcceptedFingerprints("AA:BB:CC", next);

        Assert.Single(accepted);
        Assert.Equal("aabbcc", accepted[0]);
    }
}
