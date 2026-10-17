using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pulsar.Domain.Certificates;

namespace Pulsar.Infrastructure.Persistence.Configurations;

internal sealed class PublicIpCertificateSettingsConfiguration : IEntityTypeConfiguration<PublicIpCertificateSettings>
{
    public void Configure(EntityTypeBuilder<PublicIpCertificateSettings> builder)
    {
        builder.ToTable("public_ip_certificate_settings", t =>
        {
            t.HasCheckConstraint("ck_public_ip_certificate_settings_singleton", $"id = {PublicIpCertificateSettings.SingletonId}");
            t.HasCheckConstraint("ck_public_ip_certificate_settings_directory", SnakeCaseEnum<AcmeDirectoryKind>.CheckConstraint("directory"));
            t.HasCheckConstraint("ck_public_ip_certificate_settings_enabled_ip", "NOT enabled OR public_ip IS NOT NULL");
        });
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.PublicIp).HasMaxLength(PublicIpCertificateSettings.MaxIpLength);
        builder.Property(s => s.ContactEmail).HasMaxLength(PublicIpCertificateSettings.MaxEmailLength);
        builder.Property(s => s.Directory).HasConversion<SnakeCaseEnumConverter<AcmeDirectoryKind>>();
        builder.Property(s => s.AccountUrl).HasMaxLength(PublicIpCertificateSettings.MaxUrlLength);
        builder.Property(s => s.AccountDirectory).HasConversion<SnakeCaseEnumConverter<AcmeDirectoryKind>>();
        builder.Property(s => s.AccountContact).HasMaxLength(PublicIpCertificateSettings.MaxEmailLength + 7);
        builder.Property(s => s.TermsOfServiceUrl).HasMaxLength(PublicIpCertificateSettings.MaxUrlLength);
        builder.Property(s => s.CertificateIp).HasMaxLength(PublicIpCertificateSettings.MaxIpLength);
        builder.Property(s => s.CertificateDirectory).HasConversion<SnakeCaseEnumConverter<AcmeDirectoryKind>>();
        builder.Property(s => s.CertificateAriId).HasMaxLength(256);
        builder.Property(s => s.CertificateSha256).HasMaxLength(64);
        builder.Property(s => s.LastError).HasMaxLength(PublicIpCertificateSettings.MaxErrorLength);
        builder.Property(s => s.ExpiryNotifiedFor).HasMaxLength(64);
        builder.PrimitiveCollection(s => s.RecentAttempts);
        builder.Property<uint>("Version").IsRowVersion();
    }
}
