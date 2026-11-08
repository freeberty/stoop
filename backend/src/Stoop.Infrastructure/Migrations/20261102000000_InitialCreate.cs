using System.Reflection;
using Stoop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Stoop.Infrastructure.Migrations;

/// <summary>
/// Hand-written initial migration: runs Sql/001_init.sql (PostGIS extension, tables, GiST and other indexes).
/// The SQL was verified against PostgreSQL 16 / PostGIS 3.4. It has no model snapshot; when the model
/// changes, delete this class and run `dotnet ef migrations add Initial` to regenerate a snapshot-based history.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261102000000_InitialCreate")]
public sealed class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql(ReadScript("001_init.sql"));

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            DROP TABLE IF EXISTS nostr_challenges, audit_log, messages, conversation_members, conversations,
                community_members, communities, external_identities, users CASCADE;
            """);

    private static string ReadScript(string name)
    {
        using var stream = typeof(InitialCreate).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded SQL script '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
