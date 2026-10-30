using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Goldpath;

// The Quartz persistent-store schema expressed as EF model contributions (jobs RFC D2):
// the store is created and evolved by the SAME migration pipeline as every other table —
// never a side-channel SQL script. Quartz accesses these tables through its own ADO
// delegates; EF only owns the DDL. Column names are lowercase ON PURPOSE: Quartz emits
// unquoted identifiers, and PostgreSQL folds those to lowercase (SQL Server is
// case-insensitive, so one casing serves both providers). Oracle folds them to UPPER case
// and EF quotes what it creates, so on Oracle the store takes Quartz's own Oracle shape
// (ShapeForOracle, G7 O-3 #406). Lengths follow the standard
// Quartz DDL — key columns MUST be bounded (SQL Server cannot key nvarchar(max)).

internal sealed class QrtzJobDetail
{
    public string SchedName { get; set; } = "";
    public string JobName { get; set; } = "";
    public string JobGroup { get; set; } = "";
    public string? Description { get; set; }
    public string JobClassName { get; set; } = "";
    public bool IsDurable { get; set; }
    public bool IsNonconcurrent { get; set; }
    public bool IsUpdateData { get; set; }
    public bool RequestsRecovery { get; set; }
    public byte[]? JobData { get; set; }
}

internal sealed class QrtzTrigger
{
    public string SchedName { get; set; } = "";
    public string TriggerName { get; set; } = "";
    public string TriggerGroup { get; set; } = "";
    public string JobName { get; set; } = "";
    public string JobGroup { get; set; } = "";
    public string? Description { get; set; }
    public long? NextFireTime { get; set; }
    public long? PrevFireTime { get; set; }
    public int? Priority { get; set; }
    public string TriggerState { get; set; } = "";
    public string TriggerType { get; set; } = "";
    public long StartTime { get; set; }
    public long? EndTime { get; set; }
    public string? CalendarName { get; set; }
    public short? MisfireInstr { get; set; }
    public byte[]? JobData { get; set; }
}

internal sealed class QrtzSimpleTrigger
{
    public string SchedName { get; set; } = "";
    public string TriggerName { get; set; } = "";
    public string TriggerGroup { get; set; } = "";
    public long RepeatCount { get; set; }
    public long RepeatInterval { get; set; }
    public long TimesTriggered { get; set; }
}

internal sealed class QrtzCronTrigger
{
    public string SchedName { get; set; } = "";
    public string TriggerName { get; set; } = "";
    public string TriggerGroup { get; set; } = "";
    public string CronExpression { get; set; } = "";
    public string? TimeZoneId { get; set; }
}

internal sealed class QrtzSimpropTrigger
{
    public string SchedName { get; set; } = "";
    public string TriggerName { get; set; } = "";
    public string TriggerGroup { get; set; } = "";
    public string? StrProp1 { get; set; }
    public string? StrProp2 { get; set; }
    public string? StrProp3 { get; set; }
    public int? IntProp1 { get; set; }
    public int? IntProp2 { get; set; }
    public long? LongProp1 { get; set; }
    public long? LongProp2 { get; set; }
    public decimal? DecProp1 { get; set; }
    public decimal? DecProp2 { get; set; }
    public bool? BoolProp1 { get; set; }
    public bool? BoolProp2 { get; set; }
    public string? TimeZoneId { get; set; }
}

internal sealed class QrtzBlobTrigger
{
    public string SchedName { get; set; } = "";
    public string TriggerName { get; set; } = "";
    public string TriggerGroup { get; set; } = "";
    public byte[]? BlobData { get; set; }
}

internal sealed class QrtzCalendar
{
    public string SchedName { get; set; } = "";
    public string CalendarName { get; set; } = "";
    public byte[] Calendar { get; set; } = [];
}

internal sealed class QrtzPausedTriggerGrp
{
    public string SchedName { get; set; } = "";
    public string TriggerGroup { get; set; } = "";
}

internal sealed class QrtzFiredTrigger
{
    public string SchedName { get; set; } = "";
    public string EntryId { get; set; } = "";
    public string TriggerName { get; set; } = "";
    public string TriggerGroup { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public long FiredTime { get; set; }
    public long SchedTime { get; set; }
    public int Priority { get; set; }
    public string State { get; set; } = "";
    public string? JobName { get; set; }
    public string? JobGroup { get; set; }
    public bool IsNonconcurrent { get; set; }
    public bool? RequestsRecovery { get; set; }
}

internal sealed class QrtzSchedulerState
{
    public string SchedName { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public long LastCheckinTime { get; set; }
    public long CheckinInterval { get; set; }
}

internal sealed class QrtzLock
{
    public string SchedName { get; set; } = "";
    public string LockName { get; set; } = "";
}

internal static class QuartzStoreModel
{
    private const string Prefix = "qrtz_";

