# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

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