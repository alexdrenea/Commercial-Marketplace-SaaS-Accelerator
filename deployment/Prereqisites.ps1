# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License. See LICENSE file in the project root for license information.

<#
.SYNOPSIS
    Validates the local and Azure prerequisites for Deploy.ps1 without changing Azure state.

.EXAMPLE
    .\Prereqisites.ps1 -WebAppNamePrefix "amp-saas-accelerator-123" -Location "eastus"
#>
Param(
    [string][Parameter(Mandatory)]$WebAppNamePrefix,
    [string][Parameter()]$ResourceGroupForDeployment,
    [string][Parameter()]$TenantID,
    [string][Parameter()]$AzureSubscriptionID,
    [string][Parameter(Mandatory)]$location,
    [string][Parameter()]$SQLServerName,
    [string][Parameter()]$KeyVault,
    [string][Parameter()]$ADApplicationID,
    [string][Parameter()]$ADApplicationIDAdmin,
    [string][Parameter()]$ADMTApplicationIDPortal
)

$failures = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

function Add-Failure {
    param([string]$Message)
    $failures.Add($Message)
}

function Add-Warning {
    param([string]$Message)
    $warnings.Add($Message)
}

function Invoke-Az {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & az @Arguments 2>&1
    [pscustomobject]@{
        Succeeded = $LASTEXITCODE -eq 0
        Output = @($output)
    }
}

function ConvertFrom-AzJson {
    param([object[]]$Output)

    try {
        return ($Output -join "`n") | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        return $null
    }
}

function Invoke-AzRestJson {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][object]$Body
    )

    $bodyFile = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText(
            $bodyFile,
            ($Body | ConvertTo-Json -Compress),
            [System.Text.UTF8Encoding]::new($false)
        )
        return Invoke-Az @(
            "rest", "--method", $Method, "--uri", $Uri,
            "--headers", "Content-Type=application/json",
            "--body", "@$bodyFile", "--output", "json"
        )
    }
    finally {
        Remove-Item -Path $bodyFile -Force -ErrorAction SilentlyContinue
    }
}

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host "== $Title ==" -ForegroundColor Cyan
}

Write-Host "SaaS Accelerator prerequisite validation"
Write-Host "This script only performs read-only checks; Deploy.ps1 registers required resource providers."

Write-Section "Execution location and repository"
$deploymentDirectory = Split-Path -Parent $PSCommandPath
$expectedDeploymentDirectory = Join-Path (Split-Path -Parent $deploymentDirectory) "deployment"
if ((Get-Location).Path -ne $expectedDeploymentDirectory) {
    Add-Failure "Run this script from '$expectedDeploymentDirectory' so Deploy.ps1 relative paths resolve correctly."
}
else {
    Write-Host "Current directory is the expected deployment directory."
}

$repositoryRoot = Split-Path -Parent $deploymentDirectory
$requiredProjects = @(
    "src\AdminSite\AdminSite.csproj",
    "src\CustomerSite\CustomerSite.csproj",
    "src\MeteredTriggerJob\MeteredTriggerJob.csproj",
    "src\DataAccess\DataAccess.csproj"
)
$requiredAssets = @(
    "src\AdminSite\wwwroot\contoso-sales.png",
    "src\AdminSite\wwwroot\favicon.ico",
    "src\CustomerSite\wwwroot\contoso-sales.png",
    "src\CustomerSite\wwwroot\favicon.ico"
)
foreach ($relativePath in @($requiredProjects + $requiredAssets)) {
    if (!(Test-Path (Join-Path $repositoryRoot $relativePath) -PathType Leaf)) {
        Add-Failure "Required repository file is missing: '$relativePath'."
    }
}

$publishDirectory = Join-Path $repositoryRoot "Publish"
if (Test-Path $publishDirectory -PathType Container) {
    foreach ($zipName in @("AdminSite.zip", "CustomerSite.zip")) {
        if (!(Test-Path (Join-Path $publishDirectory $zipName) -PathType Leaf)) {
            Add-Failure "Publish exists but required deployment artifact 'Publish\$zipName' is missing. Delete Publish to let Deploy.ps1 build it, or create both ZIP files."
        }
    }
}
else {
    Write-Host "Publish does not exist; Deploy.ps1 will build deployment ZIP files from the required source projects."
}

Write-Section "Local tools"
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Add-Failure "PowerShell 7 or later is required. Found $($PSVersionTable.PSVersion)."
}
else {
    Write-Host "PowerShell $($PSVersionTable.PSVersion) is supported."
}
Write-Host "PowerShell platform: $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)."

