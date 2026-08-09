# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License. See LICENSE file in the project root for license information.

<#
.SYNOPSIS
    Builds the current source and upgrades an existing SaaS Accelerator deployment.

.EXAMPLE
    .\Upgrade.ps1 `
        -WebAppNamePrefix "marketplace-contoso" `
        -ResourceGroupForDeployment "marketplace-contoso"
#>
Param(
    [string][Parameter(Mandatory)]$WebAppNamePrefix,
    [string][Parameter()]$ResourceGroupForDeployment,
    [string][Parameter()]$TenantID,
    [string][Parameter()]$AzureSubscriptionID,
    [string][Parameter()]$SQLDatabaseName,
    [string][Parameter()]$SQLServerName,
    [switch][Parameter()]$ForceDatabaseMigration,
    [switch][Parameter()]$Quiet
)

$message = @"
The SaaS Accelerator is offered under the MIT License as open source software and is not supported by Microsoft.

If you need help with the accelerator or would like to report defects or feature requests use the Issues feature on the GitHub repository at https://aka.ms/SaaSAccelerator

Do you agree? (Y/N)
"@

Write-Host $message -ForegroundColor Yellow
$response = Read-Host
if ($response -ne "Y" -and $response -ne "y") {
    Write-Host "You did not agree. Exiting..." -ForegroundColor Red
    exit 0
}

$ErrorActionPreference = "Stop"
$azCliOutput = if ($Quiet) { "none" } else { "json" }
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$workDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "saas-accelerator-upgrade-$PID-$([guid]::NewGuid().ToString('N'))"
$adminPublishPath = Join-Path $workDirectory "AdminSite"
$customerPublishPath = Join-Path $workDirectory "CustomerSite"
$adminZipPath = Join-Path $workDirectory "AdminSite.zip"
$customerZipPath = Join-Path $workDirectory "CustomerSite.zip"
$migrationScriptPath = Join-Path $workDirectory "script.sql"
$sqlTokenFile = $null
$firewallRuleCreated = $false
$sqlPublicAccessChanged = $false
$originalConnectionStringEnvironmentValue = $env:ConnectionStrings__DefaultConnection

function Assert-NativeCommandSucceeded {
    param([Parameter(Mandatory)][string]$Operation)

    if ($LASTEXITCODE -ne 0) {
        Throw "Upgrade failed while $Operation. The command exited with code $LASTEXITCODE."
    }
}

function Get-SqlcmdExecutable {
    $executable = $null
    if ($IsWindows -and $env:ProgramFiles) {
        $goSqlcmdWindowsPath = Join-Path $env:ProgramFiles "SqlCmd\sqlcmd.exe"
        if (Test-Path $goSqlcmdWindowsPath -PathType Leaf) {
            $executable = $goSqlcmdWindowsPath
        }
    }

    if (!$executable) {
        $sqlcmdCommand = Get-Command sqlcmd -ErrorAction SilentlyContinue
        if ($sqlcmdCommand) {
            $executable = $sqlcmdCommand.Source
        }
    }

    if (!$executable) {
        Throw "sqlcmd is required. Install Go sqlcmd, or mssql-tools18 on Linux/macOS, and retry."
    }

    return $executable
}

function Assert-ResourceExists {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Description
    )

    az @Arguments --output none
    if ($LASTEXITCODE -ne 0) {
        Throw "$Description was not found or is not accessible."
    }
}

