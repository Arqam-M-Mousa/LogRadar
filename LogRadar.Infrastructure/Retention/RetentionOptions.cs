namespace LogRadar.Infrastructure.Retention;

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public int RetentionDays { get; init; } = 30;
    public TimeSpan RunAtUtc { get; init; } = TimeSpan.FromHours(2);
    public int DeleteBatchSize { get; init; } = 10_000;
}
