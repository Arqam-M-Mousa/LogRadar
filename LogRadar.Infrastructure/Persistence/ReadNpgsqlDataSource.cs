using Npgsql;

namespace LogRadar.Infrastructure.Persistence;

public sealed class ReadNpgsqlDataSource
{
    public ReadNpgsqlDataSource(NpgsqlDataSource dataSource)
    {
        DataSource = dataSource;
    }

    public NpgsqlDataSource DataSource { get; }
}