$azCommand = Get-Command az -ErrorAction SilentlyContinue
$azAvailable = $null -ne $azCommand
if (!$azAvailable) {
    Add-Failure "Azure CLI (az) is not installed or is not on PATH."
}
else {
    $azVersion = Invoke-Az @("version", "--output", "json")
    $azVersionInfo = if ($azVersion.Succeeded) { ConvertFrom-AzJson $azVersion.Output } else { $null }
    if (!$azVersionInfo -or [string]::IsNullOrWhiteSpace($azVersionInfo.'azure-cli')) {
        Add-Failure "Azure CLI could not report its version: $($azVersion.Output -join ' ')"
    }
    else {
        Write-Host "Azure CLI $($azVersionInfo.'azure-cli') found."
    }
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (!$dotnetCommand) {
    Add-Failure ".NET SDK 10.x is required, but dotnet was not found."
}
else {
    $dotnetVersion = & $dotnetCommand.Source --version 2>&1
    if ($LASTEXITCODE -ne 0 -or ($dotnetVersion -join "") -notmatch "^10\.") {
        Add-Failure ".NET SDK 10.x is required. Found '$($dotnetVersion -join ' ')'."
    }
    else {
        Write-Host ".NET SDK $($dotnetVersion -join '') found."
    }
}

$dotnetEfCommand = Get-Command dotnet-ef -ErrorAction SilentlyContinue
if (!$dotnetEfCommand) {
    Add-Failure "dotnet-ef 10.x is required, but dotnet-ef was not found on PATH."
}
else {
    $dotnetEfVersion = & $dotnetEfCommand.Source --version 2>&1
    if ($LASTEXITCODE -ne 0 -or ($dotnetEfVersion -join " ") -notmatch "(^|\s)10\.") {
        Add-Failure "dotnet-ef 10.x is required. Found '$($dotnetEfVersion -join ' ')'."
    }
    else {
        Write-Host "dotnet-ef $($dotnetEfVersion -join ' ') found."
    }
}

if (!(Get-Command git -ErrorAction SilentlyContinue)) {
    Add-Failure "git is required because Deploy.ps1 records the current commit hash."
}
else {
    Write-Host "git found."
}

$isWindowsPlatform = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
$isLinuxPlatform = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)
$isMacOSPlatform = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)
$sqlcmdExecutable = $null
if ($isWindowsPlatform -and $env:ProgramFiles) {
    $goSqlcmdWindowsPath = Join-Path $env:ProgramFiles "SqlCmd\sqlcmd.exe"
    if (Test-Path $goSqlcmdWindowsPath -PathType Leaf) {
        $sqlcmdExecutable = $goSqlcmdWindowsPath
    }
}
if (!$sqlcmdExecutable) {
    $sqlcmdCommand = Get-Command sqlcmd -ErrorAction SilentlyContinue
    if ($sqlcmdCommand) {
        $sqlcmdExecutable = $sqlcmdCommand.Source
    }
}

if (!$sqlcmdExecutable) {
    Add-Failure "sqlcmd is required. Install Go sqlcmd, or mssql-tools18 on Linux/macOS."
}
else {
    $goSqlcmdVersion = & $sqlcmdExecutable --version 2>&1
    $usesGoSqlcmd = $LASTEXITCODE -eq 0
    if ($usesGoSqlcmd) {
        Write-Host "Using Go sqlcmd: $sqlcmdExecutable ($($goSqlcmdVersion -join ' '))."
    }
    else {
        $sqlcmdHelp = & $sqlcmdExecutable -? 2>&1
        if ($LASTEXITCODE -ne 0) {
            Add-Failure "sqlcmd at '$sqlcmdExecutable' could not be executed."
        }
        elseif ($isWindowsPlatform) {
            Add-Failure "ODBC sqlcmd on Windows cannot use the Azure CLI access token. Install Go sqlcmd (prefer '$env:ProgramFiles\SqlCmd\sqlcmd.exe')."
        }
        elseif (!$isLinuxPlatform -and !$isMacOSPlatform) {
            Add-Failure "Unsupported platform for ODBC sqlcmd. Install Go sqlcmd."
        }
        else {
            $odbcVersion = [regex]::Match(($sqlcmdHelp -join "`n"), "Version\s+(\d+\.\d+)")
            if (!$odbcVersion.Success -or [version]$odbcVersion.Groups[1].Value -lt [version]"17.8") {
                Add-Failure "ODBC sqlcmd 17.8 or later is required for Azure access-token authentication. Install mssql-tools18 or Go sqlcmd."
            }
            else {
                Write-Host "Using supported ODBC sqlcmd $($odbcVersion.Groups[1].Value): $sqlcmdExecutable."
            }
        }
    }
}

