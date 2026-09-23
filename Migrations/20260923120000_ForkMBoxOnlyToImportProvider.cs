using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <inheritdoc />
    public partial class ForkMBoxOnlyToImportProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ============================================================
            // Fork only: carry IsMBoxOnly accounts over to Provider = IMPORT
            // ============================================================
            // Before syncing with upstream, this fork marked import-only accounts
            // with its own IsMBoxOnly column (20250815171800_AddIsMBoxOnlyColumn).
            // Upstream later added the same concept as ProviderType.IMPORT, which
            // is what the code now checks everywhere (no IMAP sync, import only).
            // Without this, those accounts would come out of MigrateV2509_2 as
            // 'IMAP' and the sync service would start trying to log in to them.
            //
            // The IsMBoxOnly column is left in place, unmapped, so the image from
            // before the sync (tag fork-pre-upstream-sync-2026-09-23) can still
            // read the table if it ever has to be rolled back to.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.columns
                               WHERE table_schema = 'mail_archiver'
                               AND table_name = 'MailAccounts'
                               AND column_name = 'IsMBoxOnly') THEN
                        UPDATE mail_archiver.""MailAccounts""
                        SET ""Provider"" = 'IMPORT'
                        WHERE ""IsMBoxOnly"" = TRUE;
                    END IF;
                END $$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: IsMBoxOnly was never changed, and an account that
            // is now IMPORT should stay import-only.
        }
    }
}
