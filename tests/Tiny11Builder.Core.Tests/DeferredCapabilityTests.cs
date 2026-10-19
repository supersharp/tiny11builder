using System.Runtime.Versioning;
using System.Text.Json;
using Tiny11Builder.App;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class DeferredCapabilityTests
{
    internal static readonly string[] Reported =
    [
        "App.StepsRecorder~~~~0.0.1.0", "Language.Handwriting~~~en-US~0.0.1.0",
        "Language.OCR~~~en-US~0.0.1.0", "Language.Speech~~~en-US~0.0.1.0",
        "Language.TextToSpeech~~~en-US~0.0.1.0", "MathRecognizer~~~~0.0.1.0",
        "Media.WindowsMediaPlayer~~~~0.0.12.0"
    ];

    private static BuildFixture Fixture()
    {
        var fixture = new BuildFixture(26300);
        fixture.Runner.InstalledCapabilities.Clear();
        fixture.Runner.InstalledCapabilities.AddRange(Reported);
        fixture.Runner.CapabilityStateAfterRemoval = "Uninstall Pending";
        return fixture;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seven_reported_pending_removals_can_commit_a_serviceable_image_without_claiming_absence(bool turbo)
    {
        using var fixture = Fixture();
        var progress = new List<BuildProgress>();
        var result = await fixture.Engine.BuildAsync(fixture.Recipe with
        {
            TurboMode = turbo, SelectedOptions = ["language-extras", "legacy-capabilities", "cleanup", "edge-files"]
        }, new ImmediateProgress<BuildProgress>(progress.Add), CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
        Assert.Contains(result.Warnings, warning => warning.Contains("first boot") && warning.Contains("7"));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("/Cleanup-Image"));
        Assert.Contains(progress, p => p.OperationId == "retained:edge-files" && p.State == BuildOperationState.Skipped);
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        var items = report.RootElement.GetProperty("Capabilities").EnumerateArray().ToArray();
        Assert.Equal(7, items.Length);
        Assert.All(items, item => Assert.Equal("Pending", item.GetProperty("Proof").GetString()));
        var board = new BuildStageBoard();
        foreach (var p in progress) board.Apply(p);
        foreach (var identity in Reported)
        {
            var row = Assert.Single(board.Stages.SelectMany(stage => stage.Operations), op => op.Id == "capability:" + identity);
            Assert.Contains("first boot", row.Detail);
            Assert.DoesNotContain("Verified absent", row.Detail);
        }
    }

    [Fact]
    public async Task Pending_capability_results_block_Core_even_if_package_and_feature_lists_look_settled()
    {
        using var fixture = Fixture();
        var error = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe with
        {
            TurboMode = true, Flavor = BuildFlavor.Core, CoreAcknowledged = true,
            SelectedOptions = ["language-extras", "legacy-capabilities"]
        }, null, CancellationToken.None));
        Assert.Contains("pending", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("did not prove removal", error.Message);
        Assert.DoesNotContain("TrimCore", fixture.Events);
        Assert.DoesNotContain("RemoveOriginalFiles", fixture.Events);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("/Commit"));
        Assert.Contains(fixture.Runner.Calls, call => call.Contains("/Discard"));
    }

    [Fact]
    public async Task Custom_component_payloads_are_also_protected_with_cleanup_off()
    {
        using var fixture = Fixture();
        var error = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe with
        {
            TurboMode = true, SelectedOptions = ["language-extras", "legacy-capabilities"],
            ExpertAcknowledged = true,
            ExpertActions = [new(ImageItemKind.ComponentPayload, "amd64_critical-component", ExpertActionMode.Remove)]
        }, null, CancellationToken.None));
        Assert.Contains("Component payloads were not removed", error.Message);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("/Cleanup-Image"));
        Assert.DoesNotContain("RemoveOriginalFiles", fixture.Events);
        Assert.Empty(fixture.Operations.LastActions);
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
    }

    [Theory]
    [InlineData("Install Pending")]
    [InlineData("Uninstall Pending Unexpected")]
    [InlineData("Installed")]
    [InlineData("Surprise")]
    public async Task Only_an_uninstall_pending_state_can_be_accepted_as_deferred(string state)
    {
        using var fixture = Fixture();
        fixture.Runner.CapabilityStateAfterRemoval = state;
        await Assert.ThrowsAsync<InvalidDataException>(() => new DismClient(fixture.Recipe.DismPath, fixture.Runner)
            .RemoveCapabilitiesAsync(@"C:\mock\mount", [Reported[0]], CancellationToken.None));
    }

    [Fact]
    public async Task Deferred_outcomes_and_warnings_survive_commit_and_resume_without_reissuing_the_removals()
    {
        using var fixture = Fixture();
        var recipe = fixture.Recipe with
        {
            TurboMode = true, SelectedOptions = ["language-extras", "legacy-capabilities"]
        };
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture, recipe);
        var checkpoint = CheckpointStore.Read(failure.WorkDirectory);
        Assert.Equal(7, checkpoint.Report.Capabilities.Count);
        Assert.All(checkpoint.Report.Capabilities, item => Assert.True(item.Deferred));
        Assert.Contains(checkpoint.Report.Warnings, warning => warning.Contains("first boot"));
        var before = fixture.Runner.Calls.Count;
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), recipe, null, CancellationToken.None);
        Assert.DoesNotContain(fixture.Runner.Calls.Skip(before), call => call.Contains("/Remove-Capability"));
        Assert.Equal(checkpoint.Report.Warnings, result.Warnings);
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.All(report.RootElement.GetProperty("Capabilities").EnumerateArray(), item =>
        {
            Assert.Equal("Pending", item.GetProperty("Proof").GetString());
            Assert.True(item.GetProperty("Deferred").GetBoolean());
        });
    }

    [Fact]
    public async Task An_already_queued_uninstall_is_reported_without_sending_another_remove_command()
    {
        using var fixture = Fixture();
        var runner = new CommandResultRunner(fixture.Runner, args => args.Contains("/Get-Capabilities") ?
            new ProcessResult(0, string.Concat(Reported.Select(name => $"Capability Identity : {name}\nState : Uninstall Pending\n"))) : null);
        var engine = new BuildEngine(runner, new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost());
        var result = await engine.BuildAsync(fixture.Recipe with
        {
            TurboMode = true, SelectedOptions = ["language-extras", "legacy-capabilities"]
        }, null, CancellationToken.None);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("/Remove-Capability"));
        Assert.Contains(result.Warnings, warning => warning.Contains("7 capability"));
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.Equal(7, report.RootElement.GetProperty("Capabilities").GetArrayLength());
    }

    [Fact]
    public async Task A_mixed_batch_keeps_the_actual_state_of_each_identity()
    {
        using var fixture = Fixture();
        var listing = string.Concat(Reported.Select((name, index) =>
            $"Capability Identity : {name}\nState : {(index % 2 == 0 ? "Uninstall Pending" : "Not Present")}\n"));
        var runner = new CommandResultRunner(fixture.Runner, args => args.Contains("/Get-Capabilities") ? new ProcessResult(0, listing) : null);
        var result = await new DismClient(fixture.Recipe.DismPath, runner)
            .RemoveCapabilitiesAsync(@"C:\mock\mount", Reported, CancellationToken.None);
        Assert.Equal(4, result.Items.Count(item => item.Deferred));
        Assert.Equal(3, result.Items.Count(item => item.Proof == RemovalProof.Absent));
        Assert.All(result.Items, item => Assert.True(item.Accepted));
    }

    [Fact]
    public async Task A_custom_capability_request_also_reports_its_pending_outcome()
    {
        using var fixture = Fixture();
        var result = await fixture.Engine.BuildAsync(fixture.Recipe with
        {
            SelectedOptions = [], ExpertAcknowledged = true,
            ExpertActions = [new(ImageItemKind.Capability, Reported[0], ExpertActionMode.Remove)]
        }, null, CancellationToken.None);
        Assert.Contains(result.Warnings, warning => warning.StartsWith("1 capability", StringComparison.Ordinal));
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        var capability = Assert.Single(report.RootElement.GetProperty("Capabilities").EnumerateArray());
        Assert.Equal(Reported[0], capability.GetProperty("Name").GetString());
        Assert.True(capability.GetProperty("Deferred").GetBoolean());
    }

    [Fact]
    public async Task Duplicate_capability_states_cannot_hide_a_conflicting_installed_state()
    {
        using var fixture = Fixture();
        var name = Reported[0];
        var runner = new CommandResultRunner(fixture.Runner, args => args.Contains("/Get-Capabilities") ?
            new ProcessResult(0, $"Capability Identity : {name}\nState : Not Present\nCapability Identity : {name}\nState : Installed\n") : null);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DismClient(fixture.Recipe.DismPath, runner).RemoveCapabilitiesAsync(@"C:\mock\mount", [name], CancellationToken.None));
        Assert.Contains("duplicate", error.Message);
    }
}
