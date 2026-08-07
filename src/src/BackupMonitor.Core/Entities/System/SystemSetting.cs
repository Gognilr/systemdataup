using BackupMonitor.Core.Abstractions;

namespace BackupMonitor.Core.Entities.System;

/// <summary>系统配置（敏感配置必须应用层加密），对应 system_settings 表。
/// 主键为 setting_key（varchar），非 UUID。</summary>
public class SystemSetting : IHasRowVersion
{
    /// <summary>配置键（主键，如 repository_path）</summary>
    public string SettingKey { get; set; } = null!;

    /// <summary>配置值（jsonb 序列化存储）</summary>
    public string SettingValue { get; set; } = null!;

    /// <summary>值是否加密存储</summary>
    public bool Encrypted { get; set; }

    /// <summary>最后修改人</summary>
    public Guid? UpdatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>乐观锁版本号</summary>
    public long RowVersion { get; set; }

    // 导航属性
    public Rbac.User? UpdatedByUser { get; set; }
}
