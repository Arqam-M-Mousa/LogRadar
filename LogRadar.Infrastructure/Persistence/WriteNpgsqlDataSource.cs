using Npgsql;

namespace LogRadar.Infrastructure.Persistence;

public sealed class WriteNpgsqlDataSource
{
    public WriteNpgsqlDataSource(NpgsqlDataSource dataSource)
    {
        DataSource = dataSource;
    }

    public NpgsqlDataSource DataSource { get; }
}
