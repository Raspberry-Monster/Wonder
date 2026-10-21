using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Wonder.Core;

namespace Wonder.Infrastructure;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public Guid? LogicalUserId { get; set; }
}

public sealed class MemoryEmbedding
{
    public Guid MemoryId { get; set; }
    public Guid ProfileId { get; set; }
    public float[] Values { get; set; } = [];
}

public class WonderDbContext(DbContextOptions<WonderDbContext> options) : DbContext(options)
{
    public DbSet<ApplicationUser> Users => Set<ApplicationUser>();
    public DbSet<BotInstance> Bots => Set<BotInstance>();
    public DbSet<LogicalUser> LogicalUsers => Set<LogicalUser>();
    public DbSet<PlatformIdentity> PlatformIdentities => Set<PlatformIdentity>();
    public DbSet<PlatformBinding> Bindings => Set<PlatformBinding>();
    public DbSet<BindingChallenge> BindingChallenges => Set<BindingChallenge>();
    public DbSet<GroupPolicy> GroupPolicies => Set<GroupPolicy>();
    public DbSet<ContextSpace> ContextSpaces => Set<ContextSpace>();
    public DbSet<PersonaIdentityRevision> Identities => Set<PersonaIdentityRevision>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationSession> Sessions => Set<ConversationSession>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();
    public DbSet<ConversationSummary> Summaries => Set<ConversationSummary>();
    public DbSet<GlobalEmotionState> Emotions => Set<GlobalEmotionState>();
    public DbSet<EmotionEvent> EmotionEvents => Set<EmotionEvent>();
    public DbSet<DailyNarrative> Narratives => Set<DailyNarrative>();
    public DbSet<RelationshipState> Relationships => Set<RelationshipState>();
    public DbSet<RelationshipObservation> Observations => Set<RelationshipObservation>();
    public DbSet<Core.Memory> Memories => Set<Core.Memory>();
    public DbSet<MemoryMaintenanceCandidate> MemoryCandidates => Set<MemoryMaintenanceCandidate>();
    public DbSet<MemoryRevocation> Revocations => Set<MemoryRevocation>();
    public DbSet<EmbeddingProfile> EmbeddingProfiles => Set<EmbeddingProfile>();
    public DbSet<MemoryEmbedding> Embeddings => Set<MemoryEmbedding>();
    public DbSet<ReflectionResult> Reflections => Set<ReflectionResult>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxProcessedEvent> Inbox => Set<InboxProcessedEvent>();
    public DbSet<OutgoingMessageBatch> Batches => Set<OutgoingMessageBatch>();
    public DbSet<OutgoingMessagePart> Parts => Set<OutgoingMessagePart>();
    public DbSet<ModelProfile> Models => Set<ModelProfile>();
    public DbSet<UsageRecord> Usage => Set<UsageRecord>();
    public DbSet<MediaAsset> Media => Set<MediaAsset>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ApplicationUser>().HasKey(x => x.Id);
        b.Entity<ApplicationUser>().HasIndex(x => x.NormalizedEmail).IsUnique();
        b.HasPostgresExtension("vector");
        b.Entity<BotInstance>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<BotInstance>().Property(x => x.SettingsRevision).IsConcurrencyToken();
        b.Entity<BotInstance>().ToTable(t => t.HasCheckConstraint("CK_Bot_Settings",
            "\"MessageRetentionDays\" BETWEEN 1 AND 3650 AND \"PrivateIdleMinutes\" BETWEEN 1 AND 1440 AND \"GroupIdleMinutes\" BETWEEN 1 AND 1440 AND \"DailyTokenLimit\" > 0 AND \"MonthlyTokenLimit\" > 0"));
        b.Entity<PlatformIdentity>().HasIndex(x => new { x.Platform, x.ExternalId }).IsUnique();
        b.Entity<PlatformIdentity>().HasOne<LogicalUser>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<ApplicationUser>().HasIndex(x => x.LogicalUserId).IsUnique();
        b.Entity<ApplicationUser>().HasOne<LogicalUser>().WithMany().HasForeignKey(x => x.LogicalUserId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<BindingChallenge>().HasIndex(x => x.CodeHash).IsUnique();
        b.Entity<PlatformBinding>().HasIndex(x => x.BotId).IsUnique();
        b.Entity<PlatformBinding>().HasIndex(x => new { x.AdapterType, x.PlatformAccountId }).IsUnique();
        b.Entity<GroupPolicy>().HasIndex(x => new { x.BotId, x.GroupId }).IsUnique();
        b.Entity<GroupPolicy>().HasOne<ContextSpace>().WithMany().HasForeignKey(x => x.ContextSpaceId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<ContextSpace>().Property(x => x.Name).HasMaxLength(100);
        b.Entity<LogicalUser>().Property(x => x.PreferredName).HasMaxLength(100);
        b.Entity<PersonaIdentityRevision>().HasIndex(x => new { x.BotId, x.Revision }).IsUnique();
        b.Entity<Conversation>().HasIndex(x => new { x.BotId, x.Platform, x.Kind, x.ExternalId }).IsUnique();
        b.Entity<ConversationSession>().HasIndex(x => new { x.Status, x.DeadlineAt });
        b.Entity<ConversationSession>().HasIndex(x => x.ConversationId).IsUnique().HasFilter("\"Status\" = 0");
        b.Entity<ConversationSession>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<ConversationSession>().HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId);
        b.Entity<ChatMessage>().Property(x => x.Sequence).UseIdentityAlwaysColumn();
        b.Entity<ChatMessage>().HasIndex(x => new { x.BotId, x.Platform, x.ExternalMessageId }).IsUnique();
        b.Entity<ChatMessage>().HasIndex(x => new { x.ConversationId, x.Sequence });
        b.Entity<ChatMessage>().HasIndex(x => x.ExpiresAt);
        b.Entity<ChatMessage>().HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<ChatMessage>().HasOne<ConversationSession>().WithMany().HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<ConversationSummary>().HasIndex(x => x.SessionId).IsUnique();
        b.Entity<ConversationSummary>().Property(x => x.MessageIdsJson).HasDefaultValue("[]");
        b.Entity<GlobalEmotionState>().HasKey(x => x.BotId);
        b.Entity<GlobalEmotionState>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<GlobalEmotionState>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_Mood_Range",
                "\"Valence\" BETWEEN -1 AND 1 AND \"Arousal\" BETWEEN 0 AND 1 AND \"Energy\" BETWEEN 0 AND 1");
        });
        b.Entity<EmotionEvent>().HasIndex(x => new { x.BotId, x.Sequence }).IsUnique();
        b.Entity<EmotionEvent>().HasIndex(x => new { x.SourceReflectionId, x.SourceEventKey }).IsUnique();
        b.Entity<DailyNarrative>().HasIndex(x => new { x.BotId, x.Date }).IsUnique();
        b.Entity<RelationshipState>().HasIndex(x => new { x.BotId, x.UserId, x.GroupId }).IsUnique()
            .AreNullsDistinct(false);
        b.Entity<RelationshipState>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<LogicalUser>().Property(x => x.RevocationEpoch).IsConcurrencyToken();
        b.Entity<RelationshipObservation>().HasIndex(x => new { x.ReflectionId, x.Candidate }).IsUnique();
        b.Entity<Core.Memory>().HasIndex(x => new { x.BotId, x.Scope, x.ScopeOwnerId, x.Status });
        b.Entity<Core.Memory>().HasIndex(x => new { x.SubjectId, x.FactKey });
        b.Entity<Core.Memory>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<Core.Memory>().HasIndex(x => x.SupersedesMemoryId);
        b.Entity<MemoryMaintenanceCandidate>().HasIndex(x => new { x.SourceReflectionId, x.CandidateHash }).IsUnique();
        b.Entity<MemoryMaintenanceCandidate>().HasIndex(x => new { x.SubjectId, x.Status });
        b.Entity<Core.Memory>().HasOne<Core.Memory>().WithMany().HasForeignKey(x => x.SharedFromMemoryId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<Core.Memory>().HasIndex(x => new { x.SharedFromMemoryId, x.Scope, x.ScopeOwnerId })
            .IsUnique().HasFilter("\"SharedFromMemoryId\" IS NOT NULL AND \"Status\" <> 2");
        b.Entity<Core.Memory>().ToTable(t =>
            t.HasCheckConstraint("CK_Memory_Enums", "\"Scope\" BETWEEN 0 AND 5 AND \"Status\" BETWEEN 0 AND 2"));
        b.Entity<MemoryRevocation>().HasIndex(x => new { x.BotId, x.UserId, x.Epoch });
        b.Entity<EmbeddingProfile>().HasIndex(x => new { x.BotId, x.Provider, x.Model, x.Dimensions, x.Version })
            .IsUnique();
        b.Entity<MemoryEmbedding>().HasKey(x => new { x.MemoryId, x.ProfileId });
        b.Entity<MemoryEmbedding>().Property(x => x.Values).HasColumnType("real[]");
        b.Entity<MemoryEmbedding>().HasOne<Core.Memory>().WithMany().HasForeignKey(x => x.MemoryId);
        b.Entity<MemoryEmbedding>().HasOne<EmbeddingProfile>().WithMany().HasForeignKey(x => x.ProfileId);
        b.Entity<ReflectionResult>().HasIndex(x => new { x.SessionId, x.SubjectId, x.Kind, x.Boundary }).IsUnique();
        b.Entity<OutboxMessage>().Property(x => x.Sequence).UseIdentityAlwaysColumn();
        b.Entity<OutboxMessage>().HasIndex(x => new { x.Status, x.NextAttemptAt, x.PublishedAt });
        b.Entity<OutboxMessage>().HasIndex(x => new { x.ConversationId, x.Sequence });
        b.Entity<InboxProcessedEvent>().HasKey(x => new { x.EventId, x.HandlerName });
        b.Entity<OutgoingMessageBatch>().HasIndex(x => x.SourceJobId);
        b.Entity<OutgoingMessagePart>().HasIndex(x => new { x.BatchId, x.Sequence }).IsUnique();
        b.Entity<OutgoingMessagePart>().HasOne<OutgoingMessageBatch>().WithMany().HasForeignKey(x => x.BatchId);
        b.Entity<ModelProfile>().HasIndex(x => new { x.BotId, x.TaskKind }).IsUnique();
        b.Entity<UsageRecord>().HasIndex(x => new { x.BotId, x.CreatedAt });
        b.Entity<MediaAsset>().HasIndex(x => x.ExpiresAt);
        foreach (var type in b.Model.GetEntityTypes()
                     .Where(t => t.ClrType != typeof(BotInstance) && t.FindProperty("BotId") != null))
            b.Entity(type.ClrType).HasOne(typeof(BotInstance)).WithMany().HasForeignKey("BotId")
                .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<WonderDbContext>
{
    public WonderDbContext CreateDbContext(string[] args)
    {
        return new WonderDbContext(new DbContextOptionsBuilder<WonderDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") ??
                       "Host=localhost;Port=5432;Database=wonder;Username=wonder;Password=wonder").Options);
    }
}
