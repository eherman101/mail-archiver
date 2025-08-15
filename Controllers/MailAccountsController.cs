using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Models.ViewModels;
using MailArchiver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using MailArchiver.Attributes;

namespace MailArchiver.Controllers
{
    [AdminRequired]
    public class MailAccountsController : Controller
    {
        private readonly MailArchiverDbContext _context;
        private readonly IEmailService _emailService;
        private readonly ILogger<MailAccountsController> _logger;
        private readonly BatchRestoreOptions _batchOptions;
        private readonly ISyncJobService _syncJobService;
        private readonly IMBoxImportService _mboxImportService;

        public MailAccountsController(
            MailArchiverDbContext context,
            IEmailService emailService,
            ILogger<MailAccountsController> logger,
            IOptions<BatchRestoreOptions> batchOptions,
            ISyncJobService syncJobService,
            IMBoxImportService mboxImportService)
        {
            _context = context;
            _emailService = emailService;
            _logger = logger;
            _batchOptions = batchOptions.Value;
            _syncJobService = syncJobService;
            _mboxImportService = mboxImportService;
        }

        // GET: MailAccounts
        public async Task<IActionResult> Index()
        {
            var accounts = await _context.MailAccounts
                .Select(a => new MailAccountViewModel
                {
                    Id = a.Id,
                    Name = a.Name,
                    EmailAddress = a.EmailAddress,
                    ImapServer = a.ImapServer,
                    ImapPort = a.ImapPort,
                    Username = a.Username,
                    UseSSL = a.UseSSL,
                    IsEnabled = a.IsEnabled,
                    IsMBoxOnly = a.IsMBoxOnly,
                    LastSync = a.LastSync
                })
                .ToListAsync();

            return View(accounts);
        }

        // GET: MailAccounts/Details/5
        public async Task<IActionResult> Details(int id)
        {
            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            // E-Mail-Anzahl abrufen
            var emailCount = await _emailService.GetEmailCountByAccountAsync(id);

            var model = new MailAccountViewModel
            {
                Id = account.Id,
                Name = account.Name,
                EmailAddress = account.EmailAddress,
                ImapServer = account.ImapServer,
                ImapPort = account.ImapPort,
                Username = account.Username,
                UseSSL = account.UseSSL,
                LastSync = account.LastSync,
                IsEnabled = account.IsEnabled,
                IsMBoxOnly = account.IsMBoxOnly
            };

            ViewBag.EmailCount = emailCount;
            return View(model);
        }

        // GET: MailAccounts/Create
        public IActionResult Create()
        {
            var model = new CreateMailAccountViewModel
            {
                ImapPort = 993, // Standard values
                UseSSL = true
            };
            return View(model);
        }

        // POST: MailAccounts/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(CreateMailAccountViewModel model)
        {
            // For MBox only accounts, validate IMAP fields conditionally
            if (!model.IsMBoxOnly)
            {
                if (string.IsNullOrWhiteSpace(model.ImapServer))
                    ModelState.AddModelError("ImapServer", "IMAP server is required for non-MBox accounts");
                if (string.IsNullOrWhiteSpace(model.Username))
                    ModelState.AddModelError("Username", "Username is required for non-MBox accounts");
                if (string.IsNullOrWhiteSpace(model.Password))
                    ModelState.AddModelError("Password", "Password is required for non-MBox accounts");
                if (model.ImapPort < 1 || model.ImapPort > 65535)
                    ModelState.AddModelError("ImapPort", "Port must be between 1 and 65535");
            }
            else
            {
                // For MBox only accounts, set default values and disable by default
                model.ImapServer = model.ImapServer ?? "localhost";
                model.Username = model.Username ?? "mbox-only";
                model.Password = model.Password ?? "mbox-only";
                model.IsEnabled = false; // Default to disabled for MBox only accounts
            }

            if (ModelState.IsValid)
            {
                var account = new MailAccount
                {
                    Name = model.Name,
                    EmailAddress = model.EmailAddress,
                    ImapServer = model.ImapServer,
                    ImapPort = model.ImapPort,
                    Username = model.Username,
                    Password = model.Password,
                    UseSSL = model.UseSSL,
                    IsEnabled = model.IsEnabled,
                    IsMBoxOnly = model.IsMBoxOnly,
                    ExcludedFolders = string.Empty,
                    LastSync = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                };

                try
                {
                    // Only test connection for non-MBox accounts
                    if (!model.IsMBoxOnly)
                    {
                        _logger.LogInformation("Testing connection for new account: {Name}, Server: {Server}:{Port}",
                            model.Name, model.ImapServer, model.ImapPort);

                        // Test connection before saving
                        var connectionResult = await _emailService.TestConnectionAsync(account);
                        if (!connectionResult)
                        {
                            _logger.LogWarning("Connection test failed for account {Name}", model.Name);
                            ModelState.AddModelError("", "Connection to email server could not be established. Please check your settings and ensure the server is reachable.");
                            return View(model);
                        }

                        _logger.LogInformation("Connection test successful, saving account");
                    }
                    else
                    {
                        _logger.LogInformation("Creating MBox-only account: {Name} (IMAP connection test skipped)", model.Name);
                    }

                    _context.MailAccounts.Add(account);
                    await _context.SaveChangesAsync();
                    TempData["SuccessMessage"] = model.IsMBoxOnly 
                        ? "MBox-only email account created successfully. You can now upload MBox files to this account."
                        : "Email account created successfully.";
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating email account: {Message}", ex.Message);
                    ModelState.AddModelError("", $"An error occurred: {ex.Message}");
                    return View(model);
                }
            }

            // Wenn ModelState ungültig ist, zurück zur Ansicht mit Fehlern
            return View(model);
        }

