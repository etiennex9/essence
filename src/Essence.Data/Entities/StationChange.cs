namespace Essence.Data.Entities;

/// <summary>One field of a station that changed in an import (or <c>created</c> / <c>is_active</c>).</summary>
public sealed class StationChange
{
    public const string Created = "created";
    public const string IsActive = "is_active";

    public long Id { get; set; }
    public long StationId { get; set; }
    public long ImportId { get; set; }

    /// <summary>Column name of the changed field, e.g. <c>brand</c>, or <see cref="Created"/> / <see cref="IsActive"/>.</summary>
    public required string Field { get; set; }

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    /// <summary>The feed's <c>generated_at</c>.</summary>
    public DateTimeOffset ChangedAt { get; set; }

    public Station Station { get; set; } = null!;
    public Import Import { get; set; } = null!;
}
