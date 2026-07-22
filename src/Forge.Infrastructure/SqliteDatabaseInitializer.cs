using Forge.Core;
using Microsoft.Data.Sqlite;

namespace Forge.Infrastructure;

public sealed class SqliteDatabaseInitializer(string connectionString)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (!string.IsNullOrWhiteSpace(builder.DataSource))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS EngineeringTasks (
                Id TEXT PRIMARY KEY,
                Repository TEXT NOT NULL,
                OriginalRequirement TEXT NOT NULL,
                CurrentClarifiedRequirement TEXT NOT NULL,
                ClarificationAnswers TEXT NOT NULL,
                RequirementRevisionNotes TEXT NOT NULL DEFAULT '[]',
                PlanRevisionNotes TEXT NOT NULL DEFAULT '[]',
                ModelCalls TEXT NOT NULL DEFAULT '[]',
                CurrentPendingQuestion TEXT NULL,
                RequirementSummary TEXT NULL,
                Status TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                RequirementApprovedAt TEXT NULL,
                PlanApprovedAt TEXT NULL,
                RepositorySnapshot TEXT NULL,
                EvidenceItems TEXT NOT NULL DEFAULT '[]',
                EvidenceFilesInspected INTEGER NOT NULL DEFAULT 0,
                EvidenceFilesSelected INTEGER NOT NULL DEFAULT 0,
                TotalEvidenceCharacters INTEGER NOT NULL DEFAULT 0,
                ImplementationPlan TEXT NULL,
                RepositoryAnalyzedAt TEXT NULL,
                RepositoryFingerprint TEXT NULL,
                PlanCreatedAt TEXT NULL,
                ImplementationWorkspace TEXT NULL,
                ImplementationResult TEXT NULL,
                LastImplementationFailure TEXT NULL,
                ImplementationStartedAt TEXT NULL,
                ImplementationCompletedAt TEXT NULL,
                ImplementationLease TEXT NULL,
                ImplementationRevisions TEXT NOT NULL DEFAULT '[]',
                ActiveImplementationRevisionId TEXT NULL,
                ApprovedImplementationRevisionId TEXT NULL,
                PendingImplementationRevisionId TEXT NULL,
                CurrentVerificationPlanId TEXT NULL,
                CurrentVerificationAttemptId TEXT NULL,
                VerificationDataFormatVersion INTEGER NOT NULL DEFAULT 0,
                CurrentFailureAnalysisId TEXT NULL,
                CurrentCorrectionProposalId TEXT NULL,
                CorrectionDataFormatVersion INTEGER NOT NULL DEFAULT 0,
                CurrentDeliveryProposalId TEXT NULL,
                CurrentDeliveryAttemptId TEXT NULL,
                DeliveryDataFormatVersion INTEGER NOT NULL DEFAULT 0,
                RowVersion INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS ImplementationApprovalCommands (
                CommandId TEXT PRIMARY KEY NOT NULL CHECK(length(CommandId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                ExpectedRowVersion INTEGER NOT NULL CHECK(ExpectedRowVersion >= 0),
                RevisionId TEXT NOT NULL CHECK(length(RevisionId) = 36),
                ResultFingerprint TEXT NOT NULL CHECK(length(ResultFingerprint) = 64),
                ApprovedRowVersion INTEGER NULL CHECK(ApprovedRowVersion >= 1),
                ApprovalTimestamp TEXT NULL,
                CHECK((ApprovedRowVersion IS NULL) = (ApprovalTimestamp IS NULL))
            );
            CREATE TABLE IF NOT EXISTS VerificationPlans (
                PlanId TEXT PRIMARY KEY NOT NULL CHECK(length(PlanId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                PlanNumber INTEGER NOT NULL CHECK(PlanNumber >= 1),
                ImplementationRevisionId TEXT NOT NULL CHECK(length(ImplementationRevisionId) = 36),
                ImplementationResultFingerprint TEXT NOT NULL CHECK(length(ImplementationResultFingerprint) = 64),
                ApprovedRequirementFingerprint TEXT NOT NULL CHECK(length(ApprovedRequirementFingerprint) = 64),
                ApprovedPlanFingerprint TEXT NOT NULL CHECK(length(ApprovedPlanFingerprint) = 64),
                PlanFingerprint TEXT NOT NULL CHECK(length(PlanFingerprint) = 64),
                Status TEXT NOT NULL,
                GeneratedAt TEXT NOT NULL,
                Json TEXT NOT NULL,
                UNIQUE(TaskId, PlanNumber)
            );
            CREATE TABLE IF NOT EXISTS VerificationPlanGenerationCommands (
                CommandId TEXT PRIMARY KEY NOT NULL CHECK(length(CommandId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                Status TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT NULL,
                Json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ManualVerificationAttempts (
                AttemptId TEXT PRIMARY KEY NOT NULL CHECK(length(AttemptId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                AttemptNumber INTEGER NOT NULL CHECK(AttemptNumber >= 1),
                PlanId TEXT NOT NULL CHECK(length(PlanId) = 36),
                ImplementationRevisionId TEXT NOT NULL CHECK(length(ImplementationRevisionId) = 36),
                ImplementationResultFingerprint TEXT NOT NULL CHECK(length(ImplementationResultFingerprint) = 64),
                Status TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT NULL,
                AttemptFingerprint TEXT NULL,
                Json TEXT NOT NULL,
                UNIQUE(TaskId, AttemptNumber)
            );
            CREATE TABLE IF NOT EXISTS ManualCaseResultRevisions (
                ResultRevisionId TEXT PRIMARY KEY NOT NULL CHECK(length(ResultRevisionId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                AttemptId TEXT NOT NULL CHECK(length(AttemptId) = 36),
                TestCaseId TEXT NOT NULL CHECK(length(TestCaseId) = 36),
                RevisionNumber INTEGER NOT NULL CHECK(RevisionNumber >= 1),
                Result TEXT NOT NULL,
                RecordedAt TEXT NOT NULL,
                SupersedesResultRevisionId TEXT NULL,
                Json TEXT NOT NULL,
                UNIQUE(AttemptId, TestCaseId, RevisionNumber)
            );
            CREATE TABLE IF NOT EXISTS VerificationCommandBindings (
                CommandId TEXT PRIMARY KEY NOT NULL CHECK(length(CommandId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                CommandType TEXT NOT NULL,
                SemanticFingerprint TEXT NOT NULL CHECK(length(SemanticFingerprint) = 64),
                ResultIdentity TEXT NULL,
                CompletedRowVersion INTEGER NULL CHECK(CompletedRowVersion >= 1),
                CompletedAt TEXT NULL,
                CHECK((CompletedRowVersion IS NULL) = (CompletedAt IS NULL))
            );
            CREATE TABLE IF NOT EXISTS FailureAnalyses (
                AnalysisId TEXT PRIMARY KEY NOT NULL CHECK(length(AnalysisId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                AnalysisNumber INTEGER NOT NULL CHECK(AnalysisNumber >= 1),
                AnalysisFingerprint TEXT NOT NULL CHECK(length(AnalysisFingerprint) = 64),
                Classification TEXT NOT NULL,
                Json TEXT NOT NULL,
                UNIQUE(TaskId, AnalysisNumber)
            );
            CREATE TABLE IF NOT EXISTS FailureAnalysisGenerationCommands (
                CommandId TEXT PRIMARY KEY NOT NULL CHECK(length(CommandId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                SemanticFingerprint TEXT NOT NULL CHECK(length(SemanticFingerprint) = 64),
                Status TEXT NOT NULL,
                ResultAnalysisId TEXT NULL,
                Json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS CorrectionProposals (
                ProposalId TEXT PRIMARY KEY NOT NULL CHECK(length(ProposalId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                ProposalNumber INTEGER NOT NULL CHECK(ProposalNumber >= 1),
                ProposalFingerprint TEXT NOT NULL CHECK(length(ProposalFingerprint) = 64),
                Status TEXT NOT NULL,
                Json TEXT NOT NULL,
                UNIQUE(TaskId, ProposalNumber)
            );
            CREATE TABLE IF NOT EXISTS CorrectionApprovalCommands (
                CommandId TEXT PRIMARY KEY NOT NULL CHECK(length(CommandId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                SemanticFingerprint TEXT NOT NULL CHECK(length(SemanticFingerprint) = 64),
                ProposalId TEXT NOT NULL CHECK(length(ProposalId) = 36),
                ProposalFingerprint TEXT NOT NULL CHECK(length(ProposalFingerprint) = 64),
                ExpectedRowVersion INTEGER NOT NULL CHECK(ExpectedRowVersion >= 0),
                CreatedAt TEXT NOT NULL,
                CompletedRowVersion INTEGER NULL CHECK(CompletedRowVersion >= 1),
                CompletedAt TEXT NULL,
                Result TEXT NOT NULL DEFAULT 'Approved',
                CHECK((CompletedRowVersion IS NULL) = (CompletedAt IS NULL))
            );
            CREATE TABLE IF NOT EXISTS CorrectionReconciliationCommands (
                CommandId TEXT PRIMARY KEY NOT NULL CHECK(length(CommandId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                Kind TEXT NOT NULL CHECK(Kind IN ('FailureAnalysis','CorrectionGeneration')),
                AttemptId TEXT NOT NULL CHECK(length(AttemptId) = 36),
                SemanticFingerprint TEXT NOT NULL CHECK(length(SemanticFingerprint) = 64),
                CompletedRowVersion INTEGER NOT NULL CHECK(CompletedRowVersion >= 0),
                CreatedAt TEXT NOT NULL,
                CompletedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS CorrectionGenerationCommands (
                CommandId TEXT PRIMARY KEY NOT NULL CHECK(length(CommandId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                SemanticFingerprint TEXT NOT NULL CHECK(length(SemanticFingerprint) = 64),
                ProposalId TEXT NOT NULL CHECK(length(ProposalId) = 36),
                RevisionId TEXT NULL CHECK(RevisionId IS NULL OR length(RevisionId) = 36),
                Status TEXT NOT NULL,
                Json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS DeliveryProposals (
                DeliveryProposalId TEXT PRIMARY KEY NOT NULL CHECK(length(DeliveryProposalId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                ProposalNumber INTEGER NOT NULL CHECK(ProposalNumber = 1),
                ProposalFingerprint TEXT NOT NULL CHECK(length(ProposalFingerprint) = 64),
                Status TEXT NOT NULL,
                Json TEXT NOT NULL,
                UNIQUE(TaskId, ProposalNumber)
            );
            CREATE TABLE IF NOT EXISTS DeliveryApprovalCommands (
                CommandId TEXT PRIMARY KEY NOT NULL CHECK(length(CommandId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                SemanticFingerprint TEXT NOT NULL CHECK(length(SemanticFingerprint) = 64),
                ProposalId TEXT NOT NULL CHECK(length(ProposalId) = 36),
                ProposalFingerprint TEXT NOT NULL CHECK(length(ProposalFingerprint) = 64),
                ExpectedRowVersion INTEGER NOT NULL CHECK(ExpectedRowVersion >= 0),
                CompletedRowVersion INTEGER NOT NULL CHECK(CompletedRowVersion >= 1),
                CreatedAt TEXT NOT NULL,
                CompletedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS DeliveryAttempts (
                AttemptId TEXT PRIMARY KEY NOT NULL CHECK(length(AttemptId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                AttemptNumber INTEGER NOT NULL CHECK(AttemptNumber BETWEEN 1 AND 3),
                CommandId TEXT NOT NULL UNIQUE CHECK(length(CommandId) = 36),
                DeliveryProposalId TEXT NOT NULL CHECK(length(DeliveryProposalId) = 36),
                DeliveryProposalFingerprint TEXT NOT NULL CHECK(length(DeliveryProposalFingerprint) = 64),
                Phase TEXT NOT NULL,
                Json TEXT NOT NULL,
                UNIQUE(TaskId, AttemptNumber)
            );
            CREATE TABLE IF NOT EXISTS DeliveryCommandBindings (
                CommandId TEXT PRIMARY KEY NOT NULL CHECK(length(CommandId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                CommandType TEXT NOT NULL CHECK(CommandType IN ('Prepare','Execute')),
                SemanticFingerprint TEXT NOT NULL CHECK(length(SemanticFingerprint) = 64),
                ResultIdentity TEXT NULL,
                CompletedRowVersion INTEGER NULL CHECK(CompletedRowVersion IS NULL OR CompletedRowVersion >= 1),
                CreatedAt TEXT NOT NULL,
                CompletedAt TEXT NULL,
                CHECK((CompletedRowVersion IS NULL) = (CompletedAt IS NULL))
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureTableColumnAsync(connection, "CorrectionApprovalCommands", "ProposalFingerprint",
            "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureTableColumnAsync(connection, "CorrectionApprovalCommands", "ExpectedRowVersion",
            "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureTableColumnAsync(connection, "CorrectionApprovalCommands", "CreatedAt",
            "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000+00:00'", cancellationToken);
        await EnsureTableColumnAsync(connection, "CorrectionApprovalCommands", "Result",
            "TEXT NOT NULL DEFAULT 'Approved'", cancellationToken);
        await using (var backfill = connection.CreateCommand())
        {
            backfill.CommandText = """
                UPDATE CorrectionApprovalCommands
                SET ProposalFingerprint = COALESCE((SELECT ProposalFingerprint FROM CorrectionProposals
                        WHERE CorrectionProposals.ProposalId = CorrectionApprovalCommands.ProposalId), ProposalFingerprint),
                    ExpectedRowVersion = COALESCE((SELECT CAST(json_extract(Json,'$.approvalExpectedRowVersion') AS INTEGER)
                        FROM CorrectionProposals WHERE CorrectionProposals.ProposalId = CorrectionApprovalCommands.ProposalId), ExpectedRowVersion),
                    CreatedAt = COALESCE((SELECT json_extract(Json,'$.approvedAt') FROM CorrectionProposals
                        WHERE CorrectionProposals.ProposalId = CorrectionApprovalCommands.ProposalId), CompletedAt, CreatedAt),
                    Result = 'Approved'
                WHERE ProposalFingerprint = '' OR CreatedAt = '1970-01-01T00:00:00.0000000+00:00';
                """;
            await backfill.ExecuteNonQueryAsync(cancellationToken);
        }
        await EnsureColumnAsync(connection, "RequirementRevisionNotes", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "PlanRevisionNotes", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "ModelCalls", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "RepositorySnapshot", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "EvidenceItems", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "EvidenceFilesInspected", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "EvidenceFilesSelected", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "TotalEvidenceCharacters", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "ImplementationPlan", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "RepositoryAnalyzedAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "RepositoryFingerprint", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "PlanCreatedAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ImplementationWorkspace", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ImplementationResult", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "LastImplementationFailure", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ImplementationStartedAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ImplementationCompletedAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ImplementationLease", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ImplementationRevisions", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "ActiveImplementationRevisionId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ApprovedImplementationRevisionId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "PendingImplementationRevisionId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "CurrentVerificationPlanId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "CurrentVerificationAttemptId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "VerificationDataFormatVersion", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "CurrentFailureAnalysisId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "CurrentCorrectionProposalId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "CorrectionDataFormatVersion", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "CurrentDeliveryProposalId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "CurrentDeliveryAttemptId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "DeliveryDataFormatVersion", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        command.CommandText = $"""
            CREATE TRIGGER IF NOT EXISTS RequireCurrentVerificationFormatForPlan
            BEFORE INSERT ON VerificationPlans BEGIN
              SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM EngineeringTasks WHERE Id = NEW.TaskId
                  AND VerificationDataFormatVersion = {VerificationDataFormatVersions.Current})
              THEN RAISE(ABORT, 'verification parent format mismatch') END;
            END;
            CREATE TRIGGER IF NOT EXISTS RequireCurrentVerificationFormatForGeneration
            BEFORE INSERT ON VerificationPlanGenerationCommands BEGIN
              SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM EngineeringTasks WHERE Id = NEW.TaskId
                  AND VerificationDataFormatVersion = {VerificationDataFormatVersions.Current})
              THEN RAISE(ABORT, 'verification parent format mismatch') END;
            END;
            CREATE TRIGGER IF NOT EXISTS RequireCurrentVerificationFormatForAttempt
            BEFORE INSERT ON ManualVerificationAttempts BEGIN
              SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM EngineeringTasks WHERE Id = NEW.TaskId
                  AND VerificationDataFormatVersion = {VerificationDataFormatVersions.Current})
              THEN RAISE(ABORT, 'verification parent format mismatch') END;
            END;
            CREATE TRIGGER IF NOT EXISTS RequireCurrentVerificationFormatForResult
            BEFORE INSERT ON ManualCaseResultRevisions BEGIN
              SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM EngineeringTasks WHERE Id = NEW.TaskId
                  AND VerificationDataFormatVersion = {VerificationDataFormatVersions.Current})
              THEN RAISE(ABORT, 'verification parent format mismatch') END;
            END;
            CREATE TRIGGER IF NOT EXISTS RequireCurrentVerificationFormatForBinding
            BEFORE INSERT ON VerificationCommandBindings BEGIN
              SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM EngineeringTasks WHERE Id = NEW.TaskId
                  AND VerificationDataFormatVersion = {VerificationDataFormatVersions.Current})
              THEN RAISE(ABORT, 'verification parent format mismatch') END;
            END;
            CREATE TRIGGER IF NOT EXISTS PreventVerificationPlanTaskReassignment
            BEFORE UPDATE OF TaskId ON VerificationPlans
            WHEN NEW.TaskId <> OLD.TaskId BEGIN
              SELECT RAISE(ABORT, 'verification child ownership is immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS PreventVerificationGenerationTaskReassignment
            BEFORE UPDATE OF TaskId ON VerificationPlanGenerationCommands
            WHEN NEW.TaskId <> OLD.TaskId BEGIN
              SELECT RAISE(ABORT, 'verification child ownership is immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS PreventManualVerificationAttemptTaskReassignment
            BEFORE UPDATE OF TaskId ON ManualVerificationAttempts
            WHEN NEW.TaskId <> OLD.TaskId BEGIN
              SELECT RAISE(ABORT, 'verification child ownership is immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS PreventManualCaseResultTaskReassignment
            BEFORE UPDATE OF TaskId ON ManualCaseResultRevisions
            WHEN NEW.TaskId <> OLD.TaskId BEGIN
              SELECT RAISE(ABORT, 'verification child ownership is immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS PreventVerificationBindingTaskReassignment
            BEFORE UPDATE OF TaskId ON VerificationCommandBindings
            WHEN NEW.TaskId <> OLD.TaskId BEGIN
              SELECT RAISE(ABORT, 'verification child ownership is immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS PreventFailureAnalysisTaskReassignment
            BEFORE UPDATE OF TaskId ON FailureAnalyses WHEN NEW.TaskId <> OLD.TaskId BEGIN
              SELECT RAISE(ABORT, 'correction child ownership is immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS RequireCurrentCorrectionFormatForAnalysis
            BEFORE INSERT ON FailureAnalyses BEGIN
              SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM EngineeringTasks WHERE Id = NEW.TaskId AND CorrectionDataFormatVersion = 1)
              THEN RAISE(ABORT, 'correction parent format mismatch') END;
            END;
            CREATE TRIGGER IF NOT EXISTS RequireCurrentCorrectionFormatForAnalysisCommand
            BEFORE INSERT ON FailureAnalysisGenerationCommands BEGIN
              SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM EngineeringTasks WHERE Id = NEW.TaskId AND CorrectionDataFormatVersion = 1)
              THEN RAISE(ABORT, 'correction parent format mismatch') END;
            END;
            CREATE TRIGGER IF NOT EXISTS RequireCurrentCorrectionFormatForProposal
            BEFORE INSERT ON CorrectionProposals BEGIN
              SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM EngineeringTasks WHERE Id = NEW.TaskId AND CorrectionDataFormatVersion = 1)
              THEN RAISE(ABORT, 'correction parent format mismatch') END;
            END;
            CREATE TRIGGER IF NOT EXISTS RequireCurrentCorrectionFormatForApproval
            BEFORE INSERT ON CorrectionApprovalCommands BEGIN
              SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM EngineeringTasks WHERE Id = NEW.TaskId AND CorrectionDataFormatVersion = 1)
              THEN RAISE(ABORT, 'correction parent format mismatch') END;
            END;
            CREATE TRIGGER IF NOT EXISTS RequireCurrentCorrectionFormatForGeneration
            BEFORE INSERT ON CorrectionGenerationCommands BEGIN
              SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM EngineeringTasks WHERE Id = NEW.TaskId AND CorrectionDataFormatVersion = 1)
              THEN RAISE(ABORT, 'correction parent format mismatch') END;
            END;
            CREATE TRIGGER IF NOT EXISTS RequireCurrentCorrectionFormatForReconciliation
            BEFORE INSERT ON CorrectionReconciliationCommands BEGIN
              SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM EngineeringTasks WHERE Id = NEW.TaskId AND CorrectionDataFormatVersion = 1)
              THEN RAISE(ABORT, 'correction parent format mismatch') END;
            END;
            CREATE TRIGGER IF NOT EXISTS PreventFailureAnalysisCommandTaskReassignment
            BEFORE UPDATE OF TaskId ON FailureAnalysisGenerationCommands WHEN NEW.TaskId <> OLD.TaskId BEGIN
              SELECT RAISE(ABORT, 'correction child ownership is immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS PreventCorrectionProposalTaskReassignment
            BEFORE UPDATE OF TaskId ON CorrectionProposals WHEN NEW.TaskId <> OLD.TaskId BEGIN
              SELECT RAISE(ABORT, 'correction child ownership is immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS PreventCorrectionApprovalTaskReassignment
            BEFORE UPDATE OF TaskId ON CorrectionApprovalCommands WHEN NEW.TaskId <> OLD.TaskId BEGIN
              SELECT RAISE(ABORT, 'correction child ownership is immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS PreventCorrectionGenerationTaskReassignment
            BEFORE UPDATE OF TaskId ON CorrectionGenerationCommands WHEN NEW.TaskId <> OLD.TaskId BEGIN
              SELECT RAISE(ABORT, 'correction child ownership is immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS PreventDeliveryProposalTaskReassignment
            BEFORE UPDATE OF TaskId ON DeliveryProposals WHEN NEW.TaskId <> OLD.TaskId BEGIN
              SELECT RAISE(ABORT, 'delivery child ownership is immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS PreventDeliveryApprovalTaskReassignment
            BEFORE UPDATE OF TaskId ON DeliveryApprovalCommands WHEN NEW.TaskId <> OLD.TaskId BEGIN
              SELECT RAISE(ABORT, 'delivery child ownership is immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS PreventDeliveryAttemptTaskReassignment
            BEFORE UPDATE OF TaskId ON DeliveryAttempts WHEN NEW.TaskId <> OLD.TaskId BEGIN
              SELECT RAISE(ABORT, 'delivery child ownership is immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS PreventDeliveryBindingTaskReassignment
            BEFORE UPDATE OF TaskId ON DeliveryCommandBindings WHEN NEW.TaskId <> OLD.TaskId BEGIN
              SELECT RAISE(ABORT, 'delivery child ownership is immutable');
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureSchemaObjectAsync(connection, "TRIGGER", "RequireCurrentDeliveryFormatForProposal", $"""
            CREATE TRIGGER RequireCurrentDeliveryFormatForProposal
            BEFORE INSERT ON DeliveryProposals BEGIN
              SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM EngineeringTasks WHERE Id = NEW.TaskId AND DeliveryDataFormatVersion IN (1, 2))
              THEN RAISE(ABORT, 'delivery parent format mismatch') END;
            END;
            """, cancellationToken);
        await EnsureSchemaObjectAsync(connection, "TRIGGER", "RequireCurrentDeliveryFormatForApproval", $"""
            CREATE TRIGGER RequireCurrentDeliveryFormatForApproval
            BEFORE INSERT ON DeliveryApprovalCommands BEGIN
              SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM EngineeringTasks WHERE Id = NEW.TaskId AND DeliveryDataFormatVersion IN (1, 2))
              THEN RAISE(ABORT, 'delivery parent format mismatch') END;
            END;
            """, cancellationToken);
        await EnsureSchemaObjectAsync(connection, "TRIGGER", "RequireCurrentDeliveryFormatForAttempt", $"""
            CREATE TRIGGER RequireCurrentDeliveryFormatForAttempt
            BEFORE INSERT ON DeliveryAttempts BEGIN
              SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM EngineeringTasks WHERE Id = NEW.TaskId AND DeliveryDataFormatVersion IN (1, 2))
              THEN RAISE(ABORT, 'delivery parent format mismatch') END;
            END;
            """, cancellationToken);
        await EnsureSchemaObjectAsync(connection, "TRIGGER", "RequireCurrentDeliveryFormatForBinding", $"""
            CREATE TRIGGER RequireCurrentDeliveryFormatForBinding
            BEFORE INSERT ON DeliveryCommandBindings BEGIN
              SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM EngineeringTasks WHERE Id = NEW.TaskId AND DeliveryDataFormatVersion IN (1, 2))
              THEN RAISE(ABORT, 'delivery parent format mismatch') END;
            END;
            """, cancellationToken);
        await EnsureColumnAsync(connection, "RowVersion", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureIndexAsync(connection, cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(EngineeringTasks);";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase)) return;
        }

        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE EngineeringTasks ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureTableColumnAsync(SqliteConnection connection, string tableName,
        string columnName, string columnDefinition, CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase)) return;
        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_EngineeringTasks_UpdatedAt_Id
            ON EngineeringTasks (UpdatedAt DESC, Id ASC);
            CREATE INDEX IF NOT EXISTS IX_FailureAnalyses_TaskId_AnalysisNumber
            ON FailureAnalyses (TaskId, AnalysisNumber);
            CREATE INDEX IF NOT EXISTS IX_FailureAnalysisGenerationCommands_TaskId
            ON FailureAnalysisGenerationCommands (TaskId);
            CREATE INDEX IF NOT EXISTS IX_CorrectionProposals_TaskId_ProposalNumber
            ON CorrectionProposals (TaskId, ProposalNumber);
            CREATE INDEX IF NOT EXISTS IX_CorrectionApprovalCommands_TaskId
            ON CorrectionApprovalCommands (TaskId);
            CREATE INDEX IF NOT EXISTS IX_CorrectionGenerationCommands_TaskId
            ON CorrectionGenerationCommands (TaskId);
            CREATE INDEX IF NOT EXISTS IX_CorrectionReconciliationCommands_TaskId
            ON CorrectionReconciliationCommands (TaskId);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureSchemaObjectAsync(connection, "INDEX", "UX_CorrectionGenerationCommands_ActiveTaskProposal", """
            CREATE UNIQUE INDEX UX_CorrectionGenerationCommands_ActiveTaskProposal
            ON CorrectionGenerationCommands (TaskId, ProposalId)
            WHERE Status IN ('Prepared','DispatchMayHaveStarted','ResponseReceived','OutputAccepted',
                'CheckoutVerified','RevisionReserved','WorkspacePreparing','WorkspacePrepared',
                'MutationStarted','ApplyCompleted','ResultPersisted','AmbiguousAfterDispatch','InterruptedAfterResponse','RecoveryRequired');
            """, cancellationToken);
    }

    private static async Task EnsureSchemaObjectAsync(
        SqliteConnection connection,
        string objectType,
        string objectName,
        string definition,
        CancellationToken cancellationToken)
    {
        if (objectType is not ("TRIGGER" or "INDEX"))
            throw new ArgumentOutOfRangeException(nameof(objectType));

        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT sql FROM sqlite_schema WHERE type = $type AND name = $name;";
        inspect.Parameters.AddWithValue("$type", objectType.ToLowerInvariant());
        inspect.Parameters.AddWithValue("$name", objectName);
        var existing = await inspect.ExecuteScalarAsync(cancellationToken) as string;
        if (existing is not null && string.Equals(NormalizeSchemaSql(existing), NormalizeSchemaSql(definition),
                StringComparison.OrdinalIgnoreCase)) return;

        await using var transaction = connection.BeginTransaction();
        await using var replace = connection.CreateCommand();
        replace.Transaction = transaction;
        replace.CommandText = existing is null
            ? definition
            : $"DROP {objectType} IF EXISTS \"{objectName.Replace("\"", "\"\"")}\";\n{definition}";
        await replace.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string NormalizeSchemaSql(string value) => string.Join(' ', value
        .Replace("IF NOT EXISTS", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Trim().TrimEnd(';')
        .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
