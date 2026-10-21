using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class BindingChallengeConfiguration : IEntityTypeConfiguration<BindingChallenge>
{
    public void Configure(EntityTypeBuilder<BindingChallenge> builder)
    {
        builder.HasIndex(x => x.CodeHash).IsUnique();
    }
}