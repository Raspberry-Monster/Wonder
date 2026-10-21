using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class PersonaIdentityRevisionConfiguration : IEntityTypeConfiguration<PersonaIdentityRevision>
{
    public void Configure(EntityTypeBuilder<PersonaIdentityRevision> builder)
    {
        builder.HasIndex(x => new { x.BotId, x.Revision }).IsUnique();
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}