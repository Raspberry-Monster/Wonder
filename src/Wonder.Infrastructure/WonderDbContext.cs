using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Wonder.Core;

namespace Wonder.Infrastructure;

public class WonderDbContext(DbContextOptions<WonderDbContext> options)
    : IdentityUserContext<ApplicationUser, Guid>(options)
{
    public DbSet<BotInstance> Bots => Set<BotInstance>();
    public DbSet<LogicalUser> LogicalUsers => Set<LogicalUser>();
    public DbSet<PlatformIdentity> PlatformIdentities => Set<PlatformIdentity>();
    public DbSet<PlatformBinding> Bindings => Set<PlatformBinding>();
    public DbSet<BindingChallenge> BindingChallenges => Set<BindingChallenge>();
    public DbSet<GroupPolicy> GroupPolicies => Set<GroupPolicy>();
    public DbSet<PersonaIdentityRevision> Identities => Set<PersonaIdentityRevision>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationSession> Sessions => Set<ConversationSession>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();
    public DbSet<ConversationSummary> Summaries => Set<ConversationSummary>();
    public DbSet<GlobalEmotionState> Emotions => Set<GlobalEmotionState>();
    public DbSet<EmotionEvent> EmotionEvents => Set<EmotionEvent>();
    public DbSet<RelationshipState> Relationships => Set<RelationshipState>();
    public DbSet<RelationshipObservation> Observations => Set<RelationshipObservation>();
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<MemoryOperation> MemoryOperations => Set<MemoryOperation>();
    public DbSet<MemoryOperationItem> MemoryOperationItems => Set<MemoryOperationItem>();
    public DbSet<EvidenceSuppression> EvidenceSuppressions => Set<EvidenceSuppression>();
    public DbSet<RelationshipClaim> RelationshipClaims => Set<RelationshipClaim>();
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
    public DbSet<MuteSession> Mutes => Set<MuteSession>();
    public DbSet<ForceReceipt> ForceReceipts => Set<ForceReceipt>();
    public DbSet<AgentOperation> AgentOperations => Set<AgentOperation>();
    public DbSet<ChatDisposition> ChatDispositions => Set<ChatDisposition>();
    public DbSet<ReflectionGeneration> ReflectionGenerations => Set<ReflectionGeneration>();
    public DbSet<AgentReflectionQuota> AgentReflectionQuotas => Set<AgentReflectionQuota>();
    public DbSet<AgentReflectionRequest> AgentReflectionRequests => Set<AgentReflectionRequest>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<NoteRevision> NoteRevisions => Set<NoteRevision>();
    public DbSet<NoteDependency> NoteDependencies => Set<NoteDependency>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.HasPostgresExtension("vector");
        b.ApplyConfigurationsFromAssembly(typeof(WonderDbContext).Assembly);
        EnumModelConfiguration.Apply(b);
    }
}

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<WonderDbContext>
{
    public WonderDbContext CreateDbContext(string[] args)
    {
        return new WonderDbContext(new DbContextOptionsBuilder<WonderDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") ??
                       "Host=localhost;Port=5432;Database=wonder;Username=wonder;Password=wonder", o => o.UseVector())
            .Options);
    }
}