using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connector.Kit.Hosting.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Class = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OwnerSubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CapabilitiesJson = table.Column<string>(type: "text", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    LastHeartbeatAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: false),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "challenges",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    JobId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    ImageBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    ExpiresAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: false),
                    AnsweredAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: true),
                    AnswerValue = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_challenges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "enrollments",
                columns: table => new
                {
                    CodeHash = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Subject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ExpiresAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: false),
                    RedeemedAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrollments", x => x.CodeHash);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ParamsJson = table.Column<string>(type: "text", nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LeaseExpiresAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CredentialSubmitted = table.Column<bool>(type: "boolean", nullable: false),
                    Step = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StepsDoneJson = table.Column<string>(type: "text", nullable: false),
                    Complete = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ErrorDetail = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: false),
                    UpdatedAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: false),
                    InputsJson = table.Column<string>(type: "text", nullable: true),
                    MaterialJson = table.Column<string>(type: "text", nullable: true),
                    ConfigJson = table.Column<string>(type: "text", nullable: false),
                    ProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "profiles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AgentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Healthy = table.Column<bool>(type: "boolean", nullable: false),
                    LastOkAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "provider_status",
                columns: table => new
                {
                    ProviderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Since = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: false),
                    ReasonKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_status", x => x.ProviderId);
                });

            migrationBuilder.CreateTable(
                name: "results",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    JobId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Resource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    RawJson = table.Column<string>(type: "text", nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Cursor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_results", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ManifestVersion = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConfigJson = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: false),
                    CreatedAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: false),
                    UpdatedAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: false),
                    Label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ConsentAcceptedAt = table.Column<string>(type: "character varying(28)", unicode: false, maxLength: 28, nullable: true),
                    ConsentTermsVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DeviceClass = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProviderAccountJson = table.Column<string>(type: "text", nullable: true),
                    PendingBundle = table.Column<string>(type: "text", nullable: true),
                    PendingCredentialBundle = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agents_OwnerSubject",
                table: "agents",
                column: "OwnerSubject");

            migrationBuilder.CreateIndex(
                name: "IX_agents_TokenHash",
                table: "agents",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_challenges_ExpiresAt",
                table: "challenges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_challenges_JobId",
                table: "challenges",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_enrollments_ExpiresAt",
                table: "enrollments",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_LeaseExpiresAt",
                table: "jobs",
                column: "LeaseExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_SessionId",
                table: "jobs",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_State_ProviderId",
                table: "jobs",
                columns: new[] { "State", "ProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_profiles_AgentId",
                table: "profiles",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_profiles_SessionId",
                table: "profiles",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_results_CreatedAt",
                table: "results",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_results_Cursor",
                table: "results",
                column: "Cursor");

            migrationBuilder.CreateIndex(
                name: "IX_results_SessionId_Resource_ExternalId",
                table: "results",
                columns: new[] { "SessionId", "Resource", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_ExpiresAt",
                table: "sessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_Subject_ProviderId",
                table: "sessions",
                columns: new[] { "Subject", "ProviderId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "challenges");

            migrationBuilder.DropTable(
                name: "enrollments");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "profiles");

            migrationBuilder.DropTable(
                name: "provider_status");

            migrationBuilder.DropTable(
                name: "results");

            migrationBuilder.DropTable(
                name: "sessions");
        }
    }
}
