using System.Globalization;
using Forge.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Forge.Infrastructure;

public sealed class VerificationPlanPdfExporter : IVerificationPlanPdfExporter
{
    public byte[] Export(EngineeringTask task, Guid planId)
    {
        ArgumentNullException.ThrowIfNull(task);
        var plan = task.VerificationPlans.SingleOrDefault(item => item.PlanId == planId)
            ?? throw new VerificationException("verification_plan_not_found", "The verification plan was not found.");
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var writer = new TaskPdfExporter.FlowWriter(builder, regular, bold,
            new[] { task.Repository, task.RepositorySnapshot?.NormalizedRoot }
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray(), null);

        writer.Title("Forge AI Manual Verification Plan");
        writer.Banner(VerificationTrustLabels.ForgeGenerated);
        writer.Banner(VerificationTrustLabels.ManualNotExecuted);
        writer.Field("Task ID", task.Id.ToString("D"));
        writer.Field("Plan", plan.PlanNumber.ToString(CultureInfo.InvariantCulture));
        writer.Field("Plan status", plan.Status.ToString());
        writer.Field("Implementation revision", plan.ImplementationRevisionId.ToString("D"));
        writer.Field("Implementation result fingerprint", plan.ImplementationResultFingerprint);
        writer.Field("Plan fingerprint", plan.PlanFingerprint);
        writer.Field("Generated at", plan.GeneratedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.Field("Source", plan.Source.ToString());
        writer.Field("Model", plan.Model ?? "none (deterministic Fake plan)");
        writer.Section("Summary");
        writer.Body(plan.Summary);
        writer.Section("Scope");
        writer.Body(plan.Scope);
        WriteStrings(writer, "Preconditions", plan.Preconditions);

        writer.Section("Ordered manual verification cases");
        foreach (var testCase in plan.TestCases.OrderBy(item => item.Order))
        {
            writer.Subsection($"Case {testCase.Order}: {testCase.Title} ({(testCase.IsRequired ? "REQUIRED" : "OPTIONAL")})");
            writer.Field("Category", testCase.Category.ToString());
            writer.Field("Objective", testCase.Objective);
            WriteStrings(writer, "Preconditions", testCase.Preconditions);
            WriteStrings(writer, "Test data", testCase.TestData);
            foreach (var step in testCase.OrderedSteps.OrderBy(item => item.Order))
            {
                writer.Body($"{step.Order}. {VerificationTrustLabels.ManualNotExecuted}: {step.Instruction}");
                if (step.ApprovedValidationCommandId is not null)
                    writer.Field("Approved command reference", step.ApprovedValidationCommandId);
                writer.Field("Expected observation", step.ExpectedObservation);
            }
            writer.Field("Expected result", testCase.ExpectedResult);
            WriteStrings(writer, "Negative or edge cases", testCase.NegativeOrEdgeCases);
            WriteStrings(writer, "Regression scope", testCase.RegressionScope);
            WriteStrings(writer, "Evidence requirements", testCase.EvidenceRequirements);
            WriteStrings(writer, "Safety notes", testCase.SafetyNotes);
        }
        WriteStrings(writer, "Risks", plan.Risks);
        WriteStrings(writer, "Limitations", plan.Limitations);
        WriteStrings(writer, "Evidence guidance", plan.EvidenceGuidance);
        writer.Body("Forge generated this plan but executed no command or check. Outcomes must be recorded by a human.");
        return builder.Build();
    }

    private static void WriteStrings(TaskPdfExporter.FlowWriter writer, string heading, IReadOnlyList<string> values)
    {
        writer.Section(heading);
        if (values.Count == 0) writer.Body("None recorded.");
        else foreach (var value in values) writer.Body($"- {value}");
    }
}
