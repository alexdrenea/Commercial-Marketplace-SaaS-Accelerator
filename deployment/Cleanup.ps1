# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License. See LICENSE file in the project root for license information.

#
# PowerShell script to clean up resources created by Deploy.ps1.
#

# .\Cleanup.ps1 `
#  -WebAppNamePrefix "amp_saas_accelerator_<unique>" `
#  -ResourceGroupForDeployment "amp_saas_accelerator_<unique>"

Param(
    [string][Parameter(Mandatory)]$WebAppNamePrefix,
    [string][Parameter()]$ResourceGroupForDeployment,
    [string][Parameter()]$TenantID,
    [string][Parameter()]$AzureSubscriptionID,
    [switch][Parameter()]$ConfirmAll
)

$ErrorActionPreference = "Stop"

function Confirm-Cleanup {
    param([string]$Message)

    if ($ConfirmAll) {
        return $true
    }

    Write-Host $Message -ForegroundColor Yellow
    return (Read-Host "Continue? (Y/N)") -match "^[Yy]$"
}

function Assert-AzureCliSuccess {
    param([string]$Operation)

    if ($LASTEXITCODE -ne 0) {
        Throw "Cleanup failed while $Operation. Azure CLI exited with code $LASTEXITCODE."
    }
}

function Get-AutoCreatedApplications {
    param([string]$DisplayName)

    $applicationsJson = az ad app list --display-name $DisplayName --output json
    Assert-AzureCliSuccess "listing app registrations named '$DisplayName'"

    @($applicationsJson | ConvertFrom-Json) |
        Where-Object { $_.displayName -eq $DisplayName }
}

function Get-AutoCreatedServicePrincipals {
    param([string]$DisplayName)

    $servicePrincipalsJson = az ad sp list --display-name $DisplayName --output json
    Assert-AzureCliSuccess "listing service principals named '$DisplayName'"

    @($servicePrincipalsJson | ConvertFrom-Json) |
        Where-Object { $_.displayName -eq $DisplayName }
}

function Remove-AutoCreatedApplications {
    param([string[]]$DisplayNames)

    foreach ($displayName in $DisplayNames) {
        $objectsDeleted = $false
        $servicePrincipals = @(Get-AutoCreatedServicePrincipals -DisplayName $displayName)
        foreach ($servicePrincipal in $servicePrincipals) {
            Write-Host "   Deleting service principal '$($servicePrincipal.id)' for '$displayName'."
            az ad sp delete --id $servicePrincipal.id
            Assert-AzureCliSuccess "deleting service principal '$($servicePrincipal.id)'"
            $objectsDeleted = $true
        }

        $applications = @(Get-AutoCreatedApplications -DisplayName $displayName)
        if ($applications.Count -eq 0) {
            Write-Host "   App registration '$displayName' does not exist."
        }
        else {
            foreach ($application in $applications) {
                Write-Host "   Deleting app registration '$displayName' ($($application.id))."
                az ad app delete --id $application.id
                Assert-AzureCliSuccess "deleting app registration '$displayName'"
                $objectsDeleted = $true
            }
        }

        if (!$objectsDeleted) {
            continue
        }

        for ($attempt = 1; $attempt -le 6; $attempt++) {
            $remainingApplications = @(Get-AutoCreatedApplications -DisplayName $displayName)
            $remainingServicePrincipals = @(Get-AutoCreatedServicePrincipals -DisplayName $displayName)
            if ($remainingApplications.Count -eq 0 -and $remainingServicePrincipals.Count -eq 0) {
                break
            }

            if ($attempt -eq 6) {
                Throw "Microsoft Entra cleanup verification failed for '$displayName'."
            }

            Start-Sleep -Seconds 5
        }
    }
}

function Get-DeletedKeyVault {
    param([string]$VaultName)

    $deletedVaultJson = az keyvault list-deleted --resource-type vault --output json
    Assert-AzureCliSuccess "checking whether Key Vault '$VaultName' is soft-deleted"

    @($deletedVaultJson | ConvertFrom-Json) |
        Where-Object { $_.name -eq $VaultName } |
        Select-Object -First 1
}

function Test-KeyVaultNameAvailable {
    param([string]$VaultName)

    $uri = "https://management.azure.com/subscriptions/$AzureSubscriptionID/providers/Microsoft.KeyVault/checkNameAvailability?api-version=2019-09-01"
    $body = '{"name":"' + $VaultName + '","type":"Microsoft.KeyVault/vaults"}'
    if ($PsVersionTable.Platform -ne "Unix") {
        $body = $body.Replace('"', '\"')
    }

    $resultJson = az rest `
        --method post `
        --uri $uri `
        --headers "Content-Type=application/json" `
        --body $body
    Assert-AzureCliSuccess "checking availability of Key Vault name '$VaultName'"

    return ($resultJson | ConvertFrom-Json).nameAvailable
}

$currentContextJson = az account show --output json
Assert-AzureCliSuccess "reading the current Azure context"
$currentContext = $currentContextJson | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($TenantID)) {
    $TenantID = $currentContext.tenantId
}

if ([string]::IsNullOrWhiteSpace($AzureSubscriptionID)) {
    $AzureSubscriptionID = $currentContext.id
}

