namespace BackupMonitor.Agent.Tests;

/// <summary>
/// 升级包来源校验（审计 C-01 的纵深防御那一层）。
///
/// 升级是全系统唯一一条「服务端说什么，客户端就执行什么代码」的路径。
/// 指令签名已经覆盖了 payload，这一层在理论上是冗余的，但值得两道锁：
/// 一旦签名校验哪天被绕过，这里是最后一道拦住「从任意地址下载并解压执行」的关卡。
///
/// 这个方法当初就是为了能测才拆成静态的，却一直没有用例。
/// </summary>
public class UpgradePackageOriginTests
{
    [Theory]
    [InlineData("https://srv.local:8443/downloads/agent.zip", "https://srv.local:8443")]
    [InlineData("http://srv.local:8443/downloads/agent.zip", "https://srv.local:8443")]
    [InlineData("https://SRV.LOCAL:8443/downloads/agent.zip", "https://srv.local:8443")]
    public void 同一个主机和端口放行(string packageUrl, string serverUrl)
    {
        // 只比 host:port，不比 scheme——Secure 形态下 nginx 终结 TLS 后回环到 Kestrel，
        // 两端的 scheme 本来就可能不同。主机名大小写同理，DNS 不区分大小写。
        Assert.True(AgentWorker.IsSameServerAuthority(new Uri(packageUrl), serverUrl));
    }

    [Theory]
    [InlineData("https://evil.example/agent.zip", "https://srv.local:8443")]
    [InlineData("https://srv.local:9999/agent.zip", "https://srv.local:8443")]
    public void 换了主机或端口一律拒绝(string packageUrl, string serverUrl)
    {
        // 端口也算身份的一部分：同一台机器上的另一个端口可能是完全不受控的服务。
        Assert.False(AgentWorker.IsSameServerAuthority(new Uri(packageUrl), serverUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("这不是一个地址")]
    public void 服务端地址解析不出来时拒绝(string? serverUrl)
    {
        // 配置坏掉的时候更不该放行任意下载——「比不了」必须等于「不放行」，
        // 而不是等于「放行」。
        Assert.False(AgentWorker.IsSameServerAuthority(
            new Uri("https://srv.local:8443/agent.zip"), serverUrl));
    }

    [Fact]
    public void 默认端口要按协议补齐后再比()
    {
        // https 不写端口就是 443。Uri.Port 会补默认值，两边补出来的必须一致，
        // 否则「同一个地址写法不同」会被判成不同的服务端。
        Assert.True(AgentWorker.IsSameServerAuthority(
            new Uri("https://srv.local/downloads/agent.zip"), "https://srv.local"));
        Assert.False(AgentWorker.IsSameServerAuthority(
            new Uri("https://srv.local/downloads/agent.zip"), "http://srv.local"));
    }
}
