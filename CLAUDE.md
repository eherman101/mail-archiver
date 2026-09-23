# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Fork notes (eherman101/mail-archiver)

- Fork of `s1t5/mail-archiver` (remote `upstream`). Sync with `git fetch upstream && git merge upstream/main`.
- Production runs on the broadway NAS as service `mailarchive-app` (image `mail-archiver-local:latest`,
  built from this repo); see `~/broadway-nas-docker.md`. It archives a Gmail account from Google Takeout mbox files.
- Our only fork-specific code is migration history:
  - `20250815171800_AddIsMBoxOnlyColumn`: our old import-only flag. It is applied on the live DB, so keep the file.
  - `20260923120000_ForkMBoxOnlyToImportProvider`: maps `IsMBoxOnly = true` to upstream's `Provider = 'IMPORT'`.
  Everything else comes from upstream: take upstream's side in conflicts.
- Rollback point from before the 2026-09 upstream sync: git tag `fork-pre-upstream-sync-2026-09-23`, and the NAS
  image `mail-archiver-local:fork-pre-upstream-sync-2026-09-23`.
- Upstream now targets .NET 10, and this Pi has no dotnet SDK, so build and test through Docker.

## Development Commands

### Building and Running
- `dotnet build` - Build the application
- `dotnet run` - Run the application locally (defaults to port 5000)
- `docker-compose up -d` - Run with Docker and PostgreSQL
- `docker-compose build` - Rebuild the Docker image

### Database Operations
- `dotnet ef migrations add <MigrationName>` - Create new migration
- `dotnet ef database update` - Apply migrations to database
- The application automatically runs migrations on startup via Program.cs:132

### Testing and Validation
- The application has no specific test framework configured
- Manual testing via the web interface at http://localhost:5000
- Check logs for errors during email synchronization

## Architecture Overview

### Core Components
- **ASP.NET Core 8 MVC** application with PostgreSQL database
- **MailKit/MimeKit** libraries for IMAP email communication
- **Entity Framework Core 9** with Npgsql provider for PostgreSQL
- **Background services** for automated email synchronization and batch operations

### Key Services
- `MailSyncBackgroundService` - Automatically syncs emails from IMAP accounts every 5-60 minutes
- `EmailService` - Handles IMAP communication and email archiving
- `BatchRestoreService` - Manages bulk email restoration operations
- `MBoxImportService` - Handles mbox file imports
- `UserService` - User authentication and management

### Database Schema
- Uses PostgreSQL with `mail_archiver` schema
- Key entities: `ArchivedEmails`, `MailAccounts`, `Users`, `UserMailAccounts`, `EmailAttachments`
- Email content stored as text fields, attachments as bytea
- Automatic database initialization and migration on startup

### Authentication & Authorization
- Custom cookie-based authentication via `AuthenticationMiddleware`
- Multi-user support with admin/regular user roles
- User-specific mail account access control via `UserMailAccounts` junction table
- Access control attributes: `AdminRequiredAttribute`, `UserAccessRequiredAttribute`, `EmailAccessRequiredAttribute`

### Configuration
- Main config in `appsettings.json` with sections for:
  - `Authentication` - User auth settings
  - `MailSync` - Sync intervals and timeouts
  - `BatchRestore` - Batch operation limits
  - `Npgsql` - Database timeout settings
- Docker environment variable override support

### Background Processing
- Long-running background services for email sync, batch restore, and mbox import
- Cancellation token support for timeout handling
- Job tracking via `ISyncJobService` for monitoring sync progress
- Configurable timeouts per operation type

### Key Patterns
- Repository pattern via Entity Framework DbContext
- Dependency injection for all services
- Configuration options pattern for settings
- Background service pattern for automated tasks
- Custom middleware for authentication