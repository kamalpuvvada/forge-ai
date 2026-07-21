using Forge.Core;
using Forge.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Forge.Core.Tests;

public sealed class VerificationPersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"forge-verification-db-{Guid.NewGuid():N}");
    private string ConnectionString => $"Data Source={Path.Combine(directory, "forge.db")}";
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Plan_attempt_and_append_only_results_round_trip_in_normalized_tables()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = VerificationWorkflowTests.ApprovedImplementation(approve: false);
        await repository.SaveAsync(task);
        var pendingRevision = task.ImplementationRevisions.Single();
        task = await repository.ApproveImplementationAsync(new ImplementationApprovalCommand(
            Guid.NewGuid(), task.Id, task.RowVersion, pendingRevision.RevisionId,
            pendingRevision.ResultFingerprint!), Now.AddMinutes(3));
        var service = Service(repository);
        var revision = task.ImplementationRevisions.Single(item => item.RevisionId == task.ApprovedImplementationRevisionId);
        task = await service.GeneratePlanAsync(new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id,
            task.RowVersion, revision.RevisionId, revision.ResultFingerprint!));
        var plan = Assert.Single(task.VerificationPlans);
        task = await service.StartAttemptAsync(new StartManualVerificationCommand(Guid.NewGuid(), task.Id,
            task.RowVersion, plan.PlanId, plan.PlanFingerprint, revision.RevisionId, revision.ResultFingerprint!));
        var attempt = Assert.Single(task.ManualVerificationAttempts);
        var testCase = plan.TestCases[0];
        var updateCommandId = Guid.NewGuid();
        var update = new UpdateManualVerificationCaseCommand(updateCommandId, task.Id, attempt.AttemptId,
            testCase.TestCaseId, task.RowVersion, plan.PlanId, plan.PlanFingerprint, revision.RevisionId,
            revision.ResultFingerprint!, ManualVerificationCaseResult.Passed, "Observed manually.",
            "Expected behavior observed.", ["Recorded a safe textual observation."], null, null);
        task = await service.UpdateCaseAsync(update);
        var replayed = await service.UpdateCaseAsync(update);

        Assert.Single(replayed.ManualVerificationAttempts.Single().ResultRevisions);
        var loaded = await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id);
        Assert.Equal(WorkflowStatus.AwaitingManualVerification, loaded?.Status);
        Assert.Equal(plan.PlanFingerprint, Assert.Single(loaded!.VerificationPlans).PlanFingerprint);
        Assert.Single(Assert.Single(loaded.ManualVerificationAttempts).ResultRevisions);

        await Assert.ThrowsAsync<TaskConcurrencyException>(() => service.UpdateCaseAsync(update with
        {
            Result = ManualVerificationCaseResult.NotApplicable,
            NotApplicableReason = "Different semantic replay."
        }));
    }

    [Fact]
    public async Task Additive_schema_contains_verification_tables_and_pointers()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }
        Assert.Contains("VerificationPlans", tables);
        Assert.Contains("VerificationPlanGenerationCommands", tables);
        Assert.Contains("ManualVerificationAttempts", tables);
        Assert.Contains("ManualCaseResultRevisions", tables);
        Assert.Contains("VerificationCommandBindings", tables);
        await using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(EngineeringTasks);";
        await using var columnReader = await columns.ExecuteReaderAsync();
        var names = new List<string>();
        while (await columnReader.ReadAsync()) names.Add(columnReader.GetString(1));
        Assert.Contains("CurrentVerificationPlanId", names);
        Assert.Contains("CurrentVerificationAttemptId", names);
        Assert.Contains("VerificationDataFormatVersion", names);
    }

    [Theory]
    [InlineData("$.providerResponses", "null")]
    [InlineData("$.logicalCalls", "null")]
    [InlineData("$.modelCallIds", "null")]
    [InlineData("$.providerResponses[0]", "null")]
    [InlineData("$.logicalCalls[0]", "null")]
    [InlineData("$.status", "999")]
    [InlineData("$.startedAt", "\"not-a-timestamp\"")]
    [InlineData("$.taskId", "\"11111111-1111-1111-1111-111111111111\"")]
    public async Task Malformed_generation_collections_enums_timestamps_and_ownership_fail_as_safe_corruption(
        string jsonPath, string jsonValue)
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var command = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);
        await repository.BeginPlanGenerationAsync(command, Now.AddMinutes(4));
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE VerificationPlanGenerationCommands SET Json = json_set(Json, $path, json($value)) WHERE CommandId = $id;";
            update.Parameters.AddWithValue("$path", jsonPath);
            update.Parameters.AddWithValue("$value", jsonValue);
            update.Parameters.AddWithValue("$id", command.CommandId.ToString("D"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var exception = await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id));
        Assert.StartsWith("Stored ", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ConnectionString, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Legacy_task_without_verification_rows_remains_readable_without_rewrite()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = VerificationWorkflowTests.ApprovedImplementation(approve: false);
        await repository.SaveAsync(task);
        var before = (await repository.GetAsync(task.Id))!;

        var after = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id))!;

        Assert.Empty(after.VerificationPlans);
        Assert.Empty(after.VerificationPlanGenerationAttempts);
        Assert.Equal(VerificationDataFormatVersions.Legacy, after.VerificationDataFormatVersion);
        Assert.Equal(before.RowVersion, after.RowVersion);
    }

    [Theory]
    [InlineData("VerificationPlans")]
    [InlineData("VerificationPlanGenerationCommands")]
    [InlineData("ManualVerificationAttempts")]
    [InlineData("ManualCaseResultRevisions")]
    [InlineData("VerificationCommandBindings")]
    public async Task Verification_child_ownership_is_immutable_and_integrity_remains_valid(string table)
    {
        var initializer = new SqliteDatabaseInitializer(ConnectionString);
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var source = EngineeringTask.Create("C:/source", "Source", Now);
        var legacyDestination = EngineeringTask.Create("C:/legacy", "Legacy", Now);
        var currentDestination = EngineeringTask.Create("C:/current", "Current", Now);
        await repository.SaveAsync(source);
        await repository.SaveAsync(legacyDestination);
        await repository.SaveAsync(currentDestination);
        var childId = Guid.NewGuid();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = $"""
                UPDATE EngineeringTasks SET VerificationDataFormatVersion = {VerificationDataFormatVersions.Current}
                WHERE Id IN ($sourceId, $currentId);
                {table switch
                {
                    "VerificationPlans" => """
                        INSERT INTO VerificationPlans
                          (PlanId, TaskId, PlanNumber, ImplementationRevisionId, ImplementationResultFingerprint,
                           ApprovedRequirementFingerprint, ApprovedPlanFingerprint, PlanFingerprint, Status, GeneratedAt, Json)
                        VALUES ($childId, $sourceId, 1, $otherId, $sha, $sha, $sha, $sha, 'Current', $now, '{}');
                        """,
                    "VerificationPlanGenerationCommands" => """
                        INSERT INTO VerificationPlanGenerationCommands (CommandId, TaskId, Status, StartedAt, Json)
                        VALUES ($childId, $sourceId, 'Prepared', $now, '{}');
                        """,
                    "ManualVerificationAttempts" => """
                        INSERT INTO ManualVerificationAttempts
                          (AttemptId, TaskId, AttemptNumber, PlanId, ImplementationRevisionId,
                           ImplementationResultFingerprint, Status, StartedAt, Json)
                        VALUES ($childId, $sourceId, 1, $otherId, $thirdId, $sha, 'InProgress', $now, '{}');
                        """,
                    "ManualCaseResultRevisions" => """
                        INSERT INTO ManualCaseResultRevisions
                          (ResultRevisionId, TaskId, AttemptId, TestCaseId, RevisionNumber, Result, RecordedAt, Json)
                        VALUES ($childId, $sourceId, $otherId, $thirdId, 1, 'Passed', $now, '{}');
                        """,
                    "VerificationCommandBindings" => """
                        INSERT INTO VerificationCommandBindings (CommandId, TaskId, CommandType, SemanticFingerprint)
                        VALUES ($childId, $sourceId, 'GenerateVerificationPlan', $sha);
                        """,
                    _ => throw new InvalidOperationException("Unknown table.")
                }}
                """;
            setup.Parameters.AddWithValue("$sourceId", source.Id.ToString("D"));
            setup.Parameters.AddWithValue("$currentId", currentDestination.Id.ToString("D"));
            setup.Parameters.AddWithValue("$childId", childId.ToString("D"));
            setup.Parameters.AddWithValue("$otherId", Guid.NewGuid().ToString("D"));
            setup.Parameters.AddWithValue("$thirdId", Guid.NewGuid().ToString("D"));
            setup.Parameters.AddWithValue("$sha", new string('a', 64));
            setup.Parameters.AddWithValue("$now", Now.ToString("O"));
            await setup.ExecuteNonQueryAsync();
        }

        foreach (var destination in new[]
                 {
                     legacyDestination.Id.ToString("D"), currentDestination.Id.ToString("D"), Guid.NewGuid().ToString("D")
                 })
        {
            await using var reassign = connection.CreateCommand();
            reassign.CommandText = $"UPDATE {table} SET TaskId = $destination WHERE TaskId = $sourceId;";
            reassign.Parameters.AddWithValue("$destination", destination);
            reassign.Parameters.AddWithValue("$sourceId", source.Id.ToString("D"));
            var exception = await Assert.ThrowsAsync<SqliteException>(() => reassign.ExecuteNonQueryAsync());
            Assert.Contains("ownership is immutable", exception.Message, StringComparison.Ordinal);
        }

        await using (var unchanged = connection.CreateCommand())
        {
            var unrelatedColumn = table switch
            {
                "VerificationPlans" or "VerificationPlanGenerationCommands" or
                    "ManualVerificationAttempts" => "Status",
                "ManualCaseResultRevisions" => "Result",
                "VerificationCommandBindings" => "CommandType",
                _ => throw new InvalidOperationException("Unknown table.")
            };
            unchanged.CommandText = $"UPDATE {table} SET {unrelatedColumn} = {unrelatedColumn} WHERE TaskId = $sourceId;";
            unchanged.Parameters.AddWithValue("$sourceId", source.Id.ToString("D"));
            Assert.Equal(1, await unchanged.ExecuteNonQueryAsync());
        }
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", await integrity.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("generation")]
    [InlineData("provider-response")]
    [InlineData("logical-call")]
    [InlineData("manual-attempt")]
    [InlineData("result-revision")]
    [InlineData("plan-pointer")]
    [InlineData("attempt-pointer")]
    [InlineData("verification-model-call")]
    [InlineData("command-binding")]
    [InlineData("verification-status")]
    public async Task Version_zero_is_rejected_when_any_verification_artifact_exists(string artifact)
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = EngineeringTask.Create("C:/repository", "Safe legacy task", Now);
        await repository.SaveAsync(task);
        var childId = Guid.NewGuid();
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var mutation = connection.CreateCommand();
            mutation.CommandText = artifact switch
            {
                "plan" => ChildMutation("""
                    INSERT INTO VerificationPlans
                      (PlanId, TaskId, PlanNumber, ImplementationRevisionId, ImplementationResultFingerprint,
                       ApprovedRequirementFingerprint, ApprovedPlanFingerprint, PlanFingerprint, Status, GeneratedAt, Json)
                    VALUES ($childId, $taskId, 1, $otherId, $sha, $sha, $sha, $sha, 'Current', $now, '{}');
                    """),
                "generation" => ChildMutation("""
                    INSERT INTO VerificationPlanGenerationCommands (CommandId, TaskId, Status, StartedAt, Json)
                    VALUES ($childId, $taskId, 'Prepared', $now, '{}');
                    """),
                "provider-response" => ChildMutation("""
                    INSERT INTO VerificationPlanGenerationCommands (CommandId, TaskId, Status, StartedAt, Json)
                    VALUES ($childId, $taskId, 'ResponseReceived', $now, '{"providerResponses":[{}]}');
                    """),
                "logical-call" => ChildMutation("""
                    INSERT INTO VerificationPlanGenerationCommands (CommandId, TaskId, Status, StartedAt, Json)
                    VALUES ($childId, $taskId, 'Prepared', $now, '{"logicalCalls":[{}]}');
                    """),
                "manual-attempt" => ChildMutation("""
                    INSERT INTO ManualVerificationAttempts
                      (AttemptId, TaskId, AttemptNumber, PlanId, ImplementationRevisionId,
                       ImplementationResultFingerprint, Status, StartedAt, Json)
                    VALUES ($childId, $taskId, 1, $otherId, $otherId, $sha, 'InProgress', $now, '{}');
                    """),
                "result-revision" => ChildMutation("""
                    INSERT INTO ManualCaseResultRevisions
                      (ResultRevisionId, TaskId, AttemptId, TestCaseId, RevisionNumber, Result, RecordedAt, Json)
                    VALUES ($childId, $taskId, $otherId, $thirdId, 1, 'Passed', $now, '{}');
                    """),
                "command-binding" => ChildMutation("""
                    INSERT INTO VerificationCommandBindings
                      (CommandId, TaskId, CommandType, SemanticFingerprint)
                    VALUES ($childId, $taskId, 'GenerateVerificationPlan', $sha);
                    """),
                "plan-pointer" => "UPDATE EngineeringTasks SET CurrentVerificationPlanId = $childId WHERE Id = $taskId;",
                "attempt-pointer" => "UPDATE EngineeringTasks SET CurrentVerificationAttemptId = $childId WHERE Id = $taskId;",
                "verification-model-call" => "UPDATE EngineeringTasks SET ModelCalls = $modelCalls WHERE Id = $taskId;",
                "verification-status" => "UPDATE EngineeringTasks SET Status = 'VerificationPlanning' WHERE Id = $taskId;",
                _ => throw new InvalidOperationException("Unknown verification artifact.")
            };
            mutation.Parameters.AddWithValue("$taskId", task.Id.ToString("D"));
            mutation.Parameters.AddWithValue("$childId", childId.ToString("D"));
            mutation.Parameters.AddWithValue("$otherId", Guid.NewGuid().ToString("D"));
            mutation.Parameters.AddWithValue("$thirdId", Guid.NewGuid().ToString("D"));
            mutation.Parameters.AddWithValue("$sha", new string('a', 64));
            mutation.Parameters.AddWithValue("$now", Now.ToString("O"));
            mutation.Parameters.AddWithValue("$modelCalls", JsonSerializer.Serialize(new[]
            {
                new ModelCallRecord(Guid.NewGuid(), ModelCallStage.VerificationPlanning, "Fake", "fake", "none",
                    Now, Now, false, null, null, null, null, null, null, "safe_failure")
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.True(await mutation.ExecuteNonQueryAsync() >= 1);
        }

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(VerificationDataFormatVersions.Current)]
    public async Task Invalid_or_empty_current_verification_parent_versions_are_rejected(int version)
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = EngineeringTask.Create("C:/repository", "Safe task", Now);
        await repository.SaveAsync(task);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE EngineeringTasks SET VerificationDataFormatVersion = $version WHERE Id = $taskId;";
        update.Parameters.AddWithValue("$version", version);
        update.Parameters.AddWithValue("$taskId", task.Id.ToString("D"));
        Assert.Equal(1, await update.ExecuteNonQueryAsync());

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id));
    }

    [Fact]
    public async Task Current_verification_command_binding_is_validated_when_the_task_is_read()
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var command = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);
        await repository.BeginPlanGenerationAsync(command, Now.AddMinutes(4));
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE VerificationCommandBindings SET SemanticFingerprint = $invalid WHERE CommandId = $id;";
            update.Parameters.AddWithValue("$invalid", new string('A', 64));
            update.Parameters.AddWithValue("$id", command.CommandId.ToString("D"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id));
    }

    [Theory]
    [InlineData("response-usage")]
    [InlineData("response-fingerprint")]
    [InlineData("response-version")]
    [InlineData("model-usage")]
    [InlineData("all-nested-markers")]
    [InlineData("coordinated-downgrade")]
    [InlineData("parent-downgrade")]
    [InlineData("parent-future")]
    public async Task Current_format_marker_or_parent_tampering_cannot_select_legacy_compatibility(string mutation)
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var generated = await OpenAIService(repository, new VerificationQueueGateway("success"))
            .GeneratePlanAsync(new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
                revision.RevisionId, revision.ResultFingerprint!));
        Assert.Equal(VerificationDataFormatVersions.Current, generated.VerificationDataFormatVersion);
        var commandId = Assert.Single(generated.VerificationPlanGenerationAttempts).CommandId;

        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = mutation switch
            {
                "response-usage" => "UPDATE VerificationPlanGenerationCommands SET Json = json_remove(Json, '$.providerResponses[0].usageAvailability') WHERE CommandId = $id;",
                "response-fingerprint" => "UPDATE VerificationPlanGenerationCommands SET Json = json_remove(Json, '$.providerResponses[0].telemetryFingerprint') WHERE CommandId = $id;",
                "response-version" => "UPDATE VerificationPlanGenerationCommands SET Json = json_remove(Json, '$.providerResponses[0].formatVersion') WHERE CommandId = $id;",
                "model-usage" => "UPDATE EngineeringTasks SET ModelCalls = json_remove(ModelCalls, '$[0].providerUsageAvailability') WHERE Id = $taskId;",
                "all-nested-markers" => """
                    UPDATE VerificationPlanGenerationCommands SET Json = json_remove(Json,
                        '$.providerResponses[0].usageAvailability', '$.providerResponses[0].telemetryFingerprint',
                        '$.providerResponses[0].formatVersion') WHERE CommandId = $id;
                    UPDATE EngineeringTasks SET ModelCalls = json_remove(ModelCalls,
                        '$[0].providerUsageAvailability') WHERE Id = $taskId;
                    """,
                "coordinated-downgrade" => """
                    UPDATE VerificationPlanGenerationCommands SET Json = json_remove(Json,
                        '$.providerResponses[0].usageAvailability', '$.providerResponses[0].telemetryFingerprint',
                        '$.providerResponses[0].formatVersion') WHERE CommandId = $id;
                    UPDATE EngineeringTasks SET VerificationDataFormatVersion = 0,
                        ModelCalls = json_remove(ModelCalls, '$[0].providerUsageAvailability') WHERE Id = $taskId;
                    """,
                "parent-downgrade" => "UPDATE EngineeringTasks SET VerificationDataFormatVersion = 0 WHERE Id = $taskId;",
                "parent-future" => "UPDATE EngineeringTasks SET VerificationDataFormatVersion = 99 WHERE Id = $taskId;",
                _ => throw new InvalidOperationException("Unknown test mutation.")
            };
            update.Parameters.AddWithValue("$id", commandId.ToString("D"));
            update.Parameters.AddWithValue("$taskId", task.Id.ToString("D"));
            Assert.True(await update.ExecuteNonQueryAsync() >= 1);
        }

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id));
    }

    private static string ChildMutation(string insert) => $"""
        UPDATE EngineeringTasks SET VerificationDataFormatVersion = {VerificationDataFormatVersions.Current}
        WHERE Id = $taskId;
        {insert}
        UPDATE EngineeringTasks SET VerificationDataFormatVersion = {VerificationDataFormatVersions.Legacy}
        WHERE Id = $taskId;
        """;

    [Theory]
    [InlineData("ClarificationAnswers", "{")]
    [InlineData("ModelCalls", "{}")]
    [InlineData("ImplementationRevisions", "[")]
    [InlineData("EvidenceItems", "null")]
    [InlineData("RequirementRevisionNotes", "[null]")]
    public async Task Malformed_main_task_json_is_translated_to_safe_corruption(string column, string malformedValue)
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = EngineeringTask.Create("C:/repository", "Safe task", Now);
        await repository.SaveAsync(task);
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = $"UPDATE EngineeringTasks SET {column} = $value WHERE Id = $id;";
            update.Parameters.AddWithValue("$value", malformedValue);
            update.Parameters.AddWithValue("$id", task.Id.ToString("D"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var exception = await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id));
        Assert.DoesNotContain(malformedValue, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ConnectionString, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Oversized_utf8_main_task_json_is_rejected_before_deserialization()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = EngineeringTask.Create("C:/repository", "Safe task", Now);
        await repository.SaveAsync(task);
        var oversizedUtf8 = JsonSerializer.Serialize(new[] { new string('\u20ac', 350_000) });
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE EngineeringTasks SET ClarificationAnswers = $value WHERE Id = $id;";
            update.Parameters.AddWithValue("$value", oversizedUtf8);
            update.Parameters.AddWithValue("$id", task.Id.ToString("D"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id));
    }

    [Fact]
    public async Task Duplicate_global_model_call_ids_fail_as_safe_corruption_before_lookup()
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var generated = await OpenAIService(repository, new VerificationQueueGateway("success"))
            .GeneratePlanAsync(new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
                revision.RevisionId, revision.ResultFingerprint!));
        Assert.Single(generated.ModelCalls);
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE EngineeringTasks SET ModelCalls = json_insert(ModelCalls, '$[#]', json_extract(ModelCalls, '$[0]')) WHERE Id = $id;";
            update.Parameters.AddWithValue("$id", task.Id.ToString("D"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id));
    }

    [Fact]
    public async Task Current_parent_version_prevents_nested_field_deletion_downgrade_and_rejects_boolean_contradiction()
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var generated = await OpenAIService(repository, new VerificationQueueGateway("success"))
            .GeneratePlanAsync(new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
                revision.RevisionId, revision.ResultFingerprint!));
        var commandId = Assert.Single(generated.VerificationPlanGenerationAttempts).CommandId;
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var legacy = connection.CreateCommand();
            legacy.CommandText = """
                UPDATE VerificationPlanGenerationCommands
                SET Json = json_remove(Json, '$.providerResponses[0].usageAvailability', '$.providerResponses[0].telemetryFingerprint')
                WHERE CommandId = $id;
                UPDATE EngineeringTasks
                SET ModelCalls = json_remove(ModelCalls, '$[0].providerUsageAvailability')
                WHERE Id = $taskId;
                """;
            legacy.Parameters.AddWithValue("$id", commandId.ToString("D"));
            legacy.Parameters.AddWithValue("$taskId", task.Id.ToString("D"));
            await legacy.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id));

        var (secondRepository, secondTask, secondRevision) = await ApprovedPersistedTaskAsync();
        var secondGenerated = await OpenAIService(secondRepository, new VerificationQueueGateway("success"))
            .GeneratePlanAsync(new VerificationPlanGenerationCommand(Guid.NewGuid(), secondTask.Id,
                secondTask.RowVersion, secondRevision.RevisionId, secondRevision.ResultFingerprint!));
        var secondCommandId = Assert.Single(secondGenerated.VerificationPlanGenerationAttempts).CommandId;

        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var current = connection.CreateCommand();
            current.CommandText = "UPDATE VerificationPlanGenerationCommands SET Json = json_set(Json, '$.providerResponses[0].usageAvailability', 'Complete', '$.providerResponses[0].usageAvailable', json('false')) WHERE CommandId = $id;";
            current.Parameters.AddWithValue("$id", secondCommandId.ToString("D"));
            Assert.Equal(1, await current.ExecuteNonQueryAsync());
        }
        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(secondTask.Id));
    }

    [Fact]
    public async Task Coordinated_response_and_model_timing_tampering_is_rejected_by_telemetry_fingerprint()
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var generated = await OpenAIService(repository, new VerificationQueueGateway("success"))
            .GeneratePlanAsync(new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
                revision.RevisionId, revision.ResultFingerprint!));
        var generation = Assert.Single(generated.VerificationPlanGenerationAttempts);
        var changed = generation.StartedAt.AddSeconds(2).ToString("O");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE VerificationPlanGenerationCommands
            SET Json = json_set(Json, '$.logicalCalls[0].startedAt', $changed, '$.providerResponses[0].startedAt', $changed)
            WHERE CommandId = $commandId;
            UPDATE EngineeringTasks SET ModelCalls = json_set(ModelCalls, '$[0].startedAt', $changed) WHERE Id = $taskId;
            """;
        update.Parameters.AddWithValue("$changed", changed);
        update.Parameters.AddWithValue("$commandId", generation.CommandId.ToString("D"));
        update.Parameters.AddWithValue("$taskId", task.Id.ToString("D"));
        await update.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id));
    }

    [Theory]
    [InlineData("response-id")]
    [InlineData("request-id")]
    [InlineData("usage-counter")]
    [InlineData("usage-boolean")]
    [InlineData("child-version-mismatch")]
    public async Task Bound_provider_response_field_tampering_is_rejected(string mutation)
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var generated = await OpenAIService(repository, new VerificationQueueGateway("success"))
            .GeneratePlanAsync(new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
                revision.RevisionId, revision.ResultFingerprint!));
        var commandId = Assert.Single(generated.VerificationPlanGenerationAttempts).CommandId;
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = mutation switch
            {
                "response-id" => "UPDATE VerificationPlanGenerationCommands SET Json = json_set(Json, '$.providerResponses[0].providerResponseId', 'resp_changed') WHERE CommandId = $id;",
                "request-id" => "UPDATE VerificationPlanGenerationCommands SET Json = json_set(Json, '$.providerResponses[0].providerRequestId', 'req_changed') WHERE CommandId = $id;",
                "usage-counter" => "UPDATE VerificationPlanGenerationCommands SET Json = json_set(Json, '$.providerResponses[0].inputTokens', 102) WHERE CommandId = $id;",
                "usage-boolean" => "UPDATE VerificationPlanGenerationCommands SET Json = json_set(Json, '$.providerResponses[0].usageAvailable', json('false')) WHERE CommandId = $id;",
                "child-version-mismatch" => "UPDATE VerificationPlanGenerationCommands SET Json = json_set(Json, '$.providerResponses[0].formatVersion', 99) WHERE CommandId = $id;",
                _ => throw new InvalidOperationException("Unknown test mutation.")
            };
            update.Parameters.AddWithValue("$id", commandId.ToString("D"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id));
    }

    [Theory]
    [InlineData("$.testCases", "null")]
    [InlineData("$.testCases[0].orderedSteps", "null")]
    [InlineData("$.testCases[0].evidenceRequirements", "null")]
    [InlineData("$.testCases[0].orderedSteps[0]", "null")]
    [InlineData("$.testCases[0].preconditions[0]", "null")]
    public async Task Malformed_plan_child_collections_fail_as_safe_corruption_without_affecting_another_task(
        string jsonPath, string jsonValue)
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var generated = await Service(repository).GeneratePlanAsync(new VerificationPlanGenerationCommand(
            Guid.NewGuid(), task.Id, task.RowVersion, revision.RevisionId, revision.ResultFingerprint!));
        var healthy = EngineeringTask.Create("C:/healthy", "Healthy unrelated task", Now);
        await repository.SaveAsync(healthy);
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE VerificationPlans SET Json = json_set(Json, $path, json($value)) WHERE PlanId = $id;";
            update.Parameters.AddWithValue("$path", jsonPath);
            update.Parameters.AddWithValue("$value", jsonValue);
            update.Parameters.AddWithValue("$id", generated.CurrentVerificationPlanId!.Value.ToString("D"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id));
        Assert.NotNull(await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(healthy.Id));
    }

    [Fact]
    public async Task Orphan_manual_result_row_is_rejected_before_grouping_or_projection()
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        task = await Service(repository).GeneratePlanAsync(new VerificationPlanGenerationCommand(
            Guid.NewGuid(), task.Id, task.RowVersion, revision.RevisionId, revision.ResultFingerprint!));
        var plan = Assert.Single(task.VerificationPlans);
        task = await Service(repository).StartAttemptAsync(new StartManualVerificationCommand(
            Guid.NewGuid(), task.Id, task.RowVersion, plan.PlanId, plan.PlanFingerprint,
            revision.RevisionId, revision.ResultFingerprint!));
        var attempt = Assert.Single(task.ManualVerificationAttempts);
        var testCase = plan.TestCases[0];
        task = await Service(repository).UpdateCaseAsync(new UpdateManualVerificationCaseCommand(
            Guid.NewGuid(), task.Id, attempt.AttemptId, testCase.TestCaseId, task.RowVersion,
            plan.PlanId, plan.PlanFingerprint, revision.RevisionId, revision.ResultFingerprint!,
            ManualVerificationCaseResult.Passed, "Observed manually.", "Expected result observed.",
            ["Safe evidence description."], null, null));
        var orphanAttemptId = Guid.NewGuid();

        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE ManualCaseResultRevisions
                SET AttemptId = $orphanAttemptId,
                    Json = json_set(Json, '$.attemptId', $orphanAttemptId)
                WHERE TaskId = $taskId;
                """;
            update.Parameters.AddWithValue("$orphanAttemptId", orphanAttemptId.ToString("D"));
            update.Parameters.AddWithValue("$taskId", task.Id.ToString("D"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id));
    }

    [Fact]
    public async Task Durable_dispatch_intent_survives_restart_blocks_new_commands_and_replays_original_without_dispatch()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = VerificationWorkflowTests.ApprovedImplementation(approve: false);
        await repository.SaveAsync(task);
        var pending = Assert.Single(task.ImplementationRevisions);
        task = await repository.ApproveImplementationAsync(new ImplementationApprovalCommand(
            Guid.NewGuid(), task.Id, task.RowVersion, pending.RevisionId, pending.ResultFingerprint!), Now.AddMinutes(3));
        var revision = task.ImplementationRevisions.Single(item => item.RevisionId == task.ApprovedImplementationRevisionId);
        var command = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);
        var begun = await repository.BeginPlanGenerationAsync(command, Now.AddMinutes(4));
        var physicalCall = Guid.NewGuid();
        await repository.RecordPlanGenerationCheckpointAsync(task.Id, command.CommandId,
            VerificationDispatchCheckpoint.DispatchMayHaveStarted, physicalCall, Now.AddMinutes(4).AddSeconds(1));
        var restarted = new SqliteEngineeringTaskRepository(ConnectionString);
        var loaded = await restarted.GetAsync(task.Id);
        var attempt = Assert.Single(loaded!.VerificationPlanGenerationAttempts);
        Assert.Equal(VerificationGenerationAttemptStatus.DispatchMayHaveStarted, attempt.Status);
        Assert.Equal(physicalCall, attempt.LastLogicalCallId);
        Assert.Equal(1, attempt.LogicalCallCount);
        Assert.Equal(0, attempt.PhysicalRequestCount);
        Assert.Empty(attempt.ModelCallIds);
        Assert.Empty(loaded.ModelCalls);

        var replay = await restarted.BeginPlanGenerationAsync(command, Now.AddHours(1));
        Assert.True(replay.Replayed);
        Assert.Single(replay.Task.VerificationPlanGenerationAttempts);
        var newCommand = command with { CommandId = Guid.NewGuid(), ExpectedRowVersion = replay.Task.RowVersion };
        await Assert.ThrowsAsync<WorkflowException>(() =>
            restarted.BeginPlanGenerationAsync(newCommand, Now.AddHours(1)));
        Assert.Equal(begun.Task.Id, replay.Task.Id);
    }

    [Fact]
    public async Task Response_phase_identity_and_unavailable_usage_are_committed_atomically_before_parsing()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = VerificationWorkflowTests.ApprovedImplementation(approve: false);
        await repository.SaveAsync(task);
        var pending = Assert.Single(task.ImplementationRevisions);
        task = await repository.ApproveImplementationAsync(new ImplementationApprovalCommand(
            Guid.NewGuid(), task.Id, task.RowVersion, pending.RevisionId, pending.ResultFingerprint!),
            Now.AddMinutes(3));
        var revision = task.ImplementationRevisions.Single(item => item.RevisionId == task.ApprovedImplementationRevisionId);
        var command = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);
        await repository.BeginPlanGenerationAsync(command, Now.AddMinutes(4));
        var logicalCallId = Guid.NewGuid();
        await repository.RecordPlanGenerationCheckpointAsync(task.Id, command.CommandId,
            VerificationDispatchCheckpoint.DispatchMayHaveStarted, logicalCallId, Now.AddMinutes(4).AddSeconds(1));
        var response = new VerificationProviderResponseTelemetry(logicalCallId, Now.AddMinutes(4).AddSeconds(1),
            Now.AddMinutes(4).AddSeconds(2),
            "resp_safe", "req_safe", VerificationProviderResponseStatus.Completed, null, false,
            null, null, null, null, 200, VerificationCallDispatchDisposition.ResponseReceived);

        await repository.RecordVerificationProviderResponseAsync(task.Id, command.CommandId, response,
            Now.AddMinutes(4).AddSeconds(2));

        var restarted = new SqliteEngineeringTaskRepository(ConnectionString);
        var loaded = (await restarted.GetAsync(task.Id))!;
        var attempt = Assert.Single(loaded.VerificationPlanGenerationAttempts);
        Assert.Equal(VerificationGenerationAttemptStatus.ResponseReceived, attempt.Status);
        Assert.Equal(1, attempt.LogicalCallCount);
        Assert.Equal(1, attempt.PhysicalRequestCount);
        Assert.Equal(0, attempt.PossiblyDispatchedRequestCount);
        var persisted = Assert.Single(attempt.ProviderResponses);
        Assert.Equal("resp_safe", persisted.ProviderResponseId);
        Assert.False(persisted.UsageAvailable);
        Assert.Null(persisted.InputTokens);
        Assert.Null(persisted.OutputTokens);
        Assert.Empty(loaded.ModelCalls);

        var replay = await restarted.BeginPlanGenerationAsync(command, Now.AddHours(1));
        Assert.True(replay.Replayed);
        Assert.Single(replay.Task.VerificationPlanGenerationAttempts);
        await Assert.ThrowsAsync<WorkflowException>(() => restarted.BeginPlanGenerationAsync(command with
            { CommandId = Guid.NewGuid(), ExpectedRowVersion = replay.Task.RowVersion }, Now.AddHours(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Atomic_response_transaction_faults_roll_back_every_persistent_change(int faultPointValue)
    {
        var faultPoint = (VerificationResponsePersistencePoint)faultPointValue;
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var command = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);
        await repository.BeginPlanGenerationAsync(command, Now.AddMinutes(4));
        var callId = Guid.NewGuid();
        var startedAt = Now.AddMinutes(4).AddSeconds(1);
        var dispatched = await repository.RecordPlanGenerationCheckpointAsync(task.Id, command.CommandId,
            VerificationDispatchCheckpoint.DispatchMayHaveStarted, callId, startedAt, default, startedAt);
        var rowVersionBeforeResponse = dispatched.RowVersion;
        var response = new VerificationProviderResponseTelemetry(callId, startedAt, startedAt.AddSeconds(1),
            "resp-safe", "req-safe", VerificationProviderResponseStatus.Completed, null, true,
            12, null, 7, null, 200, VerificationCallDispatchDisposition.ResponseReceived,
            VerificationUsageAvailability.Partial);
        var faulting = new SqliteEngineeringTaskRepository(ConnectionString, new VerificationLimits(),
            (point, _) => point == faultPoint
                ? Task.FromException(new InjectedRepositoryException(RepositoryFault.BeforeResponseCheckpoint))
                : Task.CompletedTask);

        await Assert.ThrowsAsync<InjectedRepositoryException>(() => faulting.RecordVerificationProviderResponseAsync(
            task.Id, command.CommandId, response, startedAt.AddSeconds(1)));

        var restarted = new SqliteEngineeringTaskRepository(ConnectionString);
        var persisted = (await restarted.GetAsync(task.Id))!;
        var attempt = Assert.Single(persisted.VerificationPlanGenerationAttempts);
        Assert.Equal(VerificationGenerationAttemptStatus.DispatchMayHaveStarted, attempt.Status);
        Assert.Empty(attempt.ProviderResponses);
        Assert.Equal(rowVersionBeforeResponse, persisted.RowVersion);
        Assert.Empty(persisted.ModelCalls);
        var replay = await restarted.BeginPlanGenerationAsync(command, Now.AddHours(1));
        Assert.True(replay.Replayed);
        Assert.Equal(rowVersionBeforeResponse, replay.Task.RowVersion);
        Assert.Empty(replay.Task.VerificationPlans);
        await Assert.ThrowsAsync<WorkflowException>(() => restarted.BeginPlanGenerationAsync(command with
        {
            CommandId = Guid.NewGuid(),
            ExpectedRowVersion = replay.Task.RowVersion
        }, Now.AddHours(1)));
        await using (var integrityConnection = new SqliteConnection(ConnectionString))
        {
            await integrityConnection.OpenAsync();
            await using var integrity = integrityConnection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", await integrity.ExecuteScalarAsync());
            await using var rawCheck = integrityConnection.CreateCommand();
            rawCheck.CommandText = "SELECT COUNT(*) FROM VerificationPlanGenerationCommands WHERE Json LIKE '%raw provider output%';";
            Assert.Equal(0L, await rawCheck.ExecuteScalarAsync());
        }
        Assert.NotEmpty(new TaskPdfExporter(new ModelCostResolver(new ModelCostCalculator(
            new Dictionary<string, ModelPricing>()))).Export(replay.Task));
    }

    [Fact]
    public async Task Atomic_response_success_preserves_exact_start_partial_usage_and_restart_identity()
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var command = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);
        await repository.BeginPlanGenerationAsync(command, Now.AddMinutes(4));
        var callId = Guid.NewGuid();
        var startedAt = Now.AddMinutes(4).AddTicks(1234);
        await repository.RecordPlanGenerationCheckpointAsync(task.Id, command.CommandId,
            VerificationDispatchCheckpoint.DispatchMayHaveStarted, callId, startedAt, default, startedAt);
        await repository.RecordVerificationProviderResponseAsync(task.Id, command.CommandId,
            new VerificationProviderResponseTelemetry(callId, startedAt, startedAt.AddSeconds(2), "resp-safe",
                "req-safe", VerificationProviderResponseStatus.Completed, null, true, 9, null, 4, null, 200,
                VerificationCallDispatchDisposition.ResponseReceived, VerificationUsageAvailability.Partial),
            startedAt.AddSeconds(2));

        var persisted = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id))!;
        var attempt = Assert.Single(persisted.VerificationPlanGenerationAttempts);
        Assert.Equal(startedAt, Assert.Single(attempt.LogicalCalls!).StartedAt);
        var response = Assert.Single(attempt.ProviderResponses);
        Assert.Equal(startedAt, response.StartedAt);
        Assert.Equal(VerificationUsageAvailability.Partial, response.EffectiveUsageAvailability);
        Assert.Equal(9, response.InputTokens);
        Assert.Equal(4, response.OutputTokens);
        Assert.Null(response.CachedInputTokens);
    }

    [Fact]
    public async Task Production_service_success_persists_atomic_response_then_one_plan_and_replays_without_dispatch()
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var gateway = new VerificationQueueGateway("success");
        var service = OpenAIService(repository, gateway);
        var command = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);

        var completed = await service.GeneratePlanAsync(command);
        var replayed = await service.GeneratePlanAsync(command);

        Assert.Equal(1, gateway.RequestCount);
        Assert.Single(completed.VerificationPlans);
        Assert.Single(replayed.VerificationPlans);
        var attempt = Assert.Single(replayed.VerificationPlanGenerationAttempts);
        Assert.Equal(VerificationGenerationAttemptStatus.Completed, attempt.Status);
        Assert.Equal(1, attempt.LogicalCallCount);
        Assert.Equal(1, attempt.PhysicalRequestCount);
        Assert.Equal(0, attempt.PossiblyDispatchedRequestCount);
        var response = Assert.Single(attempt.ProviderResponses);
        Assert.Equal("resp_verification", response.ProviderResponseId);
        Assert.True(response.UsageAvailable);
        Assert.Equal(101, response.InputTokens);
        var call = Assert.Single(replayed.ModelCalls, call => call.Stage == ModelCallStage.VerificationPlanning);
        Assert.Equal(VerificationCallDispatchDisposition.ResponseReceived, call.VerificationDispatchDisposition);
        Assert.Equal(200, call.ProviderHttpStatusCode);
    }

    [Theory]
    [InlineData("rate_limit", 429)]
    [InlineData("provider_error", 502)]
    [InlineData("provider_error", 503)]
    public async Task Production_service_explicit_retryable_http_response_counts_two_physical_requests(
        string category, int status)
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var gateway = new VerificationQueueGateway(
            new OpenAITransportException(category, "safe", statusCode: status,
                dispatchCertainty: OpenAITransportDispatchCertainty.ResponseReceived), "success");
        var service = OpenAIService(repository, gateway);

        var completed = await service.GeneratePlanAsync(new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id,
            task.RowVersion, revision.RevisionId, revision.ResultFingerprint!));

        Assert.Equal(2, gateway.RequestCount);
        var attempt = Assert.Single(completed.VerificationPlanGenerationAttempts);
        Assert.Equal(2, attempt.LogicalCallCount);
        Assert.Equal(2, attempt.PhysicalRequestCount);
        Assert.Equal(0, attempt.PossiblyDispatchedRequestCount);
        Assert.Equal(2, attempt.ModelCallIds.Count);
        Assert.Contains(completed.ModelCalls, call => call.ProviderHttpStatusCode == status &&
            call.VerificationDispatchDisposition == VerificationCallDispatchDisposition.ResponseReceived);
    }

    [Fact]
    public async Task Production_service_definite_pre_dispatch_failure_records_logical_not_physical_attempt()
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var gateway = new VerificationQueueGateway(new OpenAITransportException("provider_error", "safe",
            dispatchCertainty: OpenAITransportDispatchCertainty.DefinitelyBeforeRequestDispatch), "success");
        var service = OpenAIService(repository, gateway);

        var completed = await service.GeneratePlanAsync(new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id,
            task.RowVersion, revision.RevisionId, revision.ResultFingerprint!));

        var attempt = Assert.Single(completed.VerificationPlanGenerationAttempts);
        Assert.Equal(2, attempt.LogicalCallCount);
        Assert.Equal(1, attempt.PhysicalRequestCount);
        Assert.Equal(0, attempt.PossiblyDispatchedRequestCount);
        var undispatched = completed.ModelCalls.Single(call => !call.Succeeded);
        Assert.Equal(VerificationCallDispatchDisposition.DefinitelyNotDispatched,
            undispatched.VerificationDispatchDisposition);
        Assert.Null(undispatched.ProviderRequestId);
        Assert.Null(undispatched.ProviderResponseId);
    }

    [Fact]
    public async Task Production_service_ambiguous_gateway_failure_is_not_retried_and_survives_restart()
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var gateway = new VerificationQueueGateway(new OpenAITransportException("provider_error", "safe",
            dispatchCertainty: OpenAITransportDispatchCertainty.DispatchMayHaveOccurred));
        var service = OpenAIService(repository, gateway);
        var command = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);

        await Assert.ThrowsAsync<VerificationException>(() => service.GeneratePlanAsync(command));
        var restarted = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id))!;

        Assert.Equal(1, gateway.RequestCount);
        var attempt = Assert.Single(restarted.VerificationPlanGenerationAttempts);
        Assert.Equal(VerificationGenerationAttemptStatus.AmbiguousAfterDispatch, attempt.Status);
        Assert.Equal(1, attempt.LogicalCallCount);
        Assert.Equal(0, attempt.PhysicalRequestCount);
        Assert.Equal(1, attempt.PossiblyDispatchedRequestCount);
        Assert.Equal(VerificationCallDispatchDisposition.PossiblyDispatched,
            Assert.Single(restarted.ModelCalls).VerificationDispatchDisposition);
        var callsBeforeReplay = gateway.RequestCount;
        var replayed = await OpenAIService(new SqliteEngineeringTaskRepository(ConnectionString), gateway)
            .GeneratePlanAsync(command);
        Assert.Equal(callsBeforeReplay, gateway.RequestCount);
        Assert.Equal(VerificationGenerationAttemptStatus.AmbiguousAfterDispatch,
            Assert.Single(replayed.VerificationPlanGenerationAttempts).Status);
        await Assert.ThrowsAsync<WorkflowException>(() => new SqliteEngineeringTaskRepository(ConnectionString)
            .BeginPlanGenerationAsync(command with { CommandId = Guid.NewGuid(), ExpectedRowVersion = restarted.RowVersion },
                Now.AddHours(1)));
    }

    [Fact]
    public async Task Production_service_parsing_failure_retains_atomic_response_usage_and_one_failed_call()
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var gateway = new VerificationQueueGateway("malformed");
        var command = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);

        await Assert.ThrowsAsync<VerificationException>(() => OpenAIService(repository, gateway).GeneratePlanAsync(command));

        var restarted = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id))!;
        Assert.Equal(1, gateway.RequestCount);
        Assert.Empty(restarted.VerificationPlans);
        var attempt = Assert.Single(restarted.VerificationPlanGenerationAttempts);
        Assert.Equal(VerificationGenerationAttemptStatus.AmbiguousAfterDispatch, attempt.Status);
        Assert.Equal(1, attempt.LogicalCallCount);
        Assert.Equal(1, attempt.PhysicalRequestCount);
        var response = Assert.Single(attempt.ProviderResponses);
        Assert.Equal("resp_verification", response.ProviderResponseId);
        Assert.True(response.UsageAvailable);
        Assert.Equal(101, response.InputTokens);
        var call = Assert.Single(restarted.ModelCalls, call => call.Stage == ModelCallStage.VerificationPlanning);
        Assert.False(call.Succeeded);
        Assert.Equal(response.ProviderResponseId, call.ProviderResponseId);
    }

    [Theory]
    [InlineData(true, VerificationGenerationAttemptStatus.FailedBeforeDispatch, 0)]
    [InlineData(false, VerificationGenerationAttemptStatus.RetryableProviderResponse, 2)]
    public async Task Terminal_safe_retry_replays_the_same_command_and_allows_one_explicit_new_command(
        bool definitelyBeforeDispatch, VerificationGenerationAttemptStatus expectedStatus, int expectedPhysicalRequests)
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        OpenAITransportException Failure() => definitelyBeforeDispatch
            ? new OpenAITransportException("provider_error", "safe",
                dispatchCertainty: OpenAITransportDispatchCertainty.DefinitelyBeforeRequestDispatch)
            : new OpenAITransportException("provider_error", "safe", statusCode: 503,
                dispatchCertainty: OpenAITransportDispatchCertainty.ResponseReceived);
        var firstGateway = new VerificationQueueGateway(Failure(), Failure());
        var command = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);

        await Assert.ThrowsAsync<VerificationException>(() => OpenAIService(repository, firstGateway)
            .GeneratePlanAsync(command));
        var restartedRepository = new SqliteEngineeringTaskRepository(ConnectionString);
        var failed = (await restartedRepository.GetAsync(task.Id))!;
        var firstAttempt = Assert.Single(failed.VerificationPlanGenerationAttempts);
        Assert.Equal(expectedStatus, firstAttempt.Status);
        Assert.Equal(2, firstAttempt.LogicalCallCount);
        Assert.Equal(expectedPhysicalRequests, firstAttempt.PhysicalRequestCount);
        Assert.Equal(2, firstGateway.RequestCount);

        await OpenAIService(restartedRepository, firstGateway).GeneratePlanAsync(command);
        Assert.Equal(2, firstGateway.RequestCount);

        var retryGateway = new VerificationQueueGateway("success");
        var retry = new VerificationPlanGenerationCommand(Guid.NewGuid(), failed.Id, failed.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);
        var completed = await OpenAIService(restartedRepository, retryGateway).GeneratePlanAsync(retry);

        Assert.Equal(1, retryGateway.RequestCount);
        Assert.Equal(2, completed.VerificationPlanGenerationAttempts.Count);
        Assert.Equal(expectedStatus, completed.VerificationPlanGenerationAttempts[0].Status);
        Assert.Equal(VerificationGenerationAttemptStatus.Completed,
            completed.VerificationPlanGenerationAttempts[1].Status);
        Assert.Single(completed.VerificationPlans);
    }

    [Theory]
    [InlineData(RepositoryFault.BeforeBegin, null, 0, 0, 0)]
    [InlineData(RepositoryFault.AfterBegin, VerificationGenerationAttemptStatus.Prepared, 0, 0, 0)]
    [InlineData(RepositoryFault.BeforeDispatchIntent, VerificationGenerationAttemptStatus.Prepared, 0, 0, 0)]
    [InlineData(RepositoryFault.AfterDispatchIntent, VerificationGenerationAttemptStatus.DispatchMayHaveStarted, 0, 1, 0)]
    [InlineData(RepositoryFault.BeforeResponseCheckpoint, VerificationGenerationAttemptStatus.DispatchMayHaveStarted, 1, 1, 0)]
    [InlineData(RepositoryFault.AfterResponseCheckpoint, VerificationGenerationAttemptStatus.ResponseReceived, 1, 1, 1)]
    [InlineData(RepositoryFault.BeforeCompletion, VerificationGenerationAttemptStatus.ResponseReceived, 1, 1, 1)]
    [InlineData(RepositoryFault.AfterCompletion, VerificationGenerationAttemptStatus.Completed, 1, 1, 1)]
    public async Task Production_boundary_faults_leave_the_exact_last_durable_phase_and_replay_never_dispatches(
        RepositoryFault fault, VerificationGenerationAttemptStatus? expectedStatus, int expectedGatewayCalls,
        int expectedLogicalCalls, int expectedResponses)
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var gateway = new VerificationQueueGateway("success");
        var command = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);

        var thrown = await Record.ExceptionAsync(() =>
            OpenAIService(new FaultingVerificationRepository(repository, fault), gateway).GeneratePlanAsync(command));
        Assert.NotNull(thrown);
        Assert.True(ContainsInjectedFailure(thrown), $"Unexpected exception: {thrown}");

        var restartedRepository = new SqliteEngineeringTaskRepository(ConnectionString);
        var restarted = (await restartedRepository.GetAsync(task.Id))!;
        Assert.Equal(expectedGatewayCalls, gateway.RequestCount);
        if (expectedStatus is null)
        {
            Assert.Empty(restarted.VerificationPlanGenerationAttempts);
            return;
        }

        var attempt = Assert.Single(restarted.VerificationPlanGenerationAttempts);
        Assert.Equal(expectedStatus, attempt.Status);
        Assert.Equal(expectedLogicalCalls, attempt.LogicalCallCount);
        Assert.Equal(expectedResponses, attempt.ProviderResponses.Count);
        if (expectedResponses > 0)
        {
            var response = Assert.Single(attempt.ProviderResponses);
            Assert.Equal("resp_verification", response.ProviderResponseId);
            Assert.Equal(101, response.InputTokens);
        }

        var callsBeforeReplay = gateway.RequestCount;
        var replayed = await OpenAIService(restartedRepository, gateway).GeneratePlanAsync(command);
        Assert.Equal(callsBeforeReplay, gateway.RequestCount);
        Assert.Single(replayed.VerificationPlanGenerationAttempts);
        Assert.Equal(expectedStatus, Assert.Single(replayed.VerificationPlanGenerationAttempts).Status);
        Assert.Equal(expectedStatus == VerificationGenerationAttemptStatus.Completed ? 1 : 0,
            replayed.VerificationPlans.Count);

        static bool ContainsInjectedFailure(Exception exception) => exception is InjectedRepositoryException ||
            exception.InnerException is not null && ContainsInjectedFailure(exception.InnerException);
    }

    [Theory]
    [InlineData(GenerationMatrixPhase.PreparedLive, VerificationGenerationAttemptStatus.Prepared, 0, 0, 0, 0, 0, false)]
    [InlineData(GenerationMatrixPhase.PreparedExpired, VerificationGenerationAttemptStatus.Prepared, 0, 0, 0, 0, 0, true)]
    [InlineData(GenerationMatrixPhase.DispatchLive, VerificationGenerationAttemptStatus.DispatchMayHaveStarted, 1, 0, 0, 0, 0, false)]
    [InlineData(GenerationMatrixPhase.DispatchExpired, VerificationGenerationAttemptStatus.DispatchMayHaveStarted, 1, 0, 0, 0, 0, false)]
    [InlineData(GenerationMatrixPhase.ResponseLive, VerificationGenerationAttemptStatus.ResponseReceived, 1, 1, 0, 1, 0, false)]
    [InlineData(GenerationMatrixPhase.ResponseExpired, VerificationGenerationAttemptStatus.ResponseReceived, 1, 1, 0, 1, 0, false)]
    [InlineData(GenerationMatrixPhase.FailedBeforeDispatch, VerificationGenerationAttemptStatus.FailedBeforeDispatch, 2, 0, 0, 0, 0, true)]
    [InlineData(GenerationMatrixPhase.RetryableProviderResponse, VerificationGenerationAttemptStatus.RetryableProviderResponse, 2, 2, 0, 0, 0, true)]
    [InlineData(GenerationMatrixPhase.AmbiguousAfterDispatch, VerificationGenerationAttemptStatus.AmbiguousAfterDispatch, 1, 0, 1, 0, 0, false)]
    [InlineData(GenerationMatrixPhase.InterruptedBeforeDispatch, VerificationGenerationAttemptStatus.InterruptedBeforeDispatch, 0, 0, 0, 0, 0, true)]
    [InlineData(GenerationMatrixPhase.Completed, VerificationGenerationAttemptStatus.Completed, 0, 0, 0, 0, 1, false)]
    public async Task Durable_generation_phase_restart_replay_and_new_command_matrix_is_conservative(
        GenerationMatrixPhase phase,
        VerificationGenerationAttemptStatus expectedStatus,
        int expectedLogicalCalls,
        int expectedPhysicalRequests,
        int expectedPossibleDispatches,
        int expectedResponses,
        int expectedPlans,
        bool newCommandMayDispatch)
    {
        var (repository, task, revision) = await ApprovedPersistedTaskAsync();
        var command = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);
        var begunAt = Now.AddMinutes(4);
        var observationAt = phase is GenerationMatrixPhase.PreparedExpired or
            GenerationMatrixPhase.DispatchExpired or GenerationMatrixPhase.ResponseExpired
                ? begunAt.AddMinutes(6)
                : begunAt.AddMinutes(1);

        if (phase == GenerationMatrixPhase.Completed)
        {
            await Service(repository).GeneratePlanAsync(command);
        }
        else if (phase is GenerationMatrixPhase.FailedBeforeDispatch or
                 GenerationMatrixPhase.RetryableProviderResponse or
                 GenerationMatrixPhase.AmbiguousAfterDispatch)
        {
            var failure = phase switch
            {
                GenerationMatrixPhase.FailedBeforeDispatch => new OpenAITransportException(
                    "provider_error", "safe",
                    dispatchCertainty: OpenAITransportDispatchCertainty.DefinitelyBeforeRequestDispatch),
                GenerationMatrixPhase.RetryableProviderResponse => new OpenAITransportException(
                    "provider_error", "safe", statusCode: 503,
                    dispatchCertainty: OpenAITransportDispatchCertainty.ResponseReceived),
                _ => new OpenAITransportException("provider_error", "safe",
                    dispatchCertainty: OpenAITransportDispatchCertainty.DispatchMayHaveOccurred)
            };
            var gateway = phase == GenerationMatrixPhase.AmbiguousAfterDispatch
                ? new VerificationQueueGateway(failure)
                : new VerificationQueueGateway(failure, failure);
            await Assert.ThrowsAsync<VerificationException>(() =>
                OpenAIService(repository, gateway, begunAt).GeneratePlanAsync(command));
        }
        else
        {
            task = (await repository.BeginPlanGenerationAsync(command, begunAt)).Task;
            if (phase == GenerationMatrixPhase.InterruptedBeforeDispatch)
            {
                await repository.FailPlanGenerationAsync(task.Id, command.CommandId,
                    "verification_interrupted_before_dispatch", "The attempt stopped safely.", [],
                    VerificationGenerationAttemptStatus.InterruptedBeforeDispatch, begunAt.AddSeconds(1));
            }
            else if (phase is GenerationMatrixPhase.DispatchLive or GenerationMatrixPhase.DispatchExpired or
                     GenerationMatrixPhase.ResponseLive or GenerationMatrixPhase.ResponseExpired)
            {
                var logicalCallId = Guid.NewGuid();
                var callStartedAt = begunAt.AddSeconds(1);
                await repository.RecordPlanGenerationCheckpointAsync(task.Id, command.CommandId,
                    VerificationDispatchCheckpoint.DispatchMayHaveStarted, logicalCallId, callStartedAt,
                    logicalCallStartedAt: callStartedAt);
                if (phase is GenerationMatrixPhase.ResponseLive or GenerationMatrixPhase.ResponseExpired)
                    await repository.RecordVerificationProviderResponseAsync(task.Id, command.CommandId,
                        new VerificationProviderResponseTelemetry(logicalCallId, callStartedAt,
                            callStartedAt.AddSeconds(1), "resp_matrix", "req_matrix",
                            VerificationProviderResponseStatus.Completed, null, true,
                            10, null, 5, null, 200,
                            VerificationCallDispatchDisposition.ResponseReceived,
                            VerificationUsageAvailability.Partial), callStartedAt.AddSeconds(1));
            }
        }

        var restartedRepository = new SqliteEngineeringTaskRepository(ConnectionString);
        var gatewayAfterRestart = new VerificationQueueGateway("success");
        _ = OpenAIService(restartedRepository, gatewayAfterRestart, observationAt);
        var restarted = (await restartedRepository.GetAsync(task.Id))!;
        Assert.Equal(0, gatewayAfterRestart.RequestCount);
        var attempt = Assert.Single(restarted.VerificationPlanGenerationAttempts);
        Assert.Equal(expectedStatus, attempt.Status);
        Assert.Equal(expectedLogicalCalls, attempt.LogicalCallCount);
        Assert.Equal(expectedPhysicalRequests, attempt.PhysicalRequestCount);
        Assert.Equal(expectedPossibleDispatches, attempt.PossiblyDispatchedRequestCount);
        Assert.Equal(expectedResponses, attempt.ProviderResponses.Count);
        Assert.Equal(expectedPlans, restarted.VerificationPlans.Count);
        Assert.Equal(expectedLogicalCalls, attempt.LogicalCalls!.Count);
        Assert.Equal(expectedLogicalCalls, attempt.LogicalCalls.Select(call => call.LogicalCallId).Distinct().Count());
        Assert.Equal(phase == GenerationMatrixPhase.FailedBeforeDispatch ? 2 : 0,
            restarted.ModelCalls.Count(call =>
                call.VerificationDispatchDisposition == VerificationCallDispatchDisposition.DefinitelyNotDispatched));
        Assert.Equal(attempt.ModelCallIds.Count, attempt.ModelCallIds.Distinct().Count());
        Assert.Equal(restarted.ModelCalls.Count, restarted.ModelCalls.Select(call => call.Id).Distinct().Count());
        var rowVersionBeforeReplay = restarted.RowVersion;

        var replayed = await OpenAIService(restartedRepository, gatewayAfterRestart, observationAt)
            .GeneratePlanAsync(command);
        Assert.Equal(0, gatewayAfterRestart.RequestCount);
        Assert.Equal(rowVersionBeforeReplay, replayed.RowVersion);
        Assert.Single(replayed.VerificationPlanGenerationAttempts);
        Assert.Equal(expectedPlans, replayed.VerificationPlans.Count);

        var retryGateway = new VerificationQueueGateway("success");
        var retryCommand = command with
        {
            CommandId = Guid.NewGuid(),
            ExpectedRowVersion = replayed.RowVersion
        };
        if (newCommandMayDispatch)
        {
            var completed = await OpenAIService(restartedRepository, retryGateway, observationAt)
                .GeneratePlanAsync(retryCommand);
            Assert.Equal(1, retryGateway.RequestCount);
            Assert.Equal(2, completed.VerificationPlanGenerationAttempts.Count);
            Assert.Single(completed.VerificationPlans);
        }
        else
        {
            await Assert.ThrowsAsync<WorkflowException>(() =>
                OpenAIService(restartedRepository, retryGateway, observationAt).GeneratePlanAsync(retryCommand));
            Assert.Equal(0, retryGateway.RequestCount);
            var unchanged = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id))!;
            Assert.Single(unchanged.VerificationPlanGenerationAttempts);
            Assert.Equal(expectedPlans, unchanged.VerificationPlans.Count);
        }
    }

    private static VerificationWorkflowService Service(SqliteEngineeringTaskRepository repository) => new(
        repository, new FakeVerificationPlanEngine(), new ImplementationOperationCoordinator(), new VerificationLimits(),
        new FixedTimeProvider(Now.AddMinutes(4)));

    private async Task<(SqliteEngineeringTaskRepository Repository, EngineeringTask Task,
        ImplementationRevision Revision)> ApprovedPersistedTaskAsync()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = VerificationWorkflowTests.ApprovedImplementation(approve: false);
        await repository.SaveAsync(task);
        var pending = Assert.Single(task.ImplementationRevisions);
        task = await repository.ApproveImplementationAsync(new ImplementationApprovalCommand(Guid.NewGuid(), task.Id,
            task.RowVersion, pending.RevisionId, pending.ResultFingerprint!), Now.AddMinutes(3));
        return (repository, task,
            task.ImplementationRevisions.Single(item => item.RevisionId == task.ApprovedImplementationRevisionId));
    }

    private static VerificationWorkflowService OpenAIService(IVerificationRepository repository,
        IOpenAIResponsesGateway gateway, DateTimeOffset? now = null) => new(repository,
        new OpenAIVerificationPlanEngine(new ForgeAiOptions
        {
            Mode = ForgeAiModes.OpenAI,
            VerificationPlanningModel = "gpt-5.6-sol",
            VerificationPlanningReasoningEffort = "medium",
            VerificationPlanningMaxOutputTokens = 8_000,
            VerificationPlanningTimeoutSeconds = 30
        }, gateway, new ModelCostCalculator(ForgeAiOptions.DefaultPricing()), new FixedTimeProvider(now ?? Now.AddMinutes(4))),
        new ImplementationOperationCoordinator(), new VerificationLimits(), new FixedTimeProvider(now ?? Now.AddMinutes(4)));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class VerificationQueueGateway(params object[] outcomes) : IOpenAIResponsesGateway
    {
        private readonly Queue<object> queue = new(outcomes);
        public int RequestCount { get; private set; }

        public Task<OpenAIResponseEnvelope> CreateResponseAsync(OpenAIResponseRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            var outcome = queue.Dequeue();
            if (outcome is Exception exception) return Task.FromException<OpenAIResponseEnvelope>(exception);
            using var context = JsonDocument.Parse(request.UserInput);
            var fingerprint = context.RootElement.GetProperty("contextFingerprint").GetString()!;
            var output = outcome as string == "malformed" ? "{ malformed" : JsonSerializer.Serialize(new
            {
                contextFingerprint = fingerprint,
                summary = "Concise manual verification guidance.", scope = "Exact approved revision only.",
                preconditions = new[] { "Use the exact approved revision." },
                testCases = new[]
                {
                    new
                    {
                        order = 1, title = "Manual behavior check", objective = "Observe the approved behavior.",
                        category = "ManualBehavior", isRequired = true, preconditions = Array.Empty<string>(),
                        testData = Array.Empty<string>(), orderedSteps = new[]
                        {
                            new { order = 1, instruction = "Inspect the approved behavior manually.",
                                approvedValidationCommandId = "", expectedObservation = "The expected behavior is observed." }
                        },
                        expectedResult = "The user reports the expected behavior.", negativeOrEdgeCases = Array.Empty<string>(),
                        regressionScope = Array.Empty<string>(), evidenceRequirements = Array.Empty<string>(),
                        safetyNotes = new[] { "Forge does not execute this check." }, originTestCaseId = "",
                        regressionFailureReportIds = Array.Empty<string>()
                    }
                },
                risks = Array.Empty<string>(), limitations = new[] { "Manual user report only." },
                evidenceGuidance = new[] { "Do not include secrets." }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Task.FromResult(new OpenAIResponseEnvelope("resp_verification", output, 101, 11, 37, 7,
                ProviderRequestId: "req_verification", OutputItems:
                [new OpenAIResponseOutputItem(OpenAIResponseOutputItemKind.Message, "assistant",
                    [new OpenAIResponseContent(OpenAIResponseContentKind.OutputText, output)])]));
        }
    }

    public enum RepositoryFault
    {
        BeforeBegin,
        AfterBegin,
        BeforeDispatchIntent,
        AfterDispatchIntent,
        BeforeResponseCheckpoint,
        AfterResponseCheckpoint,
        BeforeCompletion,
        AfterCompletion
    }

    public enum GenerationMatrixPhase
    {
        PreparedLive,
        PreparedExpired,
        DispatchLive,
        DispatchExpired,
        ResponseLive,
        ResponseExpired,
        FailedBeforeDispatch,
        RetryableProviderResponse,
        AmbiguousAfterDispatch,
        InterruptedBeforeDispatch,
        Completed
    }

    private sealed class InjectedRepositoryException(RepositoryFault fault) : Exception(fault.ToString());

    private sealed class FaultingVerificationRepository(IVerificationRepository inner, RepositoryFault fault)
        : IVerificationRepository
    {
        private void Throw(RepositoryFault point)
        {
            if (fault == point) throw new InjectedRepositoryException(point);
        }

        public async Task<VerificationRepositoryCommandResult> BeginPlanGenerationAsync(
            VerificationPlanGenerationCommand command, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Throw(RepositoryFault.BeforeBegin);
            var result = await inner.BeginPlanGenerationAsync(command, now, cancellationToken);
            Throw(RepositoryFault.AfterBegin);
            return result;
        }

        public async Task<EngineeringTask> CompletePlanGenerationAsync(Guid taskId, Guid commandId,
            VerificationPlan plan, IReadOnlyList<ModelCallRecord> modelCalls, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Throw(RepositoryFault.BeforeCompletion);
            var result = await inner.CompletePlanGenerationAsync(taskId, commandId, plan, modelCalls, now, cancellationToken);
            Throw(RepositoryFault.AfterCompletion);
            return result;
        }

        public Task<EngineeringTask> FailPlanGenerationAsync(Guid taskId, Guid commandId, string category,
            string safeMessage, IReadOnlyList<ModelCallRecord> modelCalls,
            VerificationGenerationAttemptStatus durableStatus, DateTimeOffset now,
            CancellationToken cancellationToken = default) => inner.FailPlanGenerationAsync(taskId, commandId,
            category, safeMessage, modelCalls, durableStatus, now, cancellationToken);

        public async Task<EngineeringTask> RecordPlanGenerationCheckpointAsync(Guid taskId, Guid commandId,
            VerificationDispatchCheckpoint checkpoint, Guid physicalCallId, DateTimeOffset now,
            CancellationToken cancellationToken = default, DateTimeOffset? logicalCallStartedAt = null)
        {
            if (checkpoint == VerificationDispatchCheckpoint.DispatchMayHaveStarted)
                Throw(RepositoryFault.BeforeDispatchIntent);
            var result = await inner.RecordPlanGenerationCheckpointAsync(taskId, commandId, checkpoint,
                physicalCallId, now, cancellationToken, logicalCallStartedAt);
            if (checkpoint == VerificationDispatchCheckpoint.DispatchMayHaveStarted)
                Throw(RepositoryFault.AfterDispatchIntent);
            return result;
        }

        public Task<EngineeringTask> RecordPlanGenerationModelCallAsync(Guid taskId, Guid commandId,
            Guid physicalCallId, ModelCallRecord modelCall, DateTimeOffset now,
            CancellationToken cancellationToken = default) => inner.RecordPlanGenerationModelCallAsync(taskId,
            commandId, physicalCallId, modelCall, now, cancellationToken);

        public async Task<EngineeringTask> RecordVerificationProviderResponseAsync(Guid taskId, Guid commandId,
            VerificationProviderResponseTelemetry response, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Throw(RepositoryFault.BeforeResponseCheckpoint);
            var result = await inner.RecordVerificationProviderResponseAsync(taskId, commandId, response, now,
                cancellationToken);
            Throw(RepositoryFault.AfterResponseCheckpoint);
            return result;
        }

        public Task<EngineeringTask> RecordVerificationTransportFailureAsync(Guid taskId, Guid commandId,
            Guid logicalCallId, VerificationDispatchCheckpoint checkpoint, ModelCallRecord modelCall,
            VerificationCallDispatchDisposition disposition, string safeFailureMessage, DateTimeOffset now,
            CancellationToken cancellationToken = default) => inner.RecordVerificationTransportFailureAsync(taskId,
            commandId, logicalCallId, checkpoint, modelCall, disposition, safeFailureMessage, now, cancellationToken);

        public Task<VerificationRepositoryCommandResult> StartAttemptAsync(StartManualVerificationCommand command,
            DateTimeOffset now, CancellationToken cancellationToken = default) =>
            inner.StartAttemptAsync(command, now, cancellationToken);

        public Task<VerificationRepositoryCommandResult> UpdateCaseAsync(UpdateManualVerificationCaseCommand command,
            DateTimeOffset now, CancellationToken cancellationToken = default) =>
            inner.UpdateCaseAsync(command, now, cancellationToken);

        public Task<VerificationRepositoryCommandResult> CompleteAttemptAsync(CompleteManualVerificationCommand command,
            DateTimeOffset now, CancellationToken cancellationToken = default) =>
            inner.CompleteAttemptAsync(command, now, cancellationToken);
    }
}
