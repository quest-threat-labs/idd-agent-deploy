using System.CommandLine;
using HybridAgentDeploy.Cli;
using HybridAgentDeploy.Cli.Commands;
using HybridAgentDeploy.Core.Deployment;

// The CLI exists for repeatability and as an escape hatch where the operator's jump host has
// no desktop (PRD 9, and PRD-OPEN-Q Q5 on Server Core). It calls the same Core library the
// GUI will, and contains no logic of its own.

var output = Output.Console();

var root = new RootCommand(
    "Deploys the Quest Identity Defense Hybrid Audit Agent to Active Directory domain " +
    "controllers, and records what was deployed where.");

// ---------------------------------------------------------------------------------------
// Shared options
// ---------------------------------------------------------------------------------------

var formatOption = new Option<OutputFormat>("--format")
{
    Description = "Output format. JSON is machine-parseable with no interleaved human text; " +
                  "progress and diagnostics always go to stderr.",
    DefaultValueFactory = _ => OutputFormat.Table,
};

var databaseOption = new Option<string?>("--db")
{
    Description = "Inventory database to use, overriding the configured location.",
};

var userOption = new Option<string?>("--user")
{
    Description = "Alternate account (DOMAIN\\user). The password is prompted for, or read " +
                  "from stdin when redirected. It is never accepted as an argument.",
};

var tagOption = new Option<string[]>("--tag")
{
    Description = "Tag name. May be repeated.",
    AllowMultipleArgumentsPerToken = false,
    DefaultValueFactory = _ => [],
};

var hostOption = new Option<string[]>("--host")
{
    Description = "Domain controller FQDN or NetBIOS name. May be repeated.",
    AllowMultipleArgumentsPerToken = false,
    DefaultValueFactory = _ => [],
};

var allOption = new Option<bool>("--all") { Description = "Select every active domain controller." };

var httpsOption = new Option<bool>("--use-https")
{
    Description = "Use WinRM over HTTPS on 5986. Negotiate over 5985 already encrypts the " +
                  "payload, so this is for policies that require transport-level TLS.",
};

// ---------------------------------------------------------------------------------------
// enumerate
// ---------------------------------------------------------------------------------------

var domainOption = new Option<string?>("--domain")
{
    Description = "Limit enumeration to one domain. Defaults to every domain in the current forest.",
};

var enumerateCommand = new Command("enumerate", "Discover domain controllers from Active Directory.")
{
    domainOption, tagOption, userOption, formatOption, databaseOption,
};

enumerateCommand.SetAction((parseResult, ct) => Run(parseResult, databaseOption, output, (services, o) =>
    InventoryCommands.EnumerateAsync(
        services, o,
        parseResult.GetValue(domainOption),
        parseResult.GetValue(tagOption) ?? [],
        parseResult.GetValue(userOption),
        parseResult.GetValue(formatOption),
        ct), ct));

// ---------------------------------------------------------------------------------------
// import
// ---------------------------------------------------------------------------------------

var fileOption = new Option<string>("--file")
{
    Description = "Text file naming domain controllers, one per line. Blank lines and lines " +
                  "beginning with # are ignored.",
    Required = true,
};

var dryRunOption = new Option<bool>("--dry-run")
{
    Description = "Report what would be tagged without writing anything.",
};

var importCommand = new Command(
    "import",
    "Apply tags to domain controllers named in a text file. Import selects and tags; it does " +
    "not add hosts — run `enumerate` first.")
{
    fileOption, tagOption, dryRunOption, formatOption, databaseOption,
};

importCommand.SetAction((parseResult, ct) => Run(parseResult, databaseOption, output, (services, o) =>
    InventoryCommands.ImportAsync(
        services, o,
        parseResult.GetValue(fileOption)!,
        parseResult.GetValue(tagOption) ?? [],
        parseResult.GetValue(dryRunOption),
        parseResult.GetValue(formatOption),
        ct), ct));

// ---------------------------------------------------------------------------------------
// list
// ---------------------------------------------------------------------------------------

var listTagOption = new Option<string?>("--tag") { Description = "Show only DCs carrying this tag." };
var siteOption = new Option<string?>("--site") { Description = "Show only DCs in this Active Directory site." };

var listCommand = new Command("list", "List the domain controller inventory and its last known deployment state.")
{
    listTagOption, siteOption, formatOption, databaseOption,
};

listCommand.SetAction((parseResult, ct) => Run(parseResult, databaseOption, output, (services, o) =>
    InventoryCommands.ListAsync(
        services, o,
        parseResult.GetValue(listTagOption),
        parseResult.GetValue(siteOption),
        parseResult.GetValue(formatOption),
        ct), ct));

// ---------------------------------------------------------------------------------------
// inspect-msi
// ---------------------------------------------------------------------------------------

var msiOption = new Option<string>("--msi")
{
    Description = "Path to the agent MSI.",
    Required = true,
};

var inspectCommand = new Command("inspect-msi", "Read an MSI's product name, version, and identity.")
{
    msiOption, formatOption,
};

inspectCommand.SetAction(async (parseResult, ct) =>
{
    try
    {
        return await InventoryCommands.InspectMsiAsync(
            output, parseResult.GetValue(msiOption)!, parseResult.GetValue(formatOption), ct);
    }
    catch (CommandFailedException ex)
    {
        output.Error(ex.Message);
        return ex.ExitCode;
    }
});

// ---------------------------------------------------------------------------------------
// deploy
// ---------------------------------------------------------------------------------------

var orgIdOption = new Option<string>("--org-id")
{
    Description = "Identity Defense tenant identifier, passed to the installer as INSTALLATION_NAME.",
    Required = true,
};

