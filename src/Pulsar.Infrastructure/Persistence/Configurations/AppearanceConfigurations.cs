using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pulsar.Domain.Appearance;

namespace Pulsar.Infrastructure.Persistence.Configurations;

internal sealed class AppearanceSettingsConfiguration : IEntityTypeConfiguration<AppearanceSettings>
{
    public void Configure(EntityTypeBuilder<AppearanceSettings> builder)
    {
        builder.ToTable("appearance_settings", t =>
            t.HasCheckConstraint("ck_appearance_settings_singleton", $"id = {AppearanceSettings.SingletonId}"));
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property<uint>("Version").IsRowVersion();
    }
}
