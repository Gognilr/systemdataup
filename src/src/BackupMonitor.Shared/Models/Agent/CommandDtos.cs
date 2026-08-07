using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Agent;

/// <summary>领取指令请求（设计书 12.1）</summary>
public class ClaimCommandsRequest
{
    [Range(1, 50)]
    public int MaxItems { get; set; } = 5;

    /// <summary>客户端支持的指令类型（snake_case）</summary>
    public List<string> SupportedCommandTypes { get; set; } = [];
}

/// <summary>领取指令响应（设计书 12.1）</summary>
public class ClaimCommandsResponse
{
    public List<CommandDto> Commands { get; set; } = [];
}

/// <summary>指令对象</summary>
public class CommandDto
{
    public Guid Id { get; set; }

    /// <summary>指令类型（snake_case，如 precheck_task）</summary>
    public string Type { get; set; } = null!;

    public Guid? TaskId { get; set; }
    public Guid? CandidateBackupSetId { get; set; }

    /// <summary>指令参数（jsonb 原样下发）</summary>
    public string? Payload { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>防重放一次性随机数</summary>
    public string Nonce { get; set; } = null!;

    /// <summary>服务端签名（客户端校验指令来源）</summary>
    public string Signature { get; set; } = null!;
}

/// <summary>上报指令开始（设计书 12.2，空体）</summary>
public class CommandStartedRequest
{
}

/// <summary>上报指令进度（设计书 12.3）</summary>
public class CommandProgressRequest
{
    [Range(0, 100)]
    public decimal Percent { get; set; }

    public string? Stage { get; set; }
    public string? Message { get; set; }
}

/// <summary>上报指令完成（设计书 12.4）</summary>
public class CommandCompletedRequest
{
    public bool Success { get; set; }

    /// <summary>结果码（如 OK / TASK_PATH_NOT_FOUND）</summary>
    public string? ResultCode { get; set; }

    public string? ResultMessage { get; set; }

    /// <summary>执行结果数据（jsonb 原样上报）</summary>
    public string? Result { get; set; }
}
