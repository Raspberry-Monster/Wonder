using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;
using Wonder.Core;
using Wonder.Host.Features.Emotion;
using Wonder.Host.Features.Memory;
using Wonder.Infrastructure;
using Wonder.Infrastructure.Models;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessage = Wonder.Core.ChatMessage;

namespace Wonder.Host;

public sealed class ReflectionWorkflow(
    IDbContextFactory<WonderDbContext> factory,
    ModelRouter models,
    EmotionEngine emotion,
    PromptCatalog prompts)
{
    public async Task GenerateAsync(Guid jobId, CancellationToken ct, Guid? expectedLeaseId = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.Outbox.SingleAsync(x => x.Id == jobId, ct);
        Jobs.RequireLease(job, expectedLeaseId);
        if (job.Status is JobStatus.Completed or JobStatus.Cancelled) return;
        var input = Json.Read<ReflectionJob>(job.Payload);
        var generation = await db.ReflectionGenerations.SingleOrDefaultAsync(x => x.JobId == job.Id, ct);
        var user = await db.LogicalUsers.AsNoTracking().SingleAsync(x => x.Id == input.SubjectId, ct);
        if (!user.AllowMemory || user.RevocationEpoch != input.Epoch)
        {
            ReflectionRuns.Cancel(generation, ReflectionCancellationReason.AuthorizationChanged);
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            return;
        }

        var session = await db.Sessions.AsNoTracking().SingleAsync(x => x.Id == input.SessionId, ct);
        var conv = await db.Conversations.AsNoTracking().SingleAsync(x => x.Id == session.ConversationId, ct);
        if (conv.Kind == ConversationKind.Group)
        {
            Jobs.Complete(db, job);
            await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
            return;
        }

        if (input.Kind == ReflectionKind.Full)
        {
            if (generation is null)
                throw new InvalidOperationException("Full reflection requires a scheduled generation.");
            if (generation.Status == ReflectionGenerationStatus.Cancelled ||
                generation.SourceSignature != await ReflectionRuns.SignatureAsync(db, job.BotId, input, ct))
            {
                ReflectionRuns.Cancel(generation, ReflectionCancellationReason.SourceChanged);
                Jobs.Complete(db, job);
                await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
                return;
            }

            await using var admission = await db.Database.BeginTransactionAsync(ct);
            if (!await UserFence.CheckAsync(db, user.Id, input.Epoch, ct))
            {
                ReflectionRuns.Cancel(generation, ReflectionCancellationReason.AuthorizationChanged);
                Jobs.Complete(db, job);
            }
            else
            {
                if (generation.Status is ReflectionGenerationStatus.Completed or ReflectionGenerationStatus.Cancelled)
                    Jobs.Complete(db, job);
                else generation.Status = ReflectionGenerationStatus.Running;
            }

            await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
            await admission.CommitAsync(ct);
            if (job.Status is JobStatus.Completed or JobStatus.Cancelled) return;
            if (generation.SourceSignature != await ReflectionRuns.SignatureAsync(db, job.BotId, input, ct))
            {
                ReflectionRuns.Cancel(generation, ReflectionCancellationReason.NoEligibleSources);
                Jobs.Complete(db, job);
                await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
                return;
            }
        }

        var progress = job.ProgressJson is null
            ? new ReflectionProgress()
            : Json.Read<ReflectionProgress>(job.ProgressJson);
        var processedIds = progress.SourceHashes.Keys.ToArray();
        var processed = await db.Messages.AsNoTracking()
            .Where(x => processedIds.Contains(x.Id) && x.AllowReflection && !x.ContextInvalidated).ToListAsync(ct);
        if (processed.Count != processedIds.Length ||
            processed.Any(x => TextBatch.Fingerprint(x.Text) != progress.SourceHashes[x.Id]))
        {
            ReflectionRuns.Cancel(generation, ReflectionCancellationReason.SourceChanged);
            Jobs.Complete(db, job);
            await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
            return;
        }

        var source = await db.Messages.AsNoTracking().Where(x =>
                x.SessionId == session.Id && x.SenderId == user.Id && x.AllowReflection &&
                !x.ContextInvalidated && x.Sequence <= input.Boundary &&
                !db.EvidenceSuppressions.Any(s => s.BotId == job.BotId && s.MessageId == x.Id) &&
                (input.Kind != ReflectionKind.Light || x.Sequence == input.Boundary) &&
                (x.Sequence > progress.Sequence || (progress.Offset > 0 && x.Sequence == progress.Sequence)))
            .OrderBy(x => x.Sequence).Take(100).ToListAsync(ct);
        var safe = await SafeReflectionEvidence.ReadAsync(db, job.BotId, user.Id, session.Id, progress.Sequence,
            input.Boundary, progress.Offset, input.Kind == ReflectionKind.Light, ct);
        source = source.Concat(safe.Rows).OrderBy(x => x.Sequence).Take(100).ToList();
        if (source.Count == 0)
        {
            ReflectionRuns.Cancel(generation, ReflectionCancellationReason.NoEligibleSources);
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            return;
        }

        var profile = await models.ProfileAsync(job.BotId, TaskKind.LightReflection, ct);

        AiMessage[] Request(IReadOnlyList<TextFragment<ChatMessage>> fragments)
        {
            return
            [
                new AiMessage(ChatRole.System, prompts.Reflection), new AiMessage(ChatRole.User,
                    Json.ForPrompt(fragments.Select(f => new
                    {
                        id = f.Source.Id, speaker = SpeakerTags.For(conv.Id, f.Source.SenderId, f.Source.Role),
                        text = f.Text, f.Offset, f.Truncated, sentAt = f.Source.CreatedAt.ToString("O")
                    })))
            ];
        }

        var (batch, proposal) =
            await ReflectionBatches.GenerateAsync(models, profile, source, progress.Offset, Request, Parse, ct);
        if (proposal.NeedsStrongReview)
        {
            profile = await models.ProfileAsync(job.BotId, TaskKind.StrongReflection, ct);
            var reviews = new List<ReflectionProposal>();
            foreach (var fragment in batch)
            {
                var part = fragment.Source;
                var remaining = fragment.Text;
                var offset = fragment.Offset;
                while (remaining.Length > 0)
                {
                    var length = ModelContextBudget.PrefixLength(remaining,
                        value => models.Context(profile).Fits(Request([
                            new TextFragment<ChatMessage>(part, value, offset,
                                fragment.Truncated || value.Length < remaining.Length)
                        ])));
                    if (length == 0)
                        throw new ContextLimitException("StrongReflection has no room for the review evidence.");
                    ReflectionProposal review;
                    for (var attempt = 0;; attempt++)
                        try
                        {
                            review = Parse((await models.GenerateAsync(profile, Request([
                                new TextFragment<ChatMessage>(part, remaining[..length], offset,
                                    fragment.Truncated || length < remaining.Length)
                            ]), true, ct)).Text);
                            break;
                        }
                        catch (OutputLimitException) when (attempt < 2 && length > 32)
                        {
                            length /= 2;
                            if (char.IsHighSurrogate(remaining[length - 1])) length--;
                        }

                    if (!review.NeedsStrongReview) reviews.Add(review);
                    remaining = remaining[length..];
                    offset += length;
                }
            }

            proposal = ReflectionBatches.Merge(reviews);
        }

        var allowed = batch.Select(x => x.Source.Id).ToHashSet();
        proposal = proposal with
        {
            Emotion = proposal.Emotion is { } ep && ep.EvidenceMessageIds.Length > 0 &&
                      ep.EvidenceMessageIds.All(allowed.Contains)
                ? ep
                : null,
            Relationships = proposal.Relationships
                .Where(x => x.EvidenceMessageIds.Length > 0 && x.EvidenceMessageIds.All(allowed.Contains))
                .ToArray(),
            Memories = proposal.Memories
                .Where(x => x.EvidenceMessageIds.Length > 0 && x.EvidenceMessageIds.All(allowed.Contains))
                .ToArray()
        };
        if (proposal.Emotion is { } emotionProposal) progress.Emotions.Add(emotionProposal);
        progress.Proposal = ReflectionBatches.Merge([progress.Proposal, proposal]) with { Emotion = null };
        foreach (var fragment in batch)
            progress.SourceHashes[fragment.Source.Id] = safe.OriginalHashes.GetValueOrDefault(fragment.Source.Id) ??
                                                        TextBatch.Fingerprint(fragment.Source.Text);
        var last = batch[^1];
        progress.Sequence = last.Source.Sequence;
        progress.Offset = last.Offset + last.Text.Length < last.Source.Text.Length ? last.Offset + last.Text.Length : 0;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await UserFence.CheckAsync(db, user.Id, input.Epoch, ct))
        {
            ReflectionRuns.Cancel(generation, ReflectionCancellationReason.AuthorizationChanged);
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }

        var sourceIds = progress.SourceHashes.Keys.ToArray();
        var unchanged = await db.Messages.AsNoTracking()
            .Where(x => sourceIds.Contains(x.Id) && x.AllowReflection && !x.ContextInvalidated).ToListAsync(ct);
        if ((generation is not null &&
             generation.SourceSignature != await ReflectionRuns.SignatureAsync(db, job.BotId, input, ct)) ||
            unchanged.Count != sourceIds.Length ||
            unchanged.Any(x => TextBatch.Fingerprint(x.Text) != progress.SourceHashes[x.Id]))
        {
            ReflectionRuns.Cancel(generation, ReflectionCancellationReason.SourceChanged);
            Jobs.Complete(db, job);
            await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
            await tx.CommitAsync(ct);
            return;
        }

        // A Forget may have won while the model was running. Do not persist its stale memory subpayloads.
        var admitted = new List<MemoryCandidate>();
        foreach (var candidate in progress.Proposal.Memories)
            if (await EvidencePolicy.MayAdmitAsync(db, job.BotId, user.Id, candidate, ct))
                admitted.Add(candidate);
        progress.Proposal = progress.Proposal with { Memories = admitted.ToArray() };
        if (progress.Offset > 0 || await db.Messages.AnyAsync(x => x.SessionId == session.Id && x.SenderId == user.Id &&
                                                                   x.AllowReflection && !x.ContextInvalidated &&
                                                                   x.Sequence > progress.Sequence &&
                                                                   x.Sequence <= input.Boundary &&
                                                                   (input.Kind != ReflectionKind.Light ||
                                                                    x.Sequence == input.Boundary), ct))
        {
            Jobs.Continue(job, progress);
            await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
            await tx.CommitAsync(ct);
            return;
        }

        proposal = progress.Proposal with
        {
            Emotion = ReflectionBatches.Merge(progress.Emotions.Select(x =>
                new ReflectionProposal(false, x, [], []))).Emotion
        };

        var result = new ReflectionResult
        {
            BotId = job.BotId,
            SessionId = session.Id,
            SubjectId = user.Id,
            Kind = input.Kind,
            Boundary = input.Boundary,
            RevocationEpoch = input.Epoch,
            ModelId = profile.Id.ToString(),
            Payload = Json.Write(proposal)
        };
        db.Reflections.Add(result);
        if (generation is not null)
        {
            generation.Status = ReflectionGenerationStatus.Completed;
            generation.ResultId = result.Id;
        }

        foreach (var type in new[] { JobKind.Emotion, JobKind.Relationship, JobKind.Memory })
            Jobs.Add(db, job.BotId, type, new IdPayload(result.Id), conv.Id, job.Id, job.CorrelationId);
        Jobs.Complete(db, job);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static ReflectionProposal Parse(string text)
    {
        var cleaned = text.Trim();
        if (cleaned.StartsWith("\x60\x60\x60", StringComparison.Ordinal))
            cleaned = cleaned[(cleaned.IndexOf('\n') + 1)..].TrimEnd().TrimEnd('\x60').TrimEnd();
        if (string.IsNullOrWhiteSpace(cleaned))
            throw new ModelResponseException("Reflection model returned empty JSON after removing code fences.");
        var p = Json.Read<ReflectionProposal>(cleaned);
        if (p.Relationships is null || p.Memories is null ||
            p.Relationships.Any(x => x.Preference is null || x.EvidenceMessageIds is null) ||
            p.Memories.Any(x => x.FactKey is null || x.Text is null || x.EvidenceMessageIds is null) ||
            p.Emotion is { EvidenceMessageIds: null })
            throw new JsonException("Invalid proposal schema.");
        return p;
    }

    public async Task ApplyAsync(Guid jobId, CancellationToken ct, Guid? expectedLeaseId = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.Outbox.SingleAsync(x => x.Id == jobId, ct);
        Jobs.RequireLease(job, expectedLeaseId);
        var result = await db.Reflections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == Json.Read<IdPayload>(job.Payload).Id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (job.Status is JobStatus.Completed or JobStatus.Cancelled) return;
        if (result is null || result.Payload == "{}" ||
            !await db.Sessions.AnyAsync(s => s.Id == result.SessionId && db.Conversations.Any(c =>
                c.Id == s.ConversationId && c.Kind == ConversationKind.Private), ct) ||
            !await UserFence.CheckAsync(db, result.SubjectId, result.RevocationEpoch, ct))
        {
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }

        var user = await db.LogicalUsers.AsNoTracking().SingleAsync(x => x.Id == result.SubjectId, ct);
        if (!user.AllowMemory)
        {
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }

        var proposal = Json.Read<ReflectionProposal>(result.Payload);
        var evidence = await db.Messages.AsNoTracking().Where(x =>
                x.SessionId == result.SessionId && x.SenderId == result.SubjectId && x.AllowReflection &&
                x.Sequence <= result.Boundary && !x.ContextInvalidated)
            .Select(x => x.Id).ToListAsync(ct);
        var allowed = evidence.ToHashSet();
        if (job.Type == JobKind.Emotion && proposal.Emotion is { } ep && ep.EvidenceMessageIds.All(allowed.Contains))
        {
            // Each source message can influence the global mood once across light/full reflections.
            var key = string.Join(":", ep.EvidenceMessageIds.Order());
            var previous = await db.EmotionEvents.Where(x => x.BotId == job.BotId).OrderByDescending(x => x.Sequence)
                .Take(1000).Select(x => x.DeltaJson).ToListAsync(ct);
            var alreadyApplied = previous.Any(p =>
                Json.Read<EmotionProposal>(p).EvidenceMessageIds.Intersect(ep.EvidenceMessageIds).Any());
            var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
            var dailyCount = await (from evt in db.EmotionEvents
                join r in db.Reflections on evt.SourceReflectionId equals r.Id
                where evt.BotId == job.BotId && r.SubjectId == user.Id && evt.AppliedAt >= today
                select evt.Id).CountAsync(ct);
            if (!alreadyApplied && dailyCount < 5)
            {
                var mood = await db.Emotions.SingleAsync(x => x.BotId == job.BotId, ct);
                emotion.Evolve(mood, DateTimeOffset.UtcNow, ep);
                db.EmotionEvents.Add(new EmotionEvent
                {
                    BotId = job.BotId, Sequence = mood.Revision, SourceReflectionId = result.Id, SourceEventKey = key,
                    DeltaJson = Json.Write(ep with { Reason = "" })
                });
            }
        }
        else if (job.Type == JobKind.Relationship)
        {
            var state = await db.Relationships.SingleOrDefaultAsync(
                x => x.BotId == job.BotId && x.UserId == user.Id, ct);
            foreach (var candidate in proposal.Relationships.Where(x => x.EvidenceMessageIds.All(allowed.Contains)))
            {
                if (candidate.Kind is not ("Preference" or "Boundary") ||
                    !await EvidencePolicy.MayAdmitAsync(db, job.BotId, user.Id,
                        new MemoryCandidate("relationship:" + candidate.Preference, candidate.Preference,
                            candidate.Explicit, false,
                            candidate.EvidenceMessageIds, candidate.Assertions), ct)) continue;
                if (await db.Observations.AnyAsync(
                        x => x.ReflectionId == result.Id && x.Candidate == candidate.Preference, ct)) continue;
                var old = await db.Observations
                    .Where(x => x.BotId == job.BotId && x.UserId == user.Id &&
                                x.Candidate == candidate.Preference).Select(x => x.EvidenceJson).ToListAsync(ct);
                var independent = old.SelectMany(Json.Read<Guid[]>).Concat(candidate.EvidenceMessageIds).Distinct()
                    .Count();
                var accepted = RelationshipAdmission.CanAccept(candidate, independent);
                db.Observations.Add(new RelationshipObservation
                {
                    BotId = job.BotId,
                    UserId = user.Id,
                    ReflectionId = result.Id,
                    Candidate = candidate.Preference,
                    EvidenceJson = Json.Write(candidate.EvidenceMessageIds),
                    Accepted = accepted
                });
                if (!accepted) continue;
                if (state is null)
                {
                    state = new RelationshipState { BotId = job.BotId, UserId = user.Id };
                    db.Relationships.Add(state);
                }

                var claim = db.RelationshipClaims.Local.SingleOrDefault(x => x.RelationshipId == state.Id &&
                                                                             x.Kind == candidate.Kind &&
                                                                             x.Text == candidate.Preference) ??
                            await db.RelationshipClaims.SingleOrDefaultAsync(x => x.RelationshipId == state.Id &&
                                x.Kind == candidate.Kind && x.Text == candidate.Preference, ct);
                if (claim is null)
                {
                    claim = new RelationshipClaim
                    {
                        RelationshipId = state.Id, BotId = job.BotId, SubjectId = user.Id,
                        Kind = candidate.Kind, Text = candidate.Preference,
                        Origin = candidate.Explicit ? "Explicit" : "Inferred"
                    };
                    db.RelationshipClaims.Add(claim);
                }

                var roots = Json.Read<Guid[]>(claim.EvidenceJson).Concat(candidate.EvidenceMessageIds).Distinct()
                    .ToArray();
                claim.EvidenceJson = Json.Write(roots);
                if (candidate.Explicit) claim.Origin = "Explicit";
                claim.Qualification = candidate.Explicit ? "Current" : "Tentative";
                claim.Revision++;
                state.Revision++;
            }
        }
        else if (job.Type == JobKind.Memory)
        {
            foreach (var candidate in result.MemorySourcesBlocked ? [] : proposal.Memories)
            {
                if (!MemoryRules.CanAdmit(candidate, user.AllowMemory, result.RevocationEpoch, user.RevocationEpoch,
                        allowed)) continue;
                if (!await EvidencePolicy.MayAdmitAsync(db, job.BotId, user.Id, candidate, ct)) continue;
                var hash = Json.Hash(Json.Write(candidate));
                if (db.MemoryCandidates.Local.Any(x => x.SourceReflectionId == result.Id && x.CandidateHash == hash) ||
                    await db.MemoryCandidates.AnyAsync(
                        x => x.SourceReflectionId == result.Id && x.CandidateHash == hash, ct)) continue;
                var pending = new MemoryMaintenanceCandidate
                {
                    BotId = job.BotId,
                    SubjectId = user.Id,
                    FactKey = candidate.FactKey,
                    Text = candidate.Text,
                    SourceReflectionId = result.Id,
                    EvidenceJson = Json.Write(candidate.EvidenceMessageIds.Distinct().ToArray()),
                    AssertionsJson = candidate.Assertions is null ? null : Json.Write(candidate.Assertions),
                    RevocationEpoch = result.RevocationEpoch,
                    CandidateHash = hash
                };
                db.MemoryCandidates.Add(pending);
                Jobs.Add(db, job.BotId, JobKind.MemoryMaintenance, new IdPayload(pending.Id), cause: job.Id);
            }
        }

        Jobs.Complete(db, job);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task EmbedAsync(Guid jobId, CancellationToken ct, Guid? expectedLeaseId = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.Outbox.SingleAsync(x => x.Id == jobId, ct);
        Jobs.RequireLease(job, expectedLeaseId);
        var memory =
            await db.Memories.SingleOrDefaultAsync(
                x => x.Id == Json.Read<IdPayload>(job.Payload).Id && x.Status != MemoryStatus.Revoked &&
                     x.ArchivedAt == null, ct);
        if (job.Status is JobStatus.Completed or JobStatus.Cancelled) return;
        if (memory is null)
        {
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            return;
        }

        var user = await db.LogicalUsers.AsNoTracking().SingleAsync(x => x.Id == memory.SubjectId, ct);
        if (!user.AllowMemory)
        {
            Jobs.Complete(db, job);
            await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
            return;
        }

        if (memory.BotId != job.BotId) throw new InvalidOperationException("Embedding job bot mismatch.");
        var sourceRevision = memory.Revision;
        var sourceText = memory.Text;
        var profile = await models.ProfileAsync(job.BotId, TaskKind.Embedding, ct);
        var values = await models.EmbedAsync(profile, memory.Text, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await UserFence.CheckAsync(db, user.Id, user.RevocationEpoch, ct))
        {
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }

        await db.Entry(memory).ReloadAsync(ct);
        var currentProfile = await db.Models.AsNoTracking().SingleOrDefaultAsync(x => x.BotId == job.BotId &&
            x.TaskKind == TaskKind.Embedding, ct);
        // Fence the original lease (including expiry) after the provider call, before any vector write.
        await db.Entry(job).ReloadAsync(ct);
        Jobs.RequireLease(job, expectedLeaseId);
        if (expectedLeaseId is not null && (job.LeaseUntil is null ||
                                            job.LeaseUntil <=
                                            await InteractionBoundary.DatabaseNowAsync(db, DateTimeOffset.UtcNow, ct)))
            throw new DbUpdateConcurrencyException("Embedding lease expired.");
        if (memory.Revision != sourceRevision || memory.Text != sourceText || memory.IsOutdated ||
            currentProfile is null || currentProfile.Id != profile.Id || currentProfile.Endpoint != profile.Endpoint ||
            currentProfile.Model != profile.Model ||
            currentProfile.EmbeddingDimensions != profile.EmbeddingDimensions ||
            memory.Status == MemoryStatus.Revoked || memory.ArchivedAt != null)
        {
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }

        var space = await db.EmbeddingProfiles.SingleOrDefaultAsync(
            x => x.BotId == job.BotId && x.Provider == profile.Endpoint && x.Model == profile.Model &&
                 x.Dimensions == values.Length && x.Version == 1, ct);
        if (space is null)
        {
            space = new EmbeddingProfile
                { BotId = job.BotId, Provider = profile.Endpoint, Model = profile.Model, Dimensions = values.Length };
            db.EmbeddingProfiles.Add(space);
        }

        var existing =
            await db.Embeddings.SingleOrDefaultAsync(x => x.MemoryId == memory.Id && x.ProfileId == space.Id, ct);
        if (existing is null)
        {
            db.Embeddings.Add(new MemoryEmbedding
            {
                MemoryId = memory.Id, ProfileId = space.Id, SourceRevision = sourceRevision, Values = new Vector(values)
            });
        }
        else
        {
            existing.Values = new Vector(values);
            existing.SourceRevision = sourceRevision;
        }

        memory.Status = MemoryStatus.Active;
        Jobs.Complete(db, job);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
