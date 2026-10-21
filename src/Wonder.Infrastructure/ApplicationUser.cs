using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Wonder.Infrastructure.Configurations;

namespace Wonder.Infrastructure;

[EntityTypeConfiguration(typeof(ApplicationUserConfiguration))]
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public Guid? LogicalUserId { get; set; }
}
