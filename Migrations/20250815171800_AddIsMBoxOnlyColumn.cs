using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <inheritdoc />
    public partial class AddIsMBoxOnlyColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add IsMBoxOnly column to MailAccounts table if it doesn't exist
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                                   WHERE table_schema = 'mail_archiver' 
                                   AND table_name = 'MailAccounts' 
                                   AND column_name = 'IsMBoxOnly') THEN
                        ALTER TABLE mail_archiver.""MailAccounts"" 
                        ADD COLUMN ""IsMBoxOnly"" boolean NOT NULL DEFAULT false;
                    END IF;
                END $$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove IsMBoxOnly column from MailAccounts table
            migrationBuilder.Sql(@"
                ALTER TABLE mail_archiver.""MailAccounts"" 
                DROP COLUMN IF EXISTS ""IsMBoxOnly"";
            ");
        }
    }
}