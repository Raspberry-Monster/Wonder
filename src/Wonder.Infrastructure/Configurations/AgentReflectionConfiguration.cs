using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class ReflectionGenerationConfiguration : IEntityTypeConfiguration<ReflectionGeneration>
{
    public void Configure(EntityTypeBuilder<ReflectionGeneration> b)
    {
        b.HasIndex(x => new { x.BotId, x.SessionId, x.SubjectId, x.SourceSignature }).IsUnique();
        b.HasIndex(x => x.JobId).IsUnique();
    }
}

public sealed class AgentReflectionQuotaConfiguration : IEntityTypeConfiguration<AgentReflectionQuota>
{
    public void Configure(EntityTypeBuilder<AgentReflectionQuota> b)
    {
        b.HasKey(x => new { x.BotId, x.Date });
        b.ToTable(t => t.HasCheckConstraint("CK_Reflection_Quota", "\"UsedNormal\" BETWEEN 0 AND 2 AND \"UsedImportant\" BETWEEN 0 AND 1"));
    }
}

public sealed class AgentReflectionRequestConfiguration : IEntityTypeConfiguration<AgentReflectionRequest>
{
    public void Configure(EntityTypeBuilder<AgentReflectionRequest> b)
    {
        b.HasIndex(x => x.OperationId).IsUnique();
        b.HasIndex(x => new { x.BotId, x.UserId, x.CreatedAt });
    }
}