function Set-SqlPublicNetworkAccess {
    param([Parameter(Mandatory)][ValidateSet("Enabled", "Disabled")][string]$DesiredState)

    for ($updateAttempt = 1; $updateAttempt -le 4; $updateAttempt++) {
        $updateOutput = az resource update `
            --ids $sqlServer.id `
            --api-version 2021-11-01 `
            --set "properties.publicNetworkAccess=$DesiredState" `
            --output none 2>&1
        if ($LASTEXITCODE -ne 0) {
            $updateError = $updateOutput -join " "
            $requestAlreadyInProgress = $updateError -match "UpsertLogicalServerRequestAlreadyInProgress|AnotherOperationInProgress|OperationInProgress"
            if (!$requestAlreadyInProgress -or $updateAttempt -eq 4) {
                Write-Warning "SQL public network access could not be set to '$DesiredState': $updateError"
                return $false
            }

            Write-Warning "Another SQL Server update is still in progress. Retrying in 15 seconds (attempt $($updateAttempt + 1) of 4)."
            Start-Sleep -Seconds 15
            continue
        }

        $stateDeadline = (Get-Date).AddSeconds(30)
        do {
            $currentState = az sql server show `
                --resource-group $ResourceGroupForDeployment `
                --name $SQLServerName `
                --query publicNetworkAccess `
                --output tsv
            if ($LASTEXITCODE -eq 0 -and $currentState -eq $DesiredState) {
                return $true
            }

            Start-Sleep -Seconds 5
        } while ((Get-Date) -lt $stateDeadline)

        if ($updateAttempt -lt 4) {
            Write-Warning "SQL public network access has not reached '$DesiredState'. Reapplying the state (attempt $($updateAttempt + 1) of 4)."
        }
    }

    return $false
}

function Get-RequiredDotnetRuntimes {
    param(
        [Parameter(Mandatory)][string]$PublishPath,
        [Parameter(Mandatory)][string]$ApplicationName
    )

    $runtimeConfigPath = Join-Path $PublishPath "$ApplicationName.runtimeconfig.json"
    if (!(Test-Path $runtimeConfigPath -PathType Leaf)) {
        Throw "The published runtime configuration '$runtimeConfigPath' was not found."
    }

    $runtimeConfig = Get-Content -Path $runtimeConfigPath -Raw | ConvertFrom-Json
    $frameworks = @()
    if ($runtimeConfig.runtimeOptions.framework) {
        $frameworks += $runtimeConfig.runtimeOptions.framework
    }
    if ($runtimeConfig.runtimeOptions.frameworks) {
        $frameworks += @($runtimeConfig.runtimeOptions.frameworks)
    }

    $rollForward = if ($runtimeConfig.runtimeOptions.rollForward) {
        $runtimeConfig.runtimeOptions.rollForward
    }
    else {
        "Minor"
    }
    $requiredRuntimes = @($frameworks |
        Where-Object { $_.name -in @("Microsoft.NETCore.App", "Microsoft.AspNetCore.App") } |
        ForEach-Object {
            [pscustomobject]@{
                Name = $_.name
                Version = $_.version
                RollForward = $rollForward
            }
        })
    if ($requiredRuntimes.Count -eq 0) {
        Throw "The published runtime configuration for '$ApplicationName' does not declare a supported .NET runtime."
    }

    return $requiredRuntimes
}