    internal static ModelBuilder AddQuartzStoreTables(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QrtzJobDetail>(e =>
        {
            e.ToTable(Prefix + "job_details");
            e.HasKey(x => new { x.SchedName, x.JobName, x.JobGroup });
            e.Property(x => x.SchedName).HasColumnName("sched_name").HasMaxLength(120);
            e.Property(x => x.JobName).HasColumnName("job_name").HasMaxLength(150);
            e.Property(x => x.JobGroup).HasColumnName("job_group").HasMaxLength(150);
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(250);
            e.Property(x => x.JobClassName).HasColumnName("job_class_name").HasMaxLength(250);
            e.Property(x => x.IsDurable).HasColumnName("is_durable");
            e.Property(x => x.IsNonconcurrent).HasColumnName("is_nonconcurrent");
            e.Property(x => x.IsUpdateData).HasColumnName("is_update_data");
            e.Property(x => x.RequestsRecovery).HasColumnName("requests_recovery");
            e.Property(x => x.JobData).HasColumnName("job_data");
            e.HasIndex(x => x.RequestsRecovery).HasDatabaseName("idx_qrtz_j_req_recovery");
        });

        modelBuilder.Entity<QrtzTrigger>(e =>
        {
            e.ToTable(Prefix + "triggers");
            e.HasKey(x => new { x.SchedName, x.TriggerName, x.TriggerGroup });
            e.Property(x => x.SchedName).HasColumnName("sched_name").HasMaxLength(120);
            e.Property(x => x.TriggerName).HasColumnName("trigger_name").HasMaxLength(150);
            e.Property(x => x.TriggerGroup).HasColumnName("trigger_group").HasMaxLength(150);
            e.Property(x => x.JobName).HasColumnName("job_name").HasMaxLength(150);
            e.Property(x => x.JobGroup).HasColumnName("job_group").HasMaxLength(150);
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(250);
            e.Property(x => x.NextFireTime).HasColumnName("next_fire_time");
            e.Property(x => x.PrevFireTime).HasColumnName("prev_fire_time");
            e.Property(x => x.Priority).HasColumnName("priority");
            e.Property(x => x.TriggerState).HasColumnName("trigger_state").HasMaxLength(16);
            e.Property(x => x.TriggerType).HasColumnName("trigger_type").HasMaxLength(8);
            e.Property(x => x.StartTime).HasColumnName("start_time");
            e.Property(x => x.EndTime).HasColumnName("end_time");
            e.Property(x => x.CalendarName).HasColumnName("calendar_name").HasMaxLength(200);
            e.Property(x => x.MisfireInstr).HasColumnName("misfire_instr");
            e.Property(x => x.JobData).HasColumnName("job_data");
            e.HasOne<QrtzJobDetail>().WithMany()
                .HasForeignKey(x => new { x.SchedName, x.JobName, x.JobGroup })
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.NextFireTime).HasDatabaseName("idx_qrtz_t_next_fire_time");
            e.HasIndex(x => x.TriggerState).HasDatabaseName("idx_qrtz_t_state");
            e.HasIndex(x => new { x.NextFireTime, x.TriggerState }).HasDatabaseName("idx_qrtz_t_nft_st");
        });

