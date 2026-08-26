using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "age_groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    min_age = table.Column<int>(type: "integer", nullable: false),
                    max_age = table.Column<int>(type: "integer", nullable: true),
                    is_minor = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_age_groups", x => x.Id);
                    table.CheckConstraint("ck_age_groups_range", "max_age IS NULL OR max_age >= min_age");
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CountryId = table.Column<int>(type: "integer", nullable: true),
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
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "attendance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<long>(type: "bigint", nullable: false),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    marked_by = table.Column<long>(type: "bigint", nullable: false),
                    marked_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    performed_by = table.Column<long>(type: "bigint", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    before_json = table.Column<string>(type: "jsonb", nullable: true),
                    after_json = table.Column<string>(type: "jsonb", nullable: true),
                    occurred_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "certificates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    level_id = table.Column<int>(type: "integer", nullable: false),
                    course_id = table.Column<long>(type: "bigint", nullable: false),
                    certificate_no = table.Column<string>(type: "text", nullable: false),
                    minutes_completed = table.Column<int>(type: "integer", nullable: false),
                    issued_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    issued_by = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Issued"),
                    file_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certificates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "class_sessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    country_id = table.Column<int>(type: "integer", nullable: false),
                    recurring_id = table.Column<long>(type: "bigint", nullable: true),
                    course_id = table.Column<long>(type: "bigint", nullable: false),
                    level_id = table.Column<int>(type: "integer", nullable: false),
                    age_group_id = table.Column<int>(type: "integer", nullable: false),
                    teacher_id = table.Column<long>(type: "bigint", nullable: false),
                    starts_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ends_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    schedule_tz = table.Column<string>(type: "text", nullable: false),
                    local_start_text = table.Column<string>(type: "text", nullable: false),
                    session_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false, defaultValue: 4),
                    seats_taken = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Scheduled"),
                    cancel_reason = table.Column<string>(type: "text", nullable: true),
                    cancelled_by = table.Column<long>(type: "bigint", nullable: true),
                    replaced_by_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_class_sessions", x => x.Id);
                    table.CheckConstraint("ck_session_capacity_band", "capacity BETWEEN 1 AND 10");
                    table.CheckConstraint("ck_session_duration_positive", "duration_minutes > 0");
                    table.CheckConstraint("ck_session_end_after_start", "ends_at_utc > starts_at_utc");
                    table.CheckConstraint("ck_session_seats", "seats_taken <= capacity");
                });

            migrationBuilder.CreateTable(
                name: "countries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    name_ar = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    phone_country_code = table.Column<string>(type: "text", nullable: false),
                    default_timezone = table.Column<string>(type: "text", nullable: false),
                    payment_provider_key = table.Column<string>(type: "text", nullable: false, defaultValue: "manual"),
                    is_default_intl = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_countries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "courses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "text", nullable: false),
                    name_ar = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    is_leveled = table.Column<bool>(type: "boolean", nullable: false),
                    grants_certificate = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_courses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "entitlement_ledger",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    subscription_id = table.Column<long>(type: "bigint", nullable: true),
                    course_id = table.Column<long>(type: "bigint", nullable: false),
                    level_id = table.Column<int>(type: "integer", nullable: false),
                    delta_minutes = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    session_id = table.Column<long>(type: "bigint", nullable: true),
                    payment_id = table.Column<long>(type: "bigint", nullable: true),
                    migration_id = table.Column<long>(type: "bigint", nullable: true),
                    reverses_id = table.Column<long>(type: "bigint", nullable: true),
                    performed_by = table.Column<long>(type: "bigint", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    expires_on = table.Column<LocalDate>(type: "date", nullable: true),
                    created_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entitlement_ledger", x => x.Id);
                    table.CheckConstraint("ck_ledger_delta_nonzero", "delta_minutes <> 0");
                });

            migrationBuilder.CreateTable(
                name: "files",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    object_key = table.Column<Guid>(type: "uuid", nullable: false),
                    original_file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    purpose = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    owner_student_id = table.Column<long>(type: "bigint", nullable: true),
                    uploaded_by = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_files", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "homework",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<long>(type: "bigint", nullable: false),
                    teacher_id = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    instructions = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    due_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_homework", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "homework_submissions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    homework_id = table.Column<long>(type: "bigint", nullable: false),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    file_id = table.Column<long>(type: "bigint", nullable: false),
                    submitted_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    submitted_by = table.Column<long>(type: "bigint", nullable: false),
                    grade = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    feedback = table.Column<string>(type: "text", nullable: true),
                    graded_by = table.Column<long>(type: "bigint", nullable: true),
                    graded_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_homework_submissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "level_progress",
                columns: table => new
                {
                    StudentId = table.Column<long>(type: "bigint", nullable: false),
                    LevelId = table.Column<int>(type: "integer", nullable: false),
                    CourseId = table.Column<long>(type: "bigint", nullable: false),
                    minutes_completed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    completed_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_level_progress", x => new { x.StudentId, x.LevelId, x.CourseId });
                });

            migrationBuilder.CreateTable(
                name: "levels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name_ar = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_levels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "migration_batches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_file_name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    total_rows = table.Column<int>(type: "integer", nullable: false),
                    valid_rows = table.Column<int>(type: "integer", nullable: false),
                    error_rows = table.Column<int>(type: "integer", nullable: false),
                    imported_rows = table.Column<int>(type: "integer", nullable: false),
                    uploaded_by = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    imported_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    rolled_back_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_migration_batches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "migration_records",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    source_reference = table.Column<string>(type: "text", nullable: true),
                    student_id = table.Column<long>(type: "bigint", nullable: true),
                    guardian_id = table.Column<long>(type: "bigint", nullable: true),
                    raw_payload = table.Column<string>(type: "jsonb", nullable: false),
                    level_code = table.Column<string>(type: "text", nullable: true),
                    remaining_minutes = table.Column<int>(type: "integer", nullable: true),
                    amount_paid = table.Column<decimal>(type: "numeric(12,3)", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: true),
                    subscription_start = table.Column<DateOnly>(type: "date", nullable: true),
                    subscription_end = table.Column<DateOnly>(type: "date", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    imported_by = table.Column<long>(type: "bigint", nullable: true),
                    imported_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_migration_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notification_outbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    @event = table.Column<string>(name: "event", type: "character varying(50)", maxLength: 50, nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recipient_user_id = table.Column<long>(type: "bigint", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Pending"),
                    scheduled_for_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    sent_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    subscription_id = table.Column<long>(type: "bigint", nullable: true),
                    payer_user_id = table.Column<long>(type: "bigint", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false, defaultValue: "manual"),
                    provider_txn_id = table.Column<string>(type: "text", nullable: true),
                    reference_code = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Pending"),
                    proof_file_id = table.Column<long>(type: "bigint", nullable: true),
                    confirmed_by = table.Column<long>(type: "bigint", nullable: true),
                    confirmed_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "text", nullable: true),
                    created_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payments", x => x.Id);
                    table.CheckConstraint("ck_payment_amount_positive", "amount > 0");
                });

            migrationBuilder.CreateTable(
                name: "payroll_periods",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    country_id = table.Column<int>(type: "integer", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Open"),
                    approved_by = table.Column<long>(type: "bigint", nullable: true),
                    approved_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_periods", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "placement_interviews",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    interviewer_teacher_id = table.Column<long>(type: "bigint", nullable: true),
                    scheduled_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    assigned_level_id = table.Column<int>(type: "integer", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_placement_interviews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pricing_plans",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    country_id = table.Column<int>(type: "integer", nullable: false),
                    course_id = table.Column<long>(type: "bigint", nullable: false),
                    level_id = table.Column<int>(type: "integer", nullable: true),
                    age_group_id = table.Column<int>(type: "integer", nullable: true),
                    session_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sessions_count = table.Column<int>(type: "integer", nullable: false),
                    minutes_total = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    validity_days = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    created_by = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pricing_plans", x => x.Id);
                    table.CheckConstraint("ck_pricing_plans_amount", "amount >= 0");
                });

            migrationBuilder.CreateTable(
                name: "recurring_schedules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    country_id = table.Column<int>(type: "integer", nullable: false),
                    course_id = table.Column<long>(type: "bigint", nullable: false),
                    level_id = table.Column<int>(type: "integer", nullable: false),
                    age_group_id = table.Column<int>(type: "integer", nullable: false),
                    teacher_id = table.Column<long>(type: "bigint", nullable: false),
                    days_of_week = table.Column<short[]>(type: "smallint[]", nullable: false),
                    start_local = table.Column<LocalTime>(type: "time", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    timezone_id = table.Column<string>(type: "text", nullable: false),
                    starts_on = table.Column<LocalDate>(type: "date", nullable: false),
                    ends_on = table.Column<LocalDate>(type: "date", nullable: true),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_schedules", x => x.Id);
                    table.CheckConstraint("ck_recurring_days_len", "array_length(days_of_week, 1) BETWEEN 1 AND 7");
                });

            migrationBuilder.CreateTable(
                name: "refund_requests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    payment_id = table.Column<long>(type: "bigint", nullable: false),
                    requested_by = table.Column<long>(type: "bigint", nullable: false),
                    requested_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "Rejected-Policy"),
                    resolved_by = table.Column<long>(type: "bigint", nullable: true),
                    resolved_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refund_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "session_delivery",
                columns: table => new
                {
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    teacher_id = table.Column<long>(type: "bigint", nullable: false),
                    declared_by = table.Column<long>(type: "bigint", nullable: true),
                    declared_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    declared_minutes = table.Column<int>(type: "integer", nullable: true),
                    teacher_note = table.Column<string>(type: "text", nullable: true),
                    verified_by = table.Column<long>(type: "bigint", nullable: true),
                    verified_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    verified_minutes = table.Column<int>(type: "integer", nullable: true),
                    admin_note = table.Column<string>(type: "text", nullable: true),
                    rate_amount = table.Column<decimal>(type: "numeric(12,3)", nullable: true),
                    rate_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    rate_source_id = table.Column<long>(type: "bigint", nullable: true),
                    payable_amount = table.Column<decimal>(type: "numeric(12,3)", nullable: true),
                    payroll_period_id = table.Column<long>(type: "bigint", nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Pending")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_delivery", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "session_enrollments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<long>(type: "bigint", nullable: false),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    age_group_at_enrollment = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    enrolled_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    enrolled_by = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_enrollments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    last_updated_by = table.Column<long>(type: "bigint", nullable: true),
                    last_updated_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "student_levels",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    level_id = table.Column<int>(type: "integer", nullable: false),
                    assigned_by = table.Column<long>(type: "bigint", nullable: false),
                    assigned_by_role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    placement_interview_id = table.Column<long>(type: "bigint", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    effective_from_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_levels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "subscription_freezes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    subscription_id = table.Column<long>(type: "bigint", nullable: false),
                    starts_on = table.Column<LocalDate>(type: "date", nullable: false),
                    ends_on = table.Column<LocalDate>(type: "date", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    approved_by = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscription_freezes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    country_id = table.Column<int>(type: "integer", nullable: false),
                    course_id = table.Column<long>(type: "bigint", nullable: false),
                    level_id = table.Column<int>(type: "integer", nullable: false),
                    price_amount = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    price_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    price_plan_id = table.Column<long>(type: "bigint", nullable: true),
                    sessions_count = table.Column<int>(type: "integer", nullable: false),
                    minutes_total = table.Column<int>(type: "integer", nullable: false),
                    starts_on = table.Column<LocalDate>(type: "date", nullable: false),
                    expires_on = table.Column<LocalDate>(type: "date", nullable: false),
                    validity_days = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Draft"),
                    origin = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by = table.Column<long>(type: "bigint", nullable: false),
                    created_reason = table.Column<string>(type: "text", nullable: true),
                    extended_by = table.Column<long>(type: "bigint", nullable: true),
                    extended_reason = table.Column<string>(type: "text", nullable: true),
                    extended_to = table.Column<LocalDate>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscriptions", x => x.Id);
                    table.CheckConstraint("ck_subscription_dates", "expires_on > starts_on");
                });

            migrationBuilder.CreateTable(
                name: "teacher_availability_rules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    teacher_id = table.Column<long>(type: "bigint", nullable: false),
                    day_of_week = table.Column<short>(type: "smallint", nullable: false),
                    start_local = table.Column<LocalTime>(type: "time", nullable: false),
                    end_local = table.Column<LocalTime>(type: "time", nullable: false),
                    timezone_id = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<LocalDate>(type: "date", nullable: false),
                    valid_to = table.Column<LocalDate>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teacher_availability_rules", x => x.Id);
                    table.CheckConstraint("ck_avail_end_after_start", "end_local > start_local");
                });

            migrationBuilder.CreateTable(
                name: "teacher_rates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    teacher_id = table.Column<long>(type: "bigint", nullable: false),
                    course_id = table.Column<long>(type: "bigint", nullable: true),
                    level_id = table.Column<int>(type: "integer", nullable: true),
                    age_group_id = table.Column<int>(type: "integer", nullable: true),
                    rate_amount = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    rate_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    rate_unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    created_by = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teacher_rates", x => x.Id);
                    table.CheckConstraint("ck_rate_effective_range", "effective_to IS NULL OR effective_to > effective_from");
                });

            migrationBuilder.CreateTable(
                name: "teacher_time_off",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    teacher_id = table.Column<long>(type: "bigint", nullable: false),
                    starts_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ends_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teacher_time_off", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<long>(type: "bigint", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
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
                    UserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    RoleId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guardians",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guardians", x => x.Id);
                    table.ForeignKey(
                        name: "FK_guardians_AspNetUsers_user_id",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "students",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    country_id = table.Column<int>(type: "integer", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: false),
                    date_of_birth = table.Column<LocalDate>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_students", x => x.Id);
                    table.ForeignKey(
                        name: "FK_students_AspNetUsers_user_id",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "teachers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: false),
                    timezone_id = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teachers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_teachers_AspNetUsers_user_id",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payroll_lines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    period_id = table.Column<long>(type: "bigint", nullable: false),
                    teacher_id = table.Column<long>(type: "bigint", nullable: false),
                    session_id = table.Column<long>(type: "bigint", nullable: false),
                    minutes = table.Column<int>(type: "integer", nullable: false),
                    rate_amount = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    rate_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payroll_lines_payroll_periods_period_id",
                        column: x => x.period_id,
                        principalTable: "payroll_periods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guardianships",
                columns: table => new
                {
                    GuardianId = table.Column<long>(type: "bigint", nullable: false),
                    StudentId = table.Column<long>(type: "bigint", nullable: false),
                    relationship = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    can_pay = table.Column<bool>(type: "boolean", nullable: false),
                    linked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    linked_by = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guardianships", x => new { x.GuardianId, x.StudentId });
                    table.ForeignKey(
                        name: "FK_guardianships_guardians_GuardianId",
                        column: x => x.GuardianId,
                        principalTable: "guardians",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_guardianships_students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_age_groups_code",
                table: "age_groups",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
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
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_attendance_session_student",
                table: "attendance",
                columns: new[] { "session_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_entity_type_entity_id_occurred_at_utc",
                table: "audit_logs",
                columns: new[] { "entity_type", "entity_id", "occurred_at_utc" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_certificates_certificate_no",
                table: "certificates",
                column: "certificate_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_certificates_student_id_level_id_course_id",
                table: "certificates",
                columns: new[] { "student_id", "level_id", "course_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_class_sessions_starts_at_utc_ends_at_utc",
                table: "class_sessions",
                columns: new[] { "starts_at_utc", "ends_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_class_sessions_teacher_id",
                table: "class_sessions",
                column: "teacher_id");

            migrationBuilder.CreateIndex(
                name: "IX_countries_code",
                table: "countries",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_courses_code",
                table: "courses",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ent_student",
                table: "entitlement_ledger",
                columns: new[] { "student_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_ent_sub",
                table: "entitlement_ledger",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ux_ent_consumption",
                table: "entitlement_ledger",
                columns: new[] { "session_id", "student_id" },
                unique: true,
                filter: "\"reason\" = 'Consumption'");

            migrationBuilder.CreateIndex(
                name: "IX_files_object_key",
                table: "files",
                column: "object_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guardians_user_id",
                table: "guardians",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_guardianship_primary",
                table: "guardianships",
                column: "StudentId",
                unique: true,
                filter: "\"is_primary\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_homework_submissions_homework_id_student_id",
                table: "homework_submissions",
                columns: new[] { "homework_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_levels_code",
                table: "levels",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_levels_sort_order",
                table: "levels",
                column: "sort_order",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_migration_batches_batch_id",
                table: "migration_batches",
                column: "batch_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_migration_records_batch_id",
                table: "migration_records",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "IX_notification_outbox_status_scheduled_for_utc",
                table: "notification_outbox",
                columns: new[] { "status", "scheduled_for_utc" },
                filter: "\"status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_payments_provider_key_provider_txn_id",
                table: "payments",
                columns: new[] { "provider_key", "provider_txn_id" },
                unique: true,
                filter: "\"provider_txn_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_payments_reference_code",
                table: "payments",
                column: "reference_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_lines_period_id_session_id",
                table: "payroll_lines",
                columns: new[] { "period_id", "session_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_periods_country_id_period_start_period_end",
                table: "payroll_periods",
                columns: new[] { "country_id", "period_start", "period_end" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plans_lookup",
                table: "pricing_plans",
                columns: new[] { "country_id", "course_id", "level_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_session_delivery_teacher_id_verified_at_utc",
                table: "session_delivery",
                columns: new[] { "teacher_id", "verified_at_utc" },
                filter: "\"state\" = 'Verified'");

            migrationBuilder.CreateIndex(
                name: "ux_enrollment_active",
                table: "session_enrollments",
                columns: new[] { "session_id", "student_id" },
                unique: true,
                filter: "\"state\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ux_student_current_level",
                table: "student_levels",
                column: "student_id",
                unique: true,
                filter: "\"is_current\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_students_user_id",
                table: "students",
                column: "user_id",
                unique: true,
                filter: "\"user_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_subscription_freezes_subscription_id",
                table: "subscription_freezes",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_expires_on",
                table: "subscriptions",
                column: "expires_on");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_student_id",
                table: "subscriptions",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_rates_lookup",
                table: "teacher_rates",
                columns: new[] { "teacher_id", "effective_from" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_teachers_user_id",
                table: "teachers",
                column: "user_id",
                unique: true);

            // ⭐⭐ Technical Study §14.2 — makes a teacher schedule conflict a
            // physical impossibility, not an application-level check. EF Core's
            // fluent API has no first-class support for PostgreSQL EXCLUDE
            // constraints, so this is raw SQL (master engineering prompt §19:
            // "database-level protection for teacher schedule conflicts").
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql(@"
                ALTER TABLE class_sessions ADD CONSTRAINT no_teacher_overlap
                  EXCLUDE USING gist (
                      teacher_id WITH =,
                      tstzrange(starts_at_utc, ends_at_utc, '[)') WITH &&
                  ) WHERE (status <> 'Cancelled');");

            // §20.5 rule 1: append-only at the database level, not merely by
            // convention in application code.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION forbid_ledger_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'entitlement_ledger is append-only (Technical Study §20.5 rule 1) — % is not permitted', TG_OP;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_entitlement_ledger_append_only
                    BEFORE UPDATE OR DELETE ON entitlement_ledger
                    FOR EACH ROW EXECUTE FUNCTION forbid_ledger_mutation();
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_entitlement_ledger_append_only ON entitlement_ledger;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS forbid_ledger_mutation();");
            migrationBuilder.Sql("ALTER TABLE class_sessions DROP CONSTRAINT IF EXISTS no_teacher_overlap;");

            migrationBuilder.DropTable(
                name: "age_groups");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "attendance");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "certificates");

            migrationBuilder.DropTable(
                name: "class_sessions");

            migrationBuilder.DropTable(
                name: "countries");

            migrationBuilder.DropTable(
                name: "courses");

            migrationBuilder.DropTable(
                name: "entitlement_ledger");

            migrationBuilder.DropTable(
                name: "files");

            migrationBuilder.DropTable(
                name: "guardianships");

            migrationBuilder.DropTable(
                name: "homework");

            migrationBuilder.DropTable(
                name: "homework_submissions");

            migrationBuilder.DropTable(
                name: "level_progress");

            migrationBuilder.DropTable(
                name: "levels");

            migrationBuilder.DropTable(
                name: "migration_batches");

            migrationBuilder.DropTable(
                name: "migration_records");

            migrationBuilder.DropTable(
                name: "notification_outbox");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropTable(
                name: "payroll_lines");

            migrationBuilder.DropTable(
                name: "placement_interviews");

            migrationBuilder.DropTable(
                name: "pricing_plans");

            migrationBuilder.DropTable(
                name: "recurring_schedules");

            migrationBuilder.DropTable(
                name: "refund_requests");

            migrationBuilder.DropTable(
                name: "session_delivery");

            migrationBuilder.DropTable(
                name: "session_enrollments");

            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.DropTable(
                name: "student_levels");

            migrationBuilder.DropTable(
                name: "subscription_freezes");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropTable(
                name: "teacher_availability_rules");

            migrationBuilder.DropTable(
                name: "teacher_rates");

            migrationBuilder.DropTable(
                name: "teacher_time_off");

            migrationBuilder.DropTable(
                name: "teachers");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "guardians");

            migrationBuilder.DropTable(
                name: "students");

            migrationBuilder.DropTable(
                name: "payroll_periods");

            migrationBuilder.DropTable(
                name: "AspNetUsers");
        }
    }
}
