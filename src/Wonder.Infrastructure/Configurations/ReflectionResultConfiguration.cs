using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class ReflectionResultConfiguration : IEntityTypeConfiguration<ReflectionResult>
{
    public void Configure(EntityTypeBuilder<ReflectionResult> builder)
    {
        builder.HasIndex(x => new { x.SessionId, x.SubjectId, x.Kind, x.Boundary }).IsUnique();
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}