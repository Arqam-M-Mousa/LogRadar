using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LogRadar.Infrastructure.Migrations;

public partial class AddBrinTimestampIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE INDEX IF NOT EXISTS "IX_log_Timestamp_brin"
            ON log USING brin ("Timestamp")
            WITH (pages_per_range = 32);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_log_Timestamp_brin";""");
    }
}