if ($currentContext.tenantId -ne $TenantID) {
    Throw "The current Azure CLI tenant is '$($currentContext.tenantId)', but cleanup targets '$TenantID'. Run 'az login --tenant $TenantID' first."
}

if ($currentContext.id -ne $AzureSubscriptionID) {
    az account set --subscription $AzureSubscriptionID
    Assert-AzureCliSuccess "selecting subscription '$AzureSubscriptionID'"
}

if ([string]::IsNullOrWhiteSpace($ResourceGroupForDeployment)) {
    $ResourceGroupForDeployment = $WebAppNamePrefix
}

# Only the convention-based vault and app registrations created by Deploy.ps1 are removed.
# Customer-provided Key Vaults and app registrations are intentionally excluded.
$KeyVault = $WebAppNamePrefix + "-kv"
$AppRegistrationNames = @(
    "$WebAppNamePrefix-FulfillmentAppReg",
    "$WebAppNamePrefix-AdminPortalAppReg",
    "$WebAppNamePrefix-LandingpageAppReg"
)

Write-Host ""
Write-Host "The following auto-created deployment assets will be permanently removed:" -ForegroundColor Yellow
Write-Host "   Subscription: $AzureSubscriptionID"
Write-Host "   Tenant: $TenantID"
Write-Host "   Resource group: $ResourceGroupForDeployment"
Write-Host "   Key Vault to purge: $KeyVault"
Write-Host "   App registrations and service principals:"
$AppRegistrationNames | ForEach-Object { Write-Host "      $_" }
# Write-Host "   Local generated output: Publish folder and deployment\script.sql"

if (!(Confirm-Cleanup -Message "This operation is destructive and cannot be undone.")) {
    Write-Host "Cleanup cancelled."
    exit 0
}

Write-Host "Cleaning up app registrations and service principals..."
Remove-AutoCreatedApplications -DisplayNames $AppRegistrationNames

$resourceGroupExists = az group exists --name $ResourceGroupForDeployment
Assert-AzureCliSuccess "checking resource group '$ResourceGroupForDeployment'"

$keyVaultWasActive = $false
if ($resourceGroupExists -eq "true") {
    $activeVaultsJson = az keyvault list --resource-group $ResourceGroupForDeployment --output json
    Assert-AzureCliSuccess "checking Key Vault '$KeyVault' in the resource group"
    $keyVaultWasActive = @($activeVaultsJson | ConvertFrom-Json |
        Where-Object { $_.name -eq $KeyVault }).Count -gt 0

    Write-Host "Deleting resource group '$ResourceGroupForDeployment'..."
    az group delete --name $ResourceGroupForDeployment --yes
    Assert-AzureCliSuccess "deleting resource group '$ResourceGroupForDeployment'"

    $resourceGroupStillExists = az group exists --name $ResourceGroupForDeployment
    Assert-AzureCliSuccess "verifying deletion of resource group '$ResourceGroupForDeployment'"
    if ($resourceGroupStillExists -ne "false") {
        Throw "Resource group '$ResourceGroupForDeployment' still exists after the delete operation."
    }
}
else {
    Write-Host "Resource group '$ResourceGroupForDeployment' does not exist."
}

$deletedVault = Get-DeletedKeyVault -VaultName $KeyVault
if (!$deletedVault -and $keyVaultWasActive) {
    Write-Host "Waiting for Key Vault '$KeyVault' to enter the soft-deleted state..."
    for ($attempt = 1; $attempt -le 30 -and !$deletedVault; $attempt++) {
        Start-Sleep -Seconds 10
        $deletedVault = Get-DeletedKeyVault -VaultName $KeyVault
    }
}

if ($deletedVault) {
    Write-Host "Purging Key Vault '$KeyVault'..."
    az keyvault purge --name $KeyVault
    Assert-AzureCliSuccess "purging Key Vault '$KeyVault'. Purge-protected vaults cannot be purged before their retention period expires"

    Write-Host "Waiting for Key Vault name '$KeyVault' to become available..."
    for ($attempt = 1; $attempt -le 30; $attempt++) {
        if (Test-KeyVaultNameAvailable -VaultName $KeyVault) {
            break
        }

        if ($attempt -eq 30) {
            Throw "Key Vault '$KeyVault' was purged, but its name did not become available within five minutes."
        }

        Start-Sleep -Seconds 10
    }
}
elseif (!(Test-KeyVaultNameAvailable -VaultName $KeyVault)) {
    Throw "Key Vault name '$KeyVault' is still unavailable, but no deleted vault is visible in this subscription. It might exist in another subscription or be purge-protected."
}
else {
    Write-Host "Key Vault '$KeyVault' is already absent and its name is available."
}

# Preserve generated deployment artifacts so Deploy.ps1 can reuse them without recompiling.
# $publishPath = Join-Path $PSScriptRoot "..\Publish"
# $migrationScriptPath = Join-Path $PSScriptRoot "script.sql"
#
# if (Test-Path $publishPath) {
#     Remove-Item $publishPath -Recurse -Force
# }
#
# if (Test-Path $migrationScriptPath) {
#     Remove-Item $migrationScriptPath -Force
# }

Write-Host ""
Write-Host "Cleanup complete. The deployment prefix '$WebAppNamePrefix' is ready for reuse." -ForegroundColor Green
