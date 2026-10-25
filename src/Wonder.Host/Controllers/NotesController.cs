using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

public sealed record NotesSettingsInput(bool AllowNotes);
public sealed record UserNoteInput(Guid BotId, string Content, long? ExpectedVersion = null, string? Reason = null);

[Route("/api/v1/me/notes")]
public sealed class NotesController(WonderDbContext db, IConfiguration configuration) : WonderControllerBase(db)
{
    [HttpGet("settings")]
    public async Task<IActionResult> Settings(CancellationToken ct)
    {
        var id = await FindLogicalUserIdAsync(ct);
        if (id is null) return Conflict();
        return Ok(await Db.LogicalUsers.Where(x => x.Id == id).Select(x => new { x.AllowNotes }).SingleAsync(ct));
    }

    [HttpPut("settings")]
    public async Task<IActionResult> Settings(NotesSettingsInput input, CancellationToken ct)
    {
        var id = await FindLogicalUserIdAsync(ct);
        if (id is null) return Conflict();
        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        var user = await Db.LogicalUsers.SingleAsync(x => x.Id == id, ct);
        if (!await UserFence.CheckAsync(Db, user.Id, user.RevocationEpoch, ct)) return Conflict();
        user.AllowNotes = input.AllowNotes;
        user.NotesEpoch++;
        if (!input.AllowNotes)
            await NoteSources.InvalidateAsync(Db, await Db.Notes.Where(x => x.UserId == id).Select(x => x.Id).ToArrayAsync(ct), false, ct);
        await Db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return NoContent();
    }