function Get-WebAppInstalledDotnetRuntimes {
    param([Parameter(Mandatory)][string]$WebAppName)

    $webAppJson = az webapp show `
        --resource-group $ResourceGroupForDeployment `
        --name $WebAppName `
        --output json
    Assert-NativeCommandSucceeded "reading web app '$WebAppName'"
    $webApp = $webAppJson | ConvertFrom-Json
    $runtimeCommand = if ($webApp.reserved) {
        "dotnet --list-runtimes"
    }
    else {
        '"D:\Program Files\dotnet\dotnet.exe" --list-runtimes'
    }
    $commandDirectory = if ($webApp.reserved) { "/home" } else { "D:\home" }

    $scmHost = @($webApp.enabledHostNames | Where-Object { $_ -match "\.scm\." })[0]
    if ([string]::IsNullOrWhiteSpace($scmHost)) {
        Throw "The Kudu hostname for web app '$WebAppName' could not be determined."
    }

    $kuduAccessToken = az account get-access-token `
        --resource-type arm `
        --query accessToken `
        --output tsv
    Assert-NativeCommandSucceeded "acquiring an Entra token for Kudu"
    if ([string]::IsNullOrWhiteSpace($kuduAccessToken)) {
        Throw "Azure CLI returned an empty Entra token for Kudu."
    }

    try {
        $commandResponse = Invoke-RestMethod `
            -Uri "https://$scmHost/api/command" `
            -Method Post `
            -Headers @{ Authorization = "Bearer $kuduAccessToken" } `
            -ContentType "application/json" `
            -Body (@{
                command = $runtimeCommand
                dir = $commandDirectory
            } | ConvertTo-Json)
    }
    catch {
        Throw "Unable to query installed .NET runtimes from Kudu for '$WebAppName'. Verify SCM network access and that your Azure role includes Microsoft.Web/sites/publish/Action. $($_.Exception.Message)"
    }
    finally {
        $kuduAccessToken = $null
    }

    if ($commandResponse.ExitCode -ne 0) {
        Throw "Kudu could not list installed .NET runtimes for '$WebAppName': $($commandResponse.Error)"
    }

    $runtimeMatches = [regex]::Matches(
        $commandResponse.Output,
        "(?m)^(?<Name>Microsoft\.(?:NETCore|AspNetCore)\.App)\s+(?<Version>\d+\.\d+\.\d+)"
    )
    $installedRuntimes = @($runtimeMatches | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Groups["Name"].Value
            Version = [version]$_.Groups["Version"].Value
        }
    })
    if ($installedRuntimes.Count -eq 0) {
        Throw "Kudu returned no supported .NET runtimes for '$WebAppName'. Output: $($commandResponse.Output)"
    }

    return $installedRuntimes
}

function Assert-WebAppSupportsRequiredDotnetRuntimes {
    param(
        [Parameter(Mandatory)][string]$WebAppName,
        [Parameter(Mandatory)][object[]]$RequiredRuntimes
    )

    try {
        $installedRuntimes = @(Get-WebAppInstalledDotnetRuntimes -WebAppName $WebAppName)
    }
    catch {
        Write-Warning "Live runtime validation was unavailable for '$WebAppName': $($_.Exception.Message)"

        $isLinuxWebApp = az webapp show `
            --resource-group $ResourceGroupForDeployment `
            --name $WebAppName `
            --query reserved `
            --output tsv
        Assert-NativeCommandSucceeded "determining the operating system for web app '$WebAppName'"
        if ($isLinuxWebApp -eq "true") {
            Throw "Runtime validation cannot be bypassed for Linux web app '$WebAppName'; its configured Linux runtime stack must match the published application."
        }

        $requiredRuntimeSummary = @($RequiredRuntimes | ForEach-Object {
            "$($_.Name) $($_.Version)"
        }) -join ", "
        Write-Host "Required runtimes: $requiredRuntimeSummary" -ForegroundColor Yellow
        $runtimeConfirmation = Read-Host "Confirm that .NET 10 is available for Windows App Service '$WebAppName' in the Azure portal runtime picker and continue without live validation? (Y/N)"
        if ($runtimeConfirmation -notmatch "^[Yy]$") {
            Throw "Upgrade cancelled because runtime compatibility for '$WebAppName' was not confirmed."
        }

        Write-Warning "Proceeding with user-confirmed runtime compatibility for '$WebAppName'."
        return
    }

    foreach ($requiredRuntime in $RequiredRuntimes) {
        $requiredVersion = [version]$requiredRuntime.version
        $matchingRuntime = @($installedRuntimes | Where-Object {
            if ($_.Name -ne $requiredRuntime.name) {
                return $false
            }

            $installedRuntime = $_
            switch ($requiredRuntime.RollForward) {
                "Disable" {
                    return $installedRuntime.Version -eq $requiredVersion
                }
                "LatestPatch" {
                    return $installedRuntime.Version.Major -eq $requiredVersion.Major -and
                        $installedRuntime.Version.Minor -eq $requiredVersion.Minor -and
                        $installedRuntime.Version -ge $requiredVersion
                }
                { $_ -in @("Minor", "LatestMinor") } {
                    return $installedRuntime.Version.Major -eq $requiredVersion.Major -and
                        $installedRuntime.Version -ge $requiredVersion
                }
                { $_ -in @("Major", "LatestMajor") } {
                    return $installedRuntime.Version -ge $requiredVersion
                }
                default {
                    Throw "Unsupported .NET roll-forward policy '$($requiredRuntime.RollForward)' in the published runtime configuration."
                }
            }
        })
        if ($matchingRuntime.Count -eq 0) {
            $installedVersions = @($installedRuntimes |
                Where-Object { $_.Name -eq $requiredRuntime.name } |
                ForEach-Object { $_.Version.ToString() }) -join ", "
            Throw "Web app '$WebAppName' requires $($requiredRuntime.name) $requiredVersion, but installed versions are: $installedVersions."
        }

        Write-Host "Web app '$WebAppName' supports $($requiredRuntime.name) $requiredVersion (installed: $($matchingRuntime[-1].Version))."
    }
}

function Test-DatabaseMigrationRequired {
    if ($ForceDatabaseMigration) {
        Write-Host "Database migration forced by -ForceDatabaseMigration."
        return $true
    }

    $migrationPath = "src\DataAccess\Migrations"
    $localMigrationChanges = git -C $repositoryRoot status --porcelain -- $migrationPath 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Local migration changes could not be inspected; database migration will run."
        return $true
    }
    if ($localMigrationChanges) {
        Write-Host "Local migration changes were detected; database migration is required."
        return $true
    }

    $currentCodeHash = (git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or $currentCodeHash -notmatch "^[0-9a-fA-F]{40}$") {
        Write-Warning "The current source commit could not be determined; database migration will run."
        return $true
    }

    $deployedCodeHashes = @()
    foreach ($webAppName in @($webAppNameAdmin, $webAppNamePortal)) {
        $deployedCodeHash = az webapp config appsettings list `
            --resource-group $ResourceGroupForDeployment `
            --name $webAppName `
            --query "[?name=='SaaSApiConfiguration_CodeHash'].value | [0]" `
            --output tsv
        if ($LASTEXITCODE -ne 0 -or $deployedCodeHash -notmatch "^[0-9a-fA-F]{40}$") {
            Write-Warning "A valid deployed commit hash was not found for '$webAppName'; database migration will run."
            return $true
        }

        $deployedCodeHashes += $deployedCodeHash.Trim()
    }

    $uniqueDeployedCodeHashes = @($deployedCodeHashes | Sort-Object -Unique)
    if ($uniqueDeployedCodeHashes.Count -ne 1) {
        Write-Warning "The deployed web apps report different commit hashes; database migration will run."
        return $true
    }

    $deployedCodeHash = $uniqueDeployedCodeHashes[0]
    if ($deployedCodeHash -eq $currentCodeHash) {
        Write-Host "The deployed and current commits match; no database migration is required."
        return $false
    }

    git -C $repositoryRoot cat-file -e "$deployedCodeHash`^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Fetching deployed commit metadata for migration comparison..."
        git -C $repositoryRoot fetch --quiet --depth 1 origin $deployedCodeHash 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "The deployed commit could not be retrieved; database migration will run."
            return $true
        }
    }

    $changedMigrationFiles = @(git -C $repositoryRoot diff `
        --name-only `
        "$deployedCodeHash..$currentCodeHash" `
        -- $migrationPath 2>$null)
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Migration files could not be compared; database migration will run."
        return $true
    }
    if ($changedMigrationFiles.Count -eq 0) {
        Write-Host "No migration files changed between deployed commit $deployedCodeHash and current commit $currentCodeHash."
        return $false
    }

    Write-Host "Database migration is required because migration files changed:"
    $changedMigrationFiles | ForEach-Object { Write-Host "   $_" }
    return $true
}

Write-Host "Thank you for agreeing. Proceeding with the upgrade..." -ForegroundColor Green

if ([string]::IsNullOrWhiteSpace($ResourceGroupForDeployment)) {
    $ResourceGroupForDeployment = $WebAppNamePrefix
}
if ([string]::IsNullOrWhiteSpace($SQLServerName)) {
    $SQLServerName = "$WebAppNamePrefix-sql"
}
if ([string]::IsNullOrWhiteSpace($SQLDatabaseName)) {
    $SQLDatabaseName = "${WebAppNamePrefix}AMPSaaSDB"
}

$webAppNameAdmin = "$WebAppNamePrefix-admin"
$webAppNamePortal = "$WebAppNamePrefix-portal"
$serverUri = "$SQLServerName.database.windows.net"
$firewallRuleName = "SAUpgrade-$PID"

if ($WebAppNamePrefix.Length -gt 21 -or $WebAppNamePrefix -notmatch "^[a-zA-Z0-9-]+$") {
    Throw "WebAppNamePrefix must be 21 characters or fewer and contain only alphanumeric characters and hyphens."
}

$currentContextJson = az account show --output json
Assert-NativeCommandSucceeded "reading the current Azure CLI context"
$currentContext = $currentContextJson | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($TenantID)) {
    $TenantID = $currentContext.tenantId
}
if ([string]::IsNullOrWhiteSpace($AzureSubscriptionID)) {
    $AzureSubscriptionID = $currentContext.id
}
if ($currentContext.tenantId -ne $TenantID) {
    Throw "The current Azure CLI tenant is '$($currentContext.tenantId)', but the upgrade targets '$TenantID'. Run 'az login --tenant $TenantID' first."
}

az account set --subscription $AzureSubscriptionID
Assert-NativeCommandSucceeded "selecting subscription '$AzureSubscriptionID'"
$selectedContextJson = az account show --output json
Assert-NativeCommandSucceeded "verifying the selected Azure subscription"
$selectedContext = $selectedContextJson | ConvertFrom-Json
if ($selectedContext.tenantId -ne $TenantID -or $selectedContext.id -ne $AzureSubscriptionID) {
    Throw "Azure CLI did not select the requested tenant and subscription."
}

$dotnetVersion = dotnet --version 2>$null
if ($LASTEXITCODE -ne 0 -or ($dotnetVersion -join "") -notmatch "^10\.") {
    Throw ".NET SDK 10.x is required. Found '$($dotnetVersion -join ' ')'."
}

$resourceGroupExists = az group exists --name $ResourceGroupForDeployment
Assert-NativeCommandSucceeded "checking resource group '$ResourceGroupForDeployment'"
if ($resourceGroupExists -ne "true") {
    Throw "Resource group '$ResourceGroupForDeployment' does not exist."
}

Assert-ResourceExists `
    -Arguments @("webapp", "show", "--resource-group", $ResourceGroupForDeployment, "--name", $webAppNameAdmin) `
    -Description "Admin Portal web app '$webAppNameAdmin'"
Assert-ResourceExists `
    -Arguments @("webapp", "show", "--resource-group", $ResourceGroupForDeployment, "--name", $webAppNamePortal) `
    -Description "Customer Portal web app '$webAppNamePortal'"

$databaseMigrationRequired = Test-DatabaseMigrationRequired
$sqlServer = $null
$sqlPublicAccessWasEnabled = $false
$sqlcmdExecutable = $null
$useGoSqlcmd = $false
if ($databaseMigrationRequired) {
    $dotnetEfVersion = dotnet-ef --version 2>$null
    if ($LASTEXITCODE -ne 0 -or ($dotnetEfVersion -join " ") -notmatch "(^|\s)10\.") {
        Throw "dotnet-ef 10.x is required. Found '$($dotnetEfVersion -join ' ')'."
    }

    if ($IsLinux -and $env:ACC_CLOUD) {
        $env:PATH = "/opt/mssql-tools18/bin:$env:PATH"
    }

    $sqlcmdExecutable = Get-SqlcmdExecutable
    $null = & $sqlcmdExecutable --version 2>$null
    $useGoSqlcmd = $LASTEXITCODE -eq 0
    if (!$useGoSqlcmd) {
        $sqlcmdHelp = & $sqlcmdExecutable -? 2>&1
        if ($LASTEXITCODE -ne 0) {
            Throw "sqlcmd at '$sqlcmdExecutable' could not be executed."
        }
        if ($IsWindows) {
            Throw "ODBC sqlcmd on Windows cannot use the Azure CLI access token. Install Go sqlcmd and retry."
        }

        $sqlcmdVersionMatch = [regex]::Match(($sqlcmdHelp -join "`n"), "Version\s+(\d+\.\d+)")
        if (!$sqlcmdVersionMatch.Success -or [version]$sqlcmdVersionMatch.Groups[1].Value -lt [version]"17.8") {
            Throw "ODBC sqlcmd 17.8 or later is required for Azure access-token authentication."
        }
    }

    Assert-ResourceExists `
        -Arguments @("sql", "server", "show", "--resource-group", $ResourceGroupForDeployment, "--name", $SQLServerName) `
        -Description "SQL Server '$SQLServerName'"
    Assert-ResourceExists `
        -Arguments @("sql", "db", "show", "--resource-group", $ResourceGroupForDeployment, "--server", $SQLServerName, "--name", $SQLDatabaseName) `
        -Description "SQL Database '$SQLDatabaseName'"

    $sqlServerJson = az sql server show `
        --resource-group $ResourceGroupForDeployment `
        --name $SQLServerName `
        --output json
    Assert-NativeCommandSucceeded "reading SQL Server '$SQLServerName'"
    $sqlServer = $sqlServerJson | ConvertFrom-Json
    $originalSqlPublicAccess = $sqlServer.publicNetworkAccess
    $sqlPublicAccessWasEnabled = [string]::IsNullOrWhiteSpace($originalSqlPublicAccess) -or
        $originalSqlPublicAccess -eq "Enabled"
}

$compatibilityScript = @"
IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    IF OBJECT_ID(N'[DatabaseVersionHistory]') IS NULL
        THROW 50000, 'Neither __EFMigrationsHistory nor DatabaseVersionHistory exists. The database cannot be upgraded automatically.', 1;

    DECLARE @LegacyVersion decimal(6,2) =
        (SELECT TOP 1 VersionNumber FROM DatabaseVersionHistory ORDER BY CreateDate DESC);

    IF @LegacyVersion NOT IN (2.10, 5.00, 6.10)
        THROW 50001, 'The legacy database version is not supported by the automatic upgrade.', 1;

    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );

    IF @LegacyVersion = 2.10
        INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
            VALUES (N'20221118045814_Baseline_v2', N'6.0.1');
    ELSE IF @LegacyVersion = 5.00
        INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
            VALUES
                (N'20221118045814_Baseline_v2', N'6.0.1'),
                (N'20221118203340_Baseline_v5', N'6.0.1');
    ELSE IF @LegacyVersion = 6.10
        INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
            VALUES
                (N'20221118045814_Baseline_v2', N'6.0.1'),
                (N'20221118203340_Baseline_v5', N'6.0.1'),
                (N'20221118211554_Baseline_v6', N'6.0.1');
END;
"@

try {
    New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null

    Write-Host "#### STEP 1 Build deployment packages ####"
    dotnet publish (Join-Path $repositoryRoot "src" "AdminSite" "AdminSite.csproj") `
        -v q `
        -c Release `
        -o $adminPublishPath
    Assert-NativeCommandSucceeded "building the Admin Portal"

    dotnet publish (Join-Path $repositoryRoot "src" "MeteredTriggerJob" "MeteredTriggerJob.csproj") `
        -c Release `
        -o (Join-Path $adminPublishPath "app_data" "jobs" "triggered" "MeteredTriggerJob") `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishReadyToRun=false
    Assert-NativeCommandSucceeded "building the Meter Scheduler"

    dotnet publish (Join-Path $repositoryRoot "src" "CustomerSite" "CustomerSite.csproj") `
        -v q `
        -c Release `
        -o $customerPublishPath
    Assert-NativeCommandSucceeded "building the Customer Portal"

    Write-Host "#### STEP 2 Validate App Service runtimes ####"
    $adminRequiredRuntimes = @(Get-RequiredDotnetRuntimes `
        -PublishPath $adminPublishPath `
        -ApplicationName "AdminSite")
    Assert-WebAppSupportsRequiredDotnetRuntimes `
        -WebAppName $webAppNameAdmin `
        -RequiredRuntimes $adminRequiredRuntimes

    $customerRequiredRuntimes = @(Get-RequiredDotnetRuntimes `
        -PublishPath $customerPublishPath `
        -ApplicationName "CustomerSite")
    Assert-WebAppSupportsRequiredDotnetRuntimes `
        -WebAppName $webAppNamePortal `
        -RequiredRuntimes $customerRequiredRuntimes

    Compress-Archive -Path (Join-Path $adminPublishPath "*") -DestinationPath $adminZipPath -Force
    Compress-Archive -Path (Join-Path $customerPublishPath "*") -DestinationPath $customerZipPath -Force

    if ($databaseMigrationRequired) {
        Write-Host "#### STEP 3 Generate and apply database migrations ####"
        $env:ConnectionStrings__DefaultConnection = "Server=tcp:$serverUri;Database=$SQLDatabaseName;Authentication=Active Directory Default;"
        dotnet-ef migrations script `
            --idempotent `
            --context SaaSKitContext `
            --project (Join-Path $repositoryRoot "src" "DataAccess" "DataAccess.csproj") `
            --startup-project (Join-Path $repositoryRoot "src" "AdminSite" "AdminSite.csproj") `
            --output $migrationScriptPath
        Assert-NativeCommandSucceeded "generating the database migration script"

        if (!$sqlPublicAccessWasEnabled) {
            $sqlPublicAccessChanged = $true
            if (!(Set-SqlPublicNetworkAccess -DesiredState "Enabled")) {
                Throw "SQL Server public network access did not become Enabled within two minutes."
            }
        }

        $currentIp = (Invoke-RestMethod -Uri "https://api4.ipify.org").Trim()
        if ($currentIp -notmatch "^\d{1,3}(\.\d{1,3}){3}$") {
            Throw "The current public IPv4 address could not be determined."
        }

        for ($firewallAttempt = 1; $firewallAttempt -le 6; $firewallAttempt++) {
            $firewallOutput = az sql server firewall-rule create `
                --resource-group $ResourceGroupForDeployment `
                --server $SQLServerName `
                --name $firewallRuleName `
                --start-ip-address $currentIp `
                --end-ip-address $currentIp `
                --output $azCliOutput 2>&1
            if ($LASTEXITCODE -eq 0) {
                $firewallRuleCreated = $true
                break
            }

            $firewallError = $firewallOutput -join " "
            if ($firewallAttempt -eq 6 -or $firewallError -notmatch "DenyPublicEndpointEnabled") {
                Throw "Upgrade failed while creating the temporary SQL firewall rule: $firewallError"
            }

            Write-Warning "SQL public network access is still propagating. Retrying the firewall rule in 10 seconds (attempt $($firewallAttempt + 1) of 6)."
            Start-Sleep -Seconds 10
        }

        Write-Host "Waiting for the SQL public endpoint and firewall rule to propagate..."
        Start-Sleep -Seconds 20

        if ($useGoSqlcmd) {
            $sqlcmdAuthenticationArguments = @("--authentication-method", "ActiveDirectoryDefault")
        }
        else {
            $sqlAccessToken = az account get-access-token `
                --resource https://database.windows.net/ `
                --query accessToken `
                --output tsv
            Assert-NativeCommandSucceeded "acquiring an Azure SQL access token"
            if ([string]::IsNullOrWhiteSpace($sqlAccessToken)) {
                Throw "Azure CLI returned an empty Azure SQL access token."
            }

            $sqlTokenFile = [System.IO.Path]::GetTempFileName()
            [System.IO.File]::WriteAllText(
                $sqlTokenFile,
                $sqlAccessToken.Trim(),
                [System.Text.UnicodeEncoding]::new($false, $false)
            )
            $sqlcmdAuthenticationArguments = @("-G", "-P", $sqlTokenFile)
        }

        for ($sqlConnectionAttempt = 1; $sqlConnectionAttempt -le 3; $sqlConnectionAttempt++) {
            & $sqlcmdExecutable `
                -S $serverUri `
                -d $SQLDatabaseName `
                @sqlcmdAuthenticationArguments `
                -b `
                -l 30 `
                -Q $compatibilityScript
            if ($LASTEXITCODE -eq 0) {
                break
            }
            if ($sqlConnectionAttempt -eq 3) {
                Throw "Upgrade failed while preparing legacy migration history after three attempts."
            }

            Write-Warning "SQL is not reachable yet. Retrying in 15 seconds (attempt $($sqlConnectionAttempt + 1) of 3)."
            Start-Sleep -Seconds 15
        }

        & $sqlcmdExecutable `
            -S $serverUri `
            -d $SQLDatabaseName `
            @sqlcmdAuthenticationArguments `
            -b `
            -l 30 `
            -i $migrationScriptPath
        Assert-NativeCommandSucceeded "applying database migrations"
    }
    else {
        Write-Host "#### STEP 3 Database migration skipped; no migration files changed ####" -ForegroundColor Green
    }

    Write-Host "#### STEP 4 Deploy application packages ####"
    az webapp deploy `
        --resource-group $ResourceGroupForDeployment `
        --name $webAppNameAdmin `
        --src-path $adminZipPath `
        --type zip `
        --output $azCliOutput
    Assert-NativeCommandSucceeded "deploying the Admin Portal"

    az webapp deploy `
        --resource-group $ResourceGroupForDeployment `
        --name $webAppNamePortal `
        --src-path $customerZipPath `
        --type zip `
        --output $azCliOutput
    Assert-NativeCommandSucceeded "deploying the Customer Portal"

    $codeHash = git -C $repositoryRoot log --format="%H" -1 2>$null
    if ($LASTEXITCODE -eq 0 -and ![string]::IsNullOrWhiteSpace($codeHash)) {
        foreach ($webAppName in @($webAppNameAdmin, $webAppNamePortal)) {
            az webapp config appsettings set `
                --resource-group $ResourceGroupForDeployment `
                --name $webAppName `
                --settings "SaaSApiConfiguration_CodeHash=$codeHash" `
                --output $azCliOutput
            Assert-NativeCommandSucceeded "recording the deployed commit for web app '$webAppName'"
        }
    }
    else {
        Write-Warning "The deployed commit hash could not be determined; existing CodeHash settings were preserved."
    }
}
finally {
    $env:ConnectionStrings__DefaultConnection = $originalConnectionStringEnvironmentValue

    if ($sqlTokenFile) {
        [System.IO.File]::Delete($sqlTokenFile)
    }

    if ($firewallRuleCreated) {
        az sql server firewall-rule delete `
            --resource-group $ResourceGroupForDeployment `
            --server $SQLServerName `
            --name $firewallRuleName `
            --output none
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "The temporary SQL firewall rule '$firewallRuleName' could not be removed."
        }
    }

    if ($sqlPublicAccessChanged) {
        if (!(Set-SqlPublicNetworkAccess -DesiredState "Disabled")) {
            Write-Warning "SQL Server public network access could not be restored to Disabled within two minutes."
        }
    }

    if (Test-Path $workDirectory) {
        Remove-Item -Path $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
Write-Host "The SaaS Accelerator upgrade completed successfully." -ForegroundColor Green
Write-Host "If upgrading from a version before 7.5.0, review IsMeteredBillingEnabled in Admin Portal > Settings."