var noCloudOption = new Option<bool>("--no-cloud-mode")
{
    Description = "Omit SG=1. Cloud mode is on by default.",
};

var maxParallelOption = new Option<int>("--max-parallel")
{
    Description = $"Concurrent deployments, 1-{DeploymentLimits.MaxConcurrencyCeiling}. " +
                  "Values above the ceiling are clamped.",
    DefaultValueFactory = _ => DeploymentLimits.MaxConcurrencyCeiling,
};

var timeoutOption = new Option<int>("--timeout-minutes")
{
    Description = $"Per-target timeout, {DeploymentLimits.MinTimeoutMinutes}-{DeploymentLimits.MaxTimeoutMinutes} minutes.",
    DefaultValueFactory = _ => DeploymentLimits.DefaultTimeoutMinutes,
};

var logDirOption = new Option<string?>("--log-dir") { Description = "Directory for this run's logs." };

var confirmOption = new Option<bool>("--confirm")
{
    Description = "Required when more than one domain controller is selected.",
};

var deployCommand = new Command("deploy", "Deploy the agent MSI to the selected domain controllers.")
{
    msiOption, orgIdOption, tagOption, hostOption, allOption, noCloudOption,
    maxParallelOption, timeoutOption, logDirOption, confirmOption,
    userOption, httpsOption, formatOption, databaseOption,
};

deployCommand.SetAction((parseResult, ct) => Run(parseResult, databaseOption, output, (services, o) =>
    DeployCommand.RunAsync(services, o, new DeployOptions
    {
        MsiPath = parseResult.GetValue(msiOption)!,
        OrgId = parseResult.GetValue(orgIdOption),
        Tags = parseResult.GetValue(tagOption) ?? [],
        Hosts = parseResult.GetValue(hostOption) ?? [],
        All = parseResult.GetValue(allOption),
        NoCloudMode = parseResult.GetValue(noCloudOption),
        MaxParallel = parseResult.GetValue(maxParallelOption),
        TimeoutMinutes = parseResult.GetValue(timeoutOption),
        LogDirectory = parseResult.GetValue(logDirOption),
        Confirm = parseResult.GetValue(confirmOption),
        UserName = parseResult.GetValue(userOption),
        UseHttps = parseResult.GetValue(httpsOption),
        Format = parseResult.GetValue(formatOption),
    }, ct), ct));

// ---------------------------------------------------------------------------------------
// test-connectivity
// ---------------------------------------------------------------------------------------

var testCommand = new Command(
    "test-connectivity",
    "Run each target's pre-flight check without deploying anything.")
{
    tagOption, hostOption, allOption, userOption, httpsOption, formatOption, databaseOption,
};

testCommand.SetAction((parseResult, ct) => Run(parseResult, databaseOption, output, (services, o) =>
    DeployCommand.TestConnectivityAsync(
        services, o,
        parseResult.GetValue(tagOption) ?? [],
        parseResult.GetValue(hostOption) ?? [],
        parseResult.GetValue(allOption),
        parseResult.GetValue(userOption),
        parseResult.GetValue(httpsOption),
        parseResult.GetValue(formatOption),
        ct), ct));

// ---------------------------------------------------------------------------------------
// history
// ---------------------------------------------------------------------------------------

var runOption = new Option<Guid?>("--run") { Description = "Show per-target results for one run GUID." };
var historyHostOption = new Option<string?>("--host") { Description = "Show every deployment recorded for one DC." };

var historyCommand = new Command("history", "Show deployment history.")
{
    runOption, historyHostOption, formatOption, databaseOption,
};

historyCommand.SetAction((parseResult, ct) => Run(parseResult, databaseOption, output, (services, o) =>
    InventoryCommands.HistoryAsync(
        services, o,
        parseResult.GetValue(runOption),
        parseResult.GetValue(historyHostOption),
        parseResult.GetValue(formatOption),
        ct), ct));

root.Subcommands.Add(enumerateCommand);
root.Subcommands.Add(importCommand);
root.Subcommands.Add(listCommand);
root.Subcommands.Add(inspectCommand);
root.Subcommands.Add(deployCommand);
root.Subcommands.Add(testCommand);
root.Subcommands.Add(historyCommand);

return await root.Parse(args).InvokeAsync();

// ---------------------------------------------------------------------------------------

/// <summary>
/// Opens the inventory, runs a command, and converts any failure into a process exit code.
/// </summary>
/// <remarks>
/// Every command that touches the database goes through here so that exit-code translation
/// exists in exactly one place. PRD 9 makes those codes a contract with the calling script,
/// and a command that returned the wrong one would be worse than one that crashed.
/// </remarks>
static async Task<int> Run(
    System.CommandLine.ParseResult parseResult,
    Option<string?> databaseOption,
    Output output,
    Func<AppServices, Output, Task<int>> action,
    CancellationToken ct)
{
    try
    {
        await using var services = await AppServices.CreateAsync(parseResult.GetValue(databaseOption), ct);
        return await action(services, output);
    }
    catch (CommandFailedException ex)
    {
        output.Error(ex.Message);
        return ex.ExitCode;
    }
    catch (OperationCanceledException)
    {
        output.Error("Cancelled.");
        return ExitCode.Cancelled;
    }
    catch (Exception ex)
    {
        // Unexpected, so it is reported in full rather than summarised: this is the message an
        // operator will paste into a support request.
        output.Error($"Unexpected failure: {ex}");
        return ExitCode.InvalidArgumentsOrPreflight;
    }
}
