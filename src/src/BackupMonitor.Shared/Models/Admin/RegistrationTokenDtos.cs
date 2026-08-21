using System.ComponentModel.DataAnnotations;

namespace BackupMonitor.Shared.Models.Admin;

public sealed class RegistrationTokenQuery
{
    public bool IncludeInactive { get; set; }
}

public sealed class CreateRegistrationTokenRequest
{
    [Required, StringLength(128, MinimumLength = 1)]
    public string Name { get; set; } = null!;

    public Guid? ClientGroupId { get; set; }
    public DateTime? ExpiresAt { get; set; }

    [Range(1, 10000)]
    public int MaxUses { get; set; } = 1;
}

public sealed class RegistrationTokenDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public Guid? ClientGroupId { get; set; }
    public string? ClientGroupName { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int MaxUses { get; set; }
    public int UsedCount { get; set; }
    public string Status { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

public sealed class CreateRegistrationTokenResponse
{
    public string Token { get; set; } = null!;
    public RegistrationTokenDto RegistrationToken { get; set; } = null!;
}

public sealed class RevokeRegistrationTokenRequest
{
    public string? Reason { get; set; }
}