        // GET: MailAccounts/Edit/5
        public async Task<IActionResult> Edit(int id)
        {
            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            var model = new MailAccountViewModel
            {
                Id = account.Id,
                Name = account.Name,
                EmailAddress = account.EmailAddress,
                ImapServer = account.ImapServer,
                ImapPort = account.ImapPort,
                Username = account.Username,
                UseSSL = account.UseSSL,
                IsEnabled = account.IsEnabled,
                IsMBoxOnly = account.IsMBoxOnly,
                LastSync = account.LastSync,
                ExcludedFolders = account.ExcludedFolders
            };

            // Load available folders for exclusion selection
            try
            {
                var folders = await _emailService.GetMailFoldersAsync(id);
                ViewBag.AvailableFolders = folders;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load folders for account {AccountId}", id);
                ViewBag.AvailableFolders = new List<string>();
            }

            return View(model);
        }

        // MailAccountsController.cs
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleEnabled(int id)
        {
            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            // Toggle the enabled status
            account.IsEnabled = !account.IsEnabled;
            await _context.SaveChangesAsync();

            // Correct message based on the NEW status (after toggling)
            TempData["SuccessMessage"] = account.IsEnabled
                ? $"Account '{account.Name}' has been enabled for synchronization."
                : $"Account '{account.Name}' has been disabled for synchronization.";

            return RedirectToAction(nameof(Index));
        }