$effectiveSqlServerName = if ([string]::IsNullOrWhiteSpace($SQLServerName)) { "$WebAppNamePrefix-sql" } else { $SQLServerName }
$effectiveKeyVault = if ([string]::IsNullOrWhiteSpace($KeyVault)) { "$WebAppNamePrefix-kv" } else { $KeyVault }

Write-Section "Deployment inputs"
if ([string]::IsNullOrWhiteSpace($WebAppNamePrefix)) {
    Add-Failure "WebAppNamePrefix cannot be empty."
}
elseif ($WebAppNamePrefix.Length -gt 21) {
    Add-Failure "WebAppNamePrefix must be 21 characters or fewer, matching Deploy.ps1."
}
elseif ($WebAppNamePrefix -notmatch "^[a-zA-Z0-9-]+$") {
    Add-Failure "WebAppNamePrefix only allows alphanumeric characters and hyphens, matching Deploy.ps1."
}
else {
    Write-Host "Web app name prefix: $WebAppNamePrefix"
}

if ($effectiveKeyVault -notmatch "^(?=.{3,24}$)[a-zA-Z][a-zA-Z0-9-]*[a-zA-Z0-9]$") {
    Add-Failure "Key Vault name '$effectiveKeyVault' is invalid. It must be 3-24 characters, start with a letter, contain only alphanumeric characters or hyphens, and end with an alphanumeric character."
}
else {
    Write-Host "Key Vault name: $effectiveKeyVault"
}

if ([string]::IsNullOrWhiteSpace($location)) {
    Add-Failure "Location is required by Deploy.ps1. Supply -location with an Azure location name such as 'eastus'."
}
else {
    Write-Host "Requested location: $location"
}

Write-Section "Azure CLI authentication and selection"
$context = $null
$selectedSubscription = $null
$selectedTenant = $null
if ($azAvailable) {
    $contextResult = Invoke-Az @("account", "show", "--output", "json")
    $context = if ($contextResult.Succeeded) { ConvertFrom-AzJson $contextResult.Output } else { $null }
    if (!$context) {
        Add-Failure "Azure CLI is not authenticated. Run 'az login' and retry."
    }
    else {
        Write-Host "Current Azure CLI context: tenant $($context.tenantId), subscription $($context.id)."
        $accountsResult = Invoke-Az @("account", "list", "--all", "--output", "json")
        $accounts = if ($accountsResult.Succeeded) { @(ConvertFrom-AzJson $accountsResult.Output) } else { @() }
        if (!$accountsResult.Succeeded) {
            Add-Failure "Azure CLI could not list available subscriptions: $($accountsResult.Output -join ' ')"
        }
        else {
            $selectedTenant = if ([string]::IsNullOrWhiteSpace($TenantID)) { $context.tenantId } else { $TenantID }
            $tenantAccounts = @($accounts | Where-Object { $_.tenantId -eq $selectedTenant })
            if ($tenantAccounts.Count -eq 0) {
                Add-Failure "No Azure subscriptions available for tenant '$selectedTenant'. Run 'az login --tenant $selectedTenant' and retry."
            }
            else {
                $selectedSubscription = if ([string]::IsNullOrWhiteSpace($AzureSubscriptionID)) {
                    if ($selectedTenant -eq $context.tenantId) { $context.id } else { $tenantAccounts[0].id }
                }
                else {
                    $AzureSubscriptionID
                }
                $subscriptionMatch = @($tenantAccounts | Where-Object { $_.id -eq $selectedSubscription })
                if ($subscriptionMatch.Count -eq 0) {
                    Add-Failure "Subscription '$selectedSubscription' is not available in tenant '$selectedTenant'."
                }
                elseif ($subscriptionMatch[0].state -ne "Enabled") {
                    Add-Failure "Subscription '$selectedSubscription' is not enabled (state: $($subscriptionMatch[0].state))."
                }
                else {
                    Write-Host "Effective tenant: $selectedTenant; subscription: $selectedSubscription."
                }
            }
        }
    }
}

