using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class LogicalUserConfiguration : IEntityTypeConfiguration<LogicalUser>
{
    public void Configure(EntityTypeBuilder<LogicalUser> builder)
    {
        builder.Property(x => x.PreferredName).HasMaxLength(100);
        builder.Property(x => x.RevocationEpoch).IsConcurrencyToken();
        builder.Property(x => x.NotesEpoch).IsConcurrencyToken();
    }
}
