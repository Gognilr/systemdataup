namespace BackupMonitor.Core.Abstractions;

/// <summary>
/// 乐观锁标记接口。实现该接口的实体在 UPDATE 时由应用层递增 RowVersion，
/// EF Core 通过并发令牌校验（WHERE row_version = 原值）防止丢失更新。
/// </summary>
public interface IHasRowVersion
{
    long RowVersion { get; set; }
}