Write-Section "Azure access and validation"
$armAccessAvailable = $false
if ($azAvailable -and $context) {
    if (!$selectedTenant) {
        Add-Warning "Tenant and subscription dependent Azure checks were skipped because no effective tenant could be resolved."
    }
    else {
    $armToken = Invoke-Az @("account", "get-access-token", "--resource-type", "arm", "--tenant", $selectedTenant, "--output", "none")
    if (!$armToken.Succeeded) {
        Add-Failure "Azure CLI could not acquire an Azure Resource Manager token for tenant '$selectedTenant'."
    }
    else {
        $armAccessAvailable = $true
        Write-Host "Azure Resource Manager token acquisition succeeded."
    }

    $requiresGraph = [string]::IsNullOrWhiteSpace($ADApplicationID) -or
        [string]::IsNullOrWhiteSpace($ADApplicationIDAdmin) -or
        [string]::IsNullOrWhiteSpace($ADMTApplicationIDPortal)
    if ($requiresGraph) {
        $graphToken = Invoke-Az @("account", "get-access-token", "--resource-type", "ms-graph", "--tenant", $selectedTenant, "--output", "none")
        if (!$graphToken.Succeeded) {
            Add-Failure "Microsoft Graph token acquisition failed. Deploy.ps1 must create or update one or more app registrations."
        }
        else {
            Write-Host "Microsoft Graph token acquisition succeeded for required app-registration work."
        }
    }
    else {
        Write-Host "All app registration IDs were supplied; Graph token preflight was not required."
    }

    $signedInUser = Invoke-Az @("ad", "signed-in-user", "show", "--output", "json")
    $signedInUserInfo = if ($signedInUser.Succeeded) { ConvertFrom-AzJson $signedInUser.Output } else { $null }
    if (!$signedInUserInfo -or [string]::IsNullOrWhiteSpace($signedInUserInfo.id)) {
        Add-Failure "The signed-in Entra user could not be resolved with 'az ad signed-in-user show'; Deploy.ps1 requires it as the SQL Entra administrator."
    }
    else {
        Write-Host "Signed-in Entra user: $($signedInUserInfo.userPrincipalName) ($($signedInUserInfo.id))."
        if ($context.tenantId -ne $selectedTenant) {
            Add-Failure "The current Azure CLI tenant '$($context.tenantId)' differs from selected tenant '$selectedTenant'. Deploy.ps1 resolves the SQL Entra administrator from the current tenant; run 'az login --tenant $selectedTenant' before deployment."
        }
    }

    if ($selectedSubscription -and -not [string]::IsNullOrWhiteSpace($location)) {
        $locationsResult = Invoke-Az @("rest", "--method", "get", "--uri", "https://management.azure.com/subscriptions/$selectedSubscription/locations?api-version=2022-12-01", "--output", "json")
        $locationsResponse = if ($locationsResult.Succeeded) { ConvertFrom-AzJson $locationsResult.Output } else { $null }
        $locations = if ($locationsResponse) { @($locationsResponse.value) } else { @() }
        if (!$locationsResult.Succeeded -or !$locationsResponse) {
            Add-Failure "Azure CLI could not list locations for subscription '$selectedSubscription'."
        }
        elseif (@($locations | Where-Object { $_.name -eq $location -or $_.displayName -eq $location }).Count -eq 0) {
            Add-Failure "Location '$location' is not available to subscription '$selectedSubscription'. Use an Azure location name returned by 'az account list-locations'."
        }
        else {
            Write-Host "Location '$location' is available to the selected subscription."
        }
    }

    if ($armAccessAvailable -and $selectedSubscription) {
        if ($effectiveKeyVault -match "^(?=.{3,24}$)[a-zA-Z][a-zA-Z0-9-]*[a-zA-Z0-9]$") {
        $keyVaultCheck = Invoke-AzRestJson `
            -Method "post" `
            -Uri "https://management.azure.com/subscriptions/$selectedSubscription/providers/Microsoft.KeyVault/checkNameAvailability?api-version=2019-09-01" `
            -Body @{ name = $effectiveKeyVault; type = "Microsoft.KeyVault/vaults" }
        $keyVaultCheckInfo = if ($keyVaultCheck.Succeeded) { ConvertFrom-AzJson $keyVaultCheck.Output } else { $null }
        if (!$keyVaultCheckInfo) {
            Add-Warning "Could not determine Key Vault name availability for '$effectiveKeyVault': $($keyVaultCheck.Output -join ' ')"
        }
        elseif (!$keyVaultCheckInfo.nameAvailable) {
            Add-Failure "Key Vault name '$effectiveKeyVault' is unavailable ($($keyVaultCheckInfo.message)). Deploy.ps1 is non-resumable; choose an available name."
        }
        else {
            Write-Host "Key Vault name '$effectiveKeyVault' is available."
        }
        }

        $sqlServerCheck = Invoke-AzRestJson `
            -Method "post" `
            -Uri "https://management.azure.com/subscriptions/$selectedSubscription/providers/Microsoft.Sql/checkNameAvailability?api-version=2023-08-01" `
            -Body @{ name = $effectiveSqlServerName; type = "Microsoft.Sql/servers" }
        $sqlServerCheckInfo = if ($sqlServerCheck.Succeeded) { ConvertFrom-AzJson $sqlServerCheck.Output } else { $null }
        if (!$sqlServerCheckInfo) {
            Add-Warning "Could not determine SQL Server name availability for '$effectiveSqlServerName': $($sqlServerCheck.Output -join ' ')"
        }
        elseif (!$sqlServerCheckInfo.available) {
            Add-Failure "SQL Server name '$effectiveSqlServerName' is unavailable ($($sqlServerCheckInfo.message)). Deploy.ps1 is non-resumable; choose an available name."
        }
        else {
            Write-Host "SQL Server name '$effectiveSqlServerName' is available."
        }
    }

    if ($selectedSubscription -and $signedInUserInfo) {
        $roleAssignments = Invoke-Az @("role", "assignment", "list", "--assignee-object-id", $signedInUserInfo.id, "--scope", "/subscriptions/$selectedSubscription", "--include-inherited", "--output", "json")
        $roleAssignmentInfo = $null
        $roleAssignmentParseSucceeded = $false
        if ($roleAssignments.Succeeded) {
            try {
                $roleAssignmentInfo = @(($roleAssignments.Output -join "`n") | ConvertFrom-Json -ErrorAction Stop)
                $roleAssignmentParseSucceeded = $true
            }
            catch {
                $roleAssignmentParseSucceeded = $false
            }
        }
        if (!$roleAssignmentParseSucceeded) {
            Add-Warning "Could not read role assignments for the signed-in user. Authorization validation is indeterminate: $($roleAssignments.Output -join ' ')"
        }
        else {
            $broadRoles = @($roleAssignmentInfo | Where-Object { $_.roleDefinitionName -in @("Owner", "Contributor") })
            if ($broadRoles.Count -eq 0) {
                Add-Failure "The signed-in user has no Owner or Contributor assignment visible at subscription scope. Deploy.ps1 requires broad resource-management permissions."
            }
            else {
                Write-Host "Subscription role assignment includes: $($broadRoles[0].roleDefinitionName)."
            }
        }
    }

    if ($selectedSubscription -and -not [string]::IsNullOrWhiteSpace($location)) {
        $quotaExtension = Invoke-Az @("extension", "show", "--name", "quota", "--output", "json")
        if (!$quotaExtension.Succeeded) {
            Add-Failure "The Azure CLI quota extension is required for quota preflight. Install it with 'az extension add --name quota' and rerun this script."
        }
        else {
            $quotaScope = "/subscriptions/$selectedSubscription/providers/Microsoft.Network/locations/$location"
            $networkQuota = Invoke-Az @("quota", "list", "--scope", $quotaScope, "--output", "json")
            if (!$networkQuota.Succeeded) {
                $quotaError = $networkQuota.Output -join ' '
                if ($quotaError -match "Unsupported|BadRequest") {
                    Add-Warning "Network quota cannot be determined for '$location' ($quotaError). This is indeterminate, not a quota success."
                }
                else {
                    Add-Warning "Could not query Microsoft.Network quota for '$location': $quotaError"
                }
            }
            else {
                $quotaItems = @()
                $quotaParseSucceeded = $false
                try {
                    $quotaItems = @(($networkQuota.Output -join "`n") | ConvertFrom-Json -ErrorAction Stop)
                    $quotaParseSucceeded = $true
                }
                catch {
                    $quotaParseSucceeded = $false
                }
                if (!$quotaParseSucceeded) {
                    Add-Warning "Microsoft.Network quota returned an unreadable response for '$location'; quota status is indeterminate."
                }
                $exhaustedQuota = @($quotaItems | Where-Object {
                    $_.properties.limit.value -is [ValueType] -and
                    $_.properties.usages.value -is [ValueType] -and
                    [double]$_.properties.usages.value -ge [double]$_.properties.limit.value
                })
                if ($quotaParseSucceeded -and $exhaustedQuota.Count -gt 0) {
                    Add-Failure "Verified Microsoft.Network quota is exhausted in '$location': $($exhaustedQuota[0].name.localizedValue). Request quota before deployment."
                }
                elseif ($quotaParseSucceeded) {
                    Write-Host "Microsoft.Network quota query completed; no verified exhausted network quota was returned."
                }
            }

            # Deploy.ps1 creates one B1 App Service plan. The quota resource name is
            # discovered from Microsoft.Web and is deliberately not inferred from an ARM type.
            $appServiceQuotaScope = "/subscriptions/$selectedSubscription/providers/Microsoft.Web/locations/$location"
            $b1Quota = Invoke-Az @("quota", "show", "--resource-name", "B1", "--scope", $appServiceQuotaScope, "--output", "json")
            $b1Usage = Invoke-Az @("quota", "usage", "show", "--resource-name", "B1", "--scope", $appServiceQuotaScope, "--output", "json")
            $b1QuotaInfo = if ($b1Quota.Succeeded) { ConvertFrom-AzJson $b1Quota.Output } else { $null }
            $b1UsageInfo = if ($b1Usage.Succeeded) { ConvertFrom-AzJson $b1Usage.Output } else { $null }
            if (!$b1QuotaInfo -or !$b1UsageInfo) {
                Add-Warning "Could not determine the Microsoft.Web B1 App Service plan quota for '$location': $($b1Quota.Output -join ' ') $($b1Usage.Output -join ' ')"
            }
            else {
                $b1Limit = $b1QuotaInfo.properties.limit.value
                $b1CurrentUsage = $b1UsageInfo.properties.usages.value
                if ($b1Limit -is [ValueType] -and $b1CurrentUsage -is [ValueType]) {
                    if ([double]$b1CurrentUsage + 1 -gt [double]$b1Limit) {
                        Add-Failure "Microsoft.Web B1 App Service plan quota is insufficient in '$location': $b1CurrentUsage of $b1Limit instances are used, but Deploy.ps1 requires one additional B1 instance."
                    }
                    else {
                        Write-Host "Microsoft.Web B1 quota: $b1CurrentUsage of $b1Limit instances used; one additional instance is available."
                    }
                }
                else {
                    Add-Warning "Microsoft.Web B1 quota returned a nonnumeric limit or usage for '$location'; quota status is indeterminate."
                }
            }
        }

        # Deploy.ps1 creates an Azure SQL Database on the Standard S0 (10 DTU) SKU.
        # Microsoft.Sql does not expose this quota through az quota, so query the SKU
        # catalog instead and treat a provisioning restriction as a failed prerequisite.
        $sqlEditions = Invoke-Az @("sql", "db", "list-editions", "--location", $location, "--output", "json")
        $sqlEditionInfo = if ($sqlEditions.Succeeded) { ConvertFrom-AzJson $sqlEditions.Output } else { $null }
        $standardEdition = @($sqlEditionInfo | Where-Object { $_.name -eq "Standard" })[0]
        $s0Objective = if ($standardEdition) {
            @($standardEdition.supportedServiceLevelObjectives | Where-Object {
                $_.name -eq "S0" -and $_.performanceLevel.value -eq 10
            })[0]
        }
        else {
            $null
        }
        if (!$sqlEditionInfo -or !$s0Objective) {
            Add-Warning "Could not verify Azure SQL Standard S0 (10 DTU) availability in '$location': $($sqlEditions.Output -join ' ')"
        }
        elseif (![string]::IsNullOrWhiteSpace($s0Objective.reason)) {
            Add-Failure "Azure SQL Standard S0 (10 DTU) cannot currently be provisioned in '$location': $($s0Objective.reason)"
        }
        else {
            Write-Host "Azure SQL Standard S0 (10 DTU) is available in '$location'."
        }
    }
    }
}

Write-Section "Preflight limitations"
Write-Host "Role checks are best effort and are not exhaustive. Entra application privileges, Azure Policy enforcement, and service/SKU capacity cannot be fully preflighted."

Write-Section "Validation report"
if ($warnings.Count -gt 0) {
    Write-Host "Warnings ($($warnings.Count)):" -ForegroundColor Yellow
    foreach ($warning in $warnings) {
        Write-Host "  ! $warning" -ForegroundColor Yellow
    }
}

if ($failures.Count -gt 0) {
    Write-Host "Failed prerequisites ($($failures.Count)):" -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host "  X $failure" -ForegroundColor Red
    }
    exit 1
}

Write-Host "All required prerequisites passed." -ForegroundColor Green
