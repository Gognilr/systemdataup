using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Admin;

/// <summary>一条业务系统探测（列表 / 详情）。</summary>
public class MonitoredEndpointDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public Guid? ClientId { get; set; }

    public string? ClientName { get; set; }

    /// <summary>tcp / http</summary>
    public string ProbeType { get; set; } = "tcp";

    public string Target { get; set; } = null!;

    public int? Port { get; set; }

    public string? ExpectedStatus { get; set; }

    public string? ExpectedContent { get; set; }

    public int TimeoutSeconds { get; set; } = 10;

    public int IntervalSeconds { get; set; } = 60;

    public int FailureThreshold { get; set; } = 3;

    public int? SlowMilliseconds { get; set; }

    public bool Enabled { get; set; } = true;

    public bool AlertOnFailure { get; set; } = true;

    public DateTime? LastProbedAt { get; set; }

    public DateTime? LastSuccessAt { get; set; }

    /// <summary>up / down；从没探过时为空。</summary>
    public string? LastStatus { get; set; }

    public int? LastLatencyMs { get; set; }

    public string? LastError { get; set; }

    public int ConsecutiveFailures { get; set; }
}

/// <summary>新建 / 修改一条探测。</summary>
public class MonitoredEndpointUpsertDto
{
    [Required(ErrorMessage = "请填写名称")]
    [StringLength(128)]
    public string Name { get; set; } = null!;

    public Guid? ClientId { get; set; }

    /// <summary>tcp / http</summary>
    public string ProbeType { get; set; } = "tcp";

    [Required(ErrorMessage = "请填写探测地址")]
    [StringLength(1024)]
    public string Target { get; set; } = null!;

    [Range(1, 65535, ErrorMessage = "端口必须在 1~65535 之间")]
    public int? Port { get; set; }

    [StringLength(64)]
    public string? ExpectedStatus { get; set; }

    [StringLength(512)]
    public string? ExpectedContent { get; set; }

    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 10;

    [Range(10, 86400)]
    public int IntervalSeconds { get; set; } = 60;

    [Range(1, 100)]
    public int FailureThreshold { get; set; } = 3;

    [Range(1, 600000)]
    public int? SlowMilliseconds { get; set; }

    public bool Enabled { get; set; } = true;

    public bool AlertOnFailure { get; set; } = true;
}

/// <summary>
/// 「立即试一次」的结果。
///
/// 回显正文是这个接口存在的主要理由：HTTP 探测真正管用的那一项是「期望包含文本」，
/// 而它最容易配错——填的词如果在错误页里也出现，这条探测就永远是绿的，
/// 而人以为它在把关。照着真实响应挑词，比凭猜靠谱得多。
/// </summary>
public class EndpointProbeResultDto
{
    public bool Success { get; set; }

    public int LatencyMs { get; set; }

    public int? StatusCode { get; set; }

    public string? Error { get; set; }

    /// <summary>响应正文开头若干字符；TCP 探测为空。</summary>
    public string? BodyPreview { get; set; }
}
