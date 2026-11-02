using System;
using System.Collections.Generic;
using System.IO;
using DesktopNetBlocker.Core.Models;
using Microsoft.Data.Sqlite;

namespace DesktopNetBlocker.Core.Data;

public class BlockerDbContext : IDisposable
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    public BlockerDbContext(string dbPath)
    {
        string? directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeDatabase();
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL;";
            cmd.ExecuteNonQuery();
        }

        return conn;
    }

    private void InitializeDatabase()
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS blocked_domains (
                    id TEXT PRIMARY KEY,
                    domain TEXT NOT NULL,
                    matching_mode INTEGER NOT NULL,
                    category TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    policy_version INTEGER NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS idx_domains_domain ON blocked_domains (domain);
                CREATE INDEX IF NOT EXISTS idx_domains_status ON blocked_domains (status);

                CREATE TABLE IF NOT EXISTS authorization_requests (
                    id TEXT PRIMARY KEY,
                    request_type INTEGER NOT NULL,
                    domain_id TEXT,
                    target_domain TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    initial_deadline TEXT NOT NULL,
                    confirmation_deadline TEXT,
                    confirmed_at TEXT,
                    final_deadline TEXT,
                    status INTEGER NOT NULL,
                    cancellation_count INTEGER NOT NULL,
                    payload_json TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_auth_requests_status ON authorization_requests (status);
                CREATE INDEX IF NOT EXISTS idx_auth_requests_domain ON authorization_requests (domain_id);

                CREATE TABLE IF NOT EXISTS cancellation_history (
                    id TEXT PRIMARY KEY,
                    request_id TEXT NOT NULL,
                    domain_id TEXT,
                    target_domain TEXT NOT NULL,
                    cancelled_at TEXT NOT NULL,
                    cancellation_count INTEGER NOT NULL,
                    resulting_cooldown_seconds INTEGER NOT NULL,
                    next_allowed_request_at TEXT NOT NULL,
                    reason TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_cancellation_domain ON cancellation_history (domain_id);

                CREATE TABLE IF NOT EXISTS temporary_access (
                    id TEXT PRIMARY KEY,
                    domain_id TEXT NOT NULL,
                    target_domain TEXT NOT NULL,
                    granted_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    status INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_temporary_access_status ON temporary_access (status);

                CREATE TABLE IF NOT EXISTS policy_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    pending_value TEXT,
                    effective_at TEXT,
                    status TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS restriction_events (
                    id TEXT PRIMARY KEY,
                    timestamp TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    domain TEXT,
                    details TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_events_timestamp ON restriction_events (timestamp DESC);
            ";
            cmd.ExecuteNonQuery();

            // Default policy settings if not present
            InsertSettingIfNotExists(conn, "InitialWaitingPeriodDays", "3");
            InsertSettingIfNotExists(conn, "ConfirmationDelayDays", "3");
            InsertSettingIfNotExists(conn, "ConfirmationWindowHours", "24");
            InsertSettingIfNotExists(conn, "TemporaryAccessDurationHours", "2");
            InsertSettingIfNotExists(conn, "FirstCancellationCooldownDays", "7");
            InsertSettingIfNotExists(conn, "SecondCancellationCooldownDays", "14");
            InsertSettingIfNotExists(conn, "ThirdCancellationCooldownDays", "30");
            InsertSettingIfNotExists(conn, "OverlayOpacity", "0.92");
            InsertSettingIfNotExists(conn, "OverlayPosition", "TopRight");
            InsertSettingIfNotExists(conn, "OverlayEnabled", "true");
        }
    }

    private static void InsertSettingIfNotExists(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO policy_settings (key, value, status) VALUES (@k, @v, 'Active');";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    // --- Domain Queries ---

    public List<BlockedDomain> GetAllDomains()
    {
        lock (_lock)
        {
            var list = new List<BlockedDomain>();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, domain, matching_mode, category, created_at, status, policy_version FROM blocked_domains ORDER BY created_at DESC;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new BlockedDomain
                {
                    Id = reader.GetString(0),
                    Domain = reader.GetString(1),
                    MatchingMode = (MatchingMode)reader.GetInt32(2),
                    Category = reader.GetString(3),
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
                    Status = (DomainStatus)reader.GetInt32(5),
                    PolicyVersion = reader.GetInt32(6)
                });
            }
            return list;
        }
    }

    public List<BlockedDomain> GetActiveBlockedDomains()
    {
        lock (_lock)
        {
            var list = new List<BlockedDomain>();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, domain, matching_mode, category, created_at, status, policy_version FROM blocked_domains WHERE status = 0;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new BlockedDomain
                {
                    Id = reader.GetString(0),
                    Domain = reader.GetString(1),
                    MatchingMode = (MatchingMode)reader.GetInt32(2),
                    Category = reader.GetString(3),
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
                    Status = (DomainStatus)reader.GetInt32(5),
                    PolicyVersion = reader.GetInt32(6)
                });
            }
            return list;
        }
    }

    public BlockedDomain? GetDomainById(string id)
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, domain, matching_mode, category, created_at, status, policy_version FROM blocked_domains WHERE id = @id LIMIT 1;";
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new BlockedDomain
                {
                    Id = reader.GetString(0),
                    Domain = reader.GetString(1),
                    MatchingMode = (MatchingMode)reader.GetInt32(2),
                    Category = reader.GetString(3),
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
                    Status = (DomainStatus)reader.GetInt32(5),
                    PolicyVersion = reader.GetInt32(6)
                };
            }
            return null;
        }
    }

    public BlockedDomain? GetDomainByName(string domain)
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, domain, matching_mode, category, created_at, status, policy_version FROM blocked_domains WHERE domain = @domain COLLATE NOCASE LIMIT 1;";
            cmd.Parameters.AddWithValue("@domain", domain.Trim().ToLowerInvariant());

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new BlockedDomain
                {
                    Id = reader.GetString(0),
                    Domain = reader.GetString(1),
                    MatchingMode = (MatchingMode)reader.GetInt32(2),
                    Category = reader.GetString(3),
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
                    Status = (DomainStatus)reader.GetInt32(5),
                    PolicyVersion = reader.GetInt32(6)
                };
            }
            return null;
        }
    }

    public void AddDomains(IEnumerable<BlockedDomain> domains)
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            foreach (var d in domains)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO blocked_domains (id, domain, matching_mode, category, created_at, status, policy_version)
                    VALUES (@id, @domain, @mode, @category, @created, @status, @ver)
                    ON CONFLICT(domain) DO UPDATE SET
                        status = 0,
                        matching_mode = excluded.matching_mode,
                        category = excluded.category;
                ";
                cmd.Parameters.AddWithValue("@id", d.Id);
                cmd.Parameters.AddWithValue("@domain", d.Domain.Trim().ToLowerInvariant());
                cmd.Parameters.AddWithValue("@mode", (int)d.MatchingMode);
                cmd.Parameters.AddWithValue("@category", d.Category);
                cmd.Parameters.AddWithValue("@created", d.CreatedAt.ToString("o"));
                cmd.Parameters.AddWithValue("@status", (int)d.Status);
                cmd.Parameters.AddWithValue("@ver", d.PolicyVersion);
                cmd.ExecuteNonQuery();

                RecordEventInternal(conn, tx, "DomainAdded", d.Domain, $"Added with matching mode {d.MatchingMode} and category {d.Category}");
            }

            tx.Commit();
        }
    }

    public void UpdateDomainStatus(string domainId, DomainStatus status)
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE blocked_domains SET status = @status WHERE id = @id;";
            cmd.Parameters.AddWithValue("@status", (int)status);
            cmd.Parameters.AddWithValue("@id", domainId);
            cmd.ExecuteNonQuery();
        }
    }

    // --- Authorization Requests ---

    public AuthorizationRequest? GetActiveRequest()
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            // Active requests are in Pending (1), Confirmation (2), or FinalDelay (3)
            cmd.CommandText = @"
                SELECT id, request_type, domain_id, target_domain, created_at, initial_deadline,
                       confirmation_deadline, confirmed_at, final_deadline, status, cancellation_count, payload_json
                FROM authorization_requests
                WHERE status IN (1, 2, 3)
                ORDER BY created_at ASC
                LIMIT 1;
            ";

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return MapAuthRequest(reader);
            }
            return null;
        }
    }

    public List<AuthorizationRequest> GetAllRequests()
    {
        lock (_lock)
        {
            var list = new List<AuthorizationRequest>();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, request_type, domain_id, target_domain, created_at, initial_deadline,
                       confirmation_deadline, confirmed_at, final_deadline, status, cancellation_count, payload_json
                FROM authorization_requests
                ORDER BY created_at DESC;
            ";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(MapAuthRequest(reader));
            }
            return list;
        }
    }

    public AuthorizationRequest? GetRequestById(string id)
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, request_type, domain_id, target_domain, created_at, initial_deadline,
                       confirmation_deadline, confirmed_at, final_deadline, status, cancellation_count, payload_json
                FROM authorization_requests
                WHERE id = @id
                LIMIT 1;
            ";
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return MapAuthRequest(reader);
            }
            return null;
        }
    }

    public void SaveAuthorizationRequest(AuthorizationRequest req)
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO authorization_requests (
                    id, request_type, domain_id, target_domain, created_at, initial_deadline,
                    confirmation_deadline, confirmed_at, final_deadline, status, cancellation_count, payload_json
                ) VALUES (
                    @id, @type, @did, @domain, @created, @initDl,
                    @confDl, @confAt, @finDl, @status, @cancels, @payload
                )
                ON CONFLICT(id) DO UPDATE SET
                    status = excluded.status,
                    confirmation_deadline = excluded.confirmation_deadline,
                    confirmed_at = excluded.confirmed_at,
                    final_deadline = excluded.final_deadline,
                    cancellation_count = excluded.cancellation_count,
                    payload_json = excluded.payload_json;
            ";
            cmd.Parameters.AddWithValue("@id", req.Id);
            cmd.Parameters.AddWithValue("@type", (int)req.RequestType);
            cmd.Parameters.AddWithValue("@did", (object?)req.DomainId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@domain", req.TargetDomain);
            cmd.Parameters.AddWithValue("@created", req.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@initDl", req.InitialDeadline.ToString("o"));
            cmd.Parameters.AddWithValue("@confDl", req.ConfirmationDeadline.HasValue ? req.ConfirmationDeadline.Value.ToString("o") : DBNull.Value);
            cmd.Parameters.AddWithValue("@confAt", req.ConfirmedAt.HasValue ? req.ConfirmedAt.Value.ToString("o") : DBNull.Value);
            cmd.Parameters.AddWithValue("@finDl", req.FinalDeadline.HasValue ? req.FinalDeadline.Value.ToString("o") : DBNull.Value);
            cmd.Parameters.AddWithValue("@status", (int)req.Status);
            cmd.Parameters.AddWithValue("@cancels", req.CancellationCount);
            cmd.Parameters.AddWithValue("@payload", (object?)req.PayloadJson ?? DBNull.Value);

            cmd.ExecuteNonQuery();
        }
    }

    private static AuthorizationRequest MapAuthRequest(SqliteDataReader reader)
    {
        return new AuthorizationRequest
        {
            Id = reader.GetString(0),
            RequestType = (RequestType)reader.GetInt32(1),
            DomainId = reader.IsDBNull(2) ? null : reader.GetString(2),
            TargetDomain = reader.GetString(3),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
            InitialDeadline = DateTimeOffset.Parse(reader.GetString(5)),
            ConfirmationDeadline = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
            ConfirmedAt = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
            FinalDeadline = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
            Status = (RequestStatus)reader.GetInt32(9),
            CancellationCount = reader.GetInt32(10),
            PayloadJson = reader.IsDBNull(11) ? null : reader.GetString(11)
        };
    }

    // --- Cancellation History ---

    public void AddCancellation(CancellationHistory history)
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO cancellation_history (
                    id, request_id, domain_id, target_domain, cancelled_at,
                    cancellation_count, resulting_cooldown_seconds, next_allowed_request_at, reason
                ) VALUES (
                    @id, @reqId, @did, @domain, @at,
                    @count, @secs, @nextAllowed, @reason
                );
            ";
            cmd.Parameters.AddWithValue("@id", history.Id);
            cmd.Parameters.AddWithValue("@reqId", history.RequestId);
            cmd.Parameters.AddWithValue("@did", (object?)history.DomainId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@domain", history.TargetDomain);
            cmd.Parameters.AddWithValue("@at", history.CancelledAt.ToString("o"));
            cmd.Parameters.AddWithValue("@count", history.CancellationCount);
            cmd.Parameters.AddWithValue("@secs", (long)history.ResultingCooldown.TotalSeconds);
            cmd.Parameters.AddWithValue("@nextAllowed", history.NextAllowedRequestAt.ToString("o"));
            cmd.Parameters.AddWithValue("@reason", (object?)history.Reason ?? DBNull.Value);

            cmd.ExecuteNonQuery();
        }
    }

    public List<CancellationHistory> GetCancellationHistory(string? domainId = null)
    {
        lock (_lock)
        {
            var list = new List<CancellationHistory>();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();

            if (string.IsNullOrEmpty(domainId))
            {
                cmd.CommandText = @"
                    SELECT id, request_id, domain_id, target_domain, cancelled_at,
                           cancellation_count, resulting_cooldown_seconds, next_allowed_request_at, reason
                    FROM cancellation_history
                    ORDER BY cancelled_at DESC;
                ";
            }
            else
            {
                cmd.CommandText = @"
                    SELECT id, request_id, domain_id, target_domain, cancelled_at,
                           cancellation_count, resulting_cooldown_seconds, next_allowed_request_at, reason
                    FROM cancellation_history
                    WHERE domain_id = @did
                    ORDER BY cancelled_at DESC;
                ";
                cmd.Parameters.AddWithValue("@did", domainId);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new CancellationHistory
                {
                    Id = reader.GetString(0),
                    RequestId = reader.GetString(1),
                    DomainId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    TargetDomain = reader.GetString(3),
                    CancelledAt = DateTimeOffset.Parse(reader.GetString(4)),
                    CancellationCount = reader.GetInt32(5),
                    ResultingCooldown = TimeSpan.FromSeconds(reader.GetInt64(6)),
                    NextAllowedRequestAt = DateTimeOffset.Parse(reader.GetString(7)),
                    Reason = reader.IsDBNull(8) ? null : reader.GetString(8)
                });
            }
            return list;
        }
    }

    public CancellationHistory? GetLatestCancellationForDomain(string domainId)
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, request_id, domain_id, target_domain, cancelled_at,
                       cancellation_count, resulting_cooldown_seconds, next_allowed_request_at, reason
                FROM cancellation_history
                WHERE domain_id = @did
                ORDER BY cancelled_at DESC
                LIMIT 1;
            ";
            cmd.Parameters.AddWithValue("@did", domainId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new CancellationHistory
                {
                    Id = reader.GetString(0),
                    RequestId = reader.GetString(1),
                    DomainId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    TargetDomain = reader.GetString(3),
                    CancelledAt = DateTimeOffset.Parse(reader.GetString(4)),
                    CancellationCount = reader.GetInt32(5),
                    ResultingCooldown = TimeSpan.FromSeconds(reader.GetInt64(6)),
                    NextAllowedRequestAt = DateTimeOffset.Parse(reader.GetString(7)),
                    Reason = reader.IsDBNull(8) ? null : reader.GetString(8)
                };
            }
            return null;
        }
    }

    public int GetCancellationCountForDomain(string domainId)
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM cancellation_history WHERE domain_id = @did;";
            cmd.Parameters.AddWithValue("@did", domainId);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    // --- Temporary Access ---

    public void AddTemporaryAccess(TemporaryAccess access)
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO temporary_access (id, domain_id, target_domain, granted_at, expires_at, status)
                VALUES (@id, @did, @domain, @grant, @exp, @status);
            ";
            cmd.Parameters.AddWithValue("@id", access.Id);
            cmd.Parameters.AddWithValue("@did", access.DomainId);
            cmd.Parameters.AddWithValue("@domain", access.TargetDomain);
            cmd.Parameters.AddWithValue("@grant", access.GrantedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@exp", access.ExpiresAt.ToString("o"));
            cmd.Parameters.AddWithValue("@status", (int)access.Status);
            cmd.ExecuteNonQuery();
        }
    }

    public List<TemporaryAccess> GetActiveTemporaryAccessList()
    {
        lock (_lock)
        {
            var list = new List<TemporaryAccess>();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, domain_id, target_domain, granted_at, expires_at, status
                FROM temporary_access
                WHERE status = 0;
            ";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new TemporaryAccess
                {
                    Id = reader.GetString(0),
                    DomainId = reader.GetString(1),
                    TargetDomain = reader.GetString(2),
                    GrantedAt = DateTimeOffset.Parse(reader.GetString(3)),
                    ExpiresAt = DateTimeOffset.Parse(reader.GetString(4)),
                    Status = (TemporaryAccessStatus)reader.GetInt32(5)
                });
            }
            return list;
        }
    }

    public void UpdateTemporaryAccessStatus(string id, TemporaryAccessStatus status)
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE temporary_access SET status = @status WHERE id = @id;";
            cmd.Parameters.AddWithValue("@status", (int)status);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    // --- Policy Settings ---

    public Dictionary<string, string> GetPolicySettings()
    {
        lock (_lock)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM policy_settings;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                dict[reader.GetString(0)] = reader.GetString(1);
            }
            return dict;
        }
    }

    public string? GetPolicySetting(string key)
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM policy_settings WHERE key = @k LIMIT 1;";
            cmd.Parameters.AddWithValue("@k", key);

            object? val = cmd.ExecuteScalar();
            return val?.ToString();
        }
    }

    public void SetPolicySetting(string key, string value)
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO policy_settings (key, value, status) VALUES (@k, @v, 'Active')
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            ";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", value);
            cmd.ExecuteNonQuery();
        }
    }

    // --- Restriction Events / Audit Log ---

    public void RecordEvent(string eventType, string? domain, string? details)
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            RecordEventInternal(conn, tx, eventType, domain, details);
            tx.Commit();
        }
    }

    private static void RecordEventInternal(SqliteConnection conn, SqliteTransaction tx, string eventType, string? domain, string? details)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO restriction_events (id, timestamp, event_type, domain, details)
            VALUES (@id, @ts, @type, @dom, @det);
        ";
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@type", eventType);
        cmd.Parameters.AddWithValue("@dom", (object?)domain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@det", (object?)details ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<RestrictionEvent> GetRecentEvents(int limit = 100)
    {
        lock (_lock)
        {
            var list = new List<RestrictionEvent>();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, timestamp, event_type, domain, details
                FROM restriction_events
                ORDER BY timestamp DESC
                LIMIT @lim;
            ";
            cmd.Parameters.AddWithValue("@lim", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new RestrictionEvent
                {
                    Id = reader.GetString(0),
                    Timestamp = DateTimeOffset.Parse(reader.GetString(1)),
                    EventType = reader.GetString(2),
                    Domain = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Details = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }
            return list;
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}