        modelBuilder.Entity<QrtzSimpleTrigger>(e =>
        {
            e.ToTable(Prefix + "simple_triggers");
            e.HasKey(x => new { x.SchedName, x.TriggerName, x.TriggerGroup });
            e.Property(x => x.SchedName).HasColumnName("sched_name").HasMaxLength(120);
            e.Property(x => x.TriggerName).HasColumnName("trigger_name").HasMaxLength(150);
            e.Property(x => x.TriggerGroup).HasColumnName("trigger_group").HasMaxLength(150);
            e.Property(x => x.RepeatCount).HasColumnName("repeat_count");
            e.Property(x => x.RepeatInterval).HasColumnName("repeat_interval");
            e.Property(x => x.TimesTriggered).HasColumnName("times_triggered");
            e.HasOne<QrtzTrigger>().WithMany()
                .HasForeignKey(x => new { x.SchedName, x.TriggerName, x.TriggerGroup })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QrtzCronTrigger>(e =>
        {
            e.ToTable(Prefix + "cron_triggers");
            e.HasKey(x => new { x.SchedName, x.TriggerName, x.TriggerGroup });
            e.Property(x => x.SchedName).HasColumnName("sched_name").HasMaxLength(120);
            e.Property(x => x.TriggerName).HasColumnName("trigger_name").HasMaxLength(150);
            e.Property(x => x.TriggerGroup).HasColumnName("trigger_group").HasMaxLength(150);
            e.Property(x => x.CronExpression).HasColumnName("cron_expression").HasMaxLength(120);
            e.Property(x => x.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(80);
            e.HasOne<QrtzTrigger>().WithMany()
                .HasForeignKey(x => new { x.SchedName, x.TriggerName, x.TriggerGroup })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QrtzSimpropTrigger>(e =>
        {
            e.ToTable(Prefix + "simprop_triggers");
            e.HasKey(x => new { x.SchedName, x.TriggerName, x.TriggerGroup });
            e.Property(x => x.SchedName).HasColumnName("sched_name").HasMaxLength(120);
            e.Property(x => x.TriggerName).HasColumnName("trigger_name").HasMaxLength(150);
            e.Property(x => x.TriggerGroup).HasColumnName("trigger_group").HasMaxLength(150);
            e.Property(x => x.StrProp1).HasColumnName("str_prop_1").HasMaxLength(512);
            e.Property(x => x.StrProp2).HasColumnName("str_prop_2").HasMaxLength(512);
            e.Property(x => x.StrProp3).HasColumnName("str_prop_3").HasMaxLength(512);
            e.Property(x => x.IntProp1).HasColumnName("int_prop_1");
            e.Property(x => x.IntProp2).HasColumnName("int_prop_2");
            e.Property(x => x.LongProp1).HasColumnName("long_prop_1");
            e.Property(x => x.LongProp2).HasColumnName("long_prop_2");
            e.Property(x => x.DecProp1).HasColumnName("dec_prop_1").HasPrecision(13, 4);
            e.Property(x => x.DecProp2).HasColumnName("dec_prop_2").HasPrecision(13, 4);
            e.Property(x => x.BoolProp1).HasColumnName("bool_prop_1");
            e.Property(x => x.BoolProp2).HasColumnName("bool_prop_2");
            e.Property(x => x.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(80);
            e.HasOne<QrtzTrigger>().WithMany()
                .HasForeignKey(x => new { x.SchedName, x.TriggerName, x.TriggerGroup })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QrtzBlobTrigger>(e =>
        {
            e.ToTable(Prefix + "blob_triggers");
            e.HasKey(x => new { x.SchedName, x.TriggerName, x.TriggerGroup });
            e.Property(x => x.SchedName).HasColumnName("sched_name").HasMaxLength(120);
            e.Property(x => x.TriggerName).HasColumnName("trigger_name").HasMaxLength(150);
            e.Property(x => x.TriggerGroup).HasColumnName("trigger_group").HasMaxLength(150);
            e.Property(x => x.BlobData).HasColumnName("blob_data");
            e.HasOne<QrtzTrigger>().WithMany()
                .HasForeignKey(x => new { x.SchedName, x.TriggerName, x.TriggerGroup })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QrtzCalendar>(e =>
        {
            e.ToTable(Prefix + "calendars");
            e.HasKey(x => new { x.SchedName, x.CalendarName });
            e.Property(x => x.SchedName).HasColumnName("sched_name").HasMaxLength(120);
            e.Property(x => x.CalendarName).HasColumnName("calendar_name").HasMaxLength(200);
            e.Property(x => x.Calendar).HasColumnName("calendar");
        });

        modelBuilder.Entity<QrtzPausedTriggerGrp>(e =>
        {
            e.ToTable(Prefix + "paused_trigger_grps");
            e.HasKey(x => new { x.SchedName, x.TriggerGroup });
            e.Property(x => x.SchedName).HasColumnName("sched_name").HasMaxLength(120);
            e.Property(x => x.TriggerGroup).HasColumnName("trigger_group").HasMaxLength(150);
        });

        modelBuilder.Entity<QrtzFiredTrigger>(e =>
        {
            e.ToTable(Prefix + "fired_triggers");
            e.HasKey(x => new { x.SchedName, x.EntryId });
            e.Property(x => x.SchedName).HasColumnName("sched_name").HasMaxLength(120);
            e.Property(x => x.EntryId).HasColumnName("entry_id").HasMaxLength(140);
            e.Property(x => x.TriggerName).HasColumnName("trigger_name").HasMaxLength(150);
            e.Property(x => x.TriggerGroup).HasColumnName("trigger_group").HasMaxLength(150);
            e.Property(x => x.InstanceName).HasColumnName("instance_name").HasMaxLength(200);
            e.Property(x => x.FiredTime).HasColumnName("fired_time");
            e.Property(x => x.SchedTime).HasColumnName("sched_time");
            e.Property(x => x.Priority).HasColumnName("priority");
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(16);
            e.Property(x => x.JobName).HasColumnName("job_name").HasMaxLength(150);
            e.Property(x => x.JobGroup).HasColumnName("job_group").HasMaxLength(150);
            e.Property(x => x.IsNonconcurrent).HasColumnName("is_nonconcurrent");
            e.Property(x => x.RequestsRecovery).HasColumnName("requests_recovery");
            e.HasIndex(x => x.TriggerName).HasDatabaseName("idx_qrtz_ft_trig_name");
            e.HasIndex(x => x.TriggerGroup).HasDatabaseName("idx_qrtz_ft_trig_group");
            e.HasIndex(x => new { x.TriggerName, x.TriggerGroup }).HasDatabaseName("idx_qrtz_ft_trig_nm_gp");
            e.HasIndex(x => x.InstanceName).HasDatabaseName("idx_qrtz_ft_trig_inst_name");
            e.HasIndex(x => x.JobName).HasDatabaseName("idx_qrtz_ft_job_name");
            e.HasIndex(x => x.JobGroup).HasDatabaseName("idx_qrtz_ft_job_group");
            e.HasIndex(x => x.RequestsRecovery).HasDatabaseName("idx_qrtz_ft_job_req_recovery");
        });

        modelBuilder.Entity<QrtzSchedulerState>(e =>
        {
            e.ToTable(Prefix + "scheduler_state");
            e.HasKey(x => new { x.SchedName, x.InstanceName });
            e.Property(x => x.SchedName).HasColumnName("sched_name").HasMaxLength(120);
            e.Property(x => x.InstanceName).HasColumnName("instance_name").HasMaxLength(200);
            e.Property(x => x.LastCheckinTime).HasColumnName("last_checkin_time");
            e.Property(x => x.CheckinInterval).HasColumnName("checkin_interval");
        });

        modelBuilder.Entity<QrtzLock>(e =>
        {
            e.ToTable(Prefix + "locks");
            e.HasKey(x => new { x.SchedName, x.LockName });
            e.Property(x => x.SchedName).HasColumnName("sched_name").HasMaxLength(120);
            e.Property(x => x.LockName).HasColumnName("lock_name").HasMaxLength(40);
        });

        if (IsOracle(modelBuilder))
        {
            ShapeForOracle(modelBuilder);
        }

        return modelBuilder;
    }

    /// <summary>
    /// The Oracle EF provider marks the model it builds with its own annotations (<c>Oracle:…</c>) before
    /// <c>OnModelCreating</c> runs; nothing else does, so the PostgreSQL and SQL Server models never take this path.
    /// </summary>
    internal static bool IsOracle(ModelBuilder modelBuilder)
        => modelBuilder.Model.GetAnnotations().Any(a => a.Name.StartsWith("Oracle:", StringComparison.Ordinal));

    /// <summary>
    /// The store as Quartz's own <c>tables_oracle.sql</c> declares it (G7, O-3, #406). EF quotes every identifier it
    /// creates, so the lower-case names that serve PostgreSQL would be created lower-case on Oracle, while
    /// <c>OracleDelegate</c>'s unquoted <c>QRTZ_JOB_DETAILS</c> folds to upper case and would not find them
    /// (ORA-00942): on Oracle every name is upper case. The booleans are <c>VARCHAR2(1)</c> holding <c>'0'</c>/<c>'1'</c>,
    /// and job data, calendars and blob triggers are <c>BLOB</c>, where EF's defaults (<c>NUMBER(1)</c> or
    /// <c>BOOLEAN</c>, and <c>RAW(2000)</c>) would cap a job's data at 2,000 bytes.
    /// </summary>
    private static void ShapeForOracle(ModelBuilder modelBuilder)
    {
        var flag = new BoolToStringConverter("0", "1");
        foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(e => e.ClrType.Name.StartsWith("Qrtz", StringComparison.Ordinal)))
        {
            entity.SetTableName(entity.GetTableName()!.ToUpperInvariant());
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(property.GetColumnName().ToUpperInvariant());
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (type == typeof(bool))
                {
                    property.SetValueConverter(flag);
                    property.SetColumnType("VARCHAR2(1)");
                }
                else if (type == typeof(byte[]))
                {
                    property.SetColumnType("BLOB");
                }
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(index.GetDatabaseName()!.ToUpperInvariant());
            }
        }
    }
}
