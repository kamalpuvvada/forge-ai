using Forge.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Infrastructure;

/// <summary>Mechanical Fake adapter used to exercise the isolated implementation workflow without AI reasoning.</summary>
public sealed class FakeImplementationEngine : IImplementationEngine
{
    public Task<ImplementationEvaluation> GenerateAsync(
        ImplementationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        var operations = context.Files
            .Where(file => file.PlannedAction != PlannedFileAction.Inspect)
            .Select(CreateOperation)
            .ToArray();
        var output = new ImplementationOutput(
            "Created deterministic Fake-mode changes for isolated diff-review workflow validation.",
            [
                "This is a mechanical workflow demonstration, not AI-authored implementation.",
                "Validation commands were not run."
            ],
            operations,
            ImplementationSource.DeterministicFake,
            null);
        return Task.FromResult(new ImplementationEvaluation(output));
    }

    private static ImplementationOperation CreateOperation(ImplementationFileContext file)
    {
        var action = file.PlannedAction switch
        {
            PlannedFileAction.Create => ImplementationOperationAction.Create,
            PlannedFileAction.Modify => ImplementationOperationAction.Modify,
            PlannedFileAction.Delete => ImplementationOperationAction.Delete,
            _ => throw new ImplementationException("implementation_unsupported_action", "Inspect paths cannot be changed by Fake implementation generation.")
        };
        var content = action switch
        {
            ImplementationOperationAction.Create => CreatePlaceholder(file.Path),
            ImplementationOperationAction.Modify => AddMarker(file.Path, file.OriginalContent ?? string.Empty),
            _ => null
        };
        return new ImplementationOperation(
            file.Path,
            action,
            file.OriginalContentSha256,
            content,
            action switch
            {
                ImplementationOperationAction.Create => "Create an explicitly labelled deterministic Fake placeholder.",
                ImplementationOperationAction.Modify => "Add an explicitly labelled deterministic Fake marker.",
                _ => "Delete the explicitly approved target in the isolated worktree."
            });
    }

    private static string CreatePlaceholder(string path)
    {
        var style = FakeImplementationCapabilityMatrix.GetStyle(path, PlannedFileAction.Create);
        return style switch
        {
            FakeImplementationContentStyle.SlashLine => $"// Forge deterministic Fake implementation placeholder.{Environment.NewLine}",
            FakeImplementationContentStyle.HashLine => $"# Forge deterministic Fake implementation placeholder.{Environment.NewLine}",
            FakeImplementationContentStyle.SqlLine => $"-- Forge deterministic Fake implementation placeholder.{Environment.NewLine}",
            FakeImplementationContentStyle.CommandLine => $"REM Forge deterministic Fake implementation placeholder.{Environment.NewLine}",
            FakeImplementationContentStyle.CssBlock => $"/* Forge deterministic Fake implementation placeholder. */{Environment.NewLine}",
            FakeImplementationContentStyle.XmlBlock or FakeImplementationContentStyle.MarkdownBlock =>
                $"<!-- Forge deterministic Fake implementation placeholder. -->{Environment.NewLine}",
            FakeImplementationContentStyle.JsonObject => "{\n  \"forgeDeterministicFake\": \"Mechanical workflow demonstration; not AI-authored implementation.\"\n}\n",
            _ => throw Unsupported(path)
        };
    }

    private static string AddMarker(string path, string original)
    {
        var style = FakeImplementationCapabilityMatrix.GetStyle(path, PlannedFileAction.Modify);
        if (style == FakeImplementationContentStyle.JsonObject) return ModifyJson(path, original);
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var prefix = original.Length == 0 || original.EndsWith('\n') || original.EndsWith('\r') ? string.Empty : newline;
        var comment = style switch
        {
            FakeImplementationContentStyle.SlashLine => "// Forge deterministic Fake implementation marker; not AI-authored code.",
            FakeImplementationContentStyle.HashLine => "# Forge deterministic Fake implementation marker; not AI-authored code.",
            FakeImplementationContentStyle.SqlLine => "-- Forge deterministic Fake implementation marker; not AI-authored code.",
            FakeImplementationContentStyle.CommandLine => "REM Forge deterministic Fake implementation marker; not AI-authored code.",
            FakeImplementationContentStyle.CssBlock => "/* Forge deterministic Fake implementation marker; not AI-authored code. */",
            FakeImplementationContentStyle.XmlBlock or FakeImplementationContentStyle.MarkdownBlock =>
                "<!-- Forge deterministic Fake implementation marker; not AI-authored code. -->",
            _ => throw Unsupported(path)
        };
        return $"{original}{prefix}{comment}{newline}";
    }

    private static string ModifyJson(string path, string original)
    {
        try
        {
            var node = JsonNode.Parse(original);
            if (node is JsonObject existing && existing.ContainsKey("forgeDeterministicFake"))
                throw new ImplementationException("implementation_terminal_incompatibility",
                    $"Deterministic Fake JSON marker already exists in '{path}'.");
            if (node is JsonObject objectNode)
                objectNode["forgeDeterministicFake"] = "Mechanical workflow demonstration; not AI-authored implementation.";
            else if (node is JsonArray arrayNode)
                arrayNode.Add(new JsonObject
                {
                    ["forgeDeterministicFake"] = "Mechanical workflow demonstration; not AI-authored implementation."
                });
            else if (node is not null)
                node = new JsonObject
                {
                    ["forgeDeterministicFake"] = "Mechanical workflow demonstration; not AI-authored implementation.",
                    ["originalValue"] = node.DeepClone()
                };
            else
                node = new JsonObject
                {
                    ["forgeDeterministicFake"] = "Mechanical workflow demonstration; not AI-authored implementation.",
                    ["originalValue"] = null
                };
            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        }
        catch (JsonException)
        {
            throw Unsupported(path);
        }
    }

    private static ImplementationException Unsupported(string path) => new(
        "implementation_unsupported_file",
        $"Deterministic Fake implementation does not support '{Path.GetExtension(path)}' files for this action.");

}