        // POST: MailAccounts/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, MailAccountViewModel model)
        {
            if (id != model.Id)
            {
                return NotFound();
            }

            // Remove password validation if left blank
            if (string.IsNullOrEmpty(model.Password))
            {
                ModelState.Remove("Password");
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var account = await _context.MailAccounts.FindAsync(id);
                    if (account == null)
                    {
                        return NotFound();
                    }

                    account.Name = model.Name;
                    account.EmailAddress = model.EmailAddress;
                    account.ImapServer = model.ImapServer;
                    account.ImapPort = model.ImapPort;
                    account.Username = model.Username;
                    account.IsEnabled = model.IsEnabled;
                    account.IsMBoxOnly = model.IsMBoxOnly;

                    // Only update password if provided
                    if (!string.IsNullOrEmpty(model.Password))
                    {
                        account.Password = model.Password;
                    }

                    account.UseSSL = model.UseSSL;
                    account.ExcludedFolders = model.ExcludedFolders ?? string.Empty;

                    // Test connection before saving (skip for MBox only accounts)
                    if (!string.IsNullOrEmpty(model.Password) && !model.IsMBoxOnly)
                    {
                        var connectionResult = await _emailService.TestConnectionAsync(account);
                        if (!connectionResult)
                        {
                            ModelState.AddModelError("", "Connection to email server could not be established. Please check your settings.");
                            return View(model);
                        }
                    }

                    await _context.SaveChangesAsync();
                    TempData["SuccessMessage"] = "Email account updated successfully.";
                    return RedirectToAction(nameof(Index));
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!await _context.MailAccounts.AnyAsync(e => e.Id == id))
                    {
                        return NotFound();
                    }
                    else
                    {
                        throw;
                    }
                }
            }
            return View(model);
        }

        // GET: MailAccounts/Delete/5
        public async Task<IActionResult> Delete(int id)
        {
            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            // E-Mail-Anzahl abrufen (das war der fehlende Teil!)
            var emailCount = await _emailService.GetEmailCountByAccountAsync(id);

            var model = new MailAccountViewModel
            {
                Id = account.Id,
                Name = account.Name,
                EmailAddress = account.EmailAddress
            };

            // ViewBag für die E-Mail-Anzahl setzen
            ViewBag.EmailCount = emailCount;

            return View(model);
        }

        // POST: MailAccounts/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            // Determine number of emails to delete
            var emailCount = await _context.ArchivedEmails.CountAsync(e => e.MailAccountId == id);

            var account = await _context.MailAccounts.FindAsync(id);
            if (account != null)
            {
                // First delete attachments
                var emailIds = await _context.ArchivedEmails
                    .Where(e => e.MailAccountId == id)
                    .Select(e => e.Id)
                    .ToListAsync();

                var attachments = await _context.EmailAttachments
                    .Where(a => emailIds.Contains(a.ArchivedEmailId))
                    .ToListAsync();

                _context.EmailAttachments.RemoveRange(attachments);

                // Then delete emails
                var emails = await _context.ArchivedEmails
                    .Where(e => e.MailAccountId == id)
                    .ToListAsync();

                _context.ArchivedEmails.RemoveRange(emails);

                // Finally delete the account
                _context.MailAccounts.Remove(account);

                await _context.SaveChangesAsync();

                TempData["SuccessMessage"] = $"Email account and {emailCount} related emails have been successfully deleted.";
            }

            return RedirectToAction(nameof(Index));
        }

        // POST: MailAccounts/Sync/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Sync(int id)
        {
            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            try
            {
                // Sync-Job starten
                var jobId = _syncJobService.StartSync(account.Id, account.Name, account.LastSync);

                // Sync ausführen
                await _emailService.SyncMailAccountAsync(account, jobId);

                TempData["SuccessMessage"] = $"Synchronization for {account.Name} was successfully completed.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing account {AccountName}: {Message}", account.Name, ex.Message);
                TempData["ErrorMessage"] = $"Error during synchronization: {ex.Message}";
            }

            return RedirectToAction(nameof(Index));
        }

        // POST: MailAccounts/Resync/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Resync(int id)
        {
            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null)
            {
                return NotFound();
            }

            try
            {
                var success = await _emailService.ResyncAccountAsync(id);
                if (success)
                {
                    TempData["SuccessMessage"] = $"Full resync for {account.Name} was successfully started.";
                }
                else
                {
                    TempData["ErrorMessage"] = $"Failed to start resync for {account.Name}.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting resync for account {AccountName}: {Message}", account.Name, ex.Message);
                TempData["ErrorMessage"] = $"Error during resync: {ex.Message}";
            }

            return RedirectToAction(nameof(Details), new { id });
        }

        // POST: MailAccounts/MoveAllEmails/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MoveAllEmails(int id)
        {
            var account = await _context.MailAccounts.FindAsync(id);
            if (account == null) return NotFound();

            var emailIds = await _context.ArchivedEmails
                .Where(e => e.MailAccountId == id)
                .Select(e => e.Id)
                .ToListAsync();

            if (!emailIds.Any())
            {
                TempData["ErrorMessage"] = "No emails found to copy for this account.";
                return RedirectToAction(nameof(Details), new { id });
            }

            _logger.LogInformation("Account {AccountId} has {Count} emails. Thresholds: Async={AsyncThreshold}, MaxAsync={MaxAsync}",
                id, emailIds.Count, _batchOptions.AsyncThreshold, _batchOptions.MaxAsyncEmails);

            // Prüfe absolute Limits
            if (emailIds.Count > _batchOptions.MaxAsyncEmails)
            {
                TempData["ErrorMessage"] = $"Too many emails in this account ({emailIds.Count:N0}). Maximum allowed is {_batchOptions.MaxAsyncEmails:N0} emails per operation. Please use manual selection with smaller batches.";
                return RedirectToAction(nameof(Details), new { id });
            }

            // Entscheide basierend auf Schwellenwert
            if (emailIds.Count > _batchOptions.AsyncThreshold)
            {
                // Für große Mengen: Direkt zum asynchronen Batch-Restore
                _logger.LogInformation("Using background job for {Count} emails from account {AccountId}", emailIds.Count, id);
                return RedirectToAction("StartAsyncBatchRestoreFromAccount", "Emails", new
                {
                    accountId = id,
                    returnUrl = Url.Action("Details", new { id })
                });
            }
            else
            {
                // Für kleinere Mengen: Session-basierte Verarbeitung
                _logger.LogInformation("Using direct processing for {Count} emails from account {AccountId}", emailIds.Count, id);
                try
                {
                    HttpContext.Session.SetString("BatchRestoreIds", string.Join(",", emailIds));
                    HttpContext.Session.SetString("BatchRestoreReturnUrl", Url.Action("Details", new { id }));
                    return RedirectToAction("BatchRestore", "Emails");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to store {Count} email IDs in session for account {AccountId}", emailIds.Count, id);
                    // Fallback zu Background Job
                    _logger.LogWarning("Session storage failed, redirecting to background job");
                    return RedirectToAction("StartAsyncBatchRestoreFromAccount", "Emails", new
                    {
                        accountId = id,
                        returnUrl = Url.Action("Details", new { id })
                    });
                }
            }
        }

        // GET: MailAccounts/ImportMBox
        public async Task<IActionResult> ImportMBox()
        {
            var accounts = await _context.MailAccounts
                .Where(a => a.IsEnabled)
                .OrderBy(a => a.Name)
                .ToListAsync();

            var model = new MBoxImportViewModel
            {
                AvailableAccounts = accounts.Select(a => new SelectListItem
                {
                    Value = a.Id.ToString(),
                    Text = $"{a.Name} ({a.EmailAddress})"
                }).ToList(),
                MaxFileSize = 5_000_000_000 // 5 GB
            };

            return View(model);
        }

        // POST: MailAccounts/ImportMBox
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(5_368_709_120)] // 5 GB
        [RequestFormLimits(MultipartBodyLengthLimit = 5_368_709_120)] // 5 GB
        public async Task<IActionResult> ImportMBox(MBoxImportViewModel model)
        {
            // Reload accounts for validation failure
            var accounts = await _context.MailAccounts
                .Where(a => a.IsEnabled)
                .OrderBy(a => a.Name)
                .ToListAsync();

            model.AvailableAccounts = accounts.Select(a => new SelectListItem
            {
                Value = a.Id.ToString(),
                Text = $"{a.Name} ({a.EmailAddress})",
                Selected = a.Id == model.TargetAccountId
            }).ToList();

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            // Validate file
            if (model.MBoxFile == null || model.MBoxFile.Length == 0)
            {
                ModelState.AddModelError("MBoxFile", "Please select a valid MBox file.");
                return View(model);
            }

            if (model.MBoxFile.Length > model.MaxFileSize)
            {
                ModelState.AddModelError("MBoxFile", $"File size exceeds maximum allowed size of {model.MaxFileSizeFormatted}.");
                return View(model);
            }

            // Validate target account
            var targetAccount = await _context.MailAccounts.FindAsync(model.TargetAccountId);
            if (targetAccount == null)
            {
                ModelState.AddModelError("TargetAccountId", "Selected account not found.");
                return View(model);
            }

            try
            {
                // Save uploaded file
                var filePath = await _mboxImportService.SaveUploadedFileAsync(model.MBoxFile);

                // Create import job
                var job = new MBoxImportJob
                {
                    FileName = model.MBoxFile.FileName,
                    FilePath = filePath,
                    FileSize = model.MBoxFile.Length,
                    TargetAccountId = model.TargetAccountId,
                    TargetFolder = model.TargetFolder,
                    UserId = HttpContext.User.Identity?.Name ?? "Anonymous"
                };

                // Estimate email count
                job.TotalEmails = await _mboxImportService.EstimateEmailCountAsync(filePath);

                // Queue the job
                var jobId = _mboxImportService.QueueImport(job);

                TempData["SuccessMessage"] = $"MBox file '{model.MBoxFile.FileName}' uploaded successfully. Import job started with estimated {job.TotalEmails:N0} emails.";
                return RedirectToAction("MBoxImportStatus", new { jobId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting MBox import for file {FileName}", model.MBoxFile.FileName);
                ModelState.AddModelError("", $"Error starting import: {ex.Message}");
                return View(model);
            }
        }

        // GET: MailAccounts/MBoxImportStatus
        [HttpGet]
        public IActionResult MBoxImportStatus(string jobId)
        {
            var job = _mboxImportService.GetJob(jobId);
            if (job == null)
            {
                TempData["ErrorMessage"] = "MBox import job not found.";
                return RedirectToAction(nameof(Index));
            }

            return View(job);
        }

        // POST: MailAccounts/CancelMBoxImport
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CancelMBoxImport(string jobId, string returnUrl = null)
        {
            var success = _mboxImportService.CancelJob(jobId);
            if (success)
            {
                TempData["SuccessMessage"] = "MBox import job has been cancelled.";
            }
            else
            {
                TempData["ErrorMessage"] = "Could not cancel the MBox import job.";
            }

            return Redirect(returnUrl ?? Url.Action(nameof(Index)));
        }

        // AJAX endpoint for folder loading
        [HttpGet]
        public async Task<JsonResult> GetFolders(int accountId)
        {
            try
            {
                var folders = await _emailService.GetMailFoldersAsync(accountId);
                if (!folders.Any())
                {
                    return Json(new List<string> { "INBOX" });
                }
                return Json(folders);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading folders for account {AccountId}", accountId);
                return Json(new List<string> { "INBOX" });
            }
        }
    }
}
