CREATE TABLE qrtz_calendars (
    sched_name character varying(120) NOT NULL,
    calendar_name character varying(200) NOT NULL,
    calendar bytea NOT NULL,
    CONSTRAINT "PK_qrtz_calendars" PRIMARY KEY (sched_name, calendar_name)
);

CREATE TABLE qrtz_fired_triggers (
    sched_name character varying(120) NOT NULL,
    entry_id character varying(140) NOT NULL,
    trigger_name character varying(150) NOT NULL,
    trigger_group character varying(150) NOT NULL,
    instance_name character varying(200) NOT NULL,
    fired_time bigint NOT NULL,
    sched_time bigint NOT NULL,
    priority integer NOT NULL,
    state character varying(16) NOT NULL,
    job_name character varying(150),
    job_group character varying(150),
    is_nonconcurrent boolean NOT NULL,
    requests_recovery boolean,
    CONSTRAINT "PK_qrtz_fired_triggers" PRIMARY KEY (sched_name, entry_id)
);

CREATE TABLE qrtz_job_details (
    sched_name character varying(120) NOT NULL,
    job_name character varying(150) NOT NULL,
    job_group character varying(150) NOT NULL,
    description character varying(250),
    job_class_name character varying(250) NOT NULL,
    is_durable boolean NOT NULL,
    is_nonconcurrent boolean NOT NULL,
    is_update_data boolean NOT NULL,
    requests_recovery boolean NOT NULL,
    job_data bytea,
    CONSTRAINT "PK_qrtz_job_details" PRIMARY KEY (sched_name, job_name, job_group)
);

CREATE TABLE qrtz_locks (
    sched_name character varying(120) NOT NULL,
    lock_name character varying(40) NOT NULL,
    CONSTRAINT "PK_qrtz_locks" PRIMARY KEY (sched_name, lock_name)
);

CREATE TABLE qrtz_paused_trigger_grps (
    sched_name character varying(120) NOT NULL,
    trigger_group character varying(150) NOT NULL,
    CONSTRAINT "PK_qrtz_paused_trigger_grps" PRIMARY KEY (sched_name, trigger_group)
);

CREATE TABLE qrtz_scheduler_state (
    sched_name character varying(120) NOT NULL,
    instance_name character varying(200) NOT NULL,
    last_checkin_time bigint NOT NULL,
    checkin_interval bigint NOT NULL,
    CONSTRAINT "PK_qrtz_scheduler_state" PRIMARY KEY (sched_name, instance_name)
);

CREATE TABLE qrtz_triggers (
    sched_name character varying(120) NOT NULL,
    trigger_name character varying(150) NOT NULL,
    trigger_group character varying(150) NOT NULL,
    job_name character varying(150) NOT NULL,
    job_group character varying(150) NOT NULL,
    description character varying(250),
    next_fire_time bigint,
    prev_fire_time bigint,
    priority integer,
    trigger_state character varying(16) NOT NULL,
    trigger_type character varying(8) NOT NULL,
    start_time bigint NOT NULL,
    end_time bigint,
    calendar_name character varying(200),
    misfire_instr smallint,
    job_data bytea,
    CONSTRAINT "PK_qrtz_triggers" PRIMARY KEY (sched_name, trigger_name, trigger_group),
    CONSTRAINT "FK_qrtz_triggers_qrtz_job_details_sched_name_job_name_job_group" FOREIGN KEY (sched_name, job_name, job_group) REFERENCES qrtz_job_details (sched_name, job_name, job_group) ON DELETE CASCADE
);

CREATE TABLE qrtz_blob_triggers (
    sched_name character varying(120) NOT NULL,
    trigger_name character varying(150) NOT NULL,
    trigger_group character varying(150) NOT NULL,
    blob_data bytea,
    CONSTRAINT "PK_qrtz_blob_triggers" PRIMARY KEY (sched_name, trigger_name, trigger_group),
    CONSTRAINT "FK_qrtz_blob_triggers_qrtz_triggers_sched_name_trigger_name_tr~" FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
);

CREATE TABLE qrtz_cron_triggers (
    sched_name character varying(120) NOT NULL,
    trigger_name character varying(150) NOT NULL,
    trigger_group character varying(150) NOT NULL,
    cron_expression character varying(120) NOT NULL,
    time_zone_id character varying(80),
    CONSTRAINT "PK_qrtz_cron_triggers" PRIMARY KEY (sched_name, trigger_name, trigger_group),
    CONSTRAINT "FK_qrtz_cron_triggers_qrtz_triggers_sched_name_trigger_name_tr~" FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
);

CREATE TABLE qrtz_simple_triggers (
    sched_name character varying(120) NOT NULL,
    trigger_name character varying(150) NOT NULL,
    trigger_group character varying(150) NOT NULL,
    repeat_count bigint NOT NULL,
    repeat_interval bigint NOT NULL,
    times_triggered bigint NOT NULL,
    CONSTRAINT "PK_qrtz_simple_triggers" PRIMARY KEY (sched_name, trigger_name, trigger_group),
    CONSTRAINT "FK_qrtz_simple_triggers_qrtz_triggers_sched_name_trigger_name_~" FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
);

CREATE TABLE qrtz_simprop_triggers (
    sched_name character varying(120) NOT NULL,
    trigger_name character varying(150) NOT NULL,
    trigger_group character varying(150) NOT NULL,
    str_prop_1 character varying(512),
    str_prop_2 character varying(512),
    str_prop_3 character varying(512),
    int_prop_1 integer,
    int_prop_2 integer,
    long_prop_1 bigint,
    long_prop_2 bigint,
    dec_prop_1 numeric(13,4),
    dec_prop_2 numeric(13,4),
    bool_prop_1 boolean,
    bool_prop_2 boolean,
    time_zone_id character varying(80),
    CONSTRAINT "PK_qrtz_simprop_triggers" PRIMARY KEY (sched_name, trigger_name, trigger_group),
    CONSTRAINT "FK_qrtz_simprop_triggers_qrtz_triggers_sched_name_trigger_name~" FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
);

CREATE INDEX idx_qrtz_ft_job_group ON qrtz_fired_triggers (job_group);

CREATE INDEX idx_qrtz_ft_job_name ON qrtz_fired_triggers (job_name);

CREATE INDEX idx_qrtz_ft_job_req_recovery ON qrtz_fired_triggers (requests_recovery);

CREATE INDEX idx_qrtz_ft_trig_group ON qrtz_fired_triggers (trigger_group);

CREATE INDEX idx_qrtz_ft_trig_inst_name ON qrtz_fired_triggers (instance_name);

CREATE INDEX idx_qrtz_ft_trig_name ON qrtz_fired_triggers (trigger_name);

CREATE INDEX idx_qrtz_ft_trig_nm_gp ON qrtz_fired_triggers (trigger_name, trigger_group);

CREATE INDEX idx_qrtz_j_req_recovery ON qrtz_job_details (requests_recovery);

CREATE INDEX idx_qrtz_t_next_fire_time ON qrtz_triggers (next_fire_time);

CREATE INDEX idx_qrtz_t_nft_st ON qrtz_triggers (next_fire_time, trigger_state);

CREATE INDEX idx_qrtz_t_state ON qrtz_triggers (trigger_state);

CREATE INDEX "IX_qrtz_triggers_sched_name_job_name_job_group" ON qrtz_triggers (sched_name, job_name, job_group);