    [HttpGet]
    public async Task<IActionResult> UserNotes(CancellationToken ct)
    {
        var id = await FindLogicalUserIdAsync(ct);
        if (id is null) return Conflict();
        // Agent Notes are deliberately never included, even for a Bot Owner.
        return Ok(await Db.Notes.AsNoTracking().Where(x => x.UserId == id && x.AuthorKind == NoteAuthorKind.User &&
                x.DeletedAt == null && !x.PrivacyDeleted)
            .OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id).Take(100)
            .Select(x => new { x.Id, x.BotId, x.Content, x.Version, x.CreatedAt, x.UpdatedAt }).ToArrayAsync(ct));
    }

    [HttpGet("bots")]
    public async Task<IActionResult> Bots(CancellationToken ct)
    {
        var user = await FindLogicalUserIdAsync(ct);
        if (user is null) return Conflict();
        return Ok(await (from c in Db.Conversations
            join p in Db.PlatformIdentities on c.ExternalId equals p.ExternalId
            join b in Db.Bots on c.BotId equals b.Id
            where c.Kind == ConversationKind.Private && p.Platform == "qq" && p.UserId == user
            orderby b.Name, b.Id
            select new { b.Id, b.Name }).Distinct().ToArrayAsync(ct));
    }

    [HttpGet("{noteId:guid}/history")]
    public async Task<IActionResult> History(Guid noteId, CancellationToken ct)
    {
        var user = await FindLogicalUserIdAsync(ct);
        if (!await Db.Notes.AnyAsync(x => x.Id == noteId && x.UserId == user && x.AuthorKind == NoteAuthorKind.User && !x.PrivacyDeleted, ct))
            return NotFound();
        return Ok(await Db.NoteRevisions.AsNoTracking().Where(x => x.NoteId == noteId).OrderBy(x => x.Version).ToArrayAsync(ct));
    }

    [HttpDelete("{noteId:guid}")]
    public async Task<IActionResult> Delete(Guid noteId, long expectedVersion, CancellationToken ct)
    {
        var id = await FindLogicalUserIdAsync(ct);
        if (id is null) return Conflict();
        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        var user = await Db.LogicalUsers.SingleAsync(x => x.Id == id, ct);
        if (!await UserFence.CheckAsync(Db, user.Id, user.RevocationEpoch, ct)) return Conflict();
        var note = await Db.Notes.SingleOrDefaultAsync(x => x.Id == noteId && x.UserId == id && x.AuthorKind == NoteAuthorKind.User &&
            !x.PrivacyDeleted && x.DeletedAt == null, ct);
        if (note is null) return NotFound();
        if (note.Version != expectedVersion) return Conflict();
        await NoteSources.InvalidateAsync(Db, [note.Id], false, ct);
        note.Version++; note.DeletedAt = note.UpdatedAt = DateTimeOffset.UtcNow;
        Db.NoteRevisions.Add(new NoteRevision { NoteId = note.Id, Version = note.Version, Content = note.Content,
            CreatedAt = note.UpdatedAt, Reason = "User soft deletion" });
        await Db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return NoContent();
    }

    [HttpPost]
    public async Task<IActionResult> Create(UserNoteInput input, CancellationToken ct)
    {
        var id = await FindLogicalUserIdAsync(ct);
        if (id is null) return Conflict();
        if (string.IsNullOrWhiteSpace(input.Content) || input.Content.Length > 4000) return BadRequest();
        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        var user = await Db.LogicalUsers.SingleAsync(x => x.Id == id, ct);
        if (!await UserFence.CheckAsync(Db, user.Id, user.RevocationEpoch, ct) || !user.AllowNotes) return Conflict();
        var conversation = await (from c in Db.Conversations
            join p in Db.PlatformIdentities on c.ExternalId equals p.ExternalId
            where c.BotId == input.BotId && c.Kind == ConversationKind.Private && p.UserId == id && p.Platform == "qq"
            select c.Id).SingleOrDefaultAsync(ct);
        if (conversation == Guid.Empty) return NotFound();
        var note = new Note { BotId = input.BotId, UserId = user.Id, ConversationId = conversation,
            AuthorKind = NoteAuthorKind.User, Content = input.Content };
        Db.Notes.Add(note);
        Db.NoteRevisions.Add(new NoteRevision { NoteId = note.Id, Version = 1, Content = note.Content, CreatedAt = note.CreatedAt });
        await Db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Ok(new { note.Id, note.Version });
    }

    [HttpPut("{noteId:guid}")]
    public async Task<IActionResult> Update(Guid noteId, UserNoteInput input, CancellationToken ct)
    {
        var userId = await FindLogicalUserIdAsync(ct);
        if (userId is null) return Conflict();
        if (string.IsNullOrWhiteSpace(input.Content) || input.Content.Length > 4000 || input.Reason?.Length > 1000)
            return BadRequest();
        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        var user = await Db.LogicalUsers.SingleAsync(x => x.Id == userId, ct);
        if (!await UserFence.CheckAsync(Db, user.Id, user.RevocationEpoch, ct) || !user.AllowNotes) return Conflict();
        var note = await Db.Notes.SingleOrDefaultAsync(x => x.Id == noteId && x.UserId == userId && x.AuthorKind == NoteAuthorKind.User &&
            x.DeletedAt == null && !x.PrivacyDeleted, ct);
        if (note is null) return NotFound();
        if (note.Version != input.ExpectedVersion || note.BotId != input.BotId) return Conflict();
        await NoteSources.InvalidateAsync(Db, [note.Id], false, ct);
        note.Content = input.Content; note.Version++; note.UpdatedAt = DateTimeOffset.UtcNow;
        Db.NoteRevisions.Add(new NoteRevision { NoteId = note.Id, Version = note.Version, Content = note.Content,
            Reason = input.Reason ?? "User correction", CreatedAt = note.UpdatedAt });
        await Db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Ok(new { note.Id, note.Version });
    }

    [HttpDelete("personal-data")]
    public async Task<IActionResult> DeletePersonalData(CancellationToken ct)
    {
        var id = await FindLogicalUserIdAsync(ct);
        if (id is null) return Conflict();
        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        var user = await Db.LogicalUsers.SingleAsync(x => x.Id == id, ct);
        if (!await UserFence.CheckAsync(Db, user.Id, user.RevocationEpoch, ct)) return Conflict();
        user.NotesEpoch++;
        var notes = await Db.Notes.AsNoTracking().Where(x => x.UserId == id && !x.PrivacyDeleted).ToArrayAsync(ct);
        foreach (var note in notes)
        {
            Db.Revocations.Add(new MemoryRevocation { ResourceKind = DeletionResourceKind.Note, ResourceId = note.Id,
                ResourceVersion = note.Version, BotId = note.BotId, UserId = user.Id, Epoch = user.NotesEpoch });
            await NoteSources.PurgeAsync(Db, note.Id, ct);
        }
        if (!string.IsNullOrWhiteSpace(configuration["Backups:AgeRecipient"]))
            foreach (var bot in notes.Select(x => x.BotId).Distinct()) Jobs.Add(Db, bot, JobKind.Ledger, new IdPayload(user.Id));
        await Db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return NoContent();
    }
}
