using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "AgentOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutorLeaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    ArgumentsHash = table.Column<string>(type: "text", nullable: false),
                    ReceiptJson = table.Column<string>(type: "text", nullable: false),
                    CommittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentOperations", x => x.Id);
                    table.CheckConstraint("CK_AgentOperations_Action", "\"Action\" IN ('interaction_mute','interaction_reset_force','interaction_unmute','interaction_no_reply','notes_create','notes_search','notes_read','notes_update','notes_delete','reflection_request_review','reflection_list_runs','reflection_get_run')");
                });

            migrationBuilder.CreateTable(
                name: "AgentReflectionQuotas",
                columns: table => new
                {
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    TimeZoneId = table.Column<string>(type: "text", nullable: false),
                    UsedNormal = table.Column<int>(type: "integer", nullable: false),
                    UsedImportant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentReflectionQuotas", x => new { x.BotId, x.Date });
                    table.CheckConstraint("CK_Reflection_Quota", "\"UsedNormal\" BETWEEN 0 AND 2 AND \"UsedImportant\" BETWEEN 0 AND 1");
                });

            migrationBuilder.CreateTable(
                name: "AgentReflectionRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawBoundary = table.Column<long>(type: "bigint", nullable: false),
                    GenerationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Priority = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentReflectionRequests", x => x.Id);
                    table.CheckConstraint("CK_AgentReflectionRequests_Priority", "\"Priority\" IN ('Normal','Important')");
                });

            migrationBuilder.CreateTable(
                name: "BindingChallenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BindingChallenges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatDispositions",
                columns: table => new
                {
                    ChatJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ForceReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatDispositions", x => x.ChatJobId);
                });

            migrationBuilder.CreateTable(
                name: "Inbox",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    HandlerName = table.Column<string>(type: "text", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inbox", x => new { x.EventId, x.HandlerName });
                    table.CheckConstraint("CK_Inbox_HandlerName", "\"HandlerName\" IN ('chat','outgoing','reflection','emotion','relationship','memory','memory-maintenance','embedding','maintenance','summary','memory-privacy')");
                });

            migrationBuilder.CreateTable(
                name: "LogicalUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AllowMemory = table.Column<bool>(type: "boolean", nullable: false),
                    AllowNotes = table.Column<bool>(type: "boolean", nullable: false),
                    NotesEpoch = table.Column<long>(type: "bigint", nullable: false),
                    AllowMemoryMerge = table.Column<bool>(type: "boolean", nullable: false),
                    AllowMemoryUpdate = table.Column<bool>(type: "boolean", nullable: false),
                    AllowMemoryArchive = table.Column<bool>(type: "boolean", nullable: false),
                    RetainMemoryHistory = table.Column<bool>(type: "boolean", nullable: false),
                    PreferredName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RevocationEpoch = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogicalUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NoteDependencies",
                columns: table => new
                {
                    NoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    TargetKind = table.Column<string>(type: "text", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteDependencies", x => new { x.NoteId, x.Version, x.TargetKind, x.TargetId });
                    table.CheckConstraint("CK_NoteDependencies_TargetKind", "\"TargetKind\" IN ('Chat','Message','Summary','Note')");
                });

            migrationBuilder.CreateTable(
                name: "NoteRevisions",
                columns: table => new
                {
                    NoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteRevisions", x => new { x.NoteId, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "Notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorKind = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ClaimKind = table.Column<string>(type: "text", nullable: true),
                    SourceMessageIdsJson = table.Column<string>(type: "text", nullable: false),
                    SourceMemoryIdsJson = table.Column<string>(type: "text", nullable: false),
                    SourceInvalidated = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PrivacyDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notes", x => x.Id);
                    table.CheckConstraint("CK_Notes_AuthorKind", "\"AuthorKind\" IN ('Agent','User')");
                    table.CheckConstraint("CK_Notes_ClaimKind", "\"ClaimKind\" IN ('observation','guess','uncertain')");
                });

            migrationBuilder.CreateTable(
                name: "ReflectionGenerations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    RawBoundary = table.Column<long>(type: "bigint", nullable: false),
                    SourceSignature = table.Column<string>(type: "text", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CancellationReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReflectionGenerations", x => x.Id);
                    table.CheckConstraint("CK_ReflectionGenerations_CancellationReason", "\"CancellationReason\" IN ('AuthorizationChanged','SourceChanged','NoEligibleSources','PrivacyRevocation')");
                    table.CheckConstraint("CK_ReflectionGenerations_Status", "\"Status\" IN ('Queued','Running','Completed','Cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "PlatformIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlatformIdentities_LogicalUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "LogicalUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LogicalUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_LogicalUsers_LogicalUserId",
                        column: x => x.LogicalUserId,
                        principalTable: "LogicalUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Bots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    SettingsRevision = table.Column<long>(type: "bigint", nullable: false),
                    MessageRetentionDays = table.Column<int>(type: "integer", nullable: false),
                    PrivateIdleMinutes = table.Column<int>(type: "integer", nullable: false),
                    GroupIdleMinutes = table.Column<int>(type: "integer", nullable: false),
                    PersistMessages = table.Column<bool>(type: "boolean", nullable: false),
                    DailyTokenLimit = table.Column<long>(type: "bigint", nullable: false),
                    MonthlyTokenLimit = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bots", x => x.Id);
                    table.CheckConstraint("CK_Bot_Settings", "\"MessageRetentionDays\" BETWEEN 1 AND 3650 AND \"PrivateIdleMinutes\" BETWEEN 1 AND 1440 AND \"GroupIdleMinutes\" BETWEEN 1 AND 1440 AND \"DailyTokenLimit\" > 0 AND \"MonthlyTokenLimit\" > 0");
                    table.ForeignKey(
                        name: "FK_Bots_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Batches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSnapshotJson = table.Column<string>(type: "text", nullable: true),
                    IsSystemNotice = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Batches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Batches_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Bindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdapterType = table.Column<string>(type: "text", nullable: false),
                    PlatformAccountId = table.Column<string>(type: "text", nullable: false),
                    ApiBaseUrl = table.Column<string>(type: "text", nullable: false),
                    ProtectedAccessToken = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bindings_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.CheckConstraint("CK_Conversations_Kind", "\"Kind\" IN (0,1)");
                    table.ForeignKey(
                        name: "FK_Conversations_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmbeddingProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: false),
                    Dimensions = table.Column<int>(type: "integer", nullable: false),
                    Metric = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmbeddingProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmbeddingProfiles_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmotionEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    SourceReflectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceEventKey = table.Column<string>(type: "text", nullable: false),
                    DeltaJson = table.Column<string>(type: "text", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmotionEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmotionEvents_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Emotions",
                columns: table => new
                {
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Valence = table.Column<double>(type: "double precision", nullable: false),
                    Arousal = table.Column<double>(type: "double precision", nullable: false),
                    Energy = table.Column<double>(type: "double precision", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    LastEvolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Emotions", x => x.BotId);
                    table.CheckConstraint("CK_Mood_Range", "\"Valence\" BETWEEN -1 AND 1 AND \"Arousal\" BETWEEN 0 AND 1 AND \"Energy\" BETWEEN 0 AND 1");
                    table.ForeignKey(
                        name: "FK_Emotions_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<string>(type: "text", nullable: false),
                    RespondToMention = table.Column<bool>(type: "boolean", nullable: false),
                    RespondToReply = table.Column<bool>(type: "boolean", nullable: false),
                    CommandPrefix = table.Column<string>(type: "text", nullable: false),
                    PersistMessages = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupPolicies_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Identities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Identities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Identities_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Media_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Memories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactKeyHash = table.Column<string>(type: "text", nullable: false),
                    SupersedesMemoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsOutdated = table.Column<bool>(type: "boolean", nullable: false),
                    LastConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactKey = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SourceReflectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memories", x => x.Id);
                    table.CheckConstraint("CK_Memories_Status", "\"Status\" IN (0,1,2)");
                    table.ForeignKey(
                        name: "FK_Memories_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MemoryCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactKey = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    AssertionsJson = table.Column<string>(type: "text", nullable: true),
                    SourceReflectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevocationEpoch = table.Column<long>(type: "bigint", nullable: false),
                    CandidateHash = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: true),
                    TargetMemoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResultMemoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryCandidates", x => x.Id);
                    table.CheckConstraint("CK_MemoryCandidates_Action", "\"Action\" IN (0,1,2,3,4,5)");
                    table.CheckConstraint("CK_MemoryCandidates_Status", "\"Status\" IN (0,1,2,3)");
                    table.ForeignKey(
                        name: "FK_MemoryCandidates_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MemoryOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ArgumentsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TargetMemoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpectedRevision = table.Column<long>(type: "bigint", nullable: false),
                    ResultMemoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultRevision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Stage = table.Column<string>(type: "text", nullable: false),
                    ScopeHash = table.Column<string>(type: "text", nullable: false),
                    TargetsJson = table.Column<string>(type: "text", nullable: false),
                    AssertionHashesJson = table.Column<string>(type: "text", nullable: false),
                    PreviewExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Fenced = table.Column<bool>(type: "boolean", nullable: false),
                    CleanupVerified = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ErrorCode = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryOperations_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MemoryOperations_LogicalUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "LogicalUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskKind = table.Column<int>(type: "integer", nullable: false),
                    TokenizerId = table.Column<string>(type: "text", nullable: false),
                    MessageTokenOverhead = table.Column<int>(type: "integer", nullable: false),
                    ImageTokenBudget = table.Column<int>(type: "integer", nullable: false),
                    Endpoint = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: false),
                    ProtectedApiKey = table.Column<string>(type: "text", nullable: false),
                    ContextWindow = table.Column<int>(type: "integer", nullable: false),
                    MaxOutputTokens = table.Column<int>(type: "integer", nullable: false),
                    SupportsImages = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsJson = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsTools = table.Column<bool>(type: "boolean", nullable: false),
                    ThinkingEnabled = table.Column<bool>(type: "boolean", nullable: true),
                    ReasoningEffort = table.Column<string>(type: "text", nullable: true),
                    EmbeddingDimensions = table.Column<int>(type: "integer", nullable: false),
                    InputCostPerMillion = table.Column<decimal>(type: "numeric", nullable: false),
                    OutputCostPerMillion = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models", x => x.Id);
                    table.CheckConstraint("CK_Models_TaskKind", "\"TaskKind\" IN (0,1,2,4,5)");
                    table.ForeignKey(
                        name: "FK_Models_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Observations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReflectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Candidate = table.Column<string>(type: "text", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    Accepted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Observations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Observations_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgressJson = table.Column<string>(type: "text", nullable: true),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CausationId = table.Column<Guid>(type: "uuid", nullable: true),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    ScheduledDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ErrorCode = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id);
                    table.CheckConstraint("CK_Outbox_Status", "\"Status\" IN (0,1,2,3,4)");
                    table.CheckConstraint("CK_Outbox_Type", "\"Type\" IN ('chat','outgoing','reflection','emotion','relationship','memory','memory-maintenance','embedding','maintenance','summary','memory-privacy')");
                    table.ForeignKey(
                        name: "FK_Outbox_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Reflections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Boundary = table.Column<long>(type: "bigint", nullable: false),
                    RevocationEpoch = table.Column<long>(type: "bigint", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    ModelId = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    MemorySourcesBlocked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reflections", x => x.Id);
                    table.CheckConstraint("CK_Reflections_Kind", "\"Kind\" IN ('light','full')");
                    table.ForeignKey(
                        name: "FK_Reflections_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Relationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Relationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Relationships_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Revocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceKind = table.Column<string>(type: "text", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResourceVersion = table.Column<long>(type: "bigint", nullable: true),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactKeyHash = table.Column<string>(type: "text", nullable: false),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Revocations", x => x.Id);
                    table.CheckConstraint("CK_Revocations_ResourceKind", "\"ResourceKind\" IN ('Memory','Note')");
                    table.ForeignKey(
                        name: "FK_Revocations_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Summaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ThroughSequence = table.Column<long>(type: "bigint", nullable: false),
                    SubjectIdsJson = table.Column<string>(type: "text", nullable: false),
                    MemoryIdsJson = table.Column<string>(type: "text", nullable: false),
                    MessageIdsJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    ContextInvalidated = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    SourcesJson = table.Column<string>(type: "text", nullable: true),
                    BaseSummaryId = table.Column<Guid>(type: "uuid", nullable: true),
                    BaseSummaryRevision = table.Column<long>(type: "bigint", nullable: true),
                    SafeProjectionJson = table.Column<string>(type: "text", nullable: true),
                    ProjectionRevision = table.Column<long>(type: "bigint", nullable: false),
                    ProjectionBaseSummaryRevision = table.Column<long>(type: "bigint", nullable: false),
                    ProjectionState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProjectionExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Summaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Summaries_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Usage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Estimated = table.Column<bool>(type: "boolean", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskKind = table.Column<int>(type: "integer", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    Cost = table.Column<decimal>(type: "numeric", nullable: false),
                    ReservationExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Usage", x => x.Id);
                    table.CheckConstraint("CK_Usage_TaskKind", "\"TaskKind\" IN (0,1,2,4,5)");
                    table.ForeignKey(
                        name: "FK_Usage_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Parts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Parts", x => x.Id);
                    table.CheckConstraint("CK_Parts_Status", "\"Status\" IN (0,1,2,3,4)");
                    table.ForeignKey(
                        name: "FK_Parts_Batches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "Batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Mutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    ForceRemaining = table.Column<int>(type: "integer", nullable: false),
                    ResetGeneration = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mutes", x => x.Id);
                    table.CheckConstraint("CK_Mute_ForceRemaining", "\"ForceRemaining\" >= 0 AND \"ForceRemaining\" <= 3");
                    table.ForeignKey(
                        name: "FK_Mutes_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartingMoodRevision = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ClosingBoundary = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.CheckConstraint("CK_Sessions_Status", "\"Status\" IN (0,1,2,3)");
                    table.ForeignKey(
                        name: "FK_Sessions_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Sessions_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Embeddings",
                columns: table => new
                {
                    MemoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    Values = table.Column<Vector>(type: "vector", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Embeddings", x => new { x.MemoryId, x.ProfileId });
                    table.ForeignKey(
                        name: "FK_Embeddings_EmbeddingProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "EmbeddingProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Embeddings_Memories_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceSuppressions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageVersion = table.Column<string>(type: "text", nullable: false),
                    SpansJson = table.Column<string>(type: "text", nullable: false),
                    FactKeyHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceSuppressions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvidenceSuppressions_MemoryOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "MemoryOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MemoryOperationItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceVersion = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    ErrorCode = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryOperationItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryOperationItems_MemoryOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "MemoryOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RelationshipClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipId = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Origin = table.Column<string>(type: "text", nullable: false),
                    Qualification = table.Column<string>(type: "text", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    MemorySourcesJson = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelationshipClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RelationshipClaims_Relationships_RelationshipId",
                        column: x => x.RelationshipId,
                        principalTable: "Relationships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ForceReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    MuteSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformEventKey = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChargeGeneration = table.Column<long>(type: "bigint", nullable: false),
                    RemainingAfter = table.Column<int>(type: "integer", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessingStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompensationStatus = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForceReceipts", x => x.Id);
                    table.CheckConstraint("CK_ForceReceipts_CompensationStatus", "\"CompensationStatus\" IN ('Refunded','AlreadyProcessed','Expired','SupersededByReset')");
                    table.ForeignKey(
                        name: "FK_ForceReceipts_Mutes_MuteSessionId",
                        column: x => x.MuteSessionId,
                        principalTable: "Mutes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SenderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    ExternalMessageId = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    InReplyToSequence = table.Column<long>(type: "bigint", nullable: true),
                    SegmentsJson = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    ContextSnapshotJson = table.Column<string>(type: "text", nullable: true),
                    ContextInvalidated = table.Column<bool>(type: "boolean", nullable: false),
                    AllowReflection = table.Column<bool>(type: "boolean", nullable: false),
                    ShouldRespond = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                    table.CheckConstraint("CK_Messages_Role", "\"Role\" IN ('user','assistant')");
                    table.ForeignKey(
                        name: "FK_Messages_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Messages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Messages_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentOperations_ChatJobId_Ordinal",
                table: "AgentOperations",
                columns: new[] { "ChatJobId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentReflectionRequests_BotId_UserId_CreatedAt",
                table: "AgentReflectionRequests",
                columns: new[] { "BotId", "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentReflectionRequests_OperationId",
                table: "AgentReflectionRequests",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_BotId",
                table: "Batches",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_SourceJobId",
                table: "Batches",
                column: "SourceJobId");

            migrationBuilder.CreateIndex(
                name: "IX_BindingChallenges_CodeHash",
                table: "BindingChallenges",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bindings_AdapterType_PlatformAccountId",
                table: "Bindings",
                columns: new[] { "AdapterType", "PlatformAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bindings_BotId",
                table: "Bindings",
                column: "BotId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bots_OwnerId",
                table: "Bots",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatDispositions_OperationId",
                table: "ChatDispositions",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_BotId_Platform_Kind_ExternalId",
                table: "Conversations",
                columns: new[] { "BotId", "Platform", "Kind", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingProfiles_BotId_Provider_Model_Dimensions_Version",
                table: "EmbeddingProfiles",
                columns: new[] { "BotId", "Provider", "Model", "Dimensions", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Embeddings_ProfileId",
                table: "Embeddings",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_EmotionEvents_BotId_Sequence",
                table: "EmotionEvents",
                columns: new[] { "BotId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmotionEvents_SourceReflectionId_SourceEventKey",
                table: "EmotionEvents",
                columns: new[] { "SourceReflectionId", "SourceEventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceSuppressions_BotId_SubjectId_MessageId",
                table: "EvidenceSuppressions",
                columns: new[] { "BotId", "SubjectId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceSuppressions_OperationId_MemoryId_MessageId",
                table: "EvidenceSuppressions",
                columns: new[] { "OperationId", "MemoryId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForceReceipts_BotId_PlatformEventKey",
                table: "ForceReceipts",
                columns: new[] { "BotId", "PlatformEventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForceReceipts_ChatJobId",
                table: "ForceReceipts",
                column: "ChatJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForceReceipts_MuteSessionId",
                table: "ForceReceipts",
                column: "MuteSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupPolicies_BotId_GroupId",
                table: "GroupPolicies",
                columns: new[] { "BotId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Identities_BotId_Revision",
                table: "Identities",
                columns: new[] { "BotId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Media_BotId",
                table: "Media",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_Media_ExpiresAt",
                table: "Media",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Memories_BotId_SubjectId_Status",
                table: "Memories",
                columns: new[] { "BotId", "SubjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Memories_SubjectId_FactKey",
                table: "Memories",
                columns: new[] { "SubjectId", "FactKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Memories_SupersedesMemoryId",
                table: "Memories",
                column: "SupersedesMemoryId");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryCandidates_BotId",
                table: "MemoryCandidates",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryCandidates_SourceReflectionId_CandidateHash",
                table: "MemoryCandidates",
                columns: new[] { "SourceReflectionId", "CandidateHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryCandidates_SubjectId_Status",
                table: "MemoryCandidates",
                columns: new[] { "SubjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryOperationItems_OperationId_Kind_ResourceId",
                table: "MemoryOperationItems",
                columns: new[] { "OperationId", "Kind", "ResourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryOperations_ActorId_IdempotencyKey",
                table: "MemoryOperations",
                columns: new[] { "ActorId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryOperations_BotId",
                table: "MemoryOperations",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryOperations_Stage_CreatedAt",
                table: "MemoryOperations",
                columns: new[] { "Stage", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_BotId_Platform_ExternalMessageId",
                table: "Messages",
                columns: new[] { "BotId", "Platform", "ExternalMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_Sequence",
                table: "Messages",
                columns: new[] { "ConversationId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ExpiresAt",
                table: "Messages",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_SessionId",
                table: "Messages",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Models_BotId_TaskKind",
                table: "Models",
                columns: new[] { "BotId", "TaskKind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Mutes_ConversationId_ExpiresAt",
                table: "Mutes",
                columns: new[] { "ConversationId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NoteDependencies_TargetKind_TargetId",
                table: "NoteDependencies",
                columns: new[] { "TargetKind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_Notes_BotId_UserId_ConversationId",
                table: "Notes",
                columns: new[] { "BotId", "UserId", "ConversationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Observations_BotId",
                table: "Observations",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_Observations_ReflectionId_Candidate",
                table: "Observations",
                columns: new[] { "ReflectionId", "Candidate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_BotId_Type_ScheduledDate",
                table: "Outbox",
                columns: new[] { "BotId", "Type", "ScheduledDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_ConversationId_Sequence",
                table: "Outbox",
                columns: new[] { "ConversationId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Status_NextAttemptAt_LeaseUntil",
                table: "Outbox",
                columns: new[] { "Status", "NextAttemptAt", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_Parts_BatchId_Sequence",
                table: "Parts",
                columns: new[] { "BatchId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformIdentities_Platform_ExternalId",
                table: "PlatformIdentities",
                columns: new[] { "Platform", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformIdentities_UserId",
                table: "PlatformIdentities",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReflectionGenerations_BotId_SessionId_SubjectId_SourceSigna~",
                table: "ReflectionGenerations",
                columns: new[] { "BotId", "SessionId", "SubjectId", "SourceSignature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReflectionGenerations_JobId",
                table: "ReflectionGenerations",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reflections_BotId",
                table: "Reflections",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_Reflections_SessionId_SubjectId_Kind_Boundary",
                table: "Reflections",
                columns: new[] { "SessionId", "SubjectId", "Kind", "Boundary" },
                unique: true,
                filter: "\"Kind\" = 'light'");

            migrationBuilder.CreateIndex(
                name: "IX_RelationshipClaims_BotId_SubjectId",
                table: "RelationshipClaims",
                columns: new[] { "BotId", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_RelationshipClaims_RelationshipId",
                table: "RelationshipClaims",
                column: "RelationshipId");

            migrationBuilder.CreateIndex(
                name: "IX_Relationships_BotId_UserId",
                table: "Relationships",
                columns: new[] { "BotId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Revocations_BotId_UserId_Epoch",
                table: "Revocations",
                columns: new[] { "BotId", "UserId", "Epoch" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_BotId",
                table: "Sessions",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ConversationId",
                table: "Sessions",
                column: "ConversationId",
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_Status_DeadlineAt",
                table: "Sessions",
                columns: new[] { "Status", "DeadlineAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Summaries_BotId",
                table: "Summaries",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_Summaries_ProjectionExpiresAt",
                table: "Summaries",
                column: "ProjectionExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Summaries_SessionId",
                table: "Summaries",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Usage_BotId_CreatedAt",
                table: "Usage",
                columns: new[] { "BotId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "Users",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_LogicalUserId",
                table: "Users",
                column: "LogicalUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "Users",
                column: "NormalizedUserName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentOperations");

            migrationBuilder.DropTable(
                name: "AgentReflectionQuotas");

            migrationBuilder.DropTable(
                name: "AgentReflectionRequests");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "BindingChallenges");

            migrationBuilder.DropTable(
                name: "Bindings");

            migrationBuilder.DropTable(
                name: "ChatDispositions");

            migrationBuilder.DropTable(
                name: "Embeddings");

            migrationBuilder.DropTable(
                name: "EmotionEvents");

            migrationBuilder.DropTable(
                name: "Emotions");

            migrationBuilder.DropTable(
                name: "EvidenceSuppressions");

            migrationBuilder.DropTable(
                name: "ForceReceipts");

            migrationBuilder.DropTable(
                name: "GroupPolicies");

            migrationBuilder.DropTable(
                name: "Identities");

            migrationBuilder.DropTable(
                name: "Inbox");

            migrationBuilder.DropTable(
                name: "Media");

            migrationBuilder.DropTable(
                name: "MemoryCandidates");

            migrationBuilder.DropTable(
                name: "MemoryOperationItems");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "Models");

            migrationBuilder.DropTable(
                name: "NoteDependencies");

            migrationBuilder.DropTable(
                name: "NoteRevisions");

            migrationBuilder.DropTable(
                name: "Notes");

            migrationBuilder.DropTable(
                name: "Observations");

            migrationBuilder.DropTable(
                name: "Outbox");

            migrationBuilder.DropTable(
                name: "Parts");

            migrationBuilder.DropTable(
                name: "PlatformIdentities");

            migrationBuilder.DropTable(
                name: "ReflectionGenerations");

            migrationBuilder.DropTable(
                name: "Reflections");

            migrationBuilder.DropTable(
                name: "RelationshipClaims");

            migrationBuilder.DropTable(
                name: "Revocations");

            migrationBuilder.DropTable(
                name: "Summaries");

            migrationBuilder.DropTable(
                name: "Usage");

            migrationBuilder.DropTable(
                name: "EmbeddingProfiles");

            migrationBuilder.DropTable(
                name: "Memories");

            migrationBuilder.DropTable(
                name: "Mutes");

            migrationBuilder.DropTable(
                name: "MemoryOperations");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Batches");

            migrationBuilder.DropTable(
                name: "Relationships");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "Bots");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "LogicalUsers");
        }
    }
}
