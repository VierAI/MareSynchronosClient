using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlayerSync.FileCache.Migrations
{
    /// <summary>
    /// 1.15.5.1415 (VierAI): file-safety tables. They are not part of the EF model (accessed with raw SQL by
    /// PSyncFileSafetyStore), so the model snapshot does not change. IF NOT EXISTS keeps this safe on any existing DB.
    /// </summary>
    public partial class PSyncFileSafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "PSyncFileApplications" (
                    "Hash" TEXT NOT NULL,
                    "GamePath" TEXT NOT NULL,
                    "LastAppliedTicks" INTEGER NOT NULL,
                    CONSTRAINT "PK_PSyncFileApplications" PRIMARY KEY ("Hash", "GamePath")
                );
                """);
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "PSyncFileApplications_LastAppliedTicks" ON "PSyncFileApplications" ("LastAppliedTicks");""");
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "PSyncBadFiles" (
                    "Hash" TEXT NOT NULL CONSTRAINT "PK_PSyncBadFiles" PRIMARY KEY,
                    "Reason" TEXT NOT NULL,
                    "AddedTicks" INTEGER NOT NULL,
                    "SourcePairUid" TEXT NULL
                );
                """);
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "PSyncApplyMarkers" (
                    "ApplicationId" TEXT NOT NULL CONSTRAINT "PK_PSyncApplyMarkers" PRIMARY KEY,
                    "SessionId" TEXT NOT NULL,
                    "PairUid" TEXT NOT NULL,
                    "MdlHashes" TEXT NOT NULL,
                    "StartedTicks" INTEGER NOT NULL
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "PSyncApplyMarkers";""");
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "PSyncBadFiles";""");
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "PSyncFileApplications";""");
        }
    }
}